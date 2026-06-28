namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>
    /// Identity reject wire codes (disconnect-payload bytes 6~11) shared by the SD validator and the dev
    /// lobby core. These are the wire values the client decodes via <c>JoinFailReason.FromJoinReject</c>;
    /// they are NOT the JoinFailReason enum values (wire value ≠ enum value). Defined once here so the
    /// validator and the lobby agree on the contract (the validator clamps any out-of-range redeem code
    /// into this range before forwarding it to the client).
    /// </summary>
    internal static class SdWireCodes
    {
        public const byte IdentityInvalid         = 6; // bad signature / format / oversize-or-empty account
        public const byte IdentityExpired         = 7; // expired ticket (local 1st or redeem)
        public const byte IdentitySessionMismatch = 8; // match not assigned to this server (cross-match)
        public const byte IdentityRejected        = 9; // redeem explicit reject (ban / sanction / consumed nonce)
        public const byte IdentityRequired        = 10; // validator present but no ticket
        public const byte IdentityValidationFailed = 11; // transport failure / redeem timeout / exception

        public const int MaxAccountUtf8        = 62;   // the roster wire field (FixedString64) holds 62 UTF-8 bytes
        public const int Ed25519SignatureLength = 64;

        /// <summary>Clamps a redeem-returned reject code into the identity range 6~11 — a trust boundary on
        /// whatever the lobby returns. An out-of-range value (e.g. a buggy lobby returning RoomFull=2) would
        /// make the client misinterpret the disconnect as a retryable room reason and retry in a loop with a
        /// credential the lobby already rejected. Anything outside 6~11 defaults to 9 (IdentityRejected).</summary>
        public static byte ClampIdentityCode(byte code)
            => (code >= IdentityInvalid && code <= IdentityValidationFailed) ? code : IdentityRejected;
    }
}
