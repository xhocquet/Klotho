# SdSample — Minimum ServerDriven Sample

> Target framework: **xpTURN.Klotho v0.2.6**
> Purpose: the smallest end-to-end **ServerDriven (SD)** sample — same sumo game as P2pSample, but with an authoritative dedicated server + thin predicting clients. Shows how to stand up a Klotho dedicated server and how the deterministic core is shared between the .NET server and the Unity client.
> Audience: game devs consuming `com.xpturn.klotho` who want a minimal dedicated-server reference before the full Brawler server.
> Source: [`<repo>/Samples/SdSample/`](../../Samples/SdSample/)

> Last updated: 2026-05-30 (Step 4 검증 통과 — server + 2 clients, server-authoritative, deterministic (per-component hash match), WASD move, 60s GameOver clean shutdown).

How to run: see [`<repo>/Samples/SdSample/README.md`](../../Samples/SdSample/README.md) (server + 2 clients). This document walks through how the sample is built — and the SD-specific traps you'll hit reusing a P2P game on a server topology.

---

## 1. Game Overview

Identical rules to [P2pSample](./P2pSample.md) (top-down sumo: 2 cubes, WASD, fall-off = −1, 60 s timer). The only operational difference is the topology:

| Item | P2pSample | SdSample |
|---|---|---|
| Processes per match | 2 (host peer + guest peer) | **3** (server console + 2 clients) — both clients are guests |
| Simulation authority | host peer + lockstep | **server process alone** (clients predict + correct) |
| "Host" button | yes | **none** (SD clients can't host locally) |

---

## 2. Klotho Feature Map (vs P2pSample)

| Klotho area | SdSample usage |
|---|---|
| **ECS / [KlothoComponent] / [KlothoSerializable] / [KlothoDataAsset]** | identical to P2pSample (`PlayerComponent` / `MoveCommand` / `GameOverEvent` / `PlayerStatsAsset`) — `Sim` code reused, namespace `xpTURN.Samples.SdSample` |
| **Mode = ServerDriven** | `USimulationConfig.Mode = ServerDriven` (client) + server `simulationconfig.json` (`Mode: "ServerDriven"`, authoritative) |
| **`JoinServerDrivenAsync`** | single client entry point (no `StartHost`) — sends a `RoomHandshakeMessage` pre-join, `roomId = 0` |
| **Dedicated server** | `ServerNetworkService` + `RoomManager(MaxRooms=1)` + `RoomRouter` + `ServerLoop` ([`Server/Program.cs`](../../Samples/SdSample/Server/Program.cs)) |
| **Source-shared deterministic core** | server `.csproj` compiles the same `Sim/*.cs` the client does into the exe; the framework comes via `<ProjectReference>` to the `Server~` mirror assemblies (§3) |
| **`IMatchEndEvent`** | `GameOverEvent : SimulationEvent, IMatchEndEvent` — drives `Engine.OnMatchEnded` → server room drain (§5 SG13) |
| **`EntityViewFactory` / `EntityView`** | client-only (server has no rendering); `SdPlayerView` colors by 1-based PlayerId |

Excluded by design: P2P · multi-room · NavMesh · Bot · Replay · LateJoin · Reconnect · Spectator · FaultInjection (Brawler covers those).

---

## 3. Architecture (2 client assemblies + 1 server)

```
xpTURN.Samples.SdSample.Sim   (noEngineReferences:true — compiled by BOTH client & server)
  ├─ PlayerComponent / MoveCommand / GameOverEvent(:IMatchEndEvent) / PlayerStatsAsset   (P2p 재사용 + IMatchEndEvent)
  ├─ MovementSystem / RespawnSystem / ScoreSystem
  └─ SdSimSetup                — static helper: RegisterSystems(엔진중력+ground 등록) + InitializeWorld(스폰)

xpTURN.Samples.SdSample.View  (Unity client only)
  ├─ SdGameController          MonoBehaviour — Flow / Driver / Join·Ready·Stop (no Host)
  ├─ SdSimulationCallbacks     ISimulationCallbacks — RegisterSystems→SdSimSetup, OnPollInput (client input)
  ├─ SdViewCallbacks           IViewCallbacks — engine.OnSyncedEvent → GameOver result
  └─ SdInputCapture / SdHud / SdMenu / SdEntityViewFactory / SdPlayerView

Server/  (dedicated .NET 8 console — NOT in Assets/)
  ├─ Program.cs                RoomManager(MaxRooms=1) + RoomRouter + ServerLoop
  └─ SdServerCallbacks         ISimulationCallbacks — RegisterSystems→SdSimSetup, OnPollInput = no-op
```

**Shared deterministic setup** — `SdSimSetup.RegisterSystems` / `InitializeWorld` live in the engine-free `Sim` assembly and are invoked by **both** the client (`SdSimulationCallbacks`) and the server (`SdServerCallbacks`), so the world is built identically on every peer. The server compiles the *same `Sim/*.cs` source files* (not a DLL) into the exe; since `CommandFactory` now lives in the referenced `xpTURN.Klotho.Runtime` assembly, KlothoGenerator emits cross-assembly registration (`[ModuleInitializer]`) for those `[KlothoSerializable]` types, and `KlothoServerBootstrap.Initialize(...)` (`Program.cs`) force-loads the split assemblies + runs warmups at startup so every registration completes before the first room.

**Embedded framework package** — `com.xpturn.klotho` is **embedded** at `Samples/SdSample/Packages/com.xpturn.klotho/` (Unity `source: embedded`), not git-fetched. The server `.csproj` `<ProjectReference>`s the per-assembly server projects under `..\Packages\com.xpturn.klotho\Server~\` (`KlothoServer` + `xpTURN.Klotho.Runtime`/`Logging`/`Gameplay`/`LiteNetLib`) via this fixed in-project path; a git-fetched package would resolve to a hashed `Library/PackageCache/com.xpturn.klotho@<hash>/` path that an MSBuild project reference can't target reliably. (UniTask / Polyfill are still git-fetched via the manifest.)

---

## 4. Server bootstrap (single-room)

`Server/Program.cs` follows Brawler's `RunSingleRoom` pattern, minimized (no NavMesh / StaticCollider-export / bots):

```csharp
var transport   = new LiteNetLibTransport(logger, connectionKey: "xpTURN.SdSample");
transport.Listen("0.0.0.0", port, maxRooms * maxPlayers);
var router      = new RoomRouter(transport, logger);
var roomManager = new RoomManager(transport, router, loggerFactory, new RoomManagerConfig {
    MaxRooms = 1, MaxPlayersPerRoom = maxPlayers, MaxSpectatorsPerRoom = 0,
    SimulationFactory       = () => new EcsSimulation(simConfig.MaxEntities, maxRollbackTicks:1, tickIntervalMs, logger, registry),
    SimulationConfigFactory = () => simConfig,
    SessionConfigFactory    = () => sessionConfig,
    CallbacksFactory        = roomLogger => new SdServerCallbacks(roomLogger, maxPlayers),
});
new ServerLoop(transport, roomManager, tickIntervalMs, logger).Run();   // blocks until SIGINT, graceful drain
```

`RoomManager` wires `EcsSimulation` / `ServerNetworkService` / `KlothoEngine` / `CommandFactory` per room internally — `Program.cs` only supplies the four factories. **Why `RoomManager(MaxRooms=1)` and not a single-engine loop**: the stock `JoinServerDrivenAsync` always sends a `RoomHandshakeMessage`, which only `RoomRouter` consumes — a raw `ServerNetworkService` would reject it. `MaxRooms=1` keeps the standard routing path while disabling multi-room. (Full rationale: [Plan-SdSample.md §4-3](../IMP/IMP48/Plan-SdSample.md).)

---

## 5. SD-specific gotchas (reusing a P2P game on a server)

These four bit us porting P2pSample → SD. All four trace back to **the ServerDriven client skipping `OnInitializeWorld`** (it boots from the server FullState), or to the **1-based server player ids**. (Detail + fixes: [Plan-SdSample.md §14](../IMP/IMP48/Plan-SdSample.md).)

| # | Trap | Fix |
|---|---|---|
| **SG11** | SD client doesn't call `OnInitializeWorld` → any state cached there (e.g. an `_engine` ref) stays null → `OnPollInput` early-returns → **input never sent**. | Don't depend on `OnInitializeWorld` state in client callbacks. `OnPollInput` uses the passed `playerId` (= LocalPlayerId) directly — no guard. |
| **SG12** | SD network playerId is **1-based** (`ServerNetworkService` reserves id 0) → a 0-based spawn loop mismatches `MoveCommand.PlayerId` → `MovementSystem` drops input. | Spawn `PlayerId = i + 1`; view/HUD id mapping 1-based; client `roomId = 0`. |
| **SG13** | Match end on the server needs the Synced end-event to implement **`IMatchEndEvent`** (else `Engine.OnMatchEnded` never fires → server never drains). A plain Synced event only shows the HUD result. | `GameOverEvent : SimulationEvent, IMatchEndEvent` (+ `Reason`). |
| **SG14** | Static ground registered in `OnInitializeWorld` is **missing on the SD client** (skipped) → client box falls through the ground → landing-tick desync → full-state-resync storm → Synced GameOver never dispatched. (The physics itself is cross-runtime deterministic — verified.) | Register the static ground in **`RegisterSystems`** (runs on server *and* client) so both build the identical ground BVH. Engine gravity + resting-contact then matches. |

> SG11 & SG14 share one root: **`OnInitializeWorld` is skipped on the SD client** — so deterministic world setup that must exist on every peer (systems, static colliders) belongs in `RegisterSystems`, not `OnInitializeWorld`.

---

## 6. Config

| | Client (`USimulationConfig` asset) | Server (`simulationconfig.json`, authoritative) |
|---|---|---|
| Mode | ServerDriven | ServerDriven |
| TickIntervalMs | 33 | 33 |
| UsePrediction | true (client predicts) | false (server is authoritative) |
| MaxRollbackTicks / SyncCheckInterval | 50 / 30 | 50 / 30 |

The server propagates its `SimulationConfig` (tick interval etc.) to clients via `SimulationConfigMessage` on join — the server JSON is the source of truth. `SessionConfig`: `MaxPlayers=2`, `MinPlayers=2`, `AllowLateJoin=false`.

---

## See also

- [`Samples/SdSample/README.md`](../../Samples/SdSample/README.md) — run steps (server + 2 clients), troubleshooting.
- [Plan-SdSample.md](../IMP/IMP48/Plan-SdSample.md) — full implementation plan, §14 gotchas (SG11~SG14).
- [P2pSample.md](./P2pSample.md) — the P2P sibling (same game, peer topology).
- [Brawler.H.DedicatedServer.md](./Brawler.H.DedicatedServer.md) — full-featured dedicated server (multi-room / LateJoin / Reconnect).
