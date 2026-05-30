using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Read/write implementation of ISimulationConfig.
    /// Created inside KlothoSession.Create(),
    /// or constructed from the deserialized result of SimulationConfigMessage.
    /// </summary>
    public class SimulationConfig : ISimulationConfig
    {
        // --- Tick Loop ---

        /// <inheritdoc />
        public int TickIntervalMs { get; set; } = 25;

        /// <inheritdoc />
        public int MaxEntities { get; set; } = 256;

        /// <inheritdoc />
        public int CatchupMaxTicksPerFrame { get; set; } = 200;

        // --- Input ---

        /// <inheritdoc />
        public int InputDelayTicks { get; set; } = 4;

        // --- Rollback ---

        /// <inheritdoc />
        public int MaxRollbackTicks { get; set; } = 50;

        // --- Sync / Resync ---

        /// <inheritdoc />
        public int SyncCheckInterval { get; set; } = 30;

        /// <inheritdoc />
        public int ResyncMaxRetries { get; set; } = 3;

        /// <inheritdoc />
        public int DesyncThresholdForResync { get; set; } = 3;

        /// <inheritdoc />
        public int CorrectiveResetCooldownMs { get; set; } = 5000;

        // --- Prediction ---

        /// <inheritdoc />
        public bool UsePrediction { get; set; } = true;

        // --- Network Mode ---

        /// <inheritdoc />
        public NetworkMode Mode { get; set; } = NetworkMode.P2P;

        // --- ServerDriven ---

        /// <inheritdoc />
        public int HardToleranceMs { get; set; } = 0;

        /// <inheritdoc />
        public int InputResendIntervalMs { get; set; } = 25;

        /// <inheritdoc />
        public int MaxUnackedInputs { get; set; } = 30;

        /// <inheritdoc />
        public int ServerSnapshotRetentionTicks { get; set; } = 0;

        /// <inheritdoc />
        public int SDInputLeadTicks { get; set; } = 0;

        // --- Error Correction ---

        /// <inheritdoc />
        public bool EnableErrorCorrection { get; set; } = false;

        /// <inheritdoc />
        public int InterpolationDelayTicks { get; set; } = 3;

        // --- P2P Quorum-Miss Watchdog ---

        /// <inheritdoc />
        public int QuorumMissDropTicks { get; set; } = 20;

        // --- Reactive Dynamic InputDelay ---

        /// <inheritdoc />
        public int ReactiveWindowTicks { get; set; } = 80;

        /// <inheritdoc />
        public int ReactiveEscalateThreshold { get; set; } = 3;

        /// <inheritdoc />
        public int ReactiveStep { get; set; } = 4;

        /// <inheritdoc />
        public int ReactiveMax { get; set; } = 40;

        /// <inheritdoc />
        public int ServerPushGraceTicks { get; set; } = 40;

        /// <inheritdoc />
        public int ReactiveEscalateCooldownTicks { get; set; } = 80;

        // --- Rollback Burst ---

        /// <inheritdoc />
        public int RollbackBurstCount { get; set; } = 3;

        /// <inheritdoc />
        public int RollbackWindowTicks { get; set; } = 200;

        // --- Diagnostics ---

        /// <inheritdoc />
        public int EventDispatchWarnMs { get; set; } = 5;

        /// <inheritdoc />
        public int TickDriftWarnMultiplier { get; set; } = 2;
    }
}
