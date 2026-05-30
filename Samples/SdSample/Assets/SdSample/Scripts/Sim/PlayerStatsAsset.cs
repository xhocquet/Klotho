using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Samples.SdSample
{
    [KlothoDataAsset(100, AssetId = 100, Key = "PlayerStats")]
    public partial class PlayerStatsAsset : IDataAsset
    {
        [KlothoOrder(0)] public FP64 MoveSpeed;
        [KlothoOrder(1)] public FP64 MatchDuration;
        [KlothoOrder(2)] public FP64 FallThresholdY;
        [KlothoOrder(3)] public FPVector3 SpawnPoint;
        [KlothoOrder(4)] public FP64 InitialSpawnOffsetX;
        [KlothoOrder(5)] public FP64 PlayerMass;
        [KlothoOrder(6)] public FP64 PlayerHalfExtent;
    }
}
