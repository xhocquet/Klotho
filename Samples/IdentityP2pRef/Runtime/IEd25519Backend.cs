namespace xpTURN.Klotho.Samples.Identity
{
    /// <summary>
    /// Ed25519 sign/verify seam. The crypto library lives behind this one interface so the
    /// BouncyCastle-vs-vendored choice is a single-file swap:
    ///   - production: a BouncyCastle adapter over <c>Org.BouncyCastle.Math.EC.Rfc8032.Ed25519</c>;
    ///   - the deterministic test fake (<see cref="FakeEd25519Backend"/>) lets the lib-agnostic core
    ///     and its unit tests run without the BouncyCastle binary.
    /// Ed25519 (RFC 8032): private seed 32 B, public key 32 B, signature 64 B; deterministic & interoperable
    /// across any conformant implementation.
    /// </summary>
    public interface IEd25519Backend
    {
        /// <summary>Signs <paramref name="message"/> with the 32-byte private seed; returns a 64-byte signature.</summary>
        byte[] Sign(byte[] privateKey, byte[] message);

        /// <summary>
        /// Verifies <paramref name="signature"/> over the EXACT <paramref name="message"/> bytes
        /// (verify-over-wire — the caller never re-serializes). Returns false on any mismatch;
        /// the caller still guards segment/length validity before calling.
        /// </summary>
        bool Verify(byte[] publicKey, byte[] message, byte[] signature);
    }
}
