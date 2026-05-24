using UnityEngine;
using UnityEngine.InputSystem;
using Microsoft.Extensions.Logging;
using ZLogger;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Diagnostics
{
    public sealed class FaultInjectionHotkeyDriver : MonoBehaviour
    {
        KlothoSession _session;
        ILogger _logger;

        public void Attach(KlothoSession session, ILogger logger)
        {
            _session = session;
            _logger = logger;
        }

        public void Detach()
        {
            _session = null;
            _logger = null;
        }

        void Update()
        {
#if KLOTHO_FAULT_INJECTION
            if (_session == null) return;
            var engine = _session.Engine;
            if (engine == null) return;
            if (Keyboard.current == null) return;
            if (!Keyboard.current.f12Key.wasPressedThisFrame) return;

            var field = typeof(KlothoEngine).GetField("_lastVerifiedTick",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(engine, engine.CurrentTick - 5000);
            _logger?.ZLogWarning($"[Debug] Forced chain stall: lvt={engine.LastVerifiedTick}, currentTick={engine.CurrentTick}");
#endif
        }
    }
}
