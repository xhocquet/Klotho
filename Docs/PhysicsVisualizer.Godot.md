# Godot FPPhysics Visualizer — User Guide

Runtime tools that draw the deterministic FPPhysics world (bodies, static colliders, contacts) in the Godot 3D viewport during play, plus an on-screen inspector panel and an editor-time static-collider viewer.

> Target: `com.xpturn.klotho` Godot adapter · **Godot 4.x mono (.NET)**
> Three components: `GodotFPPhysicsWorldVisualizer` (runtime) · `GodotFPPhysicsDebugPanel` (runtime HUD) · `Klotho: Static Collider Viewer` (editor menu/dock tool)
> Related: [PhysicsWorld.md](PhysicsWorld.md) (physics system overview) · static-collider exporter (`Project > Tools > Klotho: Export Static Colliders`)

---

## 0. How it differs from the NavMesh Visualizer

Unlike the [NavMesh Visualizer](NavMeshVisualizer.Godot.md) (an **editor** plugin that loads exported `.bytes`), the physics world visualizer is **runtime**: it reads the live simulation while the game is playing. So there is **no menu / dock** — you add nodes to your scene and they draw during play. Only the static-collider viewer (§5) is an editor-time `[Tool]`.

---

## 1. Prerequisites

1. **Godot mono (.NET) build** + an installed `dotnet` SDK; the Klotho addon present in `addons/klotho/` and the C# solution built once.
2. **The game's simulation callbacks must expose the physics world.** The visualizer obtains its data through `IFPPhysicsProviderSource` on the session's `ISimulationCallbacks`. If your callbacks don't implement it, the visualizer receives nothing. The pattern (≈3 lines, see [`P2pSimulationCallbacks.cs`](../Samples/GodotP2pSample/Game/P2pSimulationCallbacks.cs)):

   ```csharp
   public class MySimulationCallbacks : ISimulationCallbacks, IFPPhysicsProviderSource
   {
       private EcsSimulation _simulation;
       public void RegisterSystems(EcsSimulation simulation) { _simulation = simulation; /* ... */ }
       public IFPPhysicsWorldProvider PhysicsProvider => _simulation?.GetSystem<PhysicsSystem>();
   }
   ```

3. **A running session.** Geometry only appears while a Klotho session is active (Host/Join). With no session the overlay stays empty (and hidden).

---

## 2. Setup — placing the nodes

Add two nodes to your gameplay scene (see [`Samples/GodotP2pSample/Main.tscn`](../Samples/GodotP2pSample/Main.tscn) for a wired example):

| Node | Type (script) | Role |
|---|---|---|
| `FPPhysicsVisualizer` | `Node3D` → `GodotFPPhysicsWorldVisualizer` | draws bodies / static colliders / contacts |
| `FPPhysicsDebugPanel` | `CanvasLayer` → `GodotFPPhysicsDebugPanel` | on-screen body/contact inspector |

- On the panel, set its **`Visualizer`** export to the `FPPhysicsVisualizer` node.
- Set both nodes' **`Enabled`** to `true` to turn them on (default is **off** — the tools are release-safe and cost nothing while disabled).

> **Place the nodes in the scene before the session starts** (i.e. author them in the `.tscn`). The visualizer hooks `KlothoSession.OnSessionCreated`; if it enters the tree *after* a session is already created it misses that event. For dynamic spawning after session start, call `visualizer.Bind(session)` from game code instead.

---

## 3. World Visualizer (`GodotFPPhysicsWorldVisualizer`)

All options are `[Export]` properties (editable in the inspector / `.tscn`). The overlay redraws on each simulation tick.

| Group | Property | Effect |
|---|---|---|
| General | `Enabled` | master on/off (default off) |
| | `AlwaysOnTop` | draw over geometry (depth-test off) vs. depth-tested |
| Body | `ShowBodies` · `ShowBodyShape` · `ShowBodyAABB` | body wireframe / AABB |
| | `ShowBodyVelocity` · `VelocityArrowScale` | velocity arrow (dynamic bodies) |
| Static | `ShowStaticColliders` · `ShowStaticShape` · `ShowStaticAABB` | static collider wireframe / AABB |
| Collision | `ShowContacts` · `ShowContactNormals` | contact points / normal arrows (CCD contacts tinted) |
| | `ShowCollisionHighlight` | re-tint colliding bodies (collision = red, trigger = magenta) |
| | `ContactNormalScale` · `ContactPointRadius` | contact glyph sizing |
| Colors | `DynamicShapeColor` · `StaticBodyShapeColor` · `KinematicShapeColor` · `SceneStaticColor` · `AabbColor` · `VelocityColor` · `ContactPointColor` · `ContactNormalColor` · `CollisionHighlightColor` · `TriggerHighlightColor` · `SelectedShapeColor` · `SelectedAABBColor` | per-element colors |

- Body color encodes the type: **dynamic** (amber) · **static** (grey) · **kinematic** (blue); triggers are drawn semi-transparent.
- The item selected in the debug panel (§4) is re-drawn in `SelectedShapeColor` + its AABB.

---

## 4. Debug Panel (`GodotFPPhysicsDebugPanel`)

An on-screen HUD listing live bodies and contacts. Updates every frame while `Enabled` and a session is live. **`Corner`** (`[Export]`) docks it to `TopLeft` / `TopRight` (default) / `BottomLeft` / `BottomRight` — the game UI usually sits top-left.

- **Tabs**: `Bodies` / `StaticColliders` — choose which list to inspect.
- **Navigation**: `<` `n / total` `>` — step through items; the selected item is highlighted in the 3D view.
- **Detail**:
  - *Body* — EntityIndex · Type · Position · Rotation · Shape · Mass/invMass · Velocity · AngVelocity · Damping · Material (restitution/friction) · Flags (Static/Kinematic/Trigger).
  - *StaticCollider* — id · Shape · Material · Trigger · AABB center/size.
- **Contacts** (body view): per-contact peer (`entity=…` or `static[i]`), penetration depth, normal, and a `[CCD]` tag for speculative contacts.
- **`Copy to Clipboard`**: copies the current detail text.

---

## 5. Static Collider Viewer (`Klotho: Static Collider Viewer`)

An **editor** tool (menu + dock) that loads exported static colliders and draws their wireframes in the 3D viewport — for verifying the static-collider exporter output. Unlike the runtime visualizer above, this is an `EditorPlugin` tool (no scene node), matching the NavMesh Visualizer and the exporter's menu UX.

1. Toggle **`Project > Tools > Klotho: Static Collider Viewer`** → an **`FPStaticColliders`** dock appears on the right and an overlay is attached under the edited scene root. (A 3D scene must be open.)
2. In the dock, enter the `.bytes` `res://` path produced by `Klotho: Export Static Colliders` (e.g. `res://<scene>.StaticColliders.bytes`) and click **`Load`** (**`Unload`** clears).
3. **`Shape`** / **`AABB`** checkboxes toggle the wireframe / bounds; the collider count is shown.
4. Toggle the menu again to remove the dock and overlay.

> Wire-only — it needs no 3D input/label forwarding (simpler than the NavMesh tool). The overlay `MeshInstance3D` is a temporary node under the edited scene root (`owner=null`, freed on toggle-off), so it is not serialized into the scene.

---

## 6. Limitations / Notes

- **No line thickness.** Godot `ImmediateMesh` lines have no width; selection/collision emphasis uses **color** rather than thicker lines.
- **Single world overlay.** Geometry is a world-space `MeshInstance3D` that the active `Camera3D` renders; there is one `Enabled` toggle (no per-camera / scene-vs-game targeting).
- **Live state is shown via the runtime HUD, not the editor inspector.** The editor inspector cannot follow a separately-running game process, so the body/contact list is the on-screen panel (§4). *(The "Remote" scene tree can inspect live node properties if you expose them, but the HUD is the default.)*
- **Provider must be exposed** (§1.2) — without `IFPPhysicsProviderSource` the world visualizer draws nothing.
- **Disabling hides immediately.** `Enabled=false` (or session end) hides the overlay; it does not leave the last frame's wireframe on screen.
- **Static viewer: shape wireframe only.** Filled faces and per-collider id labels are not yet implemented.
- **Multi-viewport / split-screen** is out of scope — one world overlay renders to all viewports.

---

## 7. Troubleshooting

| Symptom | Check |
|---|---|
| Nothing draws during play | Both nodes `Enabled`? A session active (Host/Join done)? Do your callbacks implement `IFPPhysicsProviderSource` (§1.2)? |
| Draws, but the panel is empty | Panel's `Visualizer` export wired to the visualizer node? |
| Overlay persists with `Enabled` off | Should not happen — update the addon (per-frame visibility is gated on `Enabled`). |
| Wireframe doubly offset / misplaced | The overlay node uses `TopLevel=true` (world-space). Don't override that. |
| Visualizer added at runtime sees no data | It missed `OnSessionCreated` — place it in the `.tscn` before the session, or call `Bind(session)` (§2). |
| Static viewer shows nothing | Tool toggled on + a 3D scene open? Dock path a valid non-empty `res://` `.bytes` from `Klotho: Export Static Colliders` + `Load` clicked? |
| Menu `Klotho: Static Collider Viewer` missing | Addon enabled + C# built once? |
| New nodes/changes not in the sample | Re-run `Tools/deploy-addon-to-samples.sh` after editing `Godot~/Adapters/**`, then rebuild. |

---

*Implementation: [`com.xpturn.klotho/Godot~/Adapters/Physics/GodotFPPhysics*.cs`](../com.xpturn.klotho/Godot~/Adapters/Physics/) · plan: [`Docs/IMP/IMP57/Plan-GodotPhysicsWorldVisualizer.md`](IMP/IMP57/Plan-GodotPhysicsWorldVisualizer.md)*
