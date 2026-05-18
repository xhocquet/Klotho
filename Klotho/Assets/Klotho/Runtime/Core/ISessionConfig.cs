namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Per-session mutable configuration. Determined by the host when the game starts.
    /// Propagated to guests via GameStartMessage. For Late Join, supplemented via LateJoinAcceptMessage.
    /// </summary>
    public interface ISessionConfig
    {
        /// <summary>
        /// Deterministic random seed. All peers must initialize the RNG with the same seed to guarantee determinism.
        /// If 0, the host generates one automatically.
        /// </summary>
        int RandomSeed { get; }

        /// <summary>
        /// Maximum number of players. Used by the network service and view initialization.
        /// </summary>
        int MaxPlayers { get; }

        /// <summary>
        /// Minimum number of players required to start the game.
        /// The host/server only triggers <c>StartGame</c> once <c>_players.Count &gt;= MinPlayers</c> and all players are ready.
        /// Range: 1 or greater, must satisfy <c>MinPlayers &lt;= MaxPlayers</c>.
        /// Out-of-range values are clamped at session build time with a warning log.
        /// </summary>
        int MinPlayers { get; }

        /// <summary>
        /// Whether Late Join is allowed. If false, requests from new players to join while the game is in progress are rejected.
        /// </summary>
        bool AllowLateJoin { get; }

        /// <summary>
        /// Reconnect timeout (milliseconds). If a player does not reconnect within this window after disconnecting, they are removed.
        /// Guests also stop attempting to reconnect once this time elapses.
        /// Range: 1000 or greater. Typically 10000~60000ms.
        /// </summary>
        int ReconnectTimeoutMs { get; }

        /// <summary>
        /// Maximum reconnect retry count. When a guest sends a ReconnectRequest/FullState request to the host
        /// and receives no response, it retries. Exceeding this count is treated as reconnect failure.
        /// Range: 0 or greater. 0 means a single attempt with no retries.
        /// </summary>
        int ReconnectMaxRetries { get; }

        /// <summary>
        /// Tick offset at which the PlayerJoinCommand is inserted on Late Join.
        /// The join command is scheduled at current tick + LateJoinDelayTicks, giving existing players time to prepare input.
        /// Range: 1 or greater. Larger values are safer but make joining slower.
        /// </summary>
        int LateJoinDelayTicks { get; }

        /// <summary>
        /// Maximum retry count for Full State Resync.
        /// When desyncs occur DesyncThresholdForResync times in a row, a Resync is attempted;
        /// if this count is exceeded, the OnResyncFailed event is raised.
        /// Range: 1 or greater.
        /// </summary>
        int ResyncMaxRetries { get; }

        /// <summary>
        /// Threshold of consecutive desyncs that triggers a full state resync.
        /// When sync hash mismatches are detected this many times in a row, a Full State Resync is requested instead of a rollback.
        /// Range: 1 or greater. 1 means an immediate Resync on the first desync.
        /// </summary>
        int DesyncThresholdForResync { get; }

        /// <summary>
        /// Minimum interval between consecutive corrective resets (milliseconds).
        /// Prevents broadcast storms when persistent hash divergence fires OnHashMismatch repeatedly.
        /// Range: 1000 or greater. Default 5000.
        /// </summary>
        int CorrectiveResetCooldownMs { get; }

        /// <summary>
        /// Game start countdown duration (milliseconds). After all players are ready,
        /// the game starts simultaneously after waiting this duration based on SharedClock.
        /// Compensates for network latency differences to guarantee a synchronized start.
        /// Range: 0 or greater. 0 means immediate start.
        /// </summary>
        int CountdownDurationMs { get; }

        /// <summary>
        /// Maximum number of ticks executed per frame during Late Join catch-up.
        /// The upper bound on ticks executed in a single frame while catching up to the current tick after a Late Join.
        /// Larger values catch up faster but may cause frame hitching.
        /// Range: 1 or greater. Typically 100~500.
        /// </summary>
        int CatchupMaxTicksPerFrame { get; }

        /// <summary>
        /// Maximum number of spectators allowed in the session.
        /// </summary>
        int MaxSpectators { get; }

        /// <summary>
        /// Post-match grace duration on abort (milliseconds). Time between OnMatchAborted fire and
        /// Room.State transition to Draining, giving clients time to display an error dialog
        /// (connection lost / chain stall notice) and server side time for abort logging. Default 1500.
        /// Range: 0 or greater. Typically shorter than EndGraceMs since abort uses an error UI, not a
        /// result screen.
        /// </summary>
        int AbortGraceMs { get; }

        /// <summary>
        /// Simulation behavior during the post-match grace window. Continue (default) keeps the
        /// simulation running so input/heartbeat/replay continuity is preserved; Pause halts tick
        /// advancement (KlothoState transitions Running -&gt; Ending). See EndGracePolicy.
        /// </summary>
        EndGracePolicy EndGracePolicy { get; }

        /// <summary>
        /// Post-match grace duration on normal end (milliseconds). Time between OnMatchEnded fire and
        /// Room.State transition to Draining, giving clients time to display the result screen and
        /// server side time for any post-processing hook. Default 5000.
        /// Range: 0 (immediate drain, debug/integration only) or greater.
        /// </summary>
        int EndGraceMs { get; }

        /// <summary>
        /// Client-side grace duration on normal end (milliseconds). Time between OnMatchEnded fire
        /// on the client and the client's self-initiated session shutdown, so the result screen
        /// plays out before the chain-stall warning storm begins. Default 4500 — 500ms shorter than
        /// EndGraceMs so the client tears down before the server drain disables verified broadcasts.
        /// Range: 0 or greater. Must stay below EndGraceMs; inversion risks chain-stall warnings.
        /// Raise EndGraceMs or lower this if P99 RTT exceeds the default margin (500ms).
        /// </summary>
        int ClientShutdownGraceMs { get; }
    }
}
