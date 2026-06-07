using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;

namespace xpTURN.Samples.SdSample
{
    public class MovementSystem : ISystem, ICommandSystem
    {
        public void OnCommand(ref Frame frame, ICommand command)
        {
            if (command is not MoveCommand m) return;
            int pid = m.PlayerId;
            var filter = frame.Filter<PlayerComponent>();
            while (filter.Next(out var entity))
            {
                ref var p = ref frame.Get<PlayerComponent>(entity);
                if (p.PlayerId != pid) continue;
                p.LastInputH = m.H;
                p.LastInputV = m.V;
                return;
            }
        }

        public void Update(ref Frame frame)
        {
            var stats = frame.AssetRegistry.Get<PlayerStatsAsset>();
            var filter = frame.Filter<PlayerComponent, PhysicsBodyComponent>();
            while (filter.Next(out var entity))
            {
                ref var p = ref frame.Get<PlayerComponent>(entity);
                ref var phys = ref frame.Get<PhysicsBodyComponent>(entity);
                phys.RigidBody.velocity.x = p.LastInputH * stats.MoveSpeed;
                phys.RigidBody.velocity.z = p.LastInputV * stats.MoveSpeed;
                // velocity.y left to the engine gravity (PhysicsSystem) + static ground resting-contact.
            }
        }
    }
}
