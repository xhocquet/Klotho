using System;

using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    /// <summary>
    /// Game configuration used as replay metadata for the Brawler sample.
    /// Serialized into ReplayMetadata.GameCustomData on recording,
    /// and deserialized on playback to restore Brawler-specific custom metadata.
    ///
    /// Currently has no fields — InitialStateSnapshot owns the ECS initial state (including bot entities),
    /// so duplicate fields such as bot count / max players have been removed. Kept as a slot for adding fields when needed.
    /// MessageTypeId uses values beyond BrawlerPlayerConfig (200).
    /// </summary>
    [KlothoSerializable(MessageTypeId = (NetworkMessageType)201)]
    public partial class BrawlerReplayConfig : NetworkMessageBase
    {
        // Slot for future Brawler-specific custom metadata (e.g., scenario ID, difficulty preset, etc.)
    }

    /// <summary>
    /// Helper for converting between BrawlerReplayConfig and byte[].
    /// Used for bidirectional conversion with ReplayMetadata.GameCustomData.
    /// </summary>
    public static class BrawlerReplayConfigExtensions
    {
        public static byte[] ToBytes(this BrawlerReplayConfig cfg)
        {
            var buf = new byte[cfg.GetSerializedSize()];
            var writer = new SpanWriter(buf);
            cfg.Serialize(ref writer);
            return buf;
        }

        public static BrawlerReplayConfig FromBytes(byte[] data)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("BrawlerReplayConfig: data is empty");
            var cfg = new BrawlerReplayConfig();
            var reader = new SpanReader(data);
            cfg.Deserialize(ref reader);
            return cfg;
        }
    }
}
