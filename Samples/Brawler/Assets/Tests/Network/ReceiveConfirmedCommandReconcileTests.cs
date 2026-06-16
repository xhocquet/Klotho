using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Input;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// The catchup confirmed-input path (ReceiveConfirmedCommand) must reconcile a late
    /// remote input against the reconnecting peer's speculative prediction exactly like a network
    /// arrival (HandleCommandReceived). Before the fix it was a bare AddCommand: a mispredicted
    /// (empty/repeat-last) tick was never rolled back, so TryAdvanceVerifiedChain promoted the
    /// frozen state to verified -> per-peer desync. The fix routes both callers through the shared
    /// ReconcileConfirmedAgainstPrediction helper; the catchup caller passes resyncOnDeepRollback
    /// so a rollback target below the snapshot-ring window falls back to FullStateResync.
    /// </summary>
    [TestFixture]
    internal class ReceiveConfirmedCommandReconcileTests
    {
        private static readonly MethodInfo _handleCommandReceived = typeof(KlothoEngine)
            .GetMethod("HandleCommandReceived", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _inputBufferField = typeof(KlothoEngine)
            .GetField("_inputBuffer", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _hasPendingRollbackField = typeof(KlothoEngine)
            .GetField("_hasPendingRollback", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _pendingRollbackTickField = typeof(KlothoEngine)
            .GetField("_pendingRollbackTick", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _pendingCommandsField = typeof(KlothoEngine)
            .GetField("_pendingCommands", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _isSpectatorModeField = typeof(KlothoEngine)
            .GetField("_isSpectatorMode", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _isCatchingUpField = typeof(KlothoEngine)
            .GetField("_isCatchingUp", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _resyncStateField = typeof(KlothoEngine)
            .GetField("_resyncState", BindingFlags.NonPublic | BindingFlags.Instance);

        private IKLogger _logger;
        private KlothoTestHarness _harness;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = KLoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(KLogLevel.Trace);
                logging.AddUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("ReceiveConfirmedCommandReconcileTests");
        }

        [TearDown]
        public void TearDown() => _harness?.Reset();

        // ── helpers ──

        private static InputBuffer Buffer(KlothoEngine engine) => (InputBuffer)_inputBufferField.GetValue(engine);
        private static bool HasPendingRollback(KlothoEngine engine) => (bool)_hasPendingRollbackField.GetValue(engine);
        private static int PendingRollbackTick(KlothoEngine engine) => (int)_pendingRollbackTickField.GetValue(engine);
        private static List<ICommand> PendingCommands(KlothoEngine engine) => (List<ICommand>)_pendingCommandsField.GetValue(engine);
        private static void SetSpectatorMode(KlothoEngine engine, bool v) => _isSpectatorModeField.SetValue(engine, v);
        private static void SetCatchingUp(KlothoEngine engine, bool v) => _isCatchingUpField.SetValue(engine, v);
        private static string ResyncState(KlothoEngine engine) => _resyncStateField.GetValue(engine).ToString();
        private static void HandleReceived(KlothoEngine engine, ICommand cmd) => _handleCommandReceived.Invoke(engine, new object[] { cmd });
        private static FPVector3 Dir(int x) => new FPVector3(x, 0, 0);

        // host (playerId 0) + 1 guest (playerId 1), Playing, advanced a few ticks. Returns the guest
        // (the reconnect/catchup actor — FullStateResync only fires on a non-host peer).
        private TestPeer SetUpGuest(SimulationConfig config = null)
        {
            _harness = new KlothoTestHarness(_logger);
            if (config != null) _harness.WithSimulationConfig(config);
            _harness.CreateHost();
            var guest = _harness.AddGuest(); // playerId 1
            _harness.StartPlaying();
            _harness.AdvanceAllToTick(10);
            // Warmup (CurrentTick < InputDelay) speculatively predicts ticks 0..InputDelay-1 whose
            // real input never arrives — clear those lingering predictions so each test constructs
            // its own _pendingCommands scenario deterministically (no stale (tick,player) matches).
            PendingCommands(guest.Engine).Clear();
            return guest;
        }

        // ── Prediction-mismatch (buffer slot already real → overwrite branch skipped, so the
        // _pendingCommands prediction-mismatch branch is the sole rollback trigger) ──

        [Test]
        public void PredictionMismatch_RequestsRollback()
        {
            var guest = SetUpGuest();
            var engine = guest.Engine;
            var buffer = Buffer(engine);
            int t = engine.CurrentTick - 2; // T < CurrentTick (T == CurrentTick would no-op, Rollback.cs:77)

            // Block the unsealed-empty overwrite branch with a real seat, so only prediction-mismatch fires.
            buffer.AddCommandOverwrite(new MoveCommand(1, t, Dir(1)));
            // Speculative empty prediction left in _pendingCommands for (t, player 1).
            PendingCommands(engine).Add(new EmptyCommand(1, t));

            engine.ReceiveConfirmedCommand(new MoveCommand(1, t, Dir(5))); // real != predicted

            Assert.IsTrue(HasPendingRollback(engine),
                "catchup confirmed input that mispredicts must request a rollback (was bare AddCommand before IMP60-20)");
            Assert.LessOrEqual(PendingRollbackTick(engine), t);
        }

        [Test]
        public void PredictionMatch_NoRollback()
        {
            var guest = SetUpGuest();
            var engine = guest.Engine;
            var buffer = Buffer(engine);
            int t = engine.CurrentTick - 2;

            buffer.AddCommandOverwrite(new MoveCommand(1, t, Dir(1)));
            PendingCommands(engine).Add(new MoveCommand(1, t, Dir(5))); // prediction

            engine.ReceiveConfirmedCommand(new MoveCommand(1, t, Dir(5))); // real == predicted

            Assert.IsFalse(HasPendingRollback(engine),
                "matched prediction must not trigger an unnecessary rollback");
        }

        // ── Spectator guard ──

        [Test]
        public void SpectatorMode_NoReconcile()
        {
            var guest = SetUpGuest();
            var engine = guest.Engine;
            var buffer = Buffer(engine);
            int t = engine.CurrentTick - 2;

            SetSpectatorMode(engine, true);
            buffer.AddCommandOverwrite(new EmptyCommand(1, t));   // unsealed empty that WOULD overwrite in P2P
            PendingCommands(engine).Add(new EmptyCommand(1, t));  // mismatching prediction

            engine.ReceiveConfirmedCommand(new MoveCommand(1, t, Dir(5)));

            Assert.IsFalse(HasPendingRollback(engine),
                "spectator path is a bare AddCommand — no overwrite, no reconcile, no rollback");
            Assert.AreEqual(EmptyCommand.TYPE_ID, buffer.GetCommand(t, 1)?.CommandTypeId,
                "spectator keep-first: the empty stays (no real-over-empty overwrite)");
        }

        // ── Rollback coalesce within one frame ──

        [Test]
        public void MultipleCatchupRollbacks_CoalesceToMinTick()
        {
            var guest = SetUpGuest();
            var engine = guest.Engine;
            var buffer = Buffer(engine);
            int tHi = engine.CurrentTick - 2;
            int tLo = engine.CurrentTick - 4;

            buffer.AddCommandOverwrite(new EmptyCommand(1, tHi));
            buffer.AddCommandOverwrite(new EmptyCommand(1, tLo));

            engine.ReceiveConfirmedCommand(new MoveCommand(1, tHi, Dir(5)));
            engine.ReceiveConfirmedCommand(new MoveCommand(1, tLo, Dir(5)));

            Assert.IsTrue(HasPendingRollback(engine));
            Assert.AreEqual(tLo, PendingRollbackTick(engine),
                "deferred RequestRollback coalesces to the minimum tick (single re-sim at frame end)");
        }

        // ── Real-over-unsealed-empty overwrite via the catchup entry (new caller) ──

        [Test]
        public void CatchupOverwrite_Frontier_NoRollback()
        {
            var guest = SetUpGuest();
            var engine = guest.Engine;
            var buffer = Buffer(engine);
            int t = engine.CurrentTick + 5; // frontier (not yet simulated)
            buffer.AddCommand(new EmptyCommand(1, t)); // relayed/gap-filled unsealed empty

            engine.ReceiveConfirmedCommand(new MoveCommand(1, t, Dir(5)));

            Assert.AreSame(buffer.GetCommand(t, 1).GetType(), typeof(MoveCommand),
                "catchup real must overwrite the unsealed empty (overwrite branch reachable via ReceiveConfirmedCommand)");
            Assert.IsFalse(HasPendingRollback(engine),
                "frontier overwrite: RequestRollback self-no-ops (tick >= CurrentTick)");
        }

        [Test]
        public void CatchupOverwrite_LateArrival_RequestsRollback()
        {
            var guest = SetUpGuest();
            var engine = guest.Engine;
            var buffer = Buffer(engine);
            int t = engine.CurrentTick - 2;
            buffer.AddCommandOverwrite(new EmptyCommand(1, t));

            engine.ReceiveConfirmedCommand(new MoveCommand(1, t, Dir(5)));

            Assert.AreEqual(typeof(MoveCommand), buffer.GetCommand(t, 1).GetType(),
                "late catchup real overwrites the already-seated empty");
            Assert.IsTrue(HasPendingRollback(engine));
            Assert.LessOrEqual(PendingRollbackTick(engine), t);
        }

        // ── CatchingUp-state ingest is safe (no prediction yet → no rollback) ──

        [Test]
        public void CatchingUpIngest_NoRollback()
        {
            var guest = SetUpGuest();
            var engine = guest.Engine;
            SetCatchingUp(engine, true); // catchup uses verified-only; _pendingCommands cleared in SetUpGuest

            for (int i = 0; i < 4; i++)
                engine.ReceiveConfirmedCommand(new MoveCommand(1, engine.CurrentTick + 5 + i, Dir(5)));

            Assert.IsFalse(HasPendingRollback(engine),
                "CatchingUp ingest with empty _pendingCommands must not roll back (T6 no-regression)");
        }

        // ── Deep-rollback guard (small MaxRollbackTicks so a deep gap is reachable cheaply) ──

        private static SimulationConfig SmallRollbackConfig() =>
            new SimulationConfig { MaxRollbackTicks = 10, SyncCheckInterval = 4 };

        [Test]
        public void DeepCatchupRollback_Mismatch_FallsBackToResync()
        {
            var guest = SetUpGuest(SmallRollbackConfig());
            var engine = guest.Engine;
            _harness.AdvanceAllToTick(16); // CurrentTick ~16, deep boundary = CurrentTick - 10
            var buffer = Buffer(engine);
            int deep = engine.CurrentTick - 12; // < CurrentTick - MaxRollbackTicks
            Assert.Less(deep, engine.CurrentTick - 10, "precondition: target below snapshot-ring window");
            buffer.AddCommandOverwrite(new EmptyCommand(1, deep));

            engine.ReceiveConfirmedCommand(new MoveCommand(1, deep, Dir(5)));

            Assert.AreEqual("Requested", ResyncState(engine),
                "a catchup rollback below the window must fall back to FullStateResync (clamp would leave the gap tick frozen)");
            Assert.IsFalse(HasPendingRollback(engine),
                "resync supersedes rollback — no pending rollback queued");
        }

        [Test]
        public void DeepCatchupRollback_Match_NoResyncNoRollback()
        {
            var guest = SetUpGuest(SmallRollbackConfig());
            var engine = guest.Engine;
            _harness.AdvanceAllToTick(16);
            var buffer = Buffer(engine);
            int deep = engine.CurrentTick - 12;
            buffer.AddCommandOverwrite(new MoveCommand(1, deep, Dir(1))); // real seat → overwrite branch skipped
            PendingCommands(engine).Add(new MoveCommand(1, deep, Dir(5))); // prediction == arrival below

            engine.ReceiveConfirmedCommand(new MoveCommand(1, deep, Dir(5))); // match

            Assert.AreEqual("None", ResyncState(engine),
                "a matched deep arrival has no mismatch → no resync (over-resync avoidance)");
            Assert.IsFalse(HasPendingRollback(engine));
        }

        [Test]
        public void DeepRollback_NormalArrivalPath_ClampsNotResync()
        {
            // Invariant: resyncOnDeepRollback=false (HandleCommandReceived) keeps the existing
            // clamp-and-continue behavior — the deep-rollback guard must NOT leak into the normal arrival path.
            var guest = SetUpGuest(SmallRollbackConfig());
            var engine = guest.Engine;
            _harness.AdvanceAllToTick(16);
            var buffer = Buffer(engine);
            int deep = engine.CurrentTick - 12;
            buffer.AddCommandOverwrite(new EmptyCommand(1, deep));

            HandleReceived(engine, new MoveCommand(1, deep, Dir(5)));

            Assert.IsTrue(HasPendingRollback(engine),
                "normal arrival keeps RequestRollback (ResolveRollbackTick clamps at flush) — no resync");
            Assert.AreEqual("None", ResyncState(engine), "normal path must not request a resync");
        }

        // ── Sealed-drop returns the rented catchup instance to the pool (leak fix) ──

        [Test]
        public void SealedDrop_ReturnsInstanceToPool()
        {
            var guest = SetUpGuest();
            var engine = guest.Engine;
            var buffer = Buffer(engine);
            int t = engine.CurrentTick + 5;
            buffer.AddCommand(new EmptyCommand(1, t));
            buffer.SealEmpty(t, 1);

            // Mirror the catchup deserialize-born ownership: a pool-rented instance.
            var rented = CommandPool.Get<MoveCommand>();
            rented.PlayerId = 1;
            rented.Tick = t;
            rented.Target = Dir(5);
            int outstandingBefore = CommandPool.GetOutstandingCount();

            engine.ReceiveConfirmedCommand(rented);

            Assert.IsTrue(buffer.IsSealed(t, 1), "seal persists");
            Assert.AreEqual(EmptyCommand.TYPE_ID, buffer.GetCommand(t, 1)?.CommandTypeId,
                "sealed slot hard-blocks the real (DroppedSealed)");
            Assert.AreEqual(outstandingBefore - 1, CommandPool.GetOutstandingCount(),
                "DroppedSealed must return the rented instance to the pool (bare AddCommand leaked it before IMP60-20)");
        }
    }
}
