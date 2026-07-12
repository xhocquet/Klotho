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
            public string MatchInstanceId; // the lobby's per-match-instance key, NOT the rendezvous matchId
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
        /// (ok, nakReason). Refuses out-of-range rooms and a DIFFERENT match on an already-materialized room;
        /// a SAME-instance re-push (the reconnect path) is accepted and PRESERVES <c>Materialized</c> — otherwise
        /// stores/overwrites the reservation (unmaterialized).</summary>
        public (bool ok, byte nakReason) ApplyReservePush(int roomId, string matchInstanceId, int stageId,
                                                          byte[] matchConfigData, long expiresAt)
        {
            if (roomId < 0 || roomId >= _maxRooms)
                return (false, LobbyWire.ReserveNakRoomRange);

            lock (_lock)
            {
                _table.TryGetValue(roomId, out var existing);
                bool sameInstance = existing != null
                    && string.Equals(existing.MatchInstanceId, matchInstanceId, StringComparison.Ordinal);

                if (existing != null
                    && existing.Materialized
                    && !sameInstance
                    && _nowMs() <= existing.ExpiresAt)
                {
                    _logger?.KWarning($"[LobbyMatchConfigSource] reserve NAK room={roomId} instance={matchInstanceId} (materialized for {existing.MatchInstanceId})");
                    return (false, LobbyWire.ReserveNakMatchConflict);
                }

                _table[roomId] = new Entry
                {
                    StageId = stageId,
                    MatchConfigData = matchConfigData,
                    MatchInstanceId = matchInstanceId,
                    ExpiresAt = expiresAt,
                    // A same-instance re-push (RePushReservations on reconnect) must NOT demote the flag: a live
                    // room's conflict shield is the only guard on its result key, and dropping it lets a later
                    // different-instance push overwrite the running match's key. A different/new match stays false.
                    Materialized = sameInstance && existing.Materialized,
                };
            }
            _logger?.KInformation($"[LobbyMatchConfigSource] reserved room={roomId} instance={matchInstanceId} stage={stageId}");
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

        /// <summary>Read-only lookup of a room's reserved match-instance key / stageId, for result capture.
        /// Does NOT mutate (unlike <see cref="TryResolve"/>): the reporter reads this at match-end to key the
        /// result self-contained. <paramref name="matchInstanceId"/> is the MATCH INSTANCE key the lobby minted,
        /// not the rendezvous matchId the clients share. Returns false when there is no reservation for the room
        /// (lobbyless / no result to key) — the reporter then emits nothing (no-regression).</summary>
        public bool TryGetMatchInfo(int roomId, out string matchInstanceId, out int stageId)
        {
            lock (_lock)
            {
                if (_table.TryGetValue(roomId, out var e))
                {
                    matchInstanceId = e.MatchInstanceId;
                    stageId = e.StageId;
                    return true;
                }
            }
            matchInstanceId = null;
            stageId = 0;
            return false;
        }
    }
}
