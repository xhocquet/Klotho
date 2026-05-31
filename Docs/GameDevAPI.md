# Game Developer API Overview

> Related: [Workflow](GameDevWorkflow.md)

---

## 1. Component Definition API

```csharp
// A component authored by the game developer
[KlothoComponent(100)]  // Unique ID — 1–99 reserved for the framework, 100+ for game developers
public partial struct HeroComponent : IComponent
{
    public int Level;
    public int Experience;
}
```

The source generator emits `Serialize` / `Deserialize` / `GetSerializedSize` / `GetHash` automatically. Duplicate IDs are caught at compile time.

### Built-in Components

| Component | Fields | Purpose |
|---|---|---|
| `TransformComponent` | `Position`, `Rotation`, `Scale`, `PreviousPosition`, `PreviousRotation`, `PreviousInitialized`, `TeleportTick` | Position / rotation / scale + view-interpolation prev-snapshot (see §4.1) |
| `VelocityComponent` | — | Velocity |
| `MovementComponent` | `TargetPosition`, `IsMoving` | Movement control |
| `HealthComponent` | `CurrentHealth`, `MaxHealth` | Health |
| `CombatComponent` | `AttackDamage`, `AttackRange` | Combat |
| `OwnerComponent` | `OwnerId` | Owner (player) |
| `PhysicsBodyComponent` | — | Physics body |
| `NavigationComponent` | — | Navigation agent |
| `RandomSeedComponent` | `Seed` (ulong) | **Singleton** (`[KlothoSingletonComponent]`). Engine-injected at session start (and restored via FullState on LateJoin / Reconnect / Spectator / Replay). Read via `frame.GetReadOnlySingleton<RandomSeedComponent>().Seed`; combine with `DeterministicRandom.FromSeed(seed, featureKey, frame.Tick)` to derive rollback-stable RNG streams |

### Singleton Components

Mark a component type with `[KlothoSingletonComponent]` to enforce one-carrier-per-frame:

```csharp
[KlothoComponent(106)]
[KlothoSingletonComponent]   // exactly one entity may carry this component
public partial struct GameTimerStateComponent : IComponent
{
    public int  StartTick;
    public int  LastReportedSeconds;
    public bool GameOverFired;
}
```

- `Frame.Add<T>(entity, value)` throws if a second entity tries to carry the same singleton component.
- Read via `Frame.GetSingleton<T>` / `GetReadOnlySingleton<T>` / `TryGetSingleton<T>(out var entity)`.
- The source generator emits an `IsSingleton = true` flag onto `ComponentStorageRegistry.TypeIdCache<T>`; the guard is O(1) on `Frame.Add`.
- Use this for "world state" fields that are read by many systems and should never fork into multiple instances (timer state, RNG seed, score state, etc.). The engine itself uses it for `RandomSeedComponent`.

---

## 2. System Implementation API

### Available Interfaces

| Interface | Invocation | Purpose |
|---|---|---|
| `ISystem` | Phase.Update / PostUpdate / LateUpdate | General per-tick logic |
| `ICommandSystem` | Phase.PreUpdate (on command receipt) | Command handling |
| `IInitSystem` | Once at simulation init | Initialization |
| `IDestroySystem` | Once at simulation shutdown | Cleanup |
| `ISyncEventSystem` | When a Verified tick is finalized | Sync-event emission |
| `IEntityCreatedSystem` | Right after entity creation | React to creation |
| `IEntityDestroyedSystem` | Right before entity destruction | React to destruction |
| `ISignalOnComponentAdded<T>` | On component add | Component reactions |
| `ISignalOnComponentRemoved<T>` | On component remove | Component reactions |
| `ISignal` (custom) | When `SystemRunner.Signal<T>()` is called | System-to-system signaling |

### Implementation Examples

```csharp
// Plain update system
public class HealthRegenSystem : ISystem
{
    public void Update(ref Frame frame)
    {
        var filter = frame.Filter<HealthComponent>();
        while (filter.Next(out var entity))
        {
            ref var health = ref frame.Get<HealthComponent>(entity);
            if (health.CurrentHealth < health.MaxHealth)
                health.CurrentHealth++;
        }
    }
}

// Command system
public class SpawnCommandSystem : ICommandSystem
{
    public void OnCommand(ref Frame frame, ICommand command)
    {
        if (command is SpawnCommand spawn)
        {
            var entity = frame.CreateEntity();
            frame.Add(entity, new TransformComponent { Position = spawn.Position });
            frame.Add(entity, new OwnerComponent { OwnerId = spawn.PlayerId });
        }
    }
}
```

---

## 3. System Registration & Engine Integration API

Callbacks are split into the **deterministic side (`ISimulationCallbacks`)** and the **client-view side (`IViewCallbacks`)**. Place deterministic code that must run identically on every peer (server, client, replay) in `ISimulationCallbacks`; place non-deterministic client logic such as UI, animation, and spawn commands in `IViewCallbacks`.

### ISimulationCallbacks — Deterministic Common

```csharp
public class MySimulationCallbacks : ISimulationCallbacks
{
    // Register simulation systems — called immediately after EcsSimulation construction,
    // before Engine.Initialize().
    public void RegisterSystems(EcsSimulation sim)
    {
        var events = new EventSystem();
        sim.AddSystem(new CommandSystem(),     SystemPhase.PreUpdate);
        sim.AddSystem(new MovementSystem(),    SystemPhase.Update);
        sim.AddSystem(new CombatSystem(events),SystemPhase.Update);
        sim.AddSystem(new HealthRegenSystem(), SystemPhase.Update);
        sim.AddSystem(events,                  SystemPhase.LateUpdate);
    }

    // Create initial-world entities — called inside Engine.Start(), before SaveSnapshot(0).
    // Deterministic code only. ⚠ NOT called on the ServerDriven client (see note below).
    public void OnInitializeWorld(IKlothoEngine engine)
    {
        // Examples: fixed-terrain / item spawn, initial player-entity setup, etc.
    }

    // Per-tick input polling — send commands via sender.Send()
    // (no send → EmptyCommand auto-injected).
    public void OnPollInput(int playerId, int tick, ICommandSender sender)
    {
        var cmd = CommandPool.Get<MoveCommand>();
        cmd.PlayerId = playerId;
        // ... fill input ...
        sender.Send(cmd);
    }
}
```

> **ServerDriven: `OnInitializeWorld` is skipped on the client.** A ServerDriven client boots its initial state from the server's **FullState** snapshot and does **not** call `OnInitializeWorld` (only the server / a P2P host does). The FullState snapshot carries dynamic entity state but **not static colliders**. Consequences:
>
> - **Register deterministic static geometry in `RegisterSystems`, not `OnInitializeWorld`.** `RegisterSystems` runs on *every* peer (server and client); `OnInitializeWorld` does not. If you call `PhysicsSystem.LoadStaticColliders(...)` only from `OnInitializeWorld`, the SD client's physics world has no ground/walls → dynamic bodies fall through / pass static geometry → state diverges from the server (desync). Build the static-collider BVH where all peers run it:
>   ```csharp
>   public void RegisterSystems(EcsSimulation sim)
>   {
>       var physics = new PhysicsSystem(gravity: someGravity);
>       physics.LoadStaticColliders("scene", staticColliders);   // ← here, runs on server AND SD client
>       sim.AddSystem(physics, SystemPhase.Update);
>       // ...
>   }
>   ```
> - **Don't cache `engine`/state in `OnInitializeWorld` for use by client-side callbacks** (`OnPollInput`, etc.) — that path never runs on the SD client, so the cached reference stays null and the callback silently no-ops (e.g. input never sent). Use the arguments passed to each callback instead (`OnPollInput`'s `playerId` is already the local player id).
>
> (Data-driven / runtime-mutated static colliders that can't be reproduced deterministically on every peer must instead be carried in the FullState snapshot — a framework concern beyond this guide.)

### IViewCallbacks — Client View Only

```csharp
public class MyViewCallbacks : IViewCallbacks
{
    // Called once at game start — send spawn commands, init UI, etc.
    public void OnGameStart(IKlothoEngine engine) { }

    // Called after each tick is executed — view updates, etc.
    public void OnTickExecuted(int tick) { }

    // Called once after late-join catchup completes — initial logic such as spawn commands
    public void OnLateJoinActivated(IKlothoEngine engine) { }
}
```

### Session Creation (`KlothoSession.Create`)

`KlothoSession` is created via the static factory `Create(KlothoSessionSetup)`. Host/guest, network mode (P2P/ServerDriven), and late-join behavior are determined by `KlothoSessionSetup` fields.

```csharp
var setup = new KlothoSessionSetup
{
    Logger = logger,
    SimulationCallbacks = new MySimulationCallbacks(),
    ViewCallbacks       = new MyViewCallbacks(),
    Transport           = transport,         // host only
    Connection          = connectionResult,  // guest only (when set, host fields are ignored)
    SimulationConfig    = uSimulationConfig, // ScriptableObject or any ISimulationConfig
    SessionConfig       = uSessionConfig,    // ScriptableObject or any ISessionConfig (host only — guest ignored, populated by GameStartMessage / LateJoinAcceptMessage / ReconnectAcceptMessage)
    AssetRegistry       = dataAssetRegistry, // optional: externally built registry
    CredentialsStore    = credentialsStore,  // optional: warm-reconnect save/clear (guest)
    AppVersion          = Application.version,
    DeviceIdProvider    = new UnityDeviceIdProvider(),
    LifecycleObserver   = this,              // implements IKlothoSessionObserver (see §3.1)
};
var session = KlothoSession.Create(setup);
```

`SessionConfig` carries the 16 host-decided session fields (`RandomSeed`, `MaxPlayers` / `MinPlayers` / `MaxSpectators`, late-join/reconnect policy & tuning, chain-stall watchdog, countdown, match-end grace). Author it once as a `USessionConfig` ScriptableObject and reuse across scenes — `KlothoSession.Create()` copies the values into an internal `SessionConfig` (so editor assets are never mutated, and `RandomSeed = 0` is auto-replaced by `Environment.TickCount` on host). Passing `null` falls back to the runtime default `new SessionConfig()` — convenient for tests and replay paths.

### 3.1 IKlothoSessionObserver — bulk-subscribed lifecycle

Implement `IKlothoSessionObserver` and pass the instance through `KlothoSessionSetup.LifecycleObserver` to bulk-subscribe all session-level lifecycle callbacks at `KlothoSession.Create`. The framework unsubscribes them at `Stop()` and finally calls `OnSessionStopped()` so the game can finish its own teardown. This replaces the per-event `+=` wiring that was previously spread across `StartHost / JoinGame / Reconnect / StopGame` sites.

```csharp
public class MyGameController : MonoBehaviour, IKlothoSessionObserver
{
    // NetworkService callbacks
    public void OnPlayerDisconnected(IPlayerInfo player) { /* host: gray out portrait */ }
    public void OnPlayerReconnected(IPlayerInfo player)  { /* host: clear gray */ }
    public void OnReconnecting() { /* guest UI */ }
    public void OnReconnectFailed(byte reason)
    {
        // reason is a ReconnectRejectReason byte
        var name = ReconnectRejectReason.ToName(reason);
        if (ReconnectRejectReason.RequiresUserChoice(reason))
            ShowAlreadyConnectedDialog();
        else
            FallbackToInitial();
    }
    public void OnReconnected() { /* guest UI */ }

    // Engine callbacks
    public void OnCatchupComplete()              { /* late-join active */ }
    public void OnResyncCompleted(int tick)      { /* state replaced by verified */ }
    public void OnGameStart()                    { /* match running */ }
    public void OnMatchAborted(AbortReason r)    { /* chain stall, divergence, … */ }
    public void OnMatchEnded(int tick, IMatchEndEvent endEvt) { /* normal end */ }
    public void OnMatchReset(ResetReason r)      { /* corrective reset, match continues */ }
    public void OnSessionStopped()               { /* transport disconnect, null-out session */ }
}
```

Default no-op implementations are provided on the interface, so the game only overrides the callbacks it needs. The `OnReconnectFailed(byte reason)` byte mirrors `KlothoNetworkService.OnReconnectFailed` (see Specification §9.5) — symbolic names via `ReconnectRejectReason.ToName(reason)`; `RequiresUserChoice(reason)` returns true for `AlreadyConnected`.

Cold-start reconnect (via `KlothoConnectionAsync.ReconnectAsync` / `KlothoConnection.Reconnect`) surfaces server reject through `ReconnectFailedException` — catch it and branch on `e.Reason` (same byte values as `OnReconnectFailed`).

### Driving the Session in Unity

Drive the session through `KlothoSessionDriver` — a MonoBehaviour that owns the `Update`/`Stop` loop and exposes hooks for pre/post-Update logic, Stop teardown, and idle (no-session) polling. Attach the driver as a `[SerializeField]` on the game controller prefab and wire hooks in `Awake`.

```csharp
[SerializeField] private KlothoSessionDriver _sessionDriver;

void Awake()
{
    _sessionDriver.PreSessionUpdate += OnPreSessionUpdate;
    _sessionDriver.Stopping        += OnSessionDriverStopping;
    _sessionDriver.IdlePoll        += () => _transport?.PollEvents();
}

void OnPreSessionUpdate(KlothoSession s, float dt)
{
    // Capture input / compute aim before Session.Update runs.
    _input.CaptureInput();
}

void OnSessionDriverStopping(KlothoSession s)
{
    // Cleanup that requires Engine to be alive — fires before session.Stop.
    _viewSync.Cleanup();
    _entityViewUpdater?.Cleanup();
}

// Attach after the session is created (see §X Flow).
_sessionDriver.Attach(_session);
```

The driver guarantees: dt is computed from `DateTimeOffset.UtcNow` (time-scale invariant), `IdlePoll` fires only while `Session == null`, and `Stopping` runs exactly once before `session.Stop()` (re-entry guarded). Hook exception policy: steady-state hooks (`PreSessionUpdate` / `PostSessionUpdate` / `IdlePoll`) propagate naked; `Stopping` is `try { ... } finally { session.Stop(keepReconnectCredentials); }` so teardown is guaranteed even if a subscriber throws.

`KlothoSessionDriver` also exposes `IsStopping` as a read-only flag. Game code can short-circuit re-entrant teardown calls with `if (_sessionDriver != null && _sessionDriver.IsStopping) return;` — replaces the per-game `_isStopping` / `_teardownInvoked` duplicate flags that used to guard `StopGame` against the `Driver.DetachAndStop → Session.Stop → OnSessionStopped → game StopGame` re-entry path. Invariant: when `OnSessionStopped` fires, `driver.IsStopping == true` regardless of which entry path (Driver.DetachAndStop vs Session.Stop direct) initiated teardown.

**Reconnect-credentials policy on teardown**: `KlothoSessionDriver.DetachAndStop(bool keepReconnectCredentials = false)` and `KlothoSession.Stop(bool keepReconnectCredentials = false)` accept an optional flag forwarded to `IKlothoNetworkService.LeaveRoom`. Default `false` discards persisted cold-start credentials (user-intent leave / match-end shutdown / failed bootstrap). Pass `true` from process-exit entry points so persisted credentials survive into the next launch: `KlothoSessionDriver.OnDestroy` already does this internally, and game code should mirror it in its own `OnApplicationQuit` / `OnDestroy` body (e.g. `_sessionDriver?.DetachAndStop(keepReconnectCredentials: true);`). Explicit cancel / reject paths clear credentials directly via `IReconnectCredentialsStore.Clear()` — do not rely on the teardown flag for those.

`IKlothoSession` API: `Engine` (returns `IKlothoEngine`), `Simulation`, `LocalPlayerId`, `State`, `Update(float dt)`, `InputCommand(ICommand)`, `Stop(bool keepReconnectCredentials = false)`, `IsStopped`, `PlayerCount`, convenience methods `HostGame(name, maxPlayers)` / `JoinGame(name)` / `LeaveRoom()` / `SendPlayerConfig(PlayerConfigBase)` / `SetReady(bool)`, and state-change events `StateChanged(KlothoState)` / `PhaseChanged(SessionPhase)` / `PlayerCountChanged(int)` / `AllPlayersReadyChanged(bool)` (subscribe on `OnSessionCreated`; unsubscribed automatically on `Stop`). These events replace per-frame state polling — wire each event to the corresponding UI/audio reaction directly. Backed on the service side by `IKlothoNetworkService.OnPhaseChanged` / `OnPlayerCountChanged` / `OnAllPlayersReadyChanged` and `IKlothoEngine.OnStateChanged`; the session forwards both the network-service and the spectator-service `OnPlayerCountChanged` so the same subscriber works across host / guest / spectator. `PlayerCount` reads through the `NetworkService → SpectatorService → 0` fallback chain — call it from a one-shot poll if you missed the initial event.

Logger channel: prefer `engine.Logger` / `frame.Logger` for runtime logging. `KlothoLogger.CreateDefault` is an escape hatch — use it only when a separate category or rolling-file destination is needed.

### 3.2 KlothoSessionFlow — mode-dispatched entry points

For most games the preferred construction path is `KlothoSessionFlow` (it wraps `KlothoSession.Create` and bundles common defaults). `KlothoFlowSetup` carries the long-lived dependencies; the entry methods take only the per-call parameters:

| Mode | Entry | Notes |
|---|---|---|
| P2P host | `flow.StartHostAndListen(simCfg, sessionCfg, roomName, address, port)` | synchronous — folds StartHost + HostGame + Transport.Listen with auto-teardown on failure. Returns the running session, or `null` on listen-bind failure (session already torn down); rethrows on other failures after teardown |
| P2P host (low-level) | `flow.StartHost(simCfg, sessionCfg)` | synchronous — escape hatch for custom ordering / multi-transport / tests. Caller drives `HostGame` + `Transport.Listen` and rollback manually |
| P2P guest | `flow.JoinP2PAsync(transport, host, port, sessionCfg, ct)` | guest receives `sessionCfg` from `GameStartMessage` — the passed value is a seed |
| ServerDriven client | `flow.JoinServerDrivenAsync(transport, host, port, roomId, sessionCfg, ct)` | extra `roomId` parameter (P2P does not use it) |
| Reconnect | `flow.ReconnectAsync(transport, creds, sessionConfigSeed, ct)` | `creds` is `PersistedReconnectCredentials` — carries `RoomId`, host address, magic. Mode is recovered from the credentials |
| Spectator | `flow.SpectateAsync(host, port, roomId, ct)` | no-transport overload — library calls `KlothoFlowSetup.SpectatorTransportFactory` |
| Replay | `flow.StartReplayFromFile(path)` | throws `xpTURN.Klotho.Replay.ReplayLoadException` on load failure |

Branch by mode using `KlothoModeStrategy.Resolve(simCfg)` rather than reading `simCfg.Mode` directly. The flow also dispatches per-mode session-created callbacks alongside the generic `OnSessionCreated`:

```csharp
_flow.OnSessionCreated         += s => _sessionDriver.Attach(s);    // mode-agnostic
_flow.OnHostSessionCreated     += OnHostOrGuestSessionCreated;       // host only
_flow.OnGuestSessionCreated    += OnHostOrGuestSessionCreated;       // guest / reconnect
_flow.OnReplaySessionCreated   += OnReplayOrSpectatorSessionCreated; // replay only
_flow.OnSpectatorSessionCreated += OnReplayOrSpectatorSessionCreated; // spectator only
```

`KlothoFlowSetup` also accepts two optional factories that absorb common boilerplate:

- `InitialPlayerConfigFactory : Func<PlayerConfigBase>` — invoked automatically on guest / reconnect paths after the session is created; the framework calls `session.SendPlayerConfig(factory())`. Spectator / replay paths skip the call. The factory is invoked per-session so it always observes the latest user selection.
- `SpectatorTransportFactory : Func<INetworkTransport>` — invoked from `SpectateAsync(host, port, roomId, ct)` so the library owns the transport instance. The escape-hatch overload `SpectateAsync(transport, host, port, roomId, ct)` is retained for custom transports.

`KlothoSessionFlowAsync.JoinAsync(transport, host, port, preJoin, roomId, sessionCfg, ct)` is retained as an `[Obsolete]` forwarding shim — migrate new call sites to `JoinP2PAsync` / `JoinServerDrivenAsync`. Scheduled removal: 0.3.0.

### 3.3 INetworkServiceReceiver — opt-in NetworkService handle

`ISimulationCallbacks` implementations that need the `IKlothoNetworkService` handle on host/guest entry declare it via the `INetworkServiceReceiver` marker. `KlothoSessionFlow` dispatches `SetNetworkService` automatically right after `OnSessionCreated` — gated to Host/Guest kinds, non-null callbacks, and the `is INetworkServiceReceiver recv` pattern. Implementations that don't need the handle simply omit the interface and avoid empty-body methods.

```csharp
public class MySimulationCallbacks : ISimulationCallbacks, INetworkServiceReceiver
{
    private IKlothoNetworkService _net;
    public void SetNetworkService(IKlothoNetworkService svc) { _net = svc; }
    // ... regular ISimulationCallbacks members ...
}
```

Spectator / replay kinds skip the dispatch at the Flow boundary, so games no longer need `if (!isSpectator && !isReplay)` guards around the call.

### 3.4 IKlothoEngine.IssueOnce — reliable-once command transactions

For commands that must reach the deterministic timeline exactly once despite Duplicate / PastTick rejects, use `engine.IssueOnce(Func<ICommand> commandFactory, ReliabilityPolicy policy = null)`. The framework `ReliableCommandTracker` owns retry-interval cooldown, past-tick escalation (`ExtraDelayStep` bump, capped at `ExtraDelayMax`), empty-move collision avoidance, and `OnResyncCompleted` reset.

```csharp
private Func<ICommand>          _spawnBuilder;
private IReliableCommandHandle  _spawnHandle;

// In ctor — bind once (single-alloc, payload re-evaluated per retry)
_spawnBuilder = () => new SpawnCharacterCommand(_selectedClass);

// Issue
_spawnHandle = engine.IssueOnce(_spawnBuilder);   // ReliabilityPolicy.Default

// OnPollInput integration — handle-aware empty-move skip + state-driven ack
if (_spawnHandle != null && _spawnHandle.WouldCollideAt(tick)) return;
if (HasCharacterFor(playerId)) _spawnHandle?.Confirm();
```

`IReliableCommandHandle` surface: `WouldCollideAt(tick)` (caller-side empty-move skip), `Confirm()` (state-driven ack — caller decides), `Cancel()` (caller-side abort), `OutstandingTargetTick`, `OnRejected` / `OnResolved` events. `ReliabilityPolicy.Default` (RetryIntervalTicks=20 / ExtraDelayStep=4 / ExtraDelayMax=40 / TreatDuplicateAsAck=true / TreatPastTickAsEscalation=true) matches the prior Brawler spawn invariant. Construct a custom policy for other reliable-input scenarios (e.g. `TreatDuplicateAsAck=false` when the same logical command can legitimately fire multiple times).

### 3.5 EcsSimulation.GetSystem<T> — registered-system lookup

`EcsSimulation.GetSystem<T>()` / `TryGetSystem<T>(out T)` / `GetSystems<T>(List<T> buffer)` return the first registered system instance matching `T` (`T : class`). Lets a callback boundary expose a registered system's secondary interface without a process-wide static slot. Stash the `EcsSimulation` reference on `RegisterSystems` entry and resolve in the property getter:

```csharp
public class MyCallbacks : ISimulationCallbacks
{
    private EcsSimulation _simulation;

    public void RegisterSystems(EcsSimulation simulation)
    {
        _simulation = simulation;                                  // stash for lookup
        // ... AddSystem(...) ...
    }

    // IFPPhysicsProviderSource consumer (e.g. FPPhysicsWorldVisualizer)
    public IFPPhysicsWorldProvider PhysicsProvider
        => _simulation?.GetSystem<PhysicsSystem>();                // first-match, alloc-free
}
```

`GetSystem<T>()` traversal order matches `AddSystem` registration order. For multi-instance lookups, `GetSystems<T>(buffer)` appends every match into a caller-owned `List<T>` (alloc-free for the lookup itself; the buffer manages its own capacity).

---

## 4. Entity Prototype API

Implement `IEntityPrototype` to encapsulate entity-creation logic. Two creation paths:

1. **`frame.CreateEntity(int prototypeId)`** — registered prototype lookup. Use when the prototype carries no per-spawn data.
2. **`frame.CreateEntity<TPrototype>(in TPrototype prototype)`** — typed overload. Use when the prototype needs per-spawn data (spawn position, faction, etc.). No registry registration required.

```csharp
// Define a prototype — struct is preferred (no boxing under the typed overload).
public struct WarriorPrototype : IEntityPrototype
{
    public const int Id = 100;

    // Per-spawn data (used by the typed-overload path)
    public FPVector3 SpawnPosition;
    public FP64 SpawnRotation;

    public void Apply(Frame frame, EntityRef entity)
    {
        var stats = frame.AssetRegistry.Get<CharacterStatsAsset>(1100);

        frame.Add(entity, new TransformComponent
        {
            Position = SpawnPosition,
            Rotation = SpawnRotation,
        });
        frame.Add(entity, new HealthComponent { CurrentHealth = 100, MaxHealth = 100 });
        frame.Add(entity, new CombatComponent { AttackDamage = 15, AttackRange = FP64.One });
    }
}

// Registered path — register once during RegisterSystems, then create by id
simulation.Frame.Prototypes.Register(WarriorPrototype.Id, new WarriorPrototype());
var e1 = frame.CreateEntity(WarriorPrototype.Id);   // SpawnPosition = default (origin)

// Typed-overload path — carry spawn data on the prototype instance, no registration
var e2 = frame.CreateEntity(new WarriorPrototype { SpawnPosition = spawnPos });
```

`EntityPrototypeRegistry` API: `Register(int prototypeId, IEntityPrototype)`. The typed overload bypasses the registry (no dictionary lookup) but has identical firing-order semantics — `OnEntityCreated` fires before `Apply` for both paths.

### 4.1 TransformComponent in Apply — Position / Rotation initialization pattern

`TransformComponent.PreviousPosition` / `PreviousRotation` drive the view's interpolation (Sample views: PlatformView / CharacterView) and the engine's rollback error correction.

The engine auto-initializes them at first `Frame.Add<TransformComponent>` via a marker field:

- `TransformComponent.PreviousInitialized` (default `false`) — when `Frame.Add` sees this as `false`, the hook copies `Position` → `PreviousPosition`, `Rotation` → `PreviousRotation`, then sets the marker to `true`. The per-tick SavePrev pass at PreUpdate also sets the marker, so any entity that bypasses the Add hook is still covered from the second tick onward.
- Setting `PreviousInitialized = true` in the struct literal **suppresses** the hook — use this when the caller wants the inline `PreviousPosition` value preserved (e.g. an explicit "slide-in" spawn that interpolates from origin).

**Recommended patterns**

```csharp
// (1) Spawn at non-origin, no slide — most common case.
// Hook fires: PreviousPosition := SpawnPosition, marker := true.
public void Apply(Frame frame, EntityRef entity)
{
    frame.Add(entity, new TransformComponent
    {
        Position = SpawnPosition,
        Rotation = SpawnRotation,
    });
}

// (2) Slide-in intent — interpolate from origin to spawn during the first render frame.
// Marker is explicit: PreviousPosition stays at default and is preserved.
frame.Add(entity, new TransformComponent
{
    Position = spawnPos,
    PreviousInitialized = true,
});

// (3) Explicit Previous* — full control, e.g. resuming from a known prior tick.
frame.Add(entity, new TransformComponent
{
    Position         = currentPos,
    PreviousPosition = priorPos,
    Rotation         = currentRot,
    PreviousRotation = priorRot,
    PreviousInitialized = true,
});

// (4) Runtime ref-set after CreateEntity — e.g. teleport an existing entity to a new spot.
// The Add hook cannot observe a post-Add ref-set, so call RefreshPreviousTransform to
// re-sync Previous* with the new Position and suppress the 1-frame interpolation over the jump.
ref var t = ref frame.Get<TransformComponent>(entity);
t.Position    = dest;
t.TeleportTick = frame.Tick;
frame.RefreshPreviousTransform(entity);

// (5) Per-spawn data on the prototype — use the typed-overload path so Apply receives the data.
//     Avoids the ref-set-then-refresh pattern entirely.
var entity = frame.CreateEntity(new WarriorPrototype { SpawnPosition = spawnPos });
```

**Discouraged**

```csharp
// Discouraged — Apply adds a default TransformComponent, then the caller ref-sets Position later
// without calling RefreshPreviousTransform. The Add hook fires with Position == default and the
// marker becomes true, so the subsequent ref-set leaves PreviousPosition stale at the origin.
// Result: a one-frame interpolation from origin to the ref-set Position. Either pass spawn data
// through the prototype (recommended) or call frame.RefreshPreviousTransform(entity) after the
// ref-set.
var entity = frame.CreateEntity(prototypeId);
ref var t = ref frame.Get<TransformComponent>(entity);
t.Position = spawnPos;
// (no RefreshPreviousTransform call — silent regression)
```

---

## 5. Command Definition API


### Built-in Commands

| Command | Purpose |
|---|---|
| `MoveCommand` | Move-target specification |
| `ActionCommand` | Generic action |
| `SkillCommand` | Skill use |
| `EmptyCommand` | No input (padding) |

### Command Definition Pattern

```csharp
[KlothoSerializable(10)]
public partial class AttackCommand : CommandBase
{
    [KlothoOrder]
    public EntityRef Target;
    [KlothoOrder]
    public int SkillId;

    // CommandType, SerializeData, DeserializeData are emitted by the source generator
}
```

Adding `[KlothoSerializable(N)]` auto-generates `CommandType`, `SerializeData`, and `DeserializeData`. Duplicate TypeIds are caught at compile time.

---

## 6. Event API

### Event Flow

```
Inside the simulation (an ECS System)
    │
    │  EventSystem.Enqueue(new DamageEvent { ... })
    │  or frame.EventRaiser.RaiseEvent(new DamageEvent { ... })
    ▼
ISimulationEventRaiser (EventCollector)
    │
    ▼
IKlothoEngine event callbacks (view layer)
    ├── OnEventPredicted(tick, event)    — fired on a Predicted tick (first firing)
    ├── OnEventConfirmed(tick, event)    — fired directly on a Verified tick without a Predicted firing
    │                                      (verified-direct, replay, new-on-rollback / content change)
    │                                      — no re-fire when a Predicted firing preceded
    ├── OnEventCanceled(tick, event)     — event invalidated by rollback
    └── OnSyncedEvent(tick, event)       — fired only on Verified ticks (EventMode.Synced)
```

### Game Event Definition Pattern

```csharp
[KlothoSerializable(100)]
public partial class DamageEvent : SimulationEvent
{
    [KlothoOrder]
    public EntityRef Target;
    [KlothoOrder]
    public int Damage;

    // EventTypeId, Serialize/Deserialize/GetContentHash are emitted by the source generator
}
```

Adding `[KlothoSerializable(N)]` auto-generates `EventTypeId => TYPE_ID` and the serialization methods. Duplicate TypeIds are caught at compile time.

### EventSystem Wiring Pattern

Construct `EventSystem` without arguments and register it from the `RegisterSystems` hook. It references `frame.EventRaiser` (the `EventCollector` injected by `KlothoEngine`) directly each tick, so there are no init-order issues.

```csharp
// 1. A System that shares a reference to EventSystem
public class CombatSystem : ISystem
{
    private readonly EventSystem _eventSystem;

    public CombatSystem(EventSystem eventSystem)
    {
        _eventSystem = eventSystem;
    }

    public void Update(ref Frame frame)
    {
        // ... compute damage ...
        _eventSystem.Enqueue(new DamageEvent { Target = target, Damage = 10 });
    }
}

// 2. Register inside RegisterSystems
var events = new EventSystem();
sim.AddSystem(new CombatSystem(events), SystemPhase.Update);
sim.AddSystem(events,                   SystemPhase.LateUpdate);
```

---

## 7. Frame Access API (View Layer)

### Render-Update Pattern

```csharp
private void OnTickExecuted(int tick)
{
    var frame = _simulation.Frame;

    var filter = frame.Filter<TransformComponent>();
    while (filter.Next(out var entity))
    {
        ref readonly var t = ref frame.GetReadOnly<TransformComponent>(entity);
        GetView(entity).transform.position = t.Position.ToUnityVector3();
    }
}
```

### Filter Coverage

| Filter Type | Supported |
|---|---|
| `frame.Filter<T1>()` | ✅ |
| `frame.Filter<T1, T2>()` | ✅ |
| `frame.Filter<T1, T2, T3>()` | ✅ |
| `frame.Filter<T1, T2, T3, T4>()` | ✅ |
| `frame.Filter<T1, T2, T3, T4, T5>()` | ✅ |
| `frame.FilterWithout<T1, TExclude>()` | ✅ |
| `frame.FilterWithout<T1, T2, TExclude>()` | ✅ |
| `frame.FilterWithout<T1, T2, T3, TExclude>()` | ✅ |
| `frame.FilterWithout<T1, T2, T3, T4, TExclude>()` | ✅ |
| `frame.FilterWithout<T1, T2, T3, T4, T5, TExclude>()` | ✅ |

### View transform pipeline

`EntityView` base class handles lerp + `_errorVisual` composition + `VerifiedFrameInterpolator` branching as the
standard path. Every view receives one `ApplyTransform` call per frame (during Unity LateUpdate). Subclasses only
override `ApplyTransform` when special split is required (e.g. root vs interpolation target); regular views just
inherit the base path.

Per-tick game-data updates (animator parameters / Renderer toggle / VFX SetActive) belong in `OnUpdateView` — it
fires once per tick, before the per-frame transform application.

When `ViewFlags.EnableSnapshotInterpolation` is set (typically SD-Client / Spectator remote views), the base path
skips `_errorVisual` composition — the verified-frame interpolation already renders the authoritative state, so
applying rollback-delta-based offset would double-correct and jitter.

### Engine event subscription

`Engine.OnEventPredicted` / `OnEventConfirmed` / `OnEventCanceled` follow an idempotent dispatch pattern. The
`EngineEventOneShot.Subscribe<TEvent>(engine, filter, onPlay, onCancel?, lateGuard?)` helper absorbs the three-way
subscription:

- Predicted and Confirmed are hash-deduped by the engine → `onPlay` fires once per logical event in normal cases.
- On rollback mismatch: Canceled fires `onCancel` first, then Confirmed re-fires `onPlay` with the corrected event.
- `lateGuard` (optional) returns `false` to skip stale `onPlay` after the action's natural end (late-rollback case).

The returned `EngineEventSubscription` is `IDisposable` — call `Dispose()` in `OnDeactivate` to unsubscribe and
release captured lambdas (required to avoid component leak through the engine event delegate).

```csharp
private EngineEventSubscription _attackSub;

public override void OnActivate(FrameRef frame)
{
    _attackSub = EngineEventOneShot.Subscribe<AttackActionEvent>(
        Engine,
        filter:    e => e.Attacker.Index == EntityRef.Index,
        onPlay:    e => PlayAttackAnimation(),
        onCancel:  _ => CancelActionTrigger(),
        lateGuard: HasActiveAction);
}

public override void OnDeactivate()
{
    _attackSub?.Dispose();
    _attackSub = null;
}
```

`OnSyncedEvent` (verified-time channel without Cancel pair) is intentionally outside this helper's scope — subscribe
directly when verified-time fallback Stop is needed.

---

## 8. Entity Lifecycle API

```csharp
// Create an entity
var entity = frame.CreateEntity();
frame.Add(entity, new TransformComponent { ... });
frame.Add(entity, new OwnerComponent { OwnerId = playerId });

// Destroy an entity
frame.DestroyEntity(entity);  // all components removed automatically

// Validity check
bool alive = frame.Entities.IsAlive(entity);
```

### System-to-System Signal Pattern

```csharp
// Define a Signal
public interface ISignalOnDamage : ISignal
{
    void OnDamage(ref Frame frame, EntityRef target, int damage);
}

// Raise (inside CombatSystem)
_systemRunner.Signal<ISignalOnDamage>(ref frame,
    (sys, ref f) => sys.OnDamage(ref f, target, damage));

// Receive (in another System)
public class EffectSystem : ISystem, ISignalOnDamage
{
    public void OnDamage(ref Frame frame, EntityRef target, int damage) { ... }
    public void Update(ref Frame frame) { }
}
```

---

## 9. Spectator Session API

Spectator mode is bootstrapped through a dedicated factory. The framework owns `SpectatorService`, the two-config await (`SimulationConfig` + `SessionConfig` arrive in `SpectatorAcceptMessage`), and `Engine` / `Simulation` construction. The game only supplies:

- The connection target (`HostAddress`, `Port`, `RoomId`)
- An `IKlothoSessionObserver` (optional — same lifecycle hooks as the regular client)
- A `CallbacksFactory` that runs **after** server config arrives, so callbacks can size against server-authoritative values

```csharp
// Spectator entry — delegated to KlothoSessionFlow. CallbacksFactory is supplied once at Flow
// construction (game-wide) and fires after SpectatorAcceptMessage delivers server-authoritative
// SimulationConfig + SessionConfig, so callbacks can size against the on-the-wire values.

// Recommended: no-transport overload. The library calls KlothoFlowSetup.SpectatorTransportFactory
// to instantiate the transport (register the factory once during Flow construction).
_session = await _flow.SpectateAsync(host, port, roomId, ct);

// Escape hatch: pass a custom transport instance.
var spectatorTransport = new LiteNetLibTransport(_logger, connectionKey: ConnectionKey);
_session = await _flow.SpectateAsync(spectatorTransport, host, port, roomId, ct);
```

The Flow's `CallbacksFactory` (set on `KlothoFlowSetup`) is invoked once the server config arrives:

```csharp
private SessionCallbacks BuildCallbacks(ISimulationConfig simCfg, ISessionConfig sessionCfg)
{
    // sessionCfg.MaxPlayers is server-authoritative — size callbacks against it,
    // not against any local Inspector value.
    var simCallbacks  = new MySimulationCallbacks(sessionCfg.MaxPlayers);
    var viewCallbacks = new MyViewCallbacks(simCallbacks);
    return new SessionCallbacks(simCallbacks, viewCallbacks);
}
```

The returned object is a regular `KlothoSession`: drive it through `KlothoSessionDriver.Attach(_session)` (same hook pattern as host/guest sessions) and observe lifecycle via the same `IKlothoSessionObserver`. There is no separate spectator-only Engine/Simulation field for the game to track. Spectator is identified at runtime via `session.Engine.IsSpectatorMode` (canonical signal — not `NetworkService == null` heuristic).

`KlothoSession.CreateSpectator(SpectatorSessionSetup)` remains as the synchronous escape hatch (see §X Escape Hatch APIs) for advanced users whose architecture does not fit the Flow pattern.

Notes:
- `SpectatorSessionSetup` has no `CredentialsStore`, no `SessionConfig`, and no `MaxPlayers` field. Those values either do not apply to spectators or arrive over the wire.
- The engine's error-correction path (`CapturePreRollback` / `ComputeErrorDeltas` / Predict-under-Predicted) is active in spectator mode so smoothing applies to spectator views as it does to regular clients.
- **Spectator player list surface**: `ISpectatorService.PlayerCount` and `event OnPlayerCountChanged` mirror the network-service equivalents. The host (P2P) / server (SD) extends its `LateJoinNotificationMessage` (NetworkMessageType=75) broadcast to the `_spectators` set so spectators see existing players appear / late-joiners arrive without polling. Subscribe via `KlothoSession.PlayerCountChanged` — the session forwards both network-service and spectator-service `OnPlayerCountChanged` so the same subscriber works across all modes.

---

## 10. Dynamic InputDelay (client-reactive policy)

Non-host sessions automatically attach a `DynamicInputDelayPolicy` (in `Packages/com.xpturn.klotho/Runtime/Core/Engine/`) that escalates `engine.RecommendedExtraDelay` when the server-driven push control falls behind:

- **Trigger A — PastTick reject sliding window**: non-spawn `CommandRejected(PastTick)` events accumulate within a tick-based window (`SessionConfig.ReactiveWindowTicks`); when the count crosses `ReactiveEscalateThreshold`, the policy calls `engine.EscalateExtraDelay(ReactiveStep, ReactiveMax)`.
- **Trigger B — rollback burst**: rollback events accumulate within `SessionConfig.RollbackWindowTicks`; reaching `RollbackBurstCount` triggers the same escalation. Primary fallback for P2P guests (no `CommandRejectedMessage`).
- **Grace gate**: both triggers ignore events within `ServerPushGraceTicks` of the last server `RecommendedExtraDelayUpdate` push (refreshed via `OnExtraDelayChanged`). Prevents double-counting against the authoritative path.
- **Cooldown**: rollback-triggered escalations require `ReactiveEscalateCooldownTicks` between firings.

Thresholds live in `SessionConfig` and are server-authoritative. Games typically do not subscribe to `OnCommandRejected` or `OnRollbackExecuted` for delay control — only for game-specific responses (e.g. spawn-cmd retry shaping).

---

## 11. Attribute ID planes (mutually independent)

Klotho exposes three positional-int attributes that look syntactically similar but live in independent ID planes:

| Attribute | Plane | Range / convention |
|---|---|---|
| `[KlothoComponent(ComponentTypeId)]` | ECS Frame Heap component discriminator | `0..UserMinId-1` reserved for runtime; user range >= `KlothoComponentAttribute.UserMinId` (100). |
| `[KlothoSerializable(TypeId)]` | Entity / Command / Message wire discriminator (per category) | Distinct sub-planes per base class — `EntityBase` / `CommandBase` / `NetworkMessageBase` do not share IDs. |
| `[KlothoDataAsset(TypeId, AssetId = ..., Key = ...)]` | DataAsset wire discriminator (`TypeId`) + runtime instance id (`AssetId`) + optional `Key` | `TypeId` is wire-stable. `AssetId` (named) is the runtime instance id used by `IDataAssetRegistry.Get<T>()`. `Key` (named) is an optional string handle for `GetByKey<T>(string)`. Generator auto-emits `AssetId` property + `ctor(int)` + (when `AssetId` is set) parameterless `ctor() : this(AssetIdFromAttribute)`. |

These planes do not collide. `[KlothoComponent(100)]` and `[KlothoDataAsset(100)]` can coexist on different types without conflict.

#### DataAsset lookup overloads

`IDataAssetRegistry` exposes typed lookups that auto-resolve the `AssetId` / `Key` named-args on `[KlothoDataAsset]`:

| Overload | Resolution |
|---|---|
| `Get<T>()` / `TryGet<T>(out T)` | Reads the `AssetId` named-arg on `T`'s attribute. Throws `InvalidOperationException` when the asset omits `AssetId` (single-instance assets only). |
| `GetByKey<T>(string)` / `TryGetByKey<T>(string, out T)` | Reads the `Key` named-arg on `T`'s attribute. Backed by a `(Type, string)` tuple index built at `Register` time. |
| `Get<T>(int id)` / `TryGet<T>(int id, out T)` | Caller-supplied id literal — for multi-instance assets where the same class has multiple registered instances (e.g. `BotDifficultyAsset 1700..1702`). |

The first two are the preferred entry points for single-instance assets — no magic-id literal at the call site. The third remains the escape hatch for multi-instance fan-out where the id is part of the domain (slot index, class index, etc.).

### User-defined NetworkMessageType values

`NetworkMessageType.UserDefined_Start = 200` reserves the byte range >= 200 for game-specific message types. Games may cast freely past this point — both:

```csharp
[KlothoSerializable(MessageTypeId = (NetworkMessageType)200)]   // generator emits raw-cast override
[KlothoSerializable(MessageTypeId = (NetworkMessageType)201)]   // generator emits raw-cast override
```

are auto-handled by the generator. There is no need to manually override `MessageTypeId` — the generator emits the override and the factory registration for both enum-named and raw-cast values. Values below 200 must match a defined `NetworkMessageType` member; unknown sub-200 values are silently skipped (the base class abstract `MessageTypeId` will then fail to compile, surfacing the mistake).

---

*Last updated: 2026-05-25*
