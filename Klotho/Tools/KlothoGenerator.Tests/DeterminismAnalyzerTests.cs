using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;
using xpTURN.Klotho.Generator.Analyzers;

namespace xpTURN.Klotho.Generator.Tests
{
    [TestFixture]
    public class DeterminismAnalyzerTests
    {
        // Minimal stand-ins for the deterministic engine surface the analyzer resolves by metadata name.
        // Without these in the compilation, GetTypeByMetadataName returns null and nothing fires —
        // the analyzer would be silently blind. Their presence is what makes the positive tests meaningful.
        private const string Stub = @"
namespace xpTURN.Klotho.ECS
{
    public struct EntityRef { }
    public struct Frame { }
    public interface ISystem { void Update(ref Frame f); }
    public interface ICommandSystem { void OnCommand(ref Frame f, object cmd); }
    public interface IInitSystem { void OnInit(ref Frame f); }
    public interface IDestroySystem { void OnDestroy(ref Frame f); }
    public interface ISyncEventSystem { void EmitSyncEvents(ref Frame f); }
    public interface IEntityCreatedSystem { void OnEntityCreated(ref Frame f, EntityRef e); }
    public interface IEntityDestroyedSystem { void OnEntityDestroyed(ref Frame f, EntityRef e); }
    public interface IEntityPrototype { void Apply(Frame f, EntityRef e); }
    public interface ISignalOnComponentAdded<T> { void OnAdded(ref Frame f, EntityRef e); }
    public interface ISignalOnComponentRemoved<T> { void OnRemoved(ref Frame f, EntityRef e); }
}
namespace xpTURN.Klotho.ECS.FSM
{
    public struct AIContext { }
    public abstract class AIAction { public abstract void Execute(ref AIContext c); }
    public abstract class HFSMDecision { public abstract bool Decide(ref AIContext c); }
}
namespace xpTURN.Klotho.Core
{
    public abstract class CommandBase { }
    public abstract class SimulationEvent { }
}
namespace xpTURN.Klotho.Deterministic.Math
{
    public struct FP64
    {
        public static FP64 FromFloat(float v) => default;
        public static FP64 FromDouble(double v) => default;
        public float ToFloat() => 0f;
        public double ToDouble() => 0.0;
    }
    public static class FP64Extensions
    {
        public static FP64 ToFP64(this float v) => default;
        public static FP64 ToFP64(this double v) => default;
    }
}
namespace UnityEngine
{
    public struct Vector2 { public float x, y; }
    public struct Vector3 { public float x, y, z; }
    public struct Vector4 { public float x, y, z, w; }
    public struct Quaternion { public float x, y, z, w; }
    public struct Matrix4x4 { }
    public static class Mathf { public static float Sin(float x) => 0f; public static int Max(int a, int b) => a; }
    public static class Time { public static float deltaTime => 0f; }
    public static class Random { public static float value => 0f; }
}
";

        private static List<string> Diagnose(string source, string assemblyName = "BrawlerSim")
        {
            var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator)
                .Where(p => p.Length > 0)
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .ToList();

            var compilation = CSharpCompilation.Create(
                assemblyName,
                new[]
                {
                    CSharpSyntaxTree.ParseText(Stub),
                    CSharpSyntaxTree.ParseText(source),
                },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var withAnalyzers = compilation.WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new DeterminismAnalyzer()));

            return withAnalyzers.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult()
                .Where(d => d.Id.StartsWith("KLOTHO_DET"))
                .Select(d => d.Id)
                .ToList();
        }

        [Test]
        public void Det002_FloatLocalInSystem_Fires()
        {
            var ids = Diagnose(@"
using xpTURN.Klotho.ECS;
class S : ISystem { public void Update(ref Frame f) { float x = 1.5f; } }");
            Assert.That(ids, Does.Contain("KLOTHO_DET002"));
        }

        [Test]
        public void Det002_BoundaryLiteral_Exempt()
        {
            var ids = Diagnose(@"
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;
class S : ISystem { public void Update(ref Frame f) { var v = FP64.FromFloat(0.5f); } }");
            Assert.That(ids, Does.Not.Contain("KLOTHO_DET002"));
        }

        [Test]
        public void Det003_Mathf_Fires()
        {
            var ids = Diagnose(@"
using xpTURN.Klotho.ECS;
using UnityEngine;
class S : ISystem { public void Update(ref Frame f) { var r = Mathf.Sin(1f); } }");
            Assert.That(ids, Does.Contain("KLOTHO_DET003"));
        }

        [Test]
        public void Det003_SystemMathUnderBoundary_FiresButNoFloat()
        {
            var ids = Diagnose(@"
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;
class S : ISystem { public void Update(ref Frame f) { var v = FP64.FromDouble(System.Math.Sin(1.0)); } }");
            Assert.That(ids, Does.Contain("KLOTHO_DET003"));
            Assert.That(ids, Does.Not.Contain("KLOTHO_DET002"), "doubles inside the FP64 boundary must be exempt");
        }

        [Test]
        public void Det004_Time_Fires()
        {
            var ids = Diagnose(@"
using xpTURN.Klotho.ECS;
using UnityEngine;
class S : ISystem { public void Update(ref Frame f) { var t = Time.deltaTime; } }");
            Assert.That(ids, Does.Contain("KLOTHO_DET004"));
        }

        [Test]
        public void Det003_Vector3InSystem_Fires()
        {
            var ids = Diagnose(@"
using xpTURN.Klotho.ECS;
using UnityEngine;
class S : ISystem { public void Update(ref Frame f) { Vector3 v = default; } }");
            Assert.That(ids, Does.Contain("KLOTHO_DET003"));
        }

        [Test]
        public void RuleThreeOnlyHelper_RefFrameMethod_Fires()
        {
            // Static helper implements no interface/base — caught solely by rule 3 (ref Frame param).
            var ids = Diagnose(@"
using xpTURN.Klotho.ECS;
static class NavHelper { public static void Step(ref Frame f) { float x = 1f; } }");
            Assert.That(ids, Does.Contain("KLOTHO_DET002"));
        }

        [Test]
        public void NonContextType_NoDiagnostics()
        {
            var ids = Diagnose(@"
class Plain { void M() { float x = 1.5f; var y = System.Math.Sin(x); } }");
            Assert.That(ids, Is.Empty);
        }

        [Test]
        public void TestAssembly_Skipped()
        {
            var ids = Diagnose(@"
using xpTURN.Klotho.ECS;
class S : ISystem { public void Update(ref Frame f) { float x = 1.5f; } }",
                assemblyName: "Brawler.Tests");
            Assert.That(ids, Is.Empty);
        }

        [Test]
        public void Det002_AIActionBase_Fires()
        {
            var ids = Diagnose(@"
using xpTURN.Klotho.ECS.FSM;
class Atk : AIAction { public override void Execute(ref AIContext c) { float x = 2f; } }");
            Assert.That(ids, Does.Contain("KLOTHO_DET002"));
        }
    }
}
