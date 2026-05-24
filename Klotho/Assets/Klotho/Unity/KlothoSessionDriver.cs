using System;
using UnityEngine;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Unity
{
    /// <summary>
    /// MonoBehaviour adapter that drives a KlothoSession through Unity's Update loop and exposes
    /// hooks for pre/post-Update logic, Stop teardown, and idle (no-session) polling. Game code
    /// attaches a session via <see cref="Attach"/> and tears down through <see cref="DetachAndStop"/>.
    ///
    /// Hook semantics:
    ///   PreSessionUpdate / PostSessionUpdate / IdlePoll — steady-state. Exceptions propagate (no wrap).
    ///   Stopping — lifecycle transition. Wrapped in try/finally so Session.Stop is guaranteed
    ///   even if a subscriber throws.
    ///
    /// Multi-cast invocation follows C# default: first throw in the invocation list stops
    /// subsequent subscribers from firing.
    /// </summary>
    public sealed class KlothoSessionDriver : MonoBehaviour
    {
        public KlothoSession Session { get; private set; }

        /// <summary>
        /// True while <see cref="DetachAndStop"/> is in flight — Stopping hook + session.Stop() are mid-dispatch.
        /// Game code that has its own teardown body should early-return when this is true to avoid re-entering
        /// the same shutdown sequence (typically through <see cref="IKlothoSessionObserver.OnSessionStopped"/>).
        /// </summary>
        public bool IsStopping => _stopping;

        public event Action<KlothoSession, float> PreSessionUpdate;
        public event Action<KlothoSession, float> PostSessionUpdate;
        public event Action<KlothoSession> Stopping;
        public event Action IdlePoll;

        long _lastTicks;
        bool _stopping;

        public void Attach(KlothoSession session)
        {
            Session = session;
            _lastTicks = 0;
        }

        public void DetachAndStop()
        {
            if (_stopping) return;

            var s = Session;
            if (s == null) return;
            
            _stopping = true;
            try { Stopping?.Invoke(s); }
            finally { s.Stop(); Session = null; _stopping = false; }
        }

        void Update()
        {
            var s = Session;
            if (s == null) { IdlePoll?.Invoke(); return; }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            float dt = (_lastTicks > 0) ? (now - _lastTicks) * 0.001f : 0f;
            _lastTicks = now;
            
            PreSessionUpdate?.Invoke(s, dt);
            if (s.IsStopped) return;
            
            s.Update(dt);
            if (s.IsStopped) return;
            
            PostSessionUpdate?.Invoke(s, dt);
        }

        void OnDestroy() => DetachAndStop();
    }
}
