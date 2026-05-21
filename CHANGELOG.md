# Changelog

## [0.2.0] - 2026-05-21

- IMP-41: Singleton component first-class support — `[KlothoSingletonComponent]` + `Frame.GetSingleton<T>` / `GetReadOnlySingleton<T>` / `TryGetSingleton<T>`; `Frame.Add` enforces one-carrier-per-frame via `ComponentStorageRegistry.TypeIdCache<T>.IsSingleton`.
- IMP-41: Built-in `RandomSeedComponent` (singleton, engine-injected at `Start`; restored via FullState on LateJoin / Reconnect / Spectator / Replay) replaces sample-side `GameSeedComponent`; Brawler `GameTimerStateComponent` migrated to singleton.
- IMP-41: `TransformComponent` marker-based prev-snapshot redesign — `PreviousInitialized` marker drives the `Frame.Add` auto-init hook and PreUpdate `SavePrev` pass; `Frame.RefreshPreviousTransform(entity)` covers post-Add ref-set paths. Sample-side `SavePreviousTransformSystem` removed.
- IMP-41 A-1: `KlothoConnectionAsync` lifted to `Assets/Klotho/Unity/` (Runtime.Unity); A-2: EVU `BindBehaviour` / `ViewFlags` 5-flag decision lifted to `EntityViewFactory` base; A-3: Replay `InitialStateSnapshot` auto-inject lifted to `Engine.StartReplay`; A-4: `IReconnectCredentialsStore` injected via `KlothoSessionSetup.CredentialsStore` (+ `AppVersion` / `DeviceIdProvider`); A-5: client-reactive PastTick / rollback-burst escalation lifted to `DynamicInputDelayPolicy` (thresholds in `SessionConfig`); A-6: `EndGracePolicy.Pause` `StopCommand` auto-injected by engine.
- IMP-41 B-1: `IKlothoSessionObserver` bulk-subscribed lifecycle (replaces per-event `+=` wiring across StartHost / JoinGame / Reconnect / StopGame); adds `OnSessionStopped`. B-2: `OnReconnectFailed(byte)` carries `ReconnectRejectReason` enum directly + `ReconnectFailedException.Reason` for cold-start paths. B-3: `KlothoSession.CreateSpectator(SpectatorSessionSetup)` + `KlothoSpectatorAsync.CreateAsync` — framework owns SpectatorService / two-config await / Engine+Simulation construction; game supplies `CallbacksFactory(simCfg, sessionCfg)`. B-4: `ClientShutdownGraceMs` scheduler routed through `KlothoSession.Update`. B-5: `PlayerViewRegistry<TView>` built into `EntityViewUpdater` (driven by `OwnerComponent` add/remove; events for local-view subscription).
- IMP-41: P2P spectator entry fix — unified spectator broadcast condition + `SpectatorAccept.LastVerifiedTick` derivation + `_lastVerifiedTick` default correction. Spectator bootstrap tick-race fix (framework-side gate) + `ItemSpawnSystem` defensive guard for unwritten singleton seed.
- IMP-42: Spectator EC activation — removed `_isSpectatorMode` guards on `CapturePreRollback` / `ComputeErrorDeltas` / Predict-under-Predicted; `HandleSpectatorUpdate` wires the EC pair only when a verified batch arrives.
- IMP-43: `ISessionConfig` / `ISimulationConfig` realignment — 12 engine-internal fields (Reactive 6, Rollback 2, Resync 3, `CatchupMaxTicksPerFrame`) moved Session → Sim; 3 service-side LateJoin/Reconnect/ChainStall fields (`LateJoinDelaySafety`, `RttSanityMaxMs`, `MinStallAbortTicks`) moved Sim → Session. Wire schema breakage in 5 messages (`GameStart` / `LateJoinAccept` / `SpectatorAccept` / `SimulationConfig` / `ReconnectAccept`) — server/client must roll out together.
- IMP-43: `USessionConfig` ScriptableObject introduced — `ISessionConfig` 16 fields editable from the Inspector. `KlothoSessionSetup` mirror fields (11 props) removed in favor of a single `SessionConfig` reference; `KlothoSession.Create()` normal-path now copies from `setup.SessionConfig` (host-side) and exposes the 5 previously-hidden fields (`MaxSpectators`, `AbortGraceMs`, `EndGracePolicy`, `EndGraceMs`, `ClientShutdownGraceMs`). Brawler sample: `BrawlerSettings._maxPlayers` removed, `_sessionConfig` is the single SoT (StartHost guarded; non-host paths use `InitialMaxPlayersGuess` until server-authoritative config arrives); sample `SessionConfig.asset` added and wired into `BrawlerGameController.prefab`.
- Framework: re-simulation invariant diagnostic guard extended to `DEVELOPMENT_BUILD` (was Editor-only). Pause-grace `StopCommand` auto-inject overwrite blocked + regression test. Brawler sample-system sync tests fixed for the new singleton-entity setup.
- Docs: `FEATURES.md` / `GameDevAPI.md` / `Specification.md` / `Brawler.md` / `Brawler.B.Systems.md` / `Brawler.E.Bootstrap.md` updated for the above lifts (singleton API, `IKlothoSessionObserver`, spectator path, `DynamicInputDelayPolicy`, `OnReconnectFailed(byte)`, `RandomSeedComponent` adoption, sample-side `SavePreviousTransformSystem` removal).

## [0.1.8] - 2026-05-18

- Updated LiteNetLib to [2.1.3](https://github.com/RevenantX/LiteNetLib/releases/tag/2.1.3); renamed `ConnectionRequest` → `LiteConnectionRequest` in `LiteNetLibTransport.OnConnectionRequest` to match the new interface signature.


## [0.1.7] - 2026-05-18

- IMP-40 (`KLOTHO_FAULT_INJECTION`-only): SD RTT-spike desync fix — per-peer FIFO clamp in `LiteNetLibTransport._delayedRecvMessages` resolves the fault-injection reorder bug (production transport unaffected).
- IMP-40: `ProcessVerifiedBatchCore` batch-start gap fill refactored to use `SimulateGapTickWithEmptyFallback` helper, closing a latent bug where missing players were not substituted with `EmptyCommand` (separate from the fault-injection fix above; surfaces in high-RTT predict-snapshot rollback paths).
- IMP-40: SD desync runtime alarm — `[SD][ResimGap]` `ZLogWarning` emits when a verified batch leaves resim behind `entry.Tick`; complemented by dev-only diagnostics (`[InputCollector][Reject]`/`[EmptySubst]`, `[SD][PredSource]`, `[CompHash][History]`). `ServerInit` component hash dump promoted to `ZLogInformation` so release builds still surface it.
- IMP-39: All-participants-spawned gate — introduced `SessionParticipantComponent`; engine writes deterministic participant slots into the frame at `Start()`.
- IMP-39: Normal-end lifecycle — `IMatchEndEvent`/`EndReason` + `OnMatchEnded` channel, grace-driven `Room` drain, wired into `BrawlerGameController`/`GameOverSystem`.
- IMP-39: Client match-end reaction — prediction-freeze path + `ClientShutdownGraceMs` (SessionConfig).
- IMP-39: `StopCommand`-based pause behavior — SD/P2P unified, handled in `PlatformerCommandSystem`.
- IMP-39: `SessionConfig` propagation across 17 fields (6 previously missing + Normal-join bug fix), aligned `GameStartMessage`/`LateJoinAcceptMessage`/`ReconnectAcceptMessage`.

## [0.1.6] - 2026-05-16

- IMP-38: Added `KlothoState.Aborted` — abnormal terminal state distinct from `Finished`; `IsEnded()` extension covers both terminal states.
- IMP-38: `AbortMatch(AbortReason)` API + `OnMatchAborted` event — surfaces ChainStallTimeout / StateDivergence / ReconnectFailed reasons; Brawler subscriber includes double-trigger guard.
- IMP-38: P2P chain-stall watchdog — `CheckChainStallTimeout()`; calls `AbortMatch(ChainStallTimeout)` when lag ≥ `MinStallAbortTicks` ( or reconnect timeout + 100 ticks).
- IMP-38: Corrective Reset — host broadcasts `FullStateKind.CorrectiveReset` via `TryCorrectiveReset()` on hash mismatch; `ApplyReason` enum drives retreat policy; `CorrectiveResetCooldownMs` (default 5 s) prevents broadcast storms; `OnMatchReset(ResetReason)` event for non-terminal recovery.
- IMP-38: Hash gate hardening — `ApplyFullState` blocks post-state application when localHash ≠ remoteHash; `OnHashMismatch(tick, localHash, remoteHash)` event; desync/resync telemetry counters (`ResyncHashMismatchCount`, `ConsecutiveDesyncPeak`, `ResyncRequestTotalCount`, `PostResyncDesyncCount`, `UnexpectedFullStateDropCount`) surfaced in `PresumedDrop` metrics log.
- IMP-38: Reconnect input gap recovery — reconnecting peer input injection changed from `JoinTick` to `LastSentTick + 1`; `IsPlayerInActiveCatchup()` guard suppresses presumed-drop false positives during reconnect catchup.
- IMP-38: `OnPendingWipe(tick, playerId, WipeKind)` event — tracks inputs and SyncedEvents wiped before chain verification; added `RelaySealDropCount` counter.
- DS: `--rtt-metrics` CLI flag for single/multi-room.

## [0.1.5] - 2026-05-13

- IMP-38: RTT distribution metrics from `ServerNetworkService` with `RttMetricsEnabled` runtime toggle; structured metrics for LateJoin/Reconnect extraDelay (game-level playerId); preserve RTT sample across warm Reconnect.
- IMP-38: Phase 2 — clamp policy + interface promotion; replaced `ApplyExtraDelay` bool flag with `ExtraDelaySource` enum.
- IMP-38: Phase 3 — dynamic InputDelay mid-match push + reactive fallback; server-driven `RecommendedExtraDelay` for LateJoin/Reconnect cold-start path; same-tick multi-cmd allowed in monotonic clamp; `avgRtt` clamped to sanity cap; `Sync` extra-delay seed buffered until engine subscribed and branched per `JoinKind` in `SDClientService`; skip `LagReductionLatency` tracker on Reconnect.
- IMP-38: P2P port of `RecommendedExtraDelay` — `IKlothoEngine.IsHost` + `OnChainAdvanceBreak` event; route `RecommendedExtraDelayUpdate` to engine on peers; host `PingPong` hook → dynamic-delay push smoother (full pipeline); guest reactive fallback redesigned from chainbreak-burst to rollback-amplitude (with overflow guard); RTT spike schedule driver + match-scoped metrics collector.
- IMP-38: P2P reconnect hardening — host self-wipe + chain-advance stall P0 fixes (forward gap fill); 3-peer reconnect defects from Phase 2-1 playtest; catchup batch silent drop (defect ④); bundle-based stale peer cleanup on reconnect (dropped `_pendingPeers` keep guard); catchup `InputDelay` window + `Connect` socket cleanup; diagnostic log pruning from reconnect/relay paths.

## [0.1.4] - 2026-05-09

- IMP-36: Unified single-room SD on `RoomManager` bootstrap; exposed drain phase counters and lifetime metric.
- IMP-37: Closed multi-match determinism leak — gated `DeterminismVerification` assembly behind `UNITY_INCLUDE_TESTS` (drops typeId 9000–9002 from runtime layout), skipped `OnInitializeWorld` on SD client to prevent double-init race, added per-component hash dump + pool counters for desync diagnostics.
- IMP-38: Bootstrap & recovery hardening across phases — hardened `InputDelayTicks` validation for SD (Phase 0), state-driven spawn query + resync reconciliation hook (Phase 1), bootstrap handshake removing structural first-tick race (Phase 2), command rejection feedback unicast (Phase 3), fault-injection infrastructure & scenario matrix. Follow-ups: route initial `FullState` through bootstrap path on countdown-skip clients; hybrid (Version + OwnerId) dedup for ghost view; escalate spawn cmd lead on `PastTick` reject.

## [0.1.3] - 2026-04-30

- IMP-35: 3-layer defense against malformed wire packets — L1 `MessageSerializer.Deserialize` try/catch + cache invalidation (overflow-safe boundary check), L3 `Room.DrainInboundQueue` try/finally (guaranteed buffer recovery + loop continuation), L2 server `_pendingPeers` atomicity + immediate disconnect on malformed/unknown payload (pending and regular dispatch). Minimal `ZLogWarning` traceability at 3 client-side wire-input sites.

## [0.1.2] - 2026-04-30

- IMP-32: `LiteNetLibTransport` connection key — constructor injection with `DefaultConnectionKey` constant fallback.
- IMP-33: Propagate disconnect reason via `INetworkTransport.OnDisconnected` — added 6-value `DisconnectReason` enum; `Listen`/`Connect` now return `bool` to surface startup failures immediately. Client handlers gate auto-reconnect to `NetworkFailure`/`ReconnectRequested` only.
- IMP-34: 64-bit `SessionMagic` with CSPRNG generation + device-binding on cold-start reconnect.
- Aligned spectator transport connection key with the main transport.
- Aligned `Connect` API deviceId with the provider pattern; added send-site logging.

## [0.1.1] - 2026-04-29

- IMP-30: Unified Engine `_playerCount` semantics — roster source-of-truth consolidated to `_activePlayerIds.Count`. Replaced `OnPlayerCountChanged` with `OnPlayerJoinedNotification`.
- IMP-31: Split Spectator RoomRouter capacity gate — separated into two layers: an absolute upper bound (DoS protection, `MaxPlayersPerRoom + MaxSpectatorsPerRoom`) and the spectator slot gate (`HandleSpectatorJoin`), resolving the regression where spectators were blocked once 4 players filled the room.
- Added `SessionConfig.MaxSpectators` + wired into `BrawlerDedicatedServer` single-room and multi-room paths (including expanded Listen capacity). Removed the `maxPlayers` CLI argument.

## [0.1.0] - 2026-04-26

- First release
