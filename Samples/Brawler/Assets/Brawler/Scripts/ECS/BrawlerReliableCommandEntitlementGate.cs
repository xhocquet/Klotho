using xpTURN.Klotho.Core;              // ICommand
using xpTURN.Klotho.Network;           // IReliableCommandEntitlementGate, ReliableCommandVerdict
using xpTURN.Klotho.Samples.Identity;  // DemoEntitlement

namespace Brawler
{
    /// <summary>
    /// Reference in-match reliable-command entitlement gate for the Brawler sample. Server-side only:
    /// cross-checks a <see cref="UseConsumableCommand"/>'s consumable id against the account's authoritative
    /// owned set (the opaque entitlement blob stored at join) and drops an unowned use before it reaches an
    /// authoritative tick, so a denied consumable simply does not happen. Register it on the dedicated server
    /// via <c>RoomManagerConfig.ReliableCommandEntitlementGate</c>; when no lobby/entitlement is present the
    /// entitlement is null and every command is accepted (opt-in off → unchanged behaviour).
    /// <para>
    /// Only the consumable use is gated; other reliable commands (e.g. the start-of-play spawn, whose
    /// character class is already cross-checked by <see cref="BrawlerPlayerConfigEntitlementGuard"/>) pass
    /// through. The entitlement is decoded via <see cref="DemoEntitlement.Decode"/>; this gate reads only
    /// <c>OwnedConsumableMask</c>. A null/empty blob decodes to "all owned", so every command is accepted
    /// (opt-in off).
    /// </para>
    /// <para>
    /// This demonstrates the server-only-catalog path: ownership is held off the simulation and the gate is
    /// the verification point. Had the owned set been seeded into the deterministic simulation state instead,
    /// the simulation could no-op an unowned use on every peer with no server gate.
    /// </para>
    /// </summary>
    public sealed class BrawlerReliableCommandEntitlementGate : IReliableCommandEntitlementGate
    {
        public ReliableCommandVerdict Check(int playerId, byte[] entitlement, ICommand command)
        {
            // Not a gated command (e.g. spawn) → pass through.
            if (!(command is UseConsumableCommand use))
                return ReliableCommandVerdict.Accept();

            int consumableMask = DemoEntitlement.Decode(entitlement).OwnedConsumableMask;

            // Owned consumable → accept; otherwise the unowned use is dropped. (id 100 → bit0.)
            int bit = use.ConsumableId - DemoEntitlement.ConsumableId;
            if ((uint)bit < 32u && (consumableMask & (1 << bit)) != 0)
                return ReliableCommandVerdict.Accept();

            return ReliableCommandVerdict.Drop();
        }
    }
}
