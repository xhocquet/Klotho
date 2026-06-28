using System;
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
using xpTURN.Klotho.Samples.Identity.Sd;

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

        [field: Header("Dev Lobby (KLOTHO_DEV_LOBBY only)")]
        [SerializeField] private string _lobbyAddress = "localhost";
        [SerializeField] private int _lobbyPort = 9999;
        [SerializeField] private string _devNickname = "Player";
        [SerializeField] private string _devMatchId = SdDevIdentity.DevMatchId; // distinct per match → distinct rooms (multi-room demo)

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
#if KLOTHO_DEV_LOBBY
        private SdLobbyIssueProvider _identityProvider;
        private string _devAccount;
#endif

        private void Awake()
        {
            _logger = KlothoLogger.CreateDefault(level: _logLevel, filePrefix: "Client", categoryName: "Client");
            _logger?.KInformation($"SdSample logger started : LogLevel={_logLevel}");

            _sessionDriver.PreSessionUpdate += OnPreSessionUpdate;
        }

        private void Start()
        {
            var assets = DataAssetReader.LoadMixedCollectionFromBytes(_dataAsset.bytes);
            IDataAssetRegistryBuilder registryBuilder = new DataAssetRegistry();
            registryBuilder.RegisterRange(assets);
            _assetRegistry = registryBuilder.Build();

            _transport = new LiteNetLibTransport(_logger, levels: null, connectionKey: ConnectionKey);

            _input = new SdInputCapture();
            _input.Enable();

            var builder = new KlothoFlowSetupBuilder((simCfg, sessCfg) =>
                {
                    var simCallbacks = new SdSimulationCallbacks(_input);
                    _viewCallbacks = new SdViewCallbacks(_hud);
                    return new SessionCallbacks(simCallbacks, _viewCallbacks);
                })
                .WithLogger(_logger)
                .WithTransport(_transport)
                .WithAssetRegistry(_assetRegistry)
                .WithLifecycleObserver(this)
                .WithUnityDefaults();

#if KLOTHO_DEV_LOBBY
            // Dev lobby identity (SD): the client fetches a signed ticket from DevLobbyServer on Join and
            // carries it via this mutable provider. Opt-in via KLOTHO_DEV_LOBBY — when undefined the client
            // joins with no lobby ticket (matching servers whose validator is likewise un-gated). The
            // server validator must agree (define KLOTHO_DEV_LOBBY on both sides). The validator is server-side.
            _devAccount = "dev-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            _identityProvider = new SdLobbyIssueProvider();
            builder = builder.WithLobbyIdentity(_identityProvider);
#endif

            _flow = new KlothoSessionFlow(builder.Build());
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
#if KLOTHO_DEV_LOBBY
                // Fetch the signed ticket + room assignment from the dev lobby BEFORE connecting; connect to
                // the lobby-assigned endpoint + roomId (not hardcoded). GetTicket() during the handshake
                // returns the pre-fetched ticket.
                var issue = await TryFetchLobbyAsync(destroyCancellationToken);
                if (!issue.HasValue) return; // Full / timeout / decline — abort (see TryFetchLobbyAsync log)
                var asn = issue.Value;
                await _flow.JoinServerDrivenAsync(_transport, asn.Host, asn.Port, asn.RoomId, _sessionConfig, destroyCancellationToken);
#else
                // roomId 0 — single room. RoomManager rejects roomId < 0.
                await _flow.JoinServerDrivenAsync(_transport, _hostAddress, _port, roomId: 0, _sessionConfig, destroyCancellationToken);
#endif
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

#if KLOTHO_DEV_LOBBY
        // Fetches a lobby ticket + room assignment over LiteNetLib and stores the ticket in the provider.
        // Returns the assignment (Ok) or null (abort join) on Full / timeout / decline — start DevLobbyServer first.
        private async UniTask<IssueResult?> TryFetchLobbyAsync(CancellationToken ct)
        {
            using var issueClient = new LiteNetLibLobbyIssueClient(_logger, _lobbyAddress, _lobbyPort);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(5000);
            try
            {
                // Menu field wins; else the inspector default. Distinct match ids land in distinct rooms.
                string matchId = (_menu != null && !string.IsNullOrWhiteSpace(_menu.MatchId)) ? _menu.MatchId.Trim() : _devMatchId;
                IssueResult issue = await issueClient.IssueAsync(_devAccount, _devNickname, matchId, cts.Token);
                if (issue.Full)
                {
                    _logger?.KWarning($"[SdSample] lobby FULL (all rooms occupied) — retry later.");
                    return null;
                }
                if (!issue.Ok || string.IsNullOrEmpty(issue.Ticket))
                {
                    _logger?.KWarning($"[SdSample] lobby declined (empty ticket) — aborting join.");
                    return null;
                }
                _identityProvider?.SetTicket(issue.Ticket);
                _logger?.KInformation($"[SdSample] lobby assigned {issue.Host}:{issue.Port} room={issue.RoomId} ({_devAccount}).");
                return issue;
            }
            catch (OperationCanceledException)
            {
                _logger?.KWarning($"[SdSample] lobby fetch timed out — is DevLobbyServer running?");
                return null;
            }
        }
#endif

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
