using System;
using System.Threading;
using System.Threading.Tasks;

namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>
    /// Game-layer seam for the online lobby "redeem" call (SD). The SD validator delegates the
    /// authoritative check (nonce consume, ban/sanction, match↔server binding) to the lobby through this
    /// interface, so the transport is the game's choice and never baked into the validator. Keeping this
    /// abstraction is what lets the automated tests inject an in-proc fake while the sample drives a real
    /// LiteNetLib round-trip (a live lobby process cannot run in the headless test runner).
    /// <para>
    /// Implementations:
    ///   - <see cref="InProcLobbyRedeemClient"/> (tests / no-process demo) — wraps <see cref="DevLobbyCore"/>.
    ///   - LiteNetLibLobbyRedeemClient (sample) — round-trips to an external DevLobbyServer process.
    /// </para>
    /// Thread-safety: the SD validator is server-global, so concurrent calls from multiple room workers
    /// are expected; implementations must be safe under concurrent <see cref="RedeemAsync"/>.
    /// </summary>
    public interface ILobbyRedeemClient
    {
        /// <summary>
        /// Redeems <paramref name="ticketWire"/> against the lobby. <paramref name="sessionId"/> is the
        /// ticket's lobby match id (the lobby treats the ticket's own SessionId as authoritative — this is
        /// a cross-check hint), <paramref name="serverId"/> identifies this dedicated server (the lobby
        /// verifies the match is assigned to it), <paramref name="roomId"/> is the room the peer was routed
        /// to (the lobby cross-checks it against the ticket's bound (server, room); -1 when not room-scoped).
        /// Honors <paramref name="ct"/> (Dispose/timeout cancels). Must not throw across the returned Task in
        /// normal operation — transport failures surface as a faulted/canceled Task, which the validator maps
        /// to a fail-closed reject (11).
        /// </summary>
        Task<RedeemResult> RedeemAsync(string ticketWire, string sessionId, string serverId, int roomId, CancellationToken ct);
    }

    /// <summary>
    /// Result of a lobby redeem. On accept, carries the authoritative account / display name (these win
    /// over the ticket payload — the lobby reflects renames / sanctions). On reject, carries a wire reason
    /// code; the validator clamps it into the identity range 6~11 before forwarding.
    /// </summary>
    public readonly struct RedeemResult
    {
        public readonly bool Ok;
        public readonly string Account;
        public readonly string DisplayName;
        public readonly byte RejectWireCode;
        /// <summary>Opaque authoritative entitlement blob on accept (null when none). The lobby is the
        /// authority — this rides alongside the account/displayName in the same redeem snapshot.</summary>
        public readonly byte[] Entitlement;

        private RedeemResult(bool ok, string account, string displayName, byte rejectWireCode, byte[] entitlement)
        {
            Ok = ok;
            Account = account ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            RejectWireCode = rejectWireCode;
            Entitlement = entitlement;
        }

        public static RedeemResult Accept(string account, string displayName)
            => new RedeemResult(true, account, displayName, 0, null);

        public static RedeemResult Accept(string account, string displayName, byte[] entitlement)
            => new RedeemResult(true, account, displayName, 0, entitlement);

        public static RedeemResult Reject(byte rejectWireCode)
            => new RedeemResult(false, string.Empty, string.Empty, rejectWireCode, null);
    }
}
