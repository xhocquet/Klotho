using System;
using System.Collections.Generic;
using System.Linq;

using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.ECS.FSM
{
    /// <summary>Thrown when a graph fails registration-time validation.</summary>
    public sealed class HFSMValidationException : ArgumentException
    {
        public HFSMValidationException(string message) : base(message) { }
    }

    /// <summary>
    /// Fluent assembler for <see cref="HFSMRoot"/>. Collects states/transitions in declaration order,
    /// validates the graph, sorts each state's transitions by descending priority (stable), and registers it.
    /// The runtime evaluates transitions in array order (not by the Priority field), so the stable
    /// descending sort is what gives the priority argument meaning.
    /// Build() runs once at init time, so the collection allocations here are not on a per-frame path.
    /// </summary>
    public sealed class HFSMBuilder
    {
        internal sealed class StateDef
        {
            public int StateId;
            public int ParentId;
            public int DefaultChildId;
            public AIAction[] OnEnterActions;
            public AIAction[] OnUpdateActions;
            public AIAction[] OnExitActions;
            public readonly List<HFSMTransitionNode> Transitions = new List<HFSMTransitionNode>();
        }

        private readonly int _rootId;
        private readonly IKLogger _logger;
        private readonly List<StateDef> _states = new List<StateDef>();

        private int _defaultStateId;
        private bool _defaultSet;

        /// <param name="rootId">Registry key for the assembled root.</param>
        /// <param name="logger">Target for advisory warnings. Null silences warnings (throws are unaffected).</param>
        public HFSMBuilder(int rootId, IKLogger logger = null)
        {
            _rootId = rootId;
            _logger = logger;
        }

        /// <summary>Declares a state. parentId/defaultChildId default to -1 (top-level leaf).
        /// They may reference a stateId that has not been declared yet; references are resolved at Build time.</summary>
        public StateBuilder State(int stateId, int parentId = -1, int defaultChildId = -1)
        {
            var def = new StateDef
            {
                StateId        = stateId,
                ParentId       = parentId,
                DefaultChildId = defaultChildId,
            };
            _states.Add(def);
            return new StateBuilder(this, def);
        }

        /// <summary>Sets the root's default (entry) state. Must be called before the first State().</summary>
        public HFSMBuilder Default(int stateId)
        {
            _defaultStateId = stateId;
            _defaultSet = true;
            return this;
        }

        /// <summary>Validates the graph and registers it via HFSMRoot.Register. Throws on a broken graph.
        /// strict=true promotes advisory findings (unreachable / duplicate priority / self-transition) to throws.</summary>
        public HFSMRoot Build(bool strict = false)
        {
            var root = BuildRoot(strict);
            HFSMRoot.Register(root);
            return root;
        }

        private HFSMRoot BuildRoot(bool strict)
        {
            if (!_defaultSet)
                throw new HFSMValidationException($"Default state not set for HFSM root {_rootId}");

            // Map stateId -> def, detecting duplicates and negative ids.
            var byId = new Dictionary<int, StateDef>(_states.Count);
            int maxId = -1;
            foreach (var s in _states)
            {
                if (s.StateId < 0)
                    throw new HFSMValidationException($"State id {s.StateId} must be >= 0 in HFSM root {_rootId}");
                if (byId.ContainsKey(s.StateId))
                    throw new HFSMValidationException($"Duplicate state {s.StateId} in HFSM root {_rootId}");
                byId.Add(s.StateId, s);
                if (s.StateId > maxId) maxId = s.StateId;
            }

            if (!byId.ContainsKey(_defaultStateId))
                throw new HFSMValidationException($"Default state {_defaultStateId} not declared in HFSM root {_rootId}");

            // Reference integrity + advisory findings (declaration order = deterministic).
            foreach (var s in _states)
            {
                if (s.ParentId >= 0 && !byId.ContainsKey(s.ParentId))
                    throw new HFSMValidationException($"State {s.StateId} parent {s.ParentId} not declared in HFSM root {_rootId}");
                if (s.DefaultChildId >= 0 && !byId.ContainsKey(s.DefaultChildId))
                    throw new HFSMValidationException($"State {s.StateId} defaultChild {s.DefaultChildId} not declared in HFSM root {_rootId}");

                var seenPriorities = new HashSet<int>();
                foreach (var t in s.Transitions)
                {
                    if (!byId.ContainsKey(t.TargetStateId))
                        throw new HFSMValidationException($"State {s.StateId} transitions to undeclared state {t.TargetStateId} in HFSM root {_rootId}");
                    if (t.TargetStateId == s.StateId)
                        Advisory(strict, $"State {s.StateId} has a self-transition (priority {t.Priority}) in HFSM root {_rootId}");
                    if (!seenPriorities.Add(t.Priority))
                        Advisory(strict, $"State {s.StateId} has duplicate transition priority {t.Priority} in HFSM root {_rootId}");
                }
            }

            // Dense array: the runtime indexes States by StateId directly, so 0..maxId must be gap-free.
            var nodes = new HFSMStateNode[maxId + 1];
            for (int i = 0; i <= maxId; i++)
            {
                if (!byId.TryGetValue(i, out var s))
                    throw new HFSMValidationException($"State id {i} missing — States must be dense 0..{maxId} (StateId==index) in HFSM root {_rootId}");

                nodes[i] = new HFSMStateNode
                {
                    StateId         = s.StateId,
                    ParentId        = s.ParentId,
                    DefaultChildId  = s.DefaultChildId,
                    OnEnterActions  = s.OnEnterActions,
                    OnUpdateActions = s.OnUpdateActions,
                    OnExitActions   = s.OnExitActions,
                    // Stable descending sort by priority — OrderBy* is stable, so equal priorities keep declaration order.
                    Transitions     = s.Transitions.OrderByDescending(t => t.Priority).ToArray(),
                };
            }

            ReportUnreachable(byId, strict);

            return new HFSMRoot
            {
                RootId         = _rootId,
                DefaultStateId = _defaultStateId,
                States         = nodes,
            };
        }

        // Reachability from the default state through transitions and the default-child chain.
        private void ReportUnreachable(Dictionary<int, StateDef> byId, bool strict)
        {
            var reached = new HashSet<int>();
            var queue = new Queue<int>();
            reached.Add(_defaultStateId);
            queue.Enqueue(_defaultStateId);
            while (queue.Count > 0)
            {
                var def = byId[queue.Dequeue()];
                if (def.DefaultChildId >= 0 && reached.Add(def.DefaultChildId))
                    queue.Enqueue(def.DefaultChildId);
                foreach (var t in def.Transitions)
                {
                    if (reached.Add(t.TargetStateId))
                        queue.Enqueue(t.TargetStateId);
                }
            }

            foreach (var s in _states)
            {
                if (!reached.Contains(s.StateId))
                    Advisory(strict, $"State {s.StateId} is unreachable from default state {_defaultStateId} in HFSM root {_rootId}");
            }
        }

        private void Advisory(bool strict, string message)
        {
            if (strict)
                throw new HFSMValidationException(message);
            if (_logger != null && _logger.IsEnabled(KLogLevel.Warning))
                _logger.Log(KLogLevel.Warning, message, null);
        }

        /// <summary>Per-state builder returned by State(...). Chains back to the parent HFSMBuilder.</summary>
        public sealed class StateBuilder
        {
            private readonly HFSMBuilder _builder;
            private readonly StateDef _def;

            internal StateBuilder(HFSMBuilder builder, StateDef def)
            {
                _builder = builder;
                _def = def;
            }

            /// <summary>Sets the current state's enter actions. May be called at most once.</summary>
            public StateBuilder OnEnter(params AIAction[] actions)
            {
                if (_def.OnEnterActions != null)
                    throw new HFSMValidationException($"State {_def.StateId} OnEnter set more than once in HFSM root {_builder._rootId}");
                _def.OnEnterActions = actions;
                return this;
            }

            /// <summary>Sets the current state's update actions. May be called at most once.</summary>
            public StateBuilder OnUpdate(params AIAction[] actions)
            {
                if (_def.OnUpdateActions != null)
                    throw new HFSMValidationException($"State {_def.StateId} OnUpdate set more than once in HFSM root {_builder._rootId}");
                _def.OnUpdateActions = actions;
                return this;
            }

            /// <summary>Sets the current state's exit actions. May be called at most once.</summary>
            public StateBuilder OnExit(params AIAction[] actions)
            {
                if (_def.OnExitActions != null)
                    throw new HFSMValidationException($"State {_def.StateId} OnExit set more than once in HFSM root {_builder._rootId}");
                _def.OnExitActions = actions;
                return this;
            }

            /// <summary>Adds a transition. Higher priority is evaluated first (Build sorts stably by descending priority).
            /// Multiple transitions to the same target are allowed.</summary>
            public StateBuilder To(int targetStateId, HFSMDecision decision, int priority, int eventId = 0)
            {
                _def.Transitions.Add(new HFSMTransitionNode
                {
                    Priority      = priority,
                    TargetStateId = targetStateId,
                    Decision      = decision,
                    EventId       = eventId,
                });
                return this;
            }

            /// <summary>Declares the next state.</summary>
            public StateBuilder State(int stateId, int parentId = -1, int defaultChildId = -1)
                => _builder.State(stateId, parentId, defaultChildId);

            /// <summary>Validates, sorts, and registers the graph.</summary>
            public HFSMRoot Build(bool strict = false) => _builder.Build(strict);
        }
    }
}
