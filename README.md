# xpTURN.Klotho
[![Unity 2022.3+](https://img.shields.io/badge/unity-2022.3%2B-blue.svg)](https://unity3d.com/get-unity/download)
[![License: Apache License Version 2.0](https://img.shields.io/badge/License-Apache-brightgreen.svg)](https://github.com/xpTURN/Klotho/blob/main/LICENSE)

**Deterministic Multiplayer Simulation Framework for Unity**

> ⚠️ **Experimental Release**
> This project is under active development and has not yet reached a stable stage. Public APIs, serialization formats, and network protocols may change without notice. Production use is not recommended.

A Unity-based framework supporting Client-Side Prediction (CSP), Rollback, Frame Synchronization, Server-Driven mode, and Replay. By excluding floating-point and building the simulation solely on 32.32 fixed-point (`FP64`) and a deterministic RNG (Xorshift128+), it guarantees full reproducibility across platforms and compilers.

> Klotho weaves the simulation, one frame at a time.

---

## Key Features

| Area | Contents |
| ---- | ---- |
| **Network Models** | P2P Lockstep · Rollback + Client Prediction · Server-Driven (Authoritative) · Dedicated Server · Spectator · Late Join · Reconnect |
| **Deterministic Math** | `FP64` (32.32 fixed-point) · `FPVector2/3/4` · `FPQuaternion` · `FPMatrix` · LUT/CORDIC-based trigonometry · `DeterministicRandom` (Xorshift128+) |
| **Physics** | `FPPhysicsWorld` · Broadphase (SpatialGrid) · Narrowphase · CCD (Sweep) · Constraint Solver · Joints · Triggers · Static BVH |
| **Navigation** | `FPNavMesh` · A* (triangle graph) · Funnel (SSFA) · ORCA avoidance · ECS-integrated `NavAgentComponent` |
| **ECS** | Sparse-set `ComponentStorage<T>` · `Frame` (single byte[] heap) · `FilterWithout/Filter<T1..T5>` · `FrameRingBuffer` · `SystemRunner` |
| **Serialization / Source Generator** | `SpanWriter/Reader` (ref struct, GC-free) · automatic code generation via `[KlothoComponent]` / `[KlothoSerializable]` / `[KlothoDataAsset]` |
| **Data Assets** | `IDataAsset` · `DataAssetRegistry` · `DataAssetRef` · JSON serialization (`xpTURN.Klotho.DataAsset.Json`) |
| **Replay** | Record / playback / seek / variable speed · LZ4 compression (`K4os.Compression.LZ4`) |
| **Verification Tools** | `SyncTestRunner` (GGPO-style determinism verification) · `DeterminismVerificationRunner` · benchmark suite |
| **Unity Integration** | `USimulationConfig` · `USessionConfig` · View layer (`EntityViewFactory` / `EntityViewUpdater` / `EntityView`, `BindBehaviour` / `ViewFlags`, `VerifiedFrameInterpolator`) |

---

## Suitable Genres

Genres that benefit most from Klotho's deterministic simulation features.

### Best Fit (core targets)

- **Fighting games** — frame-perfect inputs + rollback netcode
- **Platform fighters / arena brawlers**
- **2–4 player PvP action** — optimal for small-roster P2P rollback
- **Tactics / turn-based SRPG** — determinism + replay + sync verification
- **Real-time strategy (RTS)** — lockstep frame sync
- **MOBA / top-down arena** — ECS, physics, and navigation all included
- **Twin-stick shooters / co-op shooters** — deterministic physics + ORCA avoidance
- **Auto-battlers** — deterministic simulation + shareable replays

### Good Fit (structurally well-suited)

- **Roguelike / roguelite (PvE co-op)** — deterministic RNG (Xorshift128+) for shared seeds and replays
- **Card / board / deckbuilder PvP** — low input volume, strong verification and replay
- **Puzzle PvP / falling-block versus**
- **Racing (small scale)** — fixed-point physics guarantees determinism for small grids
- **Top-down survival action**

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                    Game Application                         │
│   (ISimulation impl: EcsSimulation or custom Simulation)    │
└───────────────────────────┬─────────────────────────────────┘
                            │
         ┌──────────────────┼──────────────────┐
         ▼                  ▼                  ▼
  ┌───────────────┐   ┌──────────────┐   ┌────────────────────┐
  │ KlothoEngine  │   │ ReplaySystem │   │KlothoNetworkService│
  │ (orchestrator)│◄  │ (record/play)│   │                    │
  └──────┬────────┘   └──────────────┘   └─────────┬──────────┘
         │                                         │
    ┌────┴────┐                              ┌─────┴──────┐
    ▼         ▼                              ▼            ▼
 ISimulation  InputBuffer               INetworkTransport  NetworkMessages
    │
    ├── (ECS) Frame — EntityManager + ComponentStorage[]
    ├── SystemRunner — ISystem[]
    ├── FrameRingBuffer (snapshots / rollback)
    └── DataAssetRegistry
```

Three-layer separation:
- **Klotho engine layer** — Pure C#, Unity-independent. The same binary also runs on the server side (.NET console / ASP.NET).
- **Simulation transport layer** — UDP transport over the `INetworkTransport` abstraction (Input / InputAck / SyncCheck / Handshake). LiteNetLib is provided as the default reference implementation; replaceable with any other library.
- **Game service layer** — Lobby / matchmaking / authentication (external gRPC, etc.; integrated outside this project).

---

## Tech Stack

- **Unity 2022.3+**
- **C# 8.0** (some assemblies opt into newer C# language features via the `xpTURN.Polyfill` package, which supplies the runtime-attribute shims required by C# 11)
- **UniTask** — async (Cysharp)
- **ZLogger + Microsoft.Extensions.Logging** — structured logging
- **LiteNetLib** — default reference implementation for UDP transport (MIT, pure C#). The network transport layer is abstracted via the `INetworkTransport` interface and can be replaced with any other library.
- **Newtonsoft.Json** — DataAsset JSON serialization
- **K4os.Compression.LZ4** — replay compression

Details: [Docs/BaseLibraries.md](Docs/BaseLibraries.md)

---

## Repository Layout

```
Assets/Klotho/
├── Runtime/
│   ├── Core/            KlothoEngine · KlothoSession · ISimulationCallbacks
│   │                    · IViewCallbacks · ISimulationConfig · ISessionConfig
│   │                    · Command/Event/Pool families
│   ├── Input/           InputBuffer · SimpleInputPredictor
│   ├── Network/         IKlothoNetworkService · ServerDrivenClientService
│   │                    · ServerNetworkService · Spectator/Reconnect/LateJoin
│   │                    · Messages
│   ├── State/           RingSnapshotManager
│   ├── Serialization/   SpanWriter/Reader · SerializationBuffer
│   ├── Replay/          IReplaySystem · ReplayRecorder · ReplayPlayer · LZ4 compression
│   ├── ECS/             Frame · EntityManager · ComponentStorage<T> · SystemRunner
│   │                    DataAsset/ (IDataAsset · Registry · Json)
│   └── Deterministic/   FP64 · FPVector* · Physics · Navigation · Random
├── Unity/               USimulationConfig · USessionConfig · View/
├── Editor/              NavMesh · Physics · ECS · FSM · DataAsset tooling
├── Gameplay/            built-in component / system reference implementations
├── LiteNetLib/          LiteNetLibTransport (INetworkTransport implementation)
├── Samples/             Brawler (fighting-game sample)
└── Tests/               unit / integration / determinism-verification tests

Tools/
├── KlothoGenerator/     Roslyn source generator (`IIncrementalGenerator`)
├── Generated/           reference copies of generated code (not included in Unity builds)
└── gen.build.sh         generator build script
```

---

## Quick Start

### 1) Define a Component

```csharp
[KlothoComponent(100)]  // 1–99 reserved for the framework, 100+ for game developers
public partial struct HeroComponent : IComponent
{
    public int Level;
    public int Experience;
}
```

### 2) Implement a System

```csharp
public class HeroSystem : ISystem
{
    public void Update(ref Frame frame)
    {
        var filter = frame.Filter<HeroComponent, HealthComponent>();
        while (filter.Next(out var entity))
        {
            ref var hero = ref frame.Get<HeroComponent>(entity);
            // hero logic
        }
    }
}
```

### 3) Implement Callbacks (determinism / view separation)

```csharp
public class MySimulationCallbacks : ISimulationCallbacks
{
    public void RegisterSystems(EcsSimulation sim) 
    {
         /* AddSystem registrations */
    }
    public void OnInitializeWorld(IKlothoEngine engine)
    {
        /* spawn initial world */
    }
    public void OnPollInput(int playerId, int tick, ICommandSender sender)
    {
        /* send commands */
    }
}

public class MyViewCallbacks : IViewCallbacks
{
    public void OnGameStart(IKlothoEngine engine) { }
    public void OnTickExecuted(int tick) { }
    public void OnLateJoinActivated(IKlothoEngine engine) { }
}
```

### 4) Create a Session via `KlothoSessionFlow`

```csharp
[SerializeField] private KlothoSessionDriver _sessionDriver;
private KlothoSession _session;
private KlothoSessionFlow _flow;

void Awake()
{
    // Driver owns the Update / Stop lifecycle — no manual dt computation needed.
    _sessionDriver.PreSessionUpdate += (s, dt) => _input.CaptureInput();
    _sessionDriver.IdlePoll        += () => transport.PollEvents();
}

void Start()
{
    _flow = new KlothoSessionFlow(new KlothoFlowSetup
    {
        Logger            = logger,
        Transport         = transport,
        AssetRegistry     = dataAssetRegistry,
        LifecycleObserver = this,
        CallbacksFactory  = (simCfg, sessionCfg) =>
            new SessionCallbacks(new MySimulationCallbacks(), new MyViewCallbacks()),
    });
    _flow.OnSessionCreated += s => _sessionDriver.Attach(s);

    _session = _flow.StartHost(uSimulationConfig, uSessionConfig);
    _session.HostGame("MyRoom", maxPlayers: 2);
}
```

`KlothoSessionFlow` exposes 6 entry points — pick one per game mode:

- `StartHost(simCfg, sessionCfg)` — P2P host (synchronous).
- `JoinP2PAsync(transport, host, port, sessionCfg, ct)` — P2P guest join.
- `JoinServerDrivenAsync(transport, host, port, roomId, sessionCfg, ct)` — ServerDriven client join (with roomId).
- `ReconnectAsync(transport, creds, sessionConfigSeed, ct)` — cold-start reconnect; `creds` is a `PersistedReconnectCredentials` (carries `RoomId`, host address, and the magic token). Mode is recovered from the credentials.
- `SpectateAsync(host, port, roomId, ct)` — spectator entry (transport is instantiated by `KlothoFlowSetup.SpectatorTransportFactory`).
- `StartReplayFromFile(path)` — file-to-session replay (throws `ReplayLoadException` on load failure).

If your game-side code needs to branch on the active mode, use `KlothoModeStrategy.Resolve(simCfg)` rather than inspecting `simCfg.Mode` directly. Per-mode session-created callbacks are also available (`OnHostSessionCreated` / `OnGuestSessionCreated` / `OnReplaySessionCreated` / `OnSpectatorSessionCreated`) alongside the generic `OnSessionCreated`. Stop teardown runs through the driver's `Stopping` hook; game code can read `KlothoSessionDriver.IsStopping` to short-circuit re-entrant teardown calls.

Detailed guides: [Docs/GameDevWorkflow.md](Docs/GameDevWorkflow.md), [Docs/GameDevAPI.md](Docs/GameDevAPI.md)

---

## Sample

[Assets/Klotho/Samples/Brawler](Assets/Klotho/Samples/Brawler) — a 4-player fighting-game sample

- ECS-based combat / movement / skills / cooldowns / knockback / items / traps
- HFSM-based bot AI (`BotHFSMRoot` / `BotActions` / `BotDecisions`)
- Camera integration with Unity Cinemachine
- Supports both P2P and ServerDriven modes
- LZ4-compressed replay record / playback

Docs: [Docs/Samples/Brawler.md](Docs/Samples/Brawler.md)

---

## Documentation Map

| Document | Contents |
| ---- | ---- |
| [Docs/FEATURES.md](Docs/FEATURES.md) | Full feature list |
| [Docs/Specification.md](Docs/Specification.md) | Engine specification (state machines · configuration · events · message protocol · formats) |
| [Docs/GameDevWorkflow.md](Docs/GameDevWorkflow.md) | Game-developer workflow (step-by-step) |
| [Docs/GameDevAPI.md](Docs/GameDevAPI.md) | Game-developer API status |
| [Docs/SimulationConfigGuide.md](Docs/SimulationConfigGuide.md) | SimulationConfig recommended-value guide (per genre / platform) |
| [Docs/BaseLibraries.md](Docs/BaseLibraries.md) | List of base libraries used |
| [Docs/Navigation.md](Docs/Navigation.md) | Deterministic navigation (FPNavMesh · A* · Funnel · ORCA) |
| [Docs/Samples/](Docs/Samples/) | Detailed Brawler sample documentation |

---

## Design Principles

- **Determinism first** — no float; all simulation state is FP64 / integer / bool
- **Zero-GC oriented** — ref struct, object pools, cached fields, no LINQ
- **Engine independence** — the core is pure C# (no `UnityEngine` references); Unity integration lives in an adapter layer
- **Minimal bandwidth** — only inputs (commands) are sent; no state synchronization (only hash verification)
- **Layer separation** — strict separation between simulation callbacks (deterministic) and view callbacks (non-deterministic)
