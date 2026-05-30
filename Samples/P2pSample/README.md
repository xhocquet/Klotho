# P2pSample

Minimum P2P sample consuming the `com.xpturn.klotho` UPM package. Two cubes on a 10×10 platform — push each other off, fall-off costs 1 point, 60s match.

> Target: `com.xpturn.klotho v0.2.5` · Unity 6.3 · URP

---

## 1. Requirements

- Unity **6.3+**
- The package fetches three Git dependencies via UPM (no manual install required) — see [`Packages/manifest.json`](Packages/manifest.json):

  ```jsonc
  {
    "dependencies": {
      "com.cysharp.unitask": "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask",
      "com.xpturn.polyfill": "https://github.com/xpTURN/Polyfill.git?path=src/Polyfill/Assets/Polyfill",
      "com.xpturn.klotho":   "https://github.com/xpTURN/Klotho.git?path=Klotho/Packages/com.xpturn.klotho#v0.2.5",
      ...
    }
  }
  ```

  To pin a different version, change the `#v0.2.5` tag suffix on `com.xpturn.klotho`.

---

## 2. Quick Start (4 steps)

1. **Open the project** — Unity Hub → "Open" → select this `P2pSample/` folder. Unity will fetch UPM dependencies automatically on first open (`Packages/packages-lock.json` records the resolved commit SHAs).
2. **Enable Run In Background** — `Edit > Project Settings > Player > Resolution > Run In Background = ✅`. **Required** for testing two instances on the same machine — without it the inactive window stops polling and the network handshake stalls.
3. **Bake DataAsset** — `Tools > Klotho > Convert > DataAsset JsonToBytes` → select `Assets/P2pSample/Data/P2pAssets.json` → produces `P2pAssets.bytes` (one-time per JSON change).
4. **Play** — Open `Assets/P2pSample/Scenes/P2pScene.unity` and press Play.

To play a 2-peer match: build a Player (`File > Build Profiles > Build And Run`) and run the build alongside the Editor. Click **Host** in one instance, **Join** in the other (with `IP=localhost`, `Port=777`), then **Ready** in both. The match auto-starts when both are ready.

---

## 3. Controls & Rules

| Key | Action |
|---|---|
| **WASD** / Arrow keys | Move (XZ plane) |
| **Host** | Become host on `localhost:777` |
| **Join** | Connect to host (edit IP/Port fields if needed) |
| **Ready** | Mark self ready — match starts when all ready |
| **Stop** | Leave the session |

**Win condition** — Stay on the platform. Fall off the edge → score `-1`, respawn at center. After 60 seconds, the player with the highest score wins (ties → DRAW).

---

## 4. Project Layout

```
Assets/P2pSample/
├── Scripts/
│   ├── Sim/      — deterministic simulation (xpTURN.Samples.P2pSample.Sim asmdef, noEngineReferences:true)
│   └── View/     — Unity-side bootstrap, HUD, prefab views (xpTURN.Samples.P2pSample.View asmdef)
├── Scenes/P2pScene.unity
├── Prefabs/      — Player.prefab (Cube + P2pPlayerView), Stage.prefab (Plane)
├── Config/       — SimulationConfig.asset (Mode=P2P) · SessionConfig.asset (MaxPlayers=2) · EntityViewFactory.asset
└── Data/         — P2pAssets.json (editable) / P2pAssets.bytes (baked, runtime-loaded)
```

Sample-side game code is ~795 LOC (15 files). All visuals use Unity built-in primitives (Cube / Plane) — no external textures / models / audio.

---

## 5. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Guest hangs at "Connecting…" | Host hasn't called `HostGame` / `Listen` | Click **Host** before guest **Join** |
| `DivideByZeroException` in `FPRigidBody.CreateDynamic` | Stale `P2pAssets.bytes` (missing `PlayerMass` field) | Re-run `Tools > Klotho > Convert > DataAsset JsonToBytes` |
| Handshake stalls / `Peer disconnected` after focus loss | Run In Background disabled | Enable as per Quick Start step 2 |
| `Config validation failed: SyncCheckInterval...` | `MaxRollbackTicks` lowered below `SyncCheckInterval` | Keep `USimulationConfig` defaults (`MaxRollbackTicks=50`, `SyncCheckInterval=30`) |

---

## 6. Out of Scope

This sample intentionally omits: ServerDriven mode, Dedicated Server, NavMesh / Pathfinding, HFSM bots, replay recording / playback, LateJoin, Reconnect, Spectator, and FaultInjection. See the **Brawler** sample (`<klotho repo>/Klotho/Assets/Brawler/`) for those features.

---

## See also

- [Klotho repo README](https://github.com/xpTURN/Klotho) — package install, dedicated-server build patterns.
- [`../../Docs/Samples/P2pSample.md`](../../Docs/Samples/P2pSample.md) — extended walkthrough (deterministic component / system design, view bootstrap flow, framework gotchas).
- [`../../Docs/IMP/IMP48/Plan-P2pSample.md`](../../Docs/IMP/IMP48/Plan-P2pSample.md) — implementation plan + design rationale.
