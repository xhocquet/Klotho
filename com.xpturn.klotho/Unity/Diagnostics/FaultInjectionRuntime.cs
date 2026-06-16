using System;
using xpTURN.Klotho.Logging;
#if KLOTHO_FAULT_INJECTION
using UnityEngine;

#endif
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Unity;

namespace xpTURN.Klotho.Diagnostics
{
    /// <summary>
    /// Diagnostic fault-injection driver. When KLOTHO_FAULT_INJECTION is undefined,
    /// AttachToSession returns null and no schedule executes. Game code can call this
    /// unconditionally — release builds incur a single null return path.
    /// </summary>
    public sealed class FaultInjectionRuntime
    {
#if KLOTHO_FAULT_INJECTION
        readonly KlothoSession _session;
        readonly INetworkTransport _transport;
        readonly IKLogger _logger;
        readonly string _roleLabel;
        readonly KlothoSessionDriver _driver;

        float _rttAnchorTime = -1f;
        int _rttNextIdx;
        int _disconnectNextIdx;
        float _disconnectReconnectAt = -1f;
        bool _attached;

        FaultInjectionRuntime(
            KlothoSession session,
            INetworkTransport transport,
            IKLogger logger,
            string roleLabel,
            KlothoSessionDriver driver)
        {
            _session = session;
            _transport = transport;
            _logger = logger;
            _roleLabel = roleLabel;
            _driver = driver;
        }
#endif

        public static FaultInjectionRuntime AttachToSession(
            KlothoSession session,
            INetworkTransport transport,
            IKLogger logger,
            string roleLabel,
            KlothoSessionDriver driver)
        {
#if KLOTHO_FAULT_INJECTION
            if (session == null || driver == null) return null;

            // Engine null guard — spectator/replay bootstrap can attach before Engine init.
            // Returning null skips diagnostic wiring; a later attach call by caller is not expected for this lift.
            if (session.Engine == null)
            {
                logger?.KWarning($"[FIN] AttachToSession skipped — Engine not initialized (spectator/replay bootstrap)");
                return null;
            }

            var rt = new FaultInjectionRuntime(session, transport, logger, roleLabel, driver);
            rt.Attach();
            return rt;
#else
            return null;
#endif
        }

#if KLOTHO_FAULT_INJECTION
        void Attach()
        {
            if (_attached) return;
            _attached = true;

            _session.Engine.OnGameStart   += OnGameStart;
            _session.Engine.OnMatchEnded  += OnMatchEnded;
            _session.Engine.OnMatchAborted += OnMatchAborted;

            _driver.PreSessionUpdate += OnPreSessionUpdate;
            _driver.Stopping         += OnDriverStopping;
        }
#endif

        public void Detach()
        {
#if KLOTHO_FAULT_INJECTION
            if (!_attached) return;
            _attached = false;

            // Engine may still be alive when called from driver.Stopping (fires before session.Stop).
            var engine = _session?.Engine;
            if (engine != null)
            {
                engine.OnGameStart    -= OnGameStart;
                engine.OnMatchEnded   -= OnMatchEnded;
                engine.OnMatchAborted -= OnMatchAborted;
            }

            if (_driver != null)
            {
                _driver.PreSessionUpdate -= OnPreSessionUpdate;
                _driver.Stopping         -= OnDriverStopping;
            }

            // Emit final summary if a match was in progress (e.g., disconnect during Playing).
            if (_rttAnchorTime >= 0f)
            {
                RttSpikeMetricsCollector.EmitSummary(_logger);
                _rttAnchorTime = -1f;
                _rttNextIdx = 0;
            }
#endif
        }

#if KLOTHO_FAULT_INJECTION
        void OnDriverStopping(KlothoSession _)
        {
            // Wrap self-detach so a throw here cannot leak static event subscriptions.
            // Stopping is a lifecycle transition — match the umbrella wrap policy.
            try { Detach(); }
            catch (Exception e) { _logger?.KError(e, $"[FIN] Detach failed"); }
        }

        void OnPreSessionUpdate(KlothoSession _, float __)
        {
            UpdateRttSchedule();
            UpdateDisconnectSchedule();
        }

        void OnGameStart()
        {
            // Replay mode skip — spike measurement is meaningless on recorded inputs.
            // Spectator mode does not surface OnGameStart through KlothoEngine in this lift,
            // so no extra guard is required for that path.
            if (_session?.Engine?.IsReplayMode == true) return;

            _rttAnchorTime = Time.unscaledTime;
            _rttNextIdx = 0;
            int localId = _session?.Engine?.LocalPlayerId ?? -1;
            RttSpikeMetricsCollector.OnMatchStart(_roleLabel, localId);
        }

        void OnMatchEnded(int winnerId, IMatchEndEvent e) => EmitSummaryAndReset();
        void OnMatchAborted(AbortReason r)                => EmitSummaryAndReset();

        void EmitSummaryAndReset()
        {
            if (_rttAnchorTime < 0f) return;
            RttSpikeMetricsCollector.EmitSummary(_logger);
            _rttAnchorTime = -1f;
            _rttNextIdx = 0;
            _disconnectNextIdx = 0;
            _disconnectReconnectAt = -1f;
        }

        void UpdateRttSchedule()
        {
            if (_rttAnchorTime < 0f) return;

            // Metrics collection stays active even when the schedule is empty so per-peer
            // asymmetry scenarios (only some clients carry a schedule) still get baseline
            // chainBreak / rollback counts from the spike-free peers.
            if (FaultInjection.EmulatedRttSchedule.Count == 0) return;

            float elapsedSec = Time.unscaledTime - _rttAnchorTime;
            var schedule = FaultInjection.EmulatedRttSchedule;
            int currentTick = _session?.Engine?.CurrentTick ?? 0;
            while (_rttNextIdx < schedule.Count && elapsedSec >= schedule[_rttNextIdx].atSec)
            {
                var entry = schedule[_rttNextIdx];
                int prevRtt = FaultInjection.EmulatedRttMs;
                FaultInjection.EmulatedRttMs = entry.rttMs;
                _logger?.KInformation($"[FaultInjection] RTT spike: {prevRtt}ms -> {entry.rttMs}ms at anchorSec={entry.atSec:F1} (tick={currentTick})");
                RttSpikeMetricsCollector.OnSpike(entry.atSec, entry.rttMs);
                _rttNextIdx++;
            }
        }

        void UpdateDisconnectSchedule()
        {
            if (_rttAnchorTime < 0f) return;

            if (FaultInjection.EmulatedDisconnectSchedule.Count == 0 && _disconnectReconnectAt < 0f)
                return;

            float now = Time.unscaledTime;
            float anchorElapsed = now - _rttAnchorTime;
            int currentTick = _session?.Engine?.CurrentTick ?? 0;
            int localId = _session?.Engine?.LocalPlayerId ?? -1;
            bool isHost = _roleLabel == "host";

            var schedule = FaultInjection.EmulatedDisconnectSchedule;
            while (_disconnectNextIdx < schedule.Count && anchorElapsed >= schedule[_disconnectNextIdx].atSec)
            {
                var entry = schedule[_disconnectNextIdx];
                _disconnectNextIdx++;

                // Scope to the targeted client only: the disconnected guest severs its OWN transport
                // (drops from the host) and, after durationSec, arms in-session reconnect — exercising
                // the production ReconnectRequest-first path instead of cold-start (which raced the old
                // service into a PlayerJoin and degraded the return to a late-join). The
                // host never disconnects/reconnects itself — it owns the listen socket, so reconnecting
                // tears it down and every guest's reconnect then fails. A non-targeted guest is left
                // untouched; a null target applies to every guest. Only the target arms
                // _disconnectReconnectAt, so the reconnect trigger below fires on the target alone.
                bool isTarget = !isHost && (!entry.playerId.HasValue || entry.playerId.Value == localId);
                if (isTarget && _transport != null)
                {
                    _logger?.KWarning($"[FaultInjection] Disconnect self (playerId={localId}) at anchorSec={entry.atSec:F1} duration={entry.durationSec:F1}s (tick={currentTick})");
                    foreach (int peerId in _transport.GetConnectedPeerIds())
                        _transport.DisconnectPeer(peerId);
                    _disconnectReconnectAt = now + entry.durationSec;
                }
            }

            if (_disconnectReconnectAt >= 0f && now >= _disconnectReconnectAt)
            {
                _disconnectReconnectAt = -1f;
                // Arm in-session reconnect on the same (old) service rather than cold-start ReconnectAsync.
                // The service's UpdateReconnect → TryReconnectTransport then self-reconnects and sends
                // ReconnectRequest first (Update() runs regardless of Phase), so the host matches the
                // reserved slot instead of late-joining. Cold-start is intentionally not used here — it
                // raced the old service into a PlayerJoin and degraded the return to a late-join.
                _logger?.KWarning($"[FaultInjection] In-session reconnect arm after disconnect (tick={currentTick})");
                switch (_session?.NetworkService)
                {
                    case KlothoNetworkService kns:     kns.ArmInSessionReconnectForFaultInjection(); break;
                    case ServerDrivenClientService sd: sd.ArmInSessionReconnectForFaultInjection();  break;
                }
            }
        }
#endif
    }
}
