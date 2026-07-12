using Xunit;

using xpTURN.Klotho.Samples.Identity.Sd; // SdRoomReporter, LobbyWire

namespace xpTURN.Samples.DevLobby.Tests
{
    /// <summary>
    /// The dedi's reserve-entry release gate.
    /// <para>
    /// Two pre-existing defects, both on the same gate: (1) the dedi released only on → Empty while the lobby
    /// reclaims on Disposing OR Empty, leaving a window where a freshly pushed reservation gets wiped; (2) a
    /// failed room read was reported as Empty, so a transient null service / throw on a LIVE room fired the gate
    /// and dropped the entry that carries the match's result key — a silent result loss with no journal record.
    /// </para>
    /// </summary>
    public class RoomReporterReleaseGateTests
    {
        const byte Empty = LobbyWire.RoomStateEmpty;
        const byte Active = LobbyWire.RoomStateActive;
        const byte Draining = LobbyWire.RoomStateDraining;
        const byte Disposing = LobbyWire.RoomStateDisposing;

        // ── a failed read is classified apart from Empty ────────────────────────────────────────────────
        [Fact]
        public void Classify_NoRoom_IsEmpty()
            => Assert.Equal(SdRoomReporter.RoomRead.Empty, SdRoomReporter.Classify(roomExists: false, serviceReady: false));

        [Fact]
        public void Classify_RoomWithService_IsLive()
            => Assert.Equal(SdRoomReporter.RoomRead.Live, SdRoomReporter.Classify(roomExists: true, serviceReady: true));

        [Fact]
        public void Classify_RoomWithoutService_IsUnknown() // publish reorder (ARM64) / teardown
            => Assert.Equal(SdRoomReporter.RoomRead.Unknown, SdRoomReporter.Classify(roomExists: true, serviceReady: false));

        [Theory] // a live room read as Unknown must NOT lose its reserve entry (state is meaningless when Unknown)
        [InlineData(Active)]
        [InlineData(Draining)]
        [InlineData(Disposing)]
        [InlineData(Empty)]
        public void Unknown_NeverReleases(byte prev)
            => Assert.False(SdRoomReporter.ShouldRelease(prev, SdRoomReporter.RoomRead.Unknown, Empty));

        // ── release lands on live → terminal, exactly once ──────────────────────────────────────────────
        [Theory]
        [InlineData(Active, Empty)]
        [InlineData(Active, Disposing)]
        [InlineData(Draining, Empty)]
        [InlineData(Draining, Disposing)]
        public void LiveToTerminal_Releases(byte prev, byte state)
            => Assert.True(SdRoomReporter.ShouldRelease(prev, Read(state), state));

        [Theory] // terminal → terminal: the entry is already released; re-releasing would wipe a reservation
        [InlineData(Disposing, Empty)] // pushed in this very window (the RoomNotFound bug)
        [InlineData(Disposing, Disposing)]
        [InlineData(Empty, Empty)]     // reserve-before-join: room not created yet, reservation is valid
        public void TerminalToTerminal_DoesNotRelease(byte prev, byte state)
            => Assert.False(SdRoomReporter.ShouldRelease(prev, Read(state), state));

        [Theory] // live → live, and the room coming up
        [InlineData(Active, Active)]
        [InlineData(Active, Draining)]
        [InlineData(Draining, Draining)]
        [InlineData(Empty, Active)]
        public void StillLive_DoesNotRelease(byte prev, byte state)
            => Assert.False(SdRoomReporter.ShouldRelease(prev, Read(state), state));

        [Fact] // the whole point of the gate: a room's entry is released once across its full lifecycle
        public void FullLifecycle_ReleasesExactlyOnce()
        {
            byte[] observed = { Empty, Active, Active, Draining, Disposing, Empty, Empty };
            int releases = 0;
            byte prev = Empty;
            foreach (byte state in observed)
            {
                var d = SdRoomReporter.DecideSnapshot(Read(state), prev, state);
                if (d.Release) releases++;
                prev = d.NewPrevState; // drive the REAL prev-advance, not a test-local copy
            }
            Assert.Equal(1, releases);
        }

        [Fact] // regression: a transient read failure mid-match must not release, must not advance prev, and must
               // report the HELD state (not a spurious Empty the lobby would reclaim on) — drives DecideSnapshot.
        public void TransientUnknown_PreservesPrevState_AndReleasesOnceOnRealTeardown()
        {
            int releases = 0;
            byte prev = Active;
            foreach (SdRoomReporter.RoomRead read in new[]
                     { SdRoomReporter.RoomRead.Unknown, SdRoomReporter.RoomRead.Unknown, SdRoomReporter.RoomRead.Live })
            {
                // CollectSnapshot passes RoomStateEmpty as the effective state whenever the read is not Live.
                byte state = read == SdRoomReporter.RoomRead.Live ? Disposing : Empty;
                var d = SdRoomReporter.DecideSnapshot(read, prev, state);
                if (d.Release) releases++;
                if (read == SdRoomReporter.RoomRead.Unknown)
                {
                    Assert.Equal(prev, d.ReportedState); // reports the held state, NOT the Empty passed in
                    Assert.False(d.RefreshPlayerCount);  // keeps the last reported PlayerCount
                }
                prev = d.NewPrevState;
            }
            Assert.Equal(1, releases); // released on the real Disposing, not on the two failed reads
            Assert.Equal(Disposing, prev);
        }

        [Theory] // Unknown holds prev: reports prev (not the Empty CollectSnapshot passes), no advance/release/refresh
        [InlineData(Active)]
        [InlineData(Draining)]
        [InlineData(Disposing)]
        public void Unknown_HoldsPrev_ReportsPrev_NoReleaseNoRefresh(byte prev)
        {
            var d = SdRoomReporter.DecideSnapshot(SdRoomReporter.RoomRead.Unknown, prev, Empty);
            Assert.Equal(prev, d.ReportedState);
            Assert.Equal(prev, d.NewPrevState);
            Assert.False(d.Release);
            Assert.False(d.RefreshPlayerCount);
        }

        static SdRoomReporter.RoomRead Read(byte state)
            => state == Empty ? SdRoomReporter.RoomRead.Empty : SdRoomReporter.RoomRead.Live;
    }
}
