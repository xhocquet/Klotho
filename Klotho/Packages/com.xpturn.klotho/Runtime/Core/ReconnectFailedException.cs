using System;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Thrown by KlothoConnection / KlothoConnectionAsync paths when a cold-start Reconnect attempt
    /// is rejected by the server. The Reason byte is one of <see cref="xpTURN.Klotho.Network.ReconnectRejectReason"/>
    /// constants. Game catch handlers should branch on Reason instead of parsing Message.
    /// </summary>
    public sealed class ReconnectFailedException : Exception
    {
        public byte Reason { get; }

        public ReconnectFailedException(byte reason)
            : base($"Reconnect rejected: {Network.ReconnectRejectReason.ToName(reason)}")
        {
            Reason = reason;
        }
    }
}
