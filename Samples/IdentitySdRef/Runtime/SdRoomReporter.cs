using System;
using System.Threading;

using xpTURN.Klotho.Logging; // IKLogger
using xpTURN.Klotho.Network; // RoomManager, Room, RoomState, ServerNetworkService

namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>
    /// In-process dedicated-server room reporter (P1). Owns a <see cref="LiteNetLibLobbyReportClient"/> and a
    /// background thread that periodically snapshots the live rooms and pushes a <c>roomReport</c> to the lobby.
    /// <c>serverRegister</c> is the report client's responsibility (sent on connect/reconnect); on reconnect
    /// the client calls back <see cref="MarkDirty"/> so a fresh report follows promptly.
    /// <para>
    /// Core stays untouched: rooms are read via the public <c>GetRoom</c>/<c>State</c>/<c>PlayerCount</c>
    /// surface on this background thread, concurrently with the server loop. Reads are SCALARS ONLY (never
    /// enumerate <c>Players</c>) and defensive (null-check + try/catch → Empty/0) — a one-tick stale value is
    /// fine for this control-plane signal (eventual consistency; the next report corrects).
    /// </para>
    /// </summary>
    public sealed class SdRoomReporter : IDisposable
    {
        private const int PollGranularityMs = 100; // fine enough for prompt reconnect (dirty) response

        private readonly RoomManager _roomManager;
        private readonly LiteNetLibLobbyReportClient _client;
        private readonly long _intervalMs;
        private readonly int _maxRooms;
        private readonly IKLogger _logger;
        private readonly RoomStateReport[] _snapshot; // reused (always _maxRooms entries)
        private readonly LobbyMatchConfigSource _reservations; // null = lobbyless (no reserve table to release)
        private readonly byte[] _prevState; // last-reported per-room wire state (RoomStateEmpty=0 default = init Empty)

        private volatile bool _running;
        private volatile bool _dirty;
        private Thread _thread;

        /// <param name="advertiseHost">/<paramref name="advertisePort"/> — client-reachable address advertised
        /// in serverRegister (dedi's listen port; host is the dev advertised address, not 0.0.0.0).</param>
        /// <param name="maxRooms">/<paramref name="maxPlayersPerRoom"/> — this dedi's actual capacity (D4).</param>
        /// <param name="reservations">Optional lobby-driven match config source — forwarded to the report
        /// client so it receives <c>ReservePush</c> and replies <c>ReserveAck</c>. Null = lobbyless.</param>
        public SdRoomReporter(RoomManager roomManager, IKLogger logger,
                              string lobbyHost, int lobbyPort,
                              string serverId, string advertiseHost, int advertisePort,
                              int maxRooms, int maxPlayersPerRoom, long intervalMs,
                              LobbyMatchConfigSource reservations = null)
        {
            _roomManager = roomManager ?? throw new ArgumentNullException(nameof(roomManager));
            _logger = logger;
            _intervalMs = intervalMs;
            _maxRooms = maxRooms;
            _snapshot = new RoomStateReport[maxRooms];
            _reservations = reservations;
            _prevState = new byte[maxRooms];
            _client = new LiteNetLibLobbyReportClient(logger, lobbyHost, lobbyPort,
                serverId, advertiseHost, advertisePort, maxRooms, maxPlayersPerRoom,
                onConnected: MarkDirty, reservations: reservations);
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "klotho-lobby-report" };
            _thread.Start();
        }

        private void MarkDirty() => _dirty = true; // report-client onConnected hook (reconnect → fresh report)

        private void Loop()
        {
            long lastSentMs = 0;
            while (_running)
            {
                long now = NowMs();
                if (_dirty || now - lastSentMs >= _intervalMs)
                {
                    _dirty = false;
                    int count = CollectSnapshot();
                    try { _client.SendRoomReport(_snapshot, count); }
                    catch (Exception e) { _logger?.KWarning($"[SdRoomReporter] send failed: {e.Message}"); }
                    lastSentMs = now;
                }
                Thread.Sleep(PollGranularityMs);
            }
        }

        // Reports ALL room indices 0..MaxRooms-1 (Empty/0 for null slots) so the lobby distinguishes "missing
        // report" from "explicit Empty". SCALARS ONLY; defensive per-room read.
        private int CollectSnapshot()
        {
            for (int roomId = 0; roomId < _maxRooms; roomId++)
            {
                byte state = LobbyWire.RoomStateEmpty;
                int count = 0;
                try
                {
                    Room r = _roomManager.GetRoom(roomId);          // null when Empty/never-created
                    ServerNetworkService ns = r?.NetworkService;     // null guard: publish reorder (ARM64) / teardown
                    if (r != null && ns != null)
                    {
                        state = MapRoomState(r.State);
                        count = ns.PlayerCount;                       // _players.Count — scalar int, never enumerate
                    }
                }
                catch { state = LobbyWire.RoomStateEmpty; count = 0; }

                // Room-ended → release its dedi-side reservation entry. A live→gone transition
                // (non-Empty prev → Empty now) is the room-dispose signal the reserve table needs:
                // a materialized entry left behind NAKs the next match assigned to this reused room
                // until the reservation TTL expires (5 min). Gate on the down-transition (not "== Empty")
                // so the reserve-before-join window (Empty→Empty, room not yet created) is not touched —
                // releasing then would drop a valid pending reservation. Release is lock-guarded.
                if (_reservations != null
                    && _prevState[roomId] != LobbyWire.RoomStateEmpty
                    && state == LobbyWire.RoomStateEmpty)
                    _reservations.Release(roomId);
                _prevState[roomId] = state;

                _snapshot[roomId].RoomId = roomId;
                _snapshot[roomId].State = state;
                _snapshot[roomId].PlayerCount = count;
            }
            return _maxRooms;
        }

        // Explicit core RoomState → wire byte (CORE values). Explicit map (not a cast) so a core
        // enum reorder is caught here rather than silently corrupting the wire.
        private static byte MapRoomState(RoomState s)
        {
            switch (s)
            {
                case RoomState.Active:    return LobbyWire.RoomStateActive;
                case RoomState.Draining:  return LobbyWire.RoomStateDraining;
                case RoomState.Disposing: return LobbyWire.RoomStateDisposing;
                default:                  return LobbyWire.RoomStateEmpty;
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _thread?.Join(500); } catch { /* ignore */ }
            _client.Dispose();
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
