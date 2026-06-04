using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace xpTURN.Klotho.Generator.Analyzers
{
    /// <summary>
    /// Flags floating-point and non-deterministic API usage inside deterministic simulation code,
    /// where it would cause cross-platform desync. Simulation must compute only with FP64/FPVector.
    /// View/Unity code is unaffected (it is never a deterministic context).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class DeterminismAnalyzer : DiagnosticAnalyzer
    {
        private const string Category = "KlothoGenerator.Determinism";

        public static readonly DiagnosticDescriptor FloatInSimContext = new DiagnosticDescriptor(
            "KLOTHO_DET002",
            "Floating-point type in deterministic context",
            "'{0}' uses floating-point ('{1}') in a deterministic context; compute with FP64/FPVector instead",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor NonDeterministicApi = new DiagnosticDescriptor(
            "KLOTHO_DET003",
            "Non-deterministic API or type in deterministic context",
            "'{0}' accesses '{1}' in a deterministic context; it is not bit-exact across platforms — use the FP64/FPVector equivalent",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor WallClockInSimContext = new DiagnosticDescriptor(
            "KLOTHO_DET004",
            "Wall-clock time in deterministic context",
            "'{0}' reads 'UnityEngine.Time' in a deterministic context; use the fixed simulation tick/dt from Frame instead",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(FloatInSimContext, NonDeterministicApi, WallClockInSimContext);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            // Test and tool assemblies legitimately use float (tolerance comparisons, probes) and are
            // not determinism-bound — skip them wholesale.
            var assemblyName = context.Compilation.AssemblyName ?? string.Empty;
            if (assemblyName.IndexOf("Test", System.StringComparison.OrdinalIgnoreCase) >= 0
                || assemblyName.IndexOf("Editor", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }

            var known = KnownSymbols.Resolve(context.Compilation);
            if (!known.HasAnyContextSymbol)
            {
                // The deterministic interfaces/bases are not referenced here — no sim code possible.
                return;
            }

            context.RegisterSymbolStartAction(symbolStart =>
            {
                var type = (INamedTypeSymbol)symbolStart.Symbol;

                bool isRule12 = known.ImplementsDeterministicInterface(type) || known.InheritsDeterministicBase(type);
                bool hasRefFrame = type.GetMembers().OfType<IMethodSymbol>().Any(known.IsRefFrameMethod);
                if (!isRule12 && !hasRefFrame)
                {
                    return; // not a deterministic context
                }

                symbolStart.RegisterOperationAction(
                    op => AnalyzeOperation(op, known, isRule12),
                    OperationKind.Literal,
                    OperationKind.Binary,
                    OperationKind.Conversion,
                    OperationKind.VariableDeclarator,
                    OperationKind.Invocation,
                    OperationKind.FieldReference,
                    OperationKind.PropertyReference,
                    OperationKind.MethodReference,
                    OperationKind.ObjectCreation);
            }, SymbolKind.NamedType);
        }

        private static void AnalyzeOperation(OperationAnalysisContext context, KnownSymbols known, bool typeIsRule12)
        {
            // Scope gate: a rule-3-only type (no deterministic interface/base) is scanned only inside
            // its ref-Frame methods. A rule-1/2 type is scanned across all its members.
            if (!typeIsRule12)
            {
                if (!(context.ContainingSymbol is IMethodSymbol enclosing) || !known.IsRefFrameMethod(enclosing))
                {
                    return;
                }
            }

            var op = context.Operation;

            // DET003/004 — forbidden API/type access, never boundary-exempt (a non-deterministic source
            // hidden inside an FP64 boundary argument is still non-deterministic).
            INamedTypeSymbol memberOwner = MemberOwner(op);
            if (memberOwner != null)
            {
                if (known.IsWallClockType(memberOwner))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        WallClockInSimContext, op.Syntax.GetLocation(), EnclosingName(context), memberOwner.ToDisplayString()));
                    return;
                }
                if (known.IsForbiddenApiType(memberOwner))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        NonDeterministicApi, op.Syntax.GetLocation(), EnclosingName(context), memberOwner.ToDisplayString()));
                    return;
                }
            }

            // DET003 — floating-point-backed Unity value types (Vector3/Quaternion/...) escape the
            // float/double type check, so flag them by type.
            if (known.IsForbiddenValueType(op.Type as INamedTypeSymbol))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    NonDeterministicApi, op.Syntax.GetLocation(), EnclosingName(context), op.Type.ToDisplayString()));
                return;
            }

            // DET002 — float/double. Exempt the float-ness of FP64 conversion boundaries (FromFloat/
            // ToFloat/... and their argument subtree); forbidden sources inside are still caught above.
            if (IsFloatCandidate(op) && IsFloatType(op.Type) && !IsUnderFpBoundary(op, known))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    FloatInSimContext, op.Syntax.GetLocation(), EnclosingName(context), op.Type.ToDisplayString()));
            }
        }

        private static bool IsFloatCandidate(IOperation op)
        {
            switch (op.Kind)
            {
                case OperationKind.Literal:
                case OperationKind.Binary:
                case OperationKind.Conversion:
                    return true;
                case OperationKind.VariableDeclarator:
                    return op is IVariableDeclaratorOperation;
                default:
                    return false;
            }
        }

        private static bool IsFloatType(ITypeSymbol type)
            => type != null && (type.SpecialType == SpecialType.System_Single || type.SpecialType == SpecialType.System_Double);

        private static INamedTypeSymbol MemberOwner(IOperation op)
        {
            switch (op)
            {
                case IInvocationOperation inv: return inv.TargetMethod?.ContainingType;
                case IFieldReferenceOperation f: return f.Field?.ContainingType;
                case IPropertyReferenceOperation p: return p.Property?.ContainingType;
                case IMethodReferenceOperation m: return m.Method?.ContainingType;
                default: return null;
            }
        }

        private static bool IsUnderFpBoundary(IOperation op, KnownSymbols known)
        {
            for (var cur = op; cur != null; cur = cur.Parent)
            {
                if (cur is IInvocationOperation inv && known.IsFpBoundary(inv.TargetMethod))
                {
                    return true;
                }
            }
            return false;
        }

        private static string EnclosingName(OperationAnalysisContext context)
            => context.ContainingSymbol?.ToDisplayString() ?? "<unknown>";
    }
}
