namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Symbolic names for ReconnectRejectMessage.Reason byte values.
    /// Keep in sync with the reason switch in
    /// KlothoNetworkService.HandleReconnectReject / ServerDrivenClientService.HandleReconnectReject.
    ///
    /// Backwards-compat policy: append new reasons at the end. Never change an existing byte value
    /// (wire format with older clients would break).
    /// </summary>
    public static class ReconnectRejectReason
    {
        public const byte InvalidMagic     = 1;
        public const byte InvalidPlayer    = 2;
        /// <summary>
        /// Timeout — raised by server reject (disconnect elapsed &gt; ReconnectTimeoutMs) OR by
        /// client-side elapsed-since-attempt-start exceeding the same threshold. Same user-visible
        /// semantics; caller distinction goes into the log message.
        /// </summary>
        public const byte TimedOut         = 3;
        public const byte AlreadyConnected = 4;
        public const byte DeviceMismatch   = 5;
        // ── Client-only reasons — not sent over wire (local OnReconnectFailed event raise only) ──
        public const byte TransportStartFailed = 6;   // transport reinit failed during reconnect
        public const byte MaxRetries           = 7;   // exhausted ReconnectMaxRetries
        public const byte Unknown              = 8;   // generic / unspecified failure

        /// <summary>
        /// Returns the symbolic name for a reason byte (e.g. "InvalidMagic"). Game/UI layers should
        /// localize these via their own string table; the values here are stable identifiers.
        /// </summary>
        public static string ToName(byte reason)
        {
            switch (reason)
            {
                case InvalidMagic:         return "InvalidMagic";
                case InvalidPlayer:        return "InvalidPlayer";
                case TimedOut:             return "TimedOut";
                case AlreadyConnected:     return "AlreadyConnected";
                case DeviceMismatch:       return "DeviceMismatch";
                case TransportStartFailed: return "TransportStartFailed";
                case MaxRetries:           return "MaxRetries";
                case Unknown:              return "Unknown";
                default:                   return "Unknown";
            }
        }

        /// <summary>
        /// Whether this reason indicates the same PlayerId is already held by another peer/device
        /// — game layer should offer a user choice (fall back to fresh join, or quit).
        /// </summary>
        public static bool RequiresUserChoice(byte reason)
        {
            return reason == AlreadyConnected;
        }
    }
}
