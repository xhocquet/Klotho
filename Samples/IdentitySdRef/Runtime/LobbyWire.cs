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

        // RoomReport room-state byte = CORE RoomState integer values (NOT the lobby's own RoomState, which
        // differs: lobby Reserved=1 vs core Active=1). The lobby MUST MAP these to its reconcile logic, never
        // cast to LobbyRoomRegistry.RoomState. Pinned here so a core enum reorder is caught at this seam.
        public const byte RoomStateEmpty     = 0;
        public const byte RoomStateActive    = 1;
        public const byte RoomStateDraining  = 2;
        public const byte RoomStateDisposing = 3;

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

        /// <summary>Reads the leading message-kind byte (0 if empty) for dispatch.</summary>
        public static byte PeekKind(byte[] data, int length) => length >= 1 ? data[0] : (byte)0;
    }
}
