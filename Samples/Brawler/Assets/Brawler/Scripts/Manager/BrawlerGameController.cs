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
        [field: Header("ServerSettings")]
        [SerializeField] public string _hostAddress = "localhost";
        [SerializeField] public int _port = 777;

        [field: Header("ServerDriven")]
        [SerializeField] public int _roomId = 0;

        [field: Header("P2P")]
        [SerializeField] public bool _isHost = true;
        [SerializeField] public int _botCount = 0;

        [field: Header("PlayerSettings")]
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

        [field: Header("Debug")]
        [SerializeField] private KLogLevel _logLevel = KLogLevel.Information;

        [field: Header("Settings")]
        [SerializeField] private BrawlerSettings _brawlerSettings = new BrawlerSettings();
        [SerializeField] private USimulationConfig _simulationConfig;
        [SerializeField] private USessionConfig _sessionConfig;

        [field: Header("Scene References")]
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

        [field: Header("Static Colliders")]
        [SerializeField] private TextAsset _staticCollidersAsset;

        [field: Header("NavMesh")]
        [SerializeField] private TextAsset _navMeshAsset;

        [field: Header("DataAssets")]
        [SerializeField] private TextAsset _dataAsset;

        private IKLogger _logger;
        List<FPStaticCollider> _staticColliders;
        FPNavMesh _navMesh;
        List<IDataAsset> _dataAssets;
        private IDataAssetRegistry _assetRegistry;

        private IKlothoSession _session;
        private KlothoSessionFlow _flow;
        private LiteNetLibTransport _transport;

#if KLOTHO_FAULT_INJECTION
        // Per-process-unique device id for fault-injection smoke runs: stable across this process's
        // join+reconnect (so the host's credential matching still recognizes it), but distinct
        // between co-located instances (so they don't collide on the shared machine id).
        private sealed class FaultInjectionDeviceIdProvider : IDeviceIdProvider
        {
            private static readonly string _id =
                $"{SystemInfo.deviceUniqueIdentifier}-fi-{Guid.NewGuid():N}";
            public string GetDeviceId() => _id;
        }
#endif

        private Camera _mainCamera;
        private IReconnectCredentialsStore _credentialsStore;
        private IKlothoModeStrategy _modeStrategy;

        // Effective local role, re-derived from the current host preference on every read so a
        // pre-connect Host/Guest toggle is reflected immediately. Valid once _modeStrategy is resolved.
        private KlothoRole Role => _modeStrategy.ResolveRole(_brawlerSettings._isHost);

        private BrawlerInputCapture _input;
        private BrawlerSimulationCallbacks _simCallbacks;
        private BrawlerViewCallbacks _viewCallbacks;

        private string _replayPath = Application.dataPath + "/../Replays/brawler.rply";

        private void CreateLogger()
        {
            _logger = KlothoLogger.CreateDefault(
                level: _logLevel,
                filePrefix: "Client",
                categoryName: "Client");

#if DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR
            CommandPool.SetDiagnosticLogger(_logger);
            EventPool.SetDiagnosticLogger(_logger);
#endif

            _logger?.KInformation($"Brawler logging started : LogLevel={_logLevel}");
        }

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            CreateLogger();

            // Driver per-frame hook wired here so it is live even when Start() returns early via
            // cold-start reconnect. Session lifecycle (create / state / stopping / stopped) is observed
            // through IKlothoSessionObserver, not the driver Stopping event. Idle transport pumping and
            // idle-disconnect routing are owned by the driver (bound via BindTransport in Start).
            _sessionDriver.PreSessionUpdate += OnPreSessionUpdate;
        }

        private void OnPreSessionUpdate(KlothoSession session, float dt)
        {
            if (session.State == KlothoState.Running)
            {
                _input.CaptureInput();
                _input.AimDirection = GetFacingAimDirection();
            }
        }

        // Pre-stop hook — fires inside session.Stop() while Engine is still alive (replaces the
        // KlothoSessionDriver.Stopping subscription). State-event unsubscription is no longer needed:
        // the framework manages observer lifetime, nulling it before Engine.Stop.
        void IKlothoSessionObserver.OnSessionStopping()
        {
            // Diagnostic hotkey detach first — null-out only, no exception surface.
            // Kept synchronous so it always runs whether or not later cleanup throws.
            _faultInjectionHotkey?.Detach();

            // Cleanup that requires Engine to be alive — fires before Engine.Stop.
            _viewSync.OnLocalCharacterSpawned   -= OnLocalCharacterSpawned;
            _viewSync.OnLocalCharacterDespawned -= OnLocalCharacterDespawned;
            _viewSync.Cleanup();
            _entityViewUpdater?.Cleanup();
            // Replay is saved by the framework inside session.Stop (after Engine.Stop) via
            // ConfigureReplaySave — no longer orchestrated here.
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

            _input = new BrawlerInputCapture();
            _input.Enable();

            _modeStrategy = KlothoModeStrategy.Resolve(_simulationConfig);
            _brawlerSettings._roomId = _modeStrategy.NormalizeRoomId(_brawlerSettings._roomId);

            var setup = new KlothoFlowSetupBuilder(BuildCallbacks)
                .WithLogger(_logger)
                .WithTransport(_transport)
                .WithAssetRegistry(_assetRegistry)
                .WithLifecycleObserver(this)
                .WithReplaySave(_replayPath, dumpJson: true)
                .WithUnityDefaults()
#if KLOTHO_FAULT_INJECTION
                // Co-located fault-injection clients share SystemInfo.deviceUniqueIdentifier (same
                // machine), which collides on the host's reconnect credential matching → reconnect
                // rejected. Override with a per-process-unique id (stable across this process's
                // join+reconnect, distinct between instances) so the reconnect smoke is meaningful.
                .WithHandshake(Application.version, new FaultInjectionDeviceIdProvider())
#endif
                .WithReconnect(_credentialsStore)
                .WithAutoPlayerConfig(() => new BrawlerPlayerConfig { SelectedCharacterClass = _brawlerSettings._characterClass })
                .WithSpectator(() => new LiteNetLibTransport(_logger, connectionKey: KLOTHO_CONNECTION_KEY))
                .Build();

            _flow = new KlothoSessionFlow(setup);

            // Hand the main transport to the driver (before any session is created): the driver pumps it
            // while idle and routes idle disconnects to OnIdleDisconnected. Subscribing here keeps the
            // driver ahead of NetworkService so it observes a disconnect's pre-transition Phase.
            _sessionDriver.BindTransport(_transport, this, _flow);

            // Session creation is observed via IKlothoSessionObserver.OnSessionCreated(session, kind) —
            // no per-role Flow event subscription needed.

            // Populate static fault-injection schedule before any path can short-return.
            // OnSessionCreated (host/guest branch) AttachToSession reads this state, so it must be
            // loaded regardless of which entry path (cold-start reconnect / host / guest) runs.
            ApplyFaultInjection();

            _gameMenu.IsHost = Role.IsLocalHost();
            // AutoReconnect eligibility — role-decided.
            // P2P host is excluded (host death ends the session); SD client / P2P guest are eligible.
            if (Role.IsReconnectEligible())
            {
                bool started = KlothoAutoReconnect.TryStart(
                    _credentialsStore,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Application.version,
                    ct => ReconnectAsync(ct).Forget(),
                    destroyCancellationToken);
                if (started)
                {
                    _gameMenu.SetActionType(GameMenu.ActionType.Reconnect);
                    return;
                }
            }
            _gameMenu.SetActionType(Role.IsLocalHost() ? GameMenu.ActionType.CreateRoom : GameMenu.ActionType.JoinRoom);
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
        // the second invocation must be a no-op so _input.Dispose / event unsubscription are
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
            //  2. Unsubscribe the Driver hook afterward to block post-teardown firing.
            //  3. Cancel async work, dispose input — terminal cleanup. The main transport is owned by
            //     the driver and disconnected in its OnDestroy, not here.
            // Process-exit teardown — preserve persisted Reconnect credentials so a relaunch can Reconnect.
            // saveReplay: false — process-exit must not write a replay (matches the prior guarded behavior).
            _sessionDriver?.DetachAndStop(keepReconnectCredentials: true, saveReplay: false);

            if (_sessionDriver != null)
                _sessionDriver.PreSessionUpdate -= OnPreSessionUpdate;

            _flow?.DisposeConnect();
            _flow = null;

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
                // Cancel in-flight + clear credentials. SetActionType is left to the ReconnectAsync
                // OCE catch (→ ResetToInitialUi) so it runs once, race-safe.
                _flow?.CancelConnect();
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
                _flow?.CancelConnect();
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
                // Cancel — credentials kept; ReconnectAsync.catch (OperationCanceledException) → ResetToInitialUi.
                _flow?.CancelConnect();
                break;
            }
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
                // Defensive backstop: the UI routes here only when the resolved role is P2P host,
                // so this guard does not fire in normal flow — it rejects a stale P2P-only config.
                if (Role != KlothoRole.P2PHost)
                {
                    _logger?.KError($"[Brawler] StartHost requires the P2P host role (got {Role}) — set Mode = P2P in the Inspector or use a separate P2P-host SimulationConfig asset");
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
                // listen bind failed — the framework already tore the session down via OnSessionStopped,
                // which restored the menu. Just abort.
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
            JoinGameAsync(destroyCancellationToken).Forget();
        }

        private async UniTaskVoid JoinGameAsync(CancellationToken ct)
        {
            _logger?.KInformation($"[Brawler] Joining game");
            _gameMenu.ReconnectStatus = "Connecting...";

            try
            {
                _session = await _flow.JoinAsync(
                    _modeStrategy, _transport,
                    _brawlerSettings._hostAddress, _brawlerSettings._port,
                    _brawlerSettings._roomId, _sessionConfig, ct);

                _gameMenu.ReconnectStatus = null;
                _gameMenu.SetActionType(GameMenu.ActionType.Ready);
            }
            catch (OperationCanceledException)
            {
                _logger?.KWarning($"[Brawler] Join canceled");
                _gameMenu.ReconnectStatus = null;
                _gameMenu.SetActionType(GameMenu.ActionType.JoinRoom);
            }
            catch (JoinFailedException jfe)
            {
                _logger?.KWarning($"[Brawler] Join rejected: {jfe.Reason.ToName()}");
                _gameMenu.ReconnectStatus = jfe.Reason.ToDefaultMessage();
                _gameMenu.SetActionType(GameMenu.ActionType.JoinRoom);
            }
            catch (Exception e)
            {
                _logger?.KError(e, $"[Brawler] JoinGame failed");
                _gameMenu.ReconnectStatus = null;
                _gameMenu.SetActionType(GameMenu.ActionType.JoinRoom);
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
                // Status already cleared above; keep the connect CTS (old FallbackToInitial behavior).
                ResetToInitialUi(cancelConnect: false, clearStatus: false);
            }
            catch (ReconnectFailedException e)
            {
                _logger?.KError(e, $"[Brawler] Reconnect rejected: {e.Reason.ToName()}");
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
            if (_session != null
                && _session.Phase != SessionPhase.None
                && _session.Phase != SessionPhase.Disconnected)
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
            if (_session != null
                && _session.Phase != SessionPhase.None
                && _session.Phase != SessionPhase.Disconnected)
                return;

            _logger?.KInformation($"[Brawler] Spectator connecting to {_brawlerSettings._hostAddress}:{_brawlerSettings._port}");

            _flow?.CancelConnect();
            StartSpectatorAsync(destroyCancellationToken).Forget();
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

        // Single role-bearing creation callback — was OnAnyFlowSessionCreated (common) +
        // OnHostOrGuestSessionCreated + OnReplayOrSpectatorSessionCreated, merged via the kind arg.
        public void OnSessionCreated(KlothoSession session, SessionEntryKind kind)
        {
            // Replay output is declared at flow-build time via WithReplaySave; KlothoSessionFlow stamps
            // it onto host / guest sessions only, so the game no longer configures it per session here.
            _sessionDriver.Attach(session);

            // Initial push — state callbacks fire only on transition, so seed GameMenu from current state.
            _gameMenu.State      = session.State;
            _gameMenu.Phase      = session.Phase;
            _gameMenu.Players    = session.PlayerCount;
            _gameMenu.IsAllReady = session.AllPlayersReady;

            if (kind == SessionEntryKind.Host || kind == SessionEntryKind.Guest)
            {
                // Host/Guest path — FaultInjection attach (main _transport is live).
                // roleLabel comes from the resolved role, not raw _isHost: SD collapses to client
                // so a stale Inspector _isHost = true cannot leak into the diagnostic label.
                string roleLabel = Role.IsLocalHost() ? "host" : "guest";
                xpTURN.Klotho.Diagnostics.FaultInjectionRuntime.AttachToSession(
                    session, _transport, _logger,
                    roleLabel,
                    _sessionDriver);
                _faultInjectionHotkey?.Attach(session, _logger);
            }
            // Replay/Spectator skip FaultInjection (main _transport is idle for those modes).

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

        // Stop intent — thin router, no teardown. With a live session the framework drives
        // session.Stop → OnSessionStopped (terminal teardown); with no session it is just a UI reset
        // (idle/cancel paths). No re-entry guard: the framework owns idempotency.
        private void StopGame()
        {
            if (_session != null)
                _sessionDriver.DetachAndStop();
            else
                ResetToInitialUi();
        }

        // Session-independent return-to-initial-UI — terminal teardown (OnSessionStopped), no-session
        // stop intent, idle-disconnect, and reconnect cancel/fail all route here. Transport is no longer
        // disconnected (the driver owns it). cancelConnect/clearStatus absorb the two prior variants:
        // the old FallbackToInitial path left the connect CTS and the reconnect-reject message untouched.
        private void ResetToInitialUi(bool cancelConnect = true, bool clearStatus = true)
        {
            if (cancelConnect)
                _flow?.CancelConnect();

            if (clearStatus)
                _gameMenu.ReconnectStatus = null;

            _gameMenu.SetActionType(Role.IsLocalHost() ? GameMenu.ActionType.CreateRoom : GameMenu.ActionType.JoinRoom);
        }

        // ────────────────────────────────────────────
        // Reconnection
        // ────────────────────────────────────────────

        private void HandleReconnectFailure(ReconnectRejectReason reason)
        {
            _credentialsStore.Clear();

            if (reason == ReconnectRejectReason.AlreadyConnected)
            {
                _logger?.KWarning($"[Brawler] Reconnect rejected: AlreadyConnected — another device holds this PlayerId");
            }

            _gameMenu.ReconnectStatus = reason.ToDefaultMessage();
            // Preserve the reject message and the connect CTS (old FallbackToInitial behavior).
            ResetToInitialUi(cancelConnect: false, clearStatus: false);
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

        public void OnReconnectFailed(ReconnectRejectReason reason)
        {
            _logger?.KError($"[Brawler] Reconnection failed: {reason.ToName()}");
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

        // Terminal teardown — the framework calls this exactly once at the end of session.Stop(),
        // on both game-initiated and framework-internal stops. UI/transport only; replay is saved
        // by the framework (ConfigureReplaySave). No re-entry guard needed.
        public void OnSessionStopped()
        {
            // Process-exit: TeardownAll already owns terminal cleanup and _gameMenu may have been
            // destroyed first in OnDestroy ordering — skip UI to avoid MissingReferenceException.
            if (_teardownInvoked) { _session = null; return; }

            _logger?.KInformation($"[Brawler] Game stopped");
            _session = null;
            ResetToInitialUi();
        }

        // Idle transport drop (no session, no connect in flight) — the driver routes it here.
        void IKlothoSessionObserver.OnIdleDisconnected(DisconnectReason reason) => ResetToInitialUi();

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

        public void OnStateChanged(KlothoState s)         => _gameMenu.State = s;
        public void OnPhaseChanged(SessionPhase p)        => _gameMenu.Phase = p;
        public void OnPlayerCountChanged(int n)           => _gameMenu.Players = n;
        public void OnAllPlayersReadyChanged(bool v)      => _gameMenu.IsAllReady = v;

        private void OnIpAddressInputChanged(string addr) => _brawlerSettings._hostAddress = addr;
    }
}
