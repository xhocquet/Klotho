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
    // Runs identically on all peers — deterministic code only.
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

Bind the `IKlothoSession` to a `UKlothoBehaviour` and the session is driven automatically from MonoBehaviour Update.

```csharp
GetComponent<UKlothoBehaviour>().Bind(session);
```

`IKlothoSession` API: `Engine`, `Simulation`, `LocalPlayerId`, `State`, `Update(float dt)`, `InputCommand(ICommand)`, `Stop()`

`KlothoSession` convenience methods: `HostGame(name, maxPlayers)`, `JoinGame(name)`, `LeaveRoom()`, `SendPlayerConfig(PlayerConfigBase)`, `SetReady(bool)`

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
    │                                      — no re-fire when a Predicted firing preceded (IMP-21)
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
_session = await KlothoSpectatorAsync.CreateAsync(new SpectatorSessionSetup
{
    Logger            = _logger,
    AssetRegistry     = _assetRegistry,
    Transport         = transport,
    HostAddress       = host,
    Port              = port,
    RoomId            = roomId,                // -1 for P2P / single SD room
    LifecycleObserver = this,                  // implements IKlothoSessionObserver

    CallbacksFactory = (simCfg, sessionCfg) =>
    {
        // sessionCfg.MaxPlayers is server-authoritative — size callbacks against it,
        // not against any local Inspector value.
        var simCallbacks  = new MySimulationCallbacks(sessionCfg.MaxPlayers);
        var viewCallbacks = new MyViewCallbacks(simCallbacks);
        return new SpectatorCallbacks(simCallbacks, viewCallbacks);
    },
}, ct);
```

The returned object is a regular `KlothoSession`: drive it with the same `_session.Update(dt)` loop and treat lifecycle via the same `IKlothoSessionObserver`. There is no separate spectator-only Engine/Simulation field for the game to track.

`KlothoSession.CreateSpectator(SpectatorSessionSetup)` is the synchronous factory underneath — `KlothoSpectatorAsync.CreateAsync` is the UniTask convenience wrapper that also drives the transport `Connect` and the SpectatorAcceptMessage await.

Notes:
- `SpectatorSessionSetup` has no `CredentialsStore`, no `SessionConfig`, and no `MaxPlayers` field. Those values either do not apply to spectators or arrive over the wire.
- The engine's error-correction path (`CapturePreRollback` / `ComputeErrorDeltas` / Predict-under-Predicted) is active in spectator mode so smoothing applies to spectator views as it does to regular clients.

---

## 10. Dynamic InputDelay (client-reactive policy)

Non-host sessions automatically attach a `DynamicInputDelayPolicy` (in `Assets/Klotho/Runtime/Core/Engine/`) that escalates `engine.RecommendedExtraDelay` when the server-driven push control falls behind:

- **Trigger A — PastTick reject sliding window**: non-spawn `CommandRejected(PastTick)` events accumulate within a tick-based window (`SessionConfig.ReactiveWindowTicks`); when the count crosses `ReactiveEscalateThreshold`, the policy calls `engine.EscalateExtraDelay(ReactiveStep, ReactiveMax)`.
- **Trigger B — rollback burst**: rollback events accumulate within `SessionConfig.RollbackWindowTicks`; reaching `RollbackBurstCount` triggers the same escalation. Primary fallback for P2P guests (no `CommandRejectedMessage`).
- **Grace gate**: both triggers ignore events within `ServerPushGraceTicks` of the last server `RecommendedExtraDelayUpdate` push (refreshed via `OnExtraDelayChanged`). Prevents double-counting against the authoritative path.
- **Cooldown**: rollback-triggered escalations require `ReactiveEscalateCooldownTicks` between firings.

Thresholds live in `SessionConfig` and are server-authoritative. Games typically do not subscribe to `OnCommandRejected` or `OnRollbackExecuted` for delay control — only for game-specific responses (e.g. spawn-cmd retry shaping).

---

*Last updated: 2026-05-20*
