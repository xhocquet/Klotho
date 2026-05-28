using Cysharp.Threading.Tasks;
using UnityEngine;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho
{
    /// <summary>
    /// Abstract class responsible for view creation and BindBehaviour/ViewFlags determination.
    ///
    /// Initialization notes:
    /// - Do not query Engine information such as LocalPlayerId or IsServer in constructors/Awake/OnEnable. The engine may not be initialized yet.
    /// - Engine queries are only permitted inside TryGetBindBehaviour / GetViewFlags / CreateAsync. These methods are only called from the EVU.Reconcile path, which guarantees the engine is ready.
    ///
    /// Subclass contract:
    /// - Override ResolvePrefab to map entity components to prefab assets.
    /// - Override ShouldRender to filter which entities become Views.
    /// - The 5-flag BindBehaviour / ViewFlags matrix and the Pool-aware Create/Destroy paths
    ///   are implemented as virtual defaults below — override only for special needs.
    /// </summary>
    public abstract class EntityViewFactory : ScriptableObject
    {
        /// <summary>Injected by EVU at Initialize time.</summary>
        public IKlothoEngine Engine { get; private set; }

        /// <summary>
        /// View pool placed in the scene. Injected by EVU and used by subclass CreateAsync implementations.
        /// If null, the subclass calls Object.Instantiate directly without using a pool.
        /// </summary>
        public IEntityViewPool Pool { get; private set; }

        internal void Attach(IKlothoEngine engine, IEntityViewPool pool)
        {
            Engine = engine;
            Pool   = pool;
        }

        // ── Game-specific overrides (abstract) ─────────────────────────────────────

        /// <summary>
        /// Returns the prefab to spawn for this entity, or null to skip.
        /// Typical implementation: branch on component type (Character / Item / etc.)
        /// and return the corresponding SerializeField prefab.
        /// </summary>
        protected abstract GameObject ResolvePrefab(Frame frame, EntityRef entity);

        /// <summary>
        /// Whether this factory accepts this entity as a View target (spawn-time decision).
        /// Returning false causes EVU.Reconcile to skip view creation for this entity entirely
        /// (no Pool.Rent, no Instantiate). Called per-entity each Reconcile pass — keep cheap.
        /// Typical implementation:
        ///   frame.Has&lt;CharacterComponent&gt;(entity) || frame.Has&lt;ItemComponent&gt;(entity).
        /// </summary>
        protected abstract bool ShouldRender(Frame frame, EntityRef entity);

        // ── Framework default decisions (virtual — game can override if needed) ───

        /// <summary>
        /// Determines whether this entity should be rendered as a View and which BindBehaviour to use.
        /// Default implementation: delegates the "should it have a View" decision to ShouldRender,
        /// then resolves BindBehaviour from the 5-flag matrix (Mode / IsServer / IsReplayMode /
        /// IsSpectatorMode / OwnerId == LocalPlayerId).
        /// </summary>
        public virtual bool TryGetBindBehaviour(Frame frame, EntityRef entity, out BindBehaviour behaviour)
        {
            if (!ShouldRender(frame, entity))
            {
                behaviour = BindBehaviour.Verified;
                return false;
            }

            if (frame.Has<OwnerComponent>(entity))
            {
                ref readonly var owner = ref frame.GetReadOnly<OwnerComponent>(entity);
                behaviour = IsPredictedRender(owner.OwnerId)
                    ? BindBehaviour.NonVerified
                    : BindBehaviour.Verified;
                return true;
            }

            // Server-authoritative entity (no Owner) — Verified on SD-Client/Spectator, NonVerified otherwise.
            bool useVerified = UseVerifiedPath() && !Engine.IsReplayMode;
            behaviour = useVerified ? BindBehaviour.Verified : BindBehaviour.NonVerified;
            return true;
        }

        /// <summary>
        /// Computes per-entity ViewFlags (e.g. Snapshot Interpolation ON/OFF).
        /// Default: EnableSnapshotInterpolation on the Verified-path side, None on Predicted.
        /// </summary>
        public virtual ViewFlags GetViewFlags(Frame frame, EntityRef entity)
        {
            bool hasOwner = frame.Has<OwnerComponent>(entity);
            int  ownerId  = hasOwner ? frame.GetReadOnly<OwnerComponent>(entity).OwnerId : -1;

            bool useVerifiedPath = UseVerifiedPath() && !Engine.IsReplayMode;
            bool predictedRender = hasOwner
                ? !useVerifiedPath || (ownerId == Engine.LocalPlayerId)
                : !useVerifiedPath;

            return predictedRender ? ViewFlags.None : ViewFlags.EnableSnapshotInterpolation;
        }

        // ── Helpers (protected — reusable in custom overrides) ────────────────────

        /// <summary>
        /// True iff this entity should render as Predicted (local responsiveness).
        /// Replay (regardless of mode) OR non-Verified-path (P2P / SD-Server) → always Predicted.
        /// SD-Client / Spectator (Verified path, not replay) → local player owned = Predicted, others = Verified.
        /// </summary>
        protected bool IsPredictedRender(int ownerId)
            => !UseVerifiedPath() || Engine.IsReplayMode || (ownerId == Engine.LocalPlayerId);

        /// <summary>True iff this peer uses the Verified path (SD-Client or Spectator).</summary>
        protected bool UseVerifiedPath()
        {
            bool isSDClient = (Engine.SimulationConfig.Mode == NetworkMode.ServerDriven) && !Engine.IsServer;
            return isSDClient || Engine.IsSpectatorMode;
        }

        // ── Spawn / Destroy default (virtual) ─────────────────────────────────────

        /// <summary>
        /// Instantiates a prefab via Pool when available, falling back to Object.Instantiate.
        /// Returning null causes EVU to discard the spawn.
        /// </summary>
        public virtual async UniTask<EntityView> CreateAsync(Frame frame, EntityRef entity, BindBehaviour behaviour, ViewFlags flags)
        {
            var prefab = ResolvePrefab(frame, entity);
            if (prefab == null) return null;

            if (Pool != null) return await Pool.Rent(prefab);

            var go = Object.Instantiate(prefab);
            var view = go.GetComponent<EntityView>();
            if (view == null)
            {
                Object.Destroy(go);
                return null;
            }
            return view;
        }

        /// <summary>
        /// Returns the view to the Pool when available, falling back to Object.Destroy.
        /// </summary>
        public virtual void Destroy(EntityView view)
        {
            if (view == null) return;
            if (Pool != null) Pool.Return(view);
            else Object.Destroy(view.gameObject);
        }
    }
}
