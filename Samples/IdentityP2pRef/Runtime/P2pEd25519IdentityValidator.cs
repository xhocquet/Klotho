using System;
using System.Collections.Generic;
using System.Text;

using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Samples.Identity
{
    /// <summary>
    /// P2P offline Ed25519 ticket validator — host-side reference for the semi-trust model (only the host
    /// verifies; guests trust the host's verdict). Synchronous: <see cref="BeginValidate"/> returns an
    /// already-complete handle. Validation order: verify the signature over the wire bytes → bind sessionId
    /// → check expiry → bound the account field → consume a session-scoped nonce. Reject wire codes 6~11
    /// (<see cref="JoinFailReason"/>).
    /// <para>
    /// Lifecycle: construct ONE instance per host session (not a reused singleton) — the
    /// nonce set and expectedSessionId are session state; eviction = drop the instance at session end.
    /// <see cref="BeginValidate"/> is single-threaded (P2P synchronous), so the plain Dictionary is safe.
    /// </para>
    /// </summary>
    public sealed class P2pEd25519IdentityValidator : IPlayerIdentityValidator, IPropagatedTicketVerifier
    {
        // Disconnect-payload wire reason codes (6~11). Distinct from JoinFailReason enum values (client-local).
        private const byte WireIdentityInvalid         = 6;
        private const byte WireIdentityExpired         = 7;
        private const byte WireIdentitySessionMismatch = 8;
        private const byte WireIdentityRejected        = 9;
        private const byte WireIdentityRequired        = 10;

        // RosterEntry.DisplayName/Account is FixedString64 (62 UTF-8 bytes). Account truncation collides
        // identities on guests → reject over-long/empty account; displayName truncation is tolerated.
        private const int MaxAccountUtf8 = 62;
        private const int Ed25519SignatureLength = 64;

        private readonly byte[] _lobbyPublicKey;
        private readonly string _expectedSessionId;
        private readonly Func<long> _nowUnixMs;
        private readonly IEd25519Backend _backend;
        // Session-scoped replay guard: nonce → ticket expiresAt. An expired ticket is already rejected by the
        // expiry gate before the replay check, so its nonce can be forgotten — PruneExpiredNonces drops such
        // entries (gated by a soft cap) to bound the set over a long-lived host session.
        private readonly Dictionary<string, long> _seenNonces = new Dictionary<string, long>(StringComparer.Ordinal);
        private const int NoncePruneThreshold = 256;
        private readonly List<string> _expiredNonceScratch = new List<string>();

        /// <param name="lobbyPublicKey">Lobby Ed25519 public key (32 B), embedded in the sample.</param>
        /// <param name="expectedSessionId">Lobby match id this session is bound to (the sample uses a build-time dev constant).</param>
        /// <param name="nowUnixMs">Injectable clock — tests inject a fixed value; runtime = wall clock.</param>
        /// <param name="backend">Ed25519 sign/verify seam.</param>
        public P2pEd25519IdentityValidator(byte[] lobbyPublicKey, string expectedSessionId,
                                           Func<long> nowUnixMs, IEd25519Backend backend)
        {
            _lobbyPublicKey = lobbyPublicKey ?? throw new ArgumentNullException(nameof(lobbyPublicKey));
            _expectedSessionId = expectedSessionId ?? string.Empty;
            _nowUnixMs = nowUnixMs ?? throw new ArgumentNullException(nameof(nowUnixMs));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public IIdentityValidation BeginValidate(in IdentityValidationRequest request)
        {
            // IsHostSelf is NOT branched here — the validator returns one uniform verdict; the core
            // downgrades a host-self reject to the "Host" fallback (the host cannot kick itself).
            return new CompletedValidation(Evaluate(request.Ticket));
        }

        private IdentityValidationOutcome Evaluate(string ticket)
        {
            if (string.IsNullOrEmpty(ticket))
                return IdentityValidationOutcome.Reject(WireIdentityRequired); // this reference rejects an absent ticket

            byte[] payload;
            byte[] signature;
            try
            {
                if (!LobbyTicketCodec.TrySplitWire(ticket, out string payloadSeg, out string sigSeg))
                    return IdentityValidationOutcome.Reject(WireIdentityInvalid);
                payload = LobbyTicketCodec.Base64UrlDecode(payloadSeg);
                signature = LobbyTicketCodec.Base64UrlDecode(sigSeg);
            }
            catch
            {
                return IdentityValidationOutcome.Reject(WireIdentityInvalid); // malformed wire / base64
            }

            if (signature.Length != Ed25519SignatureLength)
                return IdentityValidationOutcome.Reject(WireIdentityInvalid);

            // verify-over-wire: verify over the exact decoded payload bytes; NEVER re-serialize.
            bool verified;
            try
            {
                verified = _backend.Verify(_lobbyPublicKey, payload, signature);
            }
            catch
            {
                return IdentityValidationOutcome.Reject(WireIdentityInvalid);
            }
            if (!verified)
                return IdentityValidationOutcome.Reject(WireIdentityInvalid);

            LobbyTicket p;
            try
            {
                p = LobbyTicketCodec.ParsePayload(payload); // parse ONLY after signature passes
            }
            catch
            {
                return IdentityValidationOutcome.Reject(WireIdentityInvalid); // signed-but-malformed payload
            }

            if (!string.Equals(p.SessionId, _expectedSessionId, StringComparison.Ordinal))
                return IdentityValidationOutcome.Reject(WireIdentitySessionMismatch);

            long now = _nowUnixMs();
            if (p.ExpiresAt <= now)
                return IdentityValidationOutcome.Reject(WireIdentityExpired);

            // Account bound: an empty account collides with the no-lobby sentinel; >62 UTF-8 B would
            // truncate (identity-key collision on guests). displayName over-length is tolerated
            // (truncation is the core ToFixedName concern, display-only).
            if (string.IsNullOrEmpty(p.Account) || Encoding.UTF8.GetByteCount(p.Account) > MaxAccountUtf8)
                return IdentityValidationOutcome.Reject(WireIdentityInvalid);

            // Session-scoped replay check — LAST, so a reject above never consumes a nonce. Prune expired
            // entries first (their tickets fail the expiry gate regardless) to keep the set bounded.
            PruneExpiredNonces(now);
            if (_seenNonces.ContainsKey(p.Nonce))
                return IdentityValidationOutcome.Reject(WireIdentityRejected);
            _seenNonces[p.Nonce] = p.ExpiresAt;

            // Carry the signed entitlement, which is covered by the signature verified above.
            return IdentityValidationOutcome.Accept(p.Account, p.DisplayName, p.Entitlement);
        }

        /// <summary>
        /// Guest-side signature-only re-verification of a propagated original
        /// ticket. Verifies ONLY the Ed25519 signature over the wire bytes + the account bound; it does
        /// NOT re-check sessionId / expiry / nonce — the host already gated those at join, the same ticket
        /// is received more than once (roster snapshot + notifications + reconnect re-send), and the
        /// signature is valid forever. Re-running the full join-time Evaluate would false-reject a valid
        /// member on the second receipt (nonce spent), on a long match (expired), or when this peer lacks
        /// the session id. On success returns the ticket's own Account/DisplayName (the value the guest
        /// adopts, ignoring the host-relayed roster); the reject wire code on failure is unused by the
        /// caller (it consumes only Accepted).
        /// </summary>
        public IdentityValidationOutcome ReverifyPropagatedTicket(string ticket)
        {
            if (string.IsNullOrEmpty(ticket))
                return IdentityValidationOutcome.Reject(WireIdentityInvalid);

            byte[] payload;
            byte[] signature;
            try
            {
                if (!LobbyTicketCodec.TrySplitWire(ticket, out string payloadSeg, out string sigSeg))
                    return IdentityValidationOutcome.Reject(WireIdentityInvalid);
                payload = LobbyTicketCodec.Base64UrlDecode(payloadSeg);
                signature = LobbyTicketCodec.Base64UrlDecode(sigSeg);
            }
            catch
            {
                return IdentityValidationOutcome.Reject(WireIdentityInvalid);
            }

            if (signature.Length != Ed25519SignatureLength)
                return IdentityValidationOutcome.Reject(WireIdentityInvalid);

            // verify-over-wire: verify over the exact decoded payload bytes; NEVER re-serialize.
            bool verified;
            try
            {
                verified = _backend.Verify(_lobbyPublicKey, payload, signature);
            }
            catch
            {
                return IdentityValidationOutcome.Reject(WireIdentityInvalid);
            }
            if (!verified)
                return IdentityValidationOutcome.Reject(WireIdentityInvalid);

            LobbyTicket p;
            try
            {
                p = LobbyTicketCodec.ParsePayload(payload); // parse ONLY after signature passes
            }
            catch
            {
                return IdentityValidationOutcome.Reject(WireIdentityInvalid);
            }

            // Account bound only (identity-key collision guard) — NO sessionId / expiry / nonce (G3).
            if (string.IsNullOrEmpty(p.Account) || Encoding.UTF8.GetByteCount(p.Account) > MaxAccountUtf8)
                return IdentityValidationOutcome.Reject(WireIdentityInvalid);

            // Extract the entitlement from the signed payload (the signature covers it). Expiry, nonce, and
            // sessionId stay skipped here — those gates do not apply to entitlement extraction. Because this
            // path depends only on the signature, every peer derives the same bytes from the same ticket, so
            // a player's entitlement-derived seed is identical on the host and on every guest.
            return IdentityValidationOutcome.Accept(p.Account, p.DisplayName, p.Entitlement);
        }

        // Drops nonces whose ticket has already expired (those tickets fail the expiry gate regardless).
        // Gated by a soft cap so the common path stays allocation-free; the scratch list is reused.
        private void PruneExpiredNonces(long now)
        {
            if (_seenNonces.Count <= NoncePruneThreshold)
                return;
            _expiredNonceScratch.Clear();
            foreach (var kv in _seenNonces)
                if (kv.Value <= now)
                    _expiredNonceScratch.Add(kv.Key);
            for (int i = 0; i < _expiredNonceScratch.Count; i++)
                _seenNonces.Remove(_expiredNonceScratch[i]);
        }

        /// <summary>Synchronous (P2P) handle — already complete; Dispose is a no-op (no in-flight work).</summary>
        private sealed class CompletedValidation : IIdentityValidation
        {
            public CompletedValidation(IdentityValidationOutcome outcome) { Outcome = outcome; }
            public bool IsComplete => true;
            public IdentityValidationOutcome Outcome { get; }
            public void Dispose() { }
        }
    }
}
