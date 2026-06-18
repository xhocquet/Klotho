using System.Collections.Generic;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;

namespace xpTURN.Samples.SdSample
{
    // Deterministic world construction shared by the Unity client and the dedicated server.
    // Both sides must build an identical initial world, so this lives in the engine-free Sim assembly.
    public static class SdSimSetup
    {
        public static void RegisterSystems(EcsSimulation simulation)
        {
            var events = new EventSystem();
            simulation.AddSystem(new CommandSystem(),    SystemPhase.PreUpdate);
            simulation.AddSystem(new MovementSystem(),   SystemPhase.PreUpdate);

            // Engine gravity + static ground collider. The ground is registered HERE (not in
            // OnInitializeWorld, which the ServerDriven client skips) so the server and the client
            // build the identical static BVH — engine gravity + ground resting-contact is then
            // deterministic across the .NET server and the Unity client.
            var physics = new PhysicsSystem(64);
            physics.LoadStaticColliders("", new List<FPStaticCollider> { CreateGroundCollider() });
            simulation.AddSystem(physics, SystemPhase.Update);

            simulation.AddSystem(new RespawnSystem(),    SystemPhase.LateUpdate);
            simulation.AddSystem(new ScoreSystem(),      SystemPhase.LateUpdate);
            simulation.AddSystem(events,                 SystemPhase.LateUpdate);
        }

        // Ground static collider — 10×0.2×10 box at Y=-0.1 (top face at Y=0). Fixed stage geometry.
        static FPStaticCollider CreateGroundCollider() => new FPStaticCollider
        {
            id = 1,
            isTrigger = false,
            restitution = FP64.Zero,
            friction = FP64.FromFloat(0.5f),
            collider = FPCollider.FromBox(new FPBoxShape(
                halfExtents: new FPVector3(FP64.FromInt(5), FP64.FromFloat(0.1f), FP64.FromInt(5)),
                position: new FPVector3(FP64.Zero, FP64.FromFloat(-0.1f), FP64.Zero))),
        };

        public static void InitializeWorld(IKlothoEngine engine, int maxPlayers)
        {
            var frame = engine.InitFrame;
            var stats = frame.AssetRegistry.Get<PlayerStatsAsset>();

            for (int i = 0; i < maxPlayers; i++)
            {
                FP64 offsetX = (i == 0) ? -stats.InitialSpawnOffsetX : stats.InitialSpawnOffsetX;
                FPVector3 initialPos = new FPVector3(
                    stats.SpawnPoint.x + offsetX,
                    stats.SpawnPoint.y,
                    stats.SpawnPoint.z);
                FPVector3 halfExt = new FPVector3(stats.PlayerHalfExtent, stats.PlayerHalfExtent, stats.PlayerHalfExtent);

                var entity = frame.CreateEntity();
                frame.Add(entity, new TransformComponent
                {
                    Position = initialPos,
                    Rotation = FP64.Zero,
                    Scale = FPVector3.One,
                });
                frame.Add(entity, new PhysicsBodyComponent
                {
                    RigidBody = FPRigidBody.CreateDynamic(stats.PlayerMass),
                    Collider = FPCollider.FromBox(new FPBoxShape(halfExt, FPVector3.Zero)),
                    ColliderOffset = FPVector3.Zero,
                });
                // ServerDriven assigns network playerId 1..MaxPlayers (the server reserves id 0),
                // so entities use the same 1-based id to match incoming MoveCommand.PlayerId.
                frame.Add(entity, new PlayerComponent
                {
                    PlayerId = i + 1,
                    Score = 0,
                    LastInputH = FP64.Zero,
                    LastInputV = FP64.Zero,
                });
            }

            // Static ground collider is registered in RegisterSystems (runs on both server and SD client).
        }
    }
}
