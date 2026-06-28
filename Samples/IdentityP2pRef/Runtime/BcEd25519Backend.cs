// Production Ed25519 backend over BouncyCastle.
//
// BouncyCastle.Cryptography is bundled in this shared package (Runtime/Plugins/, Unity) — so this file
// compiles unconditionally; no KLOTHO_BOUNCYCASTLE define or per-project DLL drop is needed (the prior
// #if guard is gone now that BC ships with the package). Godot consumers supply BC via a NuGet
// <PackageReference> while <Compile Include>-ing this same source.
// Uses ONLY the low-level Org.BouncyCastle.Math.EC.Rfc8032.Ed25519 static API (no provider/registry
// reflection → IL2CPP / Godot-trim safe). Ed25519 is RFC 8032 deterministic, so this interoperates
// byte-identically with any conformant implementation (BC version-independent).
using System;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace xpTURN.Klotho.Samples.Identity
{
    /// <summary>Real Ed25519 sign/verify via BouncyCastle (default backend; the test fake is <see cref="FakeEd25519Backend"/>).</summary>
    public sealed class BcEd25519Backend : IEd25519Backend
    {
        public byte[] Sign(byte[] privateKey, byte[] message)
        {
            if (privateKey == null || privateKey.Length != Ed25519.SecretKeySize)
                throw new ArgumentException("Ed25519 private seed must be 32 bytes", nameof(privateKey));
            if (message == null) throw new ArgumentNullException(nameof(message));
            var sig = new byte[Ed25519.SignatureSize];
            Ed25519.Sign(privateKey, 0, message, 0, message.Length, sig, 0);
            return sig;
        }

        public bool Verify(byte[] publicKey, byte[] message, byte[] signature)
        {
            if (publicKey == null || publicKey.Length != Ed25519.PublicKeySize) return false;
            if (signature == null || signature.Length != Ed25519.SignatureSize) return false;
            if (message == null) return false;
            return Ed25519.Verify(signature, 0, publicKey, 0, message, 0, message.Length);
        }

        /// <summary>Derives the 32-byte public key from a 32-byte private seed (used for dev/lobby key setup).</summary>
        public static byte[] DerivePublicKey(byte[] privateSeed)
        {
            if (privateSeed == null || privateSeed.Length != Ed25519.SecretKeySize)
                throw new ArgumentException("Ed25519 private seed must be 32 bytes", nameof(privateSeed));
            var pk = new byte[Ed25519.PublicKeySize];
            Ed25519.GeneratePublicKey(privateSeed, 0, pk, 0);
            return pk;
        }
    }
}
