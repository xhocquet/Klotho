using System;

using Xunit;

using xpTURN.Klotho.Network;              // RoomStateReport
using xpTURN.Klotho.Samples.Identity;     // LobbyTicket
using xpTURN.Klotho.Samples.Identity.Sd;  // DevLobbyCore, LobbyRoomRegistry, LobbyWire, SdDevIdentity

namespace xpTURN.Samples.DevLobby.Tests
{
    /// <summary>
    /// The lobby mints a unique match-instance id when it binds a room slot.
    /// <para>
    /// matchId serves two roles that pull apart: a rendezvous key clients SHARE (so it repeats across matches)
    /// and the result idempotency key, which must be unique per match instance. Reusing the rendezvous key as
    /// the result key made a real second match on one lobby get discarded as a duplicate. The instance id is
    /// minted at Empty→Reserved — the one transition that happens exactly once per match instance.
    /// </para>
    /// </summary>
    public class LobbyMatchInstanceIdTests
    {
        const string Server = "srv", Match = "brawl-1";
        const int MaxRooms = 2, Capacity = 2;
        const long Window = 30_000, Validity = 60_000;

        long _now = 1_000_000;
        int _nonceSeq, _tokenSeq;
        readonly BcEd25519Backend _backend = new BcEd25519Backend();
        LobbyRoomRegistry _reg;

        string Nonce() => "n" + (++_nonceSeq);

        // A SEQUENCE, not a constant: a constant factory would let "re-reserve mints a new id" pass vacuously.
        DevLobbyCore NewCore(Func<string> tokenFactory = null)
        {
            _reg = new LobbyRoomRegistry();
            _reg.AddServer(Server, "127.0.0.1", 7777, MaxRooms, Capacity);
            return new DevLobbyCore(SdDevLobby.CreateIssuer(_backend), _backend, SdDevIdentity.PublicKey,
                                    () => _now, Window, _reg,
                                    instanceTokenFactory: tokenFactory ?? (() => "t" + (++_tokenSeq)));
        }

        LobbyRoomRegistry.RoomSlot Slot(int roomId) => _reg.Servers[Server].Rooms[roomId];

        static void Report(DevLobbyCore core, int roomId, byte state, int count)
            => core.HandleRoomReport(Server, new[] { new RoomStateReport { RoomId = roomId, State = state, PlayerCount = count } }, 1);

        // ── minting ─────────────────────────────────────────────────────────────
        [Fact]
        public void FreshReserve_MintsInstanceId_PrefixedWithRendezvousMatchId()
        {
            var core = NewCore();
            var assign = core.TryAssign(Match, Nonce(), _now + Validity);

            Assert.True(assign.Ok);
            Assert.Equal(Match + "#t1", assign.InstanceId);
            Assert.Equal(Match + "#t1", Slot(assign.RoomId).InstanceId);
            Assert.Equal(Match, Slot(assign.RoomId).SessionId); // rendezvous key is untouched
        }

        [Fact]
        public void SecondClientOfSameMatch_ReusesTheSameInstanceId() // step-1 reuse: same match instance
        {
            var core = NewCore();
            var first = core.TryAssign(Match, Nonce(), _now + Validity);
            var second = core.TryAssign(Match, Nonce(), _now + Validity);

            Assert.Equal(first.RoomId, second.RoomId);
            Assert.False(second.FreshReserve);
            Assert.Equal(first.InstanceId, second.InstanceId);
            Assert.Equal(Match + "#t1", second.InstanceId); // no second token was drawn
        }

        [Fact]
        public void DistinctMatches_GetDistinctInstanceIds()
        {
            var core = NewCore();
            Assert.NotEqual(core.TryAssign("A", Nonce(), _now + Validity).InstanceId,
                            core.TryAssign("B", Nonce(), _now + Validity).InstanceId);
        }

        // ── the bug this exists to fix ──────────────────────────────────────────
        [Fact]
        public void SecondMatchAfterReclaim_GetsADifferentInstanceId() // same rendezvous key, new instance
        {
            var core = NewCore();
            var first = core.TryAssign(Match, Nonce(), _now + Validity);
            core.CommitReservation(Server, first.RoomId);
            Report(core, first.RoomId, LobbyWire.RoomStateActive, 1);
            Report(core, first.RoomId, LobbyWire.RoomStateEmpty, 0); // room ended → ReclaimRoom

            var second = core.TryAssign(Match, Nonce(), _now + Validity);

            Assert.Equal(Match, second.SessionIdOfSlot(_reg, Server)); // rendezvous key repeats, as it must
            Assert.NotEqual(first.InstanceId, second.InstanceId);      // ...but the result key does not
        }

        // ── clear paths: the instance id lives and dies with SessionId ──────────
        [Fact]
        public void ReleaseReservation_ClearsInstanceId() // ReserveAck nak / timeout / client disconnect
        {
            var core = NewCore();
            string nonce = Nonce();
            var assign = core.TryAssign(Match, nonce, _now + Validity);
            core.ReleaseReservation(nonce);

            Assert.Null(Slot(assign.RoomId).InstanceId);
            Assert.Null(Slot(assign.RoomId).SessionId);
        }

        [Fact]
        public void ReclaimRoom_ClearsInstanceId()
        {
            var core = NewCore();
            var assign = core.TryAssign(Match, Nonce(), _now + Validity);
            core.CommitReservation(Server, assign.RoomId);
            Report(core, assign.RoomId, LobbyWire.RoomStateActive, 1);
            Report(core, assign.RoomId, LobbyWire.RoomStateEmpty, 0);

            Assert.Null(Slot(assign.RoomId).InstanceId);
        }

        [Fact]
        public void SweepReservations_ClearsInstanceId() // reserved but never redeemed → ticket expired
        {
            var core = NewCore();
            var assign = core.TryAssign(Match, Nonce(), _now + Validity);
            _now += Validity + 1;
            core.Sweep(_now);

            Assert.Null(Slot(assign.RoomId).InstanceId);
            Assert.Equal(LobbyRoomRegistry.RoomState.Empty, Slot(assign.RoomId).State);
        }

        // ── rollback → re-reserve: a NEW id, and the dedi's stale entry self-heals ──
        [Fact]
        public void RollbackThenReReserve_MintsANewInstanceId()
        {
            var core = NewCore();
            string nonce = Nonce();
            var first = core.TryAssign(Match, nonce, _now + Validity);
            core.ReleaseReservation(nonce); // never materialized (no client joined) → dedi entry is overwritable

            var second = core.TryAssign(Match, Nonce(), _now + Validity);

            Assert.True(second.FreshReserve);
            Assert.NotEqual(first.InstanceId, second.InstanceId);
        }

        // ── the default token must stay a full GUID ─────────────────────────────
        [Fact]
        public void DefaultTokenFactory_Mints32HexChars() // truncating to 32 bits reintroduces birthday collisions
        {
            _reg = new LobbyRoomRegistry();
            _reg.AddServer(Server, "127.0.0.1", 7777, MaxRooms, Capacity);
            var core = new DevLobbyCore(SdDevLobby.CreateIssuer(_backend), _backend, SdDevIdentity.PublicKey,
                                        () => _now, Window, _reg); // no factory → default

            string instanceId = core.TryAssign(Match, Nonce(), _now + Validity).InstanceId;
            string token = instanceId.Substring(instanceId.LastIndexOf('#') + 1);

            Assert.Equal(32, token.Length);
            Assert.All(token, c => Assert.True(Uri.IsHexDigit(c), $"non-hex '{c}' in token"));
        }

        // ── parsing contract: last '#' recovers the rendezvous key ──────────────
        [Theory]
        [InlineData("brawl-1", "brawl-1#abc")]
        [InlineData("has#hash", "has#hash#abc")] // user-typed matchId may itself contain '#'
        [InlineData("", "#abc")]
        public void RendezvousKey_IsRecoverable_FromLastHash(string expected, string instanceId)
            => Assert.Equal(expected, instanceId.Substring(0, instanceId.LastIndexOf('#')));
    }

    static class AssignResultSlotExtensions
    {
        // The rendezvous key is not on AssignResult (it never was) — read it back off the bound slot.
        public static string SessionIdOfSlot(this DevLobbyCore.AssignResult a, LobbyRoomRegistry reg, string serverId)
            => reg.Servers[serverId].Rooms[a.RoomId].SessionId;
    }
}
