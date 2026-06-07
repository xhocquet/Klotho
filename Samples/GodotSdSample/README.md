# GodotSdSample

Godot 3D port of the Unity **SdSample** — a standalone, playable **Server-Driven** sample on top of the engine-agnostic `com.xpturn.klotho` core. Two cubes on a 10×10 platform; push each other off, fall-off costs 1 point. A **dedicated server** is authoritative; clients send input and render server state. One instance = one client (run the server + two clients to play).

> Target: `com.xpturn.klotho` core (shared with SdSample) · **Godot 4.6.3 mono (.NET)** · dedicated server (.NET 8 console) · top-down 3D

---

## 1. Requirements

- **Godot 4.6.3 mono** (.NET build) for the client. Earlier 4.x may work if `Godot.NET.Sdk`/`GodotSharp` versions are matched.
- **.NET SDK** for building the client C# solution **and** running the dedicated server (`Server/`, a plain `net8.0` console app).
- The client references the in-repo Godot adapter library (which transitively pulls the core) via `ProjectReference` — see [`GodotSdSample.csproj`](GodotSdSample.csproj):

  ```xml
  <ProjectReference Include="..\..\com.xpturn.klotho\Godot~\xpTURN.Klotho.Runtime.Godot.csproj" />
  <Analyzer Include="..\..\com.xpturn.klotho\Plugins\Analyzers\KlothoGenerator.dll" />
  ```

  The adapter (`xpTURN.Klotho.Runtime.Godot`) and the core (`xpTURN.Klotho.Runtime`) live under [`com.xpturn.klotho/Godot~/`](../../com.xpturn.klotho/Godot~/) and source-link `Runtime/**` (single source of truth — no forked core copy).

- Unlike SdSample, this sample is **self-contained**: the deterministic sim (`Sim/`), the simulation callbacks (`Game/`), the data asset (`Data/SdAssets.bytes`) and the **dedicated server** (`Server/`) are copied/owned in-tree, so it has **no dependency on the SdSample project**.

---

## 2. Quick Start

Server-Driven needs **one server + two clients** (the server is authoritative; it never renders).

1. **Run the dedicated server** first (a normal .NET console app — no Godot needed):
   ```
   dotnet run --project Server -- 7777            # port 7777 (default), logLevel Information
   ```
   It loads `Server/simulationconfig.json` + `Server/sessionconfig.json`, binds `0.0.0.0:7777`, and waits for `MinPlayers` (2) clients.
2. **Open the client project** — Godot 4.6.3 mono → Import → select this `GodotSdSample/` folder. Let it build the C# solution once (`Project > Tools > C#: Build`, or it builds on first run).
3. **Run two clients** — this is a standalone app, so run **two instances**:
   - **Editor + exported app**: press Play in the editor for one client; export an `.app`/binary (`Project > Export`, macOS preset is preconfigured) and run it for the other.
   - **Two exported binaries**, or **two editor runs** (same or different machines on the LAN).
4. In each client set IP=`127.0.0.1` (or the server's LAN IP), Port=`7777`, click **Join**, then **Ready**. The match auto-starts after the server countdown once both are ready.

**Headless self-test** (CI / quick check, no window): build the client C# solution once, run the server, then two clients with a CLI flag —
```
dotnet run --project Server -- 7777                 # terminal 1: dedicated server
godot --headless --path . --build-solutions         # once, to compile the client mono assemblies
godot --headless --path . -- join                   # terminal 2: client 1
godot --headless --path . -- join                   # terminal 3: client 2
```
Each client auto-readies once synchronized and prints `=== SD STANDALONE OK ===` with `viewNodes≥1` at tick 120, exiting 0 (1 on failure). `godot` must resolve to the **mono** build (e.g. `/Applications/Godot_mono.app/Contents/MacOS/Godot`). A single client will wait forever — the server's `MinPlayers=2` needs both.

---

## 3. Controls & Rules

| Key | Action |
|---|---|
| **WASD** / Arrow keys | Move (XZ plane; W/↑ = up on the top-down screen) |
| **Join** | Connect to the dedicated server (edit IP/Port fields; default `127.0.0.1:7777`) |
| **Ready** | Mark self ready — match starts when all ready (server-driven countdown) |
| **Stop** | Leave the session |

There is **no Host button** — hosting is the dedicated server's job (`Server/`).

**Win condition** — Stay on the platform. Fall off the edge → score `-1`, respawn at center. The higher score wins at the match timeout (ties → DRAW). Player cubes are colored **P1 = blue, P2 = red** (Server-Driven assigns 1-based ids; the server reserves id 0).

---

## 4. Project Layout

```
Samples/GodotSdSample/
├── Sim/                      — deterministic sim, COPIED from SdSample (single-source-of-truth tradeoff
│                               accepted for a fully standalone sample): PlayerComponent / MoveCommand /
│                               MovementSystem / ScoreSystem / RespawnSystem / GameOverEvent / PlayerStatsAsset /
│                               SdSimSetup (RegisterSystems + InitializeWorld, shared by client & server)
├── Game/
│   └── SdSimulationCallbacks.cs   — COPIED (client RegisterSystems / OnPollInput; world init arrives via server FullState)
├── Data/SdAssets.bytes       — COPIED data asset (loaded at runtime via Godot FileAccess; also copied next to the server exe)
├── Server/                   — OWNED dedicated server (plain net8.0 console, no Godot)
│   ├── Program.cs                 — bind, RoomRouter + RoomManager (1 room), ServerLoop
│   ├── SdServerCallbacks.cs       — server-side RegisterSystems / OnInitializeWorld (authoritative spawn)
│   ├── simulationconfig.json      — server-authoritative sim config (ServerDriven, EnableErrorCorrection, …)
│   └── sessionconfig.json         — server-authoritative session config (MaxPlayers/MinPlayers/Countdown)
├── SdGameNode.cs             — client bootstrap: single session + menu (Join/Ready/Stop), GodotSessionDriver
│                               + pooled views (DefaultGodotEntityViewPool), JoinServerDrivenAsync, 3D view, logging
├── SdInputCapture.cs         — WASD + arrows → FP64 H/V
├── GodotSdViewCallbacks.cs   — IViewCallbacks → drives the HUD
├── GodotSdMenu.cs / GodotSdHud.cs   — Control-based menu (Join/Ready/Stop) / HUD
├── SdEntityViewFactory.cs    — maps player entities to player.tscn
├── SdPlayerView.cs           — EntityViewNode subclass; tints mesh by PlayerId
├── Main.tscn / player.tscn / project.godot
└── GodotSdSample.csproj / GodotSdSample.sln / export_presets.cfg
```

Godot-new code is the input/view/menu/HUD/bootstrap; the sim and simulation callbacks are copies of SdSample's; the dedicated server is owned in `Server/`. Visuals use Godot built-in primitives (BoxMesh / PlaneMesh) — no external assets.

---

## 5. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Client `join failed (server running?)` | The dedicated server isn't up / wrong IP·port | Start `dotnet run --project Server -- 7777` first; match the IP/Port in the client menu |
| Client connects but match never starts | Only one client joined; server `MinPlayers=2` | Run **two** clients and click **Ready** in both |
| `CS0400 'Godot' not found` building the adapter | The adapter must be on `Godot.NET.Sdk` so GodotSharp resolves consistently | Keep it on `Godot.NET.Sdk` (don't revert to `Microsoft.NET.Sdk`) |
| Godot export "EditorPlugin build callback failed → Aborting" | Missing classic `.sln` | `GodotSdSample.sln` is committed (dotnet 10 makes `.slnx`; Godot needs `.sln`) |
| Exported `.app` quits at start: `SdAssets.bytes not found` | `.bytes` not packed / read via `System.IO` | Loaded via `Godot.FileAccess` + `include_filter="*.bytes"` in `export_presets.cfg` (both configured) |
| Exported `.app` won't launch without `codesign --force --deep --sign -` | export signing gets in the way for local runs | `export_presets.cfg` disables export-time signing (`codesign/codesign=0`) and sets `disable_library_validation=true` |
| Server can't find its asset | `SdAssets.bytes` not next to the server exe | The server reads `Data/SdAssets.bytes` from its output dir; the `.csproj` copies it on build |
| W/S feel inverted | Top-down screen-up is world −Z, but `MovementSystem` maps V→+Z | `SdInputCapture` flips V (W/↑ → −V) |

Logs are written to the Godot console **and** to a rolling file under `user://logs` (client, e.g. `~/Library/Application Support/Godot/app_userdata/GodotSdSample/logs/`) and to `Server/Logs/` (server).

---

## 6. Out of Scope

Same gameplay scope as SdSample — a minimal Server-Driven slice. Omits P2P/host mode, NavMesh, bot HFSM, replay, LateJoin, Spectator, FaultInjection, multi-room (the server runs a single room). Also out of scope: mobile/console export, in-game lobby/matchmaking UI. For P2P on Godot see [`GodotP2pSample`](../GodotP2pSample/); for the full feature set see the Unity **Brawler** sample.

---

## See also

- [`../../Docs/Samples/GodotSdSample.md`](../../Docs/Samples/GodotSdSample.md) — extended walkthrough (architecture, server, Godot adapter porting, SD pitfalls).
- [`../GodotP2pSample/README.md`](../GodotP2pSample/README.md) — the P2P Godot sample (shared adapter/view pattern, no dedicated server).
- [`../SdSample/README.md`](../SdSample/README.md) — the original Unity sample this is ported from.
