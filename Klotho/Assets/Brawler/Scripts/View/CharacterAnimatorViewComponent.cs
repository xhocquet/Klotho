using UnityEngine;

using xpTURN.Klotho;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;

namespace Brawler
{
    /// <summary>
    /// View component that syncs the Character's Animator parameters.
    /// On each tick's OnUpdateView, it reflects CharacterComponent / PhysicsBodyComponent / KnockbackComponent values into Animator parameters.
    /// The UseSkill trigger is reset on the edge where ActionLockTicks changes from positive to 0,
    /// and VFX SetActive is handled exclusively by CharacterActionVfxViewComponent.
    ///
    /// Attack/Skill triggers use <see cref="EngineEventOneShot"/> — Predicted+Confirmed fire onPlay (hash-deduped
    /// by the engine), Canceled resets the trigger, and the lateGuard skips stale Play when ActionLockTicks has
    /// already dropped to 0 (late-rollback case).
    /// </summary>
    public class CharacterAnimatorViewComponent : EntityViewComponent
    {
        [SerializeField] private Animator _animator;

        private static readonly int SpeedId         = Animator.StringToHash("Speed");
        private static readonly int JumpId          = Animator.StringToHash("Jump");
        private static readonly int VerticalSpeedId = Animator.StringToHash("VerticalSpeed");
        private static readonly int HitId           = Animator.StringToHash("Hit");
        private static readonly int KnockbackId     = Animator.StringToHash("Knockback");
        private static readonly int UseSkillId      = Animator.StringToHash("UseSkill");
        private static readonly int SkillId         = Animator.StringToHash("Skill");
        private static readonly int ClassId         = Animator.StringToHash("Class");

        private int _prevActionLockTicks;

        private EngineEventSubscription _attackSub;
        private EngineEventSubscription _skillSub;

        public override void OnInitialize()
        {
            if (_animator == null)
                _animator = GetComponentInChildren<Animator>();
        }

        public override void OnActivate(FrameRef frame)
        {
            _prevActionLockTicks = 0;

            // Pool reuse handling — subscribe/unsubscribe paired in OnActivate/OnDeactivate.
            _attackSub = EngineEventOneShot.Subscribe<AttackActionEvent>(
                Engine,
                filter:    e => e.Attacker.Index == EntityRef.Index,
                onPlay:    _ => PlayAttackAnimation(),
                onCancel:  _ => CancelActionTrigger(),
                lateGuard: HasActiveAction);

            _skillSub = EngineEventOneShot.Subscribe<SkillActionEvent>(
                Engine,
                filter:    e => e.Caster.Index == EntityRef.Index,
                onPlay:    e => PlaySkillAnimation(e.ClassIndex, e.SkillSlot),
                onCancel:  _ => CancelActionTrigger(),
                lateGuard: HasActiveAction);
        }

        public override void OnDeactivate()
        {
            _attackSub?.Dispose();
            _skillSub?.Dispose();
            _attackSub = null;
            _skillSub  = null;
        }

        // Late-dispatch guard — skip stale trigger when action has already ended in the latest frame.
        // Late rollback (rollback delay > ActionLock duration) can cause DiffRollback to fire Confirmed
        // Play after the action's natural end; without this guard a stale trigger would set on the animator.
        private bool HasActiveAction()
        {
            var frame = Engine.PredictedFrame.Frame;
            if (frame == null || !frame.Has<CharacterComponent>(EntityRef)) return false;
            return frame.GetReadOnly<CharacterComponent>(EntityRef).ActionLockTicks > 0;
        }

        public override void OnUpdateView()
        {
            if (_animator == null) return;

            var frameRef = Engine.PredictedFrame;
            var frame = frameRef.Frame;
            if (frame == null) return;
            if (!frame.Has<CharacterComponent>(EntityRef)) return;

            ref readonly var c = ref frame.GetReadOnly<CharacterComponent>(EntityRef);
            if (c.IsDead)
            {
                // While dead, skip Speed/Jump computation. Death/Respawn animation triggers are handled by separate event handlers.
                _prevActionLockTicks = c.ActionLockTicks;
                return;
            }

            // Jump / VerticalSpeed based on PhysicsBody velocity
            bool jump = false;
            float verticalSpeed = 0f;
            float speed = 0f;
            if (frame.Has<PhysicsBodyComponent>(EntityRef))
            {
                ref readonly var phys = ref frame.GetReadOnly<PhysicsBodyComponent>(EntityRef);
                verticalSpeed = Mathf.Clamp(phys.RigidBody.velocity.y.ToFloat(), -1f, 1f);
                jump = Mathf.Abs(verticalSpeed) > 0.1f;

                // Use only input-based speed (prevents animation playback during passive movement)
                float inputMag = c.InputMagnitude.ToFloat();
                if      (inputMag > 0.5f)  speed = 2f;
                else if (inputMag > 0.01f) speed = 1f;
            }

            bool isKnockback = frame.Has<KnockbackComponent>(EntityRef)
                && frame.GetReadOnly<KnockbackComponent>(EntityRef).BlockInput;

            _animator.SetFloat(SpeedId, speed);
            _animator.SetBool(JumpId, jump);
            _animator.SetFloat(VerticalSpeedId, (verticalSpeed + 1f) * 0.5f);
            _animator.SetBool(KnockbackId, isKnockback);
            _animator.SetBool(HitId, c.HitReactionTicks > 0);
            _animator.SetInteger(ClassId, c.CharacterClass);

            // Reset UseSkill trigger — ActionLockTicks edge (>0 → 0)
            if (_prevActionLockTicks > 0 && c.ActionLockTicks <= 0)
                _animator.ResetTrigger(UseSkillId);
            _prevActionLockTicks = c.ActionLockTicks;
        }

        // ── External triggers (called from Attack/Skill event handlers) ──

        /// <summary>Attack animation trigger (regardless of ranged aim direction, Attack motion).</summary>
        public void PlayAttackAnimation()
        {
            if (_animator == null) return;
            _animator.SetInteger(SkillId, -1);
            _animator.SetTrigger(UseSkillId);
        }

        /// <summary>Skill animation trigger (classIdx + slot).</summary>
        public void PlaySkillAnimation(int classIdx, int slot)
        {
            if (_animator == null) return;
            _animator.SetInteger(ClassId, classIdx);
            _animator.SetInteger(SkillId, slot);
            _animator.SetTrigger(UseSkillId);
        }

        /// <summary>Called when a predicted action is canceled by Rollback, etc.</summary>
        public void CancelActionTrigger()
        {
            if (_animator == null) return;
            _animator.ResetTrigger(UseSkillId);
        }
    }
}
