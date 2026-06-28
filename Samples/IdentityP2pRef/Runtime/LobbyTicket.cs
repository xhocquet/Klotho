namespace xpTURN.Klotho.Samples.Identity
{
    /// <summary>
    /// Logical lobby ticket payload — the minimal field set this reference uses. Opaque to the Klotho
    /// core; defined and validated only by the game layer. Encoded as JSON per <see cref="LobbyTicketCodec"/>.
    /// Games may add fields to their own encoding — the validator ignores unknown JSON keys.
    /// </summary>
    public readonly struct LobbyTicket
    {
        /// <summary>Stable account id (invariant across sessions).</summary>
        public readonly string Account;
        /// <summary>Display name.</summary>
        public readonly string DisplayName;
        /// <summary>Lobby match id this ticket is bound to; the validator checks it against the expected session id.</summary>
        public readonly string SessionId;
        /// <summary>Issue time (unix ms). Carried but not checked by the validator; the operative time bound is ExpiresAt.</summary>
        public readonly long IssuedAt;
        /// <summary>Expiry time (unix ms). The validator rejects the ticket when ExpiresAt &lt;= now.</summary>
        public readonly long ExpiresAt;
        /// <summary>One-time nonce providing session-scoped replay defence.</summary>
        public readonly string Nonce;

        public LobbyTicket(string account, string displayName, string sessionId,
                           long issuedAt, long expiresAt, string nonce)
        {
            Account = account ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            SessionId = sessionId ?? string.Empty;
            IssuedAt = issuedAt;
            ExpiresAt = expiresAt;
            Nonce = nonce ?? string.Empty;
        }
    }
}
