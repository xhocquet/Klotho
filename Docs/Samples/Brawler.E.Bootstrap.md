# Brawler Appendix E — Bootstrap Order

> Related: [Brawler.md](Brawler.md) §11 (Phase 8 — Callbacks & Session Wiring)
> Target: `BrawlerGameController` Awake → Start → HostGame / JoinGame flow + field-injection mapping
>
> ⚠️ **Note**: The code in this appendix is a condensed view of the actual `BrawlerGameController` structure. Refer to the real source for cancellation-token handling, async exception paths, and Ready-transition details. Method names, signatures, and event names match the actual source.

---

## E-1. End-to-End Initialization Flow

```
┌──────────────────────────────────────────────────────────────┐
│ [Unity scene loads]                                          │
│                                                              │
│ BrawlerGameController.Awake()                                │
│   • DontDestroyOnLoad                                        │
│   • CreateLogger()  → builds LoggerFactory, attaches rolling │
│                       file + UnityDebug sinks                │
│                                                              │
│ BrawlerGameController.Start()                                │
│   • Pre-load: StaticColliders, NavMesh, DataAssets, Registry │
│   • new PlayerPrefsReconnectCredentialsStore()               │
│   • new LiteNetLibTransport(_logger, …)                      │
│   • new BrawlerInputCapture() + Enable()                     │
│   • Mode-specific roomId reset (P2P → -1)                    │
│   • TryAutoReconnect() — cold-start ReconnectAsync if creds  │
│   • GameMenu.SetActionType(CreateRoom / JoinRoom)            │
│   • [KLOTHO_FAULT_INJECTION] ApplyFaultInjection()           │
│                                                              │
│ [Wait — GameMenu button input]                               │
│                                                              │
│ ┌─ StartHost() ────┐  ┌─ JoinGame() ─────────────┐           │
│ │ Build P2P SimCfg │  │ JoinGameAsync (UniTask)  │           │
│ │ new SimCallbacks │  │  KlothoConnectionAsync   │           │
│ │ new ViewCallbacks│  │    .ConnectAsync(...)    │           │
│ │ KlothoSession    │  │  new SimCallbacks        │           │
│ │   .Create        │  │  new ViewCallbacks       │           │
│ │ (LifecycleObserver=this)│ KlothoSession.Create │           │
│ │ HostGame +       │  │  (Connection = result    │           │
│ │   Transport.Listen  │   CredentialsStore /     │           │
│ │ SendPlayerConfig │  │   LifecycleObserver=this)│           │
│ │ InitializeViewSync  │  SendPlayerConfig        │           │
│ └──────────────────┘  │  InitializeViewSync      │           │
│                       └──────────────────────────┘           │
│                                                              │
│ ┌─ StartSpectator() ──────────────────────┐                  │
│ │ StartSpectatorAsync (UniTask)           │                  │
│ │  KlothoSpectatorAsync.CreateAsync(      │                  │
│ │   SpectatorSessionSetup {               │                  │
│ │     Transport, HostAddress, Port, RoomId│                  │
│ │     LifecycleObserver = this            │                  │
│ │     CallbacksFactory(simCfg, sessionCfg)│                  │
│ │   })                                    │                  │
│ │  InitializeViewSync                     │                  │
│ └─────────────────────────────────────────┘                  │
│                                                              │
│ [Game loop starts]                                           │
│   • Update() → session.Update(dt) (direct call)              │
│   • ISimulationCallbacks.OnInitializeWorld (once)            │
│   • Engine auto-injects InitialStateSnapshot on replay path  │
│   • IViewCallbacks.OnGameStart (once, on game start)         │
│   • Per tick: OnPollInput → Simulation.Tick → OnTickExecuted │
└──────────────────────────────────────────────────────────────┘
```

---

## E-2. BrawlerGameController Field Layout

```csharp
[DefaultExecutionOrder(-100)]
public class BrawlerGameController : MonoBehaviour, IKlothoSessionObserver
{
    const string KLOTHO_CONNECTION_KEY = "xpTURN.Brawler";

    [Header("Debug")]
    [SerializeField] private LogLevel _logLevel = LogLevel.Information;

    [Header("Settings")]
    [SerializeField] private BrawlerSettings _brawlerSettings = new BrawlerSettings();
    [SerializeField] private USimulationConfig _simulationConfig;
    [SerializeField] private USessionConfig    _sessionConfig;  // host-decided session policy (MaxPlayers / grace / late-join…)

    [Header("Scene References")]
    [SerializeField] private GameMenu _gameMenu;
    [SerializeField] private BrawlerViewSync _viewSync;
    // EVU reference. If the prefab has an EntityView, EVU auto-spawns it.
    // Inspector-null → EVU hook is skipped.
    [SerializeField] private xpTURN.Klotho.EntityViewUpdater _entityViewUpdater;

    [Header("Static Colliders")]
    [SerializeField] private TextAsset _staticCollidersAsset;
    [Header("NavMesh")]
    [SerializeField] private TextAsset _navMeshAsset;
    [Header("DataAssets")]
    [SerializeField] private TextAsset _dataAsset;

    // Runtime state
    private ILogger _logger;
    private List<FPStaticCollider> _staticColliders;
    private FPNavMesh _navMesh;
    private List<IDataAsset> _dataAssets;
    private IDataAssetRegistry _assetRegistry;

    private KlothoSession _session;
    private LiteNetLibTransport _transport;
    private Camera _mainCamera;
    private CancellationTokenSource _connectCts;          // cancels in-flight JoinGameAsync / ReconnectAsync
    private IReconnectCredentialsStore _credentialsStore; // PlayerPrefs-backed cold-start credentials

    private BrawlerInputCapture _input;
    private BrawlerSimulationCallbacks _simCallbacks;
    private BrawlerViewCallbacks _viewCallbacks;

    // Spectator-mode bootstrap is delegated to KlothoSpectatorAsync.CreateAsync — the resulting
    // KlothoSession is stored in _session (same field as host / guest paths). The previous
    // _spectatorEngine / _spectatorSimulation / _pendingSpectator* fields were removed when the
    // framework took over Engine/Simulation construction for spectator mode.

    private long _lastTicks;
    private string _replayPath = Application.dataPath + "/../Replays/brawler.rply";

#if KLOTHO_FAULT_INJECTION
    // RTT spike schedule anchor — set on the first frame Phase enters Playing.
    // Per-client drift = each client's GameStartMessage receive jitter.
    private float _rttScheduleAnchorTime = -1f;
    private int   _rttScheduleNextIdx;
#endif

    public bool IsHost => _brawlerSettings._isHost;
    public SessionPhase Phase => _session?.NetworkService?.Phase ?? SessionPhase.None;
    private KlothoEngine  ActiveEngine     => _session?.Engine;       // spectator session also lives in _session now
    private EcsSimulation ActiveSimulation => _session?.Simulation;
}

[Serializable]
public class BrawlerSettings
{
    [Header("ServerSettings")]
    [SerializeField] public string _hostAddress = "localhost";
    [SerializeField] public int _port = 777;
    // NetworkMode is sourced from _simulationConfig.Mode (single SoT — was BrawlerSettings._mode).

    [Header("ServerDriven")]
    [SerializeField] public int _roomId = 0;          // SD: 0 = single room / N = multi-room slot. P2P: forced to -1 at Start()

    [Header("P2P")]
    [SerializeField] public bool _isHost = true;
    [SerializeField] public int _botCount = 0;
    // _maxPlayers removed — MaxPlayers is sourced from _sessionConfig.MaxPlayers (single SoT).

    [Header("PlayerSettings")]
    [SerializeField] public int _characterClass = 0;  // 0=Warrior, 1=Mage, 2=Rogue, 3=Knight
}
```

Notes:
- The Unity-side update driver is **not** `UKlothoBehaviour` — `BrawlerGameController.Update()` calls `_session.Update(dt)` directly (the spectator path also routes through the same `_session.Update(dt)` since the framework now returns a `KlothoSession` from `KlothoSpectatorAsync.CreateAsync`). `UKlothoBehaviour` still exists in `Assets/Klotho/Unity/ULockstepBehaviour.cs` but is unused by this sample.
- `_entityViewUpdater` is the EntityViewUpdater field name (renamed from the older `_viewUpdater`); its setup runs via `InitializeViewSync(engine, simulation)` rather than a direct `Initialize(engine)` call. EVU also owns the built-in **PlayerViewRegistry** (player ↔ view lookup) — previously a sample-side helper.
- `_credentialsStore` underpins cold-start auto-reconnect (`Start()` → `TryAutoReconnect()` → `ReconnectAsync(ct)`). It is injected into `KlothoSession` via `KlothoSessionSetup.CredentialsStore` — no separate `InjectCredentialsStoreIntoSession()` call.
- `BrawlerGameController` implements `IKlothoSessionObserver`, and `KlothoSessionSetup.LifecycleObserver = this` registers it as the single subscription site for session lifecycle (replaces per-event `+=` wiring across StartHost / JoinGame / Reconnect / StopGame).

---

## E-3. Awake / Start

### Awake()

```csharp
private void Awake()
{
    DontDestroyOnLoad(gameObject);
    CreateLogger();   // factory + UnityDebug + rolling file sinks
}

private void CreateLogger()
{
    var loggerFactory = LoggerFactory.Create(b =>
    {
        b.SetMinimumLevel(_logLevel);
        b.AddZLoggerUnityDebug();
        b.AddZLoggerRollingFile(opt =>
        {
            opt.FilePathSelector = (dt, idx) =>
                $"Logs/Client_{dt:yyyy-MM-dd-HH-mm-ss-fff}_{idx:000}.log";
            opt.RollingInterval  = RollingInterval.Day;
            opt.RollingSizeKB    = 1024 * 1024;
            opt.UsePlainTextFormatter(/* prefix + exception formatters */);
        });
    });
    _logger = loggerFactory.CreateLogger("Client");
}
```

### Start()

```csharp
private void Start()
{
    // 1) Pre-load static assets
    _staticColliders = FPStaticColliderSerializer.Load(_staticCollidersAsset.bytes);
    _navMesh         = FPNavMeshSerializer.Deserialize(_navMeshAsset.bytes);
    _dataAssets      = DataAssetReader.LoadMixedCollectionFromBytes(_dataAsset.bytes);

    IDataAssetRegistryBuilder registryBuilder = new DataAssetRegistry();
    registryBuilder.RegisterRange(_dataAssets);
    _assetRegistry = registryBuilder.Build();

    _mainCamera       = Camera.main;
    _credentialsStore = new PlayerPrefsReconnectCredentialsStore();

    // 2) Transport — connectionKey gates non-Brawler clients at the LiteNetLib layer
    var logLevels = new[] { LiteNetLib.NetLogLevel.Warning, LiteNetLib.NetLogLevel.Error };
    _transport = new LiteNetLibTransport(_logger, logLevels, connectionKey: KLOTHO_CONNECTION_KEY);
    _transport.OnDisconnected += OnDisconnected;

    // 3) Input capture
    _input = new BrawlerInputCapture();
    _input.Enable();

    // 4) P2P uses _roomId = -1 by convention; SD keeps the Inspector value (0..N).
    if (_simulationConfig.Mode != NetworkMode.ServerDriven)
        _brawlerSettings._roomId = -1;

    _gameMenu.IsHost = IsHost;

    // 5) Cold-start auto-reconnect probe — SD clients and P2P guests only.
    //    P2P host's death ends the session, so it is never a reconnect target.
    bool isP2PHost = _simulationConfig.Mode == NetworkMode.P2P && _brawlerSettings._isHost;
    if (!isP2PHost && TryAutoReconnect())
        return;
    _gameMenu.SetActionType(_brawlerSettings._isHost
        ? GameMenu.ActionType.CreateRoom
        : GameMenu.ActionType.JoinRoom);

#if KLOTHO_FAULT_INJECTION
    ApplyFaultInjection();   // see E-10
#endif

    bool TryAutoReconnect()
    {
        var creds = _credentialsStore.Load();
        if (creds == null) return false;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!_credentialsStore.IsValid(creds, now, Application.version))
        {
            _credentialsStore.Clear();
            return false;
        }
        _gameMenu.SetActionType(GameMenu.ActionType.Reconnect);
        _connectCts = new CancellationTokenSource();
        ReconnectAsync(_connectCts.Token).Forget();
        return true;
    }
}
```

GameMenu button wiring (`_btnHost / _btnGuest / _btnAction / _btnReplay / _btnSpectator`) is done in `OnEnable` (and torn down in `OnDisable`), not in `Start`. The action button dispatches to `StartHost()` / `JoinGame()` based on `_gameMenu.CurrentAction`.

---

## E-4. StartHost — Host Flow (P2P only)

```csharp
private void StartHost()
{
    _logger?.ZLogInformation($"[Brawler] Hosting game");

    // 1) Force the simulation-config Mode to P2P for the host path.
    //    Falls back to a default SimulationConfig if the Inspector field is null.
    ISimulationConfig simulationConfig;
    if (_simulationConfig != null)
    {
        simulationConfig = _simulationConfig;
        _simulationConfig.Mode = NetworkMode.P2P;
    }
    else
    {
        simulationConfig = new SimulationConfig();
        ((SimulationConfig)simulationConfig).Mode = NetworkMode.P2P;
    }

    // 2) Build callbacks — dataAssets are NOT passed (registry is shared via AssetRegistry).
    //    MaxPlayers is sourced from _sessionConfig (single SoT, set per-prefab).
    _simCallbacks = new BrawlerSimulationCallbacks(
        _input, _logger, _staticColliders, _navMesh,
        _sessionConfig.MaxPlayers, _brawlerSettings._botCount);
    _viewCallbacks = new BrawlerViewCallbacks(_simCallbacks, _logger);

    // 3) Create the session. LifecycleObserver = this bulk-subscribes the IKlothoSessionObserver
    //    callbacks (OnPlayerDisconnected/Reconnected, OnReconnecting/Failed/Reconnected,
    //    OnCatchupComplete, OnResyncCompleted, OnGameStart, OnMatchAborted/Ended/Reset,
    //    OnSessionStopped) — no per-event += wiring is needed below.
    _session = KlothoSession.Create(new KlothoSessionSetup
    {
        Transport           = _transport,
        Logger              = _logger,
        SimulationCallbacks = _simCallbacks,
        ViewCallbacks       = _viewCallbacks,
        AssetRegistry       = _assetRegistry,
        SimulationConfig    = simulationConfig,
        SessionConfig       = _sessionConfig,   // host-decided ISessionConfig (USessionConfig SO)
        LifecycleObserver   = this,
    });
    _simCallbacks.SetNetworkService(_session.NetworkService);   // no-op in current SimCallbacks

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    ConnectPhysicsProvider();                                   // editor-only physics debug hook
#endif

    // 4) Start host + transport listen. Early-return on bind failure.
    _session.HostGame("Game", _sessionConfig.MaxPlayers);
    if (!_transport.Listen(_brawlerSettings._hostAddress,
                           _brawlerSettings._port,
                           _sessionConfig.MaxPlayers))
    {
        _logger?.ZLogError($"[Brawler] Failed to host on port {_brawlerSettings._port}");
        StopGame();
        return;
    }

    // 5) Broadcast the local player's character selection.
    _session.SendPlayerConfig(new BrawlerPlayerConfig
    {
        SelectedCharacterClass = _brawlerSettings._characterClass,
    });

    // 6) Wire the EntityViewUpdater + BrawlerViewSync to the freshly-created engine + sim.
    InitializeViewSync(_session.Engine, _session.Simulation);

    _gameMenu.SetActionType(GameMenu.ActionType.Ready);
}
```

Notes:
- The host path is P2P-only. SD does not use `StartHost()` — the dedicated server (Appendix H) is the SD authority.
- `BrawlerSimulationCallbacks` constructor's optional `dataAssets` parameter is intentionally left default (the asset registry is already shared via `AssetRegistry`).
- No `UKlothoBehaviour.Bind(...)` call: `BrawlerGameController.Update()` drives `_session.Update(dt)` directly (see E-1).
- The host no longer needs `OnGameStart += InjectInitialStateSnapshot`. The replay snapshot is auto-injected by `Engine.StartReplay`; the live host path does not need it.

---

## E-5. JoinGame — Guest Flow (async)

`JoinGame()` (synchronous entry) cancels any in-flight token and dispatches `JoinGameAsync(ct).Forget()`. Both P2P and SD clients reach the server through the same `KlothoConnectionAsync` path (lifted to `Assets/Klotho/Unity/KlothoConnectionAsync.cs` from its earlier sample location); the only divergence is the `preJoin` message for SD multi-room routing.

```csharp
private void JoinGame()
{
    // Both P2P and SD (single / multi room) use the async path through KlothoConnection.
    _connectCts?.Cancel();
    _connectCts?.Dispose();
    _connectCts = new CancellationTokenSource();
    JoinGameAsync(_connectCts.Token).Forget();
}

private async UniTaskVoid JoinGameAsync(CancellationToken ct)
{
    _logger?.ZLogInformation($"[Brawler] Joining game");
    _gameMenu.ReconnectStatus = "Connecting...";

    try
    {
        // 1) SD path needs RoomHandshakeMessage before PlayerJoinMessage.
        //    RoomId = 0 for single-room, N for multi-room slot. -1 means P2P.
        NetworkMessageBase preJoin = null;
        int roomId = -1;
        if (_simulationConfig.Mode == NetworkMode.ServerDriven)
        {
            roomId  = _brawlerSettings._roomId;
            preJoin = new RoomHandshakeMessage { RoomId = roomId };
        }

        // 2) Connect + handshake. Returns ConnectionResult with SimulationConfig payload.
        var result = await KlothoConnectionAsync.ConnectAsync(
            _transport,
            _brawlerSettings._hostAddress, _brawlerSettings._port,
            ct, _logger, preJoin,
            deviceIdProvider: new UnityDeviceIdProvider());

        // 3) Build callbacks (same shape as host; dataAssets not passed). MaxPlayers here is just
        //    a local guess — the server-authoritative value lands via GameStartMessage /
        //    ReconnectAcceptMessage shortly after, so InitialMaxPlayersGuess() is used.
        _simCallbacks = new BrawlerSimulationCallbacks(
            _input, _logger, _staticColliders, _navMesh,
            InitialMaxPlayersGuess(), _brawlerSettings._botCount);
        _viewCallbacks = new BrawlerViewCallbacks(_simCallbacks, _logger);

        // 4) Create the session. Connection.Kind drives the Create branch; the warm-reconnect path
        //    is picked automatically when Connection was produced by ReconnectAsync. All session
        //    lifecycle events flow through IKlothoSessionObserver (LifecycleObserver = this).
        _session = KlothoSession.Create(new KlothoSessionSetup
        {
            Connection          = result,
            Logger              = _logger,
            SimulationCallbacks = _simCallbacks,
            ViewCallbacks       = _viewCallbacks,
            AssetRegistry       = _assetRegistry,
            RoomId              = roomId,
            CredentialsStore    = _credentialsStore,                    // formerly InjectCredentialsStoreIntoSession()
            AppVersion          = Application.version,
            DeviceIdProvider    = new UnityDeviceIdProvider(),
            LifecycleObserver   = this,
        });
        _simCallbacks.SetNetworkService(_session.NetworkService);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        ConnectPhysicsProvider();
#endif

        // 5) Game-side intent send. No event subscriptions here — IKlothoSessionObserver covers them.
        _session.SendPlayerConfig(new BrawlerPlayerConfig
        {
            SelectedCharacterClass = _brawlerSettings._characterClass,
        });

        InitializeViewSync(_session.Engine, _session.Simulation);

        _gameMenu.ReconnectStatus = null;
        _gameMenu.SetActionType(GameMenu.ActionType.Ready);
    }
    catch (OperationCanceledException) { /* canceled — cleanup + back to JoinRoom UI */ }
    catch (Exception e)                 { _logger?.ZLogError(e, $"[Brawler] JoinGame failed"); }
}
```

`ReconnectAsync(ct)` mirrors this shape but calls `KlothoConnectionAsync.ReconnectAsync(_transport, creds, ct, _logger)` and reads `roomId` from the persisted credentials (`creds.RoomId`). It also catches `ReconnectFailedException` and branches on `e.Reason` (a `ReconnectRejectReason` byte; e.g. `AlreadyConnected` triggers a user-choice fallback flow). The `InitializeViewSync` step is identical.

IKlothoSessionObserver wires the lifecycle callbacks once at `Create`. The forwarded handler names match the prior `OnReconnecting / OnReconnectFailed(byte) / OnReconnected / OnLateJoinActive (forwarded from `IKlothoSessionObserver.OnCatchupComplete`) / OnResyncCompleted(int)` shape; the framework unsubscribes them at `Stop()` and then dispatches `OnSessionStopped()` so the game can do its own teardown (transport disconnect, null-out `_session`, etc.).

---

## E-5b. StartSpectator — Spectator Flow (async, framework-driven)

`StartSpectator()` delegates to `KlothoSpectatorAsync.CreateAsync` (lives in `Assets/Klotho/Unity/`). The framework owns `SpectatorService` / two-config await / Engine + Simulation construction. The game supplies a `CallbacksFactory` that fires **after** `SpectatorAcceptMessage` delivers server-authoritative `SimulationConfig` + `SessionConfig`, so callback objects (e.g. `BrawlerSimulationCallbacks(maxPlayers=...)`) can size against server values rather than the local Inspector field.

```csharp
private async UniTaskVoid StartSpectatorAsync(CancellationToken ct)
{
    var spectatorTransport = new LiteNetLibTransport(_logger, connectionKey: KLOTHO_CONNECTION_KEY);
    _session = await KlothoSpectatorAsync.CreateAsync(new SpectatorSessionSetup
    {
        Logger            = _logger,
        AssetRegistry     = _assetRegistry,
        Transport         = spectatorTransport,
        HostAddress       = _brawlerSettings._hostAddress,
        Port              = _brawlerSettings._port,
        RoomId            = _brawlerSettings._roomId,
        LifecycleObserver = this,
        CallbacksFactory = (simCfg, sessionCfg) =>
        {
            _simCallbacks = new BrawlerSimulationCallbacks(
                _input, _logger, _staticColliders, _navMesh,
                sessionCfg.MaxPlayers, _brawlerSettings._botCount);
            _viewCallbacks = new BrawlerViewCallbacks(_simCallbacks, _logger);
            return new SpectatorCallbacks(_simCallbacks, _viewCallbacks);
        },
    }, ct);

    InitializeViewSync(_session.Engine, _session.Simulation);
    _gameMenu.SetActionType(GameMenu.ActionType.Playing);
}
```

Notes:
- The returned `_session` is an ordinary `KlothoSession` — the same `_session.Update(dt)` loop drives the spectator tick. There is no separate `_spectatorEngine` / `_spectatorSimulation` field any more.
- Spectator mode uses the same `IKlothoSessionObserver` for lifecycle (`OnSessionStopped`, `OnResyncCompleted`, etc.). Error-correction (capture-pre-rollback / PuP) is enabled in spectator mode just like the regular client; the EC pair is wired internally when a batch of verified input arrives.
- `SpectatorSessionSetup` has no `CredentialsStore` / `SessionConfig` / `MaxPlayers` fields — those values arrive over the wire and are owned by the framework. Only `CallbacksFactory` is required.

---

## E-6. BrawlerSimulationCallbacks — Fields, State, Reactive Hooks

The callbacks class has grown to host two reactive-escalation paths and a state-driven spawn loop on top of its core responsibilities. Subsections below mirror the actual source layout.

### E-6-1. Fields & Constructor

```csharp
public class BrawlerSimulationCallbacks : ISimulationCallbacks
{
    private readonly BrawlerInputCapture _input;
    private readonly ILogger _logger;
    private readonly List<FPStaticCollider> _staticColliders;
    private readonly FPNavMesh _navMesh;
    private readonly int _maxPlayers;
    private readonly int _botCount;
    private readonly List<IDataAsset> _dataAssets;

    private IKlothoEngine _engine;

    // Spawn lifecycle — state-driven (ECS Frame query). No more "Spawned" latch.
    private int _lastSpawnAttemptTick = -1;
    private const int SpawnRetryInterval = 20;   // ~500ms @ 40Hz

    // Spawn cmd extra lead. Escalates by SPAWN_DELAY_STEP on each PastTick reject; latched until
    // the match boundary (BrawlerGameController re-news _simCallbacks).
    private int _extraSpawnDelay = 0;
    private const int SPAWN_DELAY_STEP = 4;       // ~100ms @ 40Hz
    private const int SPAWN_DELAY_MAX  = 40;      // ~1s cap — triggers one-shot Error + post-cap latch
    private bool _capHitLogged = false;
    private int  _capHitRejectCount = 0;

    public FPNavMesh     NavMesh      => _navMesh;
    public FPNavMeshQuery NavQuery    { get; private set; }
    public BotFSMSystem  BotFSMSystem { get; private set; }

    public BrawlerSimulationCallbacks(BrawlerInputCapture input, ILogger logger,
                                      List<FPStaticCollider> colliders, FPNavMesh navMesh,
                                      int maxPlayers, int botCount,
                                      List<IDataAsset> dataAssets = null)
    {
        _input = input; _logger = logger;
        _staticColliders = colliders; _navMesh = navMesh;
        _maxPlayers = maxPlayers; _botCount = botCount;
        _dataAssets = dataAssets;     // currently always passed as default (registry is shared elsewhere)
    }

    // Retained as a no-op for API symmetry; bot spawn is decided by _botCount.
    public void SetNetworkService(IKlothoNetworkService _) { }
}
```

> The previous client-reactive PastTick / rollback-burst escalation fields (`_lastServerPushTick`, `_reactiveWindowStartTick`, `SERVER_PUSH_GRACE_TICKS`, `REACTIVE_*`, `ROLLBACK_*`) were lifted into the framework class **`DynamicInputDelayPolicy`** (`Assets/Klotho/Runtime/Core/Engine/DynamicInputDelayPolicy.cs`). The policy is attached automatically by `KlothoSession` on non-host sessions; thresholds are sourced from `SessionConfig` (`ServerPushGraceTicks`, `ReactiveWindowTicks`, `ReactiveEscalateThreshold`, `ReactiveStep`, `ReactiveMax`, `RollbackBurstCount`, `RollbackWindowTicks`, `ReactiveEscalateCooldownTicks`). The sample only keeps the spawn-cmd-specific escalation (above).

### E-6-2. Engine wiring — `SetEngine`

```csharp
public void SetEngine(IKlothoEngine engine)
{
    _engine = engine;
    engine.OnCommandRejected += HandleCommandRejected;       // spawn-cmd-specific only
}

public void OnInitializeWorld(IKlothoEngine engine)
{
    SetEngine(engine);
    BrawlerSimSetup.InitializeWorldState(engine, _maxPlayers, _botCount);
}

public void OnResyncCompleted(int _)
{
    // FullState resync reconstructs ECS — the previous spawn-attempt tick is no longer meaningful.
    _lastSpawnAttemptTick = -1;
}
```

### E-6-3. `OnPollInput` — state-driven spawn loop + input dispatch

```csharp
public void OnPollInput(int playerId, int tick, ICommandSender sender)
{
    if (_engine == null) return;

#if KLOTHO_FAULT_INJECTION
    // Force-retry path: bypass HasOwnCharacter so spawn cmd re-fires even after success.
    // Returns early so a Move/Attack send in the same poll does NOT overwrite the spawn cmd
    // in the InputBuffer (single command per (tick, playerId) slot).
    if (FaultInjection.ForceSpawnRetryPlayerIds.Contains(playerId)) { /* SendSpawnCommand + return */ }
#endif

    var frame = ((EcsSimulation)_engine.Simulation).Frame;
    if (!HasOwnCharacter(frame, playerId))
    {
        if (_lastSpawnAttemptTick < 0 || tick >= _lastSpawnAttemptTick + SpawnRetryInterval)
            SendSpawnCommand(_engine);

        // Skip emptyMove for two ticks:
        //   (a) the spawn-send tick itself
        //   (b) the tick whose emptyMove target tick equals the spawn cmd's target tick —
        //       collision would last-write-wins overwrite the spawn cmd in the server's InputBuffer.
        if (_lastSpawnAttemptTick >= 0
            && tick > _lastSpawnAttemptTick
            && tick != _lastSpawnAttemptTick + _extraSpawnDelay)
        {
            // emit a no-op MoveInputCommand so the tick advances on the server side
        }
        return;
    }

    // Normal poll path — capture input and dispatch Move/Attack/Skill commands.
    // _input.CaptureInput()/ConsumeOneShot() book-end the dispatch as before.
}

private static bool HasOwnCharacter(Frame frame, int playerId)
{
    var filter = frame.Filter<OwnerComponent, CharacterComponent>();
    while (filter.Next(out var entity))
    {
        ref readonly var owner = ref frame.GetReadOnly<OwnerComponent>(entity);
        if (owner.OwnerId == playerId) return true;
    }
    return false;
}
```

### E-6-4. Rejection hook (spawn-cmd only)

```csharp
// Receives only LocalPlayer's command rejections (server-unicast CommandRejectedMessage).
// Non-spawn PastTick / rollback-burst handling lives in DynamicInputDelayPolicy now.
private void HandleCommandRejected(int tick, int cmdTypeId, RejectionReason reason)
{
    if (cmdTypeId != SpawnCharacterCommand.TYPE_ID) return;

    if (reason == RejectionReason.Duplicate) { _lastSpawnAttemptTick = -1; return; }
    if (reason == RejectionReason.PastTick)
    {
        _lastSpawnAttemptTick = -1;
        if (_extraSpawnDelay < SPAWN_DELAY_MAX) _extraSpawnDelay += SPAWN_DELAY_STEP;
        else /* one-shot Error log + post-cap reject counter */;
    }
}
```

### E-6-5. `SendSpawnCommand` — uses `extraDelay` parameter

```csharp
public void SendSpawnCommand(IKlothoEngine engine)
{
    int playerId = engine.LocalPlayerId;
#if KLOTHO_FAULT_INJECTION
    if (FaultInjection.DropSpawnCommandPlayerIds.Contains(playerId))
    { _lastSpawnAttemptTick = engine.CurrentTick; return; }   // exercise self-heal path
#endif
    var rules    = ((EcsSimulation)engine.Simulation).Frame.AssetRegistry.Get<BrawlerGameRulesAsset>(1001);
    int spawnIdx = playerId % rules.SpawnPositions.Length;
    FPVector3 pos = rules.SpawnPositions[spawnIdx];

    var playerConfig = engine.GetPlayerConfig<BrawlerPlayerConfig>(playerId);
    var cmd = CommandPool.Get<SpawnCharacterCommand>();
    cmd.CharacterClass = playerConfig?.SelectedCharacterClass ?? 0;
    cmd.SpawnPosition  = new FPVector2(pos.x, pos.z);

    // Engine fills PlayerId/Tick; pass extra lead so retries overshoot far enough to pass server check.
    _lastSpawnAttemptTick = engine.CurrentTick;
    engine.InputCommand(cmd, extraDelay: _extraSpawnDelay);
}
```

`RegisterSystems(EcsSimulation)` retains the prior shape — NavMesh query / bot HFSM build / `BrawlerSimSetup.RegisterSystems`.

---

## E-7. BrawlerViewCallbacks — Fields & Constructor

```csharp
public class BrawlerViewCallbacks : IViewCallbacks
{
    private readonly BrawlerSimulationCallbacks _sim;
    private readonly ILogger _logger;

    public BrawlerViewCallbacks(BrawlerSimulationCallbacks sim, ILogger logger)
    {
        _sim = sim;
        _logger = logger;
    }

    public void OnGameStart(IKlothoEngine engine)
    {
        _sim.SetEngine(engine);
        if (!engine.IsReplayMode)
            _sim.SendSpawnCommand(engine);
    }

    public void OnTickExecuted(int tick) { }      // HUD updates: EVU + GameHUD subscribe directly

    public void OnLateJoinActivated(IKlothoEngine engine)
    {
        _sim.SetEngine(engine);
        _sim.SendSpawnCommand(engine);
    }
    // Note: A previous `Respawn(IKlothoEngine)` helper was removed — respawn is now driven by
    // the state-driven spawn loop in OnPollInput (see E-6-3).
}
```

---

## E-8. Cross-Reference Caveats

- `BrawlerSimulationCallbacks._engine` is null until `SetEngine` runs (called from `OnInitializeWorld`). `OnPollInput` guards on `_engine == null` to stay safe before that point.
- `SetNetworkService` is currently a **no-op** on `BrawlerSimulationCallbacks` — bot spawn is decided by `_botCount` and the engine reference (from `SetEngine`) covers everything else. The setter is kept for API symmetry only.
- `BrawlerViewCallbacks` only obtains `engine` at `OnGameStart`. Don't use it before that.
- View wiring goes through `InitializeViewSync(engine, simulation)` (an internal helper of `BrawlerGameController`) rather than a direct `EntityViewUpdater.Initialize(engine)` call. Call sites: every successful StartHost / JoinGameAsync / ReconnectAsync / StartReplay path.
- After the registry is built, `DataAsset` is **immutable** — runtime additions are not allowed.

---

## E-9. Replay Bootstrap

Replay re-runs locally without networking. Use the same `KlothoSession.Create` static factory as the host path, but configure it without a Transport, then call `session.Engine.StartReplay(replayData)` to start playback.

```csharp
private void StartReplay()
{
    var replayBytes = File.ReadAllBytes(_brawlerSettings._replayPath);
    var replayData  = /* ReplayData.Deserialize(replayBytes) or ReplaySystem.LoadFromFile */;

    _simCallbacks  = new BrawlerSimulationCallbacks(_input, _logger, _staticColliders, _navMesh, 0, 0, _dataAssets);
    _viewCallbacks = new BrawlerViewCallbacks(_simCallbacks, _logger);

    _session = KlothoSession.Create(new KlothoSessionSetup {
        Logger              = _logger,
        SimulationCallbacks = _simCallbacks,
        ViewCallbacks       = _viewCallbacks,
        AssetRegistry       = _assetRegistry,
        SimulationConfig    = _simulationConfig,
        // Transport / Connection omitted — replay is local-only
    });

    InitializeViewSync(_session.Engine, _session.Simulation);

    _session.Engine.StartReplay(replayData);   // LZ4 decompression + InitialStateSnapshot auto-inject
}
```

> **Note**: There is no session-level replay factory — `KlothoSession.CreateForReplay` does not exist. The launch path is `KlothoSession.Create` (Transport / Connection omitted) followed by `Engine.StartReplay(IReplayData)`. A file-path convenience exists on the engine: `Engine.StartReplayFromFile(string filePath)` internally calls `_replaySystem.LoadFromFile` then forwards to `StartReplay`. `StartReplay` auto-injects the `InitialStateSnapshot` from `IReplayData.Metadata` via `Simulation.RestoreFromFullState`, so the game no longer needs `OnGameStart += InjectInitialStateSnapshot`.

---

## E-10. FaultInjection / RTT Spike Schedule (development-only)

Compiled in only when the `KLOTHO_FAULT_INJECTION` define is set. Disabled and stripped in production builds.

### E-10-1. Bootstrap hook — `ApplyFaultInjection()`

Called at the end of `Start()` (after the auto-reconnect probe). Loads `Assets/StreamingAssets/faultinjectionconfig.json` via `FaultInjectionLoader.TryLoadAndApply`. Missing file is silently ignored — fault injection stays off.

```csharp
private void Start()
{
    // ... (data preload, transport init, GameMenu wiring, auto-reconnect probe)

#if KLOTHO_FAULT_INJECTION
    ApplyFaultInjection();
#endif
}

#if KLOTHO_FAULT_INJECTION
private void ApplyFaultInjection()
{
    var path = Path.Combine(Application.streamingAssetsPath, "faultinjectionconfig.json");
    FaultInjectionLoader.TryLoadAndApply(path, _logger);
}
#endif
```

Schema fields (see `FaultInjectionLoader.cs`): `EmulatedRttMs`, `EmulatedRttSchedule[(atSec, rttMs)]`, `ServerGcPauseMs`, `ServerGcPauseAtTick`, `DropSpawnCommandPlayerIds`, `SuppressBootstrapAckPlayerIds`, `ForceTickOffsetDelta`.

### E-10-2. Update hook — `UpdateRttSchedule()`

Called every frame from `Update()`. Drives the timed `EmulatedRttSchedule` once `Phase == Playing` and emits a match-end metrics line on exit.

```csharp
private void Update()
{
    UpdateStatus();

#if KLOTHO_FAULT_INJECTION
    UpdateRttSchedule();
#endif

    // ... (engine update, replay, etc.)
}

#if KLOTHO_FAULT_INJECTION
private void UpdateRttSchedule()
{
    if (Phase != SessionPhase.Playing)
    {
        if (_rttScheduleAnchorTime >= 0f)
        {
            // Leaving Playing → flush match-end summary.
            RttSpikeMetricsCollector.EmitSummary(_logger);
            _rttScheduleAnchorTime = -1f;
            _rttScheduleNextIdx = 0;
        }
        return;
    }

    if (_rttScheduleAnchorTime < 0f)
    {
        // First frame after entering Playing — anchor the schedule clock.
        _rttScheduleAnchorTime = Time.unscaledTime;
        _rttScheduleNextIdx = 0;
        int localId = ActiveEngine?.LocalPlayerId ?? -1;
        RttSpikeMetricsCollector.OnMatchStart(IsHost ? "host" : "guest", localId);
    }

    if (FaultInjection.EmulatedRttSchedule.Count == 0)
        return;

    float elapsedSec = Time.unscaledTime - _rttScheduleAnchorTime;
    var schedule = FaultInjection.EmulatedRttSchedule;
    while (_rttScheduleNextIdx < schedule.Count && elapsedSec >= schedule[_rttScheduleNextIdx].atSec)
    {
        var entry = schedule[_rttScheduleNextIdx];
        FaultInjection.EmulatedRttMs = entry.rttMs;          // overwrite live RTT
        RttSpikeMetricsCollector.OnSpike(entry.atSec, entry.rttMs);
        _rttScheduleNextIdx++;
    }
}
#endif
```

### E-10-3. Anchor clock and per-client drift

`_rttScheduleAnchorTime` is captured the first frame `Phase` enters `Playing` — that is, after `GameStartMessage` arrives. Each client anchors against its own receive time, so spike timings drift across clients by the `GameStartMessage` jitter (typically a few ms to tens of ms). Acceptable for measurement; not deterministic.

### E-10-4. Metrics emit

`RttSpikeMetricsCollector.EmitSummary` writes a one-line `[Metrics][RttSpike]` log at match end with spike list, chain-break counts windowed around each spike, rollback depth mean/p95, and chain-resume latency per spike. Used by the RTT spike measurement scripts.
