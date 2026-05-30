using UnityEngine;
using UnityEngine.UI;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Network;

namespace xpTURN.Samples.SdSample
{
    public class SdHud : MonoBehaviour
    {
        [SerializeField] private GameObject _gameUiRoot;
        [SerializeField] private GameObject _resultUiRoot;
        [SerializeField] private Text _stateText;
        [SerializeField] private Text _scoreText0;
        [SerializeField] private Text _scoreText1;
        [SerializeField] private Text _timerText;
        [SerializeField] private Text _resultText;

        private IKlothoEngine _engine;
        private int _matchEndTick;
        private SessionPhase _phase = SessionPhase.None;
        private bool _localReady;

        public void AttachEngine(IKlothoEngine engine)
        {
            _engine = engine;
            var frame = ((EcsSimulation)engine.Simulation).Frame;
            var stats = frame.AssetRegistry.Get<PlayerStatsAsset>();
            int matchDurationMs = (stats.MatchDuration * FP64.FromInt(1000)).ToInt();
            _matchEndTick = matchDurationMs / frame.DeltaTimeMs;
        }

        public void SetPhase(SessionPhase phase)
        {
            _phase = phase;
            RefreshStateText();
        }

        public void SetLocalReady(bool localReady)
        {
            _localReady = localReady;
            RefreshStateText();
        }

        private void RefreshStateText()
        {
            if (_stateText == null) return;
            // (Ready) tag only meaningful in Synchronized lobby — Countdown/Playing implies all ready.
            bool showReady = _localReady && _phase == SessionPhase.Synchronized;
            string readyTag = showReady ? " (Ready)" : string.Empty;
            _stateText.text = $"{_phase}{readyTag}";
        }

        public void ShowGameUI()
        {
            if (_gameUiRoot != null) _gameUiRoot.SetActive(true);
            if (_resultUiRoot != null) _resultUiRoot.SetActive(false);
        }

        public void ShowResult(int winnerId)
        {
            if (_resultUiRoot != null) _resultUiRoot.SetActive(true);
            if (_resultText == null) return;

            int local = _engine?.LocalPlayerId ?? -1;
            if (winnerId < 0) _resultText.text = "DRAW";
            else if (winnerId == local) _resultText.text = "WIN";
            else _resultText.text = "LOSE";
        }

        public void RefreshScoreAndTimer()
        {
            if (_engine == null) return;
            var frame = ((EcsSimulation)_engine.Simulation).Frame;

            int score0 = 0, score1 = 0;
            var filter = frame.Filter<PlayerComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var p = ref frame.Get<PlayerComponent>(entity);
                if (p.PlayerId == 1) score0 = p.Score;
                else if (p.PlayerId == 2) score1 = p.Score;
            }

            if (_scoreText0 != null) _scoreText0.text = $"P1: {score0}";
            if (_scoreText1 != null) _scoreText1.text = $"P2: {score1}";

            int remainingTicks = Mathf.Max(0, _matchEndTick - frame.Tick);
            int remainingSec = (remainingTicks * frame.DeltaTimeMs + 999) / 1000;
            if (_timerText != null) _timerText.text = $"{remainingSec}s";
        }
    }
}
