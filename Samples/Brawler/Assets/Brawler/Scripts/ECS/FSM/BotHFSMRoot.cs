using xpTURN.Klotho.ECS.FSM;

namespace Brawler
{
    /// <summary>
    /// Bot AI state-graph definition.
    /// Built as a flat FSM (5 states).
    /// Registered by calling Build() once at app start (or when BotFSMSystem is created).
    /// </summary>
    public static class BotHFSMRoot
    {
        public const int Id = 1; // HFSMRoot registry key

        // ── State IDs ─────────────────────────────────────────────────────────
        public const int Idle   = BotStateId.Idle;   // 0
        public const int Chase  = BotStateId.Chase;  // 1
        public const int Attack = BotStateId.Attack; // 2
        public const int Evade  = BotStateId.Evade;  // 3
        public const int Skill  = BotStateId.Skill;  // 4

        // ── Singleton Decision/Action (initialized when Build() is called) ────
        static ShouldEvadeDecision    _shouldEvade;
        static IsKnockbackDecision    _isKnockback    = new IsKnockbackDecision();
        static InAttackRangeDecision  _inAttackRange;
        static ShouldUseSkillDecision _shouldUseSkill;
        static HasTargetDecision      _hasTarget      = new HasTargetDecision();
        static NoTargetDecision       _noTarget       = new NoTargetDecision();
        static EvadeArrivedDecision   _evadeArrived   = new EvadeArrivedDecision();
        static SkillActionDoneDecision _skillDone     = new SkillActionDoneDecision();

        static ClearDestinationAction _clearDest  = new ClearDestinationAction();
        static EvadeEnterAction       _evadeEnter;
        static SkillUpdateAction      _skillUpdate;

        // ── Build ─────────────────────────────────────────────────────────────
        // Priority pool: Evade 90, Knockback 80 (→Chase), Attack 70, Skill 60, Chase 50 (→Chase), Idle 40 (→Idle).
        // Evade/Skill are committed states holding only their single exit transition.

        public static void Build(BotBehaviorAsset behavior, BotDifficultyAsset[] diffAssets,
                                 BasicAttackConfigAsset attack, SkillConfigAsset[][] skills)
        {
            if (HFSMRoot.Has(Id)) return;

            _shouldEvade    = new ShouldEvadeDecision(behavior, diffAssets);
            _inAttackRange  = new InAttackRangeDecision(attack);
            _shouldUseSkill = new ShouldUseSkillDecision(behavior, diffAssets, skills);
            _evadeEnter     = new EvadeEnterAction(behavior);
            _skillUpdate    = new SkillUpdateAction(behavior, diffAssets, skills);

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

    /// <summary>State ID constants.</summary>
    public static class BotStateId
    {
        public const int Idle   = 0;
        public const int Chase  = 1;
        public const int Attack = 2;
        public const int Evade  = 3;
        public const int Skill  = 4;
    }
}
