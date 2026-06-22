using System;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;

using xpTURN.Klotho.Logging;
using xpTURN.Klotho.ECS.FSM;

namespace xpTURN.Klotho.ECS.Tests
{
    // ───────────────────────────────────────────────
    // Test value sources (engine-neutral; do not read Frame)
    // ───────────────────────────────────────────────

    /// <summary>Returns a captured constant — exercises the From() path without touching Frame.</summary>
    internal sealed class FixedIntSource : AIFunction<int>
    {
        private readonly int _value;
        public FixedIntSource(int value) => _value = value;
        public override int Resolve(ref AIContext context) => _value;
    }

    /// <summary>Holds a sub-AIParam and adds one — exercises chaining.</summary>
    internal sealed class AddOneSource : AIFunction<int>
    {
        private readonly AIParam<int> _inner;
        public AddOneSource(AIParam<int> inner) => _inner = inner;
        public override int Resolve(ref AIContext context) => _inner.Resolve(ref context) + 1;
    }

    [TestFixture]
    public class AIParamTests
    {
        private const int MaxEntities = 8;

        private IKLogger _logger;
        private Frame _frame;
        private EntityRef _entity;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = KLoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(KLogLevel.Trace);
                b.AddUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("AIParamTests");
        }

        [SetUp]
        public void SetUp()
        {
            _frame = new Frame(MaxEntities, _logger);
            _entity = _frame.CreateEntity();
        }

        private AIContext Context() => new AIContext { Frame = _frame, Entity = _entity };

        // ── Const path ──────────────────────────────────────────────
        [Test]
        public void Const_ResolvesToConstant()
        {
            var p = AIParam.Const(42);
            var ctx = Context();
            Assert.AreEqual(42, p.Resolve(ref ctx));
        }

        // ── From(source) path ───────────────────────────────────────
        [Test]
        public void From_ResolvesViaSource()
        {
            var p = AIParam.From(new FixedIntSource(7));
            var ctx = Context();
            Assert.AreEqual(7, p.Resolve(ref ctx));
        }

        // ── Source switch: same consumer, different source, different value ──
        [Test]
        public void SourceSwitch_ChangesResolvedValue_WithoutConsumerChange()
        {
            var ctx = Context();
            AIParam<int> asConst = AIParam.Const(1);
            AIParam<int> asFunc  = AIParam.From(new FixedIntSource(2));
            // The "consumer" is the identical Resolve call site below.
            Assert.AreEqual(1, asConst.Resolve(ref ctx));
            Assert.AreEqual(2, asFunc.Resolve(ref ctx));
        }

        // ── Chaining: a function resolves a sub-AIParam first ───────
        [Test]
        public void Chaining_ResolvesInnerThenOuter()
        {
            var p = AIParam.From(new AddOneSource(AIParam.Const(10)));
            var ctx = Context();
            Assert.AreEqual(11, p.Resolve(ref ctx));

            // Inner can itself be a function: From(AddOne(From(Fixed(10)))) => 11
            var nested = AIParam.From(new AddOneSource(AIParam.From(new FixedIntSource(10))));
            Assert.AreEqual(11, nested.Resolve(ref ctx));
        }

        // ── Guards ───────────────────────────────────────────────────
        [Test]
        public void From_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => AIParam.From<int>(null));
        }

        // ── GC: Resolve allocates nothing on either path ────────────
        [Test]
        public void Resolve_IsAllocationFree()
        {
            var constP = AIParam.Const(3);
            var funcP  = AIParam.From(new AddOneSource(AIParam.Const(10)));
            var frame  = _frame;
            var entity = _entity;

            Assert.That(() =>
            {
                var ctx = new AIContext { Frame = frame, Entity = entity };
                constP.Resolve(ref ctx);
                funcP.Resolve(ref ctx);
            }, UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory());
        }
    }
}
