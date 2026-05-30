using System.Runtime.InteropServices;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Samples.P2pSample
{
    [KlothoComponent(100)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct PlayerComponent : IComponent
    {
        public int PlayerId;
        public int Score;
        public FP64 LastInputH;
        public FP64 LastInputV;
    }
}
