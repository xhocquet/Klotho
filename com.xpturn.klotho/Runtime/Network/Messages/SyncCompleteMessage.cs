using System.Collections.Generic;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Sync complete message (host → client, handshake complete)
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.SyncComplete)]
    public partial class SyncCompleteMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public long Magic;

        [KlothoOrder]
        public int PlayerId;

        [KlothoOrder]
        public long SharedEpoch;

        [KlothoOrder]
        public long ClockOffset;

        // Server-recommended extra InputDelay ticks for normal-join player seed.
        // Trailing field — backward-compat with older clients via deserialize underrun (server-first deploy assumption).
        [KlothoOrder]
        public int RecommendedExtraDelay;

        // Pre-game roster snapshot so the joining guest builds its full player list immediately.
        // Appended after RecommendedExtraDelay to keep prior field offsets stable. The lists are
        // inline-initialized because the generated deserializer calls .Clear()/.Capacity and the
        // serializer reads .Count, both of which throw on a null list.
        [KlothoOrder]
        public List<int> PlayerIds = new List<int>();

        [KlothoOrder]
        public List<byte> PlayerConnectionStates = new List<byte>();

        [KlothoOrder]
        public List<byte> ReadyStates = new List<byte>();
    }
}
