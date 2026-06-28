using System;

using Xunit;

using xpTURN.Klotho.Samples.Identity;     // BcEd25519Backend, LobbyTicket
using xpTURN.Klotho.Samples.Identity.Sd;  // DevLobbyCore, LobbyRoomRegistry, SdDevLobby, SdDevIdentity, SdWireCodes, RedeemResult

namespace xpTURN.Samples.DevLobby.Tests
{
    /// <summary>
    /// Unit tests for the lobby's room-assignment + reservation authority (DevLobbyCore + LobbyRoomRegistry):
    /// capacity-aware assignment, multi-room distribution, transient Full, reservation TTL reclaim, Empty
    /// restore, and the redeem reserved→occupied transition. Deterministic via an injected clock.
    /// </summary>
    public sealed class DevLobbyCoreTests
    {
        private const string Server = "srv";
        private const int MaxRooms = 2;
        private const int Capacity = 2;
        private const long Window = 30_000;   // idempotency window
        private const long Validity = 60_000; // ticket validity

        private long _now = 1_000_000;

        private readonly BcEd25519Backend _backend = new BcEd25519Backend();

        private LobbyRoomRegistry NewRegistry()
        {
            var reg = new LobbyRoomRegistry();
            reg.AddServer(Server, "127.0.0.1", 7777, MaxRooms, Capacity);
            return reg;
        }

        private DevLobbyCore NewCore(LobbyRoomRegistry reg)
            => new DevLobbyCore(SdDevLobby.CreateIssuer(_backend), _backend, SdDevIdentity.PublicKey,
                                () => _now, Window, reg);

        private static int N; // unique nonce source (deterministic per call order)
        private static string Nonce() => "n" + (++N);

        private string Ticket(DevLobbyCore core, string matchId, string nonce, string account = "acc")
            => core.Issue(new LobbyTicket(account, "Name", matchId, _now - 1000, _now + Validity, nonce));

        // ── assignment ───────────────────────────────────────────────────────

        [Fact]
        public void Assign_NewMatch_GetsRoomZeroWithEndpoint()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);

            var r = core.TryAssign("A", Nonce(), _now + Validity);

            Assert.True(r.Ok);
            Assert.Equal(0, r.RoomId);
            Assert.Equal("127.0.0.1", r.Host);
            Assert.Equal(7777, r.Port);
            Assert.Equal(Server, r.ServerId);
            Assert.Equal(1, reg.Servers[Server].Rooms[0].Reserved);
            Assert.Equal(LobbyRoomRegistry.RoomState.Reserved, reg.Servers[Server].Rooms[0].State);
        }

        [Fact]
        public void Assign_DistinctMatches_LandInDistinctRooms()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);

            var a = core.TryAssign("A", Nonce(), _now + Validity);
            var b = core.TryAssign("B", Nonce(), _now + Validity);

            Assert.True(a.Ok && b.Ok);
            Assert.Equal(0, a.RoomId);
            Assert.Equal(1, b.RoomId); // different match → different room
        }

        [Fact]
        public void Assign_SameMatch_ReusesRoom_UntilCapacity_ThenFull()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);

            var p1 = core.TryAssign("A", Nonce(), _now + Validity); // reserved=1
            var p2 = core.TryAssign("A", Nonce(), _now + Validity); // reserved=2 (capacity)
            var p3 = core.TryAssign("A", Nonce(), _now + Validity); // over capacity → Full (1:1, no spill)

            Assert.True(p1.Ok && p2.Ok);
            Assert.Equal(0, p1.RoomId);
            Assert.Equal(0, p2.RoomId); // same room reused
            Assert.False(p3.Ok);        // transient Full
        }

        [Fact]
        public void Assign_AllRoomsOccupied_ReturnsFull()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);

            Assert.True(core.TryAssign("A", Nonce(), _now + Validity).Ok); // room0
            Assert.True(core.TryAssign("B", Nonce(), _now + Validity).Ok); // room1
            var c = core.TryAssign("C", Nonce(), _now + Validity);          // no Empty room left

            Assert.False(c.Ok); // Full (MaxRooms exhausted)
        }

        // ── reservation TTL / Empty restore ──────────────────────────────────

        [Fact]
        public void Reservation_ExpiredUnredeemed_IsReclaimed_AndRoomRestoredToEmpty()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);

            long exp = _now + Validity;
            core.TryAssign("A", Nonce(), exp);
            Assert.Equal(1, reg.Servers[Server].Rooms[0].Reserved);

            _now = exp + 1;       // ticket expired, never redeemed
            core.Sweep(_now);

            var slot = reg.Servers[Server].Rooms[0];
            Assert.Equal(0, slot.Reserved);
            Assert.Equal(LobbyRoomRegistry.RoomState.Empty, slot.State); // ghost reservation room restored
            Assert.False(reg.MatchLedger.ContainsKey("A"));             // match binding evicted
        }

        [Fact]
        public void Reservation_RestoredRoom_IsReassignable()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);

            long exp = _now + Validity;
            core.TryAssign("A", Nonce(), exp); // room0
            core.TryAssign("B", Nonce(), exp); // room1
            _now = exp + 1;
            core.Sweep(_now);                   // both reclaimed → both Empty

            var c = core.TryAssign("C", Nonce(), _now + Validity);
            Assert.True(c.Ok); // a room freed up → assignable again
        }

        // ── redeem reserved→occupied ─────────────────────────────────────────

        [Fact]
        public void Redeem_FirstConsume_MovesReservedToOccupied()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);

            string nonce = Nonce();
            string ticket = Ticket(core, "A", nonce);
            var a = core.TryAssign("A", nonce, _now + Validity);

            var result = core.Redeem(ticket, "A", Server, a.RoomId);

            Assert.True(result.Ok);
            var slot = reg.Servers[Server].Rooms[0];
            Assert.Equal(0, slot.Reserved);  // reservation consumed
            Assert.Equal(1, slot.Occupied);  // entered
            Assert.Equal(LobbyRoomRegistry.RoomState.Active, slot.State);
        }

        [Fact]
        public void Redeem_RepeatWithinWindow_DoesNotDoubleCountOccupied()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);

            string nonce = Nonce();
            string ticket = Ticket(core, "A", nonce);
            var a = core.TryAssign("A", nonce, _now + Validity);

            core.Redeem(ticket, "A", Server, a.RoomId);            // first consume → occupied=1
            var again = core.Redeem(ticket, "A", Server, a.RoomId); // idempotent recovery within window

            Assert.True(again.Ok);
            Assert.Equal(1, reg.Servers[Server].Rooms[0].Occupied); // not double-counted
        }

        [Fact]
        public void Redeem_UnassignedMatch_RejectsSessionMismatch()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);

            string nonce = Nonce();
            string ticket = Ticket(core, "ghost", nonce); // never assigned (no TryAssign)

            var result = core.Redeem(ticket, "ghost", Server, 0); // roomId irrelevant — MatchLedger miss rejects first

            Assert.False(result.Ok);
            Assert.Equal(SdWireCodes.IdentitySessionMismatch, result.RejectWireCode);
        }

        [Fact]
        public void Redeem_WrongServer_RejectsSessionMismatch()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);

            string nonce = Nonce();
            string ticket = Ticket(core, "A", nonce);
            core.TryAssign("A", nonce, _now + Validity); // bound to "srv"

            var result = core.Redeem(ticket, "A", "other-server", 0); // roomId irrelevant — serverId mismatch rejects first

            Assert.False(result.Ok);
            Assert.Equal(SdWireCodes.IdentitySessionMismatch, result.RejectWireCode);
        }

        // ── P2: roomId cross-check (fail-closed) ─────────────────────────────────

        [Fact] // routed to a different room than bound → Reject(8), slot NOT consumed (correct redeem still works)
        public void Redeem_WrongRoom_Rejected8_SlotNotConsumed()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);

            string nonce = Nonce();
            string ticket = Ticket(core, "A", nonce);
            var a = core.TryAssign("A", nonce, _now + Validity); // bound to room a.RoomId (0)

            var mismatch = core.Redeem(ticket, "A", Server, a.RoomId + 1); // route to a different room

            Assert.False(mismatch.Ok);
            Assert.Equal(SdWireCodes.IdentitySessionMismatch, mismatch.RejectWireCode);
            var slot = reg.Servers[Server].Rooms[a.RoomId];
            Assert.Equal(1, slot.Reserved); // reservation untouched
            Assert.Equal(0, slot.Occupied); // not entered

            // nonce was NOT consumed by the mismatch → a correct redeem still accepts.
            var ok = core.Redeem(ticket, "A", Server, a.RoomId);
            Assert.True(ok.Ok);
            Assert.Equal(1, reg.Servers[Server].Rooms[a.RoomId].Occupied);
        }

        [Fact] // sentinel -1 against a room-bound match → fail-closed Reject(8) (ledger RoomId is always >= 0)
        public void Redeem_SentinelRoom_FailsClosed_Rejected8()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);

            string nonce = Nonce();
            string ticket = Ticket(core, "A", nonce);
            core.TryAssign("A", nonce, _now + Validity);

            var result = core.Redeem(ticket, "A", Server, -1);

            Assert.False(result.Ok);
            Assert.Equal(SdWireCodes.IdentitySessionMismatch, result.RejectWireCode);
        }

        [Fact] // idempotent recovery does NOT bypass the room check: re-redeem with a different room → Reject(8)
        public void Redeem_RepeatWithWrongRoom_Rejected8()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);

            string nonce = Nonce();
            string ticket = Ticket(core, "A", nonce);
            var a = core.TryAssign("A", nonce, _now + Validity);

            Assert.True(core.Redeem(ticket, "A", Server, a.RoomId).Ok); // first consume (correct room)
            var replayWrongRoom = core.Redeem(ticket, "A", Server, a.RoomId + 1); // recovery attempt, wrong room

            Assert.False(replayWrongRoom.Ok);
            Assert.Equal(SdWireCodes.IdentitySessionMismatch, replayWrongRoom.RejectWireCode);
        }

        [Fact] // room=match 1:1 + fail-closed must NOT false-reject the 2nd+ player of the same match
        public void Redeem_SameMatchSecondPlayer_SameRoom_Accepts()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);

            string n1 = Nonce(), n2 = Nonce();
            string t1 = Ticket(core, "A", n1, account: "p1");
            string t2 = Ticket(core, "A", n2, account: "p2");
            var a1 = core.TryAssign("A", n1, _now + Validity); // room 0, reserved=1
            var a2 = core.TryAssign("A", n2, _now + Validity); // same match → same room reuse, reserved=2
            Assert.Equal(a1.RoomId, a2.RoomId);

            Assert.True(core.Redeem(t1, "A", Server, a1.RoomId).Ok);
            Assert.True(core.Redeem(t2, "A", Server, a2.RoomId).Ok); // 2nd player same room — accepted

            Assert.Equal(2, reg.Servers[Server].Rooms[a1.RoomId].Occupied);
        }

        // ── P3: boundary / regression ────────────────────────────────────────

        [Fact] // D5 core invariant: the reservation TTL is the ticket validity, NOT the idempotency window —
               // a player who connects after the (short) idempotency window but before the ticket expires is
               // NOT robbed of their reserved slot. Guards against TTL = window regression.
        public void Redeem_PastIdempotencyWindow_WithinTicketValidity_StillAccepts()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);

            long issuedAt = _now;
            string nonce = Nonce();
            string ticket = Ticket(core, "A", nonce);
            var a = core.TryAssign("A", nonce, issuedAt + Validity);

            _now = issuedAt + Window + 5_000; // past the 30s idempotency window, before the 60s ticket validity
            var result = core.Redeem(ticket, "A", Server, a.RoomId);

            Assert.True(result.Ok); // reservation survived → first consume accepts
            Assert.Equal(1, reg.Servers[Server].Rooms[0].Occupied);
            Assert.Equal(0, reg.Servers[Server].Rooms[0].Reserved);
        }

        [Fact] // TTL reclaim is strictly AFTER expiry (now > ExpiresAt): at exactly ExpiresAt the reservation
               // is kept; one ms later it is reclaimed.
        public void Reservation_TtlReclaim_IsStrictlyAfterExpiry()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);

            long exp = _now + Validity;
            core.TryAssign("A", Nonce(), exp);

            _now = exp;        // exactly at expiry — strict '>' keeps it
            core.Sweep(_now);
            Assert.Equal(1, reg.Servers[Server].Rooms[0].Reserved);
            Assert.Equal(LobbyRoomRegistry.RoomState.Reserved, reg.Servers[Server].Rooms[0].State);

            _now = exp + 1;    // one ms past — now reclaimed
            core.Sweep(_now);
            Assert.Equal(0, reg.Servers[Server].Rooms[0].Reserved);
            Assert.Equal(LobbyRoomRegistry.RoomState.Empty, reg.Servers[Server].Rooms[0].State);
        }

        [Fact] // redeem expiry check is '<=' (asymmetric to the reservation sweep's strict '>'): a redeem at the
               // exact expiry instant is rejected Expired, even though the reservation sweep would still keep it.
        public void Redeem_AtExactExpiry_RejectsExpired()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);

            long issuedAt = _now;
            string nonce = Nonce();
            string ticket = Ticket(core, "A", nonce);
            var a = core.TryAssign("A", nonce, issuedAt + Validity);

            _now = issuedAt + Validity; // exactly at expiry
            var result = core.Redeem(ticket, "A", Server, a.RoomId);

            Assert.False(result.Ok);
            Assert.Equal(SdWireCodes.IdentityExpired, result.RejectWireCode);
            Assert.Equal(1, reg.Servers[Server].Rooms[0].Reserved); // expiry short-circuits before the slot transition
        }

        [Fact] // idempotency window boundary: re-redeem at exactly the window edge still recovers (cached accept,
               // no double-count); one ms beyond — while the ticket is still valid — is a replay reject(9).
        public void Redeem_ReplayWindowBoundary_RecoversAtEdge_RejectsBeyond()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);

            long issuedAt = _now;
            string nonce = Nonce();
            string ticket = Ticket(core, "A", nonce);
            var a = core.TryAssign("A", nonce, issuedAt + Validity);

            Assert.True(core.Redeem(ticket, "A", Server, a.RoomId).Ok); // first consume at issuedAt → occupied=1

            _now = issuedAt + Window; // exactly at the window edge (now - AtMs == window) → '<=' recovers
            var atEdge = core.Redeem(ticket, "A", Server, a.RoomId);
            Assert.True(atEdge.Ok);
            Assert.Equal(1, reg.Servers[Server].Rooms[0].Occupied); // not double-counted

            _now = issuedAt + Window + 1; // one ms beyond, ticket still valid (< validity) → replay reject(9)
            var beyond = core.Redeem(ticket, "A", Server, a.RoomId);
            Assert.False(beyond.Ok);
            Assert.Equal(SdWireCodes.IdentityRejected, beyond.RejectWireCode);
            Assert.Equal(1, reg.Servers[Server].Rooms[0].Occupied); // replay does not enter
        }
    }
}
