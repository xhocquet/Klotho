using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;

namespace xpTURN.Samples.SdSample
{
    public class SdSimulationCallbacks : ISimulationCallbacks
    {
        private readonly SdInputCapture _input;

        public SdSimulationCallbacks(SdInputCapture input)
        {
            _input = input;
        }

        public void RegisterSystems(EcsSimulation simulation)
        {
            SdSimSetup.RegisterSystems(simulation);
        }

        // Not invoked on the ServerDriven client (initial state arrives via the server FullState),
        // but kept for interface completeness / non-SD-client paths.
        public void OnInitializeWorld(IKlothoEngine engine)
        {
            SdSimSetup.InitializeWorld(engine, engine.SessionConfig.MaxPlayers);
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
    }
}
