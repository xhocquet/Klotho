using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using xpTURN.Klotho.Logging; // IKLogger

namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>
    /// Real <see cref="ILobbyRedeemClient"/> over the engine's vendored LiteNetLib — round-trips redeem to
    /// an external dev lobby process. The validator's <c>RedeemAsync</c> Task is backed by a
    /// <see cref="TaskCompletionSource{T}"/> correlated by request id; the lobby's reply completes it on the
    /// poll thread (the cross-thread completion the handle's volatile barrier covers). No blocking I/O —
    /// send the request, then await the reply.
    /// <para>One validator instance serves all rooms, so calls are concurrent: the request map is a
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> and request ids are allocated with
    /// <see cref="Interlocked"/>. A single lobby connection is reused for all rooms.</para>
    /// </summary>
    public sealed class LiteNetLibLobbyRedeemClient : ILobbyRedeemClient, IDisposable
    {
        private readonly LiteNetLibLobbyConnection _conn;
        private readonly ConcurrentDictionary<int, Pending> _pending = new ConcurrentDictionary<int, Pending>();
        private int _nextRequestId;

        private readonly struct Pending
        {
            public readonly TaskCompletionSource<RedeemResult> Tcs;
            public readonly byte[] Msg;
            public Pending(TaskCompletionSource<RedeemResult> tcs, byte[] msg) { Tcs = tcs; Msg = msg; }
        }

        public LiteNetLibLobbyRedeemClient(IKLogger logger, string host, int port, string connectionKey = "xpTURN.DevLobby")
        {
            _conn = new LiteNetLibLobbyConnection(logger, host, port, connectionKey, OnData, OnConnected);
            _conn.Start();
        }

        public Task<RedeemResult> RedeemAsync(string ticketWire, string sessionId, string serverId, int roomId, CancellationToken ct)
        {
            int requestId = Interlocked.Increment(ref _nextRequestId);
            var tcs = new TaskCompletionSource<RedeemResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] msg = LobbyWire.EncodeRedeemRequest(requestId, ticketWire, sessionId, serverId, roomId);
            _pending[requestId] = new Pending(tcs, msg);

            // A validator-side timeout / Dispose cancels ct → drop the pending entry and cancel the Task
            // (the validator maps cancellation to a fail-closed reject). A late lobby reply for an
            // already-removed id is dropped silently.
            ct.Register(() => { if (_pending.TryRemove(requestId, out var p)) p.Tcs.TrySetCanceled(); });

            _conn.TrySend(msg); // if not yet connected, OnConnected resends; otherwise the timeout cancels
            return tcs.Task;
        }

        private void OnConnected()
        {
            // Resend in-flight requests across a (re)connect — covers the startup race (first join before
            // the lobby connection is up) and reconnect after a drop.
            foreach (var kv in _pending)
                _conn.TrySend(kv.Value.Msg);
        }

        private void OnData(int peerId, byte[] data, int length)
        {
            if (LobbyWire.PeekKind(data, length) != LobbyWire.RedeemResponse) return;
            if (!LobbyWire.TryDecodeRedeemResponse(data, length, out var m)) return;
            if (_pending.TryRemove(m.RequestId, out var p))
                p.Tcs.TrySetResult(m.Result);
            // unknown / already-removed request id → late reply, drop silently
        }

        public void Dispose()
        {
            _conn.Dispose();
            foreach (var kv in _pending)
                kv.Value.Tcs.TrySetCanceled();
            _pending.Clear();
        }
    }
}
