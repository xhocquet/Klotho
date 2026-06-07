// Game view callbacks (Godot counterpart of the Unity SdViewCallbacks): drives the HUD from engine
// events. OnGameStart attaches the HUD + subscribes to synced events; GameOverEvent shows the result.
using System;
using xpTURN.Klotho.Core;

namespace xpTURN.Samples.SdSample
{
	public class GodotSdViewCallbacks : IViewCallbacks
	{
		private readonly GodotSdHud _hud;
		private IKlothoEngine _engine;
		private Action<int, SimulationEvent> _onSyncedEvent;

		public GodotSdViewCallbacks(GodotSdHud hud)
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

		public void OnTickExecuted(int tick) => _hud.RefreshScoreAndTimer();

		public void OnLateJoinActivated(IKlothoEngine engine) { }

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
			if (evt is GameOverEvent go) _hud.ShowResult(go.WinnerPlayerId);
		}
	}
}
