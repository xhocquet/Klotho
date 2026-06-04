namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Reason for <see cref="xpTURN.Klotho.Core.JoinFailedException"/>,
    /// raised on the guest Connect path (normal / late join). Game catch handlers should branch on
    /// Reason instead of parsing Message. Cancellation surfaces as OperationCanceledException, not a Reason.
    /// The underlying type is byte so the value can ride the wire via a cast; this enum itself is client-local.
    /// </summary>
    public enum JoinFailReason : byte
    {
        /// <summary>Local transport socket failed to start (e.g. port bind). Does not cover an unreachable host.</summary>
        TransportStartFailed = 1,
        /// <summary>Handshake did not complete within the connect timeout.</summary>
        TimedOut             = 2,
        /// <summary>Connection lost during handshake from a network failure (host down, unreachable, socket error).</summary>
        ConnectionLost       = 3,
        /// <summary>Connection rejected at the transport level (wrong connection key / protocol mismatch).</summary>
        Rejected             = 4,
        /// <summary>Host closed the connection during handshake.</summary>
        HostClosed           = 5,
        /// <summary>Other / unspecified failure.</summary>
        Unknown              = 6,

        // Server application-level join rejections, delivered via the disconnect packet payload.
        /// <summary>Server rejected: the requested room does not exist.</summary>
        RoomNotFound         = 7,
        /// <summary>Server rejected: the room is at player capacity.</summary>
        RoomFull             = 8,
        /// <summary>Server rejected: the server is at capacity.</summary>
        ServerFull           = 9,
        /// <summary>Server rejected: late join is not allowed for this room.</summary>
        LateJoinDisabled     = 10,
        /// <summary>Server rejected: the room is ending or not active.</summary>
        RoomClosing          = 11,
    }

    public static class JoinFailReasonExtensions
    {
        /// <summary>
        /// Maps an involuntary handshake disconnect reason to a JoinFailReason.
        /// LocalDisconnect is handled separately (not a failure) and never reaches here.
        /// </summary>
        public static JoinFailReason FromDisconnect(DisconnectReason reason)
        {
            switch (reason)
            {
                case DisconnectReason.NetworkFailure:     return JoinFailReason.ConnectionLost;
                case DisconnectReason.ConnectionRejected: return JoinFailReason.Rejected;
                case DisconnectReason.RemoteDisconnect:   return JoinFailReason.HostClosed;
                default:                                  return JoinFailReason.Unknown;
            }
        }

        /// <summary>
        /// Maps a server reject reason byte (carried on the disconnect packet payload) to a JoinFailReason.
        /// The wire reason codes are defined by the server's reject path and differ from these values.
        /// </summary>
        public static JoinFailReason FromJoinReject(byte wireReason)
        {
            switch (wireReason)
            {
                case 1:  return JoinFailReason.RoomNotFound;
                case 2:  return JoinFailReason.RoomFull;
                case 3:  return JoinFailReason.ServerFull;
                case 4:  return JoinFailReason.LateJoinDisabled;
                case 5:  return JoinFailReason.RoomClosing;
                default: return JoinFailReason.Unknown;   // 0 and any unmapped value
            }
        }

        /// <summary>
        /// Returns the symbolic name for a reason (e.g. "TimedOut"). Game/UI layers should
        /// localize these via their own string table; the values here are stable identifiers.
        /// </summary>
        public static string ToName(this JoinFailReason reason)
        {
            switch (reason)
            {
                case JoinFailReason.TransportStartFailed: return "TransportStartFailed";
                case JoinFailReason.TimedOut:             return "TimedOut";
                case JoinFailReason.ConnectionLost:       return "ConnectionLost";
                case JoinFailReason.Rejected:             return "Rejected";
                case JoinFailReason.HostClosed:           return "HostClosed";
                case JoinFailReason.Unknown:              return "Unknown";
                case JoinFailReason.RoomNotFound:         return "RoomNotFound";
                case JoinFailReason.RoomFull:             return "RoomFull";
                case JoinFailReason.ServerFull:           return "ServerFull";
                case JoinFailReason.LateJoinDisabled:     return "LateJoinDisabled";
                case JoinFailReason.RoomClosing:          return "RoomClosing";
                default:                                  return "Unknown";
            }
        }

        /// <summary>
        /// Default English user-facing message for a reason. Games needing localization should
        /// write their own switch; this is a one-line fallback. Always returns non-null.
        /// </summary>
        public static string ToDefaultMessage(this JoinFailReason reason)
        {
            switch (reason)
            {
                case JoinFailReason.TransportStartFailed: return "Network unavailable";
                case JoinFailReason.TimedOut:             return "Connection timed out";
                case JoinFailReason.ConnectionLost:       return "Could not reach the host";
                case JoinFailReason.Rejected:             return "Connection rejected";
                case JoinFailReason.HostClosed:           return "Host closed the connection";
                case JoinFailReason.Unknown:              return "Join failed";
                case JoinFailReason.RoomNotFound:         return "Room not found";
                case JoinFailReason.RoomFull:             return "Room is full";
                case JoinFailReason.ServerFull:           return "Server is full";
                case JoinFailReason.LateJoinDisabled:     return "Join is no longer allowed";
                case JoinFailReason.RoomClosing:          return "Room is closing";
                default:                                  return "Join failed";
            }
        }
    }
}
