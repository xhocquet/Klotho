using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Samples.SdSample
{
    public class RespawnSystem : ISystem
    {
        public void Update(ref Frame frame)
        {
            var stats = frame.AssetRegistry.Get<PlayerStatsAsset>();
            var filter = frame.Filter<PlayerComponent, TransformComponent, PhysicsBodyComponent>();
            while (filter.Next(out var entity))
            {
                ref var t = ref frame.Get<TransformComponent>(entity);
                if (t.Position.y >= stats.FallThresholdY) continue;

                ref var p = ref frame.Get<PlayerComponent>(entity);
                ref var phys = ref frame.Get<PhysicsBodyComponent>(entity);
                p.Score -= 1;
                p.LastInputH = FP64.Zero;
                p.LastInputV = FP64.Zero;
                t.Position = stats.SpawnPoint;
                phys.RigidBody.velocity = FPVector3.Zero;
            }
        }
    }
}
