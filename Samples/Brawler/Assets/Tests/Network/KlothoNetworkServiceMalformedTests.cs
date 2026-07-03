using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Helper.Tests;
using Brawler; // BrawlerPlayerConfig — a registered PlayerConfigBase for the positive-control config

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// Stage 3a (L2 P2P host) — KlothoNetworkService.HandleDataReceived integrity + disconnect policy.
    /// Mirror of ServerNetworkServiceMalformedTests, scoped to host mode (CreateRoom).
    /// Client mode is exercised once to confirm regular-dispatch path remains correct
    /// (P2P client never populates _pendingPeers — see audit Stage 0 #5).
    /// </summary>
    [TestFixture]
    public class KlothoNetworkServiceMalformedTests
    {
        private KlothoNetworkService _svc;
        private TestTransport _transport;
        private LogCapture _logger;
        private CommandFactory _commandFactory;
        private MessageSerializer _serializer;

        // Cached reflection handles
        private MethodInfo _handleDataReceived;
        private FieldInfo _pendingPeersField;

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();

            _transport = new TestTransport();
            _commandFactory = new CommandFactory();
            _logger = new LogCapture();

            _svc = new KlothoNetworkService();
            _svc.Initialize(_transport, _commandFactory, _logger);

            _serializer = new MessageSerializer();

            _handleDataReceived = typeof(KlothoNetworkService).GetMethod(
                "HandleDataReceived", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(_handleDataReceived, "reflection: HandleDataReceived");

            _pendingPeersField = typeof(KlothoNetworkService).GetField(
                "_pendingPeers", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(_pendingPeersField, "reflection: _pendingPeers");
        }

        [TearDown]
        public void TearDown()
        {
            TestTransport.Reset();
        }

        // ── L2h-1 ───────────────────────────────────────────────────────────

        [Test]
        public void Host_Pending_ValidPlayerJoin_NoDisconnect()
        {
            const int peerId = 1;
            BecomeHost();
            AddPending(peerId);

            byte[] data = SerializeMessage(new PlayerJoinMessage { DeviceId = "device-A" });
            InvokeHandleDataReceived(peerId, data, data.Length);

            Assert.IsFalse(GetPendingPeers().Contains(peerId), "pending must be cleared");
            Assert.AreEqual(0, _transport.DisconnectPeerCallCount, "valid PlayerJoin must not disconnect");
        }

        // ── L2h-2 ───────────────────────────────────────────────────────────

        [Test]
        public void Host_Pending_Malformed_Disconnects()
        {
            const int peerId = 2;
            BecomeHost();
            AddPending(peerId);

            byte[] data = MalformedPayloadFactory.EmptyBody(NetworkMessageType.PlayerJoin);
            InvokeHandleDataReceived(peerId, data, data.Length);

            Assert.IsFalse(GetPendingPeers().Contains(peerId), "pending must be cleared on malformed");
            Assert.AreEqual(1, _transport.DisconnectPeerCallCount, "malformed first message must disconnect");
            Assert.IsTrue(_logger.Contains(KLogLevel.Warning, "Malformed/unknown first message"),
                "L2 should log malformed-first-message warning");
        }

        // ── L2h-3 ───────────────────────────────────────────────────────────

        [Test]
        public void Host_Regular_NullMessage_Disconnects()
        {
            const int peerId = 3;
            BecomeHost();
            // peerId NOT in _pendingPeers → regular dispatch

            byte[] data = MalformedPayloadFactory.EmptyBody(NetworkMessageType.ClientInput);
            InvokeHandleDataReceived(peerId, data, data.Length);

            Assert.AreEqual(1, _transport.DisconnectPeerCallCount, "regular malformed must disconnect");
            Assert.IsTrue(_logger.Contains(KLogLevel.Warning, "Malformed payload"),
                "L2 regular dispatch should log malformed-payload warning");
        }

        // ── L2h-4 ───────────────────────────────────────────────────────────

        [Test]
        public void Client_Mode_RegularDispatchOnly_NoPendingPath()
        {
            // P2P client never populates _pendingPeers (audit Stage 0 #5: "host only").
            // The intent of this test: confirm client-mode receive path goes through *regular dispatch*,
            // and that the malformed-message handling doesn't cause client mode to misroute messages into the pending branch.
            // L2 disconnect-on-malformed *does* fire in both modes (intentional — symmetric defense),
            // so we focus on verifying the pending path is untouched in client mode.

            BecomeClient();

            const int peerId = 4;
            Assert.IsFalse(GetPendingPeers().Contains(peerId), "client mode must not pre-populate pending");

            // A valid (registered) message goes through the regular dispatch — no disconnect.
            byte[] data = SerializeMessage(new PlayerReadyMessage());
            InvokeHandleDataReceived(peerId, data, data.Length);

            Assert.AreEqual(0, _transport.DisconnectPeerCallCount,
                "valid message in client mode must not disconnect");
            Assert.AreEqual(0, GetPendingPeers().Count,
                "client mode regular dispatch must not touch _pendingPeers");
        }

        // ── L2h-5: null-config relay guard (CODE_REVIEW #5) ──────────────────

        // A PlayerConfigMessage whose ConfigData does not deserialize (here: empty → Deserialize returns
        // null via its length<1 guard, WITHOUT throwing) must be dropped before the host relay. Otherwise
        // it skips the unmapped-peer / spoof-id drop and the entitlement guard (all inside the non-null
        // path) yet is still forwarded to every peer carrying an attacker-chosen msg.PlayerId.
        [Test]
        public void Host_PlayerConfig_NullDeserialize_NotRelayed()
        {
            BecomeHost();
            SetSinglePeer(peerId: 10, playerId: 1); // one relay target → a relay would Send exactly once

            int before = _transport.SendCallCount;
            InvokeHandlePlayerConfig(
                new PlayerConfigMessage { PlayerId = 1, ConfigData = Array.Empty<byte>() },
                peerId: 10);

            Assert.AreEqual(before, _transport.SendCallCount,
                "null-deserializing config must not be relayed (dropped before the host relay)");
        }

        // Positive control: a valid config (deserializes to a registered BrawlerPlayerConfig, sender bound
        // and unspoofed) IS relayed — proves the null case's zero-relay is the guard firing, not a broken
        // host setup that never relays.
        [Test]
        public void Host_PlayerConfig_ValidConfig_Relayed()
        {
            BecomeHost();
            SetSinglePeer(peerId: 10, playerId: 1);

            byte[] configData = _serializer.Serialize(new BrawlerPlayerConfig { SelectedCharacterClass = 5 });
            int before = _transport.SendCallCount;
            InvokeHandlePlayerConfig(
                new PlayerConfigMessage { PlayerId = 1, ConfigData = configData },
                peerId: 10);

            Assert.AreEqual(before + 1, _transport.SendCallCount,
                "a valid, sender-bound config must relay to the one mapped peer");
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        // Force _peerToPlayer to a known single mapping so the relay count is deterministic.
        private void SetSinglePeer(int peerId, int playerId)
        {
            var field = typeof(KlothoNetworkService).GetField(
                "_peerToPlayer", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "reflection: _peerToPlayer");
            var map = (Dictionary<int, int>)field.GetValue(_svc);
            map.Clear();
            map[peerId] = playerId;
        }

        private void InvokeHandlePlayerConfig(PlayerConfigMessage msg, int peerId)
        {
            var method = typeof(KlothoNetworkService).GetMethod(
                "HandlePlayerConfigMessage", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, "reflection: HandlePlayerConfigMessage");
            try
            {
                method.Invoke(_svc, new object[] { msg, peerId });
            }
            catch (TargetInvocationException)
            {
                // Slim setup (no engine subscribed) — the relay runs at the END of the method, after any
                // downstream apply, so the SendCallCount assertion remains valid even if a later call throws.
            }
        }

        private void BecomeHost()
        {
            _transport.Listen("localhost", 0, 4);
            _svc.CreateRoom("test", 4);
        }

        private void BecomeClient()
        {
            _svc.JoinRoom("test");
        }

        private void AddPending(int peerId)
        {
            GetPendingPeers().Add(peerId);
        }

        private HashSet<int> GetPendingPeers()
        {
            return (HashSet<int>)_pendingPeersField.GetValue(_svc);
        }

        private void InvokeHandleDataReceived(int peerId, byte[] data, int length)
        {
            try
            {
                _handleDataReceived.Invoke(_svc, new object[] { peerId, data, length });
            }
            catch (TargetInvocationException)
            {
                // Downstream handlers (StartHandshake / HandleReconnectRequest / HandleSpectatorJoin /
                // command dispatch / etc.) may throw when the engine is not subscribed in this slim
                // setup. The L2 boundary state (pending Remove + DisconnectPeer count) is mutated
                // before the throw, so post-throw assertions remain valid.
            }
        }

        private byte[] SerializeMessage(NetworkMessageBase msg)
        {
            using (var serialized = _serializer.SerializePooled(msg))
            {
                byte[] buf = new byte[serialized.Length];
                Array.Copy(serialized.Data, 0, buf, 0, serialized.Length);
                return buf;
            }
        }
    }
}
