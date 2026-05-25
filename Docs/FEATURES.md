# Klotho Framework Feature List

A deterministic multiplayer simulation framework for Unity.
Supports client-side prediction, rollback, and frame synchronization.

---

## Core

- **Tick-based simulation loop** — runs at a default 50 ms interval (20 ticks/sec)
- **ICommand-based input system** — serializable command interface (MoveCommand, ActionCommand, SkillCommand, etc.)
  - **ISystemCommand** — interface for system-only commands (PlayerJoinCommand, etc.)
  - **CommandBase** — abstract base class for commands
  - **StopCommand** — explicit "no movement, no action" intent emitted by clients during the `EndGracePolicy.Pause` grace window; SD/P2P unified
- **CommandFactory / CommandRegistry** — command type registration / construction / deserialization, integrated with the source generator
- **Client-side prediction** — predict missing inputs and execute without delay
- **Rollback & re-simulation** — ring-snapshot based; configurable max rollback ticks
- **Event system** — Predicted → Confirmed/Canceled lifecycle for SimulationEvent
  - Regular mode: emitted immediately
  - Synced mode: emitted only on verified ticks
  - EventBuffer / EventCollector / EventDispatcher — internal collection / dispatch
- **Hash-based desync detection** — engine-level local/remote hash comparison
- **SyncTestRunner** — GGPO-style determinism verification (snapshot → run → rollback → re-run → hash compare, no network)
- **SimulationConfig / ISimulationConfig** — tick interval, input delay, max rollback, sync-check interval, prediction toggle
- **SessionConfig / ISessionConfig** — session-init parameters (network mode, player info, etc.)
- **KlothoSession / IKlothoSession** — session lifecycle management (a wrapper around KlothoEngine)
  - **KlothoSessionSetup** — session-construction helper. Field-injected:
    - `CredentialsStore` (IReconnectCredentialsStore) — warm-reconnect save/clear, formerly wired by the game after Create
    - `LifecycleObserver` (IKlothoSessionObserver) — bulk-subscribed at Create and bulk-unsubscribed at Stop (replaces per-event `+=` wiring)
    - `AppVersion` / `DeviceIdProvider` — reconnect credential issuance inputs
  - **KlothoSession.CreateSpectator** — spectator-mode factory (takes `SpectatorSessionSetup` + `CallbacksFactory` that runs after server config arrives)
  - **SpectatorSessionSetup / SpectatorCallbacks** — spectator-only setup; no `SessionConfig`/`CredentialsStore` (server-authoritative arrival via `SpectatorAcceptMessage`)
  - **IKlothoSessionObserver** — aggregated session-level lifecycle callbacks (`OnPlayerDisconnected/Reconnected`, `OnReconnecting/Failed/Reconnected`, `OnCatchupComplete`, `OnResyncCompleted`, `OnGameStart`, `OnMatchAborted/Ended/Reset`, `OnSessionStopped`)
  - **ReconnectFailedException** — thrown by `KlothoConnectionAsync.ReconnectAsync` / `KlothoConnection.Reconnect` on server reject; carries the rejection `Reason` byte (see `ReconnectRejectReason`)
  - **ISimulationCallbacks** — engine-lifecycle callback interface
  - **Replay initial-state snapshot auto-inject** — `Engine.StartReplay` automatically replays `InitialStateSnapshot` from the metadata, removing the game-side `OnGameStart += InjectInitialStateSnapshot` wiring
  - **Pause-grace StopCommand auto-inject** — during the `EndGracePolicy.Pause` grace window, the engine emits the per-tick `StopCommand` automatically; games no longer hand-roll the grace-window command stream
  - **DynamicInputDelayPolicy** — built-in client-reactive PastTick + rollback-burst escalation policy (formerly hand-rolled in the sample); thresholds sourced from `SessionConfig`, attached automatically on non-host sessions
  - **KlothoSession state-change events** — `StateChanged` / `PhaseChanged` / `PlayerCountChanged` / `AllPlayersReadyChanged`. Replaces per-frame status polling — game code subscribes once on session creation. Backed by `KlothoEngine.OnStateChanged` and `IKlothoNetworkService.OnPhaseChanged` / `OnPlayerCountChanged` / `OnAllPlayersReadyChanged` (forwarded from both network-service and spectator-service paths)
  - **KlothoSession.PlayerCount** — unified read-only getter (`NetworkService → SpectatorService → 0` fallback) so host / guest / spectator all expose the same player-count surface
- **INetworkServiceReceiver** — opt-in marker interface for `ISimulationCallbacks` implementations that need the `IKlothoNetworkService` handle on host/guest entry. `KlothoSessionFlow.FireOnSessionCreated` dispatches `SetNetworkService` right after `OnSessionCreated` (kind-gated to Host/Guest, callbacks-non-null, `is INetworkServiceReceiver recv`). Implementations that don't need the handle simply omit the interface — no empty-body `SetNetworkService` required
- **KlothoSessionFlow / KlothoFlowSetup** — recommended session construction layer
  - 6 mode-dispatched entry points: `StartHost` / `JoinP2PAsync` / `JoinServerDrivenAsync` / `ReconnectAsync` / `SpectateAsync` / `StartReplayFromFile`. The umbrella `JoinAsync(transport, host, port, preJoin, roomId, sessionCfg, ct)` is retained as an `[Obsolete]` shim — scheduled removal: 0.3.0
  - **Per-mode session-created callbacks** — `OnHostSessionCreated` / `OnGuestSessionCreated` / `OnReplaySessionCreated` / `OnSpectatorSessionCreated` alongside the generic `OnSessionCreated`. Removes the `engine.IsReplayMode` / `engine.IsSpectatorMode` 2-flag mode-by-flag branching from game-side dispatch
  - **`InitialPlayerConfigFactory`** — auto `SendPlayerConfig` on guest / reconnect paths (skipped on spectator / replay). Invoked per-session so it always observes the latest user selection
  - **`SpectatorTransportFactory`** — invoked from `SpectateAsync(host, port, roomId, ct)` so the library owns the transport instance. The transport-injection overload remains as the escape hatch
  - **`StartReplayFromFile(path)`** — 1-call file-to-session entry (throws `xpTURN.Klotho.Replay.ReplayLoadException` on load failure). Replaces game-side `ReplaySystem.LoadFromFile` + `simConfig.Validate()` + `StartReplay` boilerplate
- **IKlothoModeStrategy** — per-mode dispatcher interface with P2P / ServerDriven implementations and a `KlothoModeStrategy.Resolve(simCfg)` static factory. Game code branches on the strategy rather than inspecting `simCfg.Mode` directly
- **KlothoSessionDriver.IsStopping** — externalized in-flight teardown guard (the MonoBehaviour adapter already owned the flag internally). Game code replaces per-game `_isStopping` / `_teardownInvoked` flags with `if (_sessionDriver.IsStopping) return;` at every re-entry candidate site; `OnSessionStopped` invariant — `driver.IsStopping == true` regardless of which entry path (Driver.DetachAndStop / Session.Stop direct) initiated teardown
- **Reconnect-credentials teardown opt-out** — `KlothoSession.Stop` / `KlothoSessionDriver.DetachAndStop` / `IKlothoNetworkService.LeaveRoom` accept `bool keepReconnectCredentials = false`. Default `false` discards persisted cold-start credentials on graceful session end (user-intent leave, match end, failed bootstrap). Process-exit entry points pass `true`: `KlothoSessionDriver.OnDestroy` does this internally and game code mirrors it in `OnApplicationQuit` / `OnDestroy`. Restores cold-start Reconnect across normal app quits — previously every quit silently wiped the store
- **LateJoinNotificationMessage** — host (P2P) / server (SD) broadcasts to existing peers and spectators on mid-match late-join so they update `OnPlayerJoined` / `PlayerCount` without polling. Forged-sender guards (P2P `!IsHost`, SD `peerId != 0`) + idempotency against the local roster. NetworkMessageType=75
- **FaultInjection macro-agnostic surface** — `FaultInjectionRuntime.AttachToSession` / `FaultInjectionLoader.TryLoadAndApply` / `FaultInjection` static collections are callable without `#if KLOTHO_FAULT_INJECTION` guard. Undefined builds return null / false / empty stub. Library-internal reader bodies retain their macro guards — release cost stays at zero
- **KlothoEngine / IKlothoEngine** — engine state machine (Idle, WaitingForPlayers, BootstrapPending, Running, Paused, Ending, Finished, Aborted)
  - **NetworkMode** — selectable P2P / ServerDriven topology
  - Partials: Rollback, TimeSync, ErrorCorrection, FullStateResync, LateJoin, Reconnect, Spectator, ServerDriven, ServerDrivenClient, Replay, FrameVerification, SyncTest, EventHelpers
  - **`IKlothoEngine.IssueOnce(Func<ICommand> commandFactory, ReliabilityPolicy policy = null) → IReliableCommandHandle`** — framework-owned reliable-command transaction. The tracker (`ReliableCommandTracker`) handles duplicate / past-tick reject escalation, retry-interval cooldown, empty-move collision avoidance, and `OnResyncCompleted` reset. Handle surface: `WouldCollideAt(tick)` / `Confirm()` / `Cancel()` / `OnRejected` / `OnResolved` / `OutstandingTargetTick`. `ReliabilityPolicy.Default` (RetryIntervalTicks=20 / ExtraDelayStep=4 / ExtraDelayMax=40 / TreatDuplicateAsAck=true / TreatPastTickAsEscalation=true) matches the prior Brawler spawn invariant; games can supply a custom policy for other reliable-input scenarios
  - **Interface surface (`IKlothoEngine`)** — `PredictedFrame` / `RenderClock` / `OnEventPredicted` / `OnEventConfirmed` / `OnEventCanceled` / `Logger` now live on the interface (previously concrete-only). `KlothoSession.Engine` and `KlothoSessionFlow.OnSessionCreated` return / callback signatures narrowed to `IKlothoEngine`. Games depend on the interface, not the concrete class
- **DedicatedServerLoop** — dedicated-server loop (for standalone server processes)
- **Object pooling** — ListPool, DictionaryPool, StreamPool, CommandPool, EventPool (GC avoidance)
- **WarmupRegistry** — JIT-warmup pre-registration (command / event / message types)
- **Logging** — built on the standard `Microsoft.Extensions.Logging.ILogger<T>` interface. Implementation: ZLogger (`ZLogger.Unity`) + `Microsoft.Extensions.Logging.Abstractions`

## Deterministic Math

- **FP64** — 32.32 fixed-point number (64-bit)
  - Arithmetic with overflow protection
  - Math functions: Abs, Min, Max, Sqrt, Pow
  - Trigonometry: Sin, Cos, Tan, Asin, Acos, Atan2
- **FPVector2 / FPVector3 / FPVector4** — fixed-point vectors; Dot, Cross, Distance, Angle, Normalize
- **FPQuaternion** — fixed-point quaternion; Euler conversion, Slerp
- **FPMatrix2x2 / 3x3 / 4x4** — transform matrices, inverse, transpose
- **FPBounds2 / FPBounds3** — AABB bounding boxes
- **FPRay2 / FPRay3** — rays for raycasting
- **FPPlane / FPCapsule / FPSphere** — geometric primitives
- **FPHash** — FNV-1a deterministic hashing
- **FPAnimationCurve** — deterministic animation curves based on baked keyframes
- **DeterministicRandom** — seeded RNG
- **Unity conversions** — extension methods such as FPVector3 ↔ Vector3

## Deterministic Physics

- **FPPhysicsWorld** — physics-engine main loop
  - Apply gravity → sync colliders → broadphase → narrowphase → constraint solve → velocity integration
- **FPRigidBody** — mass, velocity, angular velocity, damping, restitution / friction; Dynamic / Static / Kinematic
- **FPPhysicsBody** — physics-body state wrapper (separate from FPRigidBody)
- **FPCollider** — union of Box, Sphere, Capsule, Mesh shapes
  - FPBoxShape / FPSphereShape / FPCapsuleShape / FPMeshShape — individual shape types
- **CollisionTests** — AABB, sphere, capsule, and mesh intersection tests
- **NarrowphaseDispatch** — per-shape-pair narrowphase dispatcher
- **FPCollisionResponse** — collision response (restitution / friction impulses)
- **FPPhysicsIntegration** — physics integrator (velocity / position update)
- **FPSweepTests** — CCD (Continuous Collision Detection)
- **FPConstraintSolver** — iterative impulse-based constraint solver
- **FPDistanceJoint / FPHingeJoint** — joint constraints
- **FPTriggerSystem** — trigger Enter / Stay / Exit callbacks
- **FPSpatialGrid** — grid-based spatial partitioning (broadphase, dynamic objects)
- **FPStaticCollider** — static colliders (immovable terrain / obstacles)
- **FPStaticBVH / FPBVHNode** — BVH (Bounding Volume Hierarchy) acceleration for static objects
- **FPStaticColliderSerializer** — serialization / deserialization for static-collider data

## Deterministic Navigation

- **FPNavMesh** — deterministic navmesh (baked from Unity NavMesh)
  - Vertex / triangle arrays, adjacency, grid acceleration
- **FPNavMeshSerializer** — navmesh-data serialization / deserialization
- **FPNavAgent** — agent state (speed, radius, stopping distance, path, etc.)
- **FPNavMeshPathfinder** — A* search (with FPNavMeshBinaryHeap)
- **FPNavMeshFunnel** — funnel-algorithm path smoothing
- **FPNavMeshPathLinearizer** — path post-processing (drops redundant waypoints)
- **FPNavMeshPath** — path data structure
- **FPNavMeshQuery** — triangle-containment test (barycentric)
- **FPNavAvoidance** — ORCA collision avoidance
- **FPNavAgentSystem** — batch agent update (path request → steering → avoidance → movement → navmesh constraint)

## Input

- **IInputHandler** — local input capture, command conversion
- **IInputBuffer** — per-tick / per-player command storage (ring buffer)
- **IInputPredictor** — missing-input prediction with accuracy tracking

## Network

- **INetworkTransport** — transport abstraction (Connect, Disconnect, Send, Receive)
- **IKlothoNetworkService / KlothoNetworkService** — P2P client-session management
  - Session phases: None → Lobby → Syncing → Synchronized → Countdown → Playing → Disconnected
  - Room create / join / leave, ready state, player info
- **IServerDrivenNetworkService / ServerDrivenClientService** — server-driven-mode client service
- **ServerNetworkService** — server-side network service (input collection, frame verification, state broadcast)
- **Handshake protocol** — SyncRequest → SyncReply → SyncComplete → Ready → GameStart
- **Bootstrap handshake (SD)** — server-driven first-tick alignment: BootstrapBegin → PlayerBootstrapReady (replaces implicit start tick)
- **Reconnect protocol** — ReconnectRequest → ReconnectAccept/Reject
- **Late-join protocol** — FullStateRequest → FullStateResponse → LateJoinAccept
- **Dynamic InputDelay / RecommendedExtraDelay** — RTT-driven extra InputDelay seeded on Sync / LateJoin / Reconnect (via `RecommendedExtraDelayCalculator`) and pushed mid-match (`RecommendedExtraDelayUpdate`, asymmetric UP/DOWN threshold, rate-limited per peer); applied via engine `ApplyExtraDelay` / `EscalateExtraDelay` / `OnExtraDelayChanged`
- **Quorum-miss watchdog (P2P)** — presumed-drop a peer whose input is missing at the verified head for `QuorumMissDropTicks`; reactive empty-fill activates before transport DisconnectTimeout. False-positive rollback on late real input
- **InputBuffer seal (P2P relay)** — sealed `(tick, playerId)` placeholders suppress relay of late real packets after the chain has advanced, preventing host↔guest divergence. Host-side relay block surfaced via `_relaySealDropCount` telemetry
- **Hash gate (post-`ApplyFullState`)** — every `ApplyFullState` entry point (LateJoin / InitialFullState / ResyncRequest / CorrectiveReset / Reconnect) verifies the post-restore hash and fires `OnHashMismatch(tick, localHash, remoteHash)`
- **Corrective reset (P2P, host-only)** — `OnHashMismatch` triggers host `TryCorrectiveReset` → `BroadcastFullState(..., FullStateKind.CorrectiveReset)` → host self-apply + guest apply with `ApplyReason.CorrectiveReset`. Cooldown via `CorrectiveResetCooldownMs` prevents broadcast storms. Match continues; `OnMatchReset(ResetReason.StateDivergence)` fires only when the post-restore hash matches (mismatch retries via the mid-match desync pipeline)
- **Chain stall watchdog (peer-local)** — `AbortMatch(AbortReason.ChainStallTimeout)` when `CurrentTick - LastVerifiedTick` exceeds `max(ReconnectTimeoutMs/TickIntervalMs + 100, MinStallAbortTicks)`. Distinct terminal state `KlothoState.Aborted` (see `KlothoStateExtensions.IsEnded()`)
- **Normal-end lifecycle** — `IMatchEndEvent` (Synced marker, e.g. `GameOverEvent`) fires `OnMatchEnded(tick, evt)` exactly once on first verification. `EndGracePolicy.Continue` keeps the simulation running through the grace window; `EndGracePolicy.Pause` transitions `Running → Ending` (tick advance frozen, transport keepalives preserved). Grace durations: `EndGraceMs` (server `Room` drain), `ClientShutdownGraceMs` (client self-shutdown — must stay below `EndGraceMs`). `EndReason.MatchEnded` / `MatchAborted` classifies the drain trigger
- **RTT spike measurement** — `RttSpikeMetricsCollector` records per-spike windowed `chainBreak`, `rollbackDepth` mean/p95, `chainResumeLatencyMs`. Emitted at match-end via `[Metrics][RttSpike]`
- **PlayerRttSmoother** — 5-sample sliding median per player (≈5s window) feeding the dynamic-delay push decision
- **Command rejection feedback (SD)** — server unicast `CommandRejected` (PeerMismatch / PastTick / ToleranceExceeded / Duplicate) surfaced as engine `OnCommandRejected`
- **Match-end metrics** — JSON-line emit (`[Metrics][RttMatch]`, `[Metrics][BurstDuration]`, `[Metrics][PresumedDrop]`, `[Metrics][DynamicDelay]`, `[Metrics][LateJoin/Reconnect/Sync]`, `[Metrics][LagReductionLatency]`)
- **Spectator protocol** — SpectatorJoin → SpectatorAccept → SpectatorInput/Leave
- **ISpectatorService / SpectatorService** — spectator-entry / state-sync management
- **Message types**
  - Basic: PlayerReady, GameStart, Command, CommandAck, SyncHash, FullStateRequest/Response, Ping/Pong, JoinReject, ServerShutdown
  - Handshake: SyncRequest, SyncReply, SyncComplete, PlayerJoin, RoomHandshake
  - Reconnect: ReconnectRequest, ReconnectAccept, ReconnectReject
  - Late join: LateJoinAccept
  - Dynamic delay: RecommendedExtraDelayUpdate
  - Spectator: SpectatorJoin, SpectatorAccept, SpectatorInput, SpectatorLeave
  - Server-driven: ClientInput, ClientInputBundle, VerifiedState, InputAck, PlayerBootstrapReady, BootstrapBegin, CommandRejected
- **Multi-room server** — Room, RoomManager, RoomManagerConfig, RoomRouter, RoomScopedTransport
  - ServerLoop — server main loop coordinating multiple rooms
  - ServerInputCollector — server-side input collector
- **ITimeSyncService** — RTT measurement, clock-offset sync
- **SharedTimeClock** — shared game time

## Serialization

- **SpanWriter / SpanReader** — ref-struct, GC-free binary serialization
  - byte, bool, int16/32/64, float, double, string, FP64, FPVector3, etc.
- **ISpanSerializable** — Span-based serialization interface
- **SerializationBuffer** — managed byte buffer (pooled, IDisposable)
- **[KlothoSerializable(typeId)]** — type-registration attribute for the source generator
- **[KlothoOrder]** — specifies field serialization order
- **[KlothoIgnore]** — excludes a field from serialization
- **[KlothoHashIgnore]** — excludes a field from hash computation

## DataAsset

- **IDataAsset** — data-asset marker interface (`AssetId`)
- **IDataAssetSerializable** — data-asset serialization interface
- **DataAssetRef** — asset-ID reference wrapper (for component fields)
- **IDataAssetRegistry / DataAssetRegistry** — global data-asset registry
  - **`Get<T>()` / `TryGet<T>(out T)`** — typed lookup that auto-resolves the `AssetId` named-arg on `[KlothoDataAsset]`; throws `InvalidOperationException` when the asset omits `AssetId` (avoids silent failures). The existing `Get<T>(int id)` / `TryGet<T>(int id, out T)` overloads remain for multi-instance fan-out (e.g. `Get<BotDifficultyAsset>(1700 + slotIndex)`)
  - **`GetByKey<T>(string)` / `TryGetByKey<T>(string, out T)`** — concrete-type lookup via the `Key` named-arg on `[KlothoDataAsset]`; backed by a `(Type, string)` tuple index built at `Register` time
- **IDataAssetRegistryBuilder** — registry builder (register / lookup)
- **DataAssetTypeRegistry** — type-metadata registry
- **DataAssetReader / DataAssetWriter** — binary read / write
- **DataAssetRegistryExtensions** — lookup / register extension methods
- **[KlothoDataAsset(typeId, AssetId = ..., Key = ...)]** — data-asset type-registration attribute (source-generator integration). Positional `typeId` is the wire-stable type discriminator; named-arg `AssetId` is the runtime instance id (separate plane); named-arg `Key` is an optional string handle for `GetByKey<T>`. The generator emits a `private readonly int _assetId` backing field, a `public int AssetId => _assetId` expression-bodied property, a `ctor(int)`, and — when `AssetId` is provided — a parameterless `ctor() : this(AssetIdFromAttribute)`
- **JSON serialization** — `xpTURN.Klotho.DataAsset.Json` assembly (built on Newtonsoft.Json)
  - DataAssetJsonSerializer, DataAssetContractResolver, DataAssetSerializationBinder
  - Converters: FP64JsonConverter, FPVector2/3JsonConverter, DataAssetRefJsonConverter

## State

- **IStateSnapshot** — snapshot interface (Tick, Serialize/Deserialize, CalculateHash)
- **IStateSnapshotManager** — snapshot save / restore / lookup interface
- **RingSnapshotManager** — ring-buffer snapshot management (fixed capacity, O(1) insert / lookup, GC 0)

## ECS

- **EntityRef** — lightweight entity reference (8 bytes, generational index prevents dangling)
- **EntityManager** — entity-lifecycle management (generational index + free-list slot reuse, fixed capacity)
- **ComponentStorage\<T\>** — sparse-set component storage (`unmanaged` constraint, O(1) Add/Remove/Has)
- **ComponentStorageRegistry** — assembly-scan-based automatic component-type registration
- **Frame** — ECS world state (EntityManager + a set of ComponentStorages, Tick, hash, snapshots / rollback)
  - `Get<T>`, `Has<T>`, `Add<T>`, `Remove<T>`, `CreateEntity`, `DestroyEntity`
  - `Filter<T1..T5>` / `FilterWithout<T1..T5, TExclude>` — ref-struct, zero-GC queries (iterates the smallest storage first)
  - `CalculateHash()` — FNV-1a deterministic hash
  - `CopyFrom()` — BlockCopy-based snapshot / restore
- **IComponent** — `unmanaged` component marker interface
- **IEntityPrototype / EntityPrototypeRegistry** — entity-prototype interface and registry (data-driven entity creation)
- **[KlothoComponent(typeId)]** — component-type-registration attribute (source-generator integration; UserMinId=100)
- **[KlothoSingletonComponent]** — marks a component type as singleton (exactly one carrier entity per frame). `Frame.Add<T>` throws on a second carrier; read via `Frame.GetSingleton<T>` / `GetReadOnlySingleton<T>` / `TryGetSingleton<T>`. Source generator emits an `IsSingleton` flag onto `ComponentStorageRegistry.TypeIdCache<T>`
- **[FrameData]** — frame-data field-serialization attribute
- **SystemPhase** — PreUpdate / Update / PostUpdate / LateUpdate
- **ISystem** — `Update(ref Frame)` system interface
- **IInitSystem / IDestroySystem** — init / destroy system interfaces
- **ICommandSystem** — `OnCommand(ref Frame, ICommand)` command-system interface
- **ISyncEventSystem** — system interface that processes events only on synced ticks
- **IEntityCreatedSystem / IEntityDestroyedSystem** — entity-lifecycle system interfaces
- **ISignal / ISignalOnComponentAdded\<T\> / ISignalOnComponentRemoved\<T\>** — component-change signal interfaces
- **SystemRunner** — system registration and phase-ordered execution (AddSystem → auto-sorted)
- **FrameRingBuffer** — Frame ring buffer (ECS-specific snapshot / rollback)
- **EcsStateSnapshot** — IStateSnapshot adapter (built on Frame.CopyFrom)
- **EcsSimulation** — ISimulation implementation (owns Frame + SystemRunner; pluggable into KlothoEngine)
  - **`GetSystem<T>()` / `TryGetSystem<T>(out T)` / `GetSystems<T>(List<T> buffer)`** — type-match lookup over registered systems (`T : class`). Returns the first registration in `AddSystem` order; `GetSystems` appends every match into a caller-owned buffer (alloc-free for the lookup itself). Lets a callback boundary expose a registered system's secondary interface (e.g. `PhysicsSystem` → `IFPPhysicsWorldProvider`) without process-wide static slots
- **FixedString32 / FixedString64** — `unmanaged` fixed-size UTF-8 strings (for component fields)
- **Built-in components** — TransformComponent, VelocityComponent, MovementComponent, HealthComponent, CombatComponent, OwnerComponent, PhysicsBodyComponent, NavigationComponent, SessionParticipantComponent (engine writes one per active player at `Start()` for a deterministic all-participants-spawned gate), RandomSeedComponent (singleton — engine-injected at session start; restored on LateJoin / Reconnect / Spectator / Replay via FullState)
- **TransformComponent prev-snapshot** — `PreviousPosition` / `PreviousRotation` / `PreviousInitialized` marker. Engine auto-initializes Previous* on first `Frame.Add` and via a PreUpdate `SavePrev` pass. Use `Frame.RefreshPreviousTransform(entity)` after a post-Add ref-set to keep Previous* in lockstep with `Position` (suppresses unwanted one-frame interpolation; see GameDevAPI §4.1)
- **Built-in systems** — MovementSystem, CombatSystem, PhysicsSystem, NavigationSystem, CommandSystem, EventSystem

## Replay

- **IReplayRecorder** — recording (start / record-tick / stop)
- **IReplayPlayer** — playback (load / play / pause / resume / stop / seek)
  - Playback speeds: 0.25x, 0.5x, 1x, 2x, 4x
- **IReplaySystem** — recording + playback combined, file save / load
- **IReplayData** — metadata + per-tick command-data serialization
- **File format** — `RPLY` magic (uncompressed) / LZ4-compressed stream (K4os.Compression.LZ4)
- **Implementations** — ReplayRecorder, ReplayPlayer, ReplaySystem, ReplayData

## Editor

- **FPNavMeshExporter** — Unity NavMesh → FPNavMesh conversion (triangle baking + grid build)
- **NavMesh Visualizer** — editor visualization tool
  - FPNavMeshVisualizerWindow — editor window
  - FPNavMeshSceneOverlay — scene-overlay rendering
  - FPNavMeshAgentSimulator — agent-movement test
  - FPNavMeshInteraction — click-to-navigate test
- **Static Collider Tools** — editor tooling for static colliders
  - FPStaticColliderExporterWindow — static-collider exporter window
  - FPStaticColliderConverter — Unity Collider → FPStaticCollider conversion

## Unity Integration

- **USimulationConfig** — ScriptableObject SimulationConfig (inspector-editable, implements `ISimulationConfig`)
- **USessionConfig** — ScriptableObject SessionConfig (inspector-editable, implements `ISessionConfig`). All 16 session-level fields (MaxPlayers/MinPlayers/MaxSpectators, late-join/reconnect policy, chain-stall watchdog, countdown, match-end grace) author in one asset; `KlothoSessionSetup.SessionConfig` replaces the previous mirror-field set (RandomSeed/MaxPlayers/MinPlayers/AllowLateJoin/…)
- **EcsDebugBridge** — editor debug bridge
- **View layer**
  - **EntityView / EntityViewComponent** — entity-view base class and view-component interface
    - **Standard transform pipeline** — `EntityView` performs lerp + `ApplyTransform` + `UpdatePositionParameter` populate in `InternalLateUpdateView` (fused with `_errorVisual.Tick`), so tick-rate < frame-rate environments reflect every per-frame `PredictedAlpha` change in the transform without stale-lerp stutter. `UpdatePositionParameter` zeros `ErrorVisualVector` / `ErrorVisualQuaternion` when `EnableSnapshotInterpolation` is set (verified-frame interpolation path no longer double-corrects the rollback delta). Games override `OnUpdateView` / `OnLateUpdateView` for game-data cache + visual feedback; the transform pipeline itself is base-delegated
    - **EngineEventOneShot.Subscribe\<TEvent\>(engine, filter, onPlay, onCancel, lateGuard) → EngineEventSubscription** — sealed `IDisposable` helper that wraps `OnEventPredicted` + `OnEventConfirmed` + `OnEventCanceled` 3-channel subscription with a late-dispatch guard. Predicted+Confirmed dispatch `onPlay` when `filter` + `lateGuard` pass; Canceled dispatches `onCancel` when `filter` passes. `Dispose()` unsubscribes from all three channels and nullifies handlers (multi-dispose safe). Scope is limited to Predicted/Confirmed/Canceled — verified-time fallback events (e.g. `ActionCompletedEvent`) keep using the `OnSyncedEvent` channel
  - **EntityViewFactory / IEntityViewPool / DefaultEntityViewPool** — view creation / pooling. The base `EntityViewFactory` resolves `BindBehaviour` / `ViewFlags` from a 5-flag decision (rolls up `RequiresBindBehaviour`, `HasViewComponentInterpolation`, `RequiresErrorCorrection`, `RequiresSnapshotInterpolation`, `RequiresViewComponentBinding`) — games override only when a sample-specific override is required
  - **EntityViewUpdater** — simulation state → view sync; owns the built-in **PlayerViewRegistry\<TView\>** (lifted from sample). EVU drives `Register` / `Unregister` automatically from `OwnerComponent` add/remove; game code uses `Get(playerId)` for lookup and subscribes to `OnViewRegistered` / `OnLocalViewRegistered` / `OnLocalViewUnregistered` for player-view event hooks
  - **KlothoSessionFlow / KlothoSessionFlowAsync** — recommended 5-entry-point builder for session creation (`StartHost` / `JoinAsync` / `ReconnectAsync` / `SpectateAsync` / `StartReplay`). Sync primitives in Runtime.Core, UniTask wrappers in Runtime.Unity. `KlothoConnectionAsync` (Runtime.Unity) remains as an escape-hatch primitive — Flow consumes it internally.
  - **KlothoSessionDriver** — MonoBehaviour adapter that drives `KlothoSession.Update` / `Stop` through Unity's Update loop; exposes `PreSessionUpdate` / `PostSessionUpdate` / `Stopping` / `IdlePoll` hooks for game-side input capture and cleanup
  - **KlothoAutoReconnect / KlothoLogger** — cold-start credentials gate + ZLogger.Unity + Rolling File factory (Runtime.Unity helpers)
  - **VerifiedFrameInterpolator** — interpolation based on verified frames
  - **BindBehaviour** — component-binding MonoBehaviour
  - **UpdatePositionParameter / ViewFlags / ErrorVisualState** — auxiliary types
- **FPStaticColliderOverride** — MonoBehaviour for overriding static-collider parameters
- **FPStaticColliderVisualizer** — MonoBehaviour for scene visualization of static colliders

## Samples

- **Brawler** — fighting-game sample
  - **BrawlerGameController** — host / client init, session management
  - **BrawlerSimSetup** — ECS simulation composition (system / component registration)
  - **BrawlerInputCapture** — player-input capture and command conversion
  - **BrawlerCallbacks** — `ISimulationCallbacks` implementation (game-event handling)
  - **BrawlerViewSync / BrawlerEntityViewFactory** — simulation-state → Unity-view sync; view factory
  - **BrawlerCharacterViewRegistry** — character entity → view mapping
  - **BrawlerPlayerConfig / BrawlerReplayConfig** — sample configuration
  - **CombatHelper** — combat helper
  - **Commands** — AttackCommand, MoveInputCommand, SpawnCharacterCommand, UseSkillCommand
  - **Components** — BotComponent, CharacterComponent, GameTimerStateComponent (singleton), ItemComponent, KnockbackComponent, PlatformComponent, SkillCooldownComponent, SpawnMarkerComponent (the sample's `GameSeedComponent` was replaced by the engine-provided singleton `RandomSeedComponent`)
  - **Events** — ActionCompletedEvent, AttackActionEvent, AttackHitEvent, CharacterKilledEvent, CharacterSpawnedEvent, DashEvent, GameOverEvent, GroundSlamEvent, ItemPickedUpEvent, JumpEvent, RoundTimerEvent, SkillActionEvent, TrapTriggeredEvent
  - **Systems** — ActionLockSystem, BotFSMSystem, BoundaryCheckSystem, CombatSystem, GameOverSystem, GroundClampSystem, ItemSpawnSystem, KnockbackSystem, ObstacleMovementSystem, PlatformerCommandSystem, RespawnSystem, SkillCooldownSystem, TimerSystem, TopdownMovementSystem, TrapTriggerSystem (the sample's `SavePreviousTransformSystem` was removed — `TransformComponent.PreviousPosition/Rotation` is engine-maintained)
  - **Bot HFSM** — BotHFSMRoot, BotActions, BotDecisions, BotFSMHelper (hierarchical-FSM-based bot AI)
  - **Prototypes** — `IEntityPrototype` implementations (KnightPrototype, MagePrototype, RoguePrototype, WarriorPrototype, ItemPickupPrototype, MovingPlatformPrototype)
  - **View** — CharacterView, CharacterAnimatorViewComponent, CharacterActionVfxViewComponent, ItemView, PlatformView, BrawlerCameraController, GameHUD, GameMenu, ResultScreen

## Tests

- **Core** — Command serialization, SyncTestRunner, FullStateResync
- **Integration** — late-join integration, server-driven-mode integration / benchmarks, replay integration (ReplayIntegrationTests), SD late-join connection
- **Network** — Handshake, Reconnect, Spectator, LateJoin, ServerDriven unit tests; message serialization; LiteNetLib integration
- **ECS** — EntityManager, ComponentStorage, Frame, Filter, SystemRunner, FrameRingBuffer, EcsStateSnapshot, EcsSimulation; built-in systems (movement / combat / physics / nav / command / event); SourceGenerator validation; OOP hash comparison
- **Deterministic** — Math (FP64 / Vector / Quaternion / Matrix); Geometry (Bounds / Ray / Plane / Capsule / Sphere); Physics (RigidBody / Collider / Shape / Broadphase / Narrowphase / Sweep / Constraint / StaticBVH / PhysicsWorld); Navigation (Pathfinder / Funnel / Linearizer / Avoidance / Query / Serializer); Random; Curve
- **DeterminismVerification** — determinism stress-verification framework (ArithmeticStressSystem, EntityLifecycleSystem, RandomStressSystem, TrigStressSystem, DeterminismVerificationRunner, ServerDrivenDeterminismRunner)
- **State** — RingSnapshotManager
- **Input** — InputBuffer
- **Helpers** — KlothoTestHarness, TestTransport, TestSimulation

---

*Last updated: 2026-05-25*
