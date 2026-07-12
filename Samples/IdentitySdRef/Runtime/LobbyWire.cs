using System.Text;

using xpTURN.Klotho.Serialization; // SpanWriter, SpanReader

namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>One room's reported state for <c>RoomReport</c> (P1). Public (top-level) so it can appear in
    /// the public reporter / reconcile signatures — <see cref="LobbyWire"/> itself stays internal.
    /// <c>State</c> = CORE RoomState byte values (see <see cref="LobbyWire"/> RoomState* constants).</summary>
    public struct RoomStateReport { public int RoomId; public byte State; public int PlayerCount; }

    /// <summary>
    /// Lobby req/resp wire codec for the dev lobby protocol over LiteNetLib. The envelope uses the engine's
    /// own <see cref="SpanWriter"/>/<see cref="SpanReader"/> (no reflection, so it is IL2CPP/AOT-safe and
    /// allocation-light). The ticket itself stays the opaque JSON wire produced by the shared ticket codec;
    /// only this RPC envelope is SpanWriter-encoded.
    /// Layout: [byte kind][int32 requestId][fields…]; strings are length-prefixed UTF-8 (SpanWriter.WriteString).
    /// </summary>
    internal static class LobbyWire
    {
        public const byte IssueRequest   = 1;
        public const byte IssueResponse  = 2;
        public const byte RedeemRequest  = 3;
        public const byte RedeemResponse = 4;
        public const byte ServerRegister = 5; // dedi → lobby: startup/reconnect endpoint + capacity advertise (one-way)
        public const byte RoomReport     = 6; // dedi → lobby: periodic heartbeat + on-change room occupancy/state (one-way)
        public const byte ReservePush    = 7; // lobby → dedi: reserve a room's match config (stageId + payload) before the client connects
        public const byte ReserveAck     = 8; // dedi → lobby: confirm/refuse a ReservePush (gates the deferred IssueResponse)
        public const byte MatchResult    = 9;  // dedi → lobby: verified match result / abort notification
        public const byte MatchResultAck = 10; // lobby → dedi: result accepted (responsibility handoff → dedi stops retrying)

        // MatchResult terminationKind: NormalEnd → payload is the game-owned result blob; Aborted → payload is
        // the abort notification blob (no game result). Both carry the identity roster side-channel.
        public const byte TerminationNormalEnd = 0;
        public const byte TerminationAborted   = 1;

        // ReserveAck nak reason (ok=false). ok=true → None.
        public const byte ReserveNakNone          = 0;
        public const byte ReserveNakRoomRange     = 1; // roomId outside the server's room range
        public const byte ReserveNakMatchConflict = 2; // room already materialized (active) for a different match

        // RoomReport room-state byte = CORE RoomState integer values (NOT the lobby's own RoomState, which
        // differs: lobby Reserved=1 vs core Active=1). The lobby MUST MAP these to its reconcile logic, never
        // cast to LobbyRoomRegistry.RoomState. Pinned here so a core enum reorder is caught at this seam.
        public const byte RoomStateEmpty     = 0;
        public const byte RoomStateActive    = 1;
        public const byte RoomStateDraining  = 2;
        public const byte RoomStateDisposing = 3;

        // Wire abortReason (the abort-notification payload's leading byte; see EncodeAbortNotification). An
        // INDEPENDENT value space from core AbortReason — LobbyWire does not reference core, and the dedi maps
        // core→wire in SdRoomReporter.TryMapAbortReason (the side that knows core owns the conversion). Only
        // StateDivergence(2) is frozen: it is the sole value the CaptureAbort filter lets reach the encoder, so
        // it is the only value ever written to the persistent journal — reassigning 2 would retroactively
        // misread existing records. 0/1/3 are never-written (aligned to the current engine ordinals for log
        // readability only — a recommendation, not a constraint).
        public const byte AbortReasonUnknown         = 0;   // core Unknown (never written)
        public const byte AbortReasonChainStall      = 1;   // core ChainStallTimeout (never written)
        public const byte AbortReasonStateDivergence = 2;   // ← FROZEN (only value hardened into the journal)
        public const byte AbortReasonReconnectFailed = 3;   // core ReconnectFailed (never written)
        public const byte AbortReasonAbandoned       = 10;  // all peers left
        public const byte AbortReasonServerShutdown  = 11;  // reserved for a future shutdown notification (never written today); MUST stay distinct from Abandoned
        public const byte AbortReasonUnmapped        = 255; // dedicated unmapped-fallback — distinct BY VALUE from Unknown(0) so it is identifiable even in the journal

        // IssueResponse status — the issue path's own result code (separate from the disconnect-payload
        // reject codes the join/redeem path uses).
        public const byte IssueOk   = 0; // ticket + endpoint + roomId valid
        public const byte IssueFull = 1; // transient — all rooms full; the client may retry later

        // IssueResponse mode — the session model the assigned server runs.
        public const byte ModeSd = 0; // Server-Driven

        private static int Str(string s) => 4 + Encoding.UTF8.GetByteCount(s ?? string.Empty);

        // ── Issue ─────────────────────────────────────────────────────────────
        public static byte[] EncodeIssueRequest(int requestId, string authToken, string displayName, string matchId)
        {
            var buf = new byte[1 + 4 + Str(authToken) + Str(displayName) + Str(matchId)];
            var w = new SpanWriter(buf);
            w.WriteByte(IssueRequest); w.WriteInt32(requestId);
            w.WriteString(authToken); w.WriteString(displayName); w.WriteString(matchId);
            return buf;
        }

        public struct IssueReq { public int RequestId; public string AuthToken, DisplayName, MatchId; }
        public static bool TryDecodeIssueRequest(byte[] data, int length, out IssueReq m)
        {
            m = default;
            var r = new SpanReader(data, 0, length);
            if (r.ReadByte() != IssueRequest) return false;
            m.RequestId = r.ReadInt32();
            m.AuthToken = r.ReadString(); m.DisplayName = r.ReadString(); m.MatchId = r.ReadString();
            return true;
        }

        public static byte[] EncodeIssueResponse(int requestId, byte status, string ticketWire,
                                                 string host, int port, int roomId, byte mode)
        {
            var buf = new byte[1 + 4 + 1 + Str(ticketWire) + Str(host) + 4 + 4 + 1];
            var w = new SpanWriter(buf);
            w.WriteByte(IssueResponse); w.WriteInt32(requestId);
            w.WriteByte(status); w.WriteString(ticketWire);
            w.WriteString(host); w.WriteInt32(port); w.WriteInt32(roomId); w.WriteByte(mode);
            return buf;
        }

        public struct IssueResp
        {
            public int RequestId; public byte Status; public string TicketWire;
            public string Host; public int Port; public int RoomId; public byte Mode;
        }
        public static bool TryDecodeIssueResponse(byte[] data, int length, out IssueResp m)
        {
            m = default;
            var r = new SpanReader(data, 0, length);
            if (r.ReadByte() != IssueResponse) return false;
            m.RequestId = r.ReadInt32();
            m.Status = r.ReadByte(); m.TicketWire = r.ReadString();
            m.Host = r.ReadString(); m.Port = r.ReadInt32(); m.RoomId = r.ReadInt32(); m.Mode = r.ReadByte();
            return true;
        }

        // ── Redeem ────────────────────────────────────────────────────────────
        // roomId (P2: routed room the validator carries for the lobby's (server,room) cross-check) is a
        // trailing append — old decoders ignore it; the redeem channel is dedi↔lobby so both rebuild together.
        public static byte[] EncodeRedeemRequest(int requestId, string ticketWire, string sessionId, string serverId, int roomId)
        {
            var buf = new byte[1 + 4 + Str(ticketWire) + Str(sessionId) + Str(serverId) + 4];
            var w = new SpanWriter(buf);
            w.WriteByte(RedeemRequest); w.WriteInt32(requestId);
            w.WriteString(ticketWire); w.WriteString(sessionId); w.WriteString(serverId); w.WriteInt32(roomId);
            return buf;
        }

        public struct RedeemReq { public int RequestId; public string TicketWire, SessionId, ServerId; public int RoomId; }
        public static bool TryDecodeRedeemRequest(byte[] data, int length, out RedeemReq m)
        {
            m = default;
            try
            {
                var r = new SpanReader(data, 0, length);
                if (r.ReadByte() != RedeemRequest) return false;
                m.RequestId = r.ReadInt32();
                m.TicketWire = r.ReadString(); m.SessionId = r.ReadString(); m.ServerId = r.ReadString();
                m.RoomId = r.ReadInt32();
                return true;
            }
            catch { m = default; return false; } // malformed/truncated wire → reject (never throw on the poll thread)
        }

        public static byte[] EncodeRedeemResponse(int requestId, RedeemResult result)
        {
            // entitlement (opaque, length-prefixed) is a trailing append — old decoders ignore it; the redeem
            // channel is dedi↔lobby so both rebuild together. Language-agnostic: int32-len + raw bytes.
            int entLen = result.Entitlement?.Length ?? 0;
            var buf = new byte[1 + 4 + 1 + Str(result.Account) + Str(result.DisplayName) + 1 + 4 + entLen];
            var w = new SpanWriter(buf);
            w.WriteByte(RedeemResponse); w.WriteInt32(requestId);
            w.WriteBool(result.Ok); w.WriteString(result.Account); w.WriteString(result.DisplayName);
            w.WriteByte(result.RejectWireCode);
            w.WriteBytes(result.Entitlement); // null → length 0
            return buf;
        }

        public struct RedeemResp { public int RequestId; public RedeemResult Result; }
        public static bool TryDecodeRedeemResponse(byte[] data, int length, out RedeemResp m)
        {
            m = default;
            var r = new SpanReader(data, 0, length);
            if (r.ReadByte() != RedeemResponse) return false;
            m.RequestId = r.ReadInt32();
            bool ok = r.ReadBool();
            string account = r.ReadString();
            string displayName = r.ReadString();
            byte rejectCode = r.ReadByte();
            byte[] entitlement = null;
            if (r.Remaining >= 4) // trailing append — tolerate an old encoder that omitted it
            {
                var span = r.ReadBytes();
                if (span.Length > 0) entitlement = span.ToArray();
            }
            m.Result = ok ? RedeemResult.Accept(account, displayName, entitlement) : RedeemResult.Reject(rejectCode);
            return true;
        }

        // ── ServerRegister (dedi → lobby, one-way) ──────────────────────────────
        public static byte[] EncodeServerRegister(int requestId, string serverId, string host, int port,
                                                  int maxRooms, int maxPlayersPerRoom)
        {
            var buf = new byte[1 + 4 + Str(serverId) + Str(host) + 4 + 4 + 4];
            var w = new SpanWriter(buf);
            w.WriteByte(ServerRegister); w.WriteInt32(requestId);
            w.WriteString(serverId); w.WriteString(host); w.WriteInt32(port);
            w.WriteInt32(maxRooms); w.WriteInt32(maxPlayersPerRoom);
            return buf;
        }

        public struct ServerRegisterMsg { public int RequestId; public string ServerId, Host; public int Port, MaxRooms, MaxPlayersPerRoom; }
        public static bool TryDecodeServerRegister(byte[] data, int length, out ServerRegisterMsg m)
        {
            m = default;
            try
            {
                var r = new SpanReader(data, 0, length);
                if (r.ReadByte() != ServerRegister) return false;
                m.RequestId = r.ReadInt32();
                m.ServerId = r.ReadString(); m.Host = r.ReadString(); m.Port = r.ReadInt32();
                m.MaxRooms = r.ReadInt32(); m.MaxPlayersPerRoom = r.ReadInt32();
                return true;
            }
            catch { m = default; return false; } // malformed wire → reject (never throw on the poll thread)
        }

        // ── RoomReport (dedi → lobby, one-way; variable length) ─────────────────
        private const int RoomReportEntryBytes = 4 + 1 + 4; // roomId + state + playerCount

        /// <summary>Encodes <paramref name="count"/> rooms from <paramref name="rooms"/> (allows a reused
        /// oversized array). Caller sends exactly MaxRooms entries (0..MaxRooms-1).</summary>
        public static byte[] EncodeRoomReport(int requestId, string serverId, RoomStateReport[] rooms, int count)
        {
            var buf = new byte[1 + 4 + Str(serverId) + 4 + count * RoomReportEntryBytes];
            var w = new SpanWriter(buf);
            w.WriteByte(RoomReport); w.WriteInt32(requestId);
            w.WriteString(serverId); w.WriteInt32(count);
            for (int i = 0; i < count; i++)
            {
                w.WriteInt32(rooms[i].RoomId); w.WriteByte(rooms[i].State); w.WriteInt32(rooms[i].PlayerCount);
            }
            return buf;
        }

        public struct RoomReportMsg { public int RequestId; public string ServerId; public RoomStateReport[] Rooms; public int RoomCount; }
        /// <param name="maxRooms">Sanity cap on the room count — bounds-checked BEFORE the *9 multiply (so a
        /// forged huge count can't overflow into a bypass). The remaining-buffer check is the real OOB guard.</param>
        public static bool TryDecodeRoomReport(byte[] data, int length, int maxRooms, out RoomReportMsg m)
        {
            m = default;
            try
            {
                var r = new SpanReader(data, 0, length);
                if (r.ReadByte() != RoomReport) return false;
                int requestId = r.ReadInt32();
                string serverId = r.ReadString();
                int roomCount = r.ReadInt32();
                if (roomCount < 0 || roomCount > maxRooms) return false;                 // sane cap (prevents *9 overflow)
                if (r.Remaining < roomCount * RoomReportEntryBytes) return false;         // real OOB guard
                var rooms = new RoomStateReport[roomCount];
                for (int i = 0; i < roomCount; i++)
                {
                    rooms[i].RoomId = r.ReadInt32();
                    rooms[i].State = r.ReadByte();
                    rooms[i].PlayerCount = r.ReadInt32();
                }
                m.RequestId = requestId; m.ServerId = serverId; m.Rooms = rooms; m.RoomCount = roomCount;
                return true;
            }
            catch { m = default; return false; } // malformed wire → reject (never throw on the poll thread)
        }

        // ── ReservePush (lobby → dedi) ──────────────────────────────────────────
        // Reserves a room's match config so the dedi's IMatchConfigSource can resolve it at CreateRoomAt,
        // before the client (holding the lobby ticket) connects. matchConfigData is opaque (game-owned),
        // length-prefixed like RedeemResponse.entitlement.
        //
        // matchInstanceId identifies this MATCH INSTANCE, not the rendezvous matchId the clients share: the dedi
        // keeps it as the result's idempotency key, which must be unique per match (a rendezvous key repeats
        // across matches). Format is `{rendezvousMatchId}#{token}`, or the bare rendezvous key when the lobby's
        // matchIds are already unique — recover the rendezvous key with LastIndexOf('#'); no '#' means the whole
        // string is it.
        public static byte[] EncodeReservePush(int requestId, int roomId, string matchInstanceId, int stageId,
                                               byte[] matchConfigData, long reservationExpiresAt)
        {
            int mcdLen = matchConfigData?.Length ?? 0;
            var buf = new byte[1 + 4 + 4 + Str(matchInstanceId) + 4 + (4 + mcdLen) + 8];
            var w = new SpanWriter(buf);
            w.WriteByte(ReservePush); w.WriteInt32(requestId);
            w.WriteInt32(roomId); w.WriteString(matchInstanceId); w.WriteInt32(stageId);
            w.WriteBytes(matchConfigData); // null → length 0
            w.WriteInt64(reservationExpiresAt);
            return buf;
        }

        public struct ReservePushMsg
        {
            public int RequestId; public int RoomId; public string MatchInstanceId; public int StageId;
            public byte[] MatchConfigData; public long ReservationExpiresAt;
        }
        public static bool TryDecodeReservePush(byte[] data, int length, out ReservePushMsg m)
        {
            m = default;
            try
            {
                var r = new SpanReader(data, 0, length);
                if (r.ReadByte() != ReservePush) return false;
                m.RequestId = r.ReadInt32();
                m.RoomId = r.ReadInt32(); m.MatchInstanceId = r.ReadString(); m.StageId = r.ReadInt32();
                var span = r.ReadBytes();
                if (span.Length > 0) m.MatchConfigData = span.ToArray();
                m.ReservationExpiresAt = r.ReadInt64();
                return true;
            }
            catch { m = default; return false; } // malformed wire → reject (never throw on the poll thread)
        }

        // ── ReserveAck (dedi → lobby) ───────────────────────────────────────────
        public static byte[] EncodeReserveAck(int requestId, bool ok, byte nakReason)
        {
            var buf = new byte[1 + 4 + 1 + 1];
            var w = new SpanWriter(buf);
            w.WriteByte(ReserveAck); w.WriteInt32(requestId);
            w.WriteBool(ok); w.WriteByte(nakReason);
            return buf;
        }

        public struct ReserveAckMsg { public int RequestId; public bool Ok; public byte NakReason; }
        public static bool TryDecodeReserveAck(byte[] data, int length, out ReserveAckMsg m)
        {
            m = default;
            try
            {
                var r = new SpanReader(data, 0, length);
                if (r.ReadByte() != ReserveAck) return false;
                m.RequestId = r.ReadInt32();
                m.Ok = r.ReadBool(); m.NakReason = r.ReadByte();
                return true;
            }
            catch { m = default; return false; } // malformed wire → reject (never throw on the poll thread)
        }

        // ── MatchResult (dedi → lobby) ──────────────────────────────────────────
        // A verified match result (or abort notification). The identity roster rides OUTSIDE the opaque
        // game-owned payload (blob = pure deterministic stats keyed by PlayerId; roster = per-player
        // Account/DisplayName). payload is length-prefixed (null → len 0), like RedeemResponse.entitlement.
        private const int MatchResultRosterEntryMinBytes = 4 + 4 + 4; // playerId + account(len0) + displayName(len0)

        // matchInstanceId (NOT the rendezvous matchId — see EncodeReservePush) is the result's idempotency key.
        public static byte[] EncodeMatchResult(int requestId, string serverId, int roomId, string matchInstanceId,
                                               int stageId, byte terminationKind,
                                               MatchResultRosterEntry[] roster, int rosterCount, byte[] payload)
        {
            int size = 1 + 4                       // kind + requestId
                     + Str(serverId) + 4           // serverId + roomId
                     + Str(matchInstanceId) + 4    // matchInstanceId + stageId
                     + 1                            // terminationKind
                     + 4;                           // rosterCount
            for (int i = 0; i < rosterCount; i++)
                size += 4 + Str(roster[i].Account) + Str(roster[i].DisplayName); // playerId + account + displayName
            int payLen = payload?.Length ?? 0;
            size += 4 + payLen;                     // payload (length-prefixed)

            var buf = new byte[size];
            var w = new SpanWriter(buf);
            w.WriteByte(MatchResult); w.WriteInt32(requestId);
            w.WriteString(serverId); w.WriteInt32(roomId);
            w.WriteString(matchInstanceId); w.WriteInt32(stageId);
            w.WriteByte(terminationKind);
            w.WriteInt32(rosterCount);
            for (int i = 0; i < rosterCount; i++)
            {
                w.WriteInt32(roster[i].PlayerId);
                w.WriteString(roster[i].Account ?? string.Empty);
                w.WriteString(roster[i].DisplayName ?? string.Empty);
            }
            w.WriteBytes(payload); // null → length 0
            return buf;
        }

        public struct MatchResultMsg
        {
            public int RequestId; public string ServerId; public int RoomId; public string MatchInstanceId; public int StageId;
            public byte TerminationKind; public MatchResultRosterEntry[] Roster; public int RosterCount; public byte[] Payload;
        }
        /// <param name="maxRoster">Sanity cap on the roster count — bounds-checked BEFORE the *entrySize
        /// multiply (so a forged huge count can't overflow into a bypass). The remaining-buffer check is the
        /// real OOB guard; ReadString past the buffer throws and is caught → reject.</param>
        public static bool TryDecodeMatchResult(byte[] data, int length, int maxRoster, out MatchResultMsg m)
        {
            m = default;
            try
            {
                var r = new SpanReader(data, 0, length);
                if (r.ReadByte() != MatchResult) return false;
                m.RequestId = r.ReadInt32();
                m.ServerId = r.ReadString(); m.RoomId = r.ReadInt32();
                m.MatchInstanceId = r.ReadString(); m.StageId = r.ReadInt32();
                m.TerminationKind = r.ReadByte();
                int rosterCount = r.ReadInt32();
                if (rosterCount < 0 || rosterCount > maxRoster) return false;                    // sane cap (prevents *entry overflow)
                if (r.Remaining < rosterCount * MatchResultRosterEntryMinBytes) return false;     // real OOB guard (min entry size)
                var roster = new MatchResultRosterEntry[rosterCount];
                for (int i = 0; i < rosterCount; i++)
                {
                    roster[i].PlayerId = r.ReadInt32();
                    roster[i].Account = r.ReadString();
                    roster[i].DisplayName = r.ReadString();
                }
                var span = r.ReadBytes();
                if (span.Length > 0) m.Payload = span.ToArray();
                m.ServerId ??= string.Empty;
                m.Roster = roster; m.RosterCount = rosterCount;
                return true;
            }
            catch { m = default; return false; } // malformed wire → reject (never throw on the poll thread)
        }

        // ── MatchResultAck (lobby → dedi) ───────────────────────────────────────
        public static byte[] EncodeMatchResultAck(int requestId, bool ok)
        {
            var buf = new byte[1 + 4 + 1];
            var w = new SpanWriter(buf);
            w.WriteByte(MatchResultAck); w.WriteInt32(requestId); w.WriteBool(ok);
            return buf;
        }

        public struct MatchResultAckMsg { public int RequestId; public bool Ok; }
        public static bool TryDecodeMatchResultAck(byte[] data, int length, out MatchResultAckMsg m)
        {
            m = default;
            try
            {
                var r = new SpanReader(data, 0, length);
                if (r.ReadByte() != MatchResultAck) return false;
                m.RequestId = r.ReadInt32(); m.Ok = r.ReadBool();
                return true;
            }
            catch { m = default; return false; } // malformed wire → reject (never throw on the poll thread)
        }

        // Abort notification blob: the MatchResult payload when terminationKind == Aborted. Opaque to
        // the wire envelope; the SD reporter (producer) and the reference lobby (consumer) share this codec.
        // abortReason is a wire AbortReason* value (NOT a raw core enum cast — see SdRoomReporter.TryMapAbortReason).
        //
        // ⚠️ culpritPlayerId = -1 carries TWO distinct meanings, told apart ONLY by abortReason:
        //   • StateDivergence(2) → "unknown who" — OnMatchAborted(reason) exposes no culprit; a real culprit is
        //     resolvable later via the roster side-channel by id.
        //   • Abandoned(10)      → "no SINGLE culprit" — everyone left; the responsible party is the WHOLE roster.
        // A backend that treats -1 uniformly across reasons is wrong. For Abandoned, the honest reading is
        // "culprit list = every roster entry", not "no one responsible".
        public static byte[] EncodeAbortNotification(byte abortReason, int culpritPlayerId)
        {
            var buf = new byte[1 + 4];
            var w = new SpanWriter(buf);
            w.WriteByte(abortReason); w.WriteInt32(culpritPlayerId);
            return buf;
        }

        public struct AbortNotificationMsg { public byte AbortReason; public int CulpritPlayerId; }
        public static bool TryDecodeAbortNotification(byte[] data, out AbortNotificationMsg m)
        {
            m = default;
            if (data == null) return false;
            try
            {
                var r = new SpanReader(data, 0, data.Length);
                m.AbortReason = r.ReadByte(); m.CulpritPlayerId = r.ReadInt32();
                return true;
            }
            catch { m = default; return false; }
        }

        /// <summary>Reads the leading message-kind byte (0 if empty) for dispatch.</summary>
        public static byte PeekKind(byte[] data, int length) => length >= 1 ? data[0] : (byte)0;
    }
}
