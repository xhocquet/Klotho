using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace xpTURN.Klotho.Generator.Analyzers
{
    /// <summary>
    /// Per-compilation resolved symbols for determinism analysis: the interfaces/bases that mark a
    /// deterministic context, the Frame type, the FP64 conversion boundary, and the forbidden
    /// non-deterministic types. All lookups are null-tolerant (engine-free or Unity-free assemblies
    /// simply resolve fewer symbols).
    /// </summary>
    internal sealed class KnownSymbols
    {
        // rule 1 — deterministic interfaces (unbound generics stored by original definition).
        private readonly HashSet<INamedTypeSymbol> _interfaces;
        // rule 2 — deterministic base classes.
        private readonly HashSet<INamedTypeSymbol> _bases;
        // rule 3 — the Frame struct (matched as a ref parameter).
        private readonly INamedTypeSymbol _frame;

        // FP64 conversion boundary owners.
        private readonly HashSet<INamedTypeSymbol> _boundaryOwners;
        private static readonly HashSet<string> BoundaryMethodNames = new HashSet<string>
        {
            "FromFloat", "FromDouble", "ToFloat", "ToDouble", "ToFP64",
        };

        // DET003 — forbidden API owners (member access) and value types (by type).
        private readonly HashSet<INamedTypeSymbol> _forbiddenApiTypes;
        private readonly HashSet<INamedTypeSymbol> _forbiddenValueTypes;
        // DET004 — wall-clock.
        private readonly INamedTypeSymbol _unityTime;

        private static readonly SymbolEqualityComparer Cmp = SymbolEqualityComparer.Default;

        private KnownSymbols(
            HashSet<INamedTypeSymbol> interfaces, HashSet<INamedTypeSymbol> bases, INamedTypeSymbol frame,
            HashSet<INamedTypeSymbol> boundaryOwners, HashSet<INamedTypeSymbol> forbiddenApiTypes,
            HashSet<INamedTypeSymbol> forbiddenValueTypes, INamedTypeSymbol unityTime)
        {
            _interfaces = interfaces;
            _bases = bases;
            _frame = frame;
            _boundaryOwners = boundaryOwners;
            _forbiddenApiTypes = forbiddenApiTypes;
            _forbiddenValueTypes = forbiddenValueTypes;
            _unityTime = unityTime;
        }

        public bool HasAnyContextSymbol => _interfaces.Count > 0 || _bases.Count > 0 || _frame != null;

        public static KnownSymbols Resolve(Compilation compilation)
        {
            var interfaces = Collect(compilation,
                "xpTURN.Klotho.ECS.ISystem",
                "xpTURN.Klotho.ECS.ICommandSystem",
                "xpTURN.Klotho.ECS.IInitSystem",
                "xpTURN.Klotho.ECS.IDestroySystem",
                "xpTURN.Klotho.ECS.ISyncEventSystem",
                "xpTURN.Klotho.ECS.IEntityCreatedSystem",
                "xpTURN.Klotho.ECS.IEntityDestroyedSystem",
                "xpTURN.Klotho.ECS.IEntityPrototype",
                "xpTURN.Klotho.ECS.ISignalOnComponentAdded`1",
                "xpTURN.Klotho.ECS.ISignalOnComponentRemoved`1");

            var bases = Collect(compilation,
                "xpTURN.Klotho.ECS.FSM.AIAction",
                "xpTURN.Klotho.ECS.FSM.HFSMDecision",
                "xpTURN.Klotho.Core.CommandBase",
                "xpTURN.Klotho.Core.SimulationEvent");

            var frame = compilation.GetTypeByMetadataName("xpTURN.Klotho.ECS.Frame");

            var boundaryOwners = Collect(compilation,
                "xpTURN.Klotho.Deterministic.Math.FP64",
                "xpTURN.Klotho.Deterministic.Math.FP64Extensions");

            var forbiddenApiTypes = Collect(compilation,
                "UnityEngine.Mathf",
                "UnityEngine.Random",
                "System.Random",
                "System.DateTime",
                "System.Math");

            var forbiddenValueTypes = Collect(compilation,
                "UnityEngine.Vector2",
                "UnityEngine.Vector3",
                "UnityEngine.Vector4",
                "UnityEngine.Quaternion",
                "UnityEngine.Matrix4x4");

            var unityTime = compilation.GetTypeByMetadataName("UnityEngine.Time");

            return new KnownSymbols(interfaces, bases, frame, boundaryOwners, forbiddenApiTypes, forbiddenValueTypes, unityTime);
        }

        private static HashSet<INamedTypeSymbol> Collect(Compilation compilation, params string[] metadataNames)
        {
            var set = new HashSet<INamedTypeSymbol>(Cmp);
            foreach (var name in metadataNames)
            {
                var symbol = compilation.GetTypeByMetadataName(name);
                if (symbol != null)
                {
                    set.Add(symbol);
                }
            }
            return set;
        }

        public bool ImplementsDeterministicInterface(INamedTypeSymbol type)
        {
            if (_interfaces.Count == 0) return false;
            foreach (var iface in type.AllInterfaces)
            {
                if (_interfaces.Contains(iface.OriginalDefinition))
                {
                    return true;
                }
            }
            return false;
        }

        public bool InheritsDeterministicBase(INamedTypeSymbol type)
        {
            if (_bases.Count == 0) return false;
            for (var b = type.BaseType; b != null; b = b.BaseType)
            {
                if (_bases.Contains(b.OriginalDefinition))
                {
                    return true;
                }
            }
            return false;
        }

        public bool IsRefFrameMethod(IMethodSymbol method)
        {
            if (_frame == null) return false;
            foreach (var p in method.Parameters)
            {
                if (p.RefKind != RefKind.None && Cmp.Equals(p.Type, _frame))
                {
                    return true;
                }
            }
            return false;
        }

        public bool IsFpBoundary(IMethodSymbol method)
        {
            if (method == null || _boundaryOwners.Count == 0) return false;
            return BoundaryMethodNames.Contains(method.Name)
                && _boundaryOwners.Contains(method.ContainingType?.OriginalDefinition);
        }

        public bool IsForbiddenApiType(INamedTypeSymbol type)
            => type != null && _forbiddenApiTypes.Contains(type.OriginalDefinition);

        public bool IsForbiddenValueType(INamedTypeSymbol type)
            => type != null && _forbiddenValueTypes.Contains(type.OriginalDefinition);

        public bool IsWallClockType(INamedTypeSymbol type)
            => _unityTime != null && type != null && Cmp.Equals(type.OriginalDefinition, _unityTime);
    }
}
