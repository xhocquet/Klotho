using System;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;
using System.Threading;

using UnityEngine;

using Cysharp.Threading.Tasks;

using xpTURN.Klotho;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.LiteNetLib;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Unity;

using xpTURN.Klotho.Diagnostics;

namespace Brawler
{
    [Serializable]
    public class BrawlerSettings
    {
        [Header("ServerSettings")]
        [SerializeField] public string _hostAddress = "localhost";
        [SerializeField] public int _port = 777;

        [Header("ServerDriven")]
        [SerializeField] public int _roomId = 0;

        [Header("P2P")]
        [SerializeField] public bool _isHost = true;
        [SerializeField] public int _botCount = 0;

        [Header("PlayerSettings")]
        [SerializeField] public int _characterClass = 0; // 0=Warrior, 1=Mage, 2=Rogue, 3=Knight
    }

    /// <summary>
    /// Brawler sample game controller.
    ///
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class BrawlerGameController : MonoBehaviour, IKlothoSessionObserver
    {
        const string KLOTHO_CONNECTION_KEY = "xpTURN.Brawler";

        [Header("Debug")]
        [SerializeField] private KLogLevel _logLevel = KLogLevel.Information;

        [Header("Settings")]
        [SerializeField] private BrawlerSettings _brawlerSettings = new BrawlerSettings();
        [SerializeField] private USimulationConfig _simulationConfig;
        [SerializeField] private USessionConfig _sessionConfig;

        [Header("Scene References")]
        [SerializeField] private GameMenu _gameMenu;
        [SerializeField] private BrawlerViewSync _viewSync;

        // EVU reference. If the prefab has an EntityView, EVU automatically spawns it.
        // If null in the Inspector, the EVU hook is skipped.
        [SerializeField] private EntityViewUpdater _entityViewUpdater;

        // Drives KlothoSession.Update / Stop teardown through Unity Update lifecycle.
        [SerializeField] private KlothoSessionDriver _sessionDriver;

        // Diagnostic F12 chain-stall hotkey. Surface kept compile-on regardless of KLOTHO_FAULT_INJECTION
        // to keep prefab serialization stable; Attach is the only call gated by the define.
        [SerializeField] private xpTURN.Klotho.Diagnostics.FaultInjectionHotkeyDriver _faultInjectionHotkey;

        [Header("Static Colliders")]
        [SerializeField] private TextAsset _staticCollidersAsset;

        [Header("NavMesh")]
        [SerializeField] private TextAsset _navMeshAsset;

        [Header("DataAssets")]
        [SerializeField] private TextAsset _dataAsset;

        private IKLogger _logger;
        List<FPStaticCollider> _staticColliders;
        FPNavMesh _navMesh;
        List<IDataAsset> _dataAssets;
        private IDataAssetRegistry _assetRegistry;

        private KlothoSession _session;
        private KlothoSessionFlow _flow;
        private LiteNetLibTransport _transport;
        private Camera _mainCamera;
        private CancellationTokenSource _connectCts;  // For canceling JoinGameAsync / StartSpectatorAsync
        private IReconnectCredentialsStore _credentialsStore;
        private IKlothoModeStrategy _modeStrategy;

        private BrawlerInputCapture _input;
        private BrawlerSimulationCallbacks _simCallbacks;
        private BrawlerViewCallbacks _viewCallbacks;

        private string _replayPath = Application.dataPath + "/../Replays/brawler.rply";

        public bool IsHost => _brawlerSettings._isHost;
        public KlothoState State => _session?.State ?? KlothoState.Idle;
        public int CurrentTick => _session?.Engine?.CurrentTick ?? 0;
        public int Players => _session?.PlayerCount ?? 0;
        public int Entities => _session?.Simulation?.Frame.Entities.Count ?? 0;
        public bool AllPlayersReady => _session?.NetworkService?.AllPlayersReady ?? false;
        public SessionPhase Phase => _session?.NetworkService?.Phase ?? SessionPhase.None;

        private void CreateLogger()
        {
            _logger = KlothoLogger.CreateDefault(
                level: _logLevel,
                filePrefix: "Client",
                categoryName: "Client");
            _logger?.KInformation($"Brawler logging started!");
        }

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            CreateLogger();

            // Driver hooks wired here so they are live even when Start() returns early via cold-start reconnect.
            _sessionDriver.PreSessionUpdate += OnPreSessionUpdate;
            _sessionDriver.Stopping         += OnSessionDriverStopping;
            _sessionDriver.IdlePoll         += OnSessionDriverIdlePoll;
        }

        private void OnPreSessionUpdate(KlothoSession session, float dt)
        {
            if (session.State == KlothoState.Running)
            {
                _input.CaptureInput();
                _input.AimDirection = GetFacingAimDirection();
            }
        }

        private void OnSessionDriverIdlePoll() => _transport?.PollEvents();

        private void OnSessionDriverStopping(KlothoSession session)
        {
            session.StateChanged           -= OnSessionStateChanged;
            session.PhaseChanged           -= OnSessionPhaseChanged;
            session.PlayerCountChanged     -= OnSessionPlayerCountChanged;
            session.AllPlayersReadyChanged -= OnSessionAllPlayersReadyChanged;

            // Diagnostic hotkey detach first — null-out only, no exception surface.
            // Kept synchronous inside this handler so it always runs whether or not later
            // cleanup throws.
            _faultInjectionHotkey?.Detach();

            // Cleanup that requires Engine to be alive — fires before session.Stop.
            _viewSync.OnLocalCharacterSpawned   -= OnLocalCharacterSpawned;
            _viewSync.OnLocalCharacterDespawned -= OnLocalCharacterDespawned;
            _viewSync.Cleanup();
            _entityViewUpdater?.Cleanup();
            // engine.SaveReplayToFile is called from StopGame body after DetachAndStop returns,
            // preserving the original Engine.Stop -> SaveReplayToFile order across both paths.
        }

        private void Start()
        {
            // Pre-load data
            _staticColliders = FPStaticColliderSerializer.Load(_staticCollidersAsset.bytes);
            _navMesh = FPNavMeshSerializer.Deserialize(_navMeshAsset.bytes);
            _dataAssets = DataAssetReader.LoadMixedCollectionFromBytes(_dataAsset.bytes);

            IDataAssetRegistryBuilder registryBuilder = new DataAssetRegistry();
            registryBuilder.RegisterRange(_dataAssets);
            _assetRegistry = registryBuilder.Build();

            _mainCamera = Camera.main;

            _credentialsStore = new PlayerPrefsReconnectCredentialsStore();

            var logLevels = new LiteNetLib.NetLogLevel[] { LiteNetLib.NetLogLevel.Warning, LiteNetLib.NetLogLevel.Error };
            _transport = new LiteNetLibTransport(_logger, logLevels, connectionKey: KLOTHO_CONNECTION_KEY);
            _transport.OnDisconnected += OnDisconnected;

            _input = new BrawlerInputCapture();
            _input.Enable();

            _modeStrategy = KlothoModeStrategy.Resolve(_simulationConfig);
            _brawlerSettings._roomId = _modeStrategy.NormalizeRoomId(_brawlerSettings._roomId);

            _flow = new KlothoSessionFlow(new KlothoFlowSetup
            {
                Logger            = _logger,
                Transport         = _transport,
                AssetRegistry     = _assetRegistry,
                CredentialsStore  = _credentialsStore,
                AppVersion        = Application.version,
                DeviceIdProvider  = new UnityDeviceIdProvider(),
                LifecycleObserver = this,
                CallbacksFactory  = BuildCallbacks,
                InitialPlayerConfigFactory = () => new BrawlerPlayerConfig
                {
                    SelectedCharacterClass = _brawlerSettings._characterClass,
                },
                SpectatorTransportFactory = () => new LiteNetLibTransport(_logger, connectionKey: KLOTHO_CONNECTION_KEY),
            });
            _flow.OnSessionCreated          += OnAnyFlowSessionCreated;
            _flow.OnHostSessionCreated      += OnHostOrGuestSessionCreated;
            _flow.OnGuestSessionCreated     += OnHostOrGuestSessionCreated;
            _flow.OnReplaySessionCreated    += OnReplayOrSpectatorSessionCreated;
            _flow.OnSpectatorSessionCreated += OnReplayOrSpectatorSessionCreated;

            // Populate static fault-injection schedule before any path can short-return.
            // OnHostOrGuestSessionCreated.AttachToSession reads this state, so it must be loaded
            // regardless of which entry path (cold-start reconnect / host / guest) runs.
            ApplyFaultInjection();

            _gameMenu.IsHost = IsHost;
            // AutoReconnect eligibility — strategy-decided.
            // P2P host is excluded (host death ends the session); SD client / P2P guest are eligible.
            if (_modeStrategy.IsReconnectEligible(_brawlerSettings._isHost))
            {
                _connectCts = new CancellationTokenSource();
                bool started;
                try
                {
                    started = KlothoAutoReconnect.TryStart(
                        _credentialsStore,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Application.version,
                        ct => ReconnectAsync(ct).Forget(),
                        _connectCts.Token);
                }
                catch
                {
                    _connectCts.Dispose();
                    _connectCts = null;
                    throw;
                }
                if (started)
                {
                    _gameMenu.SetActionType(GameMenu.ActionType.Reconnect);
                    return;
                }
                _connectCts.Dispose();
                _connectCts = null;
            }
            _gameMenu.SetActionType(_brawlerSettings._isHost ? GameMenu.ActionType.CreateRoom : GameMenu.ActionType.JoinRoom);
        }

        private void ApplyFaultInjection()
        {
            var path = System.IO.Path.Combine(Application.streamingAssetsPath, "faultinjectionconfig.json");
            FaultInjectionLoader.TryLoadAndApply(path, _logger);
        }

        private void OnEnable()
        {
            _gameMenu.IpAddress = _brawlerSettings._hostAddress;

            _gameMenu._btnHost.onClick.AddListener(OnBtnHost);
            _gameMenu._btnGuest.onClick.AddListener(OnBtnGuest);
            _gameMenu._btnAction.onClick.AddListener(OnBtnAction);
            _gameMenu._btnReplay.onClick.AddListener(StartReplay);
            _gameMenu._btnSpectator.onClick.AddListener(StartSpectator);
            _gameMenu.IpAddressInput.onValueChanged.AddListener(OnIpAddressInputChanged);
        }

        private void OnDisable()
        {
            _gameMenu._btnHost.onClick.RemoveListener(OnBtnHost);
            _gameMenu._btnGuest.onClick.RemoveListener(OnBtnGuest);
            _gameMenu._btnAction.onClick.RemoveListener(OnBtnAction);
            _gameMenu._btnReplay.onClick.RemoveListener(StartReplay);
            _gameMenu._btnSpectator.onClick.RemoveListener(StartSpectator);
            _gameMenu.IpAddressInput.onValueChanged.RemoveListener(OnIpAddressInputChanged);
        }

        // Single-shot guard — both OnDestroy and OnApplicationQuit fire on normal app exit;
        // the second invocation must be a no-op so _transport.Disconnect / _input.Dispose are
        // not invoked twice on the same instance.
        private bool _teardownInvoked;

        private void OnDestroy() => TeardownAll();
        private void OnApplicationQuit() => TeardownAll();

        private void TeardownAll()
        {
            if (_teardownInvoked) return;
            _teardownInvoked = true;

            // Order matters:
            //  1. DetachAndStop fires the Stopping hook while subscriptions are still live
            //     (Driver.OnDestroy may run first — DetachAndStop is idempotent via Session==null).
            //  2. Unsubscribe Driver / Flow events afterward to block post-teardown firing.
            //  3. Cancel async work, disconnect transport, dispose input — terminal cleanup.
            // Process-exit teardown — preserve persisted Reconnect credentials so a relaunch can Reconnect.
            _sessionDriver?.DetachAndStop(keepReconnectCredentials: true);

            if (_sessionDriver != null)
            {
                _sessionDriver.PreSessionUpdate -= OnPreSessionUpdate;
                _sessionDriver.Stopping        -= OnSessionDriverStopping;
                _sessionDriver.IdlePoll        -= OnSessionDriverIdlePoll;
            }

            if (_flow != null)
            {
                _flow.OnSessionCreated          -= OnAnyFlowSessionCreated;
                _flow.OnHostSessionCreated      -= OnHostOrGuestSessionCreated;
                _flow.OnGuestSessionCreated     -= OnHostOrGuestSessionCreated;
                _flow.OnReplaySessionCreated    -= OnReplayOrSpectatorSessionCreated;
                _flow.OnSpectatorSessionCreated -= OnReplayOrSpectatorSessionCreated;
                _flow = null;
            }

            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = null;
            _transport?.Disconnect();
            _input?.Dispose();
        }

        // ────────────────────────────────────────────
        // Game flow
        // ────────────────────────────────────────────

        private void OnBtnHost()
        {
            // Host button is meaningful only when the mode supports a local host affordance.
            if (!_modeStrategy.SupportsLocalHost) return;

            _brawlerSettings._isHost = true;
            _gameMenu.IsHost = true;
            if (_gameMenu.CurrentAction == GameMenu.ActionType.Reconnect)
            {
                // Cancel in-flight + clear credentials. SetActionType is left to FallbackToInitial (race-safe).
                _connectCts?.Cancel();
                _credentialsStore.Clear();
                return;
            }
            if (_gameMenu.CurrentAction == GameMenu.ActionType.JoinRoom)
                _gameMenu.SetActionType(GameMenu.ActionType.CreateRoom);
        }

        private void OnBtnGuest()
        {
            // The Host/Guest toggle is meaningful only when the mode exposes a local host
            // affordance to flip away from. SD mode has no local host, so the toggle is a no-op.
            if (!_modeStrategy.SupportsLocalHost) return;

            _brawlerSettings._isHost = false;
            _gameMenu.IsHost = false;
            if (_gameMenu.CurrentAction == GameMenu.ActionType.Reconnect)
            {
                _connectCts?.Cancel();
                _credentialsStore.Clear();
                return;
            }
            if (_gameMenu.CurrentAction == GameMenu.ActionType.CreateRoom)
                _gameMenu.SetActionType(GameMenu.ActionType.JoinRoom);
        }

        private void OnBtnAction()
        {
            switch(_gameMenu.CurrentAction)
            {
            case GameMenu.ActionType.CreateRoom:
                StartHost();
                break;
            case GameMenu.ActionType.JoinRoom:
                JoinGame();
                // Both P2P / SD are async — transition to Ready when JoinGameAsync completes
                break;
            case GameMenu.ActionType.Ready:
                SetReady();
                break;
            case GameMenu.ActionType.Playing:
                StopGame();
                break;
            case GameMenu.ActionType.Reconnect:
                // Cancel — credentials kept; ReconnectAsync.catch (OperationCanceledException) → FallbackToInitial.
                _connectCts?.Cancel();
                break;
            }
        }

        private void OnDisconnected(DisconnectReason _)
        {
            // While Playing, NetworkService will attempt automatic reconnection, so do not end the game
            if (Phase == SessionPhase.Playing)
                return;

            StopGame();
        }

        private void StartHost()
        {
            if (_sessionConfig == null)
            {
                _logger?.KError($"[Brawler] SessionConfig is required for host");
                _gameMenu.SetActionType(GameMenu.ActionType.CreateRoom);
                return;
            }

            _logger?.KInformation($"[Brawler] Hosting game");

            // StartHost requires a mode that supports a local host. Reject incompatible Inspector
            // setting up front rather than silently mutating the USimulationConfig ScriptableObject
            // (Editor play mode persists such mutations back to the .asset file, corrupting the
            // user's saved setting).
            ISimulationConfig simulationConfig;
            if (_simulationConfig != null)
            {
                if (!_modeStrategy.SupportsLocalHost)
                {
                    _logger?.KError($"[Brawler] StartHost requires SimulationConfig.Mode = P2P (got {_modeStrategy.Mode}) — set Mode = P2P in the Inspector or use a separate P2P-host SimulationConfig asset");
                    _gameMenu.SetActionType(GameMenu.ActionType.CreateRoom);
                    return;
                }
                simulationConfig = _simulationConfig;
            }
            else
            {
                var sc = new SimulationConfig();
                sc.Mode = NetworkMode.P2P;
                simulationConfig = sc;
            }

            _session = _flow.StartHostAndListen(simulationConfig, _sessionConfig, "Game",
                _brawlerSettings._hostAddress, _brawlerSettings._port);
            if (_session == null)
            {
                // listen bind failed — the framework already tore the session down via OnSessionStopped
                // (StopGame), which restored the menu. Just abort.
                _logger?.KError($"[Brawler] Failed to host on port {_brawlerSettings._port} — aborting StartHost.");
                return;
            }

            // Broadcast the local player's character selection via PlayerConfig
            _session.SendPlayerConfig(new BrawlerPlayerConfig
            {
                SelectedCharacterClass = _brawlerSettings._characterClass,
            });

            _gameMenu.SetActionType(GameMenu.ActionType.Ready);
        }

        private void JoinGame()
        {
            // Both P2P / SD (single / multi room) use the async path through KlothoConnection
            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = new CancellationTokenSource();
            JoinGameAsync(_connectCts.Token).Forget();
        }

        private async UniTaskVoid JoinGameAsync(CancellationToken ct)
        {
            _logger?.KInformation($"[Brawler] Joining game");
            _gameMenu.ReconnectStatus = "Connecting...";

            try
            {
                _session = _modeStrategy.Mode switch
                {
                    NetworkMode.ServerDriven => await _flow.JoinServerDrivenAsync(
                        _transport, _brawlerSettings._hostAddress, _brawlerSettings._port,
                        _brawlerSettings._roomId, _sessionConfig, ct),
                    _ => await _flow.JoinP2PAsync(
                        _transport, _brawlerSettings._hostAddress, _brawlerSettings._port,
                        _sessionConfig, ct),
                };

                _gameMenu.ReconnectStatus = null;
                _gameMenu.SetActionType(GameMenu.ActionType.Ready);
            }
            catch (OperationCanceledException)
            {
                _logger?.KWarning($"[Brawler] Join canceled");
                _gameMenu.ReconnectStatus = null;
                _gameMenu.SetActionType(GameMenu.ActionType.JoinRoom);
                _transport?.Disconnect();
            }
            catch (Exception e)
            {
                _logger?.KError(e, $"[Brawler] JoinGame failed");
                _gameMenu.ReconnectStatus = null;
                _gameMenu.SetActionType(GameMenu.ActionType.JoinRoom);
                _transport?.Disconnect();
            }
        }

        private async UniTaskVoid ReconnectAsync(CancellationToken ct)
        {
            _logger?.KInformation($"[Brawler] Cold-start reconnect");
            _gameMenu.ReconnectStatus = "Reconnecting...";

            try
            {
                var creds = _credentialsStore.Load();
                _session = await _flow.ReconnectAsync(_transport, creds, _sessionConfig, ct);

                _gameMenu.ReconnectStatus = null;
                // ActionType transition is delegated to OnLateJoinActive (catchup completion callback).
                // The "Cancel" label remains visible during catchup; OnLateJoinActive switches to Playing.
            }
            catch (OperationCanceledException)
            {
                // Cancel keeps credentials — next boot can auto-retry.
                _logger?.KWarning($"[Brawler] Reconnect canceled");
                _gameMenu.ReconnectStatus = null;
                FallbackToInitial();
            }
            catch (ReconnectFailedException e)
            {
                _logger?.KError(e, $"[Brawler] Reconnect rejected: {ReconnectRejectReason.ToName(e.Reason)}");
                HandleReconnectFailure(e.Reason);
            }
            catch (Exception e)
            {
                // Fallback — transport / serialization / unexpected failure.
                _logger?.KError(e, $"[Brawler] Reconnect attempt failed (non-rejected)");
                HandleReconnectFailure(ReconnectRejectReason.Unknown);
            }
        }

        private void SetReady()
        {
            _logger?.KInformation($"[Brawler] Ready");
            _session?.SetReady(true);

            _gameMenu.SetActionType(GameMenu.ActionType.Playing);
        }

        private void StartReplay()
        {
            if (Phase != SessionPhase.None && Phase != SessionPhase.Disconnected)
                return;

            _logger?.KInformation($"[Brawler] Replay started");

            try
            {
                _session = _flow.StartReplayFromFile(_replayPath);
                _gameMenu.SetActionType(GameMenu.ActionType.Playing);
            }
            catch (xpTURN.Klotho.Replay.ReplayLoadException e)
            {
                _logger?.KError(e, $"[Brawler] Replay load failed: {_replayPath}");
            }
        }

        // ── Spectator entry ──
        //
        // StartSpectator delegates to _flow.SpectateAsync — framework handles the
        // SpectatorService / two-Config await / Engine/Simulation construction internally.
        // The game side supplies a CallbacksFactory (BuildCallbacks) that fires after
        // SpectatorAcceptMessage delivers server-authoritative SimulationConfig + SessionConfig.
        private void StartSpectator()
        {
            if (Phase != SessionPhase.None && Phase != SessionPhase.Disconnected)
                return;

            _logger?.KInformation($"[Brawler] Spectator connecting to {_brawlerSettings._hostAddress}:{_brawlerSettings._port}");

            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = new CancellationTokenSource();
            StartSpectatorAsync(_connectCts.Token).Forget();
        }

        private async UniTaskVoid StartSpectatorAsync(CancellationToken ct)
        {
            try
            {
                _session = await _flow.SpectateAsync(
                    _brawlerSettings._hostAddress, _brawlerSettings._port,
                    _brawlerSettings._roomId, ct);

                _gameMenu.SetActionType(GameMenu.ActionType.Playing);
            }
            catch (OperationCanceledException)
            {
                _logger?.KWarning($"[Brawler] Spectator canceled");
                _gameMenu.SetActionType(GameMenu.ActionType.JoinRoom);
            }
            catch (Exception e)
            {
                _logger?.KError(e, $"[Brawler] Spectator connect failed");
                _gameMenu.SetActionType(GameMenu.ActionType.JoinRoom);
            }
        }

        private SessionCallbacks BuildCallbacks(ISimulationConfig simCfg, ISessionConfig sessionCfg)
        {
            int maxPlayers = sessionCfg?.MaxPlayers ?? InitialMaxPlayersGuess();
            _simCallbacks = new BrawlerSimulationCallbacks(
                _input, _staticColliders, _navMesh,
                maxPlayers, _brawlerSettings._botCount);
            _viewCallbacks = new BrawlerViewCallbacks(_simCallbacks);
            return new SessionCallbacks(_simCallbacks, _viewCallbacks);
        }

        // Common prefix — runs before any mode-specific callback.
        private void OnAnyFlowSessionCreated(KlothoSession session)
        {
            _sessionDriver.Attach(session);

            session.StateChanged           += OnSessionStateChanged;
            session.PhaseChanged           += OnSessionPhaseChanged;
            session.PlayerCountChanged     += OnSessionPlayerCountChanged;
            session.AllPlayersReadyChanged += OnSessionAllPlayersReadyChanged;

            // Initial push — events fire only on transition, so seed GameMenu from current state.
            _gameMenu.State      = session.State;
            _gameMenu.Phase      = session.NetworkService?.Phase ?? SessionPhase.None;
            _gameMenu.Players    = session.PlayerCount;
            _gameMenu.IsAllReady = session.NetworkService?.AllPlayersReady ?? false;
        }

        // Host/Guest path — FI attach → InitializeViewSync.
        private void OnHostOrGuestSessionCreated(KlothoSession session)
        {
            // roleLabel is strategy-decided, not _isHost-decided. SD mode has no local host
            // affordance (SupportsLocalHost == false) — stale Inspector _isHost = true must not
            // leak into the diagnostic label.
            string roleLabel = _modeStrategy.SupportsLocalHost && IsHost ? "host" : "guest";
            xpTURN.Klotho.Diagnostics.FaultInjectionRuntime.AttachToSession(
                session, _transport, _logger,
                roleLabel,
                ct => { _ = ReconnectAsync(ct); },
                _sessionDriver);
            _faultInjectionHotkey?.Attach(session, _logger);

            InitializeViewSync(session.Engine, session.Simulation);
        }

        // Replay/Spectator path — skip FaultInjection (main _transport is idle for these modes),
        // still call InitializeViewSync.
        private void OnReplayOrSpectatorSessionCreated(KlothoSession session)
        {
            InitializeViewSync(session.Engine, session.Simulation);
        }

        private void InitializeViewSync(IKlothoEngine engine, EcsSimulation simulation)
        {
            // EVU.Initialize creates a fresh PlayerViewRegistry — must run before ViewSync.Initialize
            // so the registry is non-null when ViewSync subscribes to its events.
            // Must be called after engine.Start / StartSpectator / StartReplay has completed.
            _entityViewUpdater?.Initialize(engine);

            _viewSync.Initialize(engine, simulation, _entityViewUpdater);
            _viewSync.OnLocalCharacterSpawned += OnLocalCharacterSpawned;
            _viewSync.OnLocalCharacterDespawned += OnLocalCharacterDespawned;
        }

        private void OnLocalCharacterSpawned()
        {
            _logger?.KInformation($"[Brawler] Local Character Spawned");
        }

        private void OnLocalCharacterDespawned()
        {
            _logger?.KInformation($"[Brawler] Local Character Despawned");
        }

        private void StopGame()
        {
            // Re-entry guard: driver.IsStopping indicates DetachAndStop is in flight.
            // Path 2 (external session.Stop without DetachAndStop) has driver.IsStopping == false,
            // so StopGame executes normally and drives DetachAndStop itself.
            if (_sessionDriver != null && _sessionDriver.IsStopping) return;

            _logger?.KInformation($"[Brawler] Game stopped");

            // Cancel any in-progress JoinGameAsync / StartSpectatorAsync
            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = null;

            // Capture engine reference before DetachAndStop (which sets Driver.Session = null after session.Stop).
            var engine = _session?.Engine;

            _gameMenu.ReconnectStatus = null;
            _gameMenu.SetActionType(_brawlerSettings._isHost ? GameMenu.ActionType.CreateRoom : GameMenu.ActionType.JoinRoom);

            // Driver fires Stopping hook (ViewSync/EVU cleanup) → session.Stop (Engine.Stop) → OnSessionStopped chain.
            _sessionDriver.DetachAndStop();

            // SaveReplayToFile runs after Engine.Stop — preserving the original order.
            // Path 2 (external session.Stop → OnSessionStopped → StopGame) also passes through this same location.
            // Do not save during replay playback — playback does not record commands, so overwriting could corrupt the original file
            if (engine != null && !engine.IsReplayMode)
                engine.SaveReplayToFile(_replayPath, true);

            _session = null;

            _transport?.Disconnect();
        }

        // ────────────────────────────────────────────
        // Reconnection
        // ────────────────────────────────────────────

        // Generalized fallback used by Cancel / failure / mode-toggle paths.
        // Same pattern as OnDisconnected — pick CreateRoom or JoinRoom by current _isHost.
        private void FallbackToInitial()
        {
            _gameMenu.SetActionType(_brawlerSettings._isHost
                ? GameMenu.ActionType.CreateRoom
                : GameMenu.ActionType.JoinRoom);
            _transport?.Disconnect();
        }

        private void HandleReconnectFailure(byte reason)
        {
            _credentialsStore.Clear();

            if (reason == ReconnectRejectReason.AlreadyConnected)
            {
                _logger?.KWarning($"[Brawler] Reconnect rejected: AlreadyConnected — another device holds this PlayerId");
            }

            _gameMenu.ReconnectStatus = ReconnectRejectReason.ToDefaultMessage(reason);
            FallbackToInitial();
        }

        public void OnPlayerDisconnected(IPlayerInfo player)
        {
            _logger?.KWarning($"[Brawler] Player {player.PlayerId} disconnected, waiting for reconnection...");
            _gameMenu.ReconnectStatus = $"P{player.PlayerId} disconnected";
        }

        public void OnPlayerReconnected(IPlayerInfo player)
        {
            _logger?.KInformation($"[Brawler] Player {player.PlayerId} reconnected");
            _gameMenu.ReconnectStatus = null;
        }

        public void OnReconnecting()
        {
            // Suppress reconnect UX when match-end is in progress — host disconnect after match end is not a network error.
            if (_session?.Engine?.IsMatchEnded == true) return;

            _logger?.KWarning($"[Brawler] Disconnected, reconnecting...");
            _gameMenu.ReconnectStatus = "Reconnecting...";
        }

        public void OnReconnectFailed(byte reason)
        {
            _logger?.KError($"[Brawler] Reconnection failed: {ReconnectRejectReason.ToName(reason)}");
            _gameMenu.ReconnectStatus = null;
            StopGame();
        }

        public void OnMatchAborted(AbortReason reason)
        {
            _logger?.KWarning($"[Brawler] Match aborted: {reason}");
            _gameMenu.ReconnectStatus = reason switch
            {
                AbortReason.ChainStallTimeout => "Match ended: communication timeout",
                AbortReason.StateDivergence => "Match ended: state divergence",
                AbortReason.ReconnectFailed => "Match ended: reconnection failed",
                _ => "Match ended",
            };
            StopGame();
        }

        public void OnMatchEnded(int tick, IMatchEndEvent endEvt)
        {
            _logger?.KInformation(
                $"[Brawler] Match ended: tick={tick}, winner={endEvt.WinnerPlayerId}, reason={endEvt.Reason}");

            // KlothoSession path: scheduler runs inside Session.Update (B-4 lift) — game side no-op.
            // Spectator path: same Session-internal scheduler also fires (B-3 lift).
        }

        public void OnMatchReset(ResetReason reason)
        {
            _logger?.KWarning($"[Brawler] Match reset: {reason} — state recovered, match continues");
            _gameMenu.ReconnectStatus = "State recovered — match continues";
        }

        public void OnReconnected()
        {
            _logger?.KInformation($"[Brawler] Reconnected successfully");
            _gameMenu.ReconnectStatus = null;
        }

        private void OnLateJoinActive()
        {
            _gameMenu.SetActionType(GameMenu.ActionType.Playing);
        }

        // Explicit forwarder — KlothoEngine.OnCatchupComplete event → existing sample handler name.
        // Avoids renaming the sample's semantic handler while satisfying IKlothoSessionObserver.
        void IKlothoSessionObserver.OnCatchupComplete() => OnLateJoinActive();

        // Fires at the end of KlothoSession.Stop() — trigger game-side teardown.
        // Path 1 (StopGame → DetachAndStop → session.Stop) re-enters this; the StopGame guard
        // (driver.IsStopping == true at this point) blocks the inner StopGame call.
        // Path 2 (external session.Stop) enters this first; StopGame body executes normally.
        public void OnSessionStopped() => StopGame();

        public void OnResyncCompleted(int tick)
        {
            _logger?.KInformation($"[Brawler] Resync completed at tick={tick}");
        }

        // Local MaxPlayers guess for non-host paths where the server-authoritative SessionConfig
        // has not yet been received. The session is reseeded by GameStartMessage /
        // ReconnectAcceptMessage / FullState restore shortly after — this value only sizes
        // BrawlerSimulationCallbacks prior to that. Default 4 matches SessionConfig.MaxPlayers.
        private int InitialMaxPlayersGuess() => _sessionConfig != null ? _sessionConfig.MaxPlayers : 4;

        // ────────────────────────────────────────────
        // Input
        // ────────────────────────────────────────────

        private FPVector2 GetFacingAimDirection()
        {
            // Direction the character is facing (based on TransformComponent.Rotation)
            // Since Rotation = Atan2(aimDir.x, aimDir.y), invert: sin(rot)=x, cos(rot)=y
            var frame = _session?.Simulation?.Frame;
            if (frame != null)
            {
                int localId = _session.Engine.LocalPlayerId;
                var filter = frame.Filter<TransformComponent, OwnerComponent>();
                while (filter.Next(out var entity))
                {
                    ref readonly var owner = ref frame.GetReadOnly<OwnerComponent>(entity);
                    if (owner.OwnerId != localId) continue;
                    ref readonly var tr = ref frame.GetReadOnly<TransformComponent>(entity);
                    FP64 rot = tr.Rotation;
                    return new FPVector2(FP64.Sin(rot), FP64.Cos(rot));
                }
            }
            return FPVector2.Right;
        }

        // ────────────────────────────────────────────
        // GUI state
        // ────────────────────────────────────────────

        private void OnSessionStateChanged(KlothoState s)    => _gameMenu.State = s;
        private void OnSessionPhaseChanged(SessionPhase p)   => _gameMenu.Phase = p;
        private void OnSessionPlayerCountChanged(int n)      => _gameMenu.Players = n;
        private void OnSessionAllPlayersReadyChanged(bool v) => _gameMenu.IsAllReady = v;

        private void OnIpAddressInputChanged(string addr) => _brawlerSettings._hostAddress = addr;
    }
}
