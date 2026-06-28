using System;

using Xunit;

using xpTURN.Klotho.Samples.Identity;     // BcEd25519Backend, LobbyTicket
using xpTURN.Klotho.Samples.Identity.Sd;  // DevLobbyCore, LobbyRoomRegistry, LobbyWire, RoomStateReport, SdDevLobby, SdDevIdentity, SdWireCodes

namespace xpTURN.Samples.DevLobby.Tests
{
    /// <summary>
    /// P1 unit tests — dedi→lobby reporting + reconcile: serverRegister upsert/restore, roomReport
    /// occupied/state reconcile + end-of-match reclaim, lazy-Empty keep, disconnect availability (no immediate
    /// reclaim), Sweep backup-timeout + grace reclaim, the wire codec (round-trip / CORE state-byte MAP /
    /// malformed→false), the stale-peer guard, Empty|Active unbound adopt, and the P0-regression guards
    /// (seed Available=true + LastReportMs=0 sentinel). Deterministic via an injected clock.
    /// </summary>
    public sealed class DevLobbyP1ReportTests
    {
        private const string Server = "srv";
        private const int MaxRooms = 2;
        private const int Capacity = 2;
        private const long Window = 30_000;
        private const long Validity = 60_000;
        private const long Backup = 1_000;  // small backup timeout for deterministic tests
        private const long Grace = 2_000;   // small reclaim grace for deterministic tests

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
                                () => _now, Window, reg, Backup, Grace);

        private static int N;
        private static string Nonce() => "n" + (++N);
        private string Ticket(DevLobbyCore core, string matchId, string nonce, string account = "acc")
            => core.Issue(new LobbyTicket(account, "Name", matchId, _now - 1000, _now + Validity, nonce));

        // Report a single room (reported-only: untouched rooms keep state).
        private static void Report(DevLobbyCore core, string server, int roomId, byte state, int count)
            => core.HandleRoomReport(server, new[] { new RoomStateReport { RoomId = roomId, State = state, PlayerCount = count } }, 1);

        // ── P0 regression guards ─────────────────────────────────────────────

        [Fact]
        public void Seed_Server_IsAvailable_WithoutRegister()
        {
            var reg = NewRegistry();
            Assert.True(reg.Servers[Server].Available); // AddServer must default Available=true (else all issue=Full)
            var core = NewCore(reg);
            Assert.True(core.TryAssign("A", Nonce(), _now + Validity).Ok);
        }

        [Fact]
        public void Seed_NeverReported_IsExemptFromBackupTimeout_OnClockJump()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);
            core.TryAssign("A", Nonce(), _now + Validity);

            _now += 5 * 60 * 1000; // 5-minute jump (mirrors the P0 TTL test) — must NOT reclaim the seed server
            core.Sweep(_now);

            Assert.True(reg.Servers[Server].Available); // LastReportMs==0 sentinel exempts the never-reported seed
        }

        // ── serverRegister ───────────────────────────────────────────────────

        [Fact]
        public void Register_NewServer_SeedsAvailable()
        {
            var reg = new LobbyRoomRegistry(); // empty — no seed
            var core = NewCore(reg);

            core.HandleServerRegister("s2", "10.0.0.1", 8888, MaxRooms, Capacity, peerId: 7);

            Assert.True(reg.Servers.ContainsKey("s2"));
            Assert.True(reg.Servers["s2"].Available);
            Assert.Equal("10.0.0.1", reg.Servers["s2"].Host);
            Assert.Equal("s2", reg.ServerByPeer[7]);
        }

        [Fact]
        public void Register_Reconnect_PreservesOccupancy_AndRestoresAvailable()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);
            core.HandleServerRegister(Server, "127.0.0.1", 7777, MaxRooms, Capacity, peerId: 1);

            string nonce = Nonce();
            string ticket = Ticket(core, "A", nonce);
            var a = core.TryAssign("A", nonce, _now + Validity);
            core.Redeem(ticket, "A", Server, a.RoomId); // occupied=1, Active

            core.HandleServerDisconnect(1);    // unavailable, NOT reclaimed
            core.HandleServerRegister(Server, "127.0.0.1", 7777, MaxRooms, Capacity, peerId: 2); // reconnect

            var slot = reg.Servers[Server].Rooms[0];
            Assert.True(reg.Servers[Server].Available);
            Assert.Equal(1, slot.Occupied); // preserved across reconnect
            Assert.Equal("A", slot.SessionId);
        }

        [Fact]
        public void Register_CapacityChange_Reinits_AndEvictsLedgers()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);
            core.HandleServerRegister(Server, "127.0.0.1", 7777, MaxRooms, Capacity, peerId: 1);
            core.TryAssign("A", Nonce(), _now + Validity); // matchLedger[A], room0 Reserved

            core.HandleServerRegister(Server, "127.0.0.1", 7777, maxRooms: 3, maxPlayersPerRoom: Capacity, peerId: 1);

            Assert.Equal(3, reg.Servers[Server].Rooms.Length);
            Assert.False(reg.MatchLedger.ContainsKey("A")); // ledger evicted on reinit
            Assert.Equal(LobbyRoomRegistry.RoomState.Empty, reg.Servers[Server].Rooms[0].State);
        }

        // ── roomReport reconcile ─────────────────────────────────────────────

        [Fact]
        public void Report_Active_ReconcilesOccupiedToReportedCount()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);
            string nonce = Nonce();
            string ticket = Ticket(core, "A", nonce);
            var a = core.TryAssign("A", nonce, _now + Validity);
            core.Redeem(ticket, "A", Server, a.RoomId); // occupied=1

            Report(core, Server, 0, LobbyWire.RoomStateActive, 2); // dedi authority says 2

            Assert.Equal(2, reg.Servers[Server].Rooms[0].Occupied);
        }

        [Fact]
        public void Report_StateByte_IsMapped_NotCast()
        {
            // wire byte 1 = CORE Active; lobby RoomState.Active = 2. A naive cast would yield Reserved(1).
            var reg = NewRegistry();
            var core = NewCore(reg);
            core.TryAssign("A", Nonce(), _now + Validity); // room0 Reserved

            Report(core, Server, 0, LobbyWire.RoomStateActive, 1);

            Assert.Equal(LobbyRoomRegistry.RoomState.Active, reg.Servers[Server].Rooms[0].State);
        }

        [Fact]
        public void Report_Draining_ExcludesRoomFromNewAssignment()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);
            core.TryAssign("A", Nonce(), _now + Validity); // room0
            Report(core, Server, 0, LobbyWire.RoomStateDraining, 1);

            Assert.Equal(LobbyRoomRegistry.RoomState.Draining, reg.Servers[Server].Rooms[0].State);
            var b = core.TryAssign("B", Nonce(), _now + Validity); // must NOT pick draining room0
            Assert.Equal(1, b.RoomId);
        }

        [Fact]
        public void Report_Empty_AfterActive_ReclaimsSlot_AndReassignable()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);
            string nonce = Nonce();
            string ticket = Ticket(core, "A", nonce);
            var a = core.TryAssign("A", nonce, _now + Validity);
            core.Redeem(ticket, "A", Server, a.RoomId); // Active, occupied=1

            Report(core, Server, 0, LobbyWire.RoomStateEmpty, 0); // match ended

            var slot = reg.Servers[Server].Rooms[0];
            Assert.Equal(LobbyRoomRegistry.RoomState.Empty, slot.State);
            Assert.Equal(0, slot.Occupied);
            Assert.False(reg.MatchLedger.ContainsKey("A")); // P0 R2 resolved — slot reclaimed
        }

        [Fact]
        public void Report_LazyReserved_Empty_IsKept()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);
            core.TryAssign("A", Nonce(), _now + Validity); // room0 Reserved, not yet materialized

            Report(core, Server, 0, LobbyWire.RoomStateEmpty, 0); // dedi hasn't created it yet

            Assert.Equal(LobbyRoomRegistry.RoomState.Reserved, reg.Servers[Server].Rooms[0].State); // kept
            Assert.True(reg.MatchLedger.ContainsKey("A"));
        }

        [Fact]
        public void Report_EmptySlot_Active_AdoptsUnbound_NotHandedToNewMatch()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);

            // mis-routed player on a lobby-Empty room (P1 roomId gap): dedi reports room0 Active.
            Report(core, Server, 0, LobbyWire.RoomStateActive, 1);
            var slot0 = reg.Servers[Server].Rooms[0];
            Assert.Equal(LobbyRoomRegistry.RoomState.Active, slot0.State);
            Assert.Null(slot0.SessionId); // unbound

            var b = core.TryAssign("B", Nonce(), _now + Validity); // step2 skips non-Empty room0
            Assert.Equal(1, b.RoomId); // → room1, no collision with the squatted slot
        }

        [Fact]
        public void Report_OutOfRangeRoomId_IsIgnored()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);
            core.HandleRoomReport(Server, new[] { new RoomStateReport { RoomId = 99, State = LobbyWire.RoomStateActive, PlayerCount = 1 } }, 1);
            // no throw, no effect
            Assert.Equal(LobbyRoomRegistry.RoomState.Empty, reg.Servers[Server].Rooms[0].State);
        }

        // ── disconnect + Sweep ───────────────────────────────────────────────

        [Fact]
        public void Disconnect_MarksUnavailable_AssignFull_NoImmediateReclaim()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);
            core.HandleServerRegister(Server, "127.0.0.1", 7777, MaxRooms, Capacity, peerId: 1);
            core.TryAssign("A", Nonce(), _now + Validity); // room0 reserved

            core.HandleServerDisconnect(1);

            Assert.False(reg.Servers[Server].Available);
            Assert.Equal(1, reg.Servers[Server].Rooms[0].Reserved); // NOT reclaimed immediately
            Assert.False(core.TryAssign("B", Nonce(), _now + Validity).Ok); // no available server → Full
        }

        [Fact]
        public void Disconnect_StalePeer_IsIgnored()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);
            core.HandleServerRegister(Server, "127.0.0.1", 7777, MaxRooms, Capacity, peerId: 1);
            core.HandleServerRegister(Server, "127.0.0.1", 7777, MaxRooms, Capacity, peerId: 2); // reconnect, new peer

            core.HandleServerDisconnect(1); // stale/superseded old peer

            Assert.True(reg.Servers[Server].Available); // current peer (2) unaffected
        }

        [Fact]
        public void Sweep_BackupTimeout_MarksHungServerUnavailable()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);
            core.HandleServerRegister(Server, "127.0.0.1", 7777, MaxRooms, Capacity, peerId: 1); // LastReportMs=now

            _now += Backup + 1;
            core.Sweep(_now);

            Assert.False(reg.Servers[Server].Available);
        }

        [Fact]
        public void Sweep_GraceExceeded_ReclaimsRooms()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);
            core.HandleServerRegister(Server, "127.0.0.1", 7777, MaxRooms, Capacity, peerId: 1);
            core.TryAssign("A", Nonce(), _now + Validity); // room0 reserved

            core.HandleServerDisconnect(1);
            _now += Grace + 1;
            core.Sweep(_now);

            var slot = reg.Servers[Server].Rooms[0];
            Assert.Equal(LobbyRoomRegistry.RoomState.Empty, slot.State);
            Assert.Equal(0, slot.Reserved);
            Assert.False(reg.MatchLedger.ContainsKey("A"));
        }

        [Fact]
        public void Reregister_WithinGrace_RestoresWithoutReclaim()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);
            core.HandleServerRegister(Server, "127.0.0.1", 7777, MaxRooms, Capacity, peerId: 1);
            core.TryAssign("A", Nonce(), _now + Validity);

            core.HandleServerDisconnect(1);
            _now += Grace / 2; // within grace
            core.HandleServerRegister(Server, "127.0.0.1", 7777, MaxRooms, Capacity, peerId: 2); // clears UnavailableSinceMs
            core.Sweep(_now); // grace branch sees UnavailableSinceMs==0 → no reclaim (re-register cancelled it)

            Assert.True(reg.Servers[Server].Available);
            Assert.True(reg.MatchLedger.ContainsKey("A")); // reservation preserved (no reclaim)
        }

        // ── wire codec ───────────────────────────────────────────────────────

        [Fact]
        public void Wire_RoomReport_RoundTrips()
        {
            var rooms = new[]
            {
                new RoomStateReport { RoomId = 0, State = LobbyWire.RoomStateActive, PlayerCount = 2 },
                new RoomStateReport { RoomId = 1, State = LobbyWire.RoomStateEmpty, PlayerCount = 0 },
            };
            byte[] buf = LobbyWire.EncodeRoomReport(0, "srv", rooms, 2);

            Assert.True(LobbyWire.TryDecodeRoomReport(buf, buf.Length, MaxRooms, out var m));
            Assert.Equal("srv", m.ServerId);
            Assert.Equal(2, m.RoomCount);
            Assert.Equal(LobbyWire.RoomStateActive, m.Rooms[0].State);
            Assert.Equal(2, m.Rooms[0].PlayerCount);
            Assert.Equal(1, m.Rooms[1].RoomId);
        }

        [Fact]
        public void Wire_RoomReport_Malformed_ReturnsFalse_NoThrow()
        {
            var rooms = new[] { new RoomStateReport { RoomId = 0, State = LobbyWire.RoomStateActive, PlayerCount = 1 } };
            byte[] buf = LobbyWire.EncodeRoomReport(0, "srv", rooms, 1);

            // truncate the body so the declared roomCount can't be read fully → bounds-check rejects.
            Assert.False(LobbyWire.TryDecodeRoomReport(buf, buf.Length - 3, MaxRooms, out _));
            // roomCount over the cap → rejected before the *9 multiply.
            byte[] forged = LobbyWire.EncodeRoomReport(0, "srv", rooms, 1);
            Assert.False(LobbyWire.TryDecodeRoomReport(forged, forged.Length, maxRooms: 0, out _));
        }

        [Fact]
        public void Wire_ServerRegister_RoundTrips()
        {
            byte[] buf = LobbyWire.EncodeServerRegister(0, "srv", "1.2.3.4", 7777, 4, 8);
            Assert.True(LobbyWire.TryDecodeServerRegister(buf, buf.Length, out var m));
            Assert.Equal("srv", m.ServerId);
            Assert.Equal("1.2.3.4", m.Host);
            Assert.Equal(7777, m.Port);
            Assert.Equal(4, m.MaxRooms);
            Assert.Equal(8, m.MaxPlayersPerRoom);
        }

        // ── P2: RedeemRequest wire (roomId trailing append + hardened decode) ─────
        [Theory] // roomId survives the round trip across negative/zero/positive boundaries
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(7)]
        public void Wire_RedeemRequest_RoomId_RoundTrips(int roomId)
        {
            byte[] buf = LobbyWire.EncodeRedeemRequest(42, "ticket-wire", "match-A", "srv", roomId);
            Assert.True(LobbyWire.TryDecodeRedeemRequest(buf, buf.Length, out var m));
            Assert.Equal(42, m.RequestId);
            Assert.Equal("ticket-wire", m.TicketWire);
            Assert.Equal("match-A", m.SessionId);
            Assert.Equal("srv", m.ServerId);
            Assert.Equal(roomId, m.RoomId);
        }

        [Fact] // truncated/old-format buffer → false, never throws on the poll thread (#1 hardening)
        public void Wire_RedeemRequest_Malformed_ReturnsFalse_NoThrow()
        {
            byte[] buf = LobbyWire.EncodeRedeemRequest(1, "t", "m", "srv", 0);
            // drop the trailing roomId int (old-format dedi) → new decoder must return false, not throw.
            Assert.False(LobbyWire.TryDecodeRedeemRequest(buf, buf.Length - 4, out _));
            // gross truncation mid-string → also false.
            Assert.False(LobbyWire.TryDecodeRedeemRequest(buf, 3, out _));
        }

        // ── P3: boundary / regression ────────────────────────────────────────

        [Fact] // hang (no disconnect, no report): backup-timeout marks unavailable, THEN the reconnect grace
               // — measured from the backup-set UnavailableSinceMs — elapses and the rooms are reclaimed.
               // Distinct from the disconnect→grace and backup-alone tests: the full hang chain in one go.
        public void Sweep_Hang_BackupThenGrace_ReclaimsRooms()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);
            core.HandleServerRegister(Server, "127.0.0.1", 7777, MaxRooms, Capacity, peerId: 1); // LastReportMs=now
            core.TryAssign("A", Nonce(), _now + Validity); // room0 reserved

            _now += Backup + 1; // no report since register → hang
            core.Sweep(_now);   // backup branch → unavailable (no reclaim yet)
            Assert.False(reg.Servers[Server].Available);
            Assert.Equal(1, reg.Servers[Server].Rooms[0].Reserved); // grace not elapsed → still held

            _now += Grace + 1;  // grace measured from the backup-set UnavailableSinceMs
            core.Sweep(_now);   // grace branch → reclaim

            var slot = reg.Servers[Server].Rooms[0];
            Assert.Equal(LobbyRoomRegistry.RoomState.Empty, slot.State);
            Assert.Equal(0, slot.Reserved);
            Assert.False(reg.MatchLedger.ContainsKey("A"));
        }

        [Fact] // Draining → Empty: a room reported Draining is excluded from assignment; a subsequent Empty
               // report (it was in use, not lazy-Reserved) reclaims the slot and makes it reassignable.
        public void Report_DrainingThenEmpty_ReclaimsSlot_AndReassignable()
        {
            var reg = NewRegistry();
            var core = NewCore(reg);
            string nonce = Nonce();
            string ticket = Ticket(core, "A", nonce);
            var a = core.TryAssign("A", nonce, _now + Validity);
            core.Redeem(ticket, "A", Server, a.RoomId); // Active, occupied=1

            Report(core, Server, 0, LobbyWire.RoomStateDraining, 1);
            Assert.Equal(LobbyRoomRegistry.RoomState.Draining, reg.Servers[Server].Rooms[0].State);

            Report(core, Server, 0, LobbyWire.RoomStateEmpty, 0); // match ended after drain
            var slot = reg.Servers[Server].Rooms[0];
            Assert.Equal(LobbyRoomRegistry.RoomState.Empty, slot.State);
            Assert.Equal(0, slot.Occupied);
            Assert.False(reg.MatchLedger.ContainsKey("A"));
            Assert.True(core.TryAssign("B", Nonce(), _now + Validity).Ok); // freed → reassignable
        }
    }
}
