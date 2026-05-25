using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Tests.Core
{
    /// <summary>
    /// ReliableCommandTracker unit tests — verify wire-reject handler responds correctly to
    /// (a) FullState resync, (b) Duplicate / PastTick / ToleranceExceeded reject reasons, and
    /// (c) cmdTypeId filter (only the matching outstanding handle reacts).
    ///
    /// Pattern: bare KlothoEngine instance (LocalPlayerId = 0 via null _networkService).
    /// Populate _handles via internal field write, then invoke private handler methods via
    /// reflection. No ECS scaffolding required — the reconciliation paths do not dereference
    /// engine internals beyond LocalPlayerId. Heavier scenarios (initial issue / retry due
    /// check / FaultInjection drop / force-retry / state-driven Confirm latency) are exercised
    /// by the manual fault-injection scenario matrix and integration tests.
    /// </summary>
    [TestFixture]
    public class ReliableCommandTrackerTests
    {
        private const int CmdTypeId      = 103;   // synthetic — must match handle.CommandTypeId for filter
        private const int OtherCmdTypeId = 999;   // mismatched — for NoOp filter test
        private const int SeedTick       = 100;

        private static FieldInfo HandlesField =>
            typeof(ReliableCommandTracker).GetField(
                "_handles", BindingFlags.NonPublic | BindingFlags.Instance);

        private static MethodInfo HandleCommandRejectedMethod =>
            typeof(ReliableCommandTracker).GetMethod(
                "HandleCommandRejected", BindingFlags.NonPublic | BindingFlags.Instance);

        private static MethodInfo HandleResyncCompletedMethod =>
            typeof(ReliableCommandTracker).GetMethod(
                "HandleResyncCompleted", BindingFlags.NonPublic | BindingFlags.Instance);

        private static (ReliableCommandTracker tracker, ReliableCommandHandle handle) NewTrackerWithHandle()
        {
            // SimulationConfig / SessionConfig defaults are sufficient — the reconciliation paths
            // under test (HandleCommandRejected / HandleResyncCompleted) do not read these configs.
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            var tracker = new ReliableCommandTracker(engine, logger: null);

            var handle = new ReliableCommandHandle
            {
                CommandTypeId = CmdTypeId,
                Policy = new ReliabilityPolicy(),
                Engine = engine,
            };
            handle.LastAttemptTick = SeedTick;   // simulate a prior send

            var handles = (Dictionary<int, ReliableCommandHandle>)HandlesField.GetValue(tracker);
            handles[engine.LocalPlayerId] = handle;   // LocalPlayerId = 0 with null _networkService

            return (tracker, handle);
        }

        private static void InvokeHandleCommandRejected(
            ReliableCommandTracker tracker, int tick, int cmdTypeId, RejectionReason reason)
        {
            HandleCommandRejectedMethod.Invoke(tracker, new object[] { tick, cmdTypeId, reason });
        }

        private static void InvokeHandleResyncCompleted(ReliableCommandTracker tracker, int restoredTick)
        {
            HandleResyncCompletedMethod.Invoke(tracker, new object[] { restoredTick });
        }

        [Test]
        public void OnResyncCompleted_ResetsLastAttemptTick()
        {
            var (tracker, handle) = NewTrackerWithHandle();
            Assert.AreEqual(SeedTick, handle.LastAttemptTick, "precondition: LastAttemptTick seeded");

            InvokeHandleResyncCompleted(tracker, restoredTick: 0);

            Assert.AreEqual(-1, handle.LastAttemptTick,
                "FullState resync must invalidate the outstanding handle's LastAttemptTick");
        }

        [Test]
        public void OnCommandRejected_Duplicate_ResolvesHandle()
        {
            var (tracker, handle) = NewTrackerWithHandle();
            Assert.IsFalse(handle.IsResolved, "precondition: handle not yet resolved");

            InvokeHandleCommandRejected(tracker, SeedTick + 1, CmdTypeId, RejectionReason.Duplicate);

            Assert.IsTrue(handle.IsResolved,
                "Duplicate reject (with TreatDuplicateAsAck = true) must resolve the handle");
        }

        [Test]
        public void OnCommandRejected_NonHandleCmd_NoOp()
        {
            var (tracker, handle) = NewTrackerWithHandle();

            // cmdTypeId mismatch — reject is for a different cmd type and must not touch this handle.
            InvokeHandleCommandRejected(tracker, SeedTick + 1, OtherCmdTypeId, RejectionReason.Duplicate);

            Assert.AreEqual(SeedTick, handle.LastAttemptTick,
                "Reject for a non-matching cmd type must not touch LastAttemptTick");
            Assert.IsFalse(handle.IsResolved,
                "Reject for a non-matching cmd type must not resolve the handle");
            Assert.AreEqual(0, handle.CurrentExtraDelay,
                "Reject for a non-matching cmd type must not bump CurrentExtraDelay");
        }

        [Test]
        public void OnCommandRejected_PastTick_EscalatesExtraDelay()
        {
            var (tracker, handle) = NewTrackerWithHandle();
            int step = handle.Policy.ExtraDelayStep;
            int max  = handle.Policy.ExtraDelayMax;
            Assert.AreEqual(0, handle.CurrentExtraDelay, "precondition: extra delay starts at 0");

            // First PastTick — bump by Step, clear LastAttemptTick for immediate next retry.
            InvokeHandleCommandRejected(tracker, SeedTick + 1, CmdTypeId, RejectionReason.PastTick);
            Assert.AreEqual(step, handle.CurrentExtraDelay,
                "PastTick reject (with TreatPastTickAsEscalation = true) must bump CurrentExtraDelay by ExtraDelayStep");
            Assert.AreEqual(-1, handle.LastAttemptTick,
                "PastTick reject must clear LastAttemptTick so the next due check re-emits immediately");
            Assert.IsFalse(handle.IsResolved, "PastTick reject must not resolve the handle");

            // Drive to cap. Total rejects to reach max from 0 = max / step; we already did 1, so (max/step - 1) more.
            int iterations = (max / step) - 1;
            for (int i = 0; i < iterations; i++)
                InvokeHandleCommandRejected(tracker, SeedTick + 2 + i, CmdTypeId, RejectionReason.PastTick);
            Assert.AreEqual(max, handle.CurrentExtraDelay,
                "After (Max / Step) PastTick rejects, CurrentExtraDelay must equal ExtraDelayMax");
            Assert.IsFalse(handle.CapHitLogged,
                "CapHitLogged must still be false — bumps just reached cap, no at-cap reject yet");

            // First at-cap PastTick — sets CapHitLogged once, increments CapHitRejectCount.
            InvokeHandleCommandRejected(tracker, SeedTick + 50, CmdTypeId, RejectionReason.PastTick);
            Assert.AreEqual(max, handle.CurrentExtraDelay,
                "At-cap PastTick reject must not exceed ExtraDelayMax");
            Assert.IsTrue(handle.CapHitLogged, "At-cap PastTick reject must set CapHitLogged flag");
            Assert.AreEqual(1, handle.CapHitRejectCount,
                "First at-cap reject after cap-hit must increment CapHitRejectCount");

            // Subsequent at-cap reject — flag stays true (one-time Error invariant), counter keeps counting.
            InvokeHandleCommandRejected(tracker, SeedTick + 51, CmdTypeId, RejectionReason.PastTick);
            Assert.IsTrue(handle.CapHitLogged,
                "CapHitLogged flag must remain true after subsequent at-cap rejects (one-time Error log invariant)");
            Assert.AreEqual(2, handle.CapHitRejectCount,
                "Subsequent at-cap rejects must keep incrementing CapHitRejectCount");
        }

        [Test]
        public void OnCommandRejected_ToleranceExceeded_NoOp()
        {
            var (tracker, handle) = NewTrackerWithHandle();

            // ToleranceExceeded falls through the handler without touching escalation / resolve.
            InvokeHandleCommandRejected(tracker, SeedTick + 1, CmdTypeId, RejectionReason.ToleranceExceeded);

            Assert.AreEqual(SeedTick, handle.LastAttemptTick,
                "ToleranceExceeded must not touch LastAttemptTick");
            Assert.IsFalse(handle.IsResolved,
                "ToleranceExceeded must not resolve the handle");
            Assert.AreEqual(0, handle.CurrentExtraDelay,
                "ToleranceExceeded must not bump CurrentExtraDelay");
        }
    }
}
