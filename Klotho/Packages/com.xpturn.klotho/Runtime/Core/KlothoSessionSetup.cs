using xpTURN.Klotho.Logging;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Configuration required to create a KlothoSession (replaces KlothoSessionConfig).
    /// </summary>
    public class KlothoSessionSetup
    {
        // ── Dependencies ──

        public IKLogger Logger { get; set; }
        public ISimulationCallbacks SimulationCallbacks { get; set; }
        public IViewCallbacks ViewCallbacks { get; set; }

        // ── Connection ──

        /// <summary>
        /// Host: specify the transport directly.
        /// Guest: automatically obtained from Connection (this field is ignored).
        /// </summary>
        public Network.INetworkTransport Transport { get; set; }

        /// <summary>
        /// Guest-only: result of KlothoConnection.Connect().
        /// Null indicates host mode.
        /// Contains Transport, SimulationConfig, and handshake results.
        /// </summary>
        public ConnectionResult Connection { get; set; }

        /// <summary>
        /// SD multi-room only: the room ID the client was assigned to.
        /// -1 (default) means single-room or no room-based routing.
        /// Retained for re-sending RoomHandshake on ServerDrivenClientService Reconnect.
        /// </summary>
        public int RoomId { get; set; } = -1;

        // ── DataAssetRegistry ──

        /// <summary>
        /// Externally-built asset registry.
        /// If null, the existing path is used (internal DataAssetRegistry created + registered in RegisterSystems).
        /// </summary>
        public IDataAssetRegistry AssetRegistry { get; set; }

        // ── SimulationConfig ──

        /// <summary>
        /// Host: loaded from a ScriptableObject and specified directly.
        /// Guest: automatically obtained from Connection.SimulationConfig (this field is ignored).
        /// </summary>
        public ISimulationConfig SimulationConfig { get; set; }

        // ── SessionConfig ──

        /// <summary>
        /// Host: specify the config directly (typically a USessionConfig ScriptableObject).
        /// Guest: ignored — received via GameStartMessage / LateJoinAcceptMessage / ReconnectAcceptMessage.
        /// Null falls back to a default SessionConfig instance.
        /// </summary>
        public ISessionConfig SessionConfig { get; set; }

        // ── Reconnect Credentials (cold-start Reconnect persistence) ──

        /// <summary>
        /// Store for persisting cold-start Reconnect credentials.
        /// Optional — when null, cold-start credentials are not persisted (host or anonymous client).
        /// </summary>
        public Network.IReconnectCredentialsStore CredentialsStore { get; set; } = null;

        /// <summary>
        /// App version stamp recorded into persisted credentials for version compatibility checks
        /// during cold-start Reconnect. Typically passed Application.version from the game side.
        /// </summary>
        public string AppVersion { get; set; } = null;

        /// <summary>
        /// Provides a stable device identifier embedded into persisted credentials.
        /// Optional — when null, GetDeviceId returns string.Empty.
        /// </summary>
        public Network.IDeviceIdProvider DeviceIdProvider { get; set; } = null;

        // ── Lifecycle Observer ──

        /// <summary>
        /// Optional aggregated lifecycle callback receiver. When non-null, KlothoSession.Create
        /// subscribes its methods to NetworkService / Engine events; KlothoSession.Stop unsubscribes.
        /// Replaces per-game manual +=/-= wiring across StartHost / JoinGameAsync / ReconnectAsync / StopGame.
        /// </summary>
        public IKlothoSessionObserver LifecycleObserver { get; set; } = null;
    }
}
