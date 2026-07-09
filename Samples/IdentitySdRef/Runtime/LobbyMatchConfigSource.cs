using System;
using System.Collections.Generic;

using xpTURN.Klotho.Logging; // IKLogger
using xpTURN.Klotho.Network;  // IMatchConfigSource, MatchConfigContext

namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>
    /// Lobby-backed <see cref="IMatchConfigSource"/> for a dedicated server: the lobby pushes a room's match
    /// config (<c>ReservePush</c>) BEFORE the ticket-holding client connects, and the server resolves it at
    /// <c>CreateRoomAt</c> via <see cref="TryResolve"/>. Populated by the report client's poll thread
    /// (<see cref="ApplyReservePush"/>), read by the room-creation thread — hence lock-guarded (unlike the
    /// lobbyless <c>StaticMatchConfigSource</c>, which is populated once at startup).
    /// <para>
    /// Self-heal: an unmaterialized reservation is overwritten by a push for a different match (an
    /// ack-lost orphan is reclaimed by the next assignment); only a MATERIALIZED room (already resolved by
    /// CreateRoomAt) refuses a different match. Safe because tickets are minted only on the lobby's commit,
    /// so no client can redeem an orphan reservation.
    /// </para>
    /// </summary>
    public sealed class LobbyMatchConfigSource : IMatchConfigSource
    {
        private sealed class Entry
        {
            public int StageId;
            public byte[] MatchConfigData;
            public string MatchId;
            public long ExpiresAt;   // dedi-side TTL, mirrors the lobby reservation expiry
            public bool Materialized; // set once TryResolve hands this to CreateRoomAt (room went live)
        }

        private readonly object _lock = new object();
        private readonly Dictionary<int, Entry> _table = new Dictionary<int, Entry>();
        private readonly int _maxRooms;
        private readonly Func<long> _nowMs;
        private readonly IKLogger _logger;

        public LobbyMatchConfigSource(int maxRooms, Func<long> nowMs, IKLogger logger = null)
        {
            _maxRooms = maxRooms;
            _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _logger = logger;
        }

        /// <summary>Applies a <c>ReservePush</c> (report-client poll thread). Returns the ReserveAck outcome:
        /// (ok, nakReason). Refuses out-of-range rooms and a different match on an already-materialized room;
        /// otherwise stores/overwrites the reservation.</summary>
        public (bool ok, byte nakReason) ApplyReservePush(int roomId, string matchId, int stageId,
                                                          byte[] matchConfigData, long expiresAt)
        {
            if (roomId < 0 || roomId >= _maxRooms)
                return (false, LobbyWire.ReserveNakRoomRange);

            lock (_lock)
            {
                if (_table.TryGetValue(roomId, out var existing)
                    && existing.Materialized
                    && !string.Equals(existing.MatchId, matchId, StringComparison.Ordinal)
                    && _nowMs() <= existing.ExpiresAt)
                {
                    _logger?.KWarning($"[LobbyMatchConfigSource] reserve NAK room={roomId} match={matchId} (materialized for {existing.MatchId})");
                    return (false, LobbyWire.ReserveNakMatchConflict);
                }

                _table[roomId] = new Entry
                {
                    StageId = stageId,
                    MatchConfigData = matchConfigData,
                    MatchId = matchId,
                    ExpiresAt = expiresAt,
                    Materialized = false,
                };
            }
            _logger?.KInformation($"[LobbyMatchConfigSource] reserved room={roomId} match={matchId} stage={stageId}");
            return (true, LobbyWire.ReserveNakNone);
        }

        /// <summary>Resolves the reservation for <paramref name="roomId"/> at room creation (room-creation
        /// thread). Marks it materialized so a later different-match push refuses. Expired reservations are
        /// dropped → false (room refused, RoomNotFound).</summary>
        public bool TryResolve(int roomId, out MatchConfigContext cfg)
        {
            lock (_lock)
            {
                if (!_table.TryGetValue(roomId, out var e))
                {
                    cfg = default;
                    return false;
                }
                if (_nowMs() > e.ExpiresAt)
                {
                    _table.Remove(roomId);
                    cfg = default;
                    return false;
                }
                e.Materialized = true;
                cfg = new MatchConfigContext(roomId, e.StageId, e.MatchConfigData);
                return true;
            }
        }

        /// <summary>Clears a room's reservation (e.g. on room dispose). Expiry covers this lazily, but an
        /// explicit release frees the slot for a new match immediately.</summary>
        public void Release(int roomId)
        {
            lock (_lock) { _table.Remove(roomId); }
        }
    }
}
