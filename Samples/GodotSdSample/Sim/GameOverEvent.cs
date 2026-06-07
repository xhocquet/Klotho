using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Samples.SdSample
{
    [KlothoSerializable(101)]
    public partial class GameOverEvent : SimulationEvent, IMatchEndEvent
    {
        public override EventMode Mode => EventMode.Synced;

        [KlothoOrder(0)] public int WinnerPlayerId;
        [KlothoOrder(1)] public FixedString32 Reason;

        // IMatchEndEvent — drives Engine.OnMatchEnded so the ServerDriven server runs the
        // match-end lifecycle (room drain / EndGracePolicy). A plain Synced event only fires
        // OnSyncedEvent (HUD result) and never ends the match on the server.
        int IMatchEndEvent.WinnerPlayerId => WinnerPlayerId;
        FixedString32 IMatchEndEvent.Reason => Reason;
    }
}
