using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Unity
{
    /// <summary>
    /// Runtime.Unity utility that wraps KlothoSession.CreateSpectator (callback-based Runtime API) with UniTask.
    /// Runtime asmdef does not reference UniTask, so the wrapper lives in the Runtime.Unity layer.
    ///
    /// Drives transport polling while spectator bootstrap is pending (SpectatorAcceptMessage +
    /// SimulationConfig + SessionConfig delivery) and resolves the task once Engine/Simulation are constructed.
    /// </summary>
    public static class KlothoSpectatorAsync
    {
        /// <summary>
        /// Connects, awaits SpectatorAcceptMessage, finishes bootstrap, and returns the KlothoSession.
        /// Throws Exception on bootstrap-time failure, OperationCanceledException on cancellation.
        /// </summary>
        public static UniTask<KlothoSession> CreateAsync(
            SpectatorSessionSetup setup, CancellationToken ct = default)
        {
            var tcs = new UniTaskCompletionSource<KlothoSession>();

            var session = KlothoSession.CreateSpectator(
                setup,
                onReady: s => tcs.TrySetResult(s),
                onFailed: ex => tcs.TrySetException(ex));

            var ctRegistration = ct.Register(() =>
            {
                session?.Stop();
                tcs.TrySetCanceled();
            });

            PumpAsync(session, ct, ctRegistration).Forget();

            return tcs.Task;
        }

        private static async UniTaskVoid PumpAsync(
            KlothoSession session,
            CancellationToken ct,
            CancellationTokenRegistration ctRegistration)
        {
            try
            {
                while (!ct.IsCancellationRequested && session.Engine == null && !session.IsStopped)
                {
                    session.Update(Time.unscaledDeltaTime);
                    await UniTask.Yield(PlayerLoopTiming.Update, ct).SuppressCancellationThrow();
                }
            }
            finally
            {
                ctRegistration.Dispose();
            }
        }
    }
}
