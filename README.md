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
- **xpTURN.Klotho.Logging (IKLogger)** — in-house structured logging (no external logging dependency). Optional MEL interop via the `Plugins~/Logging.Mel` sample adapter (`Microsoft.Extensions.Logging.Abstractions` DLL is consumer-provided).
- **LiteNetLib** — default reference implementation for UDP transport (MIT, pure C#, vendored under `Runtime/ThirdParty/LiteNetLib.v2.1.4`). The network transport layer is abstracted via the `INetworkTransport` interface and can be replaced with any other library.
- **Newtonsoft.Json** — DataAsset JSON serialization (`com.unity.nuget.newtonsoft-json`)
- **K4os.Compression.LZ4** — replay compression (vendored under `Runtime/ThirdParty/`)

Details: [Docs/BaseLibraries.md](Docs/BaseLibraries.md)

---

## Installation

1. Open **Window > Package Manager**
2. Click **+** > **Add package from git URL...**
3. Enter each URL in order (the first two are required git dependencies that UPM cannot auto-resolve):

```text
https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask
https://github.com/xpTURN/Polyfill.git?path=src/Polyfill/Assets/Polyfill
https://github.com/xpTURN/Klotho.git?path=Klotho/Packages/com.xpturn.klotho
```

Pin a specific Klotho version with `#vX.Y.Z` (e.g. `https://github.com/xpTURN/Klotho.git?path=Klotho/Packages/com.xpturn.klotho#v0.2.4`).

Unity registry packages (`com.unity.inputsystem`, `com.unity.ai.navigation` for the NavMesh exporter, `com.unity.nuget.newtonsoft-json`) resolve automatically via the package's `dependencies` field.

### Optional samples

After install, open Unity Package Manager → select **xpTURN.Klotho** → **Samples** → "Import" to copy the **MEL Logging Plugin** adapter into your `Assets/Samples/`. Activating the adapter still requires you to supply `Microsoft.Extensions.Logging.Abstractions` (consumer-provided).

Heavier demos (Brawler, NavMesh) are not bundled in the package — clone this repo if you want to inspect or modify them.

### Dedicated server

Klotho's source generator requires the dedicated-server build to compile core sources in the same compilation unit (binary distribution is not viable). Two install patterns:

**A. git submodule (recommended)** — vendor this repo into your game project as a submodule under `<yourGame>/Packages/com.xpturn.klotho`. The `Server~/KlothoServer.Core.props` resolves all core paths via `$(MSBuildThisFileDirectory)`, so your server csproj just imports it at the correct relative depth:

```xml
<!-- Server csproj at <yourGame>/MyDedicatedServer.csproj (project root) -->
<Import Project="Packages\com.xpturn.klotho\Server~\KlothoServer.Core.props" />

<!-- Server csproj at <yourGame>/Tools/MyDedicatedServer/MyDedicatedServer.csproj (nested 2 levels) -->
<Import Project="..\..\Packages\com.xpturn.klotho\Server~\KlothoServer.Core.props" />
```

Adjust the `..\` depth to match where your csproj sits relative to the Unity project root.

**B. UPM `Library/PackageCache` + `<KlothoPackageRoot>`** — if you don't want a submodule, set `<KlothoPackageRoot>` per-project to your resolved PackageCache path. The `@<hash>` suffix changes on every pull, so this needs occasional refresh:

```xml
<PropertyGroup>
  <KlothoPackageRoot>$(MSBuildProjectDirectory)\..\..\Library\PackageCache\com.xpturn.klotho@1a2b3c4d</KlothoPackageRoot>
</PropertyGroup>
<Import Project="$(KlothoPackageRoot)\Server~\KlothoServer.Core.props" />
```

Full guide (with `Program.cs`, callbacks, config files, single-room/multi-room/test CLI): [Docs/Samples/Brawler.H.DedicatedServer.md](Docs/Samples/Brawler.H.DedicatedServer.md) — a Brawler-specific reference you can copy and adapt to your game.

---

## Repository Layout

Klotho ships as an embedded Unity Package (`com.xpturn.klotho`) located at `Klotho/Packages/com.xpturn.klotho/`. The dev project under `Klotho/` is the source-of-truth host; consumers install via UPM (see [Installation](#installation)).

```
Klotho/                                        ← Unity dev project (this repo)
├── Packages/com.xpturn.klotho/                ← ★ framework package (UPM)
│   ├── package.json
│   ├── Runtime/
│   │   ├── Core/              KlothoEngine · KlothoSession · ISimulationCallbacks · IViewCallbacks
│   │   │                      · ISimulationConfig · ISessionConfig · Command/Event/Pool families
│   │   ├── Logging/           xpTURN.Klotho.Logging (IKLogger — in-house structured logging)
│   │   ├── Gameplay/          built-in component / system reference implementations
│   │   ├── Diagnostics/       FaultInjection · RttSpikeMetricsCollector
│   │   ├── Input/             InputBuffer · SimpleInputPredictor
│   │   ├── Network/           IKlothoNetworkService · ServerDriven · ServerNetwork · Spectator
│   │   │                      · Reconnect/LateJoin · Messages
│   │   ├── State/             RingSnapshotManager
│   │   ├── Serialization/     SpanWriter/Reader · SerializationBuffer
│   │   ├── Replay/            IReplaySystem · ReplayRecorder · ReplayPlayer · LZ4 compression
│   │   ├── ECS/               Frame · EntityManager · ComponentStorage<T> · SystemRunner
│   │   │                      · DataAsset/ (IDataAsset · Registry · Json)
│   │   ├── Deterministic/     FP64 · FPVector* · Physics · Navigation · Random
│   │   ├── Unity/             USimulationConfig · USessionConfig · View/ · UnityDebugSink
│   │   ├── LiteNetLib/        LiteNetLibTransport (INetworkTransport implementation)
│   │   └── ThirdParty/        vendored: LiteNetLib.v2.1.4, K4os.Compression.LZ4.v1.3.8, ...
│   ├── Editor/                NavMesh · Physics · ECS · FSM · DataAsset tooling
│   ├── Plugins/Analyzers/     KlothoGenerator.dll (Roslyn source generator, RoslynAnalyzer label)
│   ├── Prefabs/               debug/visualization prefabs (EcsDebugBridge · FPPhysics*Visualizer)
│   ├── Plugins~/Logging.Mel/  opt-in MEL interop adapter (UPM "Import Sample")
│   └── Server~/               dedicated-server build assets (MSBuild props + Config helpers)
│
├── Assets/                                    ← dev-only (not redistributed via UPM)
│   ├── Brawler/               4-player fighting-game sample
│   ├── NavMesh/               navmesh sample
│   ├── Tests/                 unit / integration / determinism-verification tests
│   ├── Benchmarks/            performance benchmarks
│   └── Scenes/  Settings/  StreamingAssets/  ...
│
└── Tools/                                     ← .NET tooling (not redistributed)
    ├── KlothoGenerator/       Roslyn source generator (`IIncrementalGenerator`) — built by gen.build.sh
    ├── BrawlerDedicatedServer/  Brawler dedicated server (.NET console)
    ├── DeterminismVerification/ determinism verification (.NET console)
    ├── Generated/             reference copies of generated `.g.cs` (not included in Unity builds)
    └── gen.build.sh           generator build script
```

---

## Quick Start

### 1) Define a Component

```csharp
[KlothoComponent(100)]  // 1–99: framework, 100+: game
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

[Klotho/Assets/Brawler](Klotho/Assets/Brawler) — a 4-player fighting-game sample (in the dev project of this repo)

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
