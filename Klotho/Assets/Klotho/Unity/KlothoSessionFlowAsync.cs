using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Unity
{
    /// <summary>
    /// Async wrappers for the guest-style entry points. Internal flow:
    /// <list type="bullet">
    ///   <item><c>JoinP2PAsync / JoinServerDrivenAsync → KlothoConnectionAsync.ConnectAsync → flow.CreateForConnection</c></item>
    ///   <item><c>ReconnectAsync  → KlothoConnectionAsync.ReconnectAsync → flow.CreateForConnection</c></item>
    ///   <item><c>SpectateAsync   → KlothoSession.CreateSpectator (callback-based) + UniTask pump</c></item>
    /// </list>
    /// Logger / DeviceIdProvider are pulled from <c>flow</c> internals — callers do not pass them again.
    /// </summary>
    public static class KlothoSessionFlowAsync
    {
        // P2P guest: no roomId, no pre-join handshake.
        public static UniTask<KlothoSession> JoinP2PAsync(
            this KlothoSessionFlow flow,
            INetworkTransport transport,
            string host, int port,
            ISessionConfig sessionConfigSeed,
            CancellationToken ct = default)
            => JoinInternalAsync(flow, transport, host, port,
                preJoinMessage: null, roomId: -1, sessionConfigSeed, ct);

        // ServerDriven guest: builds RoomHandshakeMessage internally via the strategy.
        public static UniTask<KlothoSession> JoinServerDrivenAsync(
            this KlothoSessionFlow flow,
            INetworkTransport transport,
            string host, int port, int roomId,
            ISessionConfig sessionConfigSeed,
            CancellationToken ct = default)
        {
            var strategy = ServerDrivenModeStrategy.Instance;
            return JoinInternalAsync(flow, transport, host, port,
                preJoinMessage: strategy.BuildPreJoinHandshake(roomId),
                roomId: strategy.NormalizeRoomId(roomId),
                sessionConfigSeed, ct);
        }

        private static async UniTask<KlothoSession> JoinInternalAsync(
            KlothoSessionFlow flow,
            INetworkTransport transport,
            string host, int port,
            NetworkMessageBase preJoinMessage,
            int roomId,
            ISessionConfig sessionConfigSeed,
            CancellationToken ct)
        {
            var result = await KlothoConnectionAsync.ConnectAsync(
                transport, host, port, ct, flow.Logger, preJoinMessage, flow.DeviceIdProvider);
            return flow.CreateForConnection(result, roomId, sessionConfigSeed);
        }

        public static async UniTask<KlothoSession> ReconnectAsync(
            this KlothoSessionFlow flow,
            INetworkTransport transport,
            PersistedReconnectCredentials creds,
            ISessionConfig sessionConfigSeed,
            CancellationToken ct = default)
        {
            var result = await KlothoConnectionAsync.ReconnectAsync(transport, creds, ct, flow.Logger);
            return flow.CreateForConnection(result, creds.RoomId, sessionConfigSeed);
        }

        /// <summary>
        /// Spectator entry without an explicit transport. The framework instantiates one via
        /// <see cref="KlothoFlowSetup.SpectatorTransportFactory"/>. Convenience for the common case;
        /// use the transport-bearing overload to inject a custom transport.
        /// </summary>
        public static UniTask<KlothoSession> SpectateAsync(
            this KlothoSessionFlow flow,
            string host, int port, int roomId,
            CancellationToken ct = default)
        {
            var factory = flow.SpectatorTransportFactory
                ?? throw new InvalidOperationException(
                    "KlothoFlowSetup.SpectatorTransportFactory must be set to use this overload. " +
                    "Either set the factory or pass a transport explicitly to the SpectateAsync(transport, ...) overload.");

            var transport = factory()
                ?? throw new InvalidOperationException("SpectatorTransportFactory returned null.");

            return flow.SpectateAsync(transport, host, port, roomId, ct);
        }

        public static UniTask<KlothoSession> SpectateAsync(
            this KlothoSessionFlow flow,
            INetworkTransport spectatorTransport,
            string host, int port, int roomId,
            CancellationToken ct = default)
        {
            var tcs = new UniTaskCompletionSource<KlothoSession>();
            var setup = flow.BuildSpectatorSetup(spectatorTransport, host, port, roomId);

            // onReady fires AFTER KlothoSession.OnSessionCreated (static, editor scope).
            // Flow.OnSessionCreated is invoked here so the game receives both notifications
            // in the same logical "session ready" frame.
            var session = KlothoSession.CreateSpectator(
                setup,
                onReady: s =>
                {
                    try
                    {
                        flow.FireOnSessionCreatedForSpectator(s);
                        tcs.TrySetResult(s);
                    }
                    catch (Exception e)
                    {
                        // FireOnSessionCreated already called s.Stop() — surface to awaiter.
                        tcs.TrySetException(e);
                    }
                },
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
            KlothoSession session, CancellationToken ct, CancellationTokenRegistration ctRegistration)
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
