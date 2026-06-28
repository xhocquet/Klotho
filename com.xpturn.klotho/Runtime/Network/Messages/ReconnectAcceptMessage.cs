using System.Collections.Generic;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    [KlothoSerializable(MessageTypeId = NetworkMessageType.ReconnectAccept)]
    public partial class ReconnectAcceptMessage : NetworkMessageBase
    {
        [KlothoOrder] public int PlayerId;
        [KlothoOrder] public int CurrentTick;
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

        // Server-recommended extra InputDelay ticks for catchup-gap compensation.
        [KlothoOrder] public int RecommendedExtraDelay;
    }
}
