namespace xpTURN.Klotho.Samples.Identity
{
    /// <summary>
    /// Dev/test lobby stand-in. **This represents the lobby**: it holds the
    /// Ed25519 private key and signs tickets. In production the private key lives ONLY on the lobby
    /// server, never in a game client — DO NOT ship this (or the key) in a release client. It is kept
    /// structurally separate from <see cref="DevIdentityProvider"/> (which only carries a ticket) so the
    /// reference never models the "client signs its own ticket" anti-pattern.
    /// <para>
    /// Test determinism: the caller supplies <c>issuedAt</c>/<c>expiresAt</c>/<c>nonce</c> via the
    /// <see cref="LobbyTicket"/> it passes in; production self-mints with a wall clock + RNG nonce.
    /// </para>
    /// </summary>
    public sealed class DevLobbyTicketIssuer
    {
        private readonly IEd25519Backend _backend;
        private readonly byte[] _privateKey;

        public DevLobbyTicketIssuer(IEd25519Backend backend, byte[] privateKey)
        {
            _backend = backend;
            _privateKey = privateKey;
        }

        /// <summary>Encodes (canonical JSON) + signs a ticket → wire string.</summary>
        public string Issue(in LobbyTicket ticket)
        {
            byte[] payload = LobbyTicketCodec.EncodePayload(ticket);
            return SignWire(payload);
        }

        /// <summary>
        /// Signs arbitrary payload bytes (test seam) → wire string. Lets tests forge foreign-format but
        /// validly-signed tickets (reordered/whitespaced JSON, unknown extra fields, malformed payloads)
        /// to exercise verify-over-wire and parser robustness — these forged-but-valid tickets are the
        /// only way to catch a validator that re-serializes the payload instead of verifying the exact
        /// wire bytes (a bug a same-issuer round-trip test would otherwise mask).
        /// </summary>
        public string IssueRaw(byte[] payloadBytes)
        {
            return SignWire(payloadBytes);
        }

        private string SignWire(byte[] payload)
        {
            byte[] sig = _backend.Sign(_privateKey, payload);
            return LobbyTicketCodec.Base64UrlEncode(payload) + "." + LobbyTicketCodec.Base64UrlEncode(sig);
        }
    }
}
