using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using xpTURN.Klotho.Network;          // IPlayerIdentityValidator, IIdentityValidation, IdentityValidationRequest/Outcome
using xpTURN.Klotho.Samples.Identity; // p2pref: LobbyTicket, LobbyTicketCodec, IEd25519Backend

namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>
    /// Dedicated-server online-redeem ticket validator — server-side reference for the trusted-server model:
    /// the lobby redeem is the authority (one-time / idempotent nonce consume, real-time ban / sanction,
    /// match binding). Division of labour: a cheap LOCAL first pass (Ed25519 signature over the wire bytes +
    /// expiry, reusing the shared ticket codec / backend) rejects obvious forgeries before paying a redeem
    /// round-trip; the redeem then decides identity, nonce, and match authoritatively.
    /// <para>
    /// Asynchronous: an obvious local reject returns an already-complete handle; otherwise the redeem runs
    /// off the network-loop thread and the handle completes when the reply arrives (the redeem client backs
    /// it with a TaskCompletionSource) — no <c>Task.Run</c> and no blocking I/O on the network loop. One
    /// instance serves all rooms, so it (and the injected redeem client) must be safe under concurrent calls.
    /// </para>
    /// </summary>
    public sealed class SdRedeemIdentityValidator : IPlayerIdentityValidator
    {
        private readonly byte[] _lobbyPublicKey;
        private readonly IEd25519Backend _backend;
        private readonly ILobbyRedeemClient _redeem;
        private readonly string _serverId;
        private readonly Func<long> _nowUnixMs;
        private readonly int _localRedeemTimeoutMs;
        private readonly long _skewMarginMs;

        /// <param name="lobbyPublicKey">Lobby Ed25519 public key (32 B) for the local 1st-pass verify.</param>
        /// <param name="backend">Ed25519 verify seam (BouncyCastle in the sample).</param>
        /// <param name="redeem">Lobby redeem transport seam (LiteNetLib in the sample, in-proc fake in tests).</param>
        /// <param name="serverId">This dedicated server's id — the lobby checks the match is assigned to it.</param>
        /// <param name="nowUnixMs">Injectable clock for the local expiry first-pass (tests inject a fixed value).</param>
        /// <param name="localRedeemTimeoutMs">Validator-internal redeem deadline. Keep it below the core
        /// <c>SessionConfig.ValidationTimeoutMs</c> so the validator returns a meaningful reject code before
        /// the core's own backstop timeout fires.</param>
        /// <param name="skewMarginMs">Lenient margin added to the ticket expiry in the LOCAL check, to
        /// avoid false-rejecting a near-expiry ticket on server↔lobby clock skew; the redeem re-checks.</param>
        public SdRedeemIdentityValidator(byte[] lobbyPublicKey, IEd25519Backend backend,
                                         ILobbyRedeemClient redeem, string serverId,
                                         Func<long> nowUnixMs, int localRedeemTimeoutMs, long skewMarginMs = 0)
        {
            _lobbyPublicKey = lobbyPublicKey ?? throw new ArgumentNullException(nameof(lobbyPublicKey));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _redeem = redeem ?? throw new ArgumentNullException(nameof(redeem));
            _serverId = serverId ?? string.Empty;
            _nowUnixMs = nowUnixMs ?? throw new ArgumentNullException(nameof(nowUnixMs));
            _localRedeemTimeoutMs = localRedeemTimeoutMs;
            _skewMarginMs = skewMarginMs;
        }

        public IIdentityValidation BeginValidate(in IdentityValidationRequest request)
        {
            string ticket = request.Ticket;

            // ── local 1st pass (synchronous) → obvious rejects return an already-complete handle ──
            if (string.IsNullOrEmpty(ticket))
                return Completed(SdWireCodes.IdentityRequired); // this reference requires a ticket (SD server)

            byte[] payload, signature;
            LobbyTicket p;
            try
            {
                if (!LobbyTicketCodec.TrySplitWire(ticket, out string payloadSeg, out string sigSeg))
                    return Completed(SdWireCodes.IdentityInvalid);
                payload = LobbyTicketCodec.Base64UrlDecode(payloadSeg);
                signature = LobbyTicketCodec.Base64UrlDecode(sigSeg);
                if (signature.Length != SdWireCodes.Ed25519SignatureLength)
                    return Completed(SdWireCodes.IdentityInvalid);
                // Verify over the exact decoded wire bytes — never re-serialize the parsed payload.
                if (!_backend.Verify(_lobbyPublicKey, payload, signature))
                    return Completed(SdWireCodes.IdentityInvalid);
                p = LobbyTicketCodec.ParsePayload(payload); // parse ONLY after signature passes
            }
            catch
            {
                return Completed(SdWireCodes.IdentityInvalid); // malformed wire / base64 / signed-but-bad payload — never throw
            }

            // Local expiry first pass (lenient skew; the redeem re-checks authoritatively). Adding the margin
            // EXTENDS validity, so this only rejects clearly-expired tickets before a wasted round-trip.
            if (p.ExpiresAt + _skewMarginMs <= _nowUnixMs())
                return Completed(SdWireCodes.IdentityExpired);

            // ── redeem authority (async) → pending handle (completed via the redeem client's TaskCompletionSource) ──
            var handle = new SdRedeemIdentityValidation();
            // The ticket payload's account/displayName (p.Account/p.DisplayName) are intentionally NOT used
            // here — the redeem RESPONSE is the identity authority (so the lobby can reflect renames/bans).
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(handle.CancellationToken);
            linkedCts.CancelAfter(_localRedeemTimeoutMs);

            // request.RoomId (the routed room) is read synchronously here and passed by value — the redeem
            // continuation never captures `request` (the in-param must not be retained on a background thread).
            _redeem.RedeemAsync(ticket, p.SessionId, _serverId, request.RoomId, linkedCts.Token)
                .ContinueWith(t =>
                {
                    try
                    {
                        if (t.IsCanceled)
                            handle.SetResult(Reject(SdWireCodes.IdentityValidationFailed)); // Dispose / timeout
                        else if (t.IsFaulted)
                            handle.SetResult(Reject(SdWireCodes.IdentityValidationFailed)); // transport failure → fail closed
                        else
                        {
                            RedeemResult r = t.Result;
                            if (!r.Ok)
                                handle.SetResult(Reject(SdWireCodes.ClampIdentityCode(r.RejectWireCode)));
                            else if (BadAccountBound(r.Account))
                                handle.SetResult(Reject(SdWireCodes.IdentityInvalid)); // bound the redeem-returned account
                            else
                                handle.SetResult(IdentityValidationOutcome.Accept(r.Account, r.DisplayName));
                        }
                    }
                    finally
                    {
                        linkedCts.Dispose(); // release the timeout timer (safe regardless of handle Dispose ordering)
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default); // pin the default scheduler

            return handle;
        }

        // An empty account collides with the empty-string used when no lobby is present; an account over
        // 62 UTF-8 bytes would be truncated in the roster wire field, colliding distinct accounts. The
        // display name has no such constraint (its truncation is display-only).
        private static bool BadAccountBound(string account)
            => string.IsNullOrEmpty(account) || Encoding.UTF8.GetByteCount(account) > SdWireCodes.MaxAccountUtf8;

        private static IdentityValidationOutcome Reject(byte code) => IdentityValidationOutcome.Reject(code);

        /// <summary>Already-complete handle for synchronous rejects (no cancellation source / no Task;
        /// Dispose is a no-op). Distinct from <see cref="SdRedeemIdentityValidation"/> to avoid the async
        /// allocation on the fast-reject path.</summary>
        private static IIdentityValidation Completed(byte rejectCode)
            => new CompletedValidation(IdentityValidationOutcome.Reject(rejectCode));

        private sealed class CompletedValidation : IIdentityValidation
        {
            public CompletedValidation(IdentityValidationOutcome outcome) { Outcome = outcome; }
            public bool IsComplete => true;
            public IdentityValidationOutcome Outcome { get; }
            public void Dispose() { }
        }
    }
}
