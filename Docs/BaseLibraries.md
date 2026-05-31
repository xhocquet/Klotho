# xpTURN.Klotho Base Libraries

> The list of general-purpose base libraries used by the Klotho framework.
> Only project-infrastructure-level libraries — those not directly tied to Klotho-specific logic — are included.

---

## A. xpTURN First-Party Libraries

Standalone packages shared across the xpTURN ecosystem. Installed via Git URL.

### xpTURN.Polyfill

| Item | Contents |
| ---- | ---- |
| Purpose | Polyfills for C# 9 / 10 / 11 language features (targeting .NET Standard 2.1) |
| Git URL | `https://github.com/xpTURN/Polyfill.git?path=src/Polyfill/Assets/Polyfill` |
| Assembly | `xpTURN.Polyfill.Runtime` |
| Dependencies | None (lowest layer) |

**Provided types**:

- `IsExternalInit` — C# 9 init-only properties
- `InterpolatedStringHandlerAttribute` — C# 10 custom interpolated strings
- `CallerArgumentExpressionAttribute` — C# 10
- `RequiredMemberAttribute`, `CompilerFeatureRequiredAttribute` — C# 11
- `ModuleInitializerAttribute` — C# 9 (used by KlothoGenerator emit)

**Klotho usage**: Enables modern syntax (`init` accessors, interpolated-string handlers, etc.) in math structs such as FP64 and the FPVector family. `ModuleInitializerAttribute` is required by the source generator's auto-registration emit.

---

### xpTURN.Klotho.Logging (IKLogger)

| Item | Contents |
| ---- | ---- |
| Purpose | In-house structured logging (no external logging dependency) |
| Location | [`Packages/com.xpturn.klotho/Runtime/Logging/`](../Klotho/Packages/com.xpturn.klotho/Runtime/Logging/) (engine-agnostic) + `Runtime/Unity/Logging/` (Unity sink) |
| Assembly | `xpTURN.Klotho.Logging`, `xpTURN.Klotho.Logging.Unity` |
| Dependencies | `xpTURN.Polyfill.Runtime` only (`noEngineReferences: true` for the core) |

**Features**:

- `IKLogger` / `KLoggerFactory` — zero external-deps logging interface
- `KLogHandler{Trace,Debug,Information,Warning,Error,Critical}` ref-struct interpolated handlers + `KInformation`/`KDebug`/`KWarning`/`KError` extension methods — uses Polyfill's `InterpolatedStringHandlerAttribute` for GC-free formatting
- Pluggable sinks — `UnityDebugSink`, Rolling-File sink (in `Runtime/Unity/Logging`)
- `RollingFileSink` flush policy — `KFlushMode.PerLine` (default; one flush per line, immediate visibility, ≤ 1 line crash window) or opt-in `KFlushMode.AsyncEvent` (background-thread flush — no per-line flush syscall on the hot path, natural batching of bursts, drains on `Dispose`/process-exit). Select via `AddRollingFile(o => o.FlushMode = KFlushMode.AsyncEvent)`. The dedicated server uses `AsyncEvent`.

**Klotho usage**: Standard logging interface used framework-wide.

**Optional MEL Interop adapter** ([`Plugins~/Logging.Mel/`](../Klotho/Packages/com.xpturn.klotho/Plugins~/Logging.Mel/)):

Opt-in package sample (UPM "Import Sample" → **MEL Logging Plugin**) that bridges `IKLogger` to `Microsoft.Extensions.Logging.ILogger`. Activating the adapter requires the consumer to provide `Microsoft.Extensions.Logging.Abstractions.dll` — consumer responsibility (the core package stays self-contained).

A runnable end-to-end example lives at [`Samples/LoggingMelConsole/`](../Samples/LoggingMelConsole/) — a .NET console app that routes Klotho's `IKLogger` surface through a standard `Microsoft.Extensions.Logging` pipeline via `MelKLogger`, logging to both the console and rolling files under `Logs/` with a ZLogger provider.

---

## B. Cysharp Open-Source Libraries

Cysharp-ecosystem libraries used across the xpTURN project.

### UniTask

| Item | Contents |
| ---- | ---- |
| Purpose | Unity-specific async (GC-free async/await) |
| Git URL | `https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask` |

**Features**:

- `UniTask`, `UniTask<T>` — ValueTask-based, zero GC
- `UniTaskCompletionSource` — manual completion control
- `PlayerLoopTiming` — selectable Unity loop timing
- `WhenAll`, `WhenAny` — parallel async

**Klotho usage**: Asynchronous view creation in `EntityViewFactory.CreateAsync` (View layer), network connect/disconnect, replay file I/O. **UniTask is referenced only by the Unity-side adapter (`xpTURN.Klotho.Runtime.Unity`)** — the engine-agnostic core does not depend on it.

---

## C. Vendored Third-Party Libraries

Third-party libraries shipped as source under [`Packages/com.xpturn.klotho/Runtime/ThirdParty/`](../Klotho/Packages/com.xpturn.klotho/Runtime/ThirdParty/) with their original licenses preserved. **Vendoring policy**: vendored sources include `.cs` + `.meta` only — no `.csproj`/`.sln` (to prevent `bin`/`obj` pollution inside `Packages/`).

### LiteNetLib

| Item | Contents |
| ---- | ---- |
| Purpose | Lightweight UDP networking library |
| GitHub | <https://github.com/RevenantX/LiteNetLib> |
| Version | 2.1.4 (vendored as [`Runtime/ThirdParty/LiteNetLib.v2.1.4/`](../Klotho/Packages/com.xpturn.klotho/Runtime/ThirdParty/LiteNetLib.v2.1.4/)) |
| License | MIT |
| Assembly | `LiteNetLib` |

**Features**:

- Reliable UDP (Reliable Ordered / Unordered / Sequenced)
- Automatic fragmentation, MTU discovery
- NAT punching, connection-request filtering
- Pure C# (Unity-compatible)

**Klotho usage**: `LiteNetLibTransport` (in `Runtime/LiteNetLib/`, `noEngineReferences: true`) — an `INetworkTransport` implementation used for both P2P and server transport.

**Per-message channel mapping**:

| Message | Channel | Reason |
| ---- | ---- | ---- |
| Input (per-tick input) | Sequenced | Only the latest input matters; ordering required; no resend needed |
| InputAck | Unreliable | Loss covered by next ack; minimal latency |
| SyncCheck (hash verification) | ReliableOrdered | Integrity is mandatory; no loss tolerated |
| Handshake (connection setup) | ReliableOrdered | Both reliability and ordering required |

---

### K4os.Compression.LZ4

| Item | Contents |
| ---- | ---- |
| Purpose | High-speed LZ4 block / frame compression |
| GitHub | <https://github.com/MiloszKrajewski/K4os.Compression.LZ4> |
| Version | 1.3.8 (vendored as [`Runtime/ThirdParty/K4os.Compression.LZ4.v1.3.8/`](../Klotho/Packages/com.xpturn.klotho/Runtime/ThirdParty/K4os.Compression.LZ4.v1.3.8/)) |
| License | MIT |
| Assembly | `K4os.Compression.LZ4` |

**Klotho usage**: Replay-file compression / decompression in `ReplaySystem`. Used on the compressed-stream path (distinguished from the uncompressed `RPLY` magic-number path).

---

### System.Runtime.CompilerServices.Unsafe

| Item | Contents |
| ---- | ---- |
| Purpose | Unsafe-cast primitives for high-performance interop |
| Version | 6.1.2 (vendored as [`Runtime/ThirdParty/System.Runtime.CompilerServices.Unsafe.v6.1.2/`](../Klotho/Packages/com.xpturn.klotho/Runtime/ThirdParty/System.Runtime.CompilerServices.Unsafe.v6.1.2/)) |
| Assembly | `System.Runtime.CompilerServices.Unsafe` |

**Klotho usage**: Backing for `SpanWriter/Reader` ref-struct primitives and zero-copy component-storage reinterpretation.

---

## D. Standard Unity Packages

Unity packages declared in [`Packages/com.xpturn.klotho/package.json`](../Klotho/Packages/com.xpturn.klotho/package.json) `dependencies` (auto-resolved from the Unity registry).

| Package | Version | Klotho Usage |
| ---- | ---- | ---- |
| `com.unity.inputsystem` | 1.18.0 | Input-Action-based local input capture → forwarded to the `OnPollInput` callback (`xpTURN.Klotho.Runtime.Unity`) |
| `com.unity.ai.navigation` | 2.0.12 | Unity NavMesh baking → converted to `.bytes` by `FPNavMeshExporter` (Editor NavMesh tooling only) |
| `com.unity.nuget.newtonsoft-json` | 3.2.2 | DataAsset serialization in the `xpTURN.Klotho.DataAsset.Json` assembly — `FPxxJsonConverter`, `DataAssetJsonSerializer` |
| `com.unity.test-framework` | 1.6.0 | NUnit-based unit / integration / determinism tests (dev project's `Assets/Tests`) |
| `com.unity.cinemachine` | 3.1.6 | Brawler sample's `BrawlerCameraController` (`Assets/Brawler` — dev only, not redistributed) |

> Cinemachine / test-framework are dev-project dependencies (`Assets/`); the core package declares only `inputsystem`, `ai.navigation`, `newtonsoft-json` in `dependencies`.

---

## E. Dependency Layering

```
xpTURN.Polyfill.Runtime              ← bottom (no dependencies)

xpTURN.Klotho.Logging                ← + Polyfill (engine-agnostic, noEngineReferences)
xpTURN.Klotho.Logging.Unity          ← + Klotho.Logging (UnityDebugSink, Rolling-File sink)

UniTask                              ← Unity-side only

LiteNetLib (vendored)                ← standalone (pure C#)
K4os.Compression.LZ4 (vendored)      ← standalone (pure C#)
System.Runtime.CompilerServices.Unsafe (vendored)  ← standalone

xpTURN.Klotho.Runtime                ← + Polyfill, Klotho.Logging (noEngineReferences)
xpTURN.Klotho.Runtime.Unity          ← + Klotho.Runtime, Klotho.Logging.Unity, UniTask, InputSystem
xpTURN.Klotho.Runtime (Replay path)  ← + K4os.Compression.LZ4 (ReplaySystem)
xpTURN.Klotho.LiteNetLib             ← + LiteNetLib, Klotho.Runtime, Klotho.Logging (noEngineReferences)
xpTURN.Klotho.DataAsset.Json         ← + Newtonsoft.Json, Klotho.Runtime
xpTURN.Klotho.Editor                 ← + Klotho.Runtime, Klotho.Runtime.Unity, Klotho.DataAsset.Json (Editor-only)
xpTURN.Klotho.Gameplay               ← + Klotho.Runtime, Polyfill

xpTURN.Klotho.Logging.Mel (opt-in)   ← + Klotho.Logging + Microsoft.Extensions.Logging.Abstractions (consumer-provided DLL)
```

> **Engine-agnostic core**: `Klotho.Runtime`, `Klotho.Logging`, `Klotho.Gameplay`, `Klotho.LiteNetLib` (transport) have `noEngineReferences: true` — they compile without UnityEngine and run on .NET dedicated server builds (see [Brawler.H.DedicatedServer.md](./Samples/Brawler.H.DedicatedServer.md)).

---

*Last updated: 2026-05-28 (IMP47 — IKLogger transition + UPM packaging)*
