using System;

using xpTURN.Klotho.Logging;          // IKLogger
using xpTURN.Klotho.Samples.Identity; // p2pref: DevLobbyTicketIssuer, IEd25519Backend, LobbyTicket

namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>
    /// Lobby-only (issuer-side) dev provisioning for the SD reference — the PRIVATE Ed25519 seed and the
    /// ticket-minting factories. This type lives ONLY in the DevLobbyServer project (the lobby), never in the
    /// shared IdentitySdRef package, so the private signing key is not compiled into client or game-server
    /// builds. Public material (PublicKey, the {matchId, serverId} constants, CreateValidator) stays in
    /// <see cref="SdDevIdentity"/> (IdentitySdRef), which clients and game servers safely reference.
    /// <para>Uses the fixed dev key pair (seed 0x01..0x20 → <see cref="SdDevIdentity.PublicKey"/>; RFC 8032
    /// deterministic; the same pair the P2P sample uses). Dev only — a production lobby holds a real signing
    /// key out-of-band.</para>
    /// </summary>
    public static class SdDevLobby
    {
        // seed 0x01..0x20 (lobby private) → SdDevIdentity.PublicKey (RFC 8032 deterministic; same pair as P2P sample).
        // Key generation/rotation guide: Docs/Samples/DevIdentityKeys.md (changing the seed requires updating SdDevIdentity.s_publicKey too).
        private static readonly byte[] s_seed =
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10,
            0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f, 0x20,
        };

        /// <summary>Lobby Ed25519 private seed (32 B) — lobby/issuer only. Returns a copy.</summary>
        public static byte[] Seed => (byte[])s_seed.Clone();

        /// <summary>Builds the lobby issuer (holds the private seed).</summary>
        public static DevLobbyTicketIssuer CreateIssuer(IEd25519Backend backend)
            => new DevLobbyTicketIssuer(backend, Seed);

        /// <summary>Builds the dev lobby decision core (issue + redeem authority), shared by the in-proc
        /// fake and the DevLobbyServer. <paramref name="verifyKey"/> = <see cref="SdDevIdentity.PublicKey"/>
        /// (BC) or <see cref="Seed"/> (symmetric test fake).</summary>
        public static DevLobbyCore CreateDevLobbyCore(IEd25519Backend backend, byte[] verifyKey, Func<long> nowUnixMs,
                                                      IKLogger logger = null)
        {
            // Seed the room registry with the single dev dedicated server. MaxRooms / MaxPlayersPerRoom MUST
            // match the dedicated server's maxRooms / sessionconfig.MaxPlayers, otherwise the lobby assigns
            // rooms the server then rejects.
            var registry = new LobbyRoomRegistry();
            registry.AddServer(SdDevIdentity.DevServerId,
                               SdDevIdentity.DedicatedServerHost, SdDevIdentity.DedicatedServerPort,
                               SdDevIdentity.MaxRooms, SdDevIdentity.MaxPlayersPerRoom);
            return new DevLobbyCore(CreateIssuer(backend), backend, verifyKey, nowUnixMs,
                                    SdDevIdentity.IdempotencyWindowMs, registry,
                                    logger: logger); // optional report-channel observability
        }

        /// <summary>Convenience: mint a dev ticket bound to DevMatchId (caller supplies time/nonce for
        /// test determinism; runtime supplies wall clock + RNG nonce).</summary>
        public static string MintTicket(DevLobbyCore lobby, string account, string displayName,
                                        long issuedAtMs, long expiresAtMs, string nonce)
            => lobby.Issue(new LobbyTicket(account, displayName, SdDevIdentity.DevMatchId, issuedAtMs, expiresAtMs, nonce));
    }
}
