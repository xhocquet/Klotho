#if DEBUG
using System;

using xpTURN.Klotho.Core; // AbortReason
using xpTURN.Klotho.ECS;  // ISystem, Frame

namespace xpTURN.Klotho.BrawlerDedicatedServer
{
    /// <summary>
    /// DEV-ONLY (DEBUG builds): fires a StateDivergence abort exactly once, <c>afterMs</c> into the match, to
    /// exercise the server→lobby abort-notification path WITHOUT inducing a real state divergence
    /// (which needs a modified/diverging client). Runs on the room's engine thread each tick — the SAME thread
    /// the natural RecoveryLadder abort uses — so the reporter's OnMatchAborted capture is thread-correct.
    /// Wired via <c>--dev-abort-after-ms &lt;N&gt;</c> on the multi-room dedicated server. NOT compiled into Release.
    /// </summary>
    internal sealed class DevAbortSystem : ISystem
    {
        private readonly Action _abort; // () => engine.AbortMatch(AbortReason.StateDivergence)
        private readonly long _afterMs;
        private bool _fired;

        public DevAbortSystem(Action abort, long afterMs)
        {
            _abort = abort;
            _afterMs = afterMs;
        }

        public void Update(ref Frame frame)
        {
            if (_fired) return;
            // frame.Tick advances only while Running (match Playing); tick 0 = match start.
            if ((long)frame.Tick * frame.DeltaTimeMs < _afterMs) return;
            _fired = true;
            _abort?.Invoke(); // AbortMatch(StateDivergence) → OnMatchAborted → reporter emits MatchResult(Aborted)
        }
    }
}
#endif
