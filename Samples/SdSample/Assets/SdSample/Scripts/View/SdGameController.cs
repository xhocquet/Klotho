using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using xpTURN.Klotho;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.LiteNetLib;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Unity;

namespace xpTURN.Samples.SdSample
{
    [DefaultExecutionOrder(-100)]
    public class SdGameController : MonoBehaviour, IKlothoSessionObserver
    {
        const string ConnectionKey = "xpTURN.SdSample";

        [field: Header("Logging")]
        [SerializeField] private KLogLevel _logLevel = KLogLevel.Information;

        [field: Header("Network")]
        [SerializeField] private string _hostAddress = "localhost";
        [SerializeField] private int _port = 7777;

        [field: Header("Configs")]
        [SerializeField] private USimulationConfig _simulationConfig;
        [SerializeField] private USessionConfig _sessionConfig;
        [SerializeField] private TextAsset _dataAsset;

        [field: Header("Scene References")]
        [SerializeField] private KlothoSessionDriver _sessionDriver;
        [SerializeField] private EntityViewUpdater _entityViewUpdater;
        [SerializeField] private SdHud _hud;
        [SerializeField] private SdMenu _menu;

        private IKLogger _logger;
        private IDataAssetRegistry _assetRegistry;
        private LiteNetLibTransport _transport;
        private SdInputCapture _input;
        private KlothoSessionFlow _flow;
        private KlothoSession _session;
        private SdViewCallbacks _viewCallbacks;
        private bool _joining;

        private void Awake()
        {
            _logger = KlothoLogger.CreateDefault(level: _logLevel, filePrefix: "Client", categoryName: "Client");
            _logger?.KInformation($"SdSample logger started : LogLevel={_logLevel}");

            _sessionDriver.PreSessionUpdate += OnPreSessionUpdate;
        }

        private void Start()
        {
            var assets = DataAssetReader.LoadMixedCollectionFromBytes(_dataAsset.bytes);
            IDataAssetRegistryBuilder builder = new DataAssetRegistry();
            builder.RegisterRange(assets);
            _assetRegistry = builder.Build();

            _transport = new LiteNetLibTransport(_logger, levels: null, connectionKey: ConnectionKey);

            _input = new SdInputCapture();
            _input.Enable();

            var setup = new KlothoFlowSetupBuilder((simCfg, sessCfg) =>
                {
                    var simCallbacks = new SdSimulationCallbacks(_input);
                    _viewCallbacks = new SdViewCallbacks(_hud);
                    return new SessionCallbacks(simCallbacks, _viewCallbacks);
                })
                .WithLogger(_logger)
                .WithTransport(_transport)
                .WithAssetRegistry(_assetRegistry)
                .WithLifecycleObserver(this)
                .WithUnityDefaults()
                .Build();

            _flow = new KlothoSessionFlow(setup);
            // Driver owns the main transport (idle pumping + idle-disconnect routing); bind before any
            // session so the driver subscribes ahead of NetworkService.
            _sessionDriver.BindTransport(_transport, this, _flow);
            // Session creation observed via IKlothoSessionObserver.OnSessionCreated(session, kind).

            _menu.SetInitialHost(_hostAddress, _port);
            _menu.OnJoinClicked  += OnBtnJoin;
            _menu.OnReadyClicked += OnBtnReady;
            _menu.OnStopClicked  += OnBtnStop;
            _menu.SetReadyEnabled(false);
            _menu.SetStopEnabled(false);
        }

        private void OnPreSessionUpdate(KlothoSession session, float dt)
        {
            if (session.State == KlothoState.Running)
            {
                _input.CaptureInput();
            }
        }

        // Pre-stop hook — fires inside session.Stop() while Engine is alive (replaces Driver.Stopping).
        public void OnSessionStopping()
        {
            _viewCallbacks?.Cleanup();
            _entityViewUpdater?.Cleanup();
            _menu.SetReadyEnabled(false);
            _menu.SetStopEnabled(false);
            _hud.SetLocalReady(false);
        }

        public void OnSessionCreated(KlothoSession session, SessionEntryKind kind)
        {
            _session = session;
            _sessionDriver.Attach(session);
            _entityViewUpdater?.Initialize(session.Engine);

            // Push initial value — OnPhaseChanged fires only on transition.
            _hud.SetPhase(session.Phase);

            _menu.SetReadyEnabled(true);
            _menu.SetStopEnabled(true);
        }

        private void OnBtnJoin()
        {
            if (_session != null || _joining) return;
            _hostAddress = _menu.Host;
            _port = _menu.Port;
            _joining = true;
            JoinAsync().Forget();
        }

        private async UniTaskVoid JoinAsync()
        {
            try
            {
                // roomId 0 — single room. RoomManager rejects roomId < 0.
                await _flow.JoinServerDrivenAsync(_transport, _hostAddress, _port, roomId: 0, _sessionConfig, destroyCancellationToken);
            }
            catch (JoinFailedException jfe)
            {
                _logger?.KWarning($"JoinServerDrivenAsync failed: {jfe.Reason.ToName()}");
            }
            catch (System.OperationCanceledException)
            {
                // User-initiated cancel (stop button / ct) — not a failure.
            }
            finally
            {
                _joining = false;
            }
        }

        private void OnBtnReady()
        {
            if (_session == null) return;
            _hud.SetLocalReady(true);
            _session.SetReady(true);
        }

        // Stop intent. DetachAndStop drives session.Stop() → OnSessionStopping (UI reset, engine alive)
        // → OnSessionStopped (idempotent). Transport is intentionally NOT disconnected — it is reused
        // across sessions for PollEvents / JoinServerDrivenAsync. No re-entry guard: the framework owns idempotency.
        private void OnBtnStop()
        {
            _sessionDriver?.DetachAndStop();
        }

        // ── IKlothoSessionObserver — only OnSessionStopped is meaningful here; the rest are
        //    explicitly implemented (no reliance on default interface methods for IL2CPP). ──

        // Terminal teardown — framework calls this once on both game-initiated (DetachAndStop) and
        // framework-internal (match-end auto-shutdown) stops. Only the reference null-out remains.
        public void OnSessionStopped() => _session = null;

        public void OnPlayerDisconnected(IPlayerInfo player) { }
        public void OnPlayerReconnected(IPlayerInfo player) { }
        public void OnReconnecting() { }
        public void OnReconnectFailed(ReconnectRejectReason reason) { }
        public void OnReconnected() { }
        public void OnCatchupComplete() { }
        public void OnResyncCompleted(int tick) { }
        public void OnGameStart() { }
        public void OnMatchAborted(AbortReason reason) { }
        public void OnMatchEnded(int tick, IMatchEndEvent endEvt) { }
        public void OnMatchReset(ResetReason reason) { }

        public void OnPhaseChanged(SessionPhase p) => _hud.SetPhase(p);
        public void OnStateChanged(KlothoState s) { }
        public void OnPlayerCountChanged(int n) { }
        public void OnAllPlayersReadyChanged(bool v) { }

        private void OnApplicationQuit()
        {
            TeardownAll();
        }

        private void OnDestroy()
        {
            TeardownAll();
        }

        private void TeardownAll()
        {
            _flow?.DisposeConnect();

            if (_sessionDriver != null)
                _sessionDriver.PreSessionUpdate -= OnPreSessionUpdate;

            _input?.Disable();
            _input?.Dispose();
            // Main transport is owned by the driver — disconnected/unsubscribed in its OnDestroy.
        }
    }
}
