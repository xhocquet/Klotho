using System;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>Thrown when a flow setup fails build-time coherence validation.</summary>
    public sealed class FlowSetupValidationException : ArgumentException
    {
        public FlowSetupValidationException(string message) : base(message) { }
    }

    /// <summary>
    /// Fluent assembler for <see cref="KlothoFlowSetup"/>. Groups the route-agnostic dependencies
    /// into cohesive feature methods so related fields are set together, then validates feature
    /// coherence at Build(). The required CallbacksFactory is a constructor argument (compile-time).
    /// Object-initializer construction of KlothoFlowSetup remains supported as an escape hatch.
    /// Build() runs once at startup — not a per-frame path.
    /// </summary>
    public sealed class KlothoFlowSetupBuilder
    {
        private readonly KlothoFlowSetup _s = new KlothoFlowSetup();

        /// <param name="callbacksFactory">Required. Builds Sim/View callbacks per session-create.</param>
        public KlothoFlowSetupBuilder(Func<ISimulationConfig, ISessionConfig, SessionCallbacks> callbacksFactory)
        {
            _s.CallbacksFactory = callbacksFactory
                ?? throw new ArgumentNullException(nameof(callbacksFactory));
        }

        // ── Always-relevant dependencies ──
        public KlothoFlowSetupBuilder WithLogger(IKLogger logger)              { _s.Logger = logger; return this; }
        public KlothoFlowSetupBuilder WithTransport(INetworkTransport transport) { _s.Transport = transport; return this; }
        public KlothoFlowSetupBuilder WithAssetRegistry(IDataAssetRegistry registry) { _s.AssetRegistry = registry; return this; }
        public KlothoFlowSetupBuilder WithLifecycleObserver(IKlothoSessionObserver observer) { _s.LifecycleObserver = observer; return this; }

        // ── Replay save output (host / guest sessions; framework saves on Stop) ──
        // null path (default) disables saving — matches the always-relevant group's no-null-check style.
        public KlothoFlowSetupBuilder WithReplaySave(string path, bool dumpJson = false) { _s.ReplaySavePath = path; _s.ReplayDumpJson = dumpJson; return this; }

        // ── Client handshake identity (guest / reconnect entry points) ──
        public KlothoFlowSetupBuilder WithHandshake(string appVersion, IDeviceIdProvider deviceIdProvider)
        {
            _s.AppVersion       = appVersion       ?? throw new ArgumentNullException(nameof(appVersion));
            _s.DeviceIdProvider = deviceIdProvider ?? throw new ArgumentNullException(nameof(deviceIdProvider));
            return this;
        }

        // ── Reconnect credentials store (requires handshake — checked at Build) ──
        public KlothoFlowSetupBuilder WithReconnect(IReconnectCredentialsStore store)
        {
            _s.CredentialsStore = store ?? throw new ArgumentNullException(nameof(store));
            return this;
        }

        // ── Auto PlayerConfig send on guest / reconnect entry ──
        public KlothoFlowSetupBuilder WithAutoPlayerConfig(Func<PlayerConfigBase> factory)
        {
            _s.InitialPlayerConfigFactory = factory ?? throw new ArgumentNullException(nameof(factory));
            return this;
        }

        // ── Spectator no-transport overload support ──
        public KlothoFlowSetupBuilder WithSpectator(Func<INetworkTransport> transportFactory)
        {
            _s.SpectatorTransportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
            return this;
        }

        /// <summary>
        /// Validates feature coherence and returns the assembled setup.
        /// Hard errors (always throw):
        ///   • WithReconnect set without WithHandshake — reconnect credentials are produced by a prior
        ///     normal join, which requires handshake identity (the reconnect handshake itself uses only
        ///     the persisted credentials). A reconnect-only setup could never create the credentials it
        ///     reconnects with.
        /// Advisory (logged; strict=true promotes to throw) — best-effort:
        ///   • cannot drive any connect entry point: no Transport (host/replay), no handshake identity
        ///     (guest/reconnect), no SpectatorTransportFactory (spectator). Replay-from-file is exempt
        ///     (offline — needs neither), so a replay-only setup is intentionally not flagged.
        /// </summary>
        public KlothoFlowSetup Build(bool strict = false)
        {
            // AppVersion can only be null here if WithHandshake was never called (it sets both
            // atomically); the AppVersion check is defensive for the object-initializer escape hatch.
            if (_s.CredentialsStore != null && (_s.DeviceIdProvider == null || _s.AppVersion == null))
                throw new FlowSetupValidationException(
                    "WithReconnect requires WithHandshake (AppVersion + DeviceIdProvider) — reconnect credentials are minted by a prior normal join, which needs handshake identity.");

            // Spectator drives via SpectatorTransportFactory; replay-from-file needs neither — both exempt.
            if (_s.Transport == null && _s.DeviceIdProvider == null && _s.SpectatorTransportFactory == null)
            {
                const string msg = "Flow setup has no Transport (host/replay), no handshake identity (guest/reconnect), and no SpectatorTransportFactory — it can drive no connect entry point (replay-from-file is still possible).";
                if (strict) throw new FlowSetupValidationException(msg);
                _s.Logger?.KWarning($"[KlothoFlowSetupBuilder] {msg}");
            }
            return _s;
        }
    }
}
