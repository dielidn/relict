using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Relict
{
    public enum HeroKind { Frog, Gecko }

    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(Health))]
    public class HeroController : MonoBehaviour, IKnockback
    {
        public HeroKind kind;
        public float maxHp = 150f;
        public float walkSpeed = 3.3f, runSpeed = 6.4f, jumpHeight = 1.4f, gravity = -24f;
        public float damage = 24f, attackInterval = 0.55f, abilityCooldown = 7f;

        CharacterController cc;
        Animator anim;
        Health hp;
        Transform cam;
        ThirdPersonCamera tpc;
        Vector3 vel, knock;
        float yVel, airTime, attackReady, abilityReady, lockUntil, armorUntil, rollUntil, rollReady, leapStart;
        bool leaping, leftGround, flip;
        string cur;
        Light armorLight;

        // Simulated input for automated play-tests (added on top of real input).
        public static Vector2 simMove;
        public static bool simSprint, simJump, simAttack, simAbility, simRoll;

        public Health Hp => hp;
        public float AbilityLeft => Mathf.Max(0f, abilityReady - Time.time);
        public float AbilityTotal { get; private set; } = 1f;
        public bool ArmorOn => Time.time < armorUntil;
        public float RollLeft => Mathf.Max(0f, rollReady - Time.time);
        public string AbilityName => kind == HeroKind.Frog ? "Прыжок" : "Корковая броня";

        void Awake()
        {
            cc = GetComponent<CharacterController>();
            hp = GetComponent<Health>();
            anim = GetComponentInChildren<Animator>();
            hp.Init(Team.Player, maxHp, kind == HeroKind.Frog ? "Лягушка" : "Геккон");
        }

        void Start()
        {
            cam = Camera.main.transform;
            tpc = cam.GetComponent<ThirdPersonCamera>();
            if (kind == HeroKind.Gecko)
            {
                var l = new GameObject("ArmorGlow").AddComponent<Light>();
                l.type = LightType.Point; l.color = new Color(1f, 0.55f, 0.15f); l.intensity = 4f; l.range = 3.5f;
                l.transform.SetParent(transform, false); l.transform.localPosition = Vector3.up * 1.1f;
                l.enabled = false; armorLight = l;
            }
            hp.Damaged += (a, amt) => { if (tpc) tpc.Shake(Mathf.Clamp(amt / 40f, 0.1f, 0.5f)); };
        }

        public void Knock(Vector3 v) { if (Time.time >= rollUntil) knock += v; }

        void Play(string s, float fade = 0.12f)
        {
            if (anim == null || cur == s) return;
            cur = s;
            anim.CrossFadeInFixedTime(s, fade);
        }

        void Restart(string s, float fade = 0.05f)
        {
            if (anim == null) return;
            cur = s;
            anim.CrossFadeInFixedTime(s, fade, 0, 0f);
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            var mm = MatchManager.I;
            bool active = !hp.Dead && mm != null && mm.Playing;
            var kb = Keyboard.current;
            var ms = Mouse.current;

            Vector3 fwd = cam.forward; fwd.y = 0; fwd.Normalize();
            Vector3 right = cam.right; right.y = 0; right.Normalize();

            Vector3 wish = Vector3.zero;
            bool sprint = false;
            if (active && kb != null)
            {
                if (kb.wKey.isPressed) wish += fwd;
                if (kb.sKey.isPressed) wish -= fwd;
                if (kb.dKey.isPressed) wish += right;
                if (kb.aKey.isPressed) wish -= right;
                sprint = kb.leftShiftKey.isPressed;
            }
            if (active && simMove.sqrMagnitude > 0f) { wish += fwd * simMove.y + right * simMove.x; sprint |= simSprint; }
            if (wish.sqrMagnitude > 1f) wish.Normalize();

            bool aiming = active && kind == HeroKind.Gecko && ms != null && ms.rightButton.isPressed;
            if (tpc) tpc.aiming = aiming;
            float speedMul = mm ? mm.SpeedMul : 1f;
            float spd = (sprint && !aiming ? runSpeed : walkSpeed) * speedMul;
            if (Time.time < lockUntil) spd *= 0.4f;

            bool grounded = cc.isGrounded;
            bool rolling = Time.time < rollUntil;
            if (rolling)
            {
                // keep roll velocity
            }
            else if (!leaping)
            {
                float accel = grounded ? 40f : 10f;
                Vector3 hv = new Vector3(vel.x, 0, vel.z);
                hv = Vector3.MoveTowards(hv, wish * spd, accel * dt);
                vel = hv;
            }

            if (grounded && yVel < 0f) yVel = -2f;
            bool jumpPressed = (kb != null && kb.spaceKey.wasPressedThisFrame) || simJump;
            simJump = false;
            if (active && grounded && jumpPressed && !leaping && !rolling)
            {
                yVel = Mathf.Sqrt(2f * jumpHeight * -gravity);
                Restart("Jump", 0.08f);
            }
            yVel += gravity * dt;
            knock = Vector3.MoveTowards(knock, Vector3.zero, 18f * dt);
            cc.Move((vel + knock + Vector3.up * yVel) * dt);
            airTime = cc.isGrounded ? 0f : airTime + dt;

            if (leaping)
            {
                if (!cc.isGrounded) leftGround = true;
                else if (leftGround || Time.time - leapStart > 1.6f) LandLeap();
            }

            // facing
            Vector3 face = (aiming || Time.time < lockUntil) ? fwd : new Vector3(vel.x, 0, vel.z);
            if (face.sqrMagnitude > 0.04f)
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(face), 1f - Mathf.Exp(-14f * dt));

            if (active)
            {
                bool atk = (ms != null && ms.leftButton.isPressed) || simAttack;
                bool abl = (kb != null && kb.qKey.wasPressedThisFrame) || simAbility;
                bool rol = (kb != null && kb.leftCtrlKey.wasPressedThisFrame) || simRoll;
                simAbility = false; simRoll = false;
                if (atk && Time.time >= attackReady && !leaping && !rolling) Attack(fwd);
                if (abl && Time.time >= abilityReady && !rolling) Ability(fwd);
                if (kind == HeroKind.Frog && rol && Time.time >= rollReady && grounded && !leaping) Roll(wish.sqrMagnitude > 0.01f ? wish : fwd);
            }

            hp.armor = ArmorOn ? 0.7f : (rolling ? 1f : 0f);
            if (armorLight) armorLight.enabled = ArmorOn;

            // animation
            if (anim == null) return;
            if (hp.Dead) { anim.speed = 0.5f; Play("Idle", 0.3f); return; }
            float hs = new Vector3(vel.x, 0, vel.z).magnitude;
            if (leaping || rolling || Time.time < lockUntil) { }
            else if (airTime > 0.2f) { anim.speed = 1f; Play("Jump", 0.1f); }
            else if (hs > 0.4f)
            {
                bool run = hs > walkSpeed * speedMul + 0.4f;
                Play(run ? "Run" : "Walk", 0.15f);
                anim.speed = Mathf.Clamp(hs / ((run ? runSpeed : walkSpeed) * speedMul), 0.75f, 1.35f);
            }
            else { anim.speed = 1f; Play("Idle", 0.2f); }
        }

        void Attack(Vector3 fwd)
        {
            var mm = MatchManager.I;
            attackReady = Time.time + attackInterval / mm.AttackSpeedMul;
            float dmg = damage * mm.DamageMul;
            transform.rotation = Quaternion.LookRotation(fwd);
            if (kind == HeroKind.Frog)
            {
                lockUntil = Time.time + 0.3f;
                Restart("Attack");
                anim.speed = 1.9f;
                vel += fwd * 2.5f;
                StartCoroutine(MeleeHit(fwd, dmg));
            }
            else
            {
                lockUntil = Time.time + 0.22f;
                Restart("Attack");
                anim.speed = 2.5f;
                Vector3 origin = transform.position + Vector3.up * 1.35f + fwd * 0.45f + transform.right * 0.15f;
                Vector3 aim = AimPoint();
                Projectile.Fire(origin, (aim - origin).normalized * 55f, dmg, hp, new Color(1f, 0.8f, 0.45f));
            }
        }

        IEnumerator MeleeHit(Vector3 fwd, float dmg)
        {
            yield return new WaitForSeconds(0.08f);
            flip = !flip;
            VFX.Slash(transform.position, fwd, new Color(0.55f, 1f, 0.75f), 1.5f, flip);
            int n = Combat.Melee(transform.position, fwd, 2.3f, 160f, dmg, hp, 5f);
            if (n > 0 && tpc) tpc.Shake(0.12f);
        }

        Vector3 AimPoint()
        {
            var ray = new Ray(cam.position, cam.forward);
            var hits = Physics.RaycastAll(ray, 150f, ~0, QueryTriggerInteraction.Ignore);
            float best = 150f;
            Vector3 p = ray.GetPoint(150f);
            foreach (var h in hits)
            {
                if (h.collider.transform.IsChildOf(transform)) continue;
                if (h.distance < best && h.distance > 1f) { best = h.distance; p = h.point; }
            }
            return p;
        }

        void Ability(Vector3 fwd)
        {
            var mm = MatchManager.I;
            AbilityTotal = abilityCooldown * mm.CooldownMul;
            abilityReady = Time.time + AbilityTotal;
            if (kind == HeroKind.Frog)
            {
                leaping = true; leftGround = false; leapStart = Time.time;
                yVel = 9.5f;
                vel = fwd * 11f;
                transform.rotation = Quaternion.LookRotation(fwd);
                Restart("Ability");
                anim.speed = 1.3f;
            }
            else
            {
                armorUntil = Time.time + 5f * mm.AbilityPowerMul;
                VFX.Burst(transform.position + Vector3.up, new Color(1f, 0.6f, 0.2f), 40, 3.5f, 0.2f);
                mm.Toast("Корковая броня: −70% урона на " + (5f * mm.AbilityPowerMul).ToString("0.#") + " с");
            }
        }

        void LandLeap()
        {
            leaping = false;
            vel *= 0.15f;
            var mm = MatchManager.I;
            float dmg = damage * 2.2f * mm.DamageMul * mm.AbilityPowerMul;
            Combat.Melee(transform.position, transform.forward, 3.8f, 360f, dmg, hp, 9f);
            VFX.Ring(transform.position + Vector3.up * 0.15f, new Color(0.5f, 1f, 0.6f));
            if (tpc) tpc.Shake(0.5f);
        }

        void Roll(Vector3 dir)
        {
            rollReady = Time.time + 1.1f;
            rollUntil = Time.time + 0.45f;
            dir.y = 0; dir.Normalize();
            vel = dir * 10f;
            transform.rotation = Quaternion.LookRotation(dir);
            Restart("Roll");
            anim.speed = 1.6f;
        }
    }
}
