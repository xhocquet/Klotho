# Brawler Appendix D — Bot HFSM Builder & Decision Predicates

> Related: [Brawler.md](Brawler.md) §10 (Phase 7 — Bot HFSM)
> Target: `BotHFSMRoot.Build()` assembly + Decision criteria
>
> ⚠️ **Note**: The Decision logic in §D-3 is **an example reflecting design intent**; the exact predicates in the actual source may vary depending on the difficulty / asset combination. Type names, method signatures, states, and transition priorities match the actual source.

---

## D-1. Structure Overview

```
BotFSMSystem (PreUpdate)
   │ — HFSM Tick for every entity holding BotComponent
   ▼
HFSMRoot (ECS.FSM namespace, registered by Id, Build() once)
   │
   ├── 5 states: Idle(0) / Chase(1) / Attack(2) / Evade(3) / Skill(4)
   │
   ├── Common transitions (evaluated in priority order in each state, **excluding self**):
   │     P=90: ShouldEvade      → Evade
   │     P=80: IsKnockback      → Chase
   │     P=70: InAttackRange    → Attack
   │     P=60: ShouldUseSkill   → Skill
   │     P=50: HasTarget        → Chase
   │     P=40: NoTarget         → Idle
   │
   ├── Evade-only transition:
   │     P=50: EvadeArrived     → Idle
   │
   ├── Skill-only transition:
   │     P=100: SkillActionDone → Chase
   │
   └── OnEnter / OnUpdate Actions per state
```

State-transition diagram:

```
 Idle ─HasTarget(50)→ Chase ─InAttackRange(70)→ Attack
  ↑                      │                         │
  NoTarget(40)      ShouldUseSkill(60)        SkillActionDone(100)
  │                      ▼                         │
  └─ ShouldEvade(90) → Evade                     Skill
                        │
                   EvadeArrived(50) → Idle
```

> **Note**: Transition targets — `T_Knockback` returns to `Chase`, and `T_SkillDone` returns to `Chase` (immediately re-enters the next decision loop after attack/skill completes).

---

## D-2. BotHFSMRoot.Build() — Actual Implementation

```csharp
using xpTURN.Klotho.ECS.FSM;

public static class BotHFSMRoot
{
    public const int Id = 1; // HFSMRoot registry key

    public const int Idle   = BotStateId.Idle;   // 0
    public const int Chase  = BotStateId.Chase;  // 1
    public const int Attack = BotStateId.Attack; // 2
    public const int Evade  = BotStateId.Evade;  // 3
    public const int Skill  = BotStateId.Skill;  // 4

    // Singleton Decisions / Actions
    static ShouldEvadeDecision     _shouldEvade;
    static IsKnockbackDecision     _isKnockback   = new IsKnockbackDecision();
    static InAttackRangeDecision   _inAttackRange;
    static ShouldUseSkillDecision  _shouldUseSkill;
    static HasTargetDecision       _hasTarget     = new HasTargetDecision();
    static NoTargetDecision        _noTarget      = new NoTargetDecision();
    static EvadeArrivedDecision    _evadeArrived  = new EvadeArrivedDecision();
    static SkillActionDoneDecision _skillDone     = new SkillActionDoneDecision();

    static ClearDestinationAction _clearDest  = new ClearDestinationAction();
    static EvadeEnterAction       _evadeEnter;
    static SkillUpdateAction      _skillUpdate;

    public static void Build(BotBehaviorAsset behavior, BotDifficultyAsset[] diffAssets,
                             BasicAttackConfigAsset attack, SkillConfigAsset[][] skills)
    {
        if (HFSMRoot.Has(Id)) return;   // prevent duplicate build

        // 1) Lazily initialize Decisions / Actions
        _shouldEvade    = new ShouldEvadeDecision(behavior, diffAssets);
        _inAttackRange  = new InAttackRangeDecision(attack);
        _shouldUseSkill = new ShouldUseSkillDecision(behavior, diffAssets, skills);
        _evadeEnter     = new EvadeEnterAction(behavior);
        _skillUpdate    = new SkillUpdateAction(behavior, diffAssets, skills);

        // 2) Assemble the graph fluently — each State() omits its own self-transition.
        //    To(...) adds a transition; Build() validates the graph (duplicate / dangling /
        //    non-dense ids, default-not-set, reachability) and stably sorts each state's
        //    transitions by descending priority before registering it under Id.
        new HFSMBuilder(Id)
            .Default(Idle)
            .State(Idle)                                       // excludes the self transition
                .OnEnter(_clearDest)
                .To(Evade,  _shouldEvade,    priority: 90)
                .To(Chase,  _isKnockback,    priority: 80)
                .To(Attack, _inAttackRange,  priority: 70)
                .To(Skill,  _shouldUseSkill, priority: 60)
                .To(Chase,  _hasTarget,      priority: 50)
            .State(Chase)                                      // excludes the hasTarget transition
                .To(Evade,  _shouldEvade,    priority: 90)
                .To(Chase,  _isKnockback,    priority: 80)
                .To(Attack, _inAttackRange,  priority: 70)
                .To(Skill,  _shouldUseSkill, priority: 60)
                .To(Idle,   _noTarget,       priority: 40)
            .State(Attack)                                     // excludes the self transition
                .OnEnter(_clearDest)
                .To(Evade,  _shouldEvade,    priority: 90)
                .To(Chase,  _isKnockback,    priority: 80)
                .To(Skill,  _shouldUseSkill, priority: 60)
                .To(Chase,  _hasTarget,      priority: 50)
                .To(Idle,   _noTarget,       priority: 40)
            .State(Evade)                                      // committed: single exit transition
                .OnEnter(_evadeEnter)
                .To(Idle,   _evadeArrived,   priority: 50)
            .State(Skill)                                      // committed: returns to Chase once the action lock clears
                .OnEnter(_clearDest)
                .OnUpdate(_skillUpdate)
                .To(Chase,  _skillDone,      priority: 100)
            .Build();
    }
}

public static class BotStateId
{
    public const int Idle   = 0;
    public const int Chase  = 1;
    public const int Attack = 2;
    public const int Evade  = 3;
    public const int Skill  = 4;
}
```

**Key types**:
- `HFSMBuilder` — Fluent assembler (`Default`, `State`, `OnEnter / OnUpdate / OnExit`, `To`, `Build`). `Build()` validates the graph at registration and fails fast on structural defects (duplicate / dangling / non-dense state ids, default-not-set), runs a reachability BFS, and stably sorts each state's transitions by descending priority — the runtime evaluates transitions in array order, so the sort is what gives `priority` its meaning. Advisory findings (unreachable / duplicate priority / self-transition) warn via `IKLogger` by default; `Build(strict: true)` promotes them to throws.
- `HFSMRoot` — Root registry (`.Register`, `.Has`) + the instance type itself (`RootId`, `DefaultStateId`, `States`). `HFSMBuilder.Build()` constructs and registers it for you.
- `HFSMStateNode` / `HFSMTransitionNode` — The node structs the builder emits (`OnEnter / OnUpdate / OnExit Actions`, `Transitions`; `Priority`, `TargetStateId`, `Decision`). Author via the builder rather than constructing arrays by hand.
- `AIAction` — Base for state enter / update actions
- `IBotDecision` — Predicate interface for transitions

Build timing: called once from `BrawlerSimulationCallbacks.RegisterSystems()` immediately after DataAssets are loaded.

---

## D-3. Decisions (Design Intent)

### D-3-1. ShouldEvadeDecision (Priority 90)

```csharp
public class ShouldEvadeDecision : IBotDecision
{
    private readonly BotBehaviorAsset _behavior;
    private readonly BotDifficultyAsset[] _difficulties;

    public bool Evaluate(ref Frame frame, EntityRef entity)
    {
        ref readonly var bot = ref frame.GetReadOnly<BotComponent>(entity);
        ref readonly var c   = ref frame.GetReadOnly<CharacterComponent>(entity);
        ref readonly var t   = ref frame.GetReadOnly<TransformComponent>(entity);

        if (bot.EvadeCooldown > 0) return false;

        var diff = _difficulties[(int)bot.Difficulty];

        // 1) Near the boundary
        FP64 boundary = _behavior.StageBoundary - diff.EvadeMargin;
        bool nearEdge = FP64.Abs(t.Position.x) >= boundary
                     || FP64.Abs(t.Position.z) >= boundary;

        // 2) Knockback accumulation high
        bool highKnockback = c.KnockbackPower >= diff.EvadeKnockbackPct;

        return nearEdge || highKnockback;
    }
}
```

**Thresholds**:
- Boundary: `StageBoundary(18) - EvadeMargin(Easy 1 / Normal 2 / Hard 3)` or higher
- Knockback: per-difficulty `EvadeKnockbackPct` (Easy 120% / Normal 80% / Hard 50%) or higher

### D-3-2. IsKnockbackDecision (Priority 80)

```csharp
public bool Evaluate(ref Frame frame, EntityRef entity)
    => frame.Has<KnockbackComponent>(entity);
```

If currently in knockback, transition to Chase (interrupting any attack / skill).

### D-3-3. InAttackRangeDecision (Priority 70)

```csharp
public bool Evaluate(ref Frame frame, EntityRef entity)
{
    ref readonly var bot = ref frame.GetReadOnly<BotComponent>(entity);
    if (bot.AttackCooldown > 0) return false;

    ref readonly var c = ref frame.GetReadOnly<CharacterComponent>(entity);
    if (c.ActionLockTicks > 0) return false;

    if (!bot.Target.IsValid) return false;
    if (!frame.Entities.IsAlive(bot.Target)) return false;

    ref readonly var tSelf = ref frame.GetReadOnly<TransformComponent>(entity);
    ref readonly var tTgt  = ref frame.GetReadOnly<TransformComponent>(bot.Target);
    FPVector2 diff = new FPVector2(tTgt.Position.x - tSelf.Position.x,
                                   tTgt.Position.z - tSelf.Position.z);
    return diff.sqrMagnitude <= _attack.MeleeRangeSqr;   // default 4.0
}
```

### D-3-4. ShouldUseSkillDecision (Priority 60)

Combined check on target validity, cooldown, per-difficulty delay, and (for ranged skills) line-of-sight. See `BotFSMHelper` for the detailed skill-selection logic.

```csharp
public bool Evaluate(ref Frame frame, EntityRef entity)
{
    ref readonly var bot = ref frame.GetReadOnly<BotComponent>(entity);
    ref readonly var c   = ref frame.GetReadOnly<CharacterComponent>(entity);
    if (c.ActionLockTicks > 0) return false;
    if (!bot.Target.IsValid) return false;

    // Skill selection (distance / knockback / type-based)
    var skill = BotFSMHelper.SelectBestSkill(ref frame, entity, bot.Target, _skills, _behavior);
    if (skill == null) return false;

    // Cooldown
    ref readonly var cd = ref frame.GetReadOnly<SkillCooldownComponent>(entity);
    int remaining = skill.Value.Slot == 0 ? cd.Skill0Cooldown : cd.Skill1Cooldown;
    if (remaining > 0) return false;

    // Per-difficulty extra delay
    var diff = _difficulties[(int)bot.Difficulty];
    if (new DeterministicRandom(frame.GetReadOnlySingleton<RandomSeedComponent>().Seed).NextInt(100) < diff.SkillExtraDelay) return false;

    // LOS check for ranged skills
    if (skill.Value.RequireLOS && !BotFSMHelper.HasLineOfSight(ref frame, entity, bot.Target, _behavior))
        return false;

    return true;
}
```

### D-3-5. HasTargetDecision / NoTargetDecision (Priority 50 / 40)

```csharp
public class HasTargetDecision : IBotDecision {
    public bool Evaluate(ref Frame frame, EntityRef entity) {
        ref readonly var bot = ref frame.GetReadOnly<BotComponent>(entity);
        return bot.Target.IsValid && frame.Entities.IsAlive(bot.Target);
    }
}

public class NoTargetDecision : IBotDecision {
    public bool Evaluate(ref Frame frame, EntityRef entity) {
        ref readonly var bot = ref frame.GetReadOnly<BotComponent>(entity);
        return !bot.Target.IsValid || !frame.Entities.IsAlive(bot.Target);
    }
}
```

> **Target re-acquisition**: Use `BotComponent.DecisionCooldown` to periodically call `BotFSMHelper.FindBestTarget` from `BotFSMSystem` (or a separate routine) and update `bot.Target`. The Decision itself only reads the current `bot.Target`.

### D-3-6. EvadeArrivedDecision / SkillActionDoneDecision

```csharp
public class EvadeArrivedDecision : IBotDecision {
    public bool Evaluate(ref Frame frame, EntityRef entity) {
        if (!frame.Has<NavAgentComponent>(entity)) return true;
        ref readonly var nav = ref frame.GetReadOnly<NavAgentComponent>(entity);
        return nav.Status == (byte)FPNavAgentStatus.Arrived
            || nav.Status == (byte)FPNavAgentStatus.Idle;
    }
}

public class SkillActionDoneDecision : IBotDecision {
    public bool Evaluate(ref Frame frame, EntityRef entity) {
        ref readonly var c = ref frame.GetReadOnly<CharacterComponent>(entity);
        return c.ActionLockTicks <= 0;
    }
}
```

---

## D-4. BotComponent — Actual Fields

```csharp
public enum BotState : byte     { Idle, Chase, Attack, Evade, Skill }
public enum BotDifficulty : byte { Easy = 0, Normal = 1, Hard = 2 }

[KlothoComponent(110)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public partial struct BotComponent : IComponent
{
    public byte        State;             // BotState
    public EntityRef   Target;
    public int         StateTimer;        // ticks spent in the current state
    public int         AttackCooldown;    // attack cooldown
    public int         DecisionCooldown;  // decision interval
    public byte        Difficulty;        // BotDifficulty
    public FPVector3   Destination;       // movement target (for skills / evade)
    public bool        HasDestination;
    public int         EvadeCooldown;     // re-evade cooldown
}
```

> **Correction vs. earlier doc versions**: The fields `TargetSearchCooldown` and `PendingSkillSlot` **do not exist**. Target re-acquisition uses `DecisionCooldown`; the skill slot is communicated via internal logic in `SkillUpdateAction` or via `CharacterComponent.ActiveSkillSlot`.

---

## D-5. Action Implementations

### ClearDestinationAction

```csharp
public class ClearDestinationAction : AIAction {
    public override void Execute(ref Frame frame, EntityRef entity) {
        ref var bot = ref frame.Get<BotComponent>(entity);
        bot.HasDestination = false;
        if (frame.Has<NavAgentComponent>(entity)) {
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            nav.HasNavDestination = false;
            nav.Status = (byte)FPNavAgentStatus.Idle;
        }
    }
}
```

### EvadeEnterAction

```csharp
public class EvadeEnterAction : AIAction
{
    private readonly BotBehaviorAsset _behavior;
    public EvadeEnterAction(BotBehaviorAsset behavior) { _behavior = behavior; }

    public override void Execute(ref Frame frame, EntityRef entity)
    {
        ref var bot = ref frame.Get<BotComponent>(entity);
        ref readonly var t = ref frame.GetReadOnly<TransformComponent>(entity);

        // Pick the EvadePoint farthest from the current position
        FPVector3 bestPt = _behavior.EvadePoints[0];
        FP64 bestDist = FP64.Zero;
        for (int i = 0; i < _behavior.EvadePoints.Length; i++) {
            FPVector3 diff = _behavior.EvadePoints[i] - t.Position;
            FP64 d = diff.sqrMagnitude;
            if (d > bestDist) { bestDist = d; bestPt = _behavior.EvadePoints[i]; }
        }

        bot.Destination    = bestPt;
        bot.HasDestination = true;
        bot.EvadeCooldown  = _behavior.EvadeCooldownTicks;

        if (frame.Has<NavAgentComponent>(entity)) {
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            nav.Destination       = bestPt;
            nav.HasNavDestination = true;
        }
    }
}
```

### SkillUpdateAction

`OnUpdate` Action — invoked every tick while in the Skill state. Picks the best skill via `BotFSMHelper.SelectBestSkill` and injects a `UseSkillCommand` directly into the command buffer (bots emit commands internally instead of going through `OnPollInput`).

---

## D-6. BotFSMHelper — Key Utilities

```csharp
public static class BotFSMHelper
{
    public static EntityRef FindBestTarget(ref Frame frame, EntityRef self, BotBehaviorAsset cfg);

    public static (int Slot, bool RequireLOS, FP64 Range)? SelectBestSkill(
        ref Frame frame, EntityRef self, EntityRef target,
        SkillConfigAsset[][] skills, BotBehaviorAsset cfg);

    public static bool HasLineOfSight(ref Frame frame, EntityRef self, EntityRef target, BotBehaviorAsset cfg);
}
```

---

## D-7. Spawn & Registration

Inside `BrawlerSimSetup.InitializeWorldState`, spawn `_botCount` entities with `BotComponent` + `NavAgentComponent`:

```csharp
for (int i = 0; i < botCount; i++)
{
    var entity = frame.CreateEntity(WarriorPrototype.Id);   // or random class
    frame.Add(entity, new BotComponent {
        Difficulty       = (byte)BotDifficulty.Normal,
        DecisionCooldown = 20,
    });
    frame.Add(entity, new NavAgentComponent());
    ref var nav = ref frame.Get<NavAgentComponent>(entity);
    NavAgentComponent.Init(ref nav, spawnPositions[maxPlayers + i]);
}
```

Every PreUpdate, `BotFSMSystem` filters entities holding `BotComponent` and runs `HFSMRoot.Get(Id=1).Tick(ref frame, entity, ref bot.State, ref bot.StateTimer)`.
