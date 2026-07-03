using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace Brawler
{
    /// <summary>
    /// In-match request to use an owned consumable. Issued mid-match via the reliable command channel
    /// (<c>IKlothoEngine.IssueOnce</c>), separately from the start-of-play character spawn. On a dedicated
    /// server the authority cross-checks <see cref="ConsumableId"/> against the player's owned set before the
    /// command reaches an authoritative tick; an unowned use is dropped (the action does not happen).
    /// </summary>
    [KlothoSerializable(114)]
    public partial class UseConsumableCommand : CommandBase, IReliableCommand
    {
        [KlothoOrder(0)] public int ConsumableId;
        [KlothoOrder(1)] public int SequenceNumber { get; set; }
        // Per-use id (≥1), stable across the initial send and every retry of the SAME use, so the simulation
        // deduplicates a re-delivered use. Distinct from SequenceNumber (framework-managed, unstable on the
        // P2P legacy path). Monotonic per issuing player; the sim tracks the last-applied value per character.
        [KlothoOrder(2)] public int UseSeq;

        public int OrderKey => 0;
    }
}
