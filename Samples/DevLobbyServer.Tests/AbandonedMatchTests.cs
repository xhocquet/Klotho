using Xunit;

using xpTURN.Klotho.Network; // SessionPhase
using xpTURN.Klotho.Samples.Identity.Sd; // SdRoomReporter, LobbyWire

namespace xpTURN.Samples.DevLobby.Tests
{
    /// <summary>
    /// The abandoned-match discriminator and the abort-notification payload it emits.
    /// The discriminator is a pure internal static so it is testable without constructing the reporter (whose
    /// ctor opens a real report client).
    /// </summary>
    public class AbandonedMatchTests
    {
        // ── IsAbandoned(endRequested, drainPhase) ───────────────────────────────────────────────────────
        [Fact] // THE abandon case: match started (Playing) and no end was ever requested
        public void IsAbandoned_PlayingAndNoEndRequested_True()
            => Assert.True(SdRoomReporter.IsAbandoned(endRequested: false, SessionPhase.Playing));

        [Theory] // endRequested == true → normal-end / abort / Provider-less end already handled — never abandon
        [InlineData(SessionPhase.Playing)]
        [InlineData(SessionPhase.Lobby)]
        [InlineData(SessionPhase.Disconnected)]
        public void IsAbandoned_EndRequested_False(SessionPhase phase)
            => Assert.False(SdRoomReporter.IsAbandoned(endRequested: true, phase));

        [Theory] // never-started matches: drain outside Playing must NOT notify — reserve TTL reclaims them.
        [InlineData(SessionPhase.None)]
        [InlineData(SessionPhase.Lobby)]
        [InlineData(SessionPhase.Syncing)]
        [InlineData(SessionPhase.Synchronized)]
        [InlineData(SessionPhase.Countdown)]
        [InlineData(SessionPhase.Disconnected)] // the ordinal trap: sorts AFTER Playing, so (< Playing) would miss it
        public void IsAbandoned_NotPlaying_False(SessionPhase phase)
            => Assert.False(SdRoomReporter.IsAbandoned(endRequested: false, phase));

        // ── the emitted abort-notification payload ──────────────────────────────────────────────────────
        [Fact] // payload is EncodeAbortNotification(Abandoned, -1); round-trips to reason=10, culprit=-1
        public void AbandonPayload_RoundTrips_AbandonedNoCulprit()
        {
            byte[] payload = LobbyWire.EncodeAbortNotification(LobbyWire.AbortReasonAbandoned, culpritPlayerId: -1);
            Assert.True(LobbyWire.TryDecodeAbortNotification(payload, out var ab));
            Assert.Equal(LobbyWire.AbortReasonAbandoned, ab.AbortReason);
            Assert.Equal((byte)10, ab.AbortReason);
            Assert.Equal(-1, ab.CulpritPlayerId); // "no single culprit" — distinct meaning, same value as StateDivergence's -1
        }
    }
}
