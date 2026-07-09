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
        private readonly int _stageId;

        public SdServerCallbacks(IKLogger logger, int maxPlayers, int stageId = 0)
        {
            _logger = logger;
            _maxPlayers = maxPlayers;
            _stageId = stageId;
        }

        public void RegisterSystems(EcsSimulation simulation)
        {
            SdSimSetup.RegisterSystems(simulation, _stageId);
        }

        public void OnInitializeWorld(IKlothoEngine engine)
        {
            SdSimSetup.InitializeWorld(engine, _maxPlayers, _stageId);
        }

        public void OnPollInput(int playerId, int tick, ICommandSender sender)
        {
            // no-op: the server produces no local input. ServerInputCollector gathers the
            // client input messages and injects them into the simulation per tick.
        }

        public void OnPlayerJoinedWorld(IKlothoEngine engine, Frame frame, int playerId) { } // no per-join world state to seed
    }
}
