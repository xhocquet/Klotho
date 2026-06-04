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
https://github.com/xpTURN/Klotho.git?path=com.xpturn.klotho
```

Pin a specific Klotho version with `#vX.Y.Z` (e.g. `https://github.com/xpTURN/Klotho.git?path=com.xpturn.klotho`).

Unity registry packages (`com.unity.inputsystem`, `com.unity.ai.navigation` for the NavMesh exporter, `com.unity.nuget.newtonsoft-json`) resolve automatically via the package's `dependencies` field.

### Polyfill activation (C# 9–11 syntax)

Klotho uses C# 11 features (`required`, `init`, custom interpolated string handlers, etc.) in some assemblies. After installing `xpTURN.Polyfill`, enable the language version once per project:

1. Run **Edit > Polyfill > Player Settings > Apply Additional Compiler Arguments -langversion (All Installed Platforms)**.
2. Settings are persisted to **ProjectSettings/xpTURN.Polyfill.Settings.json**.

This adds `-langversion:preview` to Player Settings (Unity build), inserts `<LangVersion>preview</LangVersion>` into regenerated `.csproj` (IDE), and defines the `CSHARP_PREVIEW` scripting symbol. Without this step, Klotho assemblies that rely on C# 11 syntax may fail to compile. Details: [Polyfill README](https://github.com/xpTURN/Polyfill#project-settings-c-langversion).

### Optional samples

After install, open Unity Package Manager → select **xpTURN.Klotho** → **Samples** → "Import" to copy the **MEL Logging Plugin** adapter into your `Assets/Samples/`. Activating the adapter still requires you to supply `Microsoft.Extensions.Logging.Abstractions` (consumer-provided).

**P2pSample** — a minimum P2P sample (2 cubes, 60s sumo match) consuming this package via the same UPM git URL. Located at [`Samples/P2pSample/`](Samples/P2pSample/) in this repo — open the folder directly in Unity Hub. See [`Samples/P2pSample/README.md`](Samples/P2pSample/README.md) for the 4-step quick start, or [`Docs/Samples/P2pSample.md`](Docs/Samples/P2pSample.md) for the architecture walkthrough.

**SdSample** — the ServerDriven sibling of P2pSample (same sumo game, dedicated-server topology): a Unity client + a minimal .NET 8 dedicated server. Located at [`Samples/SdSample/`](Samples/SdSample/) — see [`Samples/SdSample/README.md`](Samples/SdSample/README.md) (server + 2 clients quick start) or [`Docs/Samples/SdSample.md`](Docs/Samples/SdSample.md) (architecture + SD-specific gotchas).

**LoggingMelConsole** — a .NET console sample at [`Samples/LoggingMelConsole/`](Samples/LoggingMelConsole/) that routes Klotho's `IKLogger` surface through a standard `Microsoft.Extensions.Logging` pipeline via `MelKLogger`, logging to both console and rolling files under `Logs/` with a ZLogger provider.

Heavier demos (Brawler, NavMesh) are not bundled in the package — clone this repo if you want to inspect or modify them.

### Dedicated server

Klotho ships as Unity package source (not a binary NuGet), so a dedicated server builds the engine-agnostic assemblies from your vendored copy of the package. `Server~/` holds per-assembly server projects that mirror the client asmdef structure; your server csproj `<ProjectReference>`s them. Two install patterns:

**A. git submodule (recommended)** — vendor this repo into your game project as a submodule (e.g. under `<yourGame>/External/Klotho`); the UPM package is its top-level `com.xpturn.klotho/` subfolder. Reference it from Unity via a `file:` entry in `Packages/manifest.json` (`"com.xpturn.klotho": "file:../External/Klotho/com.xpturn.klotho"`), and reference the `Server~` projects from your server csproj at the correct relative depth:

```xml
<!-- Server csproj at <yourGame>/Server/MyDedicatedServer.csproj; submodule at <yourGame>/External/Klotho -->
<ItemGroup>
  <ProjectReference Include="..\External\Klotho\com.xpturn.klotho\Server~\KlothoServer\KlothoServer.csproj" />
  <ProjectReference Include="..\External\Klotho\com.xpturn.klotho\Server~\xpTURN.Klotho.Runtime\xpTURN.Klotho.Runtime.csproj" />
  <ProjectReference Include="..\External\Klotho\com.xpturn.klotho\Server~\xpTURN.Klotho.Logging\xpTURN.Klotho.Logging.csproj" />
  <ProjectReference Include="..\External\Klotho\com.xpturn.klotho\Server~\xpTURN.Klotho.Gameplay\xpTURN.Klotho.Gameplay.csproj" />
  <ProjectReference Include="..\External\Klotho\com.xpturn.klotho\Server~\xpTURN.Klotho.LiteNetLib\xpTURN.Klotho.LiteNetLib.csproj" />
</ItemGroup>
<!-- Source generator for your game's [KlothoSerializable]/[KlothoComponent] types compiled into the exe -->
<ItemGroup>
  <Analyzer Include="..\External\Klotho\com.xpturn.klotho\Plugins\Analyzers\KlothoGenerator.dll" />
</ItemGroup>
```

Adjust the `..\` depth and submodule path to match where your csproj and submodule sit (the bundled `Samples/SdSample` references the in-repo package the same way — `..\..\..\com.xpturn.klotho\Server~\…`). At startup call `KlothoServerBootstrap.Initialize("YourGamePrefix")` — it force-loads the split assemblies and runs warmups so the cross-assembly `[ModuleInitializer]` registrations (commands / messages / components) complete before the first room is built.

**B. UPM `Library/PackageCache` + `<KlothoServerRoot>`** — if you don't want a submodule, point a property at your resolved PackageCache `Server~` path. The `@<hash>` suffix changes on every pull, so this needs occasional refresh:

```xml
<PropertyGroup>
  <KlothoServerRoot>$(MSBuildProjectDirectory)\..\..\Library\PackageCache\com.xpturn.klotho@1a2b3c4d\Server~</KlothoServerRoot>
</PropertyGroup>
<ItemGroup>
  <ProjectReference Include="$(KlothoServerRoot)\KlothoServer\KlothoServer.csproj" />
  <ProjectReference Include="$(KlothoServerRoot)\xpTURN.Klotho.Runtime\xpTURN.Klotho.Runtime.csproj" />
  <!-- + Logging / Gameplay / LiteNetLib as above -->
</ItemGroup>
```

Full guide (with `Program.cs`, callbacks, config files, single-room/multi-room/test CLI): [Docs/Samples/Brawler.H.DedicatedServer.md](Docs/Samples/Brawler.H.DedicatedServer.md) — a Brawler-specific reference you can copy and adapt to your game.

---

## Repository Layout

Klotho ships as a Unity Package (`com.xpturn.klotho`) promoted to the repository top level at `com.xpturn.klotho/`. Consumers install via UPM (see [Installation](#installation)); the in-repo samples consume it through a `file:` manifest reference to the same top-level package.

```
<repo root>/
├── README.md  ·  CHANGELOG.md  ·  LICENSE
├── com.xpturn.klotho/                         ← ★ framework package (UPM)
│   ├── package.json
│   ├── Runtime/
│   │   ├── Core/              KlothoEngine · KlothoSession · ISimulationCallbacks · IViewCallbacks
│   │   │                      · ISimulationConfig · ISessionConfig · Command/Event/Pool families
│   │   ├── Logging/           xpTURN.Klotho.Logging (IKLogger — in-house structured logging)
│   │   ├── Gameplay/          built-in component / system reference implementations
│   │   ├── Diagnostics/       FaultInjection · RttSpikeMetricsCollector
│   │   ├── Input/             InputBuffer · SimpleInputPredictor
│   │   ├── Network/           IKlothoNetworkService · ServerDriven · ServerNetwork · Spectator
│   │   │                      · Reconnect/LateJoin · Messages · ServerLoop
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
│   └── Server~/               dedicated-server build assets (per-assembly csproj mirroring client asmdefs + KlothoServerBootstrap + Config helpers)
│
├── Samples/                                   ← standalone Unity/​.NET sample projects (each consumes the package via `file:`)
│   ├── Brawler/               4-player fighting-game sample (+ dedicated server, NavMesh, tests)
│   ├── P2pSample/             minimal P2P sample (Unity)
│   ├── SdSample/              minimal ServerDriven sample (Unity client + .NET 8 dedicated server)
│   └── LoggingMelConsole/     .NET console sample routing IKLogger through Microsoft.Extensions.Logging
│
├── Docs/                                      ← documentation (this folder)
└── Tools/                                     ← .NET tooling (not redistributed)
    ├── KlothoGenerator/       Roslyn source generator (`IIncrementalGenerator`) — built by gen.build.sh
    ├── KlothoGenerator.Tests/ generator unit tests
    ├── DeterminismVerification/ determinism verification (.NET console)
    ├── PhysicsDeterminismProbe/ cross-platform FP determinism probe
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
    // Driver owns the main transport: it pumps it while idle and routes idle disconnects to
    // IKlothoSessionObserver.OnIdleDisconnected. Bind once, before any session is created.
    _sessionDriver.BindTransport(transport, this, _flow);
    // Attach the created session to the driver from IKlothoSessionObserver.OnSessionCreated(session, kind).

    // Single-call host bootstrap: StartHost + HostGame + Transport.Listen, with
    // framework-side teardown if any step fails. MaxPlayers is read from uSessionConfig.
    _session = _flow.StartHostAndListen(uSimulationConfig, uSessionConfig,
                                        roomName: "MyRoom", address: "0.0.0.0", port: 9050);
}
```

`KlothoSessionFlow` exposes these entry points — pick one per game mode:

- `StartHostAndListen(simCfg, sessionCfg, roomName, address, port)` — P2P host (synchronous). Folds `StartHost` + `HostGame` + `Transport.Listen` into one call with framework-side teardown on failure; returns `null` on listen-bind failure (session already torn down), rethrows on other failures.
- `StartHost(simCfg, sessionCfg)` — low-level P2P host (synchronous). Escape hatch for custom ordering / multi-transport / tests; the caller drives `HostGame` + `Transport.Listen`.
- `JoinP2PAsync(transport, host, port, sessionCfg, ct)` — P2P guest join.
- `JoinServerDrivenAsync(transport, host, port, roomId, sessionCfg, ct)` — ServerDriven client join (with roomId).
- `ReconnectAsync(transport, creds, sessionConfigSeed, ct)` — cold-start reconnect; `creds` is a `PersistedReconnectCredentials` (carries `RoomId`, host address, and the magic token). Mode is recovered from the credentials.
- `SpectateAsync(host, port, roomId, ct)` — spectator entry (transport is instantiated by `KlothoFlowSetup.SpectatorTransportFactory`).
- `StartReplayFromFile(path)` — file-to-session replay (throws `ReplayLoadException` on load failure).

If your game-side code needs to branch on the active mode, use `KlothoModeStrategy.Resolve(simCfg)` rather than inspecting `simCfg.Mode` directly. Session creation is observed through the single `IKlothoSessionObserver.OnSessionCreated(session, SessionEntryKind kind)` callback — branch on `kind` (`Host` / `Guest` / `Replay` / `Spectator`) instead of per-mode events. Stop teardown runs through the driver's `Stopping` hook.

Detailed guides: [Docs/GameDevWorkflow.md](Docs/GameDevWorkflow.md), [Docs/GameDevAPI.md](Docs/GameDevAPI.md)

---

## Sample

[Samples/Brawler](Samples/Brawler) — a 4-player fighting-game sample

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
