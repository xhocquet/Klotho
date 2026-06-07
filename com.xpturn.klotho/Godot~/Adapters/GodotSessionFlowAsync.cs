// Task-based join helpers for Godot. Extension
// methods on KlothoSessionFlow that fold the two-step "connect then CreateForConnection" the samples
// otherwise inline. The returned Task completes when the handshake does — which is driven by the
// caller pumping transport.PollEvents each frame (e.g. GodotSessionDriver), so there is no internal
// yield loop.
//
// Note: these do NOT touch the flow's internal connect bookkeeping
// (BeginConnectAttempt / IsConnecting) — those are internal to KlothoSessionFlow and not visible to
// this separate adapter assembly. The logger is therefore passed explicitly (flow.Logger is internal).
// onStarted forwards the in-flight KlothoConnection (e.g. to GodotSessionDriver.TrackConnection) so its
// Update() can be pumped each frame, enforcing the client-side connect/reconnect timeout.
using System;
using System.Threading.Tasks;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Godot
{
    public static class GodotSessionFlowAsync
    {
        // P2P guest join — no room handshake. roomId is -1 (P2P has no rooms).
        public static async Task<KlothoSession> JoinP2PAsync(
            this KlothoSessionFlow flow,
            INetworkTransport transport,
            string host, int port,
            ISessionConfig sessionConfigSeed,
            IKLogger logger = null,
            Action<KlothoConnection> onStarted = null)
        {
            var result = await GodotConnectionAsync.ConnectAsync(transport, host, port, logger, onStarted: onStarted);
            return flow.CreateForConnection(result, roomId: -1, sessionConfigSeed);
        }

        // ServerDriven guest join — sends a RoomHandshakeMessage pre-join, then CreateForConnection
        // with the same roomId.
        public static async Task<KlothoSession> JoinServerDrivenAsync(
            this KlothoSessionFlow flow,
            INetworkTransport transport,
            string host, int port, int roomId,
            ISessionConfig sessionConfigSeed,
            IKLogger logger = null,
            Action<KlothoConnection> onStarted = null)
        {
            var result = await GodotConnectionAsync.ConnectAsync(
                transport, host, port, logger,
                preJoinMessage: new RoomHandshakeMessage { RoomId = roomId },
                onStarted: onStarted);
            return flow.CreateForConnection(result, roomId, sessionConfigSeed);
        }

        // Cold-start reconnect from persisted credentials. Connects to creds.RemoteAddress/Port and
        // restores the session slot (roomId from the credentials).
        public static async Task<KlothoSession> ReconnectAsync(
            this KlothoSessionFlow flow,
            INetworkTransport transport,
            PersistedReconnectCredentials creds,
            ISessionConfig sessionConfigSeed,
            IKLogger logger = null,
            Action<KlothoConnection> onStarted = null)
        {
            var result = await GodotConnectionAsync.ReconnectAsync(transport, creds, logger, onStarted: onStarted);
            return flow.CreateForConnection(result, creds.RoomId, sessionConfigSeed);
        }
    }
}
