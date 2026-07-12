using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using xpTURN.Klotho.Network;       // NetworkMessageBase, NetworkMessageType
using xpTURN.Klotho.Serialization; // [KlothoSerializable(Struct)], SpanWriter, SpanReader

namespace Brawler
{
    /// <summary>
    /// Per-player match result entry for the Brawler sample. PURE deterministic stats keyed by
    /// PlayerId — NO identity (Account/DisplayName ride the wire roster side-channel, not this game blob). All
    /// fixed-width ints, so it is an unmanaged <c>[KlothoSerializableStruct]</c> (like <c>RosterEntry</c>).
    /// </summary>
    [KlothoSerializableStruct]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct BrawlerPlayerResult
    {
        public int PlayerId;             // in-sim deterministic key; lobby/backend joins to the wire roster
        public int Placement;            // 1-based rank (1 = best)
        public int StockCount;           // remaining stocks (stat)
        public int KnockbackTaken;       // accumulated knockback % (stat) ← CharacterComponent.KnockbackPower
        public int AcquiredSkillMask;    // acquired skills (acquisition)
        public int OwnedConsumableMask;  // owned consumables (acquisition)
    }

    /// <summary>
    /// Match-scoped result blob for the Brawler sample — the game-owned opaque payload carried by
    /// <c>GameOverEvent.MatchResultData</c>. Assembled at match-end from verified ECS state and read
    /// by the server via the <c>IMatchResultProvider</c> cast. MessageTypeId beyond the reserved range
    /// (PlayerConfig=200, ReplayConfig=201 → 202).
    /// </summary>
    [KlothoSerializable(MessageTypeId = (NetworkMessageType)202)]
    public partial class BrawlerMatchResult : NetworkMessageBase
    {
        [KlothoOrder] public int StageId;
        [KlothoOrder] public List<BrawlerPlayerResult> Players = new List<BrawlerPlayerResult>();
    }

    /// <summary>byte[]↔<see cref="BrawlerMatchResult"/> facade — mirrors <c>BrawlerReplayConfig</c>. The source
    /// generator emits Serialize/Deserialize/GetSerializedSize, so producer (server sim) and consumer (a
    /// Brawler-aware lobby / offline journal reader) share one format with no hand-rolled wire.</summary>
    public static class BrawlerMatchResultExtensions
    {
        public static byte[] ToBytes(this BrawlerMatchResult r)
        {
            var buf = new byte[r.GetSerializedSize()];
            var writer = new SpanWriter(buf);
            r.Serialize(ref writer);
            return buf;
        }

        public static BrawlerMatchResult FromBytes(byte[] data)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("BrawlerMatchResult: data is empty");
            var r = new BrawlerMatchResult();
            var reader = new SpanReader(data, 0, data.Length);
            r.Deserialize(ref reader);
            return r;
        }
    }
}
