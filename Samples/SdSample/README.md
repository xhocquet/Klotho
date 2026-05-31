# SdSample

Minimum **ServerDriven** sample consuming the `com.xpturn.klotho` UPM package. Same 2-cube sumo game as P2pSample, but the simulation authority lives in a separate **dedicated .NET server** — clients only `Join` (no local host) and predict against the server's verified state.

> Target: `com.xpturn.klotho v0.2.7` · Unity 6.3 · URP · .NET 8 (server)

> **P2pSample vs SdSample**: same game (`Sim` code is byte-identical bar the namespace), different netcode topology. P2P = peers self-host (1 Unity project). SD = **server console + Unity clients** (this sample). 한 매치 = 서버 1 + 클라 2.

---

## 1. Requirements

- Unity **6.3+** (client)
- **.NET 8 SDK** (`dotnet`) — the dedicated server is a console app under [`Server/`](Server/)
- **`com.xpturn.klotho` is an *embedded* package** at [`Packages/com.xpturn.klotho/`](Packages/com.xpturn.klotho/) (a copied-in package, v0.2.7 — Unity resolves it `source: embedded`), **not** git-fetched. Reason: the dedicated server's `.csproj` `<Import>`s `Server~/KlothoServer.Core.props` and needs a **stable relative path** (`..\Packages\com.xpturn.klotho\Server~\…`); a git-fetched package lands in `Library/PackageCache/com.xpturn.klotho@<hash>/` with a per-resolve hashed path MSBuild can't reference reliably. Embedding points both the Unity client and the server at one in-project copy (§4).
- The remaining deps are git-fetched via UPM on first open — see [`Packages/manifest.json`](Packages/manifest.json):

  ```jsonc
  {
    "dependencies": {
      "com.cysharp.unitask": "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask",
      "com.xpturn.polyfill": "https://github.com/xpTURN/Polyfill.git?path=src/Polyfill/Assets/Polyfill",
      // com.xpturn.klotho — embedded under Packages/com.xpturn.klotho/ (not fetched here)
      ...
    }
  }
  ```

  To update the embedded framework, replace the `Packages/com.xpturn.klotho/` folder with the target version's contents (e.g. copy from `<klotho repo>/Klotho/Packages/com.xpturn.klotho/`).

---

## 2. Quick Start (server + 2 clients)

> ⚠ **Order matters**: bake `.bytes` **before** building the server — the server copies `SdAssets.bytes` at build time.

1. **Open the client project** — Unity Hub → "Open" → select this `SdSample/` folder. Unity fetches UPM dependencies on first open.
2. **Enable Run In Background** — `Edit > Project Settings > Player > Resolution > Run In Background = ✅`. **Required** when testing two clients on one machine (the unfocused window must keep capturing input + polling the network).
3. **Bake DataAsset** — `Tools > Klotho > Convert > DataAsset JsonToBytes` → select `Assets/SdSample/Data/SdAssets.json` → produces `SdAssets.bytes` (one-time per JSON change).
4. **Start the dedicated server**:
   ```sh
   cd Server
   dotnet run -- 7777            # [port] [logLevel]; default 7777 / Information
   ```
   Wait for `[SdServer] listening on port 7777 …`. (The server copies `SdAssets.bytes` + the config JSON next to its executable on build.)
5. **Run two clients** — open `Assets/SdSample/Scenes/SdScene.unity`, press Play; build a Player (`File > Build Profiles > Build And Run`) and run it alongside the Editor for the 2nd client.
6. In **each** client: **Join** (`IP=localhost`, `Port=7777`) → **Ready**. The server starts the match after a short countdown.
7. **WASD** to move; push the other cube off the platform (fall → `-1`, respawn at center). After 60 s the result panel shows **WIN / LOSE / DRAW**.

---

## 3. Controls & Rules

| Key / Button | Action |
|---|---|
| **WASD** / Arrow keys | Move (XZ plane) |
| **Join** | Connect to the server (edit IP/Port fields if needed). roomId is `0` (single room). |
| **Ready** | Mark self ready — the **server** starts the match when all clients are ready |
| **Stop** | Leave the session |

There is **no Host button** — SD clients never host locally (`ServerDrivenModeStrategy.SupportsLocalHost == false`); the dedicated server owns the simulation.

**Win condition** — Stay on the platform. Fall off the edge → score `-1`, respawn at center. After 60 seconds the highest score wins (ties → DRAW).

---

## 4. Project Layout

```
SdSample/
├── Assets/SdSample/
│   ├── Scripts/
│   │   ├── Sim/     — deterministic simulation (xpTURN.Samples.SdSample.Sim asmdef, noEngineReferences:true)
│   │   │            — compiled by BOTH the Unity client and the .NET server (source-shared, §6)
│   │   └── View/    — Unity client only: bootstrap, HUD, menu, prefab views (xpTURN.Samples.SdSample.View asmdef)
│   ├── Scenes/SdScene.unity
│   ├── Prefabs/     — Player.prefab (Cube + SdPlayerView), Stage.prefab (Plane)
│   ├── Config/      — SimulationConfig.asset (Mode=ServerDriven) · SessionConfig.asset (MaxPlayers=2) · EntityViewFactory.asset
│   └── Data/        — SdAssets.json (editable) / SdAssets.bytes (baked, loaded by both client & server)
├── Packages/
│   ├── com.xpturn.klotho/     — ★ EMBEDDED framework package (Runtime / Editor / Server~ / …)
│   └── manifest.json          — UPM manifest (git-fetches UniTask + Polyfill only)
└── Server/                    — dedicated .NET 8 console server
    ├── SdSampleServer.csproj  — imports ..\Packages\com.xpturn.klotho\Server~\KlothoServer.Core.props + links Sim/*.cs (source-shared)
    ├── Program.cs             — RoomManager(MaxRooms=1) + RoomRouter + ServerLoop
    ├── SdServerCallbacks.cs   — ISimulationCallbacks (OnPollInput no-op; server takes no local input)
    ├── simulationconfig.json  — Mode=ServerDriven (authoritative tick/lead values)
    └── sessionconfig.json     — MaxPlayers=2
```

Game-side code is ~916 LOC (Sim 292 / Client View 540 / Server 107). All visuals use Unity built-in primitives (Cube / Plane) — no external textures / models / audio.

### Why the server links Sim *sources* (not a DLL)

`SdSampleServer.csproj` `<Compile Include>`-links the **same `Sim/*.cs`** the Unity client compiles, via the package's `Server~/KlothoServer.Core.props`. The KlothoGenerator emits `RegisterGeneratedTypes` as a *partial method* on `CommandFactory`/`MessageSerializer`, so every `[KlothoSerializable]` type (here `MoveCommand` / `GameOverEvent`) must live in one compilation unit — a ProjectReference (binary) would split the partial across assemblies and drop registration.

### Server source path (why the framework is embedded)

`SdSampleServer.csproj` imports the props from the **embedded** package via a stable in-project path:

```xml
<Import Project="..\Packages\com.xpturn.klotho\Server~\KlothoServer.Core.props" />
```

The dedicated server is an MSBuild project, so its `<Import>` needs a path that doesn't move. A git-fetched UPM package resolves to `Library/PackageCache/com.xpturn.klotho@<hash>/`, where `<hash>` changes per resolve — not referenceable from a checked-in `.csproj`. Embedding `com.xpturn.klotho` under the project's `Packages/` gives the server (and the Unity client) one fixed location, and the whole sample stays self-contained when copied elsewhere — no PackageCache juggling. (`Server~` is hidden from Unity import by the `~` suffix but is a normal folder on disk that MSBuild reads.)

---

## 5. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| **Join** times out | Dedicated server not running | Start it first: `cd Server && dotnet run -- 7777` |
| Server errors loading `SdAssets.bytes` | Baked `.bytes` missing or server built before bake | Bake (Quick Start 3) **then** rebuild/run the server |
| Cubes don't move when pressing WASD | Client window unfocused / Run In Background off | Focus the client Game view; enable Run In Background (Quick Start 2) |
| Match never ends / result panel never shows | Mixed old/new build of the shared `Sim` code (server vs client) | Rebuild the server **and** let Unity recompile — both must run the identical `Sim/*.cs` |
| Two clients diverge / `Determinism failure` in client log | Same as above (shared-Sim version mismatch) | Keep server `dotnet build` and Unity recompile in sync |

---

## 6. Out of Scope

This sample intentionally omits: P2P mode, multi-room (`RoomManager` runs `MaxRooms=1`), NavMesh / Pathfinding, HFSM bots, replay, LateJoin, Reconnect, Spectator, FaultInjection, and server containerization/deploy. See the **Brawler** sample + [`Docs/Samples/Brawler.H.DedicatedServer.md`](../../Docs/Samples/Brawler.H.DedicatedServer.md) for the full-featured dedicated-server (multi-room / LateJoin / Reconnect) reference.

---

## See also

- [Klotho repo README](https://github.com/xpTURN/Klotho) — package install, dedicated-server build patterns.
- [`../../Docs/Samples/SdSample.md`](../../Docs/Samples/SdSample.md) — architecture walkthrough (server bootstrap, shared deterministic setup, SD-specific gotchas).
- [`../../Docs/IMP/IMP48/Plan-SdSample.md`](../../Docs/IMP/IMP48/Plan-SdSample.md) — implementation plan + design rationale.
- [`../P2pSample/`](../P2pSample/) — the P2P sibling (same game, peer topology).
