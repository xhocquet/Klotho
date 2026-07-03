using System.Runtime.InteropServices;
using xpTURN.Klotho.Serialization; // [KlothoSerializableStruct]

namespace xpTURN.Klotho.Samples.Identity
{
    /// <summary>
    /// Demo entitlement payload: the account's owned set, carried inside the opaque entitlement <c>byte[]</c>.
    /// The source generator emits <c>Serialize</c>/<c>Deserialize</c>/<c>GetSerializedSize</c>, so the producer
    /// (lobby) and the consumers (player-config guard / reliable-command gate / tick-0 loadout seed) share one
    /// format; (de)serialize at the byte[] boundary via <see cref="DemoEntitlement"/>.
    /// <para>
    /// The Klotho core carries the entitlement as an opaque <c>byte[]</c>; this struct lives in the sample/game
    /// layer only. Owned sets are fixed-width bitmasks (a <c>[KlothoSerializableStruct]</c> must be
    /// <c>unmanaged</c>): compact (12 bytes, fitting the P2P pre-auth budget) and split into three
    /// non-overlapping id namespaces — class / skill / consumable.
    /// </para>
    /// </summary>
    [KlothoSerializableStruct]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct DemoEntitlementData
    {
        /// <summary>Owned character classes: class id (0..3) → bit. Checked by the player-config guard.</summary>
        public int OwnedClassMask;
        /// <summary>Owned skill slots across all classes: (classIdx*2 + slot) → bit (4 classes * 2 = 8 bits). Loadout seed.</summary>
        public int OwnedSkillMask;
        /// <summary>Owned consumables: (consumableId - 100) → bit (id 100 → bit0). Checked by the gate + carried on the character.</summary>
        public int OwnedConsumableMask;
    }
}
