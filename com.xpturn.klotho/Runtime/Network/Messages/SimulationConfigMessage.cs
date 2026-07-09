using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Message used by the host to deliver the entire SimulationConfig to a guest.
    /// Sent immediately after the handshake (SyncComplete) and before the Ready exchange.
    /// The guest uses the received values to initialize EcsSimulation + KlothoEngine (host-authoritative model).
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.SimulationConfig)]
    public partial class SimulationConfigMessage : NetworkMessageBase
    {
        // --- Tick Loop ---
        [KlothoOrder] public int TickIntervalMs;
        [KlothoOrder] public int MaxEntities;
        [KlothoOrder] public int CatchupMaxTicksPerFrame;
        // --- Input ---
        [KlothoOrder] public int InputDelayTicks;
        // --- Rollback ---
        [KlothoOrder] public int MaxRollbackTicks;
        // --- Sync / Resync ---
        [KlothoOrder] public int SyncCheckInterval;
        [KlothoOrder] public int ResyncMaxRetries;
        [KlothoOrder] public int DesyncThresholdForResync;
        [KlothoOrder] public int CorrectiveResetCooldownMs;
        // --- Prediction ---
        [KlothoOrder] public bool UsePrediction;
        // --- Network Mode ---
        [KlothoOrder] public int Mode; // NetworkMode as int
        // --- ServerDriven ---
        [KlothoOrder] public int HardToleranceMs;
        [KlothoOrder] public int InputResendIntervalMs;
        [KlothoOrder] public int MaxUnackedInputs;
        [KlothoOrder] public int ServerSnapshotRetentionTicks;
        [KlothoOrder] public int SDInputLeadTicks;
        // --- Error Correction ---
        [KlothoOrder] public bool EnableErrorCorrection;
        [KlothoOrder] public int InterpolationDelayTicks;
        // --- P2P Quorum-Miss Watchdog ---
        [KlothoOrder] public int QuorumMissDropTicks;
        // --- Reactive Dynamic InputDelay ---
        [KlothoOrder] public int ReactiveWindowTicks;
        [KlothoOrder] public int ReactiveEscalateThreshold;
        [KlothoOrder] public int ReactiveStep;
        [KlothoOrder] public int ReactiveMax;
        [KlothoOrder] public int ServerPushGraceTicks;
        [KlothoOrder] public int ReactiveEscalateCooldownTicks;
        [KlothoOrder] public int ReactiveDeEscalateStableTicks;
        // --- Rollback Burst ---
        [KlothoOrder] public int RollbackBurstCount;
        [KlothoOrder] public int RollbackWindowTicks;
        // --- Diagnostics ---
        [KlothoOrder] public int EventDispatchWarnMs;
        [KlothoOrder] public int TickDriftWarnMultiplier;
        // --- Multi-stage — appended for append-only wire compat ---
        [KlothoOrder] public int StageId;
        [KlothoOrder] public byte[] MatchConfigData;

        /// <summary>
        /// Populates message fields from an ISimulationConfig.
        /// </summary>
        public void CopyFrom(Core.ISimulationConfig config)
        {
            TickIntervalMs = config.TickIntervalMs;
            MaxEntities = config.MaxEntities;
            CatchupMaxTicksPerFrame = config.CatchupMaxTicksPerFrame;
            InputDelayTicks = config.InputDelayTicks;
            MaxRollbackTicks = config.MaxRollbackTicks;
            SyncCheckInterval = config.SyncCheckInterval;
            ResyncMaxRetries = config.ResyncMaxRetries;
            DesyncThresholdForResync = config.DesyncThresholdForResync;
            CorrectiveResetCooldownMs = config.CorrectiveResetCooldownMs;
            UsePrediction = config.UsePrediction;
            Mode = (int)config.Mode;
            HardToleranceMs = config.HardToleranceMs;
            InputResendIntervalMs = config.InputResendIntervalMs;
            MaxUnackedInputs = config.MaxUnackedInputs;
            ServerSnapshotRetentionTicks = config.ServerSnapshotRetentionTicks;
            SDInputLeadTicks = config.SDInputLeadTicks;
            EnableErrorCorrection = config.EnableErrorCorrection;
            InterpolationDelayTicks = config.InterpolationDelayTicks;
            QuorumMissDropTicks = config.QuorumMissDropTicks;
            ReactiveWindowTicks = config.ReactiveWindowTicks;
            ReactiveEscalateThreshold = config.ReactiveEscalateThreshold;
            ReactiveStep = config.ReactiveStep;
            ReactiveMax = config.ReactiveMax;
            ServerPushGraceTicks = config.ServerPushGraceTicks;
            ReactiveEscalateCooldownTicks = config.ReactiveEscalateCooldownTicks;
            ReactiveDeEscalateStableTicks = config.ReactiveDeEscalateStableTicks;
            RollbackBurstCount = config.RollbackBurstCount;
            RollbackWindowTicks = config.RollbackWindowTicks;
            EventDispatchWarnMs = config.EventDispatchWarnMs;
            TickDriftWarnMultiplier = config.TickDriftWarnMultiplier;
            StageId = config.StageId;
            MatchConfigData = config.MatchConfigData;
        }

        /// <summary>
        /// Creates a SimulationConfig from the message fields.
        /// </summary>
        public Core.SimulationConfig ToSimulationConfig()
        {
            return new Core.SimulationConfig
            {
                TickIntervalMs = TickIntervalMs,
                MaxEntities = MaxEntities,
                CatchupMaxTicksPerFrame = CatchupMaxTicksPerFrame,
                InputDelayTicks = InputDelayTicks,
                MaxRollbackTicks = MaxRollbackTicks,
                SyncCheckInterval = SyncCheckInterval,
                ResyncMaxRetries = ResyncMaxRetries,
                DesyncThresholdForResync = DesyncThresholdForResync,
                CorrectiveResetCooldownMs = CorrectiveResetCooldownMs,
                UsePrediction = UsePrediction,
                Mode = (NetworkMode)Mode,
                HardToleranceMs = HardToleranceMs,
                InputResendIntervalMs = InputResendIntervalMs,
                MaxUnackedInputs = MaxUnackedInputs,
                ServerSnapshotRetentionTicks = ServerSnapshotRetentionTicks,
                SDInputLeadTicks = SDInputLeadTicks,
                EnableErrorCorrection = EnableErrorCorrection,
                InterpolationDelayTicks = InterpolationDelayTicks,
                QuorumMissDropTicks = QuorumMissDropTicks,
                ReactiveWindowTicks = ReactiveWindowTicks,
                ReactiveEscalateThreshold = ReactiveEscalateThreshold,
                ReactiveStep = ReactiveStep,
                ReactiveMax = ReactiveMax,
                ServerPushGraceTicks = ServerPushGraceTicks,
                ReactiveEscalateCooldownTicks = ReactiveEscalateCooldownTicks,
                ReactiveDeEscalateStableTicks = ReactiveDeEscalateStableTicks,
                RollbackBurstCount = RollbackBurstCount,
                RollbackWindowTicks = RollbackWindowTicks,
                EventDispatchWarnMs = EventDispatchWarnMs,
                TickDriftWarnMultiplier = TickDriftWarnMultiplier,
                StageId = StageId,
                MatchConfigData = MatchConfigData,
            };
        }
    }
}
