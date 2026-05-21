using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;

namespace xpTURN.Klotho.ECS.Tests
{
    // Dummy singleton component for guard / API testing.
    // typeId 9003 sits in the Tests range (9000+) to avoid collision with framework/sample slots.
    [KlothoComponent(9003)]
    [KlothoSingletonComponent]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct DummySingletonComponent : IComponent
    {
        public int Value;
    }

    // Non-singleton counterpart at typeId 9004 — verifies the guard does not fire
    // on components without the [KlothoSingletonComponent] attribute.
    [KlothoComponent(9004)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct DummyNonSingletonComponent : IComponent
    {
        public int Value;
    }

    [TestFixture]
    public class SingletonComponentTests
    {
        private Frame _frame;
        ILogger _logger = null;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("Tests");
        }

        [SetUp]
        public void SetUp()
        {
            _frame = new Frame(16, _logger);
        }

        [Test]
        public void SingletonGuard_SecondAdd_Throws()
        {
            var e1 = _frame.CreateEntity();
            _frame.Add(e1, new DummySingletonComponent { Value = 1 });

            var e2 = _frame.CreateEntity();
            Assert.Throws<InvalidOperationException>(() =>
                _frame.Add(e2, new DummySingletonComponent { Value = 2 }));
        }

        [Test]
        public void NonSingleton_MultipleAdd_DoesNotThrow()
        {
            var e1 = _frame.CreateEntity();
            var e2 = _frame.CreateEntity();
            _frame.Add(e1, new DummyNonSingletonComponent { Value = 1 });
            Assert.DoesNotThrow(() =>
                _frame.Add(e2, new DummyNonSingletonComponent { Value = 2 }));
        }

        [Test]
        public void GetSingleton_Mutable_RoundtripsWrites()
        {
            var entity = _frame.CreateEntity();
            _frame.Add(entity, new DummySingletonComponent { Value = 42 });

            ref var c = ref _frame.GetSingleton<DummySingletonComponent>();
            Assert.AreEqual(42, c.Value);

            c.Value = 99;
            Assert.AreEqual(99, _frame.GetSingleton<DummySingletonComponent>().Value);
        }

        [Test]
        public void GetReadOnlySingleton_ReturnsExpectedValue()
        {
            var entity = _frame.CreateEntity();
            _frame.Add(entity, new DummySingletonComponent { Value = 7 });

            ref readonly var c = ref _frame.GetReadOnlySingleton<DummySingletonComponent>();
            Assert.AreEqual(7, c.Value);
        }

        [Test]
        public void GetSingleton_NoEntity_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
                _frame.GetSingleton<DummySingletonComponent>());
        }

        [Test]
        public void GetReadOnlySingleton_NoEntity_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                ref readonly var _ = ref _frame.GetReadOnlySingleton<DummySingletonComponent>();
            });
        }

        [Test]
        public void TryGetSingleton_NoEntity_ReturnsFalse()
        {
            bool ok = _frame.TryGetSingleton<DummySingletonComponent>(out var entity);
            Assert.IsFalse(ok);
            Assert.IsFalse(entity.IsValid);
        }

        [Test]
        public void TryGetSingleton_OneEntity_ReturnsTrueWithCorrectEntity()
        {
            var created = _frame.CreateEntity();
            _frame.Add(created, new DummySingletonComponent { Value = 5 });

            bool ok = _frame.TryGetSingleton<DummySingletonComponent>(out var entity);
            Assert.IsTrue(ok);
            Assert.AreEqual(created.Index, entity.Index);
        }
    }
}
