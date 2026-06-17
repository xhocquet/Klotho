using System;

using UnityEngine;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;
using System.Collections.Generic;
using xpTURN.Klotho.Deterministic.Physics;

namespace Brawler
{
    public class BrawlerSimulationCallbacks
        : ISimulationCallbacks, INavMeshProvider, INavAgentProvider, IFPPhysicsProviderSource
    {
        private readonly BrawlerInputCapture _input;
        private readonly List<FPStaticCollider> _staticColliders;
        private readonly FPNavMesh _navMesh;
        private readonly List<IDataAsset> _dataAssets;
        private readonly int _maxPlayers;
        private readonly int _botCount;

        private IKlothoEngine _engine;
        private EcsSimulation _simulation;

        // Outstanding reliable handle for the local player's SpawnCharacterCommand. Resolved either by
        // wire-level Duplicate ack (framework) or by state-driven Confirm() (OnPollInput when the
        // character entity becomes visible in the simulation frame).
        private IReliableCommandHandle _spawnHandle;

        // Bound delegate cached once in ctor — retry path reuses the same delegate instance, avoiding
        // per-retry closure allocation. Factory acquires a fresh CommandPool instance each invocation
        // so InputBuffer never holds two slots referencing the same cmd.
        private Func<ICommand> _spawnBuilder;

        public FPNavMesh NavMesh { get { return _navMesh; } }
        public FPNavMeshQuery NavQuery { get; private set; }
        public BotFSMSystem BotFSMSystem { get; private set; }

        public INavAgentSnapshotProvider NavAgentSnapshotProvider => BotFSMSystem;
        public IFPPhysicsWorldProvider PhysicsProvider => _simulation?.GetSystem<PhysicsSystem>();

        public BrawlerSimulationCallbacks(BrawlerInputCapture input,
                                          List<FPStaticCollider> colliders,
                                          FPNavMesh navMesh,
                                          int maxPlayers,
                                          int botCount,
                                          List<IDataAsset> dataAssets = null)
        {
            _input = input;
            _staticColliders = colliders;
            _navMesh = navMesh;
            _dataAssets = dataAssets;

            _maxPlayers = maxPlayers;
            _botCount = botCount;

            // Cache bound delegate once — retry path reuses this instance, no per-call closure alloc.
            _spawnBuilder = BuildSpawnCommand;
        }

        public void RegisterSystems(EcsSimulation simulation)
        {
            _simulation = simulation;

            BotFSMSystem botFSMSystem = null;

            var query       = new FPNavMeshQuery(_navMesh, null);
            var pathfinder  = new FPNavMeshPathfinder(_navMesh, query, null);
            var funnel      = new FPNavMeshFunnel(_navMesh, query, null);
            var agentSystem = new FPNavAgentSystem(_navMesh, query, pathfinder, funnel, null);
            agentSystem.SetAvoidance(new FPNavAvoidance());

            botFSMSystem = new BotFSMSystem(agentSystem);
            botFSMSystem.SetQuery(query);

            NavQuery = query;
            BotFSMSystem = botFSMSystem;

            BrawlerSimSetup.RegisterSystems(
                simulation,
                simulation.Frame.Logger,
                _dataAssets,
                _staticColliders,
                botFSMSystem
            );
        }

        public void OnInitializeWorld(IKlothoEngine engine)
        {
            BrawlerSimSetup.InitializeWorldState(engine, _maxPlayers, _botCount);
        }

        public void OnPollInput(int playerId, int tick, ICommandSender sender)
        {
            if (_engine == null) return;

            // ECS frame is the single source of truth — listener-pattern flags are vulnerable to rollback noise.
            var frame = ((EcsSimulation)_engine.Simulation).Frame;
            if (!HasOwnCharacter(frame, playerId))
            {
                // Framework's reliability tracker handles retry / escalation / fault injection for the
                // outstanding spawn cmd. Emit an empty-move filler only when it does not collide with
                // the outstanding spawn cmd's target slot (single cmd per (tick, playerId)).
                if (_spawnHandle != null && !_spawnHandle.WouldCollideAt(tick))
                {
                    var emptyInput = CommandPool.Get<PlayerInputCommand>();
                    emptyInput.PlayerId = playerId;
                    emptyInput.Buttons  = 0;   // no movement/action intent; dispatch is a no-op until the character exists
                    sender.Send(emptyInput);
                }
                return;
            }

            // Character exists — resolve the outstanding spawn handle (state-driven ack, faster than
            // waiting for the server's Duplicate reject round-trip).
            if (_spawnHandle != null && !_spawnHandle.IsResolved)
            {
                _spawnHandle.Confirm();
                _spawnHandle = null;
            }

            // Single unified per-tick input (InputCommand sets Tick to CurrentTick+InputDelay). Move +
            // jump + attack + skill packed into one (tick, playerId) slot. Attack and skill are NOT
            // mutually exclusive — both fire if pressed the same tick.
            bool useSkill = _input.SkillSlot >= 0;

            byte buttons = PlayerInputCommand.HAS_MOVE_BIT;   // human always carries movement intent (neutral = stop)
            if (_input.Jump)             buttons |= PlayerInputCommand.JUMP_PRESSED_BIT;
            if (_input.JumpHeld)         buttons |= PlayerInputCommand.JUMP_HELD_BIT;
            if (_input.Attack)           buttons |= PlayerInputCommand.ATTACK_BIT;
            if (useSkill)              { buttons |= PlayerInputCommand.HAS_SKILL_BIT;
                                         if (_input.SkillSlot == 1) buttons |= PlayerInputCommand.SKILL_SLOT_BIT; }

            var cmd = CommandPool.Get<PlayerInputCommand>();
            cmd.PlayerId       = playerId;
            cmd.HorizontalAxis = _input.H;
            cmd.VerticalAxis   = _input.V;
            cmd.Buttons        = buttons;
            // Set every serialized field each send — pooled instances reuse stale data fields. Aim is
            // serialized unconditionally, so default it to zero when there is no attack/skill.
            cmd.AimDirection   = (_input.Attack || useSkill)
                               ? (GetNearestEnemyDirection(playerId) ?? _input.AimDirection)
                               : FPVector2.Zero;
            sender.Send(cmd);

            // Consume event-style input (send only once)
            _input.ConsumeOneShot();
        }

        public void SetEngine(IKlothoEngine engine)
        {
            _engine = engine;
        }

        private static bool HasOwnCharacter(Frame frame, int playerId)
        {
            var filter = frame.Filter<OwnerComponent, CharacterComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var owner = ref frame.GetReadOnly<OwnerComponent>(entity);
                if (owner.OwnerId == playerId) return true;
            }
            return false;
        }

        private FPVector2? GetNearestEnemyDirection(int playerId)
        {
            var frame = ((EcsSimulation)_engine.Simulation).Frame;

            // Find my position
            FPVector3 selfPos = default;
            bool found = false;
            var selfFilter = frame.Filter<TransformComponent, OwnerComponent, CharacterComponent>();
            while (selfFilter.Next(out var e))
            {
                ref readonly var o = ref frame.GetReadOnly<OwnerComponent>(e);
                if (o.OwnerId != playerId) continue;
                ref readonly var c = ref frame.GetReadOnly<CharacterComponent>(e);
                if (c.IsDead) continue;
                selfPos = frame.GetReadOnly<TransformComponent>(e).Position;
                found = true;
                break;
            }
            if (!found) return null;

            // Search for nearest enemy
            FP64 minDistSqr = FP64.MaxValue;
            FPVector2 bestDir = default;
            bool hasTarget = false;
            var filter = frame.Filter<TransformComponent, OwnerComponent, CharacterComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var owner = ref frame.GetReadOnly<OwnerComponent>(entity);
                if (owner.OwnerId == playerId) continue;
                ref readonly var ch = ref frame.GetReadOnly<CharacterComponent>(entity);
                if (ch.IsDead) continue;
                ref readonly var tr = ref frame.GetReadOnly<TransformComponent>(entity);
                FP64 dx = tr.Position.x - selfPos.x;
                FP64 dz = tr.Position.z - selfPos.z;
                FP64 distSqr = dx * dx + dz * dz;
                if (distSqr < minDistSqr)
                {
                    minDistSqr = distSqr;
                    FP64 len = FP64.Sqrt(distSqr);
                    bestDir = len > FP64.Zero
                        ? new FPVector2(dx / len, dz / len)
                        : FPVector2.Zero;
                    hasTarget = true;
                }
            }
            return hasTarget && bestDir != FPVector2.Zero ? bestDir : null;
        }

        public void SendSpawnCommand(IKlothoEngine engine)
        {
            // Framework's reliability tracker owns retry / escalation / drop / log. SendSpawnCommand
            // is the one-shot entry point — initial send + handle creation. Subsequent retries run
            // inside the tracker via _spawnBuilder factory invocation.
            _spawnHandle = engine.IssueOnce(_spawnBuilder);
        }

        // Factory body invoked by the reliability tracker on initial send AND every retry. Each call
        // acquires a fresh CommandPool instance — framework takes ownership. Payload is re-evaluated
        // every invocation, so PlayerConfig arriving after the first attempt is picked up by retries.
        private ICommand BuildSpawnCommand()
        {
            int playerId = _engine.LocalPlayerId;
            var rules    = ((EcsSimulation)_engine.Simulation).Frame.AssetRegistry.Get<BrawlerGameRulesAsset>();
            int spawnIdx = playerId % rules.SpawnPositions.Length;
            FPVector3 pos = rules.SpawnPositions[spawnIdx];

            // Query character selection from local player's BrawlerPlayerConfig (network-shared data).
            // If PlayerConfig has not arrived yet, fallback to 0 (Warrior) — next retry re-evaluates.
            var playerConfig = _engine.GetPlayerConfig<BrawlerPlayerConfig>(playerId);

            var cmd = CommandPool.Get<SpawnCharacterCommand>();
            cmd.CharacterClass = playerConfig?.SelectedCharacterClass ?? 0;
            cmd.SpawnPosition  = new FPVector2(pos.x, pos.z);
            return cmd;
        }
    }
}
