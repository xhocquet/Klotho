using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;

namespace Brawler
{
    /// <summary>
    /// Decrements RespawnTimer of dead characters every tick.
    /// When it reaches 0, reads the spawn position from SpawnMarkerComponent and respawns the character.
    /// - Position: SpawnPosition(XZ), Y=CharacterSpawnY
    /// - Velocity: Reset PhysicsBodyComponent velocity
    /// - Remove KnockbackComponent
    /// - IsDead = false
    /// </summary>
    public class RespawnSystem : ISystem
    {
        readonly EventSystem _events;

        public RespawnSystem(EventSystem events)
        {
            _events = events;
        }

        public void Update(ref Frame frame)
        {
            var filter = frame.Filter<CharacterComponent, TransformComponent, PhysicsBodyComponent>();
            while (filter.Next(out var entity))
            {
                ref var character = ref frame.Get<CharacterComponent>(entity);
                if (!character.IsDead) continue;
                if (character.RespawnTimer <= 0) continue;

                character.RespawnTimer--;
                if (character.RespawnTimer > 0) continue;

                var rules = frame.AssetRegistry.Get<BrawlerGameRulesAsset>();

                // Search for spawn position — match SpawnMarkerComponent.PlayerId
                FPVector2 spawnPos = FindSpawnPosition(ref frame, character.PlayerId);

                ref var transform = ref frame.Get<TransformComponent>(entity);
                transform.Position = new FPVector3(spawnPos.x, rules.CharacterSpawnY, spawnPos.y);
                transform.TeleportTick = frame.Tick;
                frame.RefreshPreviousTransform(entity);

                ref var physics = ref frame.Get<PhysicsBodyComponent>(entity);
                physics.RigidBody.velocity = FPVector3.Zero;
                physics.RigidBody.isStatic = false;

                if (frame.Has<KnockbackComponent>(entity))
                    frame.Remove<KnockbackComponent>(entity);

                character.KnockbackPower = 0;
                character.IsDead = false;
            }
        }

        FPVector2 FindSpawnPosition(ref Frame frame, int playerId)
        {
            var markerFilter = frame.Filter<SpawnMarkerComponent>();
            while (markerFilter.Next(out var marker))
            {
                ref readonly var sm = ref frame.GetReadOnly<SpawnMarkerComponent>(marker);
                if (sm.PlayerId == playerId)
                    return sm.SpawnPosition;
            }
            return FPVector2.Zero;
        }
    }
}
