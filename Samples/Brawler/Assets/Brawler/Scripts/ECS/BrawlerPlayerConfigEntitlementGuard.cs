using xpTURN.Klotho.Core;              // PlayerConfigBase
using xpTURN.Klotho.Network;           // IPlayerConfigEntitlementGuard, PlayerConfigVerdict
using xpTURN.Klotho.Samples.Identity;  // DemoEntitlement, DemoEntitlementData

namespace Brawler
{
    /// <summary>
    /// Reference player-config entitlement guard for the Brawler sample. Server-side only: cross-checks a client's
    /// <see cref="BrawlerPlayerConfig.SelectedCharacterClass"/> against the account's authoritative owned set
    /// (the opaque entitlement blob stored at join), and clamps an unowned selection to a server-chosen owned
    /// default instead of trusting the client. Register it on the dedicated server via
    /// <c>RoomManagerConfig.PlayerConfigEntitlementGuard</c>; when no lobby/entitlement is present the
    /// entitlement is null and every selection passes through (opt-in off → unchanged behaviour).
    /// <para>
    /// The entitlement is decoded via <see cref="DemoEntitlement.Decode"/> to a <see cref="DemoEntitlementData"/>;
    /// this guard reads only <c>OwnedClassMask</c>. A null/empty/malformed blob decodes to "all owned", so any
    /// selection passes (opt-in off).
    /// </para>
    /// </summary>
    public sealed class BrawlerPlayerConfigEntitlementGuard : IPlayerConfigEntitlementGuard
    {
        public PlayerConfigVerdict Check(int playerId, byte[] entitlement, PlayerConfigBase selection)
        {
            // Not a Brawler selection (unexpected) — leave it to the core/default path.
            if (!(selection is BrawlerPlayerConfig cfg))
                return PlayerConfigVerdict.Pass();

            int classMask = DemoEntitlement.Decode(entitlement).OwnedClassMask;

            // Owned selection → allow as-is. (Out-of-range class → not owned → clamp.)
            int cls = cfg.SelectedCharacterClass;
            if ((uint)cls < 32u && (classMask & (1 << cls)) != 0)
                return PlayerConfigVerdict.Pass();

            // Unowned selection → clamp to the first owned class (server-decided, deterministic).
            // No owned class at all (mask 0) → nothing to clamp to → pass through (matches prior no-owned pass).
            if (classMask == 0)
                return PlayerConfigVerdict.Pass();
            int firstOwned = LowestSetBit(classMask);
            return PlayerConfigVerdict.Clamp(new BrawlerPlayerConfig { SelectedCharacterClass = firstOwned });
        }

        // Index of the lowest set bit (deterministic "first owned class"). mask != 0 guaranteed by caller.
        private static int LowestSetBit(int mask)
        {
            int bit = 0;
            while ((mask & 1) == 0) { mask >>= 1; bit++; }
            return bit;
        }
    }
}
