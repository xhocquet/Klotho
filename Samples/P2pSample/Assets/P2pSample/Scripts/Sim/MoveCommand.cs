using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Samples.P2pSample
{
    [KlothoSerializable(100)]
    public partial class MoveCommand : CommandBase
    {
        public override bool IsContinuousInput => true;

        [KlothoOrder(0)] public FP64 H;
        [KlothoOrder(1)] public FP64 V;
    }
}
