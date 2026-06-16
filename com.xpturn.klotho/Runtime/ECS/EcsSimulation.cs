using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// ISimulation implementation — owns Frame + SystemRunner.
    /// Can be injected via the ISimulation interface without modifying KlothoEngine code.
    /// </summary>
    public class EcsSimulation : ISimulation
    {
        private IKLogger _logger;
        private Frame _frame;
        private readonly SystemRunner _systemRunner;
        private readonly FrameRingBuffer _ringBuffer;
        private readonly int _deltaTimeMs;
        private byte[] _hashBuffer = Array.Empty<byte>();
        private IDataAssetRegistryBuilder _registryBuilder;

        public int CurrentTick => _frame.Tick;

        // Reused payload for GetActiveMatchEnd — callers (resync backstop) do not retain it.
        private readonly MatchEndStatePayload _matchEndPayload = new MatchEndStatePayload();

        /// <inheritdoc />
        public bool IsMatchEndedState
        {
            // TryGetSingleton (NOT GetSingleton): the singleton is absent in spectator/pre-init and on
            // an SD client before its first FullState — absence means "not ended", never throw.
            get => _frame.TryGetSingleton<MatchEndStateComponent>(out var e)
                   && _frame.GetReadOnly<MatchEndStateComponent>(e).Ended;
        }

        /// <inheritdoc />
        public xpTURN.Klotho.Core.IMatchEndEvent GetActiveMatchEnd()
        {
            if (!_frame.TryGetSingleton<MatchEndStateComponent>(out var e))
                return null;
            ref readonly var c = ref _frame.GetReadOnly<MatchEndStateComponent>(e);
            if (!c.Ended)
                return null;
            _matchEndPayload.WinnerPlayerId = c.WinnerPlayerId;
            return _matchEndPayload;
        }

        // Engine-agnostic IMatchEndEvent backing the backstop (EcsSimulation cannot build a game event
        // type). Carries the winner from MatchEndStateComponent; Reason is a sentinel ("resync") because
        // the original game reason is not persisted in state — the game subtype is not
        // preserved on the backstop path, which is sufficient for the one-way termination notification.
        private sealed class MatchEndStatePayload : xpTURN.Klotho.Core.IMatchEndEvent
        {
            private static readonly FixedString32 s_resyncReason = FixedString32.FromString("resync");
            public int WinnerPlayerId;
            int xpTURN.Klotho.Core.IMatchEndEvent.WinnerPlayerId => WinnerPlayerId;
            FixedString32 xpTURN.Klotho.Core.IMatchEndEvent.Reason => s_resyncReason;
        }

        /// <summary>
        /// Scenario C: when &gt;= 0, XORs a salt into the hash of the named execution tick
        /// (evaluated at _frame.Tick == value+1, the post-execution hash point) so an armed SD client's
        /// resim mismatches the server's verified hash, driving a desync-resync FullState request. -1
        /// disables. Set by the engine only on the targeted client, never the server. Hash-only and
        /// tick-gated, so a FullState restore past the tick auto-recovers.
        ///
        /// <para>Field is compiled UNCONDITIONALLY (not under <c>#if KLOTHO_FAULT_INJECTION</c>) so the
        /// EcsSimulation serialization layout is identical between the Editor and a player build whose
        /// Scripting Define Symbols differ — otherwise the player build fails with "script class layout
        /// is incompatible between the editor and the player". The salt is still applied only under the
        /// define (see GetStateHash) and the field is only set by the engine under the define, so it is
        /// inert (default -1) in non-fault-injection builds.</para>
        /// </summary>
        public int ForceDesyncHashTick = -1;

        public EcsSimulation(int maxEntities, int maxRollbackTicks = 10, int deltaTimeMs = 50, IKLogger logger = null, IDataAssetRegistryBuilder registryBuilder = null, IDataAssetRegistry assetRegistry = null)
        {
            if (assetRegistry != null && registryBuilder != null)
                throw new ArgumentException("Cannot specify both assetRegistry and registryBuilder.");

            _logger = logger;
            if (assetRegistry != null)
            {
                _registryBuilder = null;
                _frame = new Frame(maxEntities, _logger, assetRegistry);
            }
            else
            {
                _registryBuilder = registryBuilder ?? new DataAssetRegistry();
                _frame = new Frame(maxEntities, _logger, _registryBuilder);
            }
            _systemRunner = new SystemRunner();
            _ringBuffer = new FrameRingBuffer(maxRollbackTicks, maxEntities, _logger);
            _deltaTimeMs = deltaTimeMs;
        }

        public void LockAssetRegistry()
        {
            if (_registryBuilder == null) return;
            _frame.SetRegistry(_registryBuilder.Build());
        }

        private readonly List<ISnapshotParticipant> _snapshotParticipants = new();

        /// <summary>
        /// Registers a system. Adds the desired system from outside to match the Phase.
        /// </summary>
        public void AddSystem(object system, SystemPhase phase)
        {
            _systemRunner.AddSystem(system, phase);
            if (system is ISnapshotParticipant sp)
                _snapshotParticipants.Add(sp);
        }

        /// <summary>
        /// Returns the first registered system instance assignable to <typeparamref name="T"/>,
        /// or <c>null</c> if none. Useful when a callback boundary needs to expose a system's
        /// secondary interface (e.g. <c>IFPPhysicsWorldProvider</c> implemented by
        /// <c>PhysicsSystem</c>) without storing a process-wide static reference.
        /// </summary>
        public T GetSystem<T>() where T : class => _systemRunner.Find<T>();

        /// <summary>
        /// Returns true and assigns <paramref name="system"/> if a matching system is registered.
        /// </summary>
        public bool TryGetSystem<T>(out T system) where T : class
        {
            system = _systemRunner.Find<T>();
            return system != null;
        }

        /// <summary>
        /// Appends every registered system instance assignable to <typeparamref name="T"/>
        /// into <paramref name="buffer"/>. Returns the appended count.
        /// </summary>
        public int GetSystems<T>(List<T> buffer) where T : class
            => _systemRunner.FindAll(buffer);

        public void Initialize()
        {
            _frame.Clear();
            _frame.Tick = 0;
            _frame.DeltaTimeMs = _deltaTimeMs;
            _frame.OnEntityCreated   = entity => _systemRunner.OnEntityCreated(ref _frame, entity);
            _frame.OnEntityDestroyed = entity => _systemRunner.OnEntityDestroyed(ref _frame, entity);
            _systemRunner.Init(ref _frame);
        }

        public void Tick(List<ICommand> commands)
        {
            _frame.DeltaTimeMs = _deltaTimeMs;

            // Phase.PreUpdate: apply commands
            for (int i = 0; i < commands.Count; i++)
            {
                var cmd = commands[i];
                _systemRunner.RunCommandSystems(ref _frame, cmd);

                if (cmd is Core.PlayerJoinCommand joinCmd)
                    OnPlayerJoined(joinCmd.JoinedPlayerId, _frame.Tick);
            }

            // Phase.Update → PostUpdate → LateUpdate
            _systemRunner.RunUpdateSystems(ref _frame);

            _frame.Tick++;
        }

        public void Rollback(int targetTick)
        {
            _ringBuffer.RestoreFrame(targetTick, _frame);
            if (_snapshotParticipants.Count > 0)
                _ringBuffer.RestoreSystemState(targetTick, _snapshotParticipants);
        }

        /// <summary>
        /// Copies the live frame + participating system state into a caller-owned side buffer,
        /// outside the rollback ring (the ring stores pre-tick snapshots only). SyncTest uses
        /// this to keep the post-forward state so a failed check can restore the forward branch
        /// instead of leaving the diverged resim branch live.
        /// </summary>
        public void CaptureStateTo(Frame targetFrame, ref byte[] systemStateBuffer)
        {
            targetFrame.CopyFrom(_frame);
            if (_snapshotParticipants.Count == 0) return;

            int totalSize = 0;
            for (int i = 0; i < _snapshotParticipants.Count; i++)
                totalSize += _snapshotParticipants[i].GetSnapshotSize();

            if (systemStateBuffer == null || systemStateBuffer.Length < totalSize)
                systemStateBuffer = new byte[totalSize];

            var writer = new SpanWriter(systemStateBuffer);
            for (int i = 0; i < _snapshotParticipants.Count; i++)
                _snapshotParticipants[i].SaveSnapshot(ref writer);
        }

        /// <summary>
        /// Restores the live frame + participating system state from a side buffer previously
        /// filled by <see cref="CaptureStateTo"/>.
        /// </summary>
        public void RestoreStateFrom(Frame sourceFrame, byte[] systemStateBuffer)
        {
            _frame.CopyFrom(sourceFrame);
            if (_snapshotParticipants.Count == 0 || systemStateBuffer == null) return;

            var reader = new SpanReader(systemStateBuffer);
            for (int i = 0; i < _snapshotParticipants.Count; i++)
                _snapshotParticipants[i].RestoreSnapshot(ref reader);
        }

        public long GetStateHash()
        {
            ulong frameHash = _frame.CalculateHash();
            ulong hash = frameHash;

            if (_snapshotParticipants.Count > 0)
            {
                int sysSize = 0;
                for (int i = 0; i < _snapshotParticipants.Count; i++)
                    sysSize += _snapshotParticipants[i].GetSnapshotSize();

                if (_hashBuffer.Length < sysSize)
                    _hashBuffer = new byte[sysSize];

                var writer = new SpanWriter(_hashBuffer);
                for (int i = 0; i < _snapshotParticipants.Count; i++)
                    _snapshotParticipants[i].SaveSnapshot(ref writer);

                hash = FPHash.HashBytes(hash, new ReadOnlySpan<byte>(_hashBuffer, 0, writer.Position));
            }

#if KLOTHO_FAULT_INJECTION
            // Scenario C: salt the hash of one execution tick on the armed client so its
            // resim diverges from the server's verified hash (determinism-failure path → FullStateRequest).
            // The verified compare hashes after Tick() increments, so the post-execution hash of tick N
            // is taken at _frame.Tick == N+1. Hash-only (no state mutation) + monotonic tick gate → a
            // FullState restore past N stops the salt and recovery is clean.
            if (ForceDesyncHashTick >= 0 && _frame.Tick == ForceDesyncHashTick + 1)
                hash ^= 0x5DE59C00DEADF00DUL;
#endif

            return (long)hash;
        }

        public void Reset()
        {
            _frame.Clear();
            _frame.Tick = 0;
            _frame.DeltaTimeMs = _deltaTimeMs;
        }

        // Diagnostic — delegates to Frame for per-component hash dump.
        public void LogComponentHashes(IKLogger logger, string label, KLogLevel logLevel = KLogLevel.Debug)
            => _frame.LogComponentHashes(logger, label, logLevel);

        // Diagnostic — one-line fingerprint of the registered static geometry (count + per-collider
        // fields). Static colliders are intentionally not part of the state hash or snapshot, so this
        // log is the only way a server/client log diff surfaces a static mismatch at setup time.
        public void LogStaticFingerprint(IKLogger logger, string label, KLogLevel logLevel = KLogLevel.Information)
        {
            if (logger == null || !logger.IsEnabled(logLevel)) return;
            var svc = GetSystem<IStaticColliderService>();
            if (svc == null) return;
            svc.GetStaticColliders(out _, out int count);
            long fp = svc.GetStaticFingerprint();
            logger.Log(logLevel, $"[Physics][StaticGeometry] {label}: count={count} fp=0x{fp:X16}", null);
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        public void SnapshotHashesToQueue() => _frame.SnapshotHashesToQueue();
        public void FlushHashHistory(IKLogger logger, int dumpTick) => _frame.FlushHashHistory(logger, dumpTick);
#endif

        public void RestoreFromFullState(byte[] stateData)
        {
            if (_snapshotParticipants.Count == 0)
            {
                _frame.DeserializeFrom(stateData);
            }
            else
            {
                var reader = new SpanReader(stateData);
                int frameLen = reader.ReadInt32();
                byte[] frameData = reader.ReadRawBytes(frameLen).ToArray();
                _frame.DeserializeFrom(frameData);
                for (int i = 0; i < _snapshotParticipants.Count; i++)
                    _snapshotParticipants[i].RestoreSnapshot(ref reader);
            }
            _ringBuffer.Clear();
        }

        public byte[] SerializeFullState()
        {
            byte[] frameData = _frame.SerializeTo();
            if (_snapshotParticipants.Count == 0)
                return frameData;

            int sysSize = 0;
            for (int i = 0; i < _snapshotParticipants.Count; i++)
                sysSize += _snapshotParticipants[i].GetSnapshotSize();

            byte[] combined = new byte[4 + frameData.Length + sysSize];
            var writer = new SpanWriter(combined);
            writer.WriteInt32(frameData.Length);
            writer.WriteRawBytes(frameData);
            for (int i = 0; i < _snapshotParticipants.Count; i++)
                _snapshotParticipants[i].SaveSnapshot(ref writer);

            return combined;
        }

        public (byte[] data, long hash) SerializeFullStateWithHash()
        {
            if (_snapshotParticipants.Count == 0)
            {
                var (d, h) = _frame.SerializeToWithHash();
                return (d, (long)h);
            }

            int sysSize = 0;
            for (int i = 0; i < _snapshotParticipants.Count; i++)
                sysSize += _snapshotParticipants[i].GetSnapshotSize();

            // Single buffer: [frameLengthPlaceholder(4)] [frameData] [participantData]
            int totalSize = 4 + _frame.EstimateSerializedSize() + sysSize;
            byte[] combined = new byte[totalSize];
            var writer = new SpanWriter(combined);

            // frameLength placeholder — patched after serialization
            writer.WriteInt32(0);
            int frameStart = writer.Position;

            // Direct Frame serialization + hash calculation
            ulong frameHash = _frame.SerializeWithHash(ref writer);
            int frameLength = writer.Position - frameStart;

            // Patch frameLength
            BinaryPrimitives.WriteInt32LittleEndian(
                new Span<byte>(combined, 0, 4), frameLength);

            // Serialize participants
            int participantStart = writer.Position;
            for (int i = 0; i < _snapshotParticipants.Count; i++)
                _snapshotParticipants[i].SaveSnapshot(ref writer);

            // Participant hash
            ulong hash = FPHash.HashBytes(frameHash,
                new ReadOnlySpan<byte>(combined, participantStart, writer.Position - participantStart));

            return (combined, (long)hash);
        }

        /// <summary>
        /// Saves the current tick's snapshot to the ring buffer.
        /// KlothoEngine calls this every tick.
        /// </summary>
        public void SaveSnapshot()
        {
            _ringBuffer.SaveFrame(_frame.Tick, _frame);
            if (_snapshotParticipants.Count > 0)
                _ringBuffer.SaveSystemState(_frame.Tick, _snapshotParticipants);
        }

        public bool HasSnapshot(int tick)
            => _ringBuffer.HasFrame(tick, _frame.Tick);

        /// <summary>
        /// Returns the frame reference for the specified tick from the ring buffer.
        /// </summary>
        public bool TryGetSnapshotFrame(int tick, out Frame frame)
            => _ringBuffer.TryGetFrame(tick, _frame.Tick, out frame);

        public int GetNearestRollbackTick(int targetTick) => GetNearestSnapshotTick(targetTick);

        public int GetNearestSnapshotTick(int targetTick)
            => _ringBuffer.GetNearestAvailableTick(targetTick, _frame.Tick);

        public void GetSavedSnapshotTicks(System.Collections.Generic.IList<int> output)
            => _ringBuffer.GetSavedTicks(_frame.Tick, output);

        public void ClearSnapshots()
            => _ringBuffer.Clear();

        public void EmitSyncEvents()
        {
            _systemRunner.EmitSyncEvents(ref _frame);
        }

        public event Action<int> OnPlayerJoinedNotification;

        public void OnPlayerJoined(int playerId, int tick)
        {
            OnPlayerJoinedNotification?.Invoke(playerId);
        }

        public int RollbackCapacity => _ringBuffer.Capacity;

        /// <summary>
        /// Direct ECS Frame access (for testing/debugging)
        /// </summary>
        public Frame Frame => _frame;
    }
}
