using Xunit;

using xpTURN.Klotho.Core;    // AbortReason
using xpTURN.Klotho.Network; // RoomState
using xpTURN.Klotho.Samples.Identity.Sd; // SdRoomReporter, LobbyWire

namespace xpTURN.Samples.DevLobby.Tests
{
    /// <summary>
    /// The two pure core-enum → wire-byte mappings, replacing the raw casts
    /// that coupled the lobby wire / journal to a core enum reorder. Both are internal static and pure
    /// (no logging) so the caller's WARN latch stays out of the unit boundary; the mapping is exercised directly.
    /// </summary>
    public class RoomReporterMappingTests
    {
        // ── AbortReason → wire abortReason ───────────────────────────────────────────────────────────────
        [Theory]
        [InlineData(AbortReason.Unknown,           LobbyWire.AbortReasonUnknown)]         // real engine value (0)
        [InlineData(AbortReason.ChainStallTimeout, LobbyWire.AbortReasonChainStall)]      // 1
        [InlineData(AbortReason.StateDivergence,   LobbyWire.AbortReasonStateDivergence)] // 2 — FROZEN
        [InlineData(AbortReason.ReconnectFailed,   LobbyWire.AbortReasonReconnectFailed)] // 3
        public void TryMapAbortReason_MappedValues_ReturnTrue(AbortReason reason, byte expected)
        {
            Assert.True(SdRoomReporter.TryMapAbortReason(reason, out byte wire));
            Assert.Equal(expected, wire);
        }

        [Fact] // StateDivergence is pinned to EXACTLY 2 — the sole value hardened into the journal
        public void TryMapAbortReason_StateDivergence_IsExactlyTwo()
        {
            Assert.True(SdRoomReporter.TryMapAbortReason(AbortReason.StateDivergence, out byte wire));
            Assert.Equal((byte)2, wire);
        }

        [Fact] // unknown member → false + the DEDICATED Unmapped(255) fallback, distinct BY VALUE from Unknown(0)
        public void TryMapAbortReason_UnknownMember_ReturnsFalse_AndUnmapped()
        {
            Assert.False(SdRoomReporter.TryMapAbortReason((AbortReason)99, out byte wire));
            Assert.Equal(LobbyWire.AbortReasonUnmapped, wire);
            Assert.Equal((byte)255, wire);
            Assert.NotEqual(LobbyWire.AbortReasonUnknown, wire); // "we didn't map" is distinguishable from engine Unknown(0)
        }

        [Fact] // the new wire values must not collide with the existing ones
        public void WireAbortReasons_DoNotCollide()
        {
            byte[] all =
            {
                LobbyWire.AbortReasonUnknown, LobbyWire.AbortReasonChainStall,
                LobbyWire.AbortReasonStateDivergence, LobbyWire.AbortReasonReconnectFailed,
                LobbyWire.AbortReasonAbandoned, LobbyWire.AbortReasonServerShutdown,
                LobbyWire.AbortReasonUnmapped,
            };
            Assert.Equal(all.Length, new System.Collections.Generic.HashSet<byte>(all).Count);
            Assert.Equal((byte)10, LobbyWire.AbortReasonAbandoned);
            Assert.Equal((byte)11, LobbyWire.AbortReasonServerShutdown);
            Assert.Equal((byte)255, LobbyWire.AbortReasonUnmapped);
        }

        // ── RoomState → wire byte ────────────────────────────────────────────────────────────────────────
        [Theory]
        [InlineData(RoomState.Empty,     LobbyWire.RoomStateEmpty)]     // dispose-race (TOCTOU) — explicit, true
        [InlineData(RoomState.Active,    LobbyWire.RoomStateActive)]
        [InlineData(RoomState.Draining,  LobbyWire.RoomStateDraining)]
        [InlineData(RoomState.Disposing, LobbyWire.RoomStateDisposing)]
        public void TryMapRoomState_MappedValues_ReturnTrue(RoomState s, byte expected)
        {
            Assert.True(SdRoomReporter.TryMapRoomState(s, out byte wire));
            Assert.Equal(expected, wire);
        }

        [Fact] // unknown member → false; the caller drops to RoomRead.Unknown (NOT masqueraded as Empty)
        public void TryMapRoomState_UnknownMember_ReturnsFalse()
            => Assert.False(SdRoomReporter.TryMapRoomState((RoomState)99, out _));

        [Fact] // false → RoomRead.Unknown → ShouldRelease == false → the result key is preserved
        public void TryMapRoomState_UnknownMember_PreservesResultKey()
        {
            bool mapped = SdRoomReporter.TryMapRoomState((RoomState)99, out _);
            Assert.False(mapped);
            // A live room read as Unknown (because the map failed) must not release its reserve entry.
            Assert.False(SdRoomReporter.ShouldRelease(LobbyWire.RoomStateActive,
                                                      SdRoomReporter.RoomRead.Unknown,
                                                      LobbyWire.RoomStateEmpty));
        }
    }
}
