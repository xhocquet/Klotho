using System;
using System.Collections.Generic;
using System.Diagnostics;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.ECS
{
    public class SystemRunner
    {
        private struct SystemEntry
        {
            public object System;
            public SystemPhase Phase;
            public int Order;
        }

        private readonly List<SystemEntry> _entries = new List<SystemEntry>();
        private SystemEntry[] _sorted;
        private bool _dirty = true;
        private int _nextOrder;

#if DEVELOPMENT_BUILD || UNITY_EDITOR || DEBUG
        private long[] _updateTimingTicks;
        private int _timingSampleCount;

        public readonly struct SystemTiming
        {
            public readonly string SystemName;
            public readonly double TotalMs;
            public readonly double AvgMs;

            public SystemTiming(string systemName, double totalMs, double avgMs)
            {
                SystemName = systemName;
                TotalMs = totalMs;
                AvgMs = avgMs;
            }
        }
#endif

        public void AddSystem(object system, SystemPhase phase)
        {
            if (system == null)
                throw new ArgumentNullException(nameof(system));

            _entries.Add(new SystemEntry
            {
                System = system,
                Phase = phase,
                Order = _nextOrder++
            });
            _dirty = true;
        }

        private void EnsureSorted()
        {
            if (!_dirty) return;

            _sorted = _entries.ToArray();
            Array.Sort(_sorted, (a, b) =>
            {
                int cmp = ((int)a.Phase).CompareTo((int)b.Phase);
                return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
            });
#if DEVELOPMENT_BUILD || UNITY_EDITOR || DEBUG
            _updateTimingTicks = new long[_sorted.Length];
            _timingSampleCount = 0;
#endif
            _dirty = false;
        }

        /// <summary>
        /// Returns the first registered system instance assignable to <typeparamref name="T"/>,
        /// or <c>null</c> if none. Type parameter must be a reference type (class or interface).
        /// Traversal order matches registration order.
        /// Lookup is O(N) over registered systems; cache the result if called on a hot path.
        /// </summary>
        public T Find<T>() where T : class
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].System is T match)
                    return match;
            }
            return null;
        }

        /// <summary>
        /// Appends all registered system instances assignable to <typeparamref name="T"/>
        /// into <paramref name="buffer"/>. Returns the count appended.
        /// Caller owns the buffer (alloc-free for the lookup itself).
        /// </summary>
        public int FindAll<T>(List<T> buffer) where T : class
        {
            int initial = buffer.Count;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].System is T match)
                    buffer.Add(match);
            }
            return buffer.Count - initial;
        }

        public void Init(ref Frame frame)
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is IInitSystem init)
                    init.OnInit(ref frame);
            }
        }

        public void Destroy(ref Frame frame)
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is IDestroySystem destroy)
                    destroy.OnDestroy(ref frame);
            }
        }

        public void RunUpdateSystems(ref Frame frame)
        {
            EnsureSorted();
            // Built-in: capture previous transform before any PreUpdate system runs.
            // Relies on SystemPhase.PreUpdate == 0 so this placement is equivalent to
            // running ahead of the first PreUpdate-phase ISystem after EnsureSorted ordering.
            Debug.Assert((int)SystemPhase.PreUpdate == 0,
                "SystemPhase.PreUpdate must remain the first enum value (0). " +
                "If the enum order changes, move SaveAllPreviousTransforms accordingly.");
            SaveAllPreviousTransforms(ref frame);
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is ISystem sys)
                {
#if DEVELOPMENT_BUILD || UNITY_EDITOR || DEBUG
                    long start = Stopwatch.GetTimestamp();
                    sys.Update(ref frame);
                    _updateTimingTicks[i] += Stopwatch.GetTimestamp() - start;
#else
                    sys.Update(ref frame);
#endif
                }
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR || DEBUG
            _timingSampleCount++;
#endif
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR || DEBUG
        public void ConsumeUpdateTimings(List<SystemTiming> buffer)
        {
            EnsureSorted();
            if (_timingSampleCount == 0) return;

            double msPerTick = 1000.0 / Stopwatch.Frequency;
            int sampleCount = _timingSampleCount;
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is not ISystem) continue;
                double totalMs = _updateTimingTicks[i] * msPerTick;
                buffer.Add(new SystemTiming(_sorted[i].System.GetType().Name, totalMs, totalMs / sampleCount));
                _updateTimingTicks[i] = 0;
            }
            _timingSampleCount = 0;
        }
#endif

        private static void SaveAllPreviousTransforms(ref Frame frame)
        {
            var filter = frame.Filter<TransformComponent>();
            while (filter.Next(out var entity))
            {
                ref var t = ref frame.Get<TransformComponent>(entity);
                t.PreviousPosition = t.Position;
                t.PreviousRotation = t.Rotation;
                t.PreviousInitialized = true;
            }
        }

        public void RunCommandSystems(ref Frame frame, ICommand command)
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is ICommandSystem cmdSys)
                    cmdSys.OnCommand(ref frame, command);
            }
        }

        public void OnComponentAdded<T>(ref Frame frame, EntityRef entity, ref T component)
            where T : unmanaged, IComponent
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is ISignalOnComponentAdded<T> sys)
                    sys.OnAdded(ref frame, entity, ref component);
            }
        }

        public void OnComponentRemoved<T>(ref Frame frame, EntityRef entity, T component)
            where T : unmanaged, IComponent
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is ISignalOnComponentRemoved<T> sys)
                    sys.OnRemoved(ref frame, entity, component);
            }
        }

        public void OnEntityCreated(ref Frame frame, EntityRef entity)
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is IEntityCreatedSystem sys)
                    sys.OnEntityCreated(ref frame, entity);
            }
        }

        public void OnEntityDestroyed(ref Frame frame, EntityRef entity)
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is IEntityDestroyedSystem sys)
                    sys.OnEntityDestroyed(ref frame, entity);
            }
        }

        public void EmitSyncEvents(ref Frame frame)
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is ISyncEventSystem syncSys)
                    syncSys.EmitSyncEvents(ref frame);
            }
        }

        public void Signal<TSignal>(ref Frame frame, SignalInvoker<TSignal> invoke)
            where TSignal : class, ISignal
        {
            EnsureSorted();
            for (int i = 0; i < _sorted.Length; i++)
            {
                if (_sorted[i].System is TSignal sys)
                    invoke(sys, ref frame);
            }
        }
    }
}
