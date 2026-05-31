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

namespace xpTURN.Samples.P2pSample
{
    [DefaultExecutionOrder(-100)]
    public class P2pGameController : MonoBehaviour, IKlothoSessionObserver
    {
        const string ConnectionKey = "xpTURN.P2pSample";

        [Header("Logging")]
        [SerializeField] private KLogLevel _logLevel = KLogLevel.Information;

        [Header("Network")]
        [SerializeField] private string _hostAddress = "localhost";
        [SerializeField] private int _port = 777;

        [Header("Configs")]
        [SerializeField] private USimulationConfig _simulationConfig;
        [SerializeField] private USessionConfig _sessionConfig;
        [SerializeField] private TextAsset _dataAsset;

        [Header("Scene References")]
        [SerializeField] private KlothoSessionDriver _sessionDriver;
        [SerializeField] private EntityViewUpdater _entityViewUpdater;
        [SerializeField] private P2pHud _hud;
        [SerializeField] private P2pMenu _menu;

        private IKLogger _logger;
        private IDataAssetRegistry _assetRegistry;
        private LiteNetLibTransport _transport;
        private P2pInputCapture _input;
        private KlothoSessionFlow _flow;
        private KlothoSession _session;
        private P2pViewCallbacks _viewCallbacks;
        private CancellationTokenSource _connectCts;
        private bool _joining;

        private void Awake()
        {
            _logger = KlothoLogger.CreateDefault(level: _logLevel, filePrefix: "Client", categoryName: "Client");
            _logger?.KInformation($"P2pSample logger started.");

            _sessionDriver.PreSessionUpdate += OnPreSessionUpdate;
            _sessionDriver.Stopping        += OnSessionDriverStopping;
            _sessionDriver.IdlePoll        += OnIdlePoll;
        }

        private void Start()
        {
            var assets = DataAssetReader.LoadMixedCollectionFromBytes(_dataAsset.bytes);
            IDataAssetRegistryBuilder builder = new DataAssetRegistry();
            builder.RegisterRange(assets);
            _assetRegistry = builder.Build();

            _transport = new LiteNetLibTransport(_logger, levels: null, connectionKey: ConnectionKey);
            _transport.OnDisconnected += OnTransportDisconnected;

            _input = new P2pInputCapture();
            _input.Enable();

            _flow = new KlothoSessionFlow(new KlothoFlowSetup
            {
                Logger            = _logger,
                Transport         = _transport,
                AssetRegistry     = _assetRegistry,
                AppVersion        = Application.version,
                DeviceIdProvider  = new UnityDeviceIdProvider(),
                LifecycleObserver = this,
                CallbacksFactory  = (simCfg, sessCfg) =>
                {
                    var simCallbacks = new P2pSimulationCallbacks(_input);
                    _viewCallbacks = new P2pViewCallbacks(_hud);
                    return new SessionCallbacks(simCallbacks, _viewCallbacks);
                },
            });
            _flow.OnSessionCreated += OnFlowSessionCreated;

            _menu.SetInitialHost(_hostAddress, _port);
            _menu.OnHostClicked  += OnBtnHost;
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

        private void OnIdlePoll()
        {
            _transport?.PollEvents();
        }

        private void OnSessionDriverStopping(KlothoSession session)
        {
            _viewCallbacks?.Cleanup();
            _entityViewUpdater?.Cleanup();
            _menu.SetReadyEnabled(false);
            _menu.SetStopEnabled(false);
            _hud.SetLocalReady(false);
        }

        private void OnFlowSessionCreated(KlothoSession session)
        {
            // _session is sourced from the entry-method return value (OnBtnHost / JoinAsync);
            // this callback is wiring-only.
            _sessionDriver.Attach(session);
            _entityViewUpdater?.Initialize(session.Engine);

            session.PhaseChanged += p => _hud.SetPhase(p);
            // Push initial value — event may have fired before subscription.
            _hud.SetPhase(session.NetworkService?.Phase ?? SessionPhase.None);

            _menu.SetReadyEnabled(true);
            _menu.SetStopEnabled(true);
        }

        private void OnBtnHost()
        {
            if (_session != null) return;
            _hostAddress = _menu.Host;
            _port = _menu.Port;
            _session = _flow.StartHostAndListen(_simulationConfig, _sessionConfig, "Game", _hostAddress, _port);
            if (_session == null) return;   // listen failure: framework already tore the session down (OnSessionStopped)
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
            _connectCts?.Dispose();
            _connectCts = new CancellationTokenSource();
            try
            {
                _session = await _flow.JoinP2PAsync(_transport, _hostAddress, _port, _sessionConfig, _connectCts.Token);
            }
            catch (System.Exception ex)
            {
                _logger?.KWarning($"JoinP2PAsync failed: {ex.Message}");
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

        private void OnBtnStop()
        {
            StopGame();
        }

        // Single teardown convergence point — driven by the stop button (path 1) and by
        // session.Stop() via OnSessionStopped (path 2, e.g. StartHostAndListen self-teardown).
        private void StopGame()
        {
            // Re-entry guard: when DetachAndStop is already in flight (path 1), OnSessionStopped
            // re-enters here with IsStopping == true — skip the duplicate teardown.
            if (_sessionDriver != null && _sessionDriver.IsStopping) return;

            // DetachAndStop fires the Stopping hook (OnSessionDriverStopping → UI reset) then
            // session.Stop() (idempotent). Transport is intentionally NOT disconnected — it is
            // reused across sessions for PollEvents / JoinP2PAsync.
            _sessionDriver?.DetachAndStop();
            _session = null;
        }

        // ── IKlothoSessionObserver — only OnSessionStopped is meaningful here; the rest are
        //    explicitly implemented (no reliance on default interface methods for IL2CPP). ──

        public void OnSessionStopped() => StopGame();

        public void OnPlayerDisconnected(IPlayerInfo player) { }
        public void OnPlayerReconnected(IPlayerInfo player) { }
        public void OnReconnecting() { }
        public void OnReconnectFailed(byte reason) { }
        public void OnReconnected() { }
        public void OnCatchupComplete() { }
        public void OnResyncCompleted(int tick) { }
        public void OnGameStart() { }
        public void OnMatchAborted(AbortReason reason) { }
        public void OnMatchEnded(int tick, IMatchEndEvent endEvt) { }
        public void OnMatchReset(ResetReason reason) { }

        private void OnTransportDisconnected(DisconnectReason reason)
        {
            _logger?.KInformation($"Transport disconnected: {reason}");
        }

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
            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = null;

            if (_sessionDriver != null)
            {
                _sessionDriver.PreSessionUpdate -= OnPreSessionUpdate;
                _sessionDriver.Stopping        -= OnSessionDriverStopping;
                _sessionDriver.IdlePoll        -= OnIdlePoll;
            }

            _input?.Disable();
            _input?.Dispose();

            if (_transport != null)
            {
                _transport.OnDisconnected -= OnTransportDisconnected;
            }
        }
    }
}
