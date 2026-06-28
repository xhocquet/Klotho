using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Input.Tests
{
    /// <summary>
    /// InputBuffer tests
    /// </summary>
    [TestFixture]
    public class InputBufferTests
    {
        private InputBuffer _buffer;

        [SetUp]
        public void SetUp()
        {
            _buffer = new InputBuffer();
        }

        #region Basic Add/Get

        [Test]
        public void AddCommand_IncreasesCount()
        {
            Assert.AreEqual(0, _buffer.Count);

            _buffer.AddCommand(new EmptyCommand(0, 0));
            Assert.AreEqual(1, _buffer.Count);

            _buffer.AddCommand(new EmptyCommand(1, 0));
            Assert.AreEqual(2, _buffer.Count);
        }

        [Test]
        public void AddCommand_Null_DoesNothing()
        {
            _buffer.AddCommand(null);
            Assert.AreEqual(0, _buffer.Count);
        }

        [Test]
        public void GetCommand_ReturnsCorrectCommand()
        {
            var cmd = new MoveCommand(1, 10, new FPVector3(FP64.FromRaw(100), FP64.FromRaw(200), FP64.FromRaw(300)));
            _buffer.AddCommand(cmd);

            var retrieved = _buffer.GetCommand(10, 1);

            Assert.IsNotNull(retrieved);
            Assert.IsInstanceOf<MoveCommand>(retrieved);
            var moveCmd = (MoveCommand)retrieved;
            Assert.AreEqual(FP64.FromRaw(100), moveCmd.Target.x);
        }

        [Test]
        public void GetCommand_NotFound_ReturnsNull()
        {
            var result = _buffer.GetCommand(100, 1);
            Assert.IsNull(result);
        }

        [Test]
        public void GetCommands_ReturnsAllForTick()
        {
            _buffer.AddCommand(new EmptyCommand(0, 5));
            _buffer.AddCommand(new EmptyCommand(1, 5));
            _buffer.AddCommand(new EmptyCommand(2, 5));
            _buffer.AddCommand(new EmptyCommand(0, 6)); // Different tick

            var commands = _buffer.GetCommands(5).ToList();
            Assert.AreEqual(3, commands.Count);
        }

        [Test]
        public void GetCommands_EmptyTick_ReturnsEmpty()
        {
            var commands = _buffer.GetCommands(100);
            Assert.IsEmpty(commands);
        }

        #endregion

        #region Tick Range

        [Test]
        public void OldestNewestTick_UpdatesCorrectly()
        {
            _buffer.AddCommand(new EmptyCommand(0, 10));
            _buffer.AddCommand(new EmptyCommand(0, 5));
            _buffer.AddCommand(new EmptyCommand(0, 15));

            Assert.AreEqual(5, _buffer.OldestTick);
            Assert.AreEqual(15, _buffer.NewestTick);
        }

        [Test]
        public void EmptyBuffer_OldestNewestAreZero()
        {
            Assert.AreEqual(0, _buffer.OldestTick);
            Assert.AreEqual(0, _buffer.NewestTick);
        }

        #endregion

        #region HasCommand Tests

        [Test]
        public void HasCommandForTick_ReturnsTrue_WhenExists()
        {
            _buffer.AddCommand(new EmptyCommand(0, 10));
            Assert.IsTrue(_buffer.HasCommandForTick(10));
        }

        [Test]
        public void HasCommandForTick_ReturnsFalse_WhenNotExists()
        {
            Assert.IsFalse(_buffer.HasCommandForTick(10));
        }

        [Test]
        public void HasCommandForTick_WithPlayerId_WorksCorrectly()
        {
            _buffer.AddCommand(new EmptyCommand(0, 10));
            _buffer.AddCommand(new EmptyCommand(1, 10));

            Assert.IsTrue(_buffer.HasCommandForTick(10, 0));
            Assert.IsTrue(_buffer.HasCommandForTick(10, 1));
            Assert.IsFalse(_buffer.HasCommandForTick(10, 2));
        }

        [Test]
        public void HasAllCommands_ReturnsTrue_WhenAllPresent()
        {
            _buffer.AddCommand(new EmptyCommand(0, 10));
            _buffer.AddCommand(new EmptyCommand(1, 10));
            _buffer.AddCommand(new EmptyCommand(2, 10));

            Assert.IsTrue(_buffer.HasAllCommands(10, new List<int> { 0, 1, 2 }));
        }

        [Test]
        public void HasAllCommands_ReturnsFalse_WhenMissing()
        {
            _buffer.AddCommand(new EmptyCommand(0, 10));
            _buffer.AddCommand(new EmptyCommand(1, 10));

            Assert.IsFalse(_buffer.HasAllCommands(10, new List<int> { 0, 1, 2 }));
        }

        [Test]
        public void HasAllCommands_DepartedPlayerLeftover_DoesNotSatisfyQuorum()
        {
            // Regression pin: player 1 departed but left a future-tick command;
            // active roster is {0, 2} and player 2's input is missing. The old count-based
            // check (2 stored >= 2 active) wrongly satisfied the quorum — membership must not.
            _buffer.AddCommand(new EmptyCommand(0, 10));
            _buffer.AddCommand(new EmptyCommand(1, 10)); // leftover from departed player 1

            Assert.IsFalse(_buffer.HasAllCommands(10, new List<int> { 0, 2 }),
                "Leftover commands from departed players must not satisfy the tick quorum");
            Assert.IsTrue(_buffer.HasAllCommands(10, new List<int> { 0, 1 }),
                "Membership check passes only when every listed player's command is present");
        }

        #endregion

        #region Delete Tests

        [Test]
        public void Clear_RemovesAllCommands()
        {
            _buffer.AddCommand(new EmptyCommand(0, 10));
            _buffer.AddCommand(new EmptyCommand(1, 20));

            _buffer.Clear();

            Assert.AreEqual(0, _buffer.Count);
        }

        [Test]
        public void ClearBefore_RemovesOlderTicks()
        {
            _buffer.AddCommand(new EmptyCommand(0, 5));
            _buffer.AddCommand(new EmptyCommand(0, 10));
            _buffer.AddCommand(new EmptyCommand(0, 15));
            _buffer.AddCommand(new EmptyCommand(0, 20));

            _buffer.ClearBefore(12);

            Assert.IsFalse(_buffer.HasCommandForTick(5));
            Assert.IsFalse(_buffer.HasCommandForTick(10));
            Assert.IsTrue(_buffer.HasCommandForTick(15));
            Assert.IsTrue(_buffer.HasCommandForTick(20));
        }

        [Test]
        public void ClearAfter_RemovesNewerTicks()
        {
            _buffer.AddCommand(new EmptyCommand(0, 5));
            _buffer.AddCommand(new EmptyCommand(0, 10));
            _buffer.AddCommand(new EmptyCommand(0, 15));
            _buffer.AddCommand(new EmptyCommand(0, 20));

            _buffer.ClearAfter(12);

            Assert.IsTrue(_buffer.HasCommandForTick(5));
            Assert.IsTrue(_buffer.HasCommandForTick(10));
            Assert.IsFalse(_buffer.HasCommandForTick(15));
            Assert.IsFalse(_buffer.HasCommandForTick(20));
        }

        [Test]
        public void ClearBefore_UpdatesBounds()
        {
            _buffer.AddCommand(new EmptyCommand(0, 5));
            _buffer.AddCommand(new EmptyCommand(0, 10));
            _buffer.AddCommand(new EmptyCommand(0, 15));

            _buffer.ClearBefore(8);

            Assert.AreEqual(10, _buffer.OldestTick);
            Assert.AreEqual(15, _buffer.NewestTick);
        }

        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        public void ClearBefore_NonPositiveOrFirstTick_PreservesAllEntries(int cleanupTick)
        {
            // KlothoEngine's CleanupOldData uses `cleanupTick = Math.Min(rawCleanupTick, _lastVerifiedTick)`
            // and gates with `if (cleanupTick > 0)`, so transient game-start state (_lastVerifiedTick = -1)
            // never reaches InputBuffer.ClearBefore. Lock in the buffer's own behaviour for cleanup ticks
            // at or below the smallest seeded tick — no entries should be removed.
            _buffer.AddCommand(new EmptyCommand(0, 1));
            _buffer.AddCommand(new EmptyCommand(0, 2));
            _buffer.AddCommand(new EmptyCommand(0, 3));

            _buffer.ClearBefore(cleanupTick);

            Assert.AreEqual(3, _buffer.Count, $"ClearBefore({cleanupTick}) must not remove any entries");
            Assert.IsTrue(_buffer.HasCommandForTick(1));
            Assert.IsTrue(_buffer.HasCommandForTick(2));
            Assert.IsTrue(_buffer.HasCommandForTick(3));
        }

        #endregion

        #region Seal guard — last line of defense against silent desync

        [Test]
        public void SealEmpty_MarksTickPlayerPair_IsSealedReturnsTrueForExactMatchOnly()
        {
            _buffer.SealEmpty(tick: 10, playerId: 1);

            Assert.IsTrue(_buffer.IsSealed(10, 1), "Exact (tick, playerId) match must be sealed");
            Assert.IsFalse(_buffer.IsSealed(10, 2), "Different playerId at same tick must not be sealed");
            Assert.IsFalse(_buffer.IsSealed(11, 1), "Different tick for same playerId must not be sealed");
            Assert.IsFalse(_buffer.IsSealed(0, 0), "Unrelated (tick, playerId) must not be sealed");
        }

        [Test]
        public void AddCommand_OnSealedTickPlayer_SilentDropsLateRealCommand()
        {
            // Seal first, then attempt to add a real command for the same (tick, playerId).
            // The seal guard drops the late real packet to keep buffer and simulation state consistent.
            _buffer.SealEmpty(tick: 10, playerId: 1);

            int countBefore = _buffer.Count;
            _buffer.AddCommand(new EmptyCommand(playerId: 1, tick: 10));

            Assert.AreEqual(countBefore, _buffer.Count,
                "AddCommand on sealed (tick, playerId) must be silently dropped — buffer count unchanged");
            Assert.IsFalse(_buffer.HasCommandForTick(10),
                "Sealed slot must remain empty after AddCommand drop");

            // Sealing must NOT affect AddCommand at a different (tick, playerId) — guard is per-key.
            _buffer.AddCommand(new EmptyCommand(playerId: 2, tick: 10));
            Assert.IsTrue(_buffer.HasCommandForTick(10),
                "AddCommand at different playerId on same tick must still succeed");
        }

        [Test]
        public void ClearBefore_RemovesSealsAtTicksBelowCleanup_PreservesSealsAtAndAbove()
        {
            // ClearBefore must keep seals in lockstep with buffer entries — stale seals below cleanup
            // would otherwise persist and block legitimate future AddCommand calls forever.
            _buffer.SealEmpty(tick: 5, playerId: 1);
            _buffer.SealEmpty(tick: 8, playerId: 1);
            _buffer.SealEmpty(tick: 12, playerId: 1);

            _buffer.ClearBefore(10);

            Assert.IsFalse(_buffer.IsSealed(5, 1),
                "Seal at tick 5 (< cleanup 10) must be removed");
            Assert.IsFalse(_buffer.IsSealed(8, 1),
                "Seal at tick 8 (< cleanup 10) must be removed");
            Assert.IsTrue(_buffer.IsSealed(12, 1),
                "Seal at tick 12 (>= cleanup 10) must be preserved");
        }

        [Test]
        public void Unseal_RemovesSeal_RestoresAddCommandAtSealedTick()
        {
            // A sealed-but-no-command hole (rollback ClearAfter clears the command
            // but keeps the seal) cannot be repopulated — AddCommand is dropped by the seal guard,
            // freezing the chain. Unseal lets the range-fill authority restore its empty placeholder.
            _buffer.SealEmpty(tick: 10, playerId: 1);
            _buffer.AddCommand(new EmptyCommand(playerId: 1, tick: 10));
            Assert.IsFalse(_buffer.HasCommandForTick(10),
                "Precondition: sealed-no-command hole — AddCommand dropped by seal guard");

            _buffer.Unseal(tick: 10, playerId: 1);
            Assert.IsFalse(_buffer.IsSealed(10, 1), "Unseal must clear the seal");

            _buffer.AddCommand(new EmptyCommand(playerId: 1, tick: 10));
            Assert.IsTrue(_buffer.HasCommandForTick(10),
                "After Unseal, AddCommand at the formerly-sealed (tick, playerId) must succeed — hole repopulated");

            // Unseal is per-key: must not affect other seals.
            _buffer.SealEmpty(tick: 11, playerId: 1);
            _buffer.Unseal(tick: 10, playerId: 1); // idempotent / unrelated
            Assert.IsTrue(_buffer.IsSealed(11, 1), "Unseal at (10,1) must not clear seal at (11,1)");
        }

        #endregion

        #region Duplicate

        [Test]
        public void AddCommand_SamePlayerSameTick_KeepsFirst()
        {
            var cmd1 = new MoveCommand(0, 10, new FPVector3(FP64.FromRaw(100), FP64.Zero, FP64.Zero));
            var cmd2 = new MoveCommand(0, 10, new FPVector3(FP64.FromRaw(200), FP64.Zero, FP64.Zero));

            _buffer.AddCommand(cmd1);
            _buffer.AddCommand(cmd2);

            // Keep-first: the duplicate arrival is dropped (no pool return — the
            // same instance can reach AddCommand twice via re-subscribed dispatch, and a double
            // return would lend one instance to two slots); the stored instance survives.
            var commands = _buffer.GetCommands(10).ToList();
            Assert.AreEqual(1, commands.Count);

            var retrieved = (MoveCommand)_buffer.GetCommand(10, 0);
            Assert.AreEqual(FP64.FromRaw(100), retrieved.Target.x);
            Assert.AreSame(cmd1, retrieved);
        }

        #endregion

        #region AddCommandChecked — ownership contract

        [Test]
        public void AddCommandChecked_EmptySlot_ReturnsStored()
        {
            var result = _buffer.AddCommandChecked(new EmptyCommand(playerId: 0, tick: 10));

            Assert.AreEqual(CommandStoreResult.Stored, result);
            Assert.IsTrue(_buffer.HasCommandForTick(10, 0));
        }

        [Test]
        public void AddCommandChecked_Null_ReturnsDroppedNull()
        {
            Assert.AreEqual(CommandStoreResult.DroppedNull, _buffer.AddCommandChecked(null));
            Assert.AreEqual(0, _buffer.Count);
        }

        [Test]
        public void AddCommandChecked_OverwriteDifferentInstance_ReturnsReplaced_AndSwapsInstance()
        {
            var predicted = new MoveCommand(0, 10, new FPVector3(FP64.FromRaw(100), FP64.Zero, FP64.Zero));
            var verified = new MoveCommand(0, 10, new FPVector3(FP64.FromRaw(200), FP64.Zero, FP64.Zero));
            _buffer.AddCommandChecked(predicted);

            var result = _buffer.AddCommandChecked(verified, overwriteExisting: true);

            Assert.AreEqual(CommandStoreResult.Replaced, result);
            Assert.AreSame(verified, _buffer.GetCommand(10, 0),
                "Overwrite must store the verified instance; the displaced one is left to GC");
        }

        [Test]
        public void AddCommandChecked_SameInstance_NonOverwrite_ReturnsAlreadyStored()
        {
            // The double-dispatch shape: the SAME arrival instance re-enters AddCommand.
            // Must be AlreadyStored (buffer property — caller must NOT Return), not
            // DroppedDuplicate, in BOTH overwrite modes.
            var cmd = new EmptyCommand(playerId: 0, tick: 10);
            _buffer.AddCommandChecked(cmd);

            Assert.AreEqual(CommandStoreResult.AlreadyStored, _buffer.AddCommandChecked(cmd));
            Assert.AreSame(cmd, _buffer.GetCommand(10, 0));
        }

        [Test]
        public void AddCommandChecked_SameInstance_Overwrite_ReturnsAlreadyStored()
        {
            var cmd = new EmptyCommand(playerId: 0, tick: 10);
            _buffer.AddCommandChecked(cmd, overwriteExisting: true);

            Assert.AreEqual(CommandStoreResult.AlreadyStored,
                _buffer.AddCommandChecked(cmd, overwriteExisting: true));
            Assert.AreSame(cmd, _buffer.GetCommand(10, 0));
        }

        [Test]
        public void AddCommandChecked_DifferentInstance_NonOverwrite_ReturnsDroppedDuplicate_KeepsFirst()
        {
            var first = new EmptyCommand(playerId: 0, tick: 10);
            var dup = new EmptyCommand(playerId: 0, tick: 10);
            _buffer.AddCommandChecked(first);

            var result = _buffer.AddCommandChecked(dup);

            Assert.AreEqual(CommandStoreResult.DroppedDuplicate, result);
            Assert.AreSame(first, _buffer.GetCommand(10, 0), "Keep-first must survive (IMP59 V2-H3)");
        }

        [Test]
        public void AddCommandChecked_Sealed_ReturnsDroppedSealed_WithoutPoolReturn()
        {
            // The buffer no longer returns the sealed-drop arrival to CommandPool —
            // disposal moved to checked callers. Pin with the pool count.
            CommandPool.ClearAll();
            _buffer.SealEmpty(tick: 10, playerId: 1);
            var late = new EmptyCommand(playerId: 1, tick: 10);

            int pooledBefore = CommandPool.GetTotalPooledCount();
            var result = _buffer.AddCommandChecked(late);

            Assert.AreEqual(CommandStoreResult.DroppedSealed, result);
            Assert.AreEqual(pooledBefore, CommandPool.GetTotalPooledCount(),
                "Sealed drop must not return the arrival to the pool inside the buffer");
            Assert.IsFalse(_buffer.HasCommandForTick(10), "Sealed slot must remain empty");
        }

        #endregion

        #region GetCommandList

        [Test]
        public void GetCommandList_EmptyTick_ReturnsEmptyList()
        {
            var list = _buffer.GetCommandList(100);
            Assert.IsNotNull(list);
            Assert.AreEqual(0, list.Count);
        }

        #endregion
    }

    /// <summary>
    /// SimpleInputPredictor tests
    /// </summary>
    [TestFixture]
    public class SimpleInputPredictorTests
    {
        [Test]
        public void PredictInput_NoPreviousCommands_ReturnsEmptyCommand()
        {
            var predictor = new SimpleInputPredictor();
            var predicted = predictor.PredictInput(0, 10, new List<ICommand>());

            Assert.IsNotNull(predicted);
            Assert.IsInstanceOf<EmptyCommand>(predicted);
            Assert.AreEqual(0, predicted.PlayerId);
            Assert.AreEqual(10, predicted.Tick);
        }

        [Test]
        public void PredictInput_WithPreviousCommands_ReturnsLastCommand()
        {
            var predictor = new SimpleInputPredictor();
            var previousCommands = new List<ICommand>
            {
                new MoveCommand(0, 5, new FPVector3(FP64.FromRaw(100), FP64.Zero, FP64.Zero)),
                new MoveCommand(0, 8, new FPVector3(FP64.FromRaw(200), FP64.Zero, FP64.Zero)),
                new MoveCommand(0, 6, new FPVector3(FP64.FromRaw(150), FP64.Zero, FP64.Zero))
            };

            var predicted = predictor.PredictInput(0, 10, previousCommands);

            Assert.IsInstanceOf<MoveCommand>(predicted);
            Assert.AreEqual(10, predicted.Tick);
        }

        // The predictor no longer judges correctness itself — the engine passes the
        // byte-equality verdict (one CommandDataEquals shared with the rollback decision), so the
        // old type-only judgment (same type, different payload counted as a hit) is gone.
        [Test]
        public void UpdateAccuracy_CorrectOutcome_IncreasesAccuracy()
        {
            var predictor = new SimpleInputPredictor();

            predictor.UpdateAccuracy(wasCorrect: true);

            Assert.AreEqual(1.0f, predictor.Accuracy, 0.01f);
        }

        [Test]
        public void UpdateAccuracy_IncorrectOutcome_DecreasesAccuracy()
        {
            var predictor = new SimpleInputPredictor();

            predictor.UpdateAccuracy(wasCorrect: false);

            Assert.AreEqual(0.0f, predictor.Accuracy, 0.01f);
        }

        [Test]
        public void UpdateAccuracy_MixedOutcomes_TracksRatio()
        {
            var predictor = new SimpleInputPredictor();

            predictor.UpdateAccuracy(wasCorrect: true);
            predictor.UpdateAccuracy(wasCorrect: true);
            predictor.UpdateAccuracy(wasCorrect: false);
            predictor.UpdateAccuracy(wasCorrect: true);

            Assert.AreEqual(0.75f, predictor.Accuracy, 0.01f);
        }
    }
}
