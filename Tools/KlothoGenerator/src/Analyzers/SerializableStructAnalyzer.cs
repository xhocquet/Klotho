using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using xpTURN.Klotho.Generator.Model;
using xpTURN.Klotho.Generator.Utils;

namespace xpTURN.Klotho.Generator.Analyzers
{
    /// <summary>
    /// Analyzes a [KlothoSerializableStruct] struct — a reusable inline-serialized field bundle.
    /// Mirrors <see cref="ComponentAnalyzer"/> (partial / unmanaged / StructLayout + field codec)
    /// minus the IComponent / TYPE_ID requirements. Reuses <see cref="ComponentTypeInfo"/> as the
    /// data carrier; ComponentTypeId is unused.
    /// </summary>
    internal static class SerializableStructAnalyzer
    {
        private const string StructLayoutAttribute = "System.Runtime.InteropServices.StructLayoutAttribute";

        public static ComponentAnalyzeResult Analyze(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
        {
            var result = new ComponentAnalyzeResult();

            var symbol = ctx.TargetSymbol as INamedTypeSymbol;
            if (symbol == null) return result;

            var structDecl = ctx.TargetNode as StructDeclarationSyntax;
            if (structDecl == null) return result;

            var location = structDecl.Identifier.GetLocation();

            if (!structDecl.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                result.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.SerializableStructMissingPartial, location, symbol.Name));
                return result;
            }

            if (!symbol.IsUnmanagedType)
            {
                result.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.SerializableStructNotUnmanaged, location, symbol.Name));
                return result;
            }

            CheckStructLayout(symbol, location, result);

            var info = new ComponentTypeInfo
            {
                Namespace = symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString(),
                TypeName = symbol.Name,
                FullTypeName = symbol.ToDisplayString(),
            };

            foreach (var member in symbol.GetMembers())
            {
                ct.ThrowIfCancellationRequested();

                if (member is not IFieldSymbol fs) continue;
                if (fs.IsStatic || fs.IsConst) continue;

                if (fs.IsFixedSizeBuffer)
                {
                    var elementType = ((IPointerTypeSymbol)fs.Type).PointedAtType.ToDisplayString();
                    info.Fields.Add(new ComponentFieldInfo
                    {
                        Name = fs.Name,
                        TypeFullName = fs.Type.ToDisplayString(),
                        IsFixed = true,
                        FixedSize = fs.FixedSize,
                        ElementType = elementType,
                    });
                    continue;
                }

                if (ComponentAnalyzer.IsNestedSerializableStruct(fs))
                {
                    info.Fields.Add(new ComponentFieldInfo
                    {
                        Name = fs.Name,
                        TypeFullName = fs.Type.ToDisplayString(),
                        IsNestedSerializable = true,
                    });
                    continue;
                }

                info.Fields.Add(new ComponentFieldInfo
                {
                    Name = fs.Name,
                    TypeFullName = fs.Type.ToDisplayString(),
                });
            }

            // Unsupported inner field types — same gate as components.
            foreach (var field in info.Fields)
            {
                if (field.IsNestedSerializable) continue;

                if (field.IsFixed)
                {
                    if (!TypeMappings.TryGetMapping(field.ElementType, out _))
                    {
                        result.Diagnostics.Add(Diagnostic.Create(
                            DiagnosticDescriptors.UnsupportedFieldType, location, field.Name, field.ElementType));
                        result.TypeInfo = null;
                        return result;
                    }
                    continue;
                }

                if (!TypeMappings.TryGetMapping(field.TypeFullName, out _))
                {
                    result.Diagnostics.Add(Diagnostic.Create(
                        DiagnosticDescriptors.UnsupportedFieldType, location, field.Name, field.TypeFullName));
                    result.TypeInfo = null;
                    return result;
                }
            }

            result.TypeInfo = info;
            return result;
        }

        private static void CheckStructLayout(INamedTypeSymbol symbol, Location location, ComponentAnalyzeResult result)
        {
            AttributeData attr = null;
            foreach (var a in symbol.GetAttributes())
            {
                if (a.AttributeClass?.ToDisplayString() == StructLayoutAttribute) { attr = a; break; }
            }
            if (attr == null)
            {
                result.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.SerializableStructStructLayoutMissing, location, symbol.Name));
                return;
            }

            if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is int lk && lk != 0)
            {
                result.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.SerializableStructStructLayoutMissing, location, symbol.Name));
                return;
            }

            bool hasPack4 = attr.NamedArguments.Any(na => na.Key == "Pack" && na.Value.Value is int pack && pack == 4);
            if (!hasPack4)
            {
                result.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.SerializableStructStructLayoutMissing, location, symbol.Name));
            }
        }
    }
}
