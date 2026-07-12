using Xunit;

using xpTURN.Klotho.Samples.Identity.Sd; // LobbyMatchConfigSource, LobbyWire

namespace xpTURN.Samples.DevLobby.Tests
{
    /// <summary>
    /// A dedi reconnect must re-push the SAME instance id.
    /// <para>
    /// The window that gives this teeth: a client has joined, so the dedi's entry is <c>Materialized</c>, but the
    /// lobby has not yet seen the room's Active report (up to RoomReportIntervalMs = 3s), so its slot is still
    /// <c>Reserved</c> — exactly the set <c>RePushReservations</c> walks. Re-pushing a freshly minted token there
    /// would trip the dedi's match-conflict check against our own reservation.
    /// </para>
    /// </summary>
    public class RePushMaterializedWindowTests
    {
        const int Room = 0, MaxRooms = 2, Stage = 1;
        long _now = 1_000;
        long Expires => _now + 60_000;

        LobbyMatchConfigSource NewSource() => new LobbyMatchConfigSource(MaxRooms, () => _now);

        // Reserve a room and let a client materialize it (TryResolve = the dedi creating the room).
        LobbyMatchConfigSource Materialized(string instanceId)
        {
            var src = NewSource();
            Assert.True(src.ApplyReservePush(Room, instanceId, Stage, null, Expires).ok);
            Assert.True(src.TryResolve(Room, out _));
            return src;
        }

        [Fact]
        public void RePush_SameInstanceId_IsAccepted() // the reconnect path — must not NAK against ourselves
        {
            var src = Materialized("brawl-1#t1");

            var (ok, nak) = src.ApplyReservePush(Room, "brawl-1#t1", Stage, null, Expires);

            Assert.True(ok);
            Assert.Equal(LobbyWire.ReserveNakNone, nak);
        }

        [Fact]
        public void RePush_FreshTokenEachTime_WouldNak() // guards against re-minting inside RePushReservations
        {
            var src = Materialized("brawl-1#t1");

            var (ok, nak) = src.ApplyReservePush(Room, "brawl-1#t2", Stage, null, Expires);

            Assert.False(ok);
            Assert.Equal(LobbyWire.ReserveNakMatchConflict, nak);
        }

        [Fact] // F2: a same-instance re-push (reconnect) must NOT demote Materialized — otherwise a later
               // different-instance push (reclaim/lobby restart) overwrites the LIVE match's result key.
        public void SameInstanceRePush_KeepsMaterialized_SoLaterDifferentInstanceStillNaks()
        {
            var src = Materialized("brawl-1#t1");
            Assert.True(src.ApplyReservePush(Room, "brawl-1#t1", Stage, null, Expires).ok); // reconnect re-push (same)

            var (ok, nak) = src.ApplyReservePush(Room, "brawl-1#t2", Stage, null, Expires); // reclaim → different

            Assert.False(ok); // shield survived the re-push
            Assert.Equal(LobbyWire.ReserveNakMatchConflict, nak);
        }

        [Fact] // pre-instance-id behaviour: the bare rendezvous key collided with itself and slipped through
        public void RePush_BareRendezvousKey_WouldAlsoBeAccepted_ButCannotDistinguishMatches()
        {
            var src = Materialized("brawl-1");

            Assert.True(src.ApplyReservePush(Room, "brawl-1", Stage, null, Expires).ok);
        }

        [Fact]
        public void NotYetMaterialized_DifferentInstanceId_Overwrites() // rollback → re-reserve self-heals
        {
            var src = NewSource();
            Assert.True(src.ApplyReservePush(Room, "brawl-1#t1", Stage, null, Expires).ok); // never resolved

            var (ok, nak) = src.ApplyReservePush(Room, "brawl-1#t2", Stage, null, Expires);

            Assert.True(ok); // NAK requires Materialized — an unmaterialized entry is replaceable
            Assert.Equal(LobbyWire.ReserveNakNone, nak);
            Assert.True(src.TryGetMatchInfo(Room, out string keyed, out _));
            Assert.Equal("brawl-1#t2", keyed);
        }

        [Fact]
        public void ExpiredMaterializedEntry_DifferentInstanceId_Overwrites() // TTL releases the conflict
        {
            var src = Materialized("brawl-1#t1");
            _now += 60_001;

            Assert.True(src.ApplyReservePush(Room, "brawl-1#t2", Stage, null, _now + 60_000).ok);
        }

        [Fact] // the result key the reporter reads back is the instance id, not the rendezvous key
        public void TryGetMatchInfo_ReturnsTheInstanceId()
        {
            var src = Materialized("brawl-1#t1");

            Assert.True(src.TryGetMatchInfo(Room, out string matchId, out int stageId));
            Assert.Equal("brawl-1#t1", matchId);
            Assert.Equal(Stage, stageId);
            Assert.Equal("brawl-1", matchId.Substring(0, matchId.LastIndexOf('#'))); // rendezvous key recoverable
        }
    }
}
