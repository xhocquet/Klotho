using ZLogger;

using xpTURN.Klotho.Core;

namespace Brawler
{
    public class BrawlerViewCallbacks : IViewCallbacks
    {
        private readonly BrawlerSimulationCallbacks _sim;

        public BrawlerViewCallbacks(BrawlerSimulationCallbacks sim)
        {
            _sim = sim;
        }

        public void OnGameStart(IKlothoEngine engine)
        {
            engine.Logger?.ZLogInformation($"[Brawler] Game started: playerId={engine.LocalPlayerId}, tick={engine.CurrentTick}");

            _sim.SetEngine(engine);
            if (!engine.IsReplayMode)
                _sim.SendSpawnCommand(engine);   // During replay playback, use the recorded SpawnCharacterCommand — prevent duplicate send
        }

        public void OnTickExecuted(int tick) { }

        public void OnLateJoinActivated(IKlothoEngine engine)
        {
            engine.Logger?.ZLogInformation($"[Brawler] Late join activated: playerId={engine.LocalPlayerId}, tick={engine.CurrentTick}");

            _sim.SetEngine(engine);
            _sim.SendSpawnCommand(engine);
        }
    }
}
