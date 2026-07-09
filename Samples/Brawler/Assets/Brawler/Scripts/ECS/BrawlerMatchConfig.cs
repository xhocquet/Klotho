using System.Runtime.InteropServices;
using xpTURN.Klotho.Serialization; // [KlothoSerializableStruct], SpanWriter, SpanReader

namespace Brawler
{
    /// <summary>
    /// Per-match dynamic config payload for the Brawler sample, carried opaquely in
    /// <c>SimulationConfig.MatchConfigData</c> (byte[]) and propagated to every peer at match start. The
    /// source generator emits <c>Serialize</c>/<c>Deserialize</c>/<c>GetSerializedSize</c> (no hand-written
    /// codec), so the authority (SD server / P2P host) and the consumers share one format. Distinct from
    /// <see cref="BrawlerPlayerConfig"/> (per-player, message-embedded): this is per-match, (de)serialized at
    /// the byte[] boundary via <see cref="BrawlerMatchConfig"/> — mirroring the DemoEntitlement pattern.
    /// <para>
    /// <c>[KlothoSerializableStruct]</c> requires the type be <c>partial</c>, <c>unmanaged</c>, and laid out
    /// <c>[StructLayout(Sequential, Pack = 4)]</c>. Add fixed-width fields here as more match knobs are needed.
    /// </para>
    /// </summary>
    [KlothoSerializableStruct]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct BrawlerMatchConfigData
    {
        public int BotCount;
    }

    /// <summary>
    /// byte[]↔<see cref="BrawlerMatchConfigData"/> boundary (mirrors the DemoEntitlement facade). null / empty
    /// / malformed → default (BotCount = 0 = no bots = the pre-existing default), so an unset MatchConfigData
    /// is a no-op — a client with no propagated config falls back cleanly.
    /// </summary>
    public static class BrawlerMatchConfig
    {
        /// <summary>Serialize the payload struct to the opaque byte[] the core carries (codegen, no hand-rolling).</summary>
        public static byte[] Encode(in BrawlerMatchConfigData d)
        {
            var buf = new byte[d.GetSerializedSize()];
            var w = new SpanWriter(buf);
            d.Serialize(ref w);
            return buf;
        }

        /// <summary>Deserialize the opaque byte[] to the payload struct; null / empty / malformed → default.</summary>
        public static BrawlerMatchConfigData Decode(byte[] data)
        {
            if (data == null || data.Length == 0) return default;
            try
            {
                var d = new BrawlerMatchConfigData();
                var r = new SpanReader(data, 0, data.Length);
                d.Deserialize(ref r);
                return d;
            }
            catch
            {
                return default; // lenient: a corrupt blob → default (no bots)
            }
        }
    }
}
