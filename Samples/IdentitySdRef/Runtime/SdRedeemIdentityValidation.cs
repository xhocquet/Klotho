using System;
using System.Threading;

using xpTURN.Klotho.Network; // IIdentityValidation, IdentityValidationOutcome

namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>
    /// Async validation handle — the core parks it and polls <see cref="IsComplete"/> each tick, then reads
    /// <see cref="Outcome"/> once complete. It is completed from a background thread (the redeem reply or a
    /// cancellation continuation), so it honors the threading contract: write <see cref="Outcome"/> fully,
    /// THEN flip <see cref="IsComplete"/> with a volatile release write (the core acquire-reads
    /// <see cref="IsComplete"/> before reading <see cref="Outcome"/>).
    /// <para>
    /// <see cref="SetResult"/> completes the handle exactly once via an Interlocked guard: a timeout
    /// <see cref="Dispose"/> (which cancels the in-flight redeem) and a late lobby reply can both race to
    /// complete it — only the first wins, so <see cref="Outcome"/> is never overwritten after
    /// <see cref="IsComplete"/> becomes true.
    /// </para>
    /// </summary>
    public sealed class SdRedeemIdentityValidation : IIdentityValidation
    {
        private IdentityValidationOutcome _outcome;
        private volatile bool _complete;
        private int _completedState;   // 0 = pending, 1 = completed (Interlocked CAS, H2)
        private int _disposedState;    // 0 = live, 1 = disposed (idempotent Dispose, N9)
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>Token the validator links its redeem timeout to; <see cref="Dispose"/> cancels it.</summary>
        public CancellationToken CancellationToken => _cts.Token;

        public bool IsComplete => _complete;

        public IdentityValidationOutcome Outcome => _outcome;

        /// <summary>Completes the handle once; subsequent calls are no-ops. Outcome is written before the
        /// volatile IsComplete flip so the core's acquire-read sees a fully-written outcome.</summary>
        public void SetResult(IdentityValidationOutcome outcome)
        {
            if (Interlocked.CompareExchange(ref _completedState, 1, 0) != 0)
                return; // already completed — second completer (cancel vs reply race) is a no-op
            _outcome = outcome;   // fully write outcome...
            _complete = true;     // ...THEN volatile/release flip the completion flag
        }

        /// <summary>Core calls this on pending-peer disconnect or validation timeout; it cancels the
        /// in-flight redeem (best-effort — the real safety against a consumed-then-abandoned nonce is the
        /// lobby's idempotent redeem). Idempotent and exception-safe regardless of dispose ordering relative
        /// to the validator's linked cancellation source.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposedState, 1) != 0)
                return; // idempotent
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { /* already disposed elsewhere — safe */ }
            _cts.Dispose();
        }
    }
}
