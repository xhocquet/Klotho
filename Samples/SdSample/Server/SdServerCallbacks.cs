using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Logging;
using xpTURN.Samples.SdSample;

namespace xpTURN.Samples.SdSample.Server
{
    public class SdServerCallbacks : ISimulationCallbacks
    {
        private readonly IKLogger _logger;
        private readonly int _maxPlayers;

        public SdServerCallbacks(IKLogger logger, int maxPlayers)
        {
            _logger = logger;
            _maxPlayers = maxPlayers;
        }

        public void RegisterSystems(EcsSimulation simulation)
        {
            SdSimSetup.RegisterSystems(simulation);
        }

        public void OnInitializeWorld(IKlothoEngine engine)
        {
            SdSimSetup.InitializeWorld(engine, _maxPlayers);
        }

        public void OnPollInput(int playerId, int tick, ICommandSender sender)
        {
            // no-op: the server produces no local input. ServerInputCollector gathers the
            // client input messages and injects them into the simulation per tick.
        }
    }
}
