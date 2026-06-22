using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.FSM;

namespace Brawler
{
    // ── Config(difficulty) value sources ──────────────────────────────────
    // Each reads the per-entity difficulty from the Frame and indexes the
    // injected difficulty-asset array. Stateless + deterministic (FP64/int only).

    /// <summary>Evade margin for the entity's current difficulty.</summary>
    public sealed class EvadeMarginByDifficulty : AIFunction<FP64>
    {
        readonly BotDifficultyAsset[] _diff;
        public EvadeMarginByDifficulty(BotDifficultyAsset[] diff) => _diff = diff;
        public override FP64 Resolve(ref AIContext context)
            => _diff[context.Frame.GetReadOnly<BotComponent>(context.Entity).Difficulty].EvadeMargin;
    }

    /// <summary>Knockback threshold (percent) that triggers an evade, for the current difficulty.</summary>
    public sealed class EvadeKnockbackPctByDifficulty : AIFunction<int>
    {
        readonly BotDifficultyAsset[] _diff;
        public EvadeKnockbackPctByDifficulty(BotDifficultyAsset[] diff) => _diff = diff;
        public override int Resolve(ref AIContext context)
            => _diff[context.Frame.GetReadOnly<BotComponent>(context.Entity).Difficulty].EvadeKnockbackPct;
    }
}
