using System;
using System.Security.Cryptography;

namespace xpTURN.Klotho.Samples.Identity
{
    /// <summary>
    /// TEST-ONLY deterministic backend — NOT real Ed25519. Lets the lib-agnostic core and its unit tests
    /// run without the BouncyCastle binary. Real crypto = a BouncyCastle adapter (one-file swap). <para>
    /// Sign = SHA-512(key ‖ message) (64 B, deterministic). Verify recomputes with the public key and
    /// compares — so tampering (message changed) or a wrong key both fail, exactly as a real signature
    /// would for the *logic* tests. The test maps public key == private key (symmetric); this fake does
    /// not provide real asymmetric security and must never leave tests.
    /// </para>
    /// </summary>
    public sealed class FakeEd25519Backend : IEd25519Backend
    {
        public byte[] Sign(byte[] privateKey, byte[] message)
        {
            using var sha = SHA512.Create();
            var buf = new byte[privateKey.Length + message.Length];
            Buffer.BlockCopy(privateKey, 0, buf, 0, privateKey.Length);
            Buffer.BlockCopy(message, 0, buf, privateKey.Length, message.Length);
            return sha.ComputeHash(buf); // 64 bytes, matches Ed25519 signature length
        }

        public bool Verify(byte[] publicKey, byte[] message, byte[] signature)
        {
            byte[] expected = Sign(publicKey, message); // fake: public key == private key
            return FixedTimeEquals(expected, signature);
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
