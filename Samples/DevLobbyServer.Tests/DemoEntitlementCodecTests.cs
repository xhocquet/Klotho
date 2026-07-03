using Xunit;

using P2p = xpTURN.Klotho.Samples.Identity;    // DemoEntitlement (shared codec), DemoEntitlementData
using Sd = xpTURN.Klotho.Samples.Identity.Sd;  // DemoEntitlement (SD content)

namespace xpTURN.Samples.DevLobby.Tests
{
    /// <summary>
    /// The entitlement payload is a codegen [KlothoSerializableStruct] (<see cref="P2p.DemoEntitlementData"/>)
    /// (de)serialized at the opaque-byte[] boundary via <see cref="P2p.DemoEntitlement"/>. These verify
    /// (1) byte round-trip, (2) the null/empty → "all owned" opt-in-off default, and (3) producer↔consumer mask
    /// agreement for the P2P and SD demo inventories — the bytes a lobby ForAccount emits decode to exactly the
    /// masks the guard/gate/seed read.
    /// </summary>
    public sealed class DemoEntitlementCodecTests
    {
        [Fact] // struct → byte[] → struct preserves every mask
        public void Encode_Decode_RoundTrips()
        {
            var src = new P2p.DemoEntitlementData
            {
                OwnedClassMask = 0b1010,
                OwnedSkillMask = 0b11001100,
                OwnedConsumableMask = 0b101,
            };
            var bytes = P2p.DemoEntitlement.Encode(src);
            var got = P2p.DemoEntitlement.Decode(bytes);

            Assert.Equal(src.OwnedClassMask, got.OwnedClassMask);
            Assert.Equal(src.OwnedSkillMask, got.OwnedSkillMask);
            Assert.Equal(src.OwnedConsumableMask, got.OwnedConsumableMask);
        }

        [Fact] // no entitlement (null / empty) → all-owned (opt-in off = no gating)
        public void Decode_NullOrEmpty_IsAllOwned()
        {
            foreach (var blob in new byte[][] { null, System.Array.Empty<byte>() })
            {
                var d = P2p.DemoEntitlement.Decode(blob);
                Assert.Equal(~0, d.OwnedClassMask);
                Assert.Equal(~0, d.OwnedSkillMask);
                Assert.Equal(~0, d.OwnedConsumableMask);
            }
        }

        [Fact] // P2P restricted ("guest"): all classes, skill slot0 of each class only, no consumable
        public void P2p_ForAccount_Guest_RestrictedMasks()
        {
            var d = P2p.DemoEntitlement.Decode(P2p.DemoEntitlement.ForAccount("guest-42"));
            Assert.Equal(0b1111, d.OwnedClassMask);
            Assert.Equal(0b01010101, d.OwnedSkillMask); // slot0 of classes 0..3
            Assert.Equal(0, d.OwnedConsumableMask);
        }

        [Fact] // P2P owning account: all classes, all 8 skill slots, consumable (id 100 → bit0)
        public void P2p_ForAccount_Owner_FullMasks()
        {
            var d = P2p.DemoEntitlement.Decode(P2p.DemoEntitlement.ForAccount("alice"));
            Assert.Equal(0b1111, d.OwnedClassMask);
            Assert.Equal(P2p.DemoEntitlement.FullSkillMask, d.OwnedSkillMask);
            Assert.Equal(1, d.OwnedConsumableMask);
        }

        [Fact] // SD: aligned with the P2P rule — guest = slot0-only skills + no consumable; owner = full
        public void Sd_ForAccount_AlignedWithP2pRule()
        {
            var guest = P2p.DemoEntitlement.Decode(Sd.DemoEntitlement.ForAccount("guest-7"));
            Assert.Equal(0b1111, guest.OwnedClassMask);
            Assert.Equal(0b01010101, guest.OwnedSkillMask); // slot0 of classes 0..3
            Assert.Equal(0, guest.OwnedConsumableMask); // guest does not own the consumable

            var owner = P2p.DemoEntitlement.Decode(Sd.DemoEntitlement.ForAccount("bob"));
            Assert.Equal(P2p.DemoEntitlement.FullSkillMask, owner.OwnedSkillMask);
            Assert.Equal(1, owner.OwnedConsumableMask); // id 100 → bit0
        }
    }
}
