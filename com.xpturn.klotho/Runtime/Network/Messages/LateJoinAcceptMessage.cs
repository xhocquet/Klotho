using System.Collections.Generic;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Late Join accept message.
    /// SimulationConfig is already received in an earlier initialization step, so it is not included here.
    /// Delivers SessionConfig as a backfill to guests that missed the GameStartMessage.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.LateJoinAccept)]
    public partial class LateJoinAcceptMessage : NetworkMessageBase
    {
        [KlothoOrder] public int PlayerId;
        [KlothoOrder] public int CurrentTick;
        [KlothoOrder] public long Magic;
        [KlothoOrder] public long SharedEpoch;
        [KlothoOrder] public long ClockOffset;
        [KlothoOrder] public int PlayerCount;
        [KlothoOrder] public List<RosterEntry> Roster = new List<RosterEntry>();

        // --- SessionConfig fields (canonical order matches ISessionConfig / GameStartMessage) ---

        [KlothoOrder] public int RandomSeed;
        [KlothoOrder] public int MaxPlayers;
        [KlothoOrder] public int MinPlayers;
        [KlothoOrder] public int MaxSpectators;
        [KlothoOrder] public bool AllowLateJoin;
        [KlothoOrder] public int LateJoinDelayTicks;
        [KlothoOrder] public int ReconnectTimeoutMs;
        [KlothoOrder] public int ReconnectMaxRetries;
        [KlothoOrder] public int LateJoinDelaySafety;
        [KlothoOrder] public int RttSanityMaxMs;
        [KlothoOrder] public int MinStallAbortTicks;
        [KlothoOrder] public int CountdownDurationMs;
        [KlothoOrder] public int AbortGraceMs;
        [KlothoOrder] public int EndGracePolicy; // EndGracePolicy enum as int
        [KlothoOrder] public int EndGraceMs;
        [KlothoOrder] public int ClientShutdownGraceMs;

        // --- PlayerConfig list (existing player data, concatenated serialized bytes) ---

        /// <summary>
        /// Serialized PlayerConfig data for existing players. All entries are concatenated into a single byte[],
        /// and each entry's length is stored in PlayerConfigLengths.
        /// </summary>
        [KlothoOrder] public byte[] PlayerConfigData;

        /// <summary>
        /// List of per-entry lengths within PlayerConfigData.
        /// </summary>
        [KlothoOrder] public List<int> PlayerConfigLengths = new List<int>();

        // Server-recommended extra InputDelay ticks for catchup-gap compensation.
        [KlothoOrder] public int RecommendedExtraDelay;
    }
}
