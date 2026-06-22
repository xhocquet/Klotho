using System;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;

using xpTURN.Klotho.Logging;
using xpTURN.Klotho.ECS.FSM;

namespace xpTURN.Klotho.ECS.Tests
{
    // Two distinct HFSM axes on one entity — each embeds HFSMState as its FIRST field.
    [KlothoComponent(9100)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct MoveHFSMComponent : IComponent, IHFSMHost { public HFSMState State; }

    [KlothoComponent(9101)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct CombatHFSMComponent : IComponent, IHFSMHost { public HFSMState State; }

    /// <summary>Decision with a toggleable result (test-only).</summary>
    internal sealed class FlagDecision : HFSMDecision
    {
        public bool Value;
        public override bool Decide(ref AIContext context) => Value;
    }

    [TestFixture]
    public class CompoundHFSMTests
    {
        private const int MoveRootId = 9601, CombatRootId = 9602;   // root-id plane: avoid HFSMBuilderTests' 9100-9117
        private const int Idle = 0, Active = 1;   // flat 2-state ids (per-root scoped)
        private const int MaxEntities = 16;

        private IKLogger _logger;
        private Frame _frame;
        private EntityRef _entity;
        private FlagDecision _moveGate, _combatGate;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var f = KLoggerFactory.Create(b => { b.SetMinimumLevel(KLogLevel.Trace); b.AddUnityDebug(); });
            _logger = f.CreateLogger("CompoundHFSMTests");
        }

        [SetUp]
        public void SetUp()
        {
            HFSMRoot.Clear();
            _frame = new Frame(MaxEntities, _logger);
            _entity = _frame.CreateEntity();

            _moveGate = new FlagDecision();
            _combatGate = new FlagDecision();

            // Two independent flat roots: Idle --gate--> Active.
            new HFSMBuilder(MoveRootId).Default(Idle)
                .State(Idle).To(Active, _moveGate, priority: 10)
                .State(Active)
                .Build();
            new HFSMBuilder(CombatRootId).Default(Idle)
                .State(Idle).To(Active, _combatGate, priority: 10)
                .State(Active)
                .Build();
        }

        [Test]
        public void TwoAxes_OnOneEntity_AreIndependent()
        {
            HFSMManager.Init<MoveHFSMComponent>(ref _frame, _entity, MoveRootId);
            HFSMManager.Init<CombatHFSMComponent>(ref _frame, _entity, CombatRootId);

            Assert.AreEqual(Idle, HFSMManager.GetLeafStateId<MoveHFSMComponent>(ref _frame, _entity));
            Assert.AreEqual(Idle, HFSMManager.GetLeafStateId<CombatHFSMComponent>(ref _frame, _entity));

            // Drive only the Move axis.
            _moveGate.Value = true;
            _combatGate.Value = false;
            HFSMManager.Update<MoveHFSMComponent>(ref _frame, _entity);
            HFSMManager.Update<CombatHFSMComponent>(ref _frame, _entity);

            Assert.AreEqual(Active, HFSMManager.GetLeafStateId<MoveHFSMComponent>(ref _frame, _entity), "Move advanced");
            Assert.AreEqual(Idle,   HFSMManager.GetLeafStateId<CombatHFSMComponent>(ref _frame, _entity), "Combat untouched");

            // Now drive only Combat.
            _moveGate.Value = false;
            _combatGate.Value = true;
            HFSMManager.Update<CombatHFSMComponent>(ref _frame, _entity);

            Assert.AreEqual(Active, HFSMManager.GetLeafStateId<MoveHFSMComponent>(ref _frame, _entity), "Move stays Active");
            Assert.AreEqual(Active, HFSMManager.GetLeafStateId<CombatHFSMComponent>(ref _frame, _entity), "Combat advanced");
        }

        [Test]
        public void Rollback_RestoresBothAxes()
        {
            HFSMManager.Init<MoveHFSMComponent>(ref _frame, _entity, MoveRootId);
            HFSMManager.Init<CombatHFSMComponent>(ref _frame, _entity, CombatRootId);
            ulong before = _frame.CalculateHash();

            var snapshot = new Frame(MaxEntities, _logger);
            snapshot.CopyFrom(_frame);

            _moveGate.Value = true; _combatGate.Value = true;
            HFSMManager.Update<MoveHFSMComponent>(ref _frame, _entity);
            HFSMManager.Update<CombatHFSMComponent>(ref _frame, _entity);
            Assert.AreNotEqual(before, _frame.CalculateHash(), "state changed");

            _frame.CopyFrom(snapshot);   // whole-heap restore (both axes)
            Assert.AreEqual(before, _frame.CalculateHash(), "rollback restores both axes");
            Assert.AreEqual(Idle, HFSMManager.GetLeafStateId<MoveHFSMComponent>(ref _frame, _entity));
            Assert.AreEqual(Idle, HFSMManager.GetLeafStateId<CombatHFSMComponent>(ref _frame, _entity));
        }

        [Test]
        public void HFSMState_IsFirstField_OfEveryHost()
        {
            foreach (var t in new[] { typeof(HFSMComponent), typeof(MoveHFSMComponent), typeof(CombatHFSMComponent) })
            {
                Assert.AreEqual(IntPtr.Zero, Marshal.OffsetOf(t, "State"),
                    $"{t.Name}: HFSMState 'State' must be the first field (offset 0) for Unsafe.As reinterpret");
            }
        }

        [Test]
        public void Update_IsAllocationFree()
        {
            HFSMManager.Init<MoveHFSMComponent>(ref _frame, _entity, MoveRootId);
            _moveGate.Value = false;
            // warm up
            HFSMManager.Update<MoveHFSMComponent>(ref _frame, _entity);

            var frame = _frame; var entity = _entity;
            Assert.That(() =>
            {
                HFSMManager.Update<MoveHFSMComponent>(ref frame, entity);
            }, UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory());
        }
    }
}
