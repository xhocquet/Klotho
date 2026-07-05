// GodotSdSample bootstrap — standalone Server-Driven client (counterpart of the Unity SdGameController).
// One instance = one client. The menu drives Join/Ready/Stop on a single session. A GodotSessionDriver
// pumps the transport + session each frame; the EntityViewUpdaterNode self-drives view interpolation;
// the join goes through GodotSessionFlowAsync; views are pooled. The dedicated server is owned under
// Server/ — run it first (dotnet run --project Samples/GodotSdSample/Server -- 7777), then run two clients.
using System;
using System.Threading;
using System.Threading.Tasks;
using global::Godot;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Godot;
using xpTURN.Klotho.LiteNetLib;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Samples.Identity.Sd;

namespace xpTURN.Samples.SdSample
{
	public partial class SdGameNode : Node
	{
		private const string ConnectionKey = "xpTURN.SdSample";
		private const int    RoomId        = 0;

		private IKLogger            _logger;
		private IDataAssetRegistry  _registry;
		private LiteNetLibTransport _transport;
		private SdInputCapture      _input;
		private KlothoSessionFlow   _flow;
		private KlothoSession       _session;
		private GodotSdViewCallbacks _viewCallbacks;
		private EntityViewUpdaterNode _view;
		private SdEntityViewFactory   _factory;
		private DefaultGodotEntityViewPool _pool;
		private GodotSessionDriver    _driver;
		private GodotSdMenu _menu;
		private GodotSdHud  _hud;

		private ISimulationConfig _simCfg;
		private ISessionConfig    _sesCfg;
		private Task<KlothoSession> _joinTask;
		private bool _joining;
#if KLOTHO_DEV_LOBBY
		// Dev lobby identity (SD): fetch a signed ticket from DevLobbyServer, then connect (issue-then-connect).
		private const string LobbyHost = "127.0.0.1";
		private const int    LobbyPort = 9999;
		private SdLobbyIssueProvider _identityProvider;
		private string _devAccount;
		private string _devMatchId = SdDevIdentity.DevMatchId; // CLI: match=<id> → distinct rooms (multi-room demo)
		private LiteNetLibLobbyIssueClient _issueClient;
		private CancellationTokenSource _issueCts;
		private Task<IssueResult> _issueTask;
		private bool _issuing;
#endif

		// Headless self-test hook (CLI: -- join). false=interactive (menu-driven).
		private bool _autoJoin;
		private bool _autoReadySent;
		private bool _verified;
		private const int VerifyTick = 120;

		public override void _Ready()
		{
			WarmupRegistry.RunAll();

			_logger   = CreateLogger();
			_registry = LoadAssetRegistry();
			_simCfg   = new SimulationConfig();
			// Server is authoritative for session/sim config; this seed only needs MaxPlayers/MinPlayers
			// to match for local view/HUD. Countdown/timing come from the server.
			_sesCfg   = new SessionConfig { MaxPlayers = 2, MinPlayers = 2, CountdownDurationMs = 3000 };
			_input    = new SdInputCapture();
			_transport = new LiteNetLibTransport(_logger, connectionKey: ConnectionKey);

			_menu = GetNode<GodotSdMenu>("UILayer/Menu");
			_hud  = GetNode<GodotSdHud>("UILayer/Hud");
			_viewCallbacks = new GodotSdViewCallbacks(_hud);

			var fsb = new KlothoFlowSetupBuilder((s, ss) =>
						new SessionCallbacks(new SdSimulationCallbacks(_input), _viewCallbacks))
					.WithLogger(_logger)
					.WithTransport(_transport)
					.WithAssetRegistry(_registry)
					.WithGodotDefaults();
#if KLOTHO_DEV_LOBBY
			_devAccount = "dev-" + Guid.NewGuid().ToString("N").Substring(0, 8);
			_identityProvider = new SdLobbyIssueProvider();
			fsb = fsb.WithLobbyIdentity(_identityProvider);
#endif
			_flow = new KlothoSessionFlow(fsb.Build());

			var playerScene = GD.Load<PackedScene>("res://player.tscn");
			_factory = new SdEntityViewFactory(playerScene);
			_pool = new DefaultGodotEntityViewPool();
			_pool.Prewarm(playerScene, _sesCfg.MaxPlayers);
			_view = new EntityViewUpdaterNode();
			AddChild(_view);

			_driver = new GodotSessionDriver();
			AddChild(_driver);
			_driver.BindTransport(_transport);
			_driver.PreSessionUpdate += (s, dt) => { if (s.State == KlothoState.Running) _input.CaptureInput(); };

			_menu.OnJoinClicked  += OnJoin;
			_menu.OnReadyClicked += OnReady;
			_menu.OnStopClicked  += OnStop;
			_menu.SetInitialHost("127.0.0.1", 7777);
			_menu.SetReadyEnabled(false);
			_menu.SetStopEnabled(false);

			SetupView3D();

			foreach (var a in OS.GetCmdlineUserArgs())
			{
#if KLOTHO_DEV_LOBBY
				if (a.StartsWith("match=", StringComparison.Ordinal)) _devMatchId = a.Substring("match=".Length);
#endif
			}
			foreach (var a in OS.GetCmdlineUserArgs())
			{
				if (a == "join") { _autoJoin = true; OnJoin(); }
			}
		}

		// Server-Driven join via GodotSessionFlowAsync (ConnectAsync with a RoomHandshakeMessage pre-join,
		// then CreateForConnection(roomId)). The async result is resolved in _Process.
		private void OnJoin()
		{
			if (_session != null || _joining) return;
#if KLOTHO_DEV_LOBBY
			// Fetch the ticket first; the join starts once it resolves (in _Process, keeping
			// JoinServerDrivenAsync on the main thread).
			if (_issuing) return;
			_issuing = true;
			_issueClient = new LiteNetLibLobbyIssueClient(_logger, LobbyHost, LobbyPort);
			_issueCts = new CancellationTokenSource(5000);
			// Menu field wins; else the CLI match=<id> / default. Distinct match ids land in distinct rooms.
			string matchId = string.IsNullOrWhiteSpace(_menu.MatchId) ? _devMatchId : _menu.MatchId.Trim();
			_issueTask = _issueClient.IssueAsync(_devAccount, _devAccount, matchId, _issueCts.Token);
#else
			_joining = true;
			_joinTask = _flow.JoinServerDrivenAsync(_transport, _menu.Host, _menu.Port, RoomId, _sesCfg);
#endif
		}

		private void OnReady()
		{
			if (_session == null) return;
			_hud.SetLocalReady(true);
			_session.SetReady(true);
		}

		private void OnStop()
		{
			if (_session == null) return;
			_driver.DetachAndStop();
			_view.Cleanup();
			_viewCallbacks.Cleanup();
			_session = null;
			_menu.SetReadyEnabled(false);
			_menu.SetStopEnabled(false);
			_hud.SetLocalReady(false);
		}

		private void OnSessionReady()
		{
			_view.Initialize(_session.Engine, _factory, _pool);
			_driver.Attach(_session);
			_hud.SetPhase(_session.Phase);
			_menu.SetReadyEnabled(true);
			_menu.SetStopEnabled(true);
		}

		// The driver pumps transport + session; the view updater self-drives interpolation. This node only
		// resolves the async join, mirrors the phase to the HUD, and runs the headless self-test.
		public override void _Process(double delta)
		{
			if (_transport == null) return; // _Ready failed to initialize.

#if KLOTHO_DEV_LOBBY
			// Resolve the lobby Issue task on the main thread, then start the join.
			if (_issuing && _issueTask != null)
			{
				if (_issueTask.IsFaulted || _issueTask.IsCanceled)
				{
					_logger.KError($"[Sd] lobby ticket fetch failed — is DevLobbyServer running?");
					CleanupIssue();
					if (_autoJoin && DisplayServer.GetName() == "headless") GetTree().Quit(1);
				}
				else if (_issueTask.IsCompleted)
				{
					var issue = _issueTask.Result;
					if (issue.Full)
					{
						// Transient: all rooms occupied. Interactive → user may retry; headless → exit code 2.
						_logger.KWarning($"[Sd] lobby FULL (all rooms occupied) — retry later.");
						CleanupIssue();
						if (_autoJoin && DisplayServer.GetName() == "headless") GetTree().Quit(2);
					}
					else
					{
						// Connect to the lobby-assigned dedi endpoint + roomId (not hardcoded).
						_identityProvider?.SetTicket(issue.Ticket);
						CleanupIssue();
						_joining = true;
						_joinTask = _flow.JoinServerDrivenAsync(_transport, issue.Host, issue.Port, issue.RoomId, _sesCfg);
					}
				}
			}
#endif

			if (_joining && _joinTask != null)
			{
				if (_joinTask.IsFaulted)
				{
					_logger.KError($"[Sd] join failed (server running?): {_joinTask.Exception?.GetBaseException().Message}");
					_joining = false; _joinTask = null;
					if (_autoJoin && DisplayServer.GetName() == "headless") GetTree().Quit(1);
				}
				else if (_joinTask.IsCompleted)
				{
					_session = _joinTask.Result;
					_joining = false; _joinTask = null;
					OnSessionReady();
				}
			}

			if (_session == null) return;

			_hud.SetPhase(_session.Phase);

			if (_autoJoin) AutoTestStep();
		}

		// Headless self-test: auto-Ready once synchronized, then verify view nodes and quit.
		private void AutoTestStep()
		{
			if (!_autoReadySent && _session.Phase == SessionPhase.Synchronized)
			{
				OnReady();
				_autoReadySent = true;
				_logger.KInformation($"[Sd] auto-join ready sent.");
			}

			if (!_verified && _session.State == KlothoState.Running && _session.Engine.CurrentTick >= VerifyTick)
			{
				_verified = true;
				int n = _view.GetChildCount();
				_logger.KInformation($"[Sd] auto-join tick={_session.Engine.CurrentTick} viewNodes={n}");
				if (n >= 1) _logger.KInformation($"=== SD STANDALONE OK ===");
				else        _logger.KError($"=== SD STANDALONE FAILED (viewNodes={n}) ===");
				if (DisplayServer.GetName() == "headless") GetTree().Quit(n >= 1 ? 0 : 1);
			}
		}

#if KLOTHO_DEV_LOBBY
		private void CleanupIssue()
		{
			_issuing = false;
			_issueTask = null;
			_issueClient?.Dispose(); _issueClient = null;
			_issueCts?.Dispose(); _issueCts = null;
		}
#endif

		private static IKLogger CreateLogger()
			=> GodotKlothoLogger.CreateDefault(filePrefix: "Sd", categoryName: "Sd", timestampFormat: "HH:mm:ss.fff");

		// Configure the 3D view in code (LookAt avoids hand-written, easy-to-mistake basis matrices in .tscn).
		private void SetupView3D()
		{
			// Top-down: camera high above the origin looking straight down; screen-up = world -Z.
			var cam = GetNodeOrNull<Camera3D>("Camera3D");
			if (cam != null)
			{
				cam.LookAtFromPosition(new Vector3(0, 7, 0), Vector3.Zero, new Vector3(0, 0, -1));
				cam.Environment = new global::Godot.Environment
				{
					BackgroundMode      = global::Godot.Environment.BGMode.Color,
					BackgroundColor     = new Color(0.12f, 0.13f, 0.18f),
					AmbientLightSource  = global::Godot.Environment.AmbientSource.Color,
					AmbientLightColor   = new Color(0.5f, 0.5f, 0.5f),
					AmbientLightEnergy  = 1.0f,
				};
			}

			var light = GetNodeOrNull<DirectionalLight3D>("DirectionalLight3D");
			light?.LookAtFromPosition(new Vector3(4, 10, 4), Vector3.Zero, Vector3.Up);
		}

		private IDataAssetRegistry LoadAssetRegistry()
		{
			// Self-contained: load the copied SdAssets.bytes from res://Data. Use Godot FileAccess so it
			// works both in the editor and in an exported .pck (System.IO cannot read res:// inside a .pck).
			byte[] bytes = global::Godot.FileAccess.GetFileAsBytes("res://Data/SdAssets.bytes");
			if (bytes == null || bytes.Length == 0)
			{
				var err = global::Godot.FileAccess.GetOpenError();
				throw new System.IO.FileNotFoundException($"res://Data/SdAssets.bytes not found (err={err})");
			}
			var assets = DataAssetReader.LoadMixedCollectionFromBytes(bytes);
			IDataAssetRegistryBuilder builder = new DataAssetRegistry();
			builder.RegisterRange(assets);
			return builder.Build();
		}

		public override void _ExitTree()
		{
			if (_session != null) { _driver?.DetachAndStop(); _session = null; }
			_view?.Cleanup();
			_viewCallbacks?.Cleanup();
			_pool?.Dispose();
			_input?.Dispose();
#if KLOTHO_DEV_LOBBY
			_issueClient?.Dispose();
			_issueCts?.Dispose();
#endif
		}
	}
}
