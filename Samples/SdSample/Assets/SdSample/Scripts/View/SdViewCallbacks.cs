using System;
using xpTURN.Klotho.Core;

namespace xpTURN.Samples.SdSample
{
    public class SdViewCallbacks : IViewCallbacks
    {
        private readonly SdHud _hud;
        private IKlothoEngine _engine;
        private Action<int, SimulationEvent> _onSyncedEvent;

        public SdViewCallbacks(SdHud hud)
        {
            _hud = hud;
        }

        public void OnGameStart(IKlothoEngine engine)
        {
            _engine = engine;
            _hud.AttachEngine(engine);
            _hud.ShowGameUI();
            _onSyncedEvent = HandleSyncedEvent;
            engine.OnSyncedEvent += _onSyncedEvent;
        }

        public void OnTickExecuted(int tick)
        {
            _hud.RefreshScoreAndTimer();
        }

        public void OnLateJoinActivated(IKlothoEngine engine)
        {
        }

        public void Cleanup()
        {
            if (_engine != null && _onSyncedEvent != null)
            {
                _engine.OnSyncedEvent -= _onSyncedEvent;
                _onSyncedEvent = null;
            }
            _engine = null;
        }

        private void HandleSyncedEvent(int tick, SimulationEvent evt)
        {
            if (evt is GameOverEvent go)
            {
                _hud.ShowResult(go.WinnerPlayerId);
            }
        }
    }
}
