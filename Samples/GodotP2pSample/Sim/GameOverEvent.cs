using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Samples.P2pSample
{
    [KlothoSerializable(101)]
    public partial class GameOverEvent : SimulationEvent
    {
        public override EventMode Mode => EventMode.Synced;

        [KlothoOrder] public int WinnerPlayerId;
    }
}
