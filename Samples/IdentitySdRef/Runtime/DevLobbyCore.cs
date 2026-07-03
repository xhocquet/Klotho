using System;
using System.Collections.Generic;

using xpTURN.Klotho.Logging;          // IKLogger (optional report-channel observability)
using xpTURN.Klotho.Samples.Identity; // p2pref: LobbyTicket, LobbyTicketCodec, DevLobbyTicketIssuer, IEd25519Backend

namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>
    /// Transport-agnostic dev-lobby decision logic — the issue + redeem authority, shared by the in-proc
    /// test fake (<see cref="InProcLobbyRedeemClient"/>) AND the external DevLobbyServer process. Extracting
    /// it here means the redeem logic (signature, expiry, match↔server binding, idempotent nonce consume)
    /// is implemented and unit-tested ONCE; the LiteNetLib server is only a thin transport wrapper, so the
    /// two paths cannot diverge.
    /// <para>
    /// This object stands in for the lobby: it holds the Ed25519 private key (via the issuer) and the
    /// authoritative match→server assignment + consumed-nonce ledger. The private key never leaves it
    /// (process boundary in the sample). Thread-safe: a server-global validator drives concurrent redeems.
    /// </para>
    /// </summary>
    public sealed class DevLobbyCore
    {
        private readonly DevLobbyTicketIssuer _issuer;   // signs (private key) — issue side
        private readonly IEd25519Backend _backend;       // verify (public key) — redeem 1st-pass
        private readonly byte[] _publicKey;
        private readonly Func<long> _nowUnixMs;
        private readonly long _idempotencyWindowMs;
        private readonly long _backupTimeoutMs;   // P1 heartbeat backup timeout (hang detection)
        private readonly long _reclaimGraceMs;    // P1 grace after unavailable before reclaiming a server's rooms
        private readonly IKLogger _logger;        // optional — P1 report-channel observability (null in tests)
        private readonly LobbyRoomRegistry _registry; // room availability + match/reservation ledgers

        private readonly object _lock = new object();
        // nonce -> consumed outcome; idempotency window returns the cached result, beyond it = replay reject.
        private readonly Dictionary<string, Consumed> _consumed = new Dictionary<string, Consumed>(StringComparer.Ordinal);

        private readonly struct Consumed
        {
            public readonly long AtMs;       // when first consumed (idempotency-window anchor)
            public readonly long ExpiresAt;   // ticket expiry (record kept until then; after that expiry-check covers it)
            public readonly RedeemResult Result;
            public Consumed(long atMs, long expiresAt, RedeemResult result) { AtMs = atMs; ExpiresAt = expiresAt; Result = result; }
        }

        /// <param name="issuer">Signs tickets (holds the private key) — the lobby's issuance side.</param>
        /// <param name="backend">Ed25519 verify (redeem 1st-pass over wire bytes).</param>
        /// <param name="publicKey">Lobby public key (verify).</param>
        /// <param name="nowUnixMs">Lobby clock (expiry / nonce-window). Injectable for deterministic tests.</param>
        /// <param name="idempotencyWindowMs">Window in which a repeat redeem of the same nonce returns the
        /// cached result — this is how a player who passed validation but disconnected before the slot was
        /// reserved recovers on re-join. Must be ≥ realistic rejoin latency (the core's validation timeout
        /// plus reconnect time).</param>
        /// <param name="registry">Room availability registry (seeded with the dedicated server(s)); holds the
        /// match→(server,room) and nonce→reservation ledgers. The lobby is the authority for room assignment.</param>
        /// <param name="roomReportBackupTimeoutMs">P1: a server with no report for this long (and that has
        /// reported at least once) is marked unavailable — hang backstop behind transport disconnect. Injectable
        /// for tests; defaults to the dev constant.</param>
        /// <param name="serverReclaimGraceMs">P1: grace after a server goes unavailable before its rooms are
        /// reclaimed (≫ reconnect, ≪ reservation TTL). Injectable for tests; defaults to the dev constant.</param>
        public DevLobbyCore(DevLobbyTicketIssuer issuer, IEd25519Backend backend, byte[] publicKey,
                            Func<long> nowUnixMs, long idempotencyWindowMs,
                            LobbyRoomRegistry registry,
                            long roomReportBackupTimeoutMs = SdDevIdentity.RoomReportBackupTimeoutMs,
                            long serverReclaimGraceMs = SdDevIdentity.ServerReclaimGraceMs,
                            IKLogger logger = null)
        {
            _issuer = issuer ?? throw new ArgumentNullException(nameof(issuer));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _publicKey = publicKey ?? throw new ArgumentNullException(nameof(publicKey));
            _nowUnixMs = nowUnixMs ?? throw new ArgumentNullException(nameof(nowUnixMs));
            _idempotencyWindowMs = idempotencyWindowMs;
            _backupTimeoutMs = roomReportBackupTimeoutMs;
            _reclaimGraceMs = serverReclaimGraceMs;
            _logger = logger; // optional; report-channel events log here when present
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        // ── Issue (lobby → client). Caller supplies the time/nonce fields (dev/test determinism). ──
        public string Issue(in LobbyTicket ticket) => _issuer.Issue(ticket);

        /// <summary>Capacity-aware room assignment for a match. Reserves a slot for the ticket's
        /// <paramref name="nonce"/> and returns where the client should connect. Atomic under the single lock.
        /// <para>step 1: reuse the match's existing room if it has free capacity (one room serves one match);
        /// step 2: else reserve a fresh Empty room (the dedicated server materializes it on the first
        /// handshake); else: <see cref="AssignResult.Full"/> (transient — all rooms occupied).</para></summary>
        /// <param name="matchId">The match id (equals the ticket's SessionId).</param>
        /// <param name="nonce">The ticket nonce — the reservation key used to reclaim the slot when the ticket expires.</param>
        /// <param name="ticketExpiresAt">The ticket's expiry; the reservation is reclaimed once this passes.</param>
        public AssignResult TryAssign(string matchId, string nonce, long ticketExpiresAt)
        {
            lock (_lock)
            {
                SweepReservations(_nowUnixMs()); // reclaim ghost reservations / restore Empty rooms first

                // step 1 — reuse the match's existing room if it has capacity (skip if its server is
                // unavailable — falls through to step 2 for multi-server failover; single-server dev → Full).
                if (_registry.MatchLedger.TryGetValue(matchId, out var loc)
                    && _registry.Servers.TryGetValue(loc.ServerId, out var srv0)
                    && srv0.Available
                    && loc.RoomId >= 0 && loc.RoomId < srv0.Rooms.Length)
                {
                    var slot0 = srv0.Rooms[loc.RoomId];
                    if (slot0.EffectiveFree > 0)
                    {
                        slot0.Reserved++;
                        _registry.ReservationLedger[nonce] = new LobbyRoomRegistry.Reservation(matchId, loc.ServerId, loc.RoomId, ticketExpiresAt);
                        return AssignResult.Assigned(srv0, loc.RoomId);
                    }
                    return AssignResult.Full; // same match, room full → cannot spill (one room serves one match)
                }

                // step 2 — reserve a fresh Empty room (the loop handles multiple servers; the dev sample has one).
                foreach (var srv in _registry.Servers.Values)
                {
                    if (!srv.Available) continue; // never assign to a down/hung server
                    for (int roomId = 0; roomId < srv.Rooms.Length; roomId++)
                    {
                        var slot = srv.Rooms[roomId];
                        if (slot.State != LobbyRoomRegistry.RoomState.Empty) continue;
                        slot.State = LobbyRoomRegistry.RoomState.Reserved;
                        slot.SessionId = matchId;
                        slot.Reserved = 1;
                        slot.Occupied = 0;
                        _registry.MatchLedger[matchId] = (srv.ServerId, roomId);
                        _registry.ReservationLedger[nonce] = new LobbyRoomRegistry.Reservation(matchId, srv.ServerId, roomId, ticketExpiresAt);
                        return AssignResult.Assigned(srv, roomId);
                    }
                }
                return AssignResult.Full; // all rooms occupied
            }
        }

        /// <summary>Periodic sweep (call from the lobby loop): reclaim expired reservations + consumed-nonce
        /// records, and restore drained Empty rooms. Atomic under the single lock.</summary>
        public void Sweep(long now)
        {
            lock (_lock)
            {
                EvictExpired(now);       // _consumed
                SweepReservations(now);  // reservationLedger + Empty restore
                SweepServers(now);       // P1: heartbeat backup timeout + reconnect-grace reclaim
            }
        }

        // P1: mark hung servers unavailable (heartbeat backup) and reclaim long-unavailable servers'
        // rooms after the reconnect grace. Must be called under _lock.
        //   - backup timeout requires LastReportMs > 0 (a server that has NEVER reported — the bootstrap seed
        //     before the dedi connects, or a P0 test that jumps the clock — is exempt; P0-regression guard).
        //   - the `Available` guard stops re-stamping UnavailableSinceMs on an already-down server.
        private void SweepServers(long now)
        {
            foreach (var srv in _registry.Servers.Values)
            {
                if (srv.Available && srv.LastReportMs > 0 && now - srv.LastReportMs > _backupTimeoutMs)
                {
                    srv.Available = false;
                    srv.UnavailableSinceMs = now;
                    _logger?.KWarning($"[DevLobby] server '{srv.ServerId}' unavailable — heartbeat timeout ({now - srv.LastReportMs}ms, hang)");
                }
                else if (!srv.Available && srv.UnavailableSinceMs > 0
                         && now - srv.UnavailableSinceMs > _reclaimGraceMs)
                {
                    ReclaimServer(srv);
                    srv.UnavailableSinceMs = 0; // reclaim done — don't repeat every sweep (stays !Available until re-register)
                    _logger?.KInformation($"[DevLobby] server '{srv.ServerId}' rooms reclaimed (reconnect grace expired)");
                }
            }
        }

        /// <summary>Result of <see cref="TryAssign"/>. <c>Ok=false</c> → transient Full (client may retry).</summary>
        public readonly struct AssignResult
        {
            public readonly bool Ok;
            public readonly string ServerId;
            public readonly int RoomId;
            public readonly string Host;
            public readonly int Port;
            private AssignResult(bool ok, string serverId, int roomId, string host, int port)
            { Ok = ok; ServerId = serverId; RoomId = roomId; Host = host; Port = port; }
            public static readonly AssignResult Full = new AssignResult(false, null, -1, string.Empty, 0);
            public static AssignResult Assigned(LobbyRoomRegistry.ServerEntry srv, int roomId)
                => new AssignResult(true, srv.ServerId, roomId, srv.Host, srv.Port);
        }

        // ── Redeem (game server → lobby). Authoritative: nonce consume, expiry, match↔server binding. ──
        // The validator already ran a local 1st-pass; the lobby re-checks everything (it is the authority).
        public RedeemResult Redeem(string ticketWire, string sessionId, string serverId, int roomId)
        {
            long now = _nowUnixMs();

            // 1. split + verify-over-wire + parse (signed-but-malformed → invalid).
            if (!LobbyTicketCodec.TrySplitWire(ticketWire, out string payloadSeg, out string sigSeg))
                return RedeemResult.Reject(SdWireCodes.IdentityInvalid);

            byte[] payload, signature;
            try
            {
                payload = LobbyTicketCodec.Base64UrlDecode(payloadSeg);
                signature = LobbyTicketCodec.Base64UrlDecode(sigSeg);
            }
            catch
            {
                return RedeemResult.Reject(SdWireCodes.IdentityInvalid);
            }
            if (signature.Length != SdWireCodes.Ed25519SignatureLength)
                return RedeemResult.Reject(SdWireCodes.IdentityInvalid);

            bool verified;
            try { verified = _backend.Verify(_publicKey, payload, signature); }
            catch { return RedeemResult.Reject(SdWireCodes.IdentityInvalid); }
            if (!verified)
                return RedeemResult.Reject(SdWireCodes.IdentityInvalid);

            LobbyTicket p;
            try { p = LobbyTicketCodec.ParsePayload(payload); }
            catch { return RedeemResult.Reject(SdWireCodes.IdentityInvalid); }

            // 2. expiry — lobby clock is authority (the validator's local check uses a lenient skew margin).
            if (p.ExpiresAt <= now)
                return RedeemResult.Reject(SdWireCodes.IdentityExpired);

            // 3+4. match binding + nonce consume + occupied transition — ONE critical section so the
            //    consumed-nonce ↔ reservation ↔ occupied transition stays atomic.
            //    Nonce zones for a previously-consumed nonce while the ticket is still valid:
            //      within window  → idempotent recovery (return cached accept; no occupied++);
            //      beyond window   → replay reject (9).
            //    The record is kept until the ticket EXPIRES (then step 2 rejects any re-presentation).
            lock (_lock)
            {
                EvictExpired(now);
                SweepReservations(now);

                // 3. match binding — the lobby's assignment ledger is authoritative (matchId == ticket.SessionId).
                //    Reject if this match is not assigned to the redeeming (server, room) (cross-match / wrong room /
                //    unassigned). P2 room cross-check is FAIL-CLOSED: ledger RoomId is always >= 0 (TryAssign), so a
                //    sentinel roomId = -1 (a non-RoomManager SD that never called SetRoomId) mismatches and is rejected.
                if (!_registry.MatchLedger.TryGetValue(p.SessionId, out var bound)
                    || !string.Equals(bound.ServerId, serverId, StringComparison.Ordinal)
                    || bound.RoomId != roomId)
                    return RedeemResult.Reject(SdWireCodes.IdentitySessionMismatch);

                if (_consumed.TryGetValue(p.Nonce, out Consumed prior))
                {
                    if (now - prior.AtMs <= _idempotencyWindowMs)
                        return prior.Result;                                  // recovery (no double occupied++)
                    return RedeemResult.Reject(SdWireCodes.IdentityRejected);  // replay
                }

                // 4. first consume — accept + reservation→occupied transition (reserved--/occupied++).
                // Demo: attach the account's authoritative entitlement (owned set) in the SAME redeem snapshot
                // as the account/displayName, so the account and its entitlement are confirmed together.
                // Deterministic per account so replay/reconnect recovers the same blob (the idempotency cache
                // below returns this exact result).
                var result = RedeemResult.Accept(p.Account, p.DisplayName, DemoEntitlement.ForAccount(p.Account));
                _consumed[p.Nonce] = new Consumed(now, p.ExpiresAt, result);
                if (_registry.ReservationLedger.TryGetValue(p.Nonce, out var resv)
                    && _registry.Servers.TryGetValue(resv.ServerId, out var srv)
                    && resv.RoomId >= 0 && resv.RoomId < srv.Rooms.Length)
                {
                    var slot = srv.Rooms[resv.RoomId];
                    if (slot.Reserved > 0) slot.Reserved--;
                    slot.Occupied++;
                    slot.State = LobbyRoomRegistry.RoomState.Active;
                    _registry.ReservationLedger.Remove(p.Nonce);
                }
                return result;
            }
        }

        // ── P1: dedi → lobby report handlers (serverRegister / roomReport / disconnect). All under _lock. ──

        /// <summary>serverRegister: upsert the server entry (reconnect-restore preserves rooms; a
        /// capacity change reinits + evicts that server's ledgers, R-G), refresh endpoint/availability, and
        /// (re)bind the peer→server reverse index. Time = internal clock.</summary>
        public void HandleServerRegister(string serverId, string host, int port,
                                         int maxRooms, int maxPlayersPerRoom, int peerId)
        {
            long now = _nowUnixMs();
            lock (_lock)
            {
                _registry.Servers.TryGetValue(serverId, out var existing);
                // drop any previous peer→server mapping for this server (reconnect churn / capacity reinit).
                if (existing != null && existing.PeerId >= 0)
                    _registry.ServerByPeer.Remove(existing.PeerId);

                LobbyRoomRegistry.ServerEntry srv;
                string mode;
                if (existing == null)
                {
                    _registry.AddServer(serverId, host, port, maxRooms, maxPlayersPerRoom);
                    srv = _registry.Servers[serverId];
                    mode = "new";
                }
                else if (existing.MaxRooms != maxRooms || existing.MaxPlayersPerRoom != maxPlayersPerRoom)
                {
                    // capacity changed (R-G): evict the old server's ledgers, then re-create with fresh Empty
                    // rooms (data loss is consistent — capacity change ⇒ dedi restart ⇒ rooms gone).
                    ReclaimServer(existing);
                    _registry.AddServer(serverId, host, port, maxRooms, maxPlayersPerRoom);
                    srv = _registry.Servers[serverId];
                    mode = "reinit(capacity-change)";
                }
                else
                {
                    // reconnect restore: refresh endpoint, PRESERVE rooms/reservations/occupancy.
                    srv = existing;
                    srv.Host = host; srv.Port = port;
                    mode = srv.Available ? "refresh" : "reconnect-restore";
                }

                srv.PeerId = peerId;
                _registry.ServerByPeer[peerId] = serverId;
                srv.Available = true;
                srv.UnavailableSinceMs = 0;
                srv.LastReportMs = now;
                _logger?.KInformation($"[DevLobby] serverRegister server='{serverId}' {host}:{port} cap={maxRooms}x{maxPlayersPerRoom} peer={peerId} ({mode})");
            }
        }

        /// <summary>roomReport: reconcile occupied/state to the dedi's report; reclaim ended rooms.
        /// Single-exception rule — adopt the reported state, except a Reserved&amp;occupied==0 room reported
        /// Empty is kept (lazy not-yet-materialized). Unregistered serverId / out-of-range roomId are ignored.</summary>
        public void HandleRoomReport(string serverId, RoomStateReport[] rooms, int roomCount)
        {
            long now = _nowUnixMs();
            lock (_lock)
            {
                if (!_registry.Servers.TryGetValue(serverId, out var srv)) return; // unregistered → ignore
                srv.Available = true;
                srv.UnavailableSinceMs = 0;
                srv.LastReportMs = now;

                for (int i = 0; i < roomCount; i++)
                {
                    int roomId = rooms[i].RoomId;
                    if (roomId < 0 || roomId >= srv.Rooms.Length) continue; // bounds-check (malformed/shrink)
                    var slot = srv.Rooms[roomId];
                    slot.LastReportMs = now;

                    bool reportEmpty = rooms[i].State == LobbyWire.RoomStateEmpty
                                    || rooms[i].State == LobbyWire.RoomStateDisposing;
                    if (reportEmpty)
                    {
                        // lazy not-yet-materialized (Reserved, nobody redeemed) → keep (no report / Empty does not mean absent).
                        if (slot.State == LobbyRoomRegistry.RoomState.Reserved && slot.Occupied == 0)
                            continue;
                        // a room that was actually in use reports Empty → match ended → reclaim.
                        if (slot.State != LobbyRoomRegistry.RoomState.Empty)
                        {
                            string ended = slot.SessionId;
                            ReclaimRoom(srv, roomId);
                            _logger?.KInformation($"[DevLobby] roomReport reclaim server='{serverId}' room={roomId} match='{ended}' (ended → slot freed)");
                        }
                        continue;
                    }

                    // report Active/Draining → adopt (occupied = dedi authority; SessionId unchanged: a
                    // lobby-Empty slot stays unbound — P1 roomId-gap surface, P2 closes).
                    var prevState = slot.State;
                    slot.Occupied = rooms[i].PlayerCount;
                    slot.State = rooms[i].State == LobbyWire.RoomStateDraining
                        ? LobbyRoomRegistry.RoomState.Draining
                        : LobbyRoomRegistry.RoomState.Active;
                    if (slot.State != prevState) // log only transitions (steady heartbeat stays quiet)
                        _logger?.KInformation($"[DevLobby] roomReport server='{serverId}' room={roomId} {prevState}→{slot.State} occupied={slot.Occupied}");
                }
            }
        }

        /// <summary>Transport disconnect: mark the server unavailable (stop new assignment) but do NOT
        /// reclaim — that waits for the reconnect grace (Sweep). Stale/superseded peerId (reconnect churn) is
        /// ignored via the reverse-index + PeerId guard.</summary>
        public void HandleServerDisconnect(int peerId)
        {
            long now = _nowUnixMs();
            lock (_lock)
            {
                if (!_registry.ServerByPeer.TryGetValue(peerId, out var serverId)) return; // not a server peer
                if (!_registry.Servers.TryGetValue(serverId, out var srv) || srv.PeerId != peerId) return; // superseded
                srv.Available = false;
                srv.UnavailableSinceMs = now;
                _registry.ServerByPeer.Remove(peerId);
                _logger?.KWarning($"[DevLobby] server '{serverId}' unavailable — transport disconnect (peer={peerId}); new assignment stopped, reclaim after grace");
                srv.PeerId = -1; // peerId may be reused by a future (different) connection
            }
        }

        // Reclaim one room slot: evict its match binding + this room's reservationLedger entries, zero counts,
        // → Empty. Must be called under _lock.
        private void ReclaimRoom(LobbyRoomRegistry.ServerEntry srv, int roomId)
        {
            var slot = srv.Rooms[roomId];
            if (slot.SessionId != null) _registry.MatchLedger.Remove(slot.SessionId);
            List<string> drop = null;
            foreach (var kv in _registry.ReservationLedger)
                if (kv.Value.RoomId == roomId
                    && string.Equals(kv.Value.ServerId, srv.ServerId, StringComparison.Ordinal))
                    (drop ??= new List<string>()).Add(kv.Key);
            if (drop != null)
                for (int i = 0; i < drop.Count; i++) _registry.ReservationLedger.Remove(drop[i]);
            slot.SessionId = null;
            slot.Reserved = 0;
            slot.Occupied = 0;
            slot.State = LobbyRoomRegistry.RoomState.Empty;
        }

        // Reclaim every room of a server (grace reclaim / capacity reinit). Must be called under _lock.
        private void ReclaimServer(LobbyRoomRegistry.ServerEntry srv)
        {
            for (int roomId = 0; roomId < srv.Rooms.Length; roomId++)
                ReclaimRoom(srv, roomId);
        }

        // Reclaim reservations whose ticket has expired (now > ExpiresAt) — reserved--; then restore any
        // room that drained to a pure reservation with nothing left (Reserved & reserved==0 & occupied==0)
        // back to Empty and evict its match binding. This is purely a lobby-ledger event (no server report
        // needed). Must be called under _lock.
        private void SweepReservations(long now)
        {
            if (_registry.ReservationLedger.Count > 0)
            {
                List<string> stale = null;
                foreach (var kv in _registry.ReservationLedger)
                    if (now > kv.Value.ExpiresAt) (stale ??= new List<string>()).Add(kv.Key);
                if (stale != null)
                {
                    for (int i = 0; i < stale.Count; i++)
                    {
                        var resv = _registry.ReservationLedger[stale[i]];
                        _registry.ReservationLedger.Remove(stale[i]);
                        if (_registry.Servers.TryGetValue(resv.ServerId, out var srv)
                            && resv.RoomId >= 0 && resv.RoomId < srv.Rooms.Length)
                        {
                            var slot = srv.Rooms[resv.RoomId];
                            if (slot.Reserved > 0) slot.Reserved--;
                        }
                    }
                }
            }
            // Empty restore: a reserved-but-never-redeemed room with no reservations left.
            foreach (var srv in _registry.Servers.Values)
            {
                foreach (var slot in srv.Rooms)
                {
                    if (slot.State == LobbyRoomRegistry.RoomState.Reserved
                        && slot.Reserved == 0 && slot.Occupied == 0)
                    {
                        if (slot.SessionId != null) _registry.MatchLedger.Remove(slot.SessionId);
                        slot.SessionId = null;
                        slot.State = LobbyRoomRegistry.RoomState.Empty;
                    }
                }
            }
        }

        // Drop consumed records for tickets that have expired (now > ExpiresAt); the expiry check then
        // guards any re-presentation, so the ledger stays bounded. Called under _lock.
        private void EvictExpired(long now)
        {
            if (_consumed.Count == 0) return;
            List<string> stale = null;
            foreach (var kv in _consumed)
            {
                if (now > kv.Value.ExpiresAt)
                    (stale ??= new List<string>()).Add(kv.Key);
            }
            if (stale != null)
                for (int i = 0; i < stale.Count; i++) _consumed.Remove(stale[i]);
        }
    }
}
