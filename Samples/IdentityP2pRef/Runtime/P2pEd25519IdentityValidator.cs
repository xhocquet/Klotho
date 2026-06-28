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
    /// <see cref="BeginValidate"/> is single-threaded (P2P synchronous), so the plain HashSet is safe.
    /// </para>
    /// </summary>
    public sealed class P2pEd25519IdentityValidator : IPlayerIdentityValidator
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
        private readonly HashSet<string> _seenNonces = new HashSet<string>(StringComparer.Ordinal);

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

            if (p.ExpiresAt <= _nowUnixMs())
                return IdentityValidationOutcome.Reject(WireIdentityExpired);

            // Account bound: an empty account collides with the no-lobby sentinel; >62 UTF-8 B would
            // truncate (identity-key collision on guests). displayName over-length is tolerated
            // (truncation is the core ToFixedName concern, display-only).
            if (string.IsNullOrEmpty(p.Account) || Encoding.UTF8.GetByteCount(p.Account) > MaxAccountUtf8)
                return IdentityValidationOutcome.Reject(WireIdentityInvalid);

            // Session-scoped replay check — LAST, so a reject above never consumes a nonce.
            if (!_seenNonces.Add(p.Nonce))
                return IdentityValidationOutcome.Reject(WireIdentityRejected);

            return IdentityValidationOutcome.Accept(p.Account, p.DisplayName);
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
