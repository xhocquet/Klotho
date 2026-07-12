using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    [KlothoSerializable(104)]
    public partial class GameOverEvent : SimulationEvent, IMatchEndEvent, IMatchResultProvider    // EventMode.Synced
    {
        public override EventMode Mode => EventMode.Synced;
        [KlothoOrder] public int WinnerPlayerId;
        [KlothoOrder] public FixedString32 Reason; // "stocks", "timeout"

        // Game-authored result blob. Deliberately NOT [KlothoOrder]: it is server-local — never sent
        // and never part of the deterministic content hash (a byte[] hashes by reference → would fake a
        // divergence). Assembled at FireGameOver from verified ECS state and read by the server via the
        // IMatchResultProvider cast. FireGameOver ALWAYS assigns it, so a pooled reuse never surfaces a stale
        // blob (the generated Reset only clears [KlothoOrder] fields).
        public byte[] MatchResultData;

        // IMatchEndEvent / IMatchResultProvider — explicit impl to keep field names unambiguous.
        int IMatchEndEvent.WinnerPlayerId => WinnerPlayerId;
        FixedString32 IMatchEndEvent.Reason => Reason;
        byte[] IMatchResultProvider.MatchResultData => MatchResultData;
    }
}
