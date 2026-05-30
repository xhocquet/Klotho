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
    public class SdGameController : MonoBehaviour
    {
        const string ConnectionKey = "xpTURN.SdSample";

        [Header("Logging")]
        [SerializeField] private KLogLevel _logLevel = KLogLevel.Information;

        [Header("Network")]
        [SerializeField] private string _hostAddress = "localhost";
        [SerializeField] private int _port = 7777;

        [Header("Configs")]
        [SerializeField] private USimulationConfig _simulationConfig;
        [SerializeField] private USessionConfig _sessionConfig;
        [SerializeField] private TextAsset _dataAsset;

        [Header("Scene References")]
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
        private CancellationTokenSource _connectCts;
        private bool _joining;

        private void Awake()
        {
            _logger = KlothoLogger.CreateDefault(level: _logLevel, filePrefix: "Client", categoryName: "Client");
            _logger?.KInformation($"SdSample logger started.");

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

            _input = new SdInputCapture();
            _input.Enable();

            _flow = new KlothoSessionFlow(new KlothoFlowSetup
            {
                Logger           = _logger,
                Transport        = _transport,
                AssetRegistry    = _assetRegistry,
                AppVersion       = Application.version,
                DeviceIdProvider = new UnityDeviceIdProvider(),
                CallbacksFactory = (simCfg, sessCfg) =>
                {
                    var simCallbacks = new SdSimulationCallbacks(_input);
                    _viewCallbacks = new SdViewCallbacks(_hud);
                    return new SessionCallbacks(simCallbacks, _viewCallbacks);
                },
            });
            _flow.OnSessionCreated += OnFlowSessionCreated;

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
            _session = session;
            _sessionDriver.Attach(session);
            _entityViewUpdater?.Initialize(session.Engine);

            session.PhaseChanged += p => _hud.SetPhase(p);
            // Push initial value — event may have fired before subscription.
            _hud.SetPhase(session.NetworkService?.Phase ?? SessionPhase.None);

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
            _connectCts?.Dispose();
            _connectCts = new CancellationTokenSource();
            try
            {
                // roomId 0 — single room. RoomManager rejects roomId < 0.
                await _flow.JoinServerDrivenAsync(_transport, _hostAddress, _port, roomId: 0, _sessionConfig, _connectCts.Token);
            }
            catch (System.Exception ex)
            {
                _logger?.KWarning($"JoinServerDrivenAsync failed: {ex.Message}");
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
            _sessionDriver?.DetachAndStop();
            _session = null;
        }

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
