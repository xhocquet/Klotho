using P2pRefEnt = xpTURN.Klotho.Samples.Identity.DemoEntitlement; // shared entitlement codec + constants

namespace xpTURN.Klotho.Samples.Identity.Sd
{
    /// <summary>
    /// SD demo entitlement CONTENT. The format — the <see cref="DemoEntitlementData"/> struct and its codec —
    /// is common with the P2P reference sample; the core carries the result as an opaque <c>byte[]</c>. This
    /// type only decides *which account owns what*; the redeem path (<see cref="DevLobbyCore"/>) serializes it
    /// into the redeem response.
    /// </summary>
    public static class DemoEntitlement
    {
        /// <summary>Demo in-match consumable id (shared namespace with the P2P sample).</summary>
        public const int ConsumableId = P2pRefEnt.ConsumableId;

        /// <summary>
        /// SD demo inventory — aligned with the P2P reference rule so both modes demo the same gates:
        /// every account owns all four character classes (0=Warrior, 1=Mage, 2=Rogue, 3=Knight), so any
        /// valid class selection passes the player-config guard. A *restricted* ("guest") account owns
        /// only **skill slot 0** of every class (slot 1 is Skip:NotAcquired via the join-time loadout
        /// seed) and **no consumable** (a Use of <see cref="ConsumableId"/> is dropped by the
        /// reliable-command gate); every other account owns the full loadout. Pure function of
        /// <paramref name="account"/> → replay/reconnect/late-join re-derive the same bytes.
        /// </summary>
        public static byte[] ForAccount(string account)
        {
            if (IsRestricted(account))
                // classes {0..3} + skill slot0 of each class (bits 0,2,4,6) + no consumable.
                return P2pRefEnt.Encode(new DemoEntitlementData
                {
                    OwnedClassMask      = P2pRefEnt.AllClassesMask,
                    OwnedSkillMask      = 0b01010101, // slot0 of classes 0..3 (matches the P2P rule)
                    OwnedConsumableMask = 0,
                });

            // classes {0..3} + all 8 skill slots + the in-match consumable (id 100 → bit0).
            return P2pRefEnt.Encode(new DemoEntitlementData
            {
                OwnedClassMask      = P2pRefEnt.AllClassesMask,
                OwnedSkillMask      = P2pRefEnt.FullSkillMask,
                OwnedConsumableMask = 1,
            });
        }

        // Demo rule (transparent + tester-controllable): a "guest" account is restricted; every other
        // account (including the generated dev-NNNN ids) owns the full loadout. To observe the gates,
        // set one client's account to contain "guest" (BrawlerGameController._account) and leave the
        // other blank/owning. Pure ordinal check → idempotent across re-redeem/reconnect; empty
        // account → restricted (defensive), matching the P2P rule.
        private static bool IsRestricted(string account)
        {
            if (string.IsNullOrEmpty(account))
                return true;
            return account.IndexOf("guest", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
