using System;
using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Input;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// Empty-prediction for confirmed-disconnected peers.
    ///   - A peer in the engine's _disconnectedPlayerIds is predicted as EmptyCommand (not
    ///     repeat-last), so the host's empty proxy-fill matches the prediction and no rollback
    ///     fires. The empty prediction is byte-identical to the host's fill.
    ///   - A connected (lag) peer is still predicted repeat-last (no regression) — the empty
    ///     branch is gated strictly on _disconnectedPlayerIds membership.
    /// PredictInputOrEmpty is the shared helper used by both the main (ExecuteTickWithPrediction)
    /// and resim (Rollback) prediction loops; this fixture exercises it directly so the contract
    /// is pinned independent of tick-driving timing.
    /// </summary>
    [TestFixture]
    internal class DisconnectedPeerPredictionTests
    {
        private static readonly MethodInfo _predictMethod = typeof(KlothoEngine)
            .GetMethod("PredictInputOrEmpty", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _inputBufferField = typeof(KlothoEngine)
            .GetField("_inputBuffer", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo _handleFrameAdvantageMethod = typeof(KlothoEngine)
            .GetMethod("HandleFrameAdvantage", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo _calcLocalAdvMethod = typeof(KlothoEngine)
            .GetMethod("CalculateLocalAdvantage", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _timeSyncEnabledField = typeof(KlothoEngine)
            .GetField("_timeSyncEnabled", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _localAdvantageField = typeof(KlothoNetworkService)
            .GetField("_localAdvantage", BindingFlags.NonPublic | BindingFlags.Instance);

        private IKLogger _logger;
        private KlothoTestHarness _harness;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = KLoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(KLogLevel.Trace);
                logging.AddUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("DisconnectedPeerPredictionTests");
        }

        [SetUp]
        public void SetUp() => _harness = new KlothoTestHarness(_logger);

        [TearDown]
        public void TearDown() => _harness.Reset();

        // host + 1 guest (playerId 1), Playing, advanced a few ticks.
        private TestPeer SetUpHostWithGuest()
        {
            _harness.CreateHost();
            _harness.AddGuest(); // playerId 1
            _harness.StartPlaying();
            _harness.AdvanceAllToTick(10);
            return _harness.Host;
        }

        private static ICommand Predict(KlothoEngine engine, int playerId, int tick, int fromTick)
            => (ICommand)_predictMethod.Invoke(engine, new object[] { playerId, tick, fromTick });

        private static byte[] Serialize(ICommand cmd)
        {
            int size = cmd.GetSerializedSize();
            var buf = new byte[size];
            var writer = new SpanWriter(buf);
            cmd.Serialize(ref writer);
            return new ReadOnlySpan<byte>(buf, 0, writer.Position).ToArray();
        }

        [Test]
        public void DisconnectedPeer_PredictsEmpty()
        {
            var host = SetUpHostWithGuest();
            host.Engine.NotifyPlayerDisconnected(1);

            var predicted = Predict(host.Engine, playerId: 1, tick: host.CurrentTick, fromTick: -1);

            Assert.IsNotNull(predicted);
            Assert.AreEqual(EmptyCommand.TYPE_ID, predicted.CommandTypeId,
                "confirmed-disconnected peer must be predicted as EmptyCommand (not repeat-last)");
            Assert.AreEqual(1, predicted.PlayerId);
            Assert.AreEqual(host.CurrentTick, predicted.Tick);
        }

        [Test]
        public void DisconnectedEmptyPrediction_ByteMatchesHostFill()
        {
            var host = SetUpHostWithGuest();
            host.Engine.NotifyPlayerDisconnected(1);
            int tick = host.CurrentTick;

            var predicted = Predict(host.Engine, playerId: 1, tick: tick, fromTick: -1);

            // Host proxy-fill construction (ForceInsertEmptyCommandsRange): CreateEmptyCommand + PopulateEmpty.
            var factory = new CommandFactory();
            var hostFill = factory.CreateEmptyCommand();
            factory.PopulateEmpty(hostFill, 1, tick);

            Assert.AreEqual(hostFill.CommandTypeId, predicted.CommandTypeId);
            Assert.AreEqual(Serialize(hostFill), Serialize(predicted),
                "empty prediction must be byte-identical to the host's empty fill (CommandDataEquals match → no rollback)");
        }

        [Test]
        public void ConnectedPeer_KeepsRepeatLast_DisconnectFlipsToEmpty()
        {
            var host = SetUpHostWithGuest();
            int seedTick = host.CurrentTick - 2;

            // Seed a continuous-input (Move) command into player 1's recent history so repeat-last
            // has something non-empty to clone. AddCommandOverwrite replaces the harness's empty.
            var moveSeed = new MoveCommand(1, seedTick, default);
            int moveTypeId = moveSeed.CommandTypeId;
            var buffer = _inputBufferField.GetValue(host.Engine);
            var addOverwrite = buffer.GetType().GetMethod("AddCommandOverwrite");
            addOverwrite.Invoke(buffer, new object[] { moveSeed });

            // Connected (not in _disconnectedPlayerIds) → repeat-last → MoveCommand, NOT empty.
            var connected = Predict(host.Engine, playerId: 1, tick: host.CurrentTick, fromTick: -1);
            Assert.AreEqual(moveTypeId, connected.CommandTypeId,
                "connected (lag) peer must keep repeat-last prediction — empty branch is disconnected-only");
            Assert.AreNotEqual(EmptyCommand.TYPE_ID, connected.CommandTypeId);

            // Same state, now confirmed-disconnected → flips to empty.
            host.Engine.NotifyPlayerDisconnected(1);
            var disconnected = Predict(host.Engine, playerId: 1, tick: host.CurrentTick, fromTick: -1);
            Assert.AreEqual(EmptyCommand.TYPE_ID, disconnected.CommandTypeId,
                "after NotifyPlayerDisconnected the same peer is predicted empty");
        }

        // ── Integration (end-to-end via KlothoTestHarness) ──

        // host + g1(player1) + g2(player2), Playing. Returns (g1, g2).
        private (TestPeer g1, TestPeer g2) SetUpHostWithTwoGuests()
        {
            _harness.CreateHost();
            var g1 = _harness.AddGuest(); // player 1 (survivor)
            var g2 = _harness.AddGuest(); // player 2 (disconnects)
            _harness.StartPlaying();
            return (g1, g2);
        }

        // Advance `count` host ticks; `mover` sends continuous-input (Move) commands, everyone
        // else sends Empty. Mirrors the harness's own inject+Tick loop. Disconnected / catching-up
        // peers are skipped.
        private void DriveHostTicks(int count, TestPeer mover)
        {
            int target = _harness.Host.CurrentTick + count;
            int safety = 0;
            while (_harness.Host.CurrentTick < target)
            {
                foreach (var peer in _harness.AllPeers)
                {
                    if (!peer.Transport.IsConnected) continue;
                    if (_harness.IsCatchingUp(peer)) continue;
                    int tick = peer.CurrentTick + peer.Engine.InputDelay;
                    ICommand cmd = peer == mover
                        ? new MoveCommand(peer.LocalPlayerId, tick, default)
                        : (ICommand)new EmptyCommand(peer.LocalPlayerId, tick);
                    peer.NetworkService.SendCommand(cmd);
                }
                _harness.Tick();
                if (++safety > count * 10)
                    Assert.Fail($"DriveHostTicks safety limit: host={_harness.Host.CurrentTick}, target={target}");
            }
        }

        [Test]
        public void Integration_MainPath_DisconnectedPeer_NoRollbackChurn()
        {
            var (g1, g2) = SetUpHostWithTwoGuests();
            DriveHostTicks(12, g2); // g2 builds continuous-input history (repeat-last ≠ empty)

            _harness.DisconnectPeer(g2);
            _harness.PumpMessages(); // g1 receives Disconnected; host begins empty proxy-fill

            int g1Rollbacks = 0;
            g1.Engine.OnRollbackExecuted += (_, __) => g1Rollbacks++;
            DriveHostTicks(20, null); // host fills g2 empty; g1 reconciles its g2 predictions

            Assert.AreEqual(0, g1Rollbacks,
                "guest must not roll back on the disconnected peer's empty fill — empty-prediction matches (was per-tick rollback pre-IMP60-7)");
        }

        [Test]
        public void Integration_ResimPath_DisconnectedPeer_EmptyPredicted()
        {
            // resim invokes PredictInputOrEmpty with fromTick = resimTick (not -1). The
            // disconnected branch must still produce empty so a rollback's resim cannot re-create
            // a repeat-last pending that the host's empty fill would mismatch into a cascade.
            var host = SetUpHostWithGuest();
            host.Engine.NotifyPlayerDisconnected(1);
            int resimTick = host.CurrentTick;

            var predicted = Predict(host.Engine, playerId: 1, tick: resimTick, fromTick: resimTick);

            Assert.AreEqual(EmptyCommand.TYPE_ID, predicted.CommandTypeId,
                "resim-style call (fromTick set) must also empty-predict a disconnected peer (no cascade)");
        }

        [Test]
        public void Integration_Determinism_StateHashConsistentAcrossDisconnect()
        {
            var (g1, g2) = SetUpHostWithTwoGuests();
            DriveHostTicks(12, g2);

            _harness.DisconnectPeer(g2);
            _harness.PumpMessages();
            DriveHostTicks(20, null);

            // Empty-prediction is speculative only — it cannot change executed results. Host and the
            // surviving guest advance the same dt each Tick (equal CurrentTick) and both execute
            // player2 = empty (host fills, guest predicts the same), so their state hashes match.
            // (AssertStateHashConsistent is avoided: it also compares the frozen, disconnected g2.)
            Assert.AreEqual(_harness.Host.Simulation.GetStateHash(), g1.Simulation.GetStateHash(),
                "surviving guest's state matches host — speculative empty-prediction == authoritative empty fill (determinism)");
        }

        [Test]
        public void Integration_Escalation_RecommendedExtraDelayNotStuck()
        {
            var (g1, g2) = SetUpHostWithTwoGuests();
            DriveHostTicks(12, g2);
            int before = g1.Engine.RecommendedExtraDelay;

            _harness.DisconnectPeer(g2);
            _harness.PumpMessages();
            DriveHostTicks(30, null);

            Assert.LessOrEqual(g1.Engine.RecommendedExtraDelay, before,
                "no rollback churn → reactive DynamicInputDelay escalation must not bump RecommendedExtraDelay during the disconnect window (#10)");
        }

        // ── Proactive fill ──

        [Test]
        public void Integration_ProactiveFill_GuestVerifiedTracksHost_NoTrail()
        {
            // Pre-change (host proxy-fill only at _localTick) the host's fill for the disconnected
            // peer trailed the verified frontier by `lead`, so the surviving guest's verified chain
            // sat persistently behind the host. With proactive fill up to the frontier
            // the guest's LastVerifiedTick keeps pace — the gap must stay bounded, not grow.
            var (g1, g2) = SetUpHostWithTwoGuests();
            DriveHostTicks(12, g2);

            _harness.DisconnectPeer(g2);
            _harness.PumpMessages();
            DriveHostTicks(20, null);
            _harness.PumpMessages();

            int hostVerified = _harness.Host.Engine.LastVerifiedTick;
            int guestVerified = g1.Engine.LastVerifiedTick;

            Assert.Greater(guestVerified, 12,
                "surviving guest's verified chain must keep advancing through the disconnect window");
            // Frontier prefill means the disconnected slot is present at/ahead of the verified tick;
            // any residual gap is message-pump jitter (<= a couple ticks), not a structural lead-sized trail.
            Assert.LessOrEqual(hostVerified - guestVerified, 2,
                $"guest verified must track host without a lead-sized trail (host={hostVerified}, guest={guestVerified})");
        }

        // ── Even-count median (advantage) ──

        [Test]
        public void F2_EvenRemoteMedian_AveragesTwoMiddle_NoUpperBias()
        {
            // 3-player match = 2 live remotes = an always-even remote set. The old
            // valid[count/2] picked the UPPER median (the faster remote), shrinking the
            // measured advantage and under-throttling. The fix averages the
            // two middle ticks. Seed two remote ticks and assert the averaged advantage.
            var (g1, g2) = SetUpHostWithTwoGuests();
            _harness.AdvanceAllToTick(20);
            var host = _harness.Host.Engine;
            int cur = host.CurrentTick;

            // g1 4 ticks behind, g2 10 behind -> sorted [cur-10, cur-4], median = cur-7,
            // advantage = cur - (cur-7) = 7. Old upper-median would give cur-4 -> advantage 4.
            _handleFrameAdvantageMethod.Invoke(host, new object[] { g1.LocalPlayerId, cur - 4, 0 });
            _handleFrameAdvantageMethod.Invoke(host, new object[] { g2.LocalPlayerId, cur - 10, 0 });

            float adv = (float)_calcLocalAdvMethod.Invoke(host, null);
            Assert.AreEqual(7f, adv, 0.001f,
                "even-count remote median must average the two middle ticks (D4) — not the upper-value bias (would be 4)");
        }

        [Test]
        public void Integration_ProxyFill_NotVotedByGuest_D3()
        {
            // After a peer disconnects, the only source of that player's
            // commands is the host's proxy fill, which carries IsProxyTiming. A surviving guest
            // must skip the timing vote for those broadcasts (else _remoteTicks[that player] is
            // polluted with the host's tick — the wire-side bias).
            var (g1, g2) = SetUpHostWithTwoGuests();
            int p2 = g2.LocalPlayerId;
            DriveHostTicks(12, g2);

            _harness.DisconnectPeer(g2);
            _harness.PumpMessages();

            int p2Votes = 0;
            g1.NetworkService.OnFrameAdvantageReceived += (pid, senderTick, senderAdvantage) =>
            {
                if (pid == p2) p2Votes++;
            };

            DriveHostTicks(20, null);
            _harness.PumpMessages();

            Assert.AreEqual(0, p2Votes,
                "host proxy-fill broadcasts for the disconnected player must NOT raise a timing vote " +
                "on the surviving guest (IsProxyTiming gate, D3) — pre-fix the host tick voted for player 2");
        }

        [Test]
        public void TimesyncOff_StampsTruthfulAdvantage_D7Hoist()
        {
            // The advantage push is hoisted OUTSIDE the _timeSyncEnabled
            // guard (parallel to SetLocalTick). A throttle-disabled peer must still report
            // a truthful SenderAdvantage; pre-hoist the service's _localAdvantage would freeze at 0
            // and the host would throttle that peer at half strength.
            var (g1, g2) = SetUpHostWithTwoGuests();
            _harness.AdvanceAllToTick(20);
            var host = _harness.Host.Engine;
            int cur = host.CurrentTick;

            // Both remotes 6 ticks behind -> local advantage ~6.
            _handleFrameAdvantageMethod.Invoke(host, new object[] { g1.LocalPlayerId, cur - 6, 0 });
            _handleFrameAdvantageMethod.Invoke(host, new object[] { g2.LocalPlayerId, cur - 6, 0 });

            _timeSyncEnabledField.SetValue(host, false);   // throttle disabled
            host.Update(0.05f);                            // one tick; the hoist runs regardless of the guard

            int pushed = (int)_localAdvantageField.GetValue(_harness.Host.NetworkService);
            Assert.GreaterOrEqual(pushed, 5,
                "with timesync disabled the measured advantage must still be computed and pushed to the " +
                "service (D7 hoist) — pre-hoist this stayed 0");
        }

        [Test]
        public void Integration_ProactiveFill_ChainBreakSuppressed()
        {
            // ChainBreak fires when TryAdvanceVerifiedChain stalls on a missing slot. With the
            // reactive (trailing) fill the disconnected peer's slot was missing at the verified
            // frontier every tick → ChainBreak spam ("host proactive-fill is ~1 tick behind").
            // Proactive frontier fill keeps the slot present, so OnChainAdvanceBreak should be
            // quiet on the surviving guest through the window.
            var (g1, g2) = SetUpHostWithTwoGuests();
            DriveHostTicks(12, g2);

            _harness.DisconnectPeer(g2);
            _harness.PumpMessages();

            int chainBreaks = 0;
            g1.Engine.OnChainAdvanceBreak += () => chainBreaks++;

            DriveHostTicks(20, null);
            _harness.PumpMessages();

            Assert.LessOrEqual(chainBreaks, 2,
                $"surviving guest must not spam ChainBreak during the disconnect window (count={chainBreaks}); " +
                "pre-proactive-fill this fired roughly per tick");
        }
    }
}
