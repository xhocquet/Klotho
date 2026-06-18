using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;

namespace Brawler
{
    [KlothoSerializable(103)]
    public partial class SpawnCharacterCommand : CommandBase, IReliableCommand
    {
        [KlothoOrder(0)] public int CharacterClass;
        [KlothoOrder(1)] public FPVector2 SpawnPosition;
        [KlothoOrder(2)] public int SequenceNumber { get; set; }

        public int OrderKey => 0;
    }
}
