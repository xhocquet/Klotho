# GodotP2pSample

Godot 3D port of the Unity **P2pSample** — a standalone, playable P2P sample on top of the engine-agnostic `com.xpturn.klotho` core. Two cubes on a 10×10 platform; push each other off, fall-off costs 1 point, 60s match. One instance = one peer (run two to play).

> Target: `com.xpturn.klotho` core (shared with P2pSample) · **Godot 4.6.3 mono (.NET)** · top-down 3D

---

## 1. Requirements

- **Godot 4.6.3 mono** (.NET build). Earlier 4.x may work if `Godot.NET.Sdk`/`GodotSharp` versions are matched.
- **.NET SDK** for building the C# solution (Godot mono bundles a runtime; building uses your installed `dotnet`).
- This sample references the in-repo Godot adapter library (which transitively pulls the core) via `ProjectReference` — see [`GodotP2pSample.csproj`](GodotP2pSample.csproj):

  ```xml
  <ProjectReference Include="..\..\com.xpturn.klotho\Godot~\xpTURN.Klotho.Runtime.Godot.csproj" />
  <Analyzer Include="..\..\com.xpturn.klotho\Plugins\Analyzers\KlothoGenerator.dll" />
  ```

  The adapter (`xpTURN.Klotho.Runtime.Godot`) and the core (`xpTURN.Klotho.Runtime`) live under [`com.xpturn.klotho/Godot~/`](../../com.xpturn.klotho/Godot~/) and source-link `Runtime/**` (single source of truth — no forked core copy).

- Unlike P2pSample, this sample is **self-contained**: the deterministic sim (`Sim/`), the simulation callbacks (`Game/`) and the data asset (`Data/P2pAssets.bytes`) are copied in, so it has **no dependency on the P2pSample project**.

---

## 2. Quick Start

1. **Open the project** — Godot 4.6.3 mono → Import → select this `GodotP2pSample/` folder. Let it build the C# solution once (`Project > Tools > C#: Build`, or it builds on first run).
2. **Play two peers** — this is a standalone app, so run **two instances**, one host and one guest:
   - **Editor + exported app**: press Play in the editor for one peer; export an `.app`/binary (`Project > Export`, macOS preset is preconfigured) and run it for the other.
   - **Two exported binaries**, or **two editor runs** on different machines on the LAN.
3. In one instance click **Host** (binds `0.0.0.0:7777`), in the other set IP=`127.0.0.1` (or the host's LAN IP), Port=`7777`, click **Join**.
4. Click **Ready** in both — the match auto-starts after the countdown.

**Headless self-test** (CI / quick check, no window): build the C# solution once, then run two processes with a CLI flag —
```
godot --headless --path . --build-solutions    # once, to compile the mono assemblies (or import in the editor)
godot --headless --path . -- host               # one terminal
godot --headless --path . -- join               # another terminal
```
Each auto-readies once synchronized and prints `=== P2P STANDALONE OK ===` with `viewNodes=2` when entities spawn, exiting 0 (1 on failure). `godot` must resolve to the **mono** build (e.g. `/Applications/Godot_mono.app/Contents/MacOS/Godot`). The join peer may log a `[FullStateResync] … not in Requested state, ignoring` **warning** with a stack trace — that is benign (a late full-state reply, harmlessly dropped; the trace is just Godot decorating `push_warning`), not a failure.

---

## 3. Controls & Rules

| Key | Action |
|---|---|
| **WASD** / Arrow keys | Move (XZ plane; W/↑ = up on the top-down screen) |
| **Host** | Become host on `0.0.0.0:7777` |
| **Join** | Connect to host (edit IP/Port fields; default `127.0.0.1:7777`) |
| **Ready** | Mark self ready — match starts when all ready |
| **Stop** | Leave the session |

**Win condition** — Stay on the platform. Fall off the edge → score `-1`, respawn at center. After 60 seconds the higher score wins (ties → DRAW). Player cubes are colored **P1 = blue, P2 = red**.

---

## 4. Project Layout

```
Samples/GodotP2pSample/
├── Sim/                      — deterministic sim, COPIED from P2pSample (single-source-of-truth tradeoff
│                               accepted for a fully standalone sample): PlayerComponent / MoveCommand /
│                               MovementSystem / ScoreSystem / RespawnSystem / GameOverEvent / PlayerStatsAsset
├── Game/
│   └── P2pSimulationCallbacks.cs  — COPIED (RegisterSystems / OnInitializeWorld / OnPollInput)
├── Data/P2pAssets.bytes      — COPIED data asset (loaded at runtime via Godot FileAccess)
├── P2pGameNode.cs            — bootstrap: single session + menu (Host/Join/Ready/Stop), GodotSessionDriver
│                               + pooled views (DefaultGodotEntityViewPool), JoinP2PAsync, 3D view setup, logging
├── P2pInputCapture.cs        — WASD + arrows → FP64 H/V
├── GodotP2pViewCallbacks.cs  — IViewCallbacks → drives the HUD
├── GodotP2pMenu.cs / GodotP2pHud.cs   — Control-based menu / HUD
├── P2pEntityViewFactory.cs   — maps player entities to player.tscn
├── P2pPlayerView.cs          — EntityViewNode subclass; tints mesh by PlayerId
├── Main.tscn / player.tscn / project.godot
└── GodotP2pSample.csproj / GodotP2pSample.sln / export_presets.cfg
```

Godot-new code is the input/view/menu/HUD/bootstrap; the sim and simulation callbacks are copies of P2pSample's. Visuals use Godot built-in primitives (BoxMesh / PlaneMesh) — no external assets.

---

## 5. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `CS0400 'Godot' not found` building the adapter | The adapter must be on `Godot.NET.Sdk` so GodotSharp resolves consistently | Keep it on `Godot.NET.Sdk` (don't revert to `Microsoft.NET.Sdk`) |
| Godot export "EditorPlugin build callback failed → Aborting" | Missing classic `.sln` | `GodotP2pSample.sln` is committed (dotnet 10 makes `.slnx`; Godot needs `.sln`) |
| Exported `.app` quits at start: `P2pAssets.bytes not found` | `.bytes` not packed / read via `System.IO` | Loaded via `Godot.FileAccess` + `include_filter="*.bytes"` in `export_presets.cfg` (both configured) |
| Exported `.app` won't launch without `codesign --force --deep --sign -` | export signing gets in the way for local runs | `export_presets.cfg` disables export-time signing (`codesign/codesign=0`) and sets `disable_library_validation=true` |
| Nothing visible after the match starts | No camera/light in the scene | `P2pGameNode.SetupView3D()` adds a top-down camera, ambient/background, directional light |
| W/S feel inverted | Top-down screen-up is world −Z, but `MovementSystem` maps V→+Z | `P2pInputCapture` flips V (W/↑ → −V) |

Logs are written to the Godot console **and** to a rolling file under `user://logs` (e.g. `~/Library/Application Support/Godot/app_userdata/GodotP2pSample/logs/`).

---

## 6. Out of Scope

Same gameplay scope as P2pSample — omits ServerDriven, Dedicated Server, NavMesh, bot HFSM, replay, LateJoin, Reconnect, Spectator, FaultInjection. Also out of scope: mobile/console export, in-game lobby/matchmaking UI. For Server-Driven on Godot see [`GodotSdSample`](../GodotSdSample/); for the full feature set see the Unity **Brawler** sample.

---

## See also

- [`../../Docs/Samples/GodotP2pSample.md`](../../Docs/Samples/GodotP2pSample.md) — extended walkthrough (architecture, Godot adapter porting, pitfalls).
- [`../P2pSample/README.md`](../P2pSample/README.md) — the original Unity sample this is ported from.
