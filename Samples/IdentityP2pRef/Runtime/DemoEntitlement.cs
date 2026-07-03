using xpTURN.Klotho.Serialization; // SpanWriter, SpanReader

namespace xpTURN.Klotho.Samples.Identity
{
    /// <summary>
    /// Demo entitlement codec — the byte[]↔<see cref="DemoEntitlementData"/> boundary shared by the producer
    /// (lobby) and the consumers (player-config guard / reliable-command gate / tick-0 loadout seed). The core
    /// treats the entitlement as an opaque <c>byte[]</c>; this facade (de)serializes the struct via its
    /// generated codec. The format is common to the P2P and SD samples; the per-account CONTENT (which account
    /// owns what) lives in each sample's <c>ForAccount</c>.
    /// </summary>
    public static class DemoEntitlement
    {
        /// <summary>
        /// Id namespaces (shared contract). All in separate numeric ranges so they never collide: class id
        /// 0..3, skill id = <see cref="SkillIdBase"/> + classIdx*2 + slot, consumable id = <see cref="ConsumableId"/>.
        /// The producer maps these ids into the struct's fixed-width masks.
        /// </summary>
        public const int ConsumableId   = 100;
        public const int SkillIdBase    = 300;
        public const int SkillSlotCount = 8;                         // 4 classes * 2 slots
        public const int FullSkillMask  = (1 << SkillSlotCount) - 1; // 0xFF — every class/slot acquired
        public const int AllClassesMask = 0b1111;                    // classes 0..3

        /// <summary>Serialize the payload struct to the opaque byte[] the core carries (codegen, no hand-rolling).</summary>
        public static byte[] Encode(in DemoEntitlementData d)
        {
            var buf = new byte[d.GetSerializedSize()];
            var w = new SpanWriter(buf);
            d.Serialize(ref w);
            return buf;
        }

        /// <summary>
        /// Deserialize the opaque byte[] to the payload struct. null / empty / malformed → <see cref="Full"/>
        /// (all owned), preserving the opt-in-OFF default: no entitlement configured ⇒ no gating (every class
        /// selection passes, full loadout). So every consumer can read the masks unconditionally.
        /// </summary>
        public static DemoEntitlementData Decode(byte[] entitlement)
        {
            if (entitlement == null || entitlement.Length == 0)
                return Full;
            try
            {
                var d = new DemoEntitlementData();
                var r = new SpanReader(entitlement, 0, entitlement.Length);
                d.Deserialize(ref r);
                return d;
            }
            catch
            {
                return Full; // lenient: a corrupt blob does not gate
            }
        }

        /// <summary>"All owned" — the no-entitlement / opt-in-off default (no gating).</summary>
        public static DemoEntitlementData Full => new DemoEntitlementData
        {
            OwnedClassMask      = ~0,
            OwnedSkillMask      = ~0,
            OwnedConsumableMask = ~0,
        };

        /// <summary>
        /// P2P demo inventory (per-account, pure → reconnect/late-join re-derives the same bytes; the guest's
        /// signature-only extraction yields identical bytes, per-peer seed agreement): every account owns all
        /// four classes. A *restricted* ("guest") account owns only **skill slot 0** of every class (so slot 1
        /// use no-ops in-match) and **no consumable**; every other account owns **both skill slots** and the
        /// consumable. To exercise it, set one client's <c>BrawlerGameController._account</c> to contain "guest".
        /// </summary>
        public static byte[] ForAccount(string account)
        {
            if (IsRestricted(account))
                // classes {0..3} + skill slot0 of each class (bits 0,2,4,6) + no consumable.
                return Encode(new DemoEntitlementData
                {
                    OwnedClassMask      = AllClassesMask,
                    OwnedSkillMask      = 0b01010101, // slot0 of classes 0..3
                    OwnedConsumableMask = 0,
                });

            // classes {0..3} + all 8 skill slots + the in-match consumable (id 100 → bit0).
            return Encode(new DemoEntitlementData
            {
                OwnedClassMask      = AllClassesMask,
                OwnedSkillMask      = FullSkillMask,
                OwnedConsumableMask = 1, // consumable id 100 → offset 0 → bit0
            });
        }

        // Demo rule (transparent + tester-controllable): a "guest" account is restricted; every other account
        // (incl. generated dev-NNNN ids) owns the full loadout. Pure ordinal check → idempotent across
        // reconnect/late-join. Empty account → restricted (defensive).
        private static bool IsRestricted(string account)
        {
            if (string.IsNullOrEmpty(account))
                return true;
            return account.IndexOf("guest", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
