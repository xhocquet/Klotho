# Klotho Synchronization — Design Direction

> A design-rationale companion to [Specification.md](Specification.md).
> Where the specification answers *"what does the engine do and what are the exact APIs/defaults?"*, this document answers *"why is the synchronization built this way, and what tensions did each decision resolve?"*

---

## 1. Scope and Intent

Klotho is a deterministic, tick-based multiplayer simulation engine. "Synchronization" here is not a single algorithm but a **layered system** whose job is to keep N independently-running simulations producing byte-identical state, while hiding network latency from the player.

This document explains the *design direction* of that system:

- the **invariant** everything else is built on (determinism),
- the **central abstraction** that organizes the whole engine (the two-chain model: *Verified* vs *Predicted*),
- the four problem domains the design carves the work into — **predict, time, verify, recover** — and why each is shaped the way it is,
- how the two network modes (**P2P lockstep** and **Server-Driven**) reuse the same core machinery under different authority assumptions, and
- the **trade-offs and non-goals** that bound the design.

Exact API signatures, message layouts, and default constants live in [Specification.md](Specification.md); this document references them rather than restating them.

---

## 2. Design Goals and the Tensions Between Them

The synchronization design is the negotiated settlement of four goals that actively conflict:

| Goal | Pulls the design toward… | …and fights against |
| --- | --- | --- |
| **Responsiveness** (input feels instant) | running the simulation *now*, before remote input arrives | correctness — you're guessing |
| **Correctness** (everyone agrees on the world) | waiting for confirmed input, comparing hashes | responsiveness — waiting is lag |
| **Minimal bandwidth** (cost, scale) | sending *inputs only*, never world state | recoverability — you can't just re-send state when something goes wrong |
| **Determinism** (same input → same result) | fixed-point math, fixed iteration order, no wall-clock in logic | ease of authoring — floats and `DateTime` are forbidden in the sim |

No single point satisfies all four. Klotho's resolution is:

1. Make **determinism** a hard, non-negotiable foundation (Section 3). Once it holds, *state is a pure function of inputs*, which makes everything above it cheap.
2. Buy **responsiveness** with **speculative execution** — predict remote input and run immediately (Section 5).
3. Pay for the occasional wrong guess with **rollback**, which is affordable *only because* determinism lets us recompute state from inputs (Section 6).
4. Keep **bandwidth** flat by transmitting inputs only; use that same property to make **verification** a tiny hash exchange rather than a state diff (Section 8).
5. Accept that determinism can still break (platform bug, packet corruption, late join) and build a **graded recovery ladder** as the safety net (Section 9).

The rest of the document is these five moves, expanded.

---

## 3. The Keystone: Determinism

Everything downstream assumes one property:

> Given the same ordered set of inputs, every peer computes byte-identical state, on every platform, every compiler, every run.

This is why the simulation excludes `float`/`double` and is built on `FP64` (32.32 fixed-point) plus a deterministic RNG (Xorshift128+). See [Specification.md §8](Specification.md) for the math layer.

The design payoff of treating this as an *invariant* rather than a *best-effort* is structural:

- **State is reconstructible from inputs alone.** This is what makes rollback cheap (Section 6) and what lets the network carry inputs only (Section 8).
- **Verification reduces to equality of a hash.** If two peers ran the same inputs and got different hashes, the *only* possible cause is a determinism violation — there is no "acceptable drift." That sharpens desync into a binary, debuggable signal (Section 9).
- **The same binary runs on client and server.** The engine core is pure C# with no `UnityEngine` dependency, so the authoritative server and the client run *the identical simulation* — there is no second implementation to keep in sync. This is a synchronization decision disguised as an architecture decision: it removes an entire class of client/server divergence by construction.

Because determinism is load-bearing, the engine ships a **determinism validator** ([SyncTestRunner.cs](com.xpturn.klotho/Runtime/Core/Sync/SyncTestRunner.cs)) that, every few ticks, rolls back and re-simulates the same inputs and asserts the hash is unchanged. This catches non-determinism in development *before* it reaches the network as a desync (Section 9.5).

---

## 4. The Central Abstraction: Two Chains

The single most important idea in the engine's organization is that each peer maintains **two timelines over the same tick axis**:

- **Verified chain** — ticks for which *all* participating players' real inputs are known. State here is authoritative and will never change. Tracked by `_lastVerifiedTick`.
- **Predicted chain** — ticks from `_lastVerifiedTick + 1` up to `CurrentTick`, executed using a mix of real local input and *guessed* remote input. State here is provisional and may be rewound.

```text
  tick:  … 97   98   99  │ 100  101  102  103
         ───────────────┼────────────────────►
        ◄── Verified ───┤◄──── Predicted ────►
           (immutable)  │   (speculative, rollback-able)
                        │
              _lastVerifiedTick = 99
                                          CurrentTick = 103
```

This split is the organizing principle for nearly every subsystem:

| Subsystem | Verified-chain behavior | Predicted-chain behavior |
| --- | --- | --- |
| **Execution** | `ExecuteTick` with confirmed inputs | `ExecuteTickWithPrediction` with guessed inputs |
| **Events** (Section 10) | fire as *Confirmed* / *Synced* (once, durable) | fire as *Predicted* (cosmetic, retractable) |
| **Snapshots** | not needed (won't rewind) | every tick saved for rollback |
| **Network verify** | hash compared against peers | not yet hashed |

The chain boundary advances *continuously and without gaps* via `TryAdvanceVerifiedChain` ([KlothoEngine.FrameVerification.cs](com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.FrameVerification.cs)): a tick is promoted to verified only when the input buffer holds every active player's command for it. The moment a gap is found, advancement stops and `OnChainAdvanceBreak` fires. This makes "are we falling behind?" an observable, first-class signal rather than something inferred later.

**Why two chains instead of one "best guess" timeline?** Because the two have fundamentally different *contracts*. Verified state can be acted on irreversibly (award a kill, spend a resource, broadcast to spectators). Predicted state must be retractable. Conflating them would force every consumer to reason about retraction; separating them lets the engine give each consumer exactly the guarantee it needs (Section 10).

---

## 5. Speculative Execution — Why We Predict, and How

### 5.1 The CPU-pipeline analogy

The design borrows directly from CPU branch prediction (see [Specification.md §"Speculative Execution"](Specification.md)):

| CPU pipeline | Klotho |
| --- | --- |
| predict branch outcome | predict remote player's input |
| advance pipeline speculatively | advance simulation on predicted input |
| prediction hit → commit | hit → no rollback |
| prediction miss → flush + re-execute | miss → snapshot restore + re-simulate |

The design bet is the same bet CPUs make: **misprediction is rare enough that paying its full cost is cheaper than always waiting.** In a networked game, "waiting" means input lag on *every* frame; "rollback" means a recompute only on the frames where a guess was wrong.

### 5.2 The prediction model: repeat the last continuous input

Klotho's predictor (`SimpleInputPredictor` in [Input/Impl](com.xpturn.klotho/Runtime/Input/Impl)) deliberately uses the **simplest model that works**: for a missing remote input, replay that player's most recent *continuous* input (movement, aim, fire-held), scanning a short history window (`PREDICTION_HISTORY_COUNT`, 5).

Two design choices are doing the work here:

1. **Continuous vs one-shot.** Continuous inputs (holding a direction) have high frame-to-frame autocorrelation — "what they did last tick" is an excellent predictor of "what they do this tick." One-shot inputs (spawn, skill, jump) do *not*; guessing them produces a spawned-then-un-spawned flicker on every miss. So one-shots are **never predicted** — the predictor emits an empty command for them and lets the real input arrive. This concentrates rollbacks on the cheap, common case and avoids them on the expensive, jarring case.

2. **Clone by serialization.** A predicted command is produced by serializing and deserializing the historical one, so the predicted input is *byte-identical* to a real input of the same shape. This matters because mismatch detection (Section 6.1) is itself a byte comparison — the predictor and the verifier speak the same representation, so "did the guess match reality?" is exact, not heuristic.

The predictor also tracks a running hit rate (`correct / total`), which feeds diagnostics and the dynamic-delay policy (Section 7.3): *sustained* misprediction is a signal that the timing buffers are too thin, not that the model is wrong.

**Why not a smarter predictor (velocity extrapolation, ML, etc.)?** Because a smarter predictor that is ever *wrong in a new way* still triggers the same rollback machinery, while adding state that itself must be deterministic and rolled back. "Repeat last input" is wrong predictably and recovers identically every time — it composes cleanly with rollback. Sophistication was spent on *recovery correctness*, not *guess cleverness*.

---

## 6. Rollback — The Pipeline Flush

When a real remote input arrives and disagrees with what was predicted, the speculative chain from that tick onward is invalid and must be recomputed.

### 6.1 Detecting the miss

Predicted commands are held in `_pendingCommands`. When the real command for `(tick, playerId)` arrives, it is byte-compared against the prediction. Equal → the guess was right, nothing happens (the common case, and it costs only a comparison). Different → `RequestRollback(tick)` is queued. See [KlothoEngine.Rollback.cs](com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.Rollback.cs).

### 6.2 Deferral and merge — rollback once per frame, to the earliest point

Rollbacks are **not** executed the instant a mismatch is found. They are recorded (`_hasPendingRollback`, keeping the *minimum* requested tick) and flushed once at end of frame:

```text
multiple mismatches this frame  →  keep the earliest target  →  one rollback covers them all
```

The rationale is twofold:

- **Correctness:** executing a rollback mid-tick-loop would invalidate state the loop is still iterating over. Deferral gives a clean boundary.
- **Efficiency:** several late packets in one frame often span overlapping ranges. Rolling back once to the earliest tick re-simulates the union in a single pass instead of thrashing.

### 6.3 Re-simulation reuses the *same* prediction path

After restoring the snapshot at the resolved tick, re-simulation from there to `CurrentTick` runs the **identical** "real-where-known, predict-where-missing" logic as the original forward pass. This is deliberate: the corrected run must be reproducible by any peer that later receives the same inputs. There is no special "rollback mode" of the simulation — there is only *the* simulation, run again with better information.

### 6.4 Snapshot ring buffer — sizing as a design statement

Snapshots are stored in a fixed-capacity ring (`RingSnapshotManager`) sized `MaxRollbackTicks + 2`. The `+2` is headroom for the one-tick prediction lead and event-diff timing. The fixed capacity is a *deliberate bound*: it caps memory, and — more importantly — it makes "how far back can we recover by rollback alone?" a known, finite number (`MaxRollbackTicks`, default 50). Anything that would need to reach further is *by design* escalated to a different recovery tier (Section 9), rather than silently growing buffers without limit.

This is the recurring shape of the design: **bound the cheap mechanism, and escalate past its edge to a more expensive one**, rather than making any one mechanism handle everything.

---

## 7. Timing — Making Predictions Land

Prediction and rollback handle *content* (what the input was). A separate problem is *timing*: getting remote inputs to arrive close to when they're needed, and keeping the local render smooth despite jitter. This is split into three cooperating mechanisms.

### 7.1 Static input delay — the baseline buffer

Local input is stamped for a *future* tick: `targetTick = CurrentTick + InputDelayTicks (+ extra)`. With defaults (`TickIntervalMs = 25`, `InputDelayTicks = 4`) this is a 100 ms head start. The design intent: if the buffer covers the round-trip, remote inputs arrive *before* their execution tick and **no prediction or rollback is needed at all**. Input delay is the *first* line of defense; prediction is what catches whatever the delay didn't cover. Bandwidth note: because only inputs travel the wire, this buffer costs latency-hiding, not bytes.

### 7.2 Clock synchronization — a shared time origin

Peers establish a **shared epoch** at handshake and a **per-peer offset** to the host clock (host offset = 0), in [SharedTimeClock.cs](com.xpturn.klotho/Runtime/Network/SharedTimeClock.cs). Separating *origin* (the handshake instant, fixed) from *drift* (per-peer offset, continuous) means each peer can converge its clock independently without renegotiating a common zero-point mid-match.

On top of this, frame-advantage exchange ([KlothoEngine.TimeSync.cs](com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.TimeSync.cs)) computes how far ahead/behind the local tick is versus peers. Two robustness choices stand out:

- **Median, not mean**, over remote ticks — one delayed packet shouldn't yank the estimate.
- **Staleness gate at `MaxRollbackTicks`** — remote ticks older than the rollback window are discarded, so the system never chases ancient peer state.

### 7.3 Adaptive timing — react to conditions, don't over-provision

Timing buffers face the classic tension: too small → late inputs and rollbacks; too large → constant input lag. Klotho's answer is to **adapt** rather than pick a fixed conservative value.

**Recommended extra delay** ([RecommendedExtraDelayCalculator.cs](com.xpturn.klotho/Runtime/Core/RecommendedExtraDelayCalculator.cs)) derives a buffer from measured RTT:

```text
rttTicks   = ceil(min(avgRtt, RttSanityMaxMs) / TickIntervalMs)
extraDelay = clamp(rttTicks + safety, 0, MaxRollbackTicks / 2)
```

- **`RttSanityMaxMs` (240 ms) cap:** a single retransmit-inflated RTT sample must not balloon everyone's input lag.
- **`safety` margin (`LateJoinDelaySafety`, 2):** absorb variance below the mean; also the fallback when RTT is unmeasurable.
- **Clamp to `MaxRollbackTicks / 2`:** never recommend a delay so large it leaves no rollback headroom — the timing buffer and the rollback budget are explicitly kept from cannibalizing each other.

**Adaptive render clock** ([AdaptiveRenderClock.cs](com.xpturn.klotho/Runtime/Core/Clock/AdaptiveRenderClock.cs)) smooths *presentation* by gently speeding up or slowing down playback toward the verified stream, using an EMA of arrival drift:

```text
drift > +1 tick  →  timescale 0.96  (we're ahead, ease off)
drift < -1 tick  →  timescale 1.02  (we're behind, catch up)
otherwise        →  1.00
```

The asymmetry (slow down 4%, speed up only 2%) encodes a *conservative bias*: arriving early is safe; arriving late costs an input. The render clock would rather hang back than overshoot and oscillate. The interpolation buffer itself is also sized dynamically from delivery-time jitter (`sendInterval + stddev × tolerance`), clamped to the static `InterpolationDelayTicks`, so smoothing scales with measured network variance instead of a hand-tuned constant.

**Reactive escalation** ([DynamicInputDelayPolicy.cs](com.xpturn.klotho/Runtime/Core/Engine/DynamicInputDelayPolicy.cs)) is the client-side fallback when push-based control lags. If past-tick rejections or rollback *bursts* accumulate within a sliding window, the client bumps its own recommended delay — but only after a **grace period** (defer to authoritative server pushes, which are lower-latency) and a **cooldown** (don't escalate every frame). Grace + cooldown exist specifically to prevent the escalation from oscillating against the very condition it's reacting to.

The unifying philosophy across 7.3: **measure, then provision the minimum that survives observed jitter — and prefer the failure that's invisible (slightly early / slightly over-buffered) over the failure that's felt (late input / sudden hitch).**

---

## 8. Authority Models — One Core, Two Topologies

The two-chain machinery (Sections 4–6) is *mode-agnostic*. What differs between network modes is **who owns the verified chain** and **how inputs reach it**. This difference is isolated behind `IKlothoModeStrategy` ([IKlothoModeStrategy.cs](com.xpturn.klotho/Runtime/Core/IKlothoModeStrategy.cs)) so core engine code never branches on the mode enum — it asks the strategy. This keeps the speculative core honest: there is one prediction path, one rollback path, regardless of topology.

### 8.1 P2P Lockstep — distributed, equal authority

Every peer holds *equal* simulation authority; no server. Each peer broadcasts its own input and independently simulates the whole world. The verified chain advances locally as soon as all peers' inputs for a tick are in hand. Verification is **peer-to-peer hash comparison** (`SyncCheck`): everyone hashes, everyone compares, mismatch raises an event.

Design consequences that fall out of "no central authority":

- **The host is a sequencer, not an oracle.** Authority for *correctness* is the determinism invariant itself, not a machine. The host's special role is limited to operations that genuinely need a single decider (e.g., corrective reset, Section 9.4).
- **Host loss ends the session.** With equal authority and inputs-only transport, there is no authoritative state holder to fail over to. P2P treats this as a clean session-end rather than pretending to recover (reconnect is guest-only).
- **Watchdogs guard the edges.** Because there's no server to declare a player dropped, P2P adds peer-local watchdogs: a quorum-miss timer (`QuorumMissDropTicks`, 20) that triggers reactive empty-fill before the transport even reports a disconnect, and a chain-stall abort (`MinStallAbortTicks`) that ends a match the local peer can no longer make progress in.

### 8.2 Server-Driven — centralized authority, client prediction

A server owns the authoritative simulation; clients predict locally and **validate against the server's broadcast hash**. The same two-chain engine runs on the client, but the verified chain is now *defined by the server*, not by local quorum.

Server side ([KlothoEngine.ServerDriven.cs](com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.ServerDriven.cs), [ServerInputCollector.cs](com.xpturn.klotho/Runtime/Network/ServerInputCollector.cs)):

- Opens a per-tick input window with a **hard-tolerance deadline**. Inputs past the deadline are rejected; missing players are substituted with `EmptyCommand`. This trades a stragglers's fidelity for *fairness and fixed tick timing* — the server never waits an unbounded time on one slow client.
- Executes, computes the authoritative hash, and **broadcasts `(tick, commands, hash)`** to all clients.

Client side ([KlothoEngine.ServerDrivenClient.cs](com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.ServerDrivenClient.cs)):

- Predicts ahead of the server (lead ticks), under **soft- and hard-throttle** limits so it never predicts further than it can afford to roll back.
- On each batch of verified messages: rollback to the nearest snapshot, **re-simulate with the server's real inputs while checking each tick's hash against the server's**, then re-predict the tail. A hash mismatch here is, by the determinism invariant, a genuine divergence — and is escalated straight to a full-state request (Section 9).

The contrast captures the whole point of having one core serve both:

| | P2P Lockstep | Server-Driven |
| --- | --- | --- |
| Verified chain owned by | local quorum of all peers | the server |
| Hash role | peer cross-check, advisory | **authoritative gate**, critical path |
| Missing input | wait / predict / watchdog | server fills `EmptyCommand` at deadline |
| Failure of authority | session ends (host loss) | client reconnects; server persists |
| Scales with | small rosters (2–8) | server capacity, not player count |

Same prediction, same rollback, same snapshots — different answer to *"whose verified chain is the truth?"*

---

## 9. Recovery — The Escalation Ladder

Determinism *should* hold, and rollback *should* repair every misprediction. The recovery system exists for when one of those doesn't: a platform-specific math bug, a corrupted packet, a divergence deeper than the rollback window, a joiner who has no state at all. The design principle is a **graded ladder — cheapest mechanism first, escalate only on failure** — so the common case stays cheap and the rare case stays survivable.

```text
   detect        rung 1            rung 2              rung 3                 rung 4
  ┌───────┐   ┌──────────┐   ┌──────────────┐   ┌────────────────┐   ┌──────────────────┐
  │ hash  │ → │ rollback │ → │ full-state   │ → │ corrective     │ → │ abort match      │
  │ gate  │   │ to last  │   │ resync       │   │ reset          │   │ (give up cleanly)│
  │       │   │ matched  │   │ (request     │   │ (host forces   │   │                  │
  │       │   │ tick     │   │  fresh state)│   │  all to a tick)│   │                  │
  └───────┘   └──────────┘   └──────────────┘   └────────────────┘   └──────────────────┘
   cheap ──────────────────────────────────────────────────────────────────► last resort
```

### 9.1 The hash gate (detection)

Every `SyncCheckInterval` (30) ticks, peers hash state and exchange it. A **pending grace window** holds each check open until the next one: if no mismatch arrives in between, the tick is promoted to `_lastMatchedSyncTick` — a known-good rollback anchor. This tolerates transient jitter (a hash that's merely *late* doesn't trigger a false alarm) while still catching real divergence. Hashes are sent unreliable: a dropped hash simply skips a check, it doesn't stall anything.

### 9.2 Rung 1 — rollback to last matched (cheapest)

On a detected desync, the first response is a normal rollback to the last *matched* sync tick — reusing the exact machinery of Section 6. If the divergence was a recoverable timing artifact, this fixes it for free. Consecutive desyncs are counted.

### 9.3 Rung 2 — full-state resync (when rollback can't reach)

When desyncs persist past `DesyncThresholdForResync` (3), or a rollback target falls outside the snapshot window, the engine requests a **full state transfer** ([KlothoEngine.FullStateResync.cs](com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.FullStateResync.cs)). This is the one place inputs-only is *intentionally* violated — and it's bounded to the rare case, which is why violating it is acceptable. Design details that matter:

- **The provider caches serialized state per tick**, so repeated requests for the same tick don't re-serialize.
- **Apply is hash-verified on arrival.** The receiver restores, recomputes its own hash, and compares to the sender's. A mismatch *after* a full-state apply is logged with per-component hashes — this is the deepest diagnostic, because it means the divergence is in the state representation itself.
- **A retreat guard** forbids applying a full state *older* than the current verified tick, except for the few reasons that legitimately rewind (corrective reset, late join, initial state). This prevents resync from itself becoming a source of desync by moving a peer backward.
- Full-state has a **timeout with auto-retry** up to `ResyncMaxRetries`; exhausting it raises `OnResyncFailed`.

### 9.4 Rung 3 — corrective reset (host forces consensus)

If divergence persists even after resync, the **host** (P2P) can broadcast its state and force every peer — including itself — to a common tick. A **cooldown** (`CorrectiveResetCooldownMs`, 5000) prevents a persistent divergence from triggering a broadcast storm. This is the only rung that uses centralized authority in P2P, and it's reserved for "we cannot otherwise agree."

### 9.5 Out-of-band guards and the dev-time net

Two mechanisms sit beside the ladder rather than on it:

- **Static-geometry fingerprint.** Static colliders are *not* part of the per-tick state hash (they don't change), so a static-only divergence (e.g., a mismatched map asset) would otherwise stay invisible until it perturbed dynamic state several ticks later. A separate fingerprint check surfaces it immediately, at the source.
- **SyncTestRunner** ([SyncTestRunner.cs](com.xpturn.klotho/Runtime/Core/Sync/SyncTestRunner.cs)). In development builds, every few ticks the engine rolls back `checkDistance` ticks, re-simulates the recorded inputs, and asserts the hash is identical. This catches non-determinism *locally, without a network*, turning "a player desynced in production" into "a unit test failed on my machine." It is the design's acknowledgment that the cheapest place to catch a determinism bug is before it ships.

### 9.6 Why a ladder instead of "always resync"?

Always sending full state would be correct and simple — and would throw away the bandwidth and scale properties the whole design is built to protect. The ladder keeps the expensive, inputs-only-violating mechanism (resync) confined to the genuinely rare case, while the common recoverable case is handled by rollback, which costs nothing on the wire.

---

## 10. Event Semantics — Mapping Chains to Side Effects

A simulation tick produces not just state but *events* (a hit landed, a sound should play, a score changed). The two-chain model dictates *when* an event may be acted on, because an event born on the predicted chain might never have really happened.

The design splits events by their **durability requirement**:

- **Regular events** (cosmetic, local: a muzzle flash, a footstep). These may fire **speculatively** on the predicted chain as `OnEventPredicted`. If a rollback later erases them, the engine emits `OnEventCanceled`. Players tolerate a rarely-retracted cosmetic far better than they tolerate input lag, so these ride the predicted chain.
- **Synced events** (game-critical, possibly networked/scored). These are **buffered, not fired**, while predicted. They emit only when their tick crosses into the verified chain, as `OnSyncedEvent` — exactly once.

The "exactly once" is enforced by a **high-water mark** (`_syncedDispatchHighWaterMark`). Rollback rewinds `_lastVerifiedTick` and then re-advances it over ticks that may have already dispatched their synced events; the high-water mark ensures a given tick's synced events never fire twice. (Full-state resync, which wipes the event buffer, resets the mark accordingly.)

After a rollback, the engine doesn't blindly re-fire everything — it **diffs** old vs new event sets for the re-simulated range and emits only the deltas: `OnEventCanceled` for events that no longer occur, `OnEventConfirmed`/`OnEventPredicted` for newly-correct ones, compared by tick + type + content hash. Game code thus receives a precise "what changed because the prediction was wrong" stream rather than a duplicate flood.

The design direction here: **let each event consumer subscribe to the guarantee it actually needs** — retractable-but-instant for cosmetics, durable-and-once for game state — instead of forcing one timing policy on all of them.

---

## 11. Error Correction — Hiding the Seam of a Rollback

A rollback can teleport a remote entity when the corrected position differs from the predicted one. Snapping is jarring. Error correction ([KlothoEngine.ErrorCorrection.cs](com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.ErrorCorrection.cs), config in [ErrorCorrectionSettings.cs](com.xpturn.klotho/Runtime/Core/ErrorCorrectionSettings.cs)) computes the visual delta between pre- and post-rollback transforms and lets the **view layer** decay it smoothly, while the **simulation** stays exactly on the corrected (authoritative) value.

This is a deliberate **separation of correctness from presentation**: the simulation is never allowed to lie for the sake of smoothness — only the rendered transform is interpolated. Thresholds encode intent:

- below `PosMinCorrection` / `RotMinCorrectionDeg`: ignore (don't jitter the view over sub-perceptible noise),
- above `PosTeleportDistance` / `RotTeleportDeg`: snap (a genuine teleport *should* look like one — smoothing it would read as a glide through a wall),
- in between: exponential decay at a rate that scales with error magnitude (`MinRate`→`MaxRate`).

It is **off by default** (`EnableErrorCorrection = false`): it costs per-entity work and only earns its keep under latency high enough to make rollback corrections visible. The design exposes it as a knob rather than baking it in — consistent with the "provision the minimum the conditions require" theme of Section 7.

---

## 12. Resource Discipline as a Synchronization Concern

Zero-GC is listed as a general engine goal, but it is *specifically* a synchronization concern: a GC spike stalls the tick loop, which delays input send/receive, which causes late inputs, which causes rollbacks. Allocation hitches and desync pressure are the same problem viewed from two angles.

Hence the hot paths — `ExecuteTick`, prediction, rollback re-simulation, event diff — are built on object pools, reusable caches, `stackalloc` for command (de)serialization, and fixed-capacity ring buffers (snapshots, events, hash history). The buffers are sized to the rollback window and **actively trimmed** (`CleanupOldData`) to `CurrentTick − MaxRollbackTicks − margin`, never ahead of `_lastVerifiedTick`. The cleanup margin (10) is the same kind of deliberate headroom as the snapshot ring's `+2`: leave enough that an edge-case rollback or a late event doesn't fall off the end, but bound it so memory and time stay flat over an arbitrarily long match.

---

## 13. The Design Knobs and What They Trade

The configuration surface (full defaults in [Specification.md §2.2](Specification.md)) is best read as a set of **trade-off dials**, each moving one of the Section-2 tensions:

| Knob | Default | Larger value buys… | …at the cost of |
| --- | --- | --- | --- |
| `InputDelayTicks` | 4 | fewer predictions/rollbacks (inputs arrive in time) | more felt input latency |
| `MaxRollbackTicks` | 50 | recover deeper divergences by rollback before resync | more snapshot memory |
| `SyncCheckInterval` | 30 | less hashing/bandwidth | slower desync detection |
| `UsePrediction` | true | responsiveness | rollback cost (false ⇒ wait-for-input, pure lockstep) |
| `RttSanityMaxMs` | 240 | tolerate genuinely high-RTT links | risk of outliers inflating delay |
| `DesyncThresholdForResync` | 3 | tolerate transient desyncs before the expensive resync | longer visible divergence |
| `InterpolationDelayTicks` | 3 | smoother view under jitter | more presentation latency |
| `EnableErrorCorrection` | false | hide rollback snaps under high latency | per-entity view-layer work |
| `HardToleranceMs` (SD) | 0 (auto) | fairer to high-latency clients | later, looser server deadline |

The defaults target small-roster real-time PvP at 40 ticks/s; the dials let a turn-based, high-latency, or large-room title re-balance the same machinery.

---

## 14. Trade-offs and Non-Goals

Stating what the design *deliberately does not do* is as important as what it does:

- **Not interest-managed / not state-replicated.** Because only inputs travel, every peer simulates the *entire* world. This is what keeps bandwidth independent of entity count — and it means the design does **not** target massive open worlds where each client should see only a slice. It targets bounded, fully-shared simulations (fighting, RTS, MOBA, tactics, auto-battlers).
- **Not float-friendly.** The determinism invariant forbids `float`/`double` in simulation logic. Authoring cost (fixed-point math, deterministic RNG) is paid up front in exchange for cross-platform reproducibility.
- **Prediction is intentionally dumb.** "Repeat last input" was chosen over cleverer extrapolation because it composes cleanly with rollback (Section 5.2). Smartness was invested in *recovery*, not *guessing*.
- **P2P does not fail over.** Equal authority + inputs-only means there is no authoritative state holder to promote on host loss; P2P ends the session cleanly rather than feigning recovery. Persistence-across-disconnect is a Server-Driven property by design.
- **Bounded recovery, then graceful surrender.** The ladder (Section 9) ends in `AbortMatch`. The design would rather end a match honestly than mask an unrecoverable divergence and let peers drift apart silently.

Each non-goal is the shadow of a goal: the same decisions that make Klotho excellent for deterministic, bandwidth-flat, small-roster real-time play are what make it the wrong tool for replicated, float-heavy, massive-scale worlds. The synchronization design is coherent precisely because it refuses to be all of those at once.

---

## See Also

- [Specification.md](Specification.md) — exact APIs, message types, defaults, state machine
- [FEATURES.md](FEATURES.md) — feature-level overview
- Source of truth: [com.xpturn.klotho/Runtime/Core/Engine/](com.xpturn.klotho/Runtime/Core/Engine/) (the `KlothoEngine.*.cs` partials), [Runtime/Core/Clock/](com.xpturn.klotho/Runtime/Core/Clock/), [Runtime/Network/](com.xpturn.klotho/Runtime/Network/)
</content>
</invoke>
