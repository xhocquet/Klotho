using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using xpTURN.Klotho.Logging; // IKLogger

namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>Lobby issue outcome: status + the connection target the lobby assigned.
    /// <see cref="Ok"/>=false → transient <see cref="Full"/> (all rooms occupied; the client may retry).</summary>
    public readonly struct IssueResult
    {
        public readonly byte Status;   // LobbyWire.IssueOk / IssueFull
        public readonly string Ticket; // signed ticket wire (empty on Full)
        public readonly string Host;   // dedi address to connect (empty on Full)
        public readonly int Port;
        public readonly int RoomId;    // -1 on Full
        public readonly byte Mode;     // LobbyWire.ModeSd
        public IssueResult(byte status, string ticket, string host, int port, int roomId, byte mode)
        { Status = status; Ticket = ticket; Host = host; Port = port; RoomId = roomId; Mode = mode; }
        public bool Ok => Status == LobbyWire.IssueOk;
        public bool Full => Status == LobbyWire.IssueFull;
    }

    /// <summary>
    /// Client-side lobby Issue client over LiteNetLib — the game client fetches its signed ticket AND the
    /// assigned (endpoint, roomId) from the dev lobby before connecting to the game server. It reuses the
    /// same transport stack the client already links via Klotho, so it needs no UnityWebRequest.
    /// <see cref="IssueAsync"/> returns an <see cref="IssueResult"/> (status + ticket + endpoint + roomId).
    /// </summary>
    public sealed class LiteNetLibLobbyIssueClient : IDisposable
    {
        private readonly LiteNetLibLobbyConnection _conn;
        private readonly ConcurrentDictionary<int, Pending> _pending = new ConcurrentDictionary<int, Pending>();
        private int _nextRequestId;

        private readonly struct Pending
        {
            public readonly TaskCompletionSource<IssueResult> Tcs;
            public readonly byte[] Msg;
            public Pending(TaskCompletionSource<IssueResult> tcs, byte[] msg) { Tcs = tcs; Msg = msg; }
        }

        public LiteNetLibLobbyIssueClient(IKLogger logger, string host, int port, string connectionKey = "xpTURN.DevLobby")
        {
            _conn = new LiteNetLibLobbyConnection(logger, host, port, connectionKey, OnData, OnConnected);
            _conn.Start();
        }

        /// <summary>Requests a signed ticket + room assignment for <paramref name="matchId"/>. Returns an
        /// <see cref="IssueResult"/> (Ok with endpoint/roomId, or transient Full). Honors <paramref name="ct"/>.</summary>
        public Task<IssueResult> IssueAsync(string authToken, string displayName, string matchId, CancellationToken ct)
        {
            int requestId = Interlocked.Increment(ref _nextRequestId);
            var tcs = new TaskCompletionSource<IssueResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] msg = LobbyWire.EncodeIssueRequest(requestId, authToken, displayName, matchId);
            _pending[requestId] = new Pending(tcs, msg);
            ct.Register(() => { if (_pending.TryRemove(requestId, out var p)) p.Tcs.TrySetCanceled(); });
            _conn.TrySend(msg);
            return tcs.Task;
        }

        private void OnConnected()
        {
            foreach (var kv in _pending)
                _conn.TrySend(kv.Value.Msg);
        }

        private void OnData(int peerId, byte[] data, int length)
        {
            if (LobbyWire.PeekKind(data, length) != LobbyWire.IssueResponse) return;
            if (!LobbyWire.TryDecodeIssueResponse(data, length, out var m)) return;
            if (_pending.TryRemove(m.RequestId, out var p))
                p.Tcs.TrySetResult(new IssueResult(m.Status, m.TicketWire, m.Host, m.Port, m.RoomId, m.Mode));
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
