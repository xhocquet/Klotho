using System;
using System.Collections.Generic;

using xpTURN.Klotho.Network;          // IPlayerIdentityValidator
using xpTURN.Klotho.Samples.Identity; // p2pref: DevLobbyTicketIssuer, IEd25519Backend, LobbyTicket

namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>
    /// Shared dev provisioning for the SD reference: the dev PUBLIC key, the constant {matchId, serverId}
    /// pair, and the lobby's match→server assignment — agreed by the lobby, the game server, and the client
    /// so a single demo match interoperates. A mismatch in any of these surfaces as IdentitySessionMismatch
    /// (8) at redeem.
    /// <para>
    /// Client/game-server safe: this type holds only PUBLIC material. The lobby's PRIVATE signing seed and
    /// the ticket-minting factories live in <c>SdDevLobby</c> (the DevLobbyServer project) so the private key
    /// is never compiled into client or game-server builds. seed 0x01..0x20 → <see cref="PublicKey"/> below
    /// (RFC 8032 deterministic; the same pair the P2P sample uses).
    /// </para>
    /// <para>NOTE: pass <see cref="PublicKey"/> as the verify key with the real BouncyCastle backend; with the
    /// symmetric test fake (public == private) the test supplies its own key.</para>
    /// </summary>
    public static class SdDevIdentity
    {
        // public key (RFC 8032 deterministic from seed 0x01..0x20; the lobby private seed lives in SdDevLobby,
        // DevLobbyServer project — never in this client/server-shared package).
        // Key generation/rotation guide: Docs/Samples/DevIdentityKeys.md (the private seed lives in SdDevLobby.s_seed).
        private static readonly byte[] s_publicKey =
        {
            0x79, 0xb5, 0x56, 0x2e, 0x8f, 0xe6, 0x54, 0xf9, 0x40, 0x78, 0xb1, 0x12, 0xe8, 0xa9, 0x8b, 0xa7,
            0x90, 0x1f, 0x85, 0x3a, 0xe6, 0x95, 0xbe, 0xd7, 0xe0, 0xe3, 0x91, 0x0b, 0xad, 0x04, 0x96, 0x64,
        };

        /// <summary>Lobby Ed25519 public key (32 B) — game server / validator verify key. Returns a copy.</summary>
        public static byte[] PublicKey => (byte[])s_publicKey.Clone();

        /// <summary>Dev lobby match id all peers' tickets share (== ticket.SessionId). Build-time constant.</summary>
        public const string DevMatchId = "sdsample-dev-match";
        /// <summary>Dev dedicated-server id. The lobby asserts DevMatchId is assigned to this server.</summary>
        public const string DevServerId = "sdsample-dev-server";

        /// <summary>Ticket validity window — short; the lobby mints fresh per Issue.</summary>
        public const long TicketValidityMs = 5 * 60 * 1000;
        /// <summary>Redeem idempotency window — a repeat redeem of the same nonce within this returns the
        /// cached result, letting a disconnect-before-slot player recover on re-join. Keep it ≥ a realistic
        /// rejoin time (the core's validation timeout of ~5s plus reconnect).</summary>
        public const long IdempotencyWindowMs = 30 * 1000;
        /// <summary>Validator-internal redeem deadline (ms). Keep it below the core ValidationTimeoutMs (~5000).</summary>
        public const int LocalRedeemTimeoutMs = 3000;
        /// <summary>Lenient local-expiry skew margin (ms) for server↔lobby clock skew (redeem re-checks).</summary>
        public const long SkewMarginMs = 2000;

        /// <summary>Authoritative match→server assignment (matchmaking result; dev = single constant pair).</summary>
        public static IReadOnlyDictionary<string, string> MatchToServer { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal) { { DevMatchId, DevServerId } };

        // ── Lobby room-registry config (dev). The lobby seeds its room registry from these; they must stay
        //    in sync with the dedicated server's actual config. MaxRooms / MaxPlayersPerRoom MUST equal the
        //    dedicated server's maxRooms / sessionconfig.MaxPlayers, otherwise the lobby assigns rooms the
        //    server then rejects.
        /// <summary>Dedicated-server address the lobby injects into the issue response. This is the
        /// client-reachable advertised address, not the server's 0.0.0.0 bind address.</summary>
        public const string DedicatedServerHost = "127.0.0.1";
        /// <summary>Dedicated-server advertised port (matches the SD server's default listen port).</summary>
        public const int DedicatedServerPort = 7777;
        /// <summary>Room count per server — MUST equal the dedicated server's <c>maxRooms</c>.</summary>
        public const int MaxRooms = 2;
        /// <summary>Room capacity — MUST equal the dedicated server's <c>sessionconfig.MaxPlayers</c>.</summary>
        public const int MaxPlayersPerRoom = 2;

        // ── P1 dedi→lobby reporting (serverRegister / roomReport). ──
        /// <summary>roomReport heartbeat interval (ms) — periodic liveness + payload refresh (allowed 2~5s).</summary>
        public const long RoomReportIntervalMs = 3000;
        /// <summary>App-level backup timeout (ms) for a HUNG dedi (socket alive, no reports) — transport
        /// OnPeerDisconnected (~5s) is the primary crash signal; this only covers hangs. ≈ interval×3.</summary>
        public const long RoomReportBackupTimeoutMs = 10000;
        /// <summary>Grace (ms) after a server goes unavailable before the lobby reclaims its rooms — ≫ disconnect
        /// detection (a transient blip reconnects first) yet ≪ reservation TTL (= ticket validity, 5min).</summary>
        public const long ServerReclaimGraceMs = 15000;

        /// <summary>Builds the SD validator. <paramref name="verifyKey"/> = <see cref="PublicKey"/> (BC) or
        /// the symmetric test fake's own key.</summary>
        public static SdRedeemIdentityValidator CreateValidator(
            IEd25519Backend backend, byte[] verifyKey, ILobbyRedeemClient redeem, Func<long> nowUnixMs)
            => new SdRedeemIdentityValidator(verifyKey, backend, redeem, DevServerId, nowUnixMs, LocalRedeemTimeoutMs, SkewMarginMs);
    }
}
