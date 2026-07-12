using System;
using System.Collections.Generic;

namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>
    /// The lobby's view of room availability — a pure DATA HOLDER (no internal lock). It tracks each
    /// (server, roomId)'s capacity/reservation/occupancy/state plus two ledgers: <c>MatchLedger</c>
    /// (matchId → assigned (server, roomId)) and <c>ReservationLedger</c> (ticket nonce → reservation,
    /// for per-issuance reclaim once a ticket expires).
    /// <para>
    /// Thread-safety: this type does NOT synchronize. The owning <see cref="DevLobbyCore"/> performs every
    /// read and write under its single lock, so the redeem path's consumed-nonce ↔ reservation ↔ occupied
    /// transition stays atomic.
    /// </para>
    /// <para>
    /// Invariant: one room serves exactly one match (<see cref="RoomSlot.SessionId"/> is singular); rooms
    /// are never shared across matches. The dev sample uses a single dedicated server with static capacity
    /// supplied at construction.
    /// </para>
    /// </summary>
    public sealed class LobbyRoomRegistry
    {
        /// <summary>The lobby's room state. <see cref="Reserved"/> marks a room the lobby has assigned but
        /// the dedicated server has not yet materialized. <see cref="Draining"/> is set only when the
        /// dedicated server reports a room is winding down.</summary>
        public enum RoomState { Empty, Reserved, Active, Draining }

        public sealed class RoomSlot
        {
            public RoomState State;
            public int Capacity;   // = server.MaxPlayersPerRoom
            public int Reserved;   // issued-but-not-redeemed (count of this room's ReservationLedger entries)
            public int Occupied;   // redeemed (entered) — reconciled to the dedi's reported PlayerCount (P1)
            public string SessionId; // = matchId; null when Empty/unbound (one room serves one match)
            public string InstanceId; // this match INSTANCE's unique key ({matchId}#{token}); lives and dies with
                                      // SessionId. matchId is a rendezvous key (clients share it, and it repeats
                                      // across matches), so it cannot serve as the result idempotency key — this can.
            public long LastReportMs; // last roomReport touching this slot (P1)
            public bool AckPending;   // reserved-but-not-yet-ack-confirmed by the dedi (tentative). Cleared on
                                      // CommitReservation (ReserveAck ok). Lobbyless assigns never set it (stays false).

            public int EffectiveFree => Capacity - Reserved - Occupied;
        }

        public sealed class ServerEntry
        {
            public string ServerId;
            public string Host;     // advertised address (client-reachable), not the 0.0.0.0 bind
            public int Port;
            public int MaxRooms;
            public int MaxPlayersPerRoom;
            public RoomSlot[] Rooms; // index = roomId (0..MaxRooms-1, dense — mirrors the server's room ids)
            // ── P1 availability (P0-regression guard) ──
            public bool Available;        // seed/AddServer = true; false on disconnect/hang → TryAssign skips
            public long LastReportMs;     // last serverRegister/roomReport; 0 = never heard (backup-timeout exempt)
            public long UnavailableSinceMs; // when marked unavailable; 0 = available. grace-reclaim anchor
            public int PeerId;            // lobby transport peerId that registered this server; -1 = none
        }

        public readonly struct Reservation
        {
            public readonly string MatchId;
            public readonly string ServerId;
            public readonly int RoomId;
            public readonly long ExpiresAt; // ticket expiry; a reservation is reclaimed once its ticket expires
            public Reservation(string matchId, string serverId, int roomId, long expiresAt)
            { MatchId = matchId; ServerId = serverId; RoomId = roomId; ExpiresAt = expiresAt; }
        }

        /// <summary>serverId → server entry. P0 = single server.</summary>
        public readonly Dictionary<string, ServerEntry> Servers =
            new Dictionary<string, ServerEntry>(StringComparer.Ordinal);

        /// <summary>matchId → assigned (serverId, roomId). Kept in sync with RoomSlot.SessionId.</summary>
        public readonly Dictionary<string, (string ServerId, int RoomId)> MatchLedger =
            new Dictionary<string, (string, int)>(StringComparer.Ordinal);

        /// <summary>ticket nonce → reservation (per-issuance TTL reclaim).</summary>
        public readonly Dictionary<string, Reservation> ReservationLedger =
            new Dictionary<string, Reservation>(StringComparer.Ordinal);

        /// <summary>lobby transport peerId → serverId (P1; built by serverRegister, for disconnect mapping).</summary>
        public readonly Dictionary<int, string> ServerByPeer = new Dictionary<int, string>();

        /// <summary>Registers a server with all rooms Empty. ⚠️ `Available=true, LastReportMs=0` is a P0
        /// regression guard: P0 unit tests / the in-proc fake seed via this path and never call
        /// HandleServerRegister — a default-false `Available` would make TryAssign skip every server
        /// (all issue=Full), and a non-zero LastReportMs would let Sweep's backup-timeout reclaim the seed
        /// on the P0 5-minute clock jump.</summary>
        public void AddServer(string serverId, string host, int port, int maxRooms, int maxPlayersPerRoom)
        {
            var rooms = new RoomSlot[maxRooms];
            for (int i = 0; i < maxRooms; i++)
                rooms[i] = new RoomSlot { State = RoomState.Empty, Capacity = maxPlayersPerRoom };
            Servers[serverId] = new ServerEntry
            {
                ServerId = serverId, Host = host, Port = port,
                MaxRooms = maxRooms, MaxPlayersPerRoom = maxPlayersPerRoom, Rooms = rooms,
                Available = true, LastReportMs = 0, UnavailableSinceMs = 0, PeerId = -1,
            };
        }
    }
}
