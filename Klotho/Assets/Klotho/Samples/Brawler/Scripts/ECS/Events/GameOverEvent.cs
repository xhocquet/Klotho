using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    [KlothoSerializable(104)]
    public partial class GameOverEvent : SimulationEvent, IMatchEndEvent    // EventMode.Synced
    {
        public override EventMode Mode => EventMode.Synced;
        [KlothoOrder] public int WinnerPlayerId;
        [KlothoOrder] public FixedString32 Reason; // "stocks", "timeout"

        // IMatchEndEvent — explicit impl to keep field names unambiguous.
        int IMatchEndEvent.WinnerPlayerId => WinnerPlayerId;
        FixedString32 IMatchEndEvent.Reason => Reason;
    }
}
