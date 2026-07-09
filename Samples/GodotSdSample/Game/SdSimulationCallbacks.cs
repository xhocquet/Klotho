using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;

namespace xpTURN.Samples.SdSample
{
    public class SdSimulationCallbacks : ISimulationCallbacks
    {
        private readonly SdInputCapture _input;
        private readonly int _stageId;

        // stageId is the server-selected stage received via SimulationConfig (SdGameNode passes
        // config.StageId in the callbacks factory). The client builds the same stage's static geometry
        // in RegisterSystems so its static BVH matches the server's.
        public SdSimulationCallbacks(SdInputCapture input, int stageId = 0)
        {
            _input = input;
            _stageId = stageId;
        }

        public void RegisterSystems(EcsSimulation simulation)
        {
            SdSimSetup.RegisterSystems(simulation, _stageId);
        }

        // Not invoked on the ServerDriven client (initial state arrives via the server FullState),
        // but kept for interface completeness / non-SD-client paths.
        public void OnInitializeWorld(IKlothoEngine engine)
        {
            SdSimSetup.InitializeWorld(engine, engine.SessionConfig.MaxPlayers, _stageId);
        }

        // The engine always polls the local player, so playerId is the local id — no extra guard needed.
        public void OnPollInput(int playerId, int tick, ICommandSender sender)
        {
            var cmd = CommandPool.Get<MoveCommand>();
            cmd.PlayerId = playerId;
            cmd.H = _input.H;
            cmd.V = _input.V;
            sender.Send(cmd);
        }

        public void OnPlayerJoinedWorld(IKlothoEngine engine, Frame frame, int playerId) { } // no per-join world state to seed
    }
}
