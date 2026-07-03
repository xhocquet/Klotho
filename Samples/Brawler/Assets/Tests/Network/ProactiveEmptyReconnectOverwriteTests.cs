using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Input;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// A reconnecting player's real input must overwrite an UNSEALED empty placeholder
    /// on every P2P peer (the host's proactive InjectDisconnectedPlayerInputs fill, or a guest's
    /// gap-fill / relayed empty). Before the fix the keep-first guard (AddCommandChecked
    /// overwrite=false) dropped the real as DroppedDuplicate, leaving that peer's buffer on empty
    /// while the reconnected player simulated real -> per-peer desync. The fix
    /// (KlothoEngine.HandleCommandReceived) overwrites real-over-unsealed-empty and requests an
    /// explicit rollback (the proactive empty was a confirmed proxy, not a prediction, so the
    /// prediction-mismatch path does not fire on its own).
    ///   - frontier (tick >= CurrentTick): overwrite only; RequestRollback self-no-ops.
    ///   - late real (tick < CurrentTick): overwrite + explicit RequestRollback re-sims the tick.
    ///   - regressions: empty-over-real, real-over-real, sealed all keep current behavior.
    ///   - symmetry: guests overwrite too — a host-only IsHost
    ///     guard left guests on empty and re-created the asymmetry.
    /// </summary>
    [TestFixture]
    internal class ProactiveEmptyReconnectOverwriteTests
    {
        private static readonly MethodInfo _handleCommandReceived = typeof(KlothoEngine)
            .GetMethod("HandleCommandReceived", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _inputBufferField = typeof(KlothoEngine)
            .GetField("_inputBuffer", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _hasPendingRollbackField = typeof(KlothoEngine)
            .GetField("_hasPendingRollback", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _pendingRollbackTickField = typeof(KlothoEngine)
            .GetField("_pendingRollbackTick", BindingFlags.NonPublic | BindingFlags.Instance);

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
            _logger = loggerFactory.CreateLogger("ProactiveEmptyReconnectOverwriteTests");
        }

        [SetUp]
        public void SetUp() => _harness = new KlothoTestHarness(_logger);

        [TearDown]
        public void TearDown() => _harness.Reset();

        // host (playerId 0) + 1 guest (playerId 1), Playing, advanced a few ticks.
        private TestPeer SetUpHostWithGuest()
        {
            _harness.CreateHost();
            _harness.AddGuest(); // playerId 1
            _harness.StartPlaying();
            _harness.AdvanceAllToTick(10);
            return _harness.Host;
        }

        private static InputBuffer Buffer(KlothoEngine engine) => (InputBuffer)_inputBufferField.GetValue(engine);
        private static void Receive(KlothoEngine engine, ICommand cmd) => _handleCommandReceived.Invoke(engine, new object[] { cmd });
        private static bool HasPendingRollback(KlothoEngine engine) => (bool)_hasPendingRollbackField.GetValue(engine);
        private static int PendingRollbackTick(KlothoEngine engine) => (int)_pendingRollbackTickField.GetValue(engine);

        // --- the fix: real overwrites the host's proactive unsealed empty ---

        [Test]
        public void RealOverProactiveEmpty_Frontier_OverwritesWithoutRollback()
        {
            var host = SetUpHostWithGuest();
            var buffer = Buffer(host.Engine);
            int tick = host.CurrentTick + 5; // frontier (not yet simulated)
            buffer.AddCommand(new EmptyCommand(1, tick)); // host proactive UNSEALED empty for the guest
            Assert.IsFalse(buffer.IsSealed(tick, 1), "proactive fill must be unsealed");

            var real = new MoveCommand(1, tick, default);
            Receive(host.Engine, real);

            Assert.AreSame(real, buffer.GetCommand(tick, 1),
                "reconnect real must overwrite the host's proactive unsealed empty");
            Assert.IsFalse(HasPendingRollback(host.Engine),
                "frontier (tick >= CurrentTick): RequestRollback self-no-ops, sim picks up real when it reaches the tick");
        }

        [Test]
        public void RealOverProactiveEmpty_LateArrival_OverwritesAndRequestsRollback()
        {
            var host = SetUpHostWithGuest();
            var buffer = Buffer(host.Engine);
            int tick = host.CurrentTick - 2; // already-simulated past tick
            buffer.AddCommandOverwrite(new EmptyCommand(1, tick)); // seat an unsealed empty in the past slot
            Assert.IsFalse(buffer.IsSealed(tick, 1), "test precondition: past slot must be unsealed");

            var real = new MoveCommand(1, tick, default);
            Receive(host.Engine, real);

            Assert.AreSame(real, buffer.GetCommand(tick, 1),
                "late reconnect real must overwrite the already-seated empty");
            Assert.IsTrue(HasPendingRollback(host.Engine),
                "late real (tick < CurrentTick): explicit RequestRollback must fire to re-sim the tick");
            Assert.LessOrEqual(PendingRollbackTick(host.Engine), tick);
        }

        // --- regressions: keep-first / seal behavior must be unchanged ---

        [Test]
        public void EmptyOverReal_KeepsReal()
        {
            var host = SetUpHostWithGuest();
            var buffer = Buffer(host.Engine);
            int tick = host.CurrentTick + 5;
            var real = new MoveCommand(1, tick, default);
            buffer.AddCommand(real);

            Receive(host.Engine, new EmptyCommand(1, tick));

            Assert.AreSame(real, buffer.GetCommand(tick, 1),
                "incoming empty must not displace an existing real — keep-first protects the real");
        }

        [Test]
        public void RealOverReal_KeepsFirst()
        {
            var host = SetUpHostWithGuest();
            var buffer = Buffer(host.Engine);
            int tick = host.CurrentTick + 5;
            var first = new MoveCommand(1, tick, default);
            buffer.AddCommand(first);

            Receive(host.Engine, new MoveCommand(1, tick, default));

            Assert.AreSame(first, buffer.GetCommand(tick, 1),
                "real-over-real stays keep-first — the override only displaces empties");
        }

        [Test]
        public void RealOverSealedEmpty_StaysDropped()
        {
            var host = SetUpHostWithGuest();
            var buffer = Buffer(host.Engine);
            int tick = host.CurrentTick + 5;
            buffer.AddCommand(new EmptyCommand(1, tick));
            buffer.SealEmpty(tick, 1);

            Receive(host.Engine, new MoveCommand(1, tick, default));

            Assert.IsTrue(buffer.IsSealed(tick, 1), "seal must persist");
            Assert.AreEqual(EmptyCommand.TYPE_ID, buffer.GetCommand(tick, 1)?.CommandTypeId,
                "a sealed slot hard-blocks the real (DroppedSealed) — no overwrite even for real-over-empty");
        }

        // --- P2P symmetry: guests overwrite too, mirroring the host cases ---

        // host (playerId 0) + 2 guests; returns the playerId-1 guest. Advanced a few ticks.
        private TestPeer SetUpGuest()
        {
            _harness.CreateHost();
            var guest = _harness.AddGuest(); // playerId 1
            _harness.StartPlaying();
            _harness.AdvanceAllToTick(10);
            return guest;
        }

        [Test]
        public void Guest_AlsoOverwrites_Frontier()
        {
            var guest = SetUpGuest();
            var buffer = Buffer(guest.Engine);
            int tick = guest.CurrentTick + 5; // frontier
            buffer.AddCommand(new EmptyCommand(0, tick)); // relayed/gap-filled unsealed empty for player 0

            var real = new MoveCommand(0, tick, default);
            Receive(guest.Engine, real);

            Assert.AreSame(real, buffer.GetCommand(tick, 0),
                "guest must ALSO overwrite real-over-unsealed-empty (P2P symmetric)");
            Assert.IsFalse(HasPendingRollback(guest.Engine),
                "frontier (tick >= CurrentTick): RequestRollback self-no-ops");
        }

        [Test]
        public void Guest_AlsoOverwrites_LateArrival_RequestsRollback()
        {
            var guest = SetUpGuest();
            var buffer = Buffer(guest.Engine);
            int tick = guest.CurrentTick - 2; // already-simulated past tick
            buffer.AddCommandOverwrite(new EmptyCommand(0, tick));
            Assert.IsFalse(buffer.IsSealed(tick, 0), "test precondition: past slot must be unsealed");

            var real = new MoveCommand(0, tick, default);
            Receive(guest.Engine, real);

            Assert.AreSame(real, buffer.GetCommand(tick, 0),
                "guest late real must overwrite the already-seated empty (P2P symmetric)");
            Assert.IsTrue(HasPendingRollback(guest.Engine),
                "late real (tick < CurrentTick): explicit RequestRollback must fire on the guest too");
            Assert.LessOrEqual(PendingRollbackTick(guest.Engine), tick);
        }
    }
}
