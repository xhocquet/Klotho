using xpTURN.Klotho.Logging;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.FSM;
using xpTURN.Klotho.ECS.Systems;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.Deterministic.Random;
using System.Collections.Generic;

namespace Brawler
{
    public static class BrawlerSimSetup
    {

        const ulong BotSpawnFeatureKey = 0x424F5453504157EDUL; // "BOTSPAWN"

        /// <summary>
        /// Creates the initial world entities at game start.
        /// Since _frame.Clear() is invoked after engine.Initialize(),
        /// this must be called from OnGameStart(), not RegisterSystems().
        /// </summary>
        public static void InitializeWorldState(xpTURN.Klotho.Core.IKlothoEngine engine,
                                                int maxPlayers = 4,
                                                int botCount = 0)
        {
            var frame = engine.InitFrame;

            // Global timer / game-over state singleton
            var timerEntity = frame.CreateEntity();
            frame.Add(timerEntity, new GameTimerStateComponent
            {
                StartTick = -1,
                LastReportedSeconds = -1,
                GameOverFired = false,
            });

            // Moving platform
            var platformEntity = frame.CreateEntity();
            frame.Add(platformEntity, new TransformComponent { Position = new FPVector3(-16.0f, 0.0f, -16.0f) });
            var platformRb = FPRigidBody.CreateKinematic();
            frame.Add(platformEntity, new PhysicsBodyComponent
            {
                RigidBody = platformRb,
                Collider  = FPCollider.FromBox(new FPBoxShape(
                    new FPVector3(FP64.FromDouble(2.0), FP64.FromDouble(0.125), FP64.FromDouble(2.0)), // Half Extents
                    FPVector3.Zero)),
            });
            frame.Add(platformEntity, new PlatformComponent
            {
                IsMoving       = true,
                Waypoint0      = new FPVector3(-16.0f, 0.0f, -16.0f),
                Waypoint1      = new FPVector3(+16.0f, 0.0f, -16.0f),
                Waypoint2      = new FPVector3(+16.0f, 0.0f, +16.0f),
                Waypoint3      = new FPVector3(-16.0f, 0.0f, +16.0f),
                WaypointIndex  = 0,
                MoveSpeed      = FP64.FromDouble(0.1),
                MoveProgress   = FP64.Zero,
            });

            // Bot spawn — place bots beyond the player range
            if (botCount > 0)
                SpawnBots(ref frame, maxPlayers, botCount,
                          frame.GetReadOnlySingleton<RandomSeedComponent>().Seed);
        }

        // PlayerId range invariant.
        //   Real players (P2P): [0, maxPlayers]      — host=0 + guest LateJoin can reach maxPlayers
        //                                              under sparse Pre-GameStart distributions.
        //   Real players (SD):  [1, maxPlayers]      — server has no slot.
        //   Bots:               [maxPlayers+1, ...]  — strictly above the player range, so bot[i] never
        //                                              collides with a LateJoiner that lands on maxPlayers.
        static void SpawnBots(ref Frame frame, int maxPlayers, int botCount, ulong worldSeed)
        {
            var rules = frame.AssetRegistry.Get<BrawlerGameRulesAsset>();
            var stats  = new CharacterStatsAsset[4];
            for (int i = 0; i < 4; i++)
                stats[i] = frame.AssetRegistry.Get<CharacterStatsAsset>(1100 + i);

            var rng = DeterministicRandom.FromSeed(worldSeed, BotSpawnFeatureKey);

            for (int i = 0; i < botCount; i++)
            {
                int botPlayerId = maxPlayers + 1 + i;

                int classIdx = rng.NextIntInclusive(0, stats.Length - 1);
                var spawnPos = rules.SpawnPositions[botPlayerId % rules.SpawnPositions.Length];
                EntityRef entity = classIdx switch
                {
                    0 => frame.CreateEntity(new WarriorPrototype { SpawnPosition = spawnPos }),
                    1 => frame.CreateEntity(new MagePrototype    { SpawnPosition = spawnPos }),
                    2 => frame.CreateEntity(new RoguePrototype   { SpawnPosition = spawnPos }),
                    3 => frame.CreateEntity(new KnightPrototype  { SpawnPosition = spawnPos }),
                    _ => throw new System.ArgumentOutOfRangeException(nameof(classIdx)),
                };

                ref var character = ref frame.Get<CharacterComponent>(entity);
                character.PlayerId       = botPlayerId;
                character.StockCount     = 3;

                ref var owner = ref frame.Get<OwnerComponent>(entity);
                owner.OwnerId = botPlayerId;

                frame.Add(entity, new BotComponent
                {
                    State      = (byte)BotStateId.Idle,
                    Difficulty = (byte)BotDifficulty.Easy,
                });

                HFSMManager.Init(ref frame, entity, BotHFSMRoot.Id);

                var marker = frame.CreateEntity();
                frame.Add(marker, new SpawnMarkerComponent
                {
                    PlayerId      = botPlayerId,
                    SpawnPosition = new FPVector2(spawnPos.x, spawnPos.z),
                });
            }
        }

        public static void RegisterSystems(EcsSimulation simulation, IKLogger logger,
                                           List<IDataAsset> dataAssets = null,
                                           List<FPStaticCollider> staticColliders = null,
                                           BotFSMSystem botFSMSystem = null)
        {
            // Register assets
            if (dataAssets != null)
            {
                var registry = (IDataAssetRegistryBuilder)simulation.Frame.AssetRegistry;
                registry.LoadMixedAndRegister(dataAssets);
            }

            // Register prototypes
            simulation.Frame.Prototypes.Register(WarriorPrototype.Id, new WarriorPrototype());
            simulation.Frame.Prototypes.Register(MagePrototype.Id, new MagePrototype());
            simulation.Frame.Prototypes.Register(RoguePrototype.Id, new RoguePrototype());
            simulation.Frame.Prototypes.Register(KnightPrototype.Id, new KnightPrototype());
            simulation.Frame.Prototypes.Register(MovingPlatformPrototype.Id, new MovingPlatformPrototype());
            simulation.Frame.Prototypes.Register(ItemPickupPrototype.Id, new ItemPickupPrototype());

            var events = new EventSystem();
            var platformerCommandSystem = new PlatformerCommandSystem(events);

            if (botFSMSystem != null)
                botFSMSystem.SetCommandSystem(platformerCommandSystem);

            // PreUpdate — bots, then command processing.
            // PreviousPosition / PreviousRotation are captured by the engine built-in pass.
            if (botFSMSystem != null)
                simulation.AddSystem(botFSMSystem, SystemPhase.PreUpdate);
            simulation.AddSystem(platformerCommandSystem, SystemPhase.PreUpdate);

            // Update — simulation systems
            simulation.AddSystem(new ObstacleMovementSystem(events), SystemPhase.Update);
            simulation.AddSystem(new TopdownMovementSystem(events), SystemPhase.Update);
            simulation.AddSystem(new ActionLockSystem(), SystemPhase.Update);
            simulation.AddSystem(new KnockbackSystem(events), SystemPhase.Update);
            var physicsSystem = new PhysicsSystem(256, FPVector3.Zero);
            physicsSystem.SetSkipStaticGroundResponse(true);
            if (staticColliders != null)
                physicsSystem.LoadStaticColliders("BrawlerScene", staticColliders);
            simulation.AddSystem(physicsSystem, SystemPhase.Update);
            platformerCommandSystem.SetRayCaster(physicsSystem);
            if (botFSMSystem != null)
                botFSMSystem.SetRayCaster(physicsSystem);
            simulation.AddSystem(new TrapTriggerSystem(physicsSystem, events), SystemPhase.Update);
            simulation.AddSystem(new SkillCooldownSystem(events), SystemPhase.Update);
            simulation.AddSystem(new BoundaryCheckSystem(events), SystemPhase.Update);
            simulation.AddSystem(new ItemSpawnSystem(events), SystemPhase.Update);
            simulation.AddSystem(new CombatSystem(events), SystemPhase.Update);
            simulation.AddSystem(new RespawnSystem(events), SystemPhase.Update);
            simulation.AddSystem(new TimerSystem(events), SystemPhase.Update);

            // PostUpdate — landing clamp, then game-over detection
            simulation.AddSystem(new GroundClampSystem(physicsSystem), SystemPhase.PostUpdate);
            simulation.AddSystem(new GameOverSystem(events), SystemPhase.PostUpdate);

            // LateUpdate — event dispatch
            simulation.AddSystem(events, SystemPhase.LateUpdate);
        }

        /// <summary>
        /// Creates the default DataAsset list for tests. Includes assets required by every system's OnInit/Update.
        /// </summary>
        public static List<IDataAsset> CreateDefaultDataAssets()
        {
            int[] skillIds = { 1200, 1201, 1210, 1211, 1220, 1221, 1230, 1231 };
            var assets = new List<IDataAsset>
            {
                new BrawlerGameRulesAsset(),
                new CombatPhysicsAsset(),
                new BasicAttackConfigAsset(),
                new ItemConfigAsset(),
                new MovementPhysicsAsset(),
                new BotBehaviorAsset(),
                new BotDifficultyAsset(1700),
                new BotDifficultyAsset(1701),
                new BotDifficultyAsset(1702),
            };
            for (int c = 0; c < 4; c++)
            {
                assets.Add(new CharacterStatsAsset(1100 + c)
                {
                    Skill0Id = skillIds[c * 2],
                    Skill1Id = skillIds[c * 2 + 1],
                });
                assets.Add(new SkillConfigAsset(skillIds[c * 2]));
                assets.Add(new SkillConfigAsset(skillIds[c * 2 + 1]));
            }
            return assets;
        }

    }
}
