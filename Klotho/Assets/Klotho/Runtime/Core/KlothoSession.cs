using System;
using Microsoft.Extensions.Logging;
using ZLogger;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Klotho session implementation.
    /// A factory and facade responsible for creating/composing engine core objects.
    /// Operates as pure C# with no MonoBehaviour dependency.
    /// </summary>
    public sealed class KlothoSession : IKlothoSession
    {
        public KlothoEngine Engine { get; private set; }
        public EcsSimulation Simulation { get; private set; }
        public IKlothoNetworkService NetworkService { get; private set; }
        public CommandFactory CommandFactory { get; private set; }

        public int LocalPlayerId => Engine?.LocalPlayerId ?? -1;
        public KlothoState State => Engine?.State ?? KlothoState.Idle;

        /// <summary>True after Stop() has been called. Exposed for external loop guards.</summary>
        public bool IsStopped => _stopped;

        private IKlothoSessionObserver _lifecycleObserver;
        // Auto-shutdown after match-end grace. 0 = not scheduled, otherwise wall-clock target ms.
        private long _clientShutdownEndMs;
        private bool _stopped;
        private ILogger _logger;

        // ── Spectator-mode fields ──
        // Concrete type — SpectatorService API (SetLogger / Initialize / SetEngine / Connect /
        // Disconnect / Update) is not all surfaced on ISpectatorService; direct reference keeps wiring simple.
        private SpectatorService _spectatorService;
        private INetworkTransport _spectatorTransport;
        private bool _isSpectatorMode;

        // Bootstrap-in-progress state — all 5 cleared in FinishSpectatorBootstrap (success) or Stop (cancel).
        private SpectatorSessionSetup _pendingSetup;
        private ISimulationConfig _pendingSimConfig;
        private ISessionConfig _pendingSessionConfig;
        private Action<KlothoSession> _spectatorReadyCallback;
        private Action<Exception> _spectatorFailedCallback;

        private KlothoSession() { }

        public void Update(float deltaTime)
        {
            if (_stopped) return;

            if (_isSpectatorMode)
            {
                // Always pump spectator transport — required during bootstrap (Engine == null) for
                // SpectatorAcceptMessage / FullStateResponse to arrive, and after bootstrap for
                // confirmed-input streaming.
                _spectatorService?.Update();
                if (Engine != null)
                    Engine.Update(deltaTime);
            }
            else
            {
                Engine.Update(deltaTime);
            }

            // Client shutdown grace check (Update-tick driven — main thread safety guaranteed).
            if (_clientShutdownEndMs > 0)
            {
                long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (nowMs >= _clientShutdownEndMs)
                {
                    _clientShutdownEndMs = 0;
                    _logger?.ZLogInformation($"[KlothoSession] Auto-shutdown grace expired — invoking Stop()");
                    Stop();
                }
            }
        }

        public void InputCommand(ICommand command)
        {
            if (_isSpectatorMode)
                throw new InvalidOperationException("InputCommand is not allowed in spectator mode.");
            Engine.InputCommand(command);
        }

        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;

            // Cancel pending client shutdown (no-op if not scheduled).
            _clientShutdownEndMs = 0;

            // Capture observer reference before UnsubscribeLifecycleObserver nulls the field —
            // OnSessionStopped fires AFTER framework cleanup so game can safely tear down.
            var obs = _lifecycleObserver;

            // Unsubscribe scheduler handler before Engine.Stop to avoid late fire during deinit.
            if (Engine != null)
                Engine.OnMatchEnded -= HandleMatchEndedForShutdown;

            // MUST unsubscribe lifecycle observer before Engine.Stop() — Engine deinit may fire
            // cleanup events (OnMatchReset etc.) that observers should not receive after teardown.
            UnsubscribeLifecycleObserver();

            if (_isSpectatorMode)
            {
                // Spectator-mode teardown: Engine may be null if bootstrap never completed.
                Engine?.Stop();
                if (_spectatorService != null)
                {
                    _spectatorService.OnSimulationConfigReceived -= HandleSpectatorSimConfig;
                    _spectatorService.OnSessionConfigReceived -= HandleSpectatorSessionConfig;
                    _spectatorService.OnSpectatorStopped -= HandleSpectatorStopped;
                    // SpectatorService.Disconnect() handles _transport.Disconnect internally —
                    // calling _spectatorTransport.Disconnect again would double-disconnect the same transport.
                    _spectatorService.Disconnect();
                    _spectatorService = null;
                }
                else
                {
                    // Defensive — BeginSpectatorConnect failed before _spectatorService was wired
                    // (e.g., synchronous transport.Connect failure prior to assignment).
                    _spectatorTransport?.Disconnect();
                }

                // Clear bootstrap-incomplete state explicitly so cancel paths do not hold setup /
                // callback references until session GC.
                _pendingSetup = null;
                _pendingSimConfig = null;
                _pendingSessionConfig = null;
                _spectatorReadyCallback = null;
                _spectatorFailedCallback = null;
            }
            else
            {
                Engine.Stop();
                NetworkService.LeaveRoom();
            }

            // Notify game so it can perform its own teardown (transport disconnect, session
            // reference null-out, UI cleanup). Game-side re-entry into Stop() is guarded by
            // the _stopped flag above — idempotent.
            obs?.OnSessionStopped();
        }

        // ── Client shutdown grace scheduler ──

        private void HandleMatchEndedForShutdown(int tick, IMatchEndEvent endEvt)
        {
            if (_stopped) return;
            if (Engine.IsReplayMode) return;
            if (_clientShutdownEndMs > 0) return;

            int graceMs = Engine.SessionConfig.ClientShutdownGraceMs;
            if (graceMs <= 0)
            {
                // Defer Stop to next Update tick — avoid re-entrancy during OnMatchEnded dispatch.
                _clientShutdownEndMs = 1;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                _logger?.ZLogDebug($"[KlothoSession] Auto-shutdown scheduled in 0ms (deferred to next Update tick)");
#endif
                return;
            }

            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _clientShutdownEndMs = nowMs + graceMs;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            _logger?.ZLogDebug($"[KlothoSession] Auto-shutdown scheduled in {graceMs}ms");
#endif
        }

        // ── Lifecycle observer wiring ──

        // internal — exposed for unit tests via InternalsVisibleTo on xpTURN.Klotho.Tests asmdef.
        internal void SubscribeLifecycleObserver(IKlothoSessionObserver obs)
        {
            if (obs == null) return;
            _lifecycleObserver = obs;

            // Spectator mode has no NetworkService — guard so spectator path can reuse this wiring.
            if (NetworkService != null)
            {
                NetworkService.OnPlayerDisconnected += obs.OnPlayerDisconnected;
                NetworkService.OnPlayerReconnected += obs.OnPlayerReconnected;
                NetworkService.OnReconnecting += obs.OnReconnecting;
                NetworkService.OnReconnectFailed += obs.OnReconnectFailed;
                NetworkService.OnReconnected += obs.OnReconnected;
            }

            if (Engine != null)
            {
                Engine.OnCatchupComplete += obs.OnCatchupComplete;
                Engine.OnResyncCompleted += obs.OnResyncCompleted;
                Engine.OnGameStart += obs.OnGameStart;
                Engine.OnMatchAborted += obs.OnMatchAborted;
                Engine.OnMatchEnded += obs.OnMatchEnded;
                Engine.OnMatchReset += obs.OnMatchReset;
            }
        }

        internal void UnsubscribeLifecycleObserver()
        {
            if (_lifecycleObserver == null) return;
            var obs = _lifecycleObserver;
            _lifecycleObserver = null;

            if (NetworkService != null)
            {
                NetworkService.OnPlayerDisconnected -= obs.OnPlayerDisconnected;
                NetworkService.OnPlayerReconnected -= obs.OnPlayerReconnected;
                NetworkService.OnReconnecting -= obs.OnReconnecting;
                NetworkService.OnReconnectFailed -= obs.OnReconnectFailed;
                NetworkService.OnReconnected -= obs.OnReconnected;
            }
            if (Engine != null)
            {
                Engine.OnCatchupComplete -= obs.OnCatchupComplete;
                Engine.OnResyncCompleted -= obs.OnResyncCompleted;
                Engine.OnGameStart -= obs.OnGameStart;
                Engine.OnMatchAborted -= obs.OnMatchAborted;
                Engine.OnMatchEnded -= obs.OnMatchEnded;
                Engine.OnMatchReset -= obs.OnMatchReset;
            }
        }

        // ── Factory ──

        /// <summary>
        /// Create a session (new Config-tier API).
        /// </summary>
        public static KlothoSession Create(KlothoSessionSetup setup)
        {
            bool isGuest = setup.Connection != null;
            var simConfig = isGuest
                ? setup.Connection.SimulationConfig
                : setup.SimulationConfig;
            var transport = isGuest
                ? setup.Connection.Transport
                : setup.Transport;

            // 1. Create EcsSimulation
            var simulation = new EcsSimulation(
                simConfig.MaxEntities,
                simConfig.MaxRollbackTicks,
                simConfig.TickIntervalMs,
                setup.Logger,
                assetRegistry: setup.AssetRegistry);

            // 2. Register systems via callback
            setup.SimulationCallbacks?.RegisterSystems(simulation);
            simulation.LockAssetRegistry();

            // 3. Create CommandFactory
            var commandFactory = new CommandFactory();

            // 4. Create SessionConfig
            // Guest: RandomSeed stays 0 because it is overwritten when GameStartMessage is received
            // Host: if 0, auto-generated from TickCount
            // Guest Late Join: overwritten with LateJoinAcceptMessage fields (replaces the GameStartMessage path)
            // Guest cold-start Reconnect: overwritten with ReconnectAcceptMessage fields
            JoinKind joinKind = isGuest ? setup.Connection.Kind : JoinKind.Normal;
            bool isLateJoin = (joinKind == JoinKind.LateJoin);
            bool isReconnect = (joinKind == JoinKind.Reconnect);
            SessionConfig sessionConfig;
            if (isLateJoin)
            {
                var accept = setup.Connection.LateJoinPayload.AcceptMessage;
                int clampedMinPlayers = System.Math.Clamp(accept.MinPlayers, 1, accept.MaxPlayers);
                if (clampedMinPlayers != accept.MinPlayers)
                {
                    setup.Logger?.ZLogWarning($"[KlothoSession] MinPlayers clamped (LateJoin): {accept.MinPlayers} -> {clampedMinPlayers} (range: 1..{accept.MaxPlayers})");
                }
                sessionConfig = new SessionConfig
                {
                    RandomSeed = accept.RandomSeed,
                    MaxPlayers = accept.MaxPlayers,
                    MinPlayers = clampedMinPlayers,
                    MaxSpectators = accept.MaxSpectators,
                    AllowLateJoin = accept.AllowLateJoin,
                    LateJoinDelayTicks = accept.LateJoinDelayTicks,
                    ReconnectTimeoutMs = accept.ReconnectTimeoutMs,
                    ReconnectMaxRetries = accept.ReconnectMaxRetries,
                    LateJoinDelaySafety = accept.LateJoinDelaySafety,
                    RttSanityMaxMs = accept.RttSanityMaxMs,
                    MinStallAbortTicks = accept.MinStallAbortTicks,
                    CountdownDurationMs = accept.CountdownDurationMs,
                    AbortGraceMs = accept.AbortGraceMs,
                    EndGracePolicy = (EndGracePolicy)accept.EndGracePolicy,
                    EndGraceMs = accept.EndGraceMs,
                    ClientShutdownGraceMs = accept.ClientShutdownGraceMs,
                };
            }
            else if (isReconnect)
            {
                var accept = setup.Connection.ReconnectPayload.AcceptMessage;
                int clampedMinPlayers = System.Math.Clamp(accept.MinPlayers, 1, accept.MaxPlayers);
                if (clampedMinPlayers != accept.MinPlayers)
                {
                    setup.Logger?.ZLogWarning($"[KlothoSession] MinPlayers clamped (Reconnect): {accept.MinPlayers} -> {clampedMinPlayers} (range: 1..{accept.MaxPlayers})");
                }
                sessionConfig = new SessionConfig
                {
                    RandomSeed = accept.RandomSeed,
                    MaxPlayers = accept.MaxPlayers,
                    MinPlayers = clampedMinPlayers,
                    MaxSpectators = accept.MaxSpectators,
                    AllowLateJoin = accept.AllowLateJoin,
                    LateJoinDelayTicks = accept.LateJoinDelayTicks,
                    ReconnectTimeoutMs = accept.ReconnectTimeoutMs,
                    ReconnectMaxRetries = accept.ReconnectMaxRetries,
                    LateJoinDelaySafety = accept.LateJoinDelaySafety,
                    RttSanityMaxMs = accept.RttSanityMaxMs,
                    MinStallAbortTicks = accept.MinStallAbortTicks,
                    CountdownDurationMs = accept.CountdownDurationMs,
                    AbortGraceMs = accept.AbortGraceMs,
                    EndGracePolicy = (EndGracePolicy)accept.EndGracePolicy,
                    EndGraceMs = accept.EndGraceMs,
                    ClientShutdownGraceMs = accept.ClientShutdownGraceMs,
                };
            }
            else
            {
                var src = setup.SessionConfig ?? new SessionConfig();
                int clampedMinPlayers = System.Math.Clamp(src.MinPlayers, 1, src.MaxPlayers);
                if (clampedMinPlayers != src.MinPlayers)
                {
                    setup.Logger?.ZLogWarning($"[KlothoSession] MinPlayers clamped: {src.MinPlayers} -> {clampedMinPlayers} (range: 1..{src.MaxPlayers})");
                }
                sessionConfig = new SessionConfig
                {
                    RandomSeed = isGuest
                        ? 0
                        : (src.RandomSeed == 0 ? System.Environment.TickCount : src.RandomSeed),
                    MaxPlayers = src.MaxPlayers,
                    MinPlayers = clampedMinPlayers,
                    MaxSpectators = src.MaxSpectators,
                    AllowLateJoin = src.AllowLateJoin,
                    LateJoinDelayTicks = src.LateJoinDelayTicks,
                    ReconnectTimeoutMs = src.ReconnectTimeoutMs,
                    ReconnectMaxRetries = src.ReconnectMaxRetries,
                    LateJoinDelaySafety = src.LateJoinDelaySafety,
                    RttSanityMaxMs = src.RttSanityMaxMs,
                    MinStallAbortTicks = src.MinStallAbortTicks,
                    CountdownDurationMs = src.CountdownDurationMs,
                    AbortGraceMs = src.AbortGraceMs,
                    EndGracePolicy = src.EndGracePolicy,
                    EndGraceMs = src.EndGraceMs,
                    ClientShutdownGraceMs = src.ClientShutdownGraceMs,
                };
            }

            // 5. Create + initialize NetworkService — guest (Connection) uses the skip-handshake path
            IKlothoNetworkService networkService;
            if (simConfig.Mode == NetworkMode.ServerDriven)
            {
                var sdService = new ServerDrivenClientService();
                if (isGuest) sdService.InitializeFromConnection(setup.Connection, commandFactory, setup.Logger, setup.RoomId);
                else         sdService.Initialize(transport, commandFactory, setup.Logger);
                networkService = sdService;
            }
            else
            {
                var p2pService = new KlothoNetworkService();
                if (isGuest) p2pService.InitializeFromConnection(setup.Connection, commandFactory, setup.Logger);
                else         p2pService.Initialize(transport, commandFactory, setup.Logger);
                networkService = p2pService;
            }

            // 5.1 Reconnect credentials wire — optional. Both KlothoNetworkService and ServerDrivenClientService
            //     own the SetReconnectCredentialsStore API; route via cast so the game side does not have to
            //     know the concrete network service type.
            if (setup.CredentialsStore != null)
            {
                if (networkService is KlothoNetworkService p2pCreds)
                    p2pCreds.SetReconnectCredentialsStore(setup.CredentialsStore, setup.AppVersion, setup.DeviceIdProvider);
                else if (networkService is ServerDrivenClientService sdCreds)
                    sdCreds.SetReconnectCredentialsStore(setup.CredentialsStore, setup.AppVersion, setup.DeviceIdProvider);
            }

            // 5.5 Late Join / cold-start Reconnect seed — restore _players / _sessionMagic / _randomSeed.
            //     Must be done at this point so the engine.Initialize _activePlayerIds auto-copy loop ([L278-280])
            //     can populate correctly.
            if (isLateJoin)
            {
                if (networkService is ServerDrivenClientService sdClient)
                    sdClient.SeedLateJoinPlayers(setup.Connection.LateJoinPayload);
                else if (networkService is KlothoNetworkService p2pClient)
                    p2pClient.SeedLateJoinPlayers(setup.Connection.LateJoinPayload);
            }
            else if (isReconnect)
            {
                if (networkService is ServerDrivenClientService sdClient)
                    sdClient.SeedReconnectPlayers(setup.Connection.ReconnectPayload);
                else if (networkService is KlothoNetworkService p2pClient)
                    p2pClient.SeedReconnectPlayers(setup.Connection.ReconnectPayload);
            }

            // 6. Create Engine: inject both SimulationConfig and SessionConfig
            var engine = new KlothoEngine(simConfig, sessionConfig);
            engine.Initialize(simulation, networkService, setup.Logger,
                setup.SimulationCallbacks, setup.ViewCallbacks);
            engine.SetCommandFactory(commandFactory);
            if (networkService is KlothoNetworkService p2pNs)
                p2pNs.SubscribeEngine(engine);
            else if (networkService is ServerDrivenClientService sdNs)
                sdNs.SubscribeEngine(engine);

            // 7.5 Late Join injection: restore FullState + start Catchup + seed existing players' PlayerConfig.
            //     Since there is no HandleGameStart path, seed manually at this point.
            //     The extra-delay value from the accept message is applied by SDClientService.SubscribeEngine
            //     (drains a pending value buffered when the handshake handler fired before the engine existed).
            if (isLateJoin)
            {
                engine.SeedLateJoinFullState(setup.Connection.LateJoinPayload);
                SeedLateJoinPlayerConfigs(engine, setup.Connection.LateJoinPayload);
            }
            else if (isReconnect)
            {
                // cold-start Reconnect: FullState restore + Catchup. PlayerConfig is re-broadcast by the host
                // upon reconnect (the existing runtime path), so no PlayerConfig seed array on this message.
                engine.SeedReconnectFullState(setup.Connection.ReconnectPayload);
            }

            var session = new KlothoSession
            {
                Engine = engine,
                Simulation = simulation,
                NetworkService = networkService,
                CommandFactory = commandFactory,
                _logger = setup.Logger,
            };

            session.SubscribeLifecycleObserver(setup.LifecycleObserver);
            session.Engine.OnMatchEnded += session.HandleMatchEndedForShutdown;

            return session;
        }

        /// <summary>
        /// Late Join path PlayerConfig injection.
        /// Sequentially deserializes LateJoinAcceptMessage.PlayerConfigData + PlayerConfigLengths and
        /// calls engine.HandlePlayerConfigReceived(playerId, configMsg). Same pattern as the regular runtime path.
        /// Since MessageSerializer._messageCache reuses singletons by type, HandlePlayerConfigReceived must be invoked
        /// immediately inside the loop (the engine copies/extracts into its internal store) — do not buffer into an intermediate array.
        /// </summary>
        private static void SeedLateJoinPlayerConfigs(KlothoEngine engine, LateJoinPayload payload)
        {
            var msg = payload.AcceptMessage;
            if (msg.PlayerConfigData == null || msg.PlayerConfigData.Length == 0) return;
            if (msg.PlayerConfigLengths == null || msg.PlayerConfigLengths.Count == 0) return;

            var serializer = new MessageSerializer();
            int offset = 0;
            int count = System.Math.Min(msg.PlayerConfigLengths.Count, msg.PlayerIds.Count);
            for (int i = 0; i < count; i++)
            {
                int len = msg.PlayerConfigLengths[i];
                if (len <= 0) continue;

                var configMsg = serializer.Deserialize(msg.PlayerConfigData, len, offset) as PlayerConfigBase;
                if (configMsg != null)
                    engine.HandlePlayerConfigReceived(msg.PlayerIds[i], configMsg);
                offset += len;
            }
        }

        // ── Convenience methods ──

        public void HostGame(string roomName, int maxPlayers)
        {
            NetworkService.CreateRoom(roomName, maxPlayers);
        }

        public void JoinGame(string roomName)
        {
            NetworkService.JoinRoom(roomName);
        }

        public void LeaveRoom()
        {
            NetworkService.LeaveRoom();
        }

        /// <summary>
        /// Sends the local player's PlayerConfig to the host.
        /// Upon receipt, the host broadcasts it to all peers.
        /// </summary>
        public void SendPlayerConfig(PlayerConfigBase playerConfig)
        {
            NetworkService.SendPlayerConfig(LocalPlayerId, playerConfig);
        }

        public void SetReady(bool ready)
        {
            NetworkService.SetReady(ready);
        }

        // ── Spectator factory ──

        /// <summary>
        /// Create a spectator session. Spectator setup is deferred — completes once both
        /// SimulationConfig and SessionConfig arrive from SpectatorAcceptMessage. Engine,
        /// Simulation, and SpectatorService wiring all performed internally. Reports completion
        /// via <paramref name="onReady"/> / <paramref name="onFailed"/>.
        ///
        /// Caller must invoke <see cref="Update"/> every frame on the returned session — it polls
        /// the spectator transport while configs are pending and drives Engine.Update after bootstrap.
        /// </summary>
        public static KlothoSession CreateSpectator(
            SpectatorSessionSetup setup,
            Action<KlothoSession> onReady = null,
            Action<Exception> onFailed = null)
        {
            var session = new KlothoSession
            {
                _logger = setup.Logger,
                _isSpectatorMode = true,
                _spectatorTransport = setup.Transport,
                _spectatorReadyCallback = onReady,
                _spectatorFailedCallback = onFailed,
                _pendingSetup = setup,
            };
            session.BeginSpectatorConnect();
            return session;
        }

        private void BeginSpectatorConnect()
        {
            var commandFactory = new CommandFactory();
            CommandFactory = commandFactory;

            var spectatorService = new SpectatorService();
            spectatorService.SetLogger(_logger);
            spectatorService.Initialize(_spectatorTransport, commandFactory, null, _logger);

            spectatorService.OnSimulationConfigReceived += HandleSpectatorSimConfig;
            spectatorService.OnSessionConfigReceived += HandleSpectatorSessionConfig;
            spectatorService.OnSpectatorStopped += HandleSpectatorStopped;

            _spectatorService = spectatorService;
            spectatorService.Connect(_pendingSetup.HostAddress, _pendingSetup.Port, _pendingSetup.RoomId);
        }

        private void HandleSpectatorSimConfig(ISimulationConfig cfg)
        {
            _pendingSimConfig = cfg;
            TryFinishSpectatorBootstrap();
        }

        private void HandleSpectatorSessionConfig(ISessionConfig cfg)
        {
            _pendingSessionConfig = cfg;
            TryFinishSpectatorBootstrap();
        }

        private void TryFinishSpectatorBootstrap()
        {
            if (_pendingSimConfig == null || _pendingSessionConfig == null) return;
            if (Engine != null) return;   // duplicate-guard
            FinishSpectatorBootstrap(_pendingSimConfig, _pendingSessionConfig);
        }

        private void FinishSpectatorBootstrap(ISimulationConfig simCfg, ISessionConfig sessionCfg)
        {
            // Build Sim/View callbacks against server-authoritative config.
            var callbacks = _pendingSetup.CallbacksFactory(simCfg, sessionCfg);

            var simulation = new EcsSimulation(
                simCfg.MaxEntities, simCfg.MaxRollbackTicks, simCfg.TickIntervalMs,
                _logger, assetRegistry: _pendingSetup.AssetRegistry);
            callbacks.Simulation?.RegisterSystems(simulation);
            simulation.LockAssetRegistry();

            var engine = new KlothoEngine(simCfg, sessionCfg);
            engine.Initialize(simulation, _logger);
            engine.SetCommandFactory(CommandFactory);

            Engine = engine;
            Simulation = simulation;

            _spectatorService.SetEngine(engine);

            _spectatorService.OnSpectatorStarted     += info => engine.StartSpectator(info);
            _spectatorService.OnConfirmedInputReceived += (tick, cmd) => engine.ReceiveConfirmedCommand(cmd);
            _spectatorService.OnTickConfirmed        += tick => engine.ConfirmSpectatorTick(tick);
            _spectatorService.OnFullStateReceived    += (tick, stateData, _, _) =>
            {
                simulation.RestoreFromFullState(stateData);
                engine.ResetToTick(tick);
            };

            // Lifecycle observer subset — SubscribeLifecycleObserver guards on NetworkService != null,
            // so spectator path naturally subscribes only Engine-side events.
            SubscribeLifecycleObserver(_pendingSetup.LifecycleObserver);

            // Reuse client-shutdown scheduler — fires on Engine.OnMatchEnded across all modes.
            engine.OnMatchEnded += HandleMatchEndedForShutdown;

            var readyCallback = _spectatorReadyCallback;
            _pendingSetup = null;
            _pendingSimConfig = null;
            _pendingSessionConfig = null;
            _spectatorReadyCallback = null;
            _spectatorFailedCallback = null;

            readyCallback?.Invoke(this);
        }

        private void HandleSpectatorStopped(string reason)
        {
            if (Engine == null)
            {
                // Pre-bootstrap failure — surface to the CreateSpectator caller.
                var failedCallback = _spectatorFailedCallback;
                _spectatorFailedCallback = null;
                _spectatorReadyCallback = null;
                failedCallback?.Invoke(new Exception($"Spectator stopped before bootstrap: {reason}"));
            }
            else if (!_stopped)
            {
                // Post-bootstrap transport drop — drive framework cleanup through Stop().
                // Stop() invokes the lifecycle observer's OnSessionStopped, letting the game tear
                // down UI / transport references without per-game disconnect detection. Stop() is
                // idempotent (_stopped guard), so re-entry from game-side StopGame() is safe.
                _logger?.ZLogWarning($"[KlothoSession] Spectator transport stopped after bootstrap: {reason} — invoking Stop()");
                Stop();
            }
        }
    }
}
