// In-game HUD (Godot counterpart of the Unity SdHud): state / score P1,P2 / match timer / result.
// Server-Driven: reads the server-authoritative state from engine.PredictedFrame; players are 1-based.
using global::Godot;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Network;

namespace xpTURN.Samples.SdSample
{
	public partial class GodotSdHud : Control
	{
		private Label _stateLabel, _score0Label, _score1Label, _timerLabel, _resultLabel;
		private Panel _resultPanel;

		private IKlothoEngine _engine;
		private int _matchEndTick;
		private SessionPhase _phase = SessionPhase.None;
		private bool _localReady;

		public override void _Ready()
		{
			_stateLabel  = GetNode<Label>("StateLabel");
			_score0Label = GetNode<Label>("Score0Label");
			_score1Label = GetNode<Label>("Score1Label");
			_timerLabel  = GetNode<Label>("TimerLabel");
			_resultPanel = GetNode<Panel>("ResultPanel");
			_resultLabel = GetNode<Label>("ResultPanel/ResultLabel");
			if (_resultPanel != null) _resultPanel.Visible = false;
		}

		public void AttachEngine(IKlothoEngine engine)
		{
			_engine = engine;
			var frame = engine.PredictedFrame.Frame;
			var stats = frame.AssetRegistry.Get<PlayerStatsAsset>();
			int matchDurationMs = (stats.MatchDuration * FP64.FromInt(1000)).ToInt();
			_matchEndTick = matchDurationMs / frame.DeltaTimeMs;
		}

		public void ShowGameUI() { if (_resultPanel != null) _resultPanel.Visible = false; }

		public void SetPhase(SessionPhase phase) { _phase = phase; RefreshStateText(); }
		public void SetLocalReady(bool localReady) { _localReady = localReady; RefreshStateText(); }

		private void RefreshStateText()
		{
			if (_stateLabel == null) return;
			bool showReady = _localReady && _phase == SessionPhase.Synchronized;
			_stateLabel.Text = $"{_phase}{(showReady ? " (Ready)" : string.Empty)}";
		}

		public void ShowResult(int winnerId)
		{
			if (_resultPanel != null) _resultPanel.Visible = true;
			if (_resultLabel == null) return;
			int local = _engine?.LocalPlayerId ?? -1;
			_resultLabel.Text = winnerId < 0 ? "DRAW" : (winnerId == local ? "WIN" : "LOSE");
		}

		public void RefreshScoreAndTimer()
		{
			if (_engine == null) return;
			var frame = _engine.PredictedFrame.Frame;
			if (frame == null) return;

			int score0 = 0, score1 = 0;
			var filter = frame.Filter<PlayerComponent>();
			while (filter.Next(out var entity))
			{
				ref readonly var p = ref frame.Get<PlayerComponent>(entity);
				if (p.PlayerId == 1) score0 = p.Score;
				else if (p.PlayerId == 2) score1 = p.Score;
			}
			if (_score0Label != null) _score0Label.Text = $"P1: {score0}";
			if (_score1Label != null) _score1Label.Text = $"P2: {score1}";

			int remainingTicks = System.Math.Max(0, _matchEndTick - frame.Tick);
			int remainingSec = (remainingTicks * frame.DeltaTimeMs + 999) / 1000;
			if (_timerLabel != null) _timerLabel.Text = $"{remainingSec}s";
		}
	}
}
