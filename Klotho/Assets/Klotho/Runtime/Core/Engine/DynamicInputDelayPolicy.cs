using Microsoft.Extensions.Logging;
using ZLogger;

using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Client-reactive Dynamic InputDelay fallback. Watches non-spawn PastTick rejects
    /// and rollback bursts, escalates engine.RecommendedExtraDelay when the server's
    /// push-based control is delayed/missed. Host is excluded by IsHost guard.
    /// Thresholds are sourced from SessionConfig (server-authoritative).
    /// </summary>
    public sealed class DynamicInputDelayPolicy
    {
        private readonly IKlothoEngine _engine;
        private readonly ILogger _logger;

        // Sliding window state (PastTick reject)
        private int _lastServerPushTick           = int.MinValue;
        private int _reactiveWindowStartTick      = int.MinValue;
        private int _reactiveRejectCount          = 0;

        // Rollback burst state
        private int _lastRollbackBurstWindowStartTick = int.MinValue;
        private int _rollbackCountInWindow            = 0;
        private int _lastReactiveEscalateTick         = int.MinValue;

        public DynamicInputDelayPolicy(IKlothoEngine engine, ILogger logger = null)
        {
            _engine = engine;
            _logger = logger;
        }

        internal void Attach()
        {
            if (_engine.IsHost) return;
            _engine.OnCommandRejected   += HandleCommandRejected;
            _engine.OnRollbackExecuted  += HandleRollback;
            _engine.OnExtraDelayChanged += HandleExtraDelayChanged;
        }

        internal void Detach()
        {
            if (_engine.IsHost) return;
            _engine.OnCommandRejected   -= HandleCommandRejected;
            _engine.OnRollbackExecuted  -= HandleRollback;
            _engine.OnExtraDelayChanged -= HandleExtraDelayChanged;
        }

        private void HandleExtraDelayChanged(int newDelay)
            => _lastServerPushTick = _engine.CurrentTick;

        private void HandleCommandRejected(int tick, int cmdTypeId, RejectionReason reason)
        {
            if (reason != RejectionReason.PastTick) return;
            HandleReactivePastTick(tick, cmdTypeId);
        }

        private void HandleReactivePastTick(int tick, int cmdTypeId)
        {
            var cfg = _engine.SimulationConfig;
            int currentTick = _engine.CurrentTick;

            if (_lastServerPushTick != int.MinValue
                && currentTick - _lastServerPushTick < cfg.ServerPushGraceTicks) return;

            if (currentTick - _reactiveWindowStartTick > cfg.ReactiveWindowTicks)
            {
                _reactiveRejectCount     = 0;
                _reactiveWindowStartTick = currentTick;
            }

            _reactiveRejectCount++;
            _logger?.ZLogDebug(
                $"[DynamicInputDelay] PastTick trigger: count={_reactiveRejectCount}, cmdTypeId={cmdTypeId}, windowStart={_reactiveWindowStartTick}, currentTick={currentTick}");
            if (_reactiveRejectCount < cfg.ReactiveEscalateThreshold) return;

            _engine.EscalateExtraDelay(cfg.ReactiveStep, cfg.ReactiveMax);
            _logger?.ZLogWarning(
                $"[DynamicInputDelay] Reactive escalate (PastTick): step={cfg.ReactiveStep}, max={cfg.ReactiveMax}, recommendedExtraDelay={_engine.RecommendedExtraDelay}");
            _reactiveRejectCount     = 0;
            _reactiveWindowStartTick = currentTick;
        }

        private void HandleRollback(int fromTick, int toTick)
        {
            var cfg = _engine.SimulationConfig;
            if (_engine.RecommendedExtraDelay >= cfg.ReactiveMax) return;

            int now = _engine.CurrentTick;

            if (_lastRollbackBurstWindowStartTick == int.MinValue
                || now - _lastRollbackBurstWindowStartTick > cfg.RollbackWindowTicks)
            {
                _lastRollbackBurstWindowStartTick = now;
                _rollbackCountInWindow            = 0;
            }
            _rollbackCountInWindow++;

            if (_rollbackCountInWindow >= cfg.RollbackBurstCount
                && (_lastReactiveEscalateTick == int.MinValue || now - _lastReactiveEscalateTick > cfg.ReactiveEscalateCooldownTicks)
                && (_lastServerPushTick == int.MinValue || now - _lastServerPushTick > cfg.ServerPushGraceTicks))
            {
                _engine.EscalateExtraDelay(cfg.ReactiveStep, cfg.ReactiveMax);
                _lastReactiveEscalateTick = now;
                _logger?.ZLogWarning(
                    $"[DynamicInputDelay] Reactive escalate (rollback): rollbackCount={_rollbackCountInWindow}, depth={fromTick - toTick}, windowTicks={cfg.RollbackWindowTicks}, recommendedExtraDelay={_engine.RecommendedExtraDelay}");
            }
        }
    }
}
