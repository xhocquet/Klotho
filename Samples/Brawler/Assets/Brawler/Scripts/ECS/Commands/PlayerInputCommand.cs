using xpTURN.Klotho.Core;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;

namespace Brawler
{
    /// <summary>
    /// Unified per-tick player input — used by both human players (sent through the input buffer /
    /// network) and bots (issued directly to the command system). Replaces the old
    /// MoveInputCommand / AttackCommand / UseSkillCommand trio so a single (tick, playerId) slot
    /// carries movement + jump + attack + skill (matches the input buffer's one-command-per-slot
    /// model and Photon Quantum's single per-tick Input struct).
    ///
    /// Buttons is a bitmask; the skill slot is folded into it (no separate field / sentinel), so a
    /// freshly-constructed or pooled instance (all bits 0) means "no action". One-shot triggers are
    /// cleared on prediction clones via <see cref="ClearOneShotForPrediction"/> so prediction repeats
    /// only the held/continuous movement portion.
    /// </summary>
    [KlothoSerializable(113)]
    public partial class PlayerInputCommand : CommandBase
    {
        public const byte JUMP_PRESSED_BIT = 1 << 0;
        public const byte JUMP_HELD_BIT    = 1 << 1;
        public const byte ATTACK_BIT       = 1 << 2;
        public const byte HAS_MOVE_BIT     = 1 << 3;  // command carries movement intent (run HandleMove)
        public const byte HAS_SKILL_BIT    = 1 << 4;  // command carries a skill trigger (run HandleSkill)
        public const byte SKILL_SLOT_BIT   = 1 << 5;  // 0 = slot 0, set = slot 1

        // Movement is continuous (repeated during prediction). One-shot bits are stripped by
        // ClearOneShotForPrediction so they do not re-fire on predicted ticks.
        public override bool IsContinuousInput => true;

        [KlothoOrder(0)] public FP64      HorizontalAxis;  // -1 ~ 1 (X axis on XZ plane)
        [KlothoOrder(1)] public FP64      VerticalAxis;    // -1 ~ 1 (Z axis on XZ plane)
        [KlothoOrder(2)] public byte      Buttons;         // bitmask; pack/unpack is game logic, not serialization
        [KlothoOrder(3)] public FPVector2 AimDirection;    // attack/skill aim (XZ plane)

        // Read-side accessors over the bitmask (game logic only; not serialized).
        public bool JumpPressed => (Buttons & JUMP_PRESSED_BIT) != 0;
        public bool JumpHeld    => (Buttons & JUMP_HELD_BIT)    != 0;
        public bool Attack      => (Buttons & ATTACK_BIT)       != 0;
        public bool HasMove     => (Buttons & HAS_MOVE_BIT)     != 0;
        public bool HasSkill    => (Buttons & HAS_SKILL_BIT)    != 0;
        public int  SkillSlot   => (Buttons & SKILL_SLOT_BIT)   != 0 ? 1 : 0;

        public override void ClearOneShotForPrediction()
        {
            // Keep JumpHeld / HasMove / axes / SKILL_SLOT_BIT; clear only the one-shot triggers so a
            // repeated predicted command does not re-fire jump/attack/skill every tick.
            Buttons &= unchecked((byte)~(JUMP_PRESSED_BIT | ATTACK_BIT | HAS_SKILL_BIT));
        }
    }
}
