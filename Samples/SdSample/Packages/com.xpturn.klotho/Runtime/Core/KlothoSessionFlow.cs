using System;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Replay;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// 5-route session builder. Sync primitives only — UniTask wrappers live in
    /// <c>KlothoSessionFlowAsync</c> (Runtime.Unity).
    /// </summary>
    public sealed class KlothoSessionFlow
    {
        /// <summary>
        /// Fired once per session, immediately after <see cref="KlothoSession"/> is fully constructed
        /// and the framework-side lifecycle observer is subscribed. The game wires per-session state
        /// here: FaultInjection attach, view initialization, etc.
        ///
        /// Simulation callbacks that need the <see cref="Network.IKlothoNetworkService"/> handle should
        /// implement <see cref="INetworkServiceReceiver"/> — the framework dispatches automatically on
        /// host/guest entry just before the mode-specific callback fires.
        ///
        /// Exception policy (lifecycle transition, mirrors <c>KlothoSessionDriver.Stopping</c>):
        /// the framework logs the exception, calls <c>session.Stop()</c>, and rethrows — a half-wired
        /// session is torn down before the exception propagates.
        ///
        /// Distinct from <see cref="KlothoSession.OnSessionCreated"/> (static, editor-diagnostics scope).
        /// Both surfaces coexist by design.
        /// </summary>
        public event Action<KlothoSession> OnSessionCreated;

        /// <summary>Fired after <see cref="OnSessionCreated"/> when the session was built via <see cref="StartHost"/>.</summary>
        public event Action<KlothoSession> OnHostSessionCreated;

        /// <summary>Fired after <see cref="OnSessionCreated"/> when the session was built via <see cref="CreateForConnection"/> (Normal join or Reconnect).</summary>
        public event Action<KlothoSession> OnGuestSessionCreated;

        /// <summary>Fired after <see cref="OnSessionCreated"/> when the session was built via <see cref="StartReplay"/>.</summary>
        public event Action<KlothoSession> OnReplaySessionCreated;

        /// <summary>Fired after <see cref="OnSessionCreated"/> when the spectator session is ready.</summary>
        public event Action<KlothoSession> OnSpectatorSessionCreated;

        private readonly KlothoFlowSetup _setup;

        public KlothoSessionFlow(KlothoFlowSetup setup)
        {
            _setup = setup ?? throw new ArgumentNullException(nameof(setup));
            if (setup.CallbacksFactory == null)
                throw new ArgumentException("KlothoFlowSetup.CallbacksFactory is required.", nameof(setup));
        }

        // ── Host ──

        public KlothoSession StartHost(ISimulationConfig simulationConfig, ISessionConfig sessionConfig)
        {
            if (simulationConfig == null) throw new ArgumentNullException(nameof(simulationConfig));
            if (sessionConfig == null)    throw new ArgumentNullException(nameof(sessionConfig));

            var callbacks = _setup.CallbacksFactory(simulationConfig, sessionConfig);
            var session = KlothoSession.Create(new KlothoSessionSetup
            {
                Logger = _setup.Logger,
                Transport = _setup.Transport,
                AssetRegistry = _setup.AssetRegistry,
                LifecycleObserver = _setup.LifecycleObserver,
                SimulationCallbacks = callbacks.Simulation,
                ViewCallbacks = callbacks.View,
                SimulationConfig = simulationConfig,
                SessionConfig = sessionConfig,
            });
            FireOnSessionCreated(session, SessionEntryKind.Host);
            return session;
        }

        /// <summary>
        /// Creates a host session, activates the host role, and starts listening — in the correct order,
        /// with automatic teardown on any failure after the session is created. Folds the StartHost +
        /// HostGame + Transport.Listen sequence into a single entry point so callers never orphan a
        /// half-started session.
        ///
        /// Outcomes:
        ///   • success            → returns the running session.
        ///   • listen bind failed → tears the session down and returns null (expected, recoverable).
        ///   • any other failure  → tears the session down, then rethrows (e.g. HostGame/CreateRoom).
        /// In every non-success case the session is already torn down before this method returns/throws.
        /// </summary>
        public KlothoSession StartHostAndListen(
            ISimulationConfig simulationConfig,
            ISessionConfig    sessionConfig,
            string            roomName,
            string            address,
            int               port)
        {
            if (_setup.Transport == null)
                throw new InvalidOperationException("KlothoFlowSetup.Transport is required for StartHostAndListen.");

            var session = StartHost(simulationConfig, sessionConfig);
            try
            {
                session.HostGame(roomName, sessionConfig.MaxPlayers);
                if (!_setup.Transport.Listen(address, port, sessionConfig.MaxPlayers))
                {
                    _setup.Logger?.KError($"Host listen failed on {address}:{port} — tearing down session.");
                    session.Stop();
                    return null;
                }
            }
            catch
            {
                // Tear the half-started session down so callers never orphan it; swallow any
                // secondary teardown error so the original exception is the one that propagates.
                try { session.Stop(); }
                catch (Exception stopEx) { _setup.Logger?.KError(stopEx, $"Teardown after host-bootstrap failure threw"); }
                throw;
            }
            return session;
        }

        // ── Replay ──

        public KlothoSession StartReplay(IReplayData replayData, ISimulationConfig simulationConfig)
        {
            if (replayData == null)       throw new ArgumentNullException(nameof(replayData));
            if (simulationConfig == null) throw new ArgumentNullException(nameof(simulationConfig));

            // Replay does not consult sessionCfg; the recorded engine state carries the snapshot.
            // CallbacksFactory receives sessionCfg = null — the game ignores it in replay mode.
            var callbacks = _setup.CallbacksFactory(simulationConfig, null);
            var session = KlothoSession.Create(new KlothoSessionSetup
            {
                Logger = _setup.Logger,
                Transport = _setup.Transport,
                AssetRegistry = _setup.AssetRegistry,
                SimulationCallbacks = callbacks.Simulation,
                ViewCallbacks = callbacks.View,
                SimulationConfig = simulationConfig,
            });
            session.Engine.StartReplay(replayData);
            FireOnSessionCreated(session, SessionEntryKind.Replay);
            return session;
        }

        /// <summary>
        /// Loads a replay file, validates its metadata-derived SimulationConfig, and starts a replay session.
        /// Equivalent to <c>StartReplay(loader.CurrentReplayData, replayData.Metadata.ToSimulationConfig())</c>
        /// with internal validation. Throws <see cref="ReplayLoadException"/> on file-not-found,
        /// file-read I/O, malformed payload, or incompatible metadata. <see cref="ArgumentException"/>
        /// from <c>simConfig.Validate</c> is wrapped as <see cref="ReplayLoadException"/> so callers
        /// catch a single exception type across all replay-load failures.
        /// </summary>
        public KlothoSession StartReplayFromFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("Replay file path is required.", nameof(filePath));

            var loader = new ReplaySystem(new CommandFactory(), _setup.Logger);
            loader.LoadFromFile(filePath);
            var replayData = loader.CurrentReplayData
                ?? throw new ReplayLoadException($"Replay load produced null data: {filePath}");

            var simConfig = replayData.Metadata.ToSimulationConfig();
            try
            {
                simConfig.Validate();
            }
            catch (ArgumentException e)
            {
                throw new ReplayLoadException($"Replay metadata is invalid: {filePath}", e);
            }

            return StartReplay(replayData, simConfig);
        }

        // ── Guest (sync primitive — async wrappers in KlothoSessionFlowAsync) ──

        /// <summary>
        /// Builds a guest session from a completed <see cref="ConnectionResult"/>.
        /// <paramref name="sessionConfigSeed"/> is used only for Normal join (it is ignored by
        /// <see cref="KlothoSession.Create"/> on LateJoin / Reconnect, which read AcceptMessage
        /// payloads). Pass the local Inspector SessionConfig as seed.
        /// </summary>
        public KlothoSession CreateForConnection(
            ConnectionResult result, int roomId, ISessionConfig sessionConfigSeed)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            var callbacks = _setup.CallbacksFactory(result.SimulationConfig, sessionConfigSeed);
            var session = KlothoSession.Create(new KlothoSessionSetup
            {
                Logger = _setup.Logger,
                Connection = result,
                AssetRegistry = _setup.AssetRegistry,
                LifecycleObserver = _setup.LifecycleObserver,
                SimulationCallbacks = callbacks.Simulation,
                ViewCallbacks = callbacks.View,
                RoomId = roomId,
                SessionConfig = sessionConfigSeed,
                CredentialsStore = _setup.CredentialsStore,
                AppVersion = _setup.AppVersion,
                DeviceIdProvider = _setup.DeviceIdProvider,
            });
            FireOnSessionCreated(session, SessionEntryKind.Guest);
            return session;
        }

        // ── Spectator wiring (Core hook called from KlothoSessionFlowAsync.SpectateAsync) ──

        // Builds a SpectatorSessionSetup that delegates CallbacksFactory to KlothoFlowSetup
        // and pre-stamps the rest from _setup. The async wrapper drives the pump.
        internal SpectatorSessionSetup BuildSpectatorSetup(
            INetworkTransport spectatorTransport, string hostAddress, int port, int roomId)
        {
            return new SpectatorSessionSetup
            {
                Logger = _setup.Logger,
                AssetRegistry = _setup.AssetRegistry,
                Transport = spectatorTransport,
                HostAddress = hostAddress,
                Port = port,
                RoomId = roomId,
                LifecycleObserver = _setup.LifecycleObserver,
                CallbacksFactory = (simCfg, sessionCfg) =>
                {
                    var cb = _setup.CallbacksFactory(simCfg, sessionCfg);
                    return new SpectatorCallbacks(cb.Simulation, cb.View);
                },
            };
        }

        // Spectator path fires OnSessionCreated after FinishSpectatorBootstrap (Engine non-null).
        // KlothoSessionFlowAsync.SpectateAsync invokes this once the spectator session is ready.
        internal void FireOnSessionCreatedForSpectator(KlothoSession session)
            => FireOnSessionCreated(session, SessionEntryKind.Spectator);

        // Read accessors for the async wrapper (KlothoSessionFlowAsync). Internal because the
        // game already holds these references directly — the async layer needs them only because
        // KlothoConnectionAsync takes them as explicit handshake parameters.
        internal IKLogger Logger => _setup.Logger;
        internal IDeviceIdProvider DeviceIdProvider => _setup.DeviceIdProvider;
        internal Func<INetworkTransport> SpectatorTransportFactory => _setup.SpectatorTransportFactory;

        // ── Shared ──

        private void FireOnSessionCreated(KlothoSession session, SessionEntryKind kind)
        {
            try
            {
                OnSessionCreated?.Invoke(session);

                // Auto-inject network service to opt-in callbacks on host/guest entry. Replay/spectator
                // paths skip via the kind gate (replay's NetworkService is non-null but inputless; the
                // null check is a defensive second gate).
                if ((kind == SessionEntryKind.Host || kind == SessionEntryKind.Guest)
                    && session.NetworkService != null
                    && session.SimulationCallbacks is INetworkServiceReceiver recv)
                {
                    recv.SetNetworkService(session.NetworkService);
                }

                // Auto-send PlayerConfig on guest entry paths only (Normal / LateJoin / Reconnect
                // through CreateForConnection). Host is excluded because IsHost / LocalPlayerId are
                // not assigned until HostGame -> NetworkService.CreateRoom runs, which happens after
                // this point. Replay / Spectator have no NetworkService.
                if (kind == SessionEntryKind.Guest
                    && _setup.InitialPlayerConfigFactory != null
                    && session.NetworkService != null)
                {
                    var cfg = _setup.InitialPlayerConfigFactory.Invoke();
                    if (cfg != null)
                        session.SendPlayerConfig(cfg);
                }

                switch (kind)
                {
                    case SessionEntryKind.Host:      OnHostSessionCreated?.Invoke(session); break;
                    case SessionEntryKind.Guest:     OnGuestSessionCreated?.Invoke(session); break;
                    case SessionEntryKind.Replay:    OnReplaySessionCreated?.Invoke(session); break;
                    case SessionEntryKind.Spectator: OnSpectatorSessionCreated?.Invoke(session); break;
                    default: throw new ArgumentOutOfRangeException(nameof(kind));
                }
            }
            catch (Exception e)
            {
                _setup.Logger?.KError(e, $"[KlothoSessionFlow] OnSessionCreated subscriber threw — stopping session");
                session.Stop();
                throw;
            }
        }

        internal enum SessionEntryKind { Host, Guest, Replay, Spectator }
    }
}
