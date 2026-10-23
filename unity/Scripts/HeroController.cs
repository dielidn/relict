using System.Collections;
using System.Collections.Generic;
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

        [Header("Mobility")]
        public int dodgeCharges = 2;
        public float dodgeRecharge = 1.5f;
        public float critMultiplier = 1.8f;

        CharacterController cc;
        Animator anim;
        Health hp;
        Transform cam;
        ThirdPersonCamera tpc;
        Vector3 vel, knock, dashDir;
        float yVel, airTime, attackReady, abilityReady, lockUntil, armorUntil, rollUntil, leapStart;
        float coyote, jumpBuffer, dodgeMeter, perfectUntil, lastSwing = -9f, chargeStart;
        int airJumps, combo;
        float plungeStart;
        bool leaping, leftGround, flip, charging, fullPlayed, sliding, plunging;
        string cur;
        Light armorLight;

        // animation feel: sliced jump states, bow upper-body layer, procedural squash / lean / flip
        Transform model;
        Vector3 modelBasePos, modelBaseScale = Vector3.one;
        Quaternion modelBaseRot = Quaternion.identity;
        float squash, squashVel, lean, flipT = -1f, jumpT = -9f, landUntil, releaseUntil, upperW;
        bool hasJumpStates, hasUpper, wasGrounded = true;
        string upperState = "";
        const float DrawClipLen = 1.55f;
        static readonly int UpperSpeedId = Animator.StringToHash("UpperSpeed");
        readonly HashSet<Health> slideHit = new HashSet<Health>();

        // Simulated input for automated play-tests (added on top of real input).
        public static Vector2 simMove;
        public static bool simSprint, simJump, simAttack, simAbility, simRoll;

        public Health Hp => hp;
        public float AbilityLeft => Mathf.Max(0f, abilityReady - Time.time);
        public float AbilityTotal { get; private set; } = 1f;
        public bool ArmorOn => Time.time < armorUntil;
        public float DodgeMeter => dodgeMeter;                  // 0..dodgeCharges
        public bool PerfectReady => Time.time < perfectUntil;
        public int Combo => Time.time - lastSwing < 0.9f ? combo : -1;
        public bool Charging => charging;
        public float Charge01 => charging ? Mathf.Clamp01((Time.time - chargeStart) / ChargeTime) : 0f;
        float ChargeTime => 0.75f / (MatchManager.I ? MatchManager.I.AttackSpeedMul : 1f);
        public string AbilityName => kind == HeroKind.Frog ? "Прыжок" : "Корковая броня";

        void Awake()
        {
            cc = GetComponent<CharacterController>();
            hp = GetComponent<Health>();
            anim = GetComponentInChildren<Animator>();
            hp.Init(Team.Player, maxHp, kind == HeroKind.Frog ? "Лягушка" : "Геккон");
            dodgeMeter = dodgeCharges;
            if (anim != null)
            {
                model = anim.transform;
                modelBasePos = model.localPosition; modelBaseRot = model.localRotation; modelBaseScale = model.localScale;
                hasJumpStates = anim.HasState(0, Animator.StringToHash("JumpUp"));
                hasUpper = anim.layerCount > 1 && anim.HasState(1, Animator.StringToHash("Draw"));
            }
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
            hp.Damaged += (a, amt) =>
            {
                if (amt <= 0.01f && Time.time < rollUntil && a != null) PerfectDodge();
                else if (tpc) tpc.Shake(Mathf.Clamp(amt / 40f, 0.1f, 0.5f));
            };
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

        // ---------- crits ----------
        bool RollCrit(float extraChance = 0f)
        {
            if (Time.time < perfectUntil) { perfectUntil = 0f; return true; }
            float chance = (MatchManager.I ? MatchManager.I.CritChance : 0.15f) + extraChance;
            return Random.value < chance;
        }

        void PerfectDodge()
        {
            if (Time.time < perfectUntil) return;
            perfectUntil = Time.time + 2.5f;
            SFX.Play(Sfx.BowFull, 0.9f, 0f);
            VFX.Burst(transform.position + Vector3.up, new Color(0.7f, 1f, 1f), 30, 4f, 0.15f, 0.5f, 0f);
            MatchManager.I.SlowMo(0.35f, 0.3f);
            MatchManager.I.Banner("ИДЕАЛЬНЫЙ УВОРОТ", "следующая атака — крит");
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
            float spd = (sprint && !aiming && !charging ? runSpeed : walkSpeed) * speedMul;
            if (charging) spd *= 0.6f;
            if (Time.time < lockUntil) spd *= 0.4f;

            bool grounded = cc.isGrounded;
            bool rolling = Time.time < rollUntil;
            if (sliding && !rolling) sliding = false;

            // dodge charges refill
            if (dodgeMeter < dodgeCharges) dodgeMeter = Mathf.Min(dodgeCharges, dodgeMeter + dt / dodgeRecharge);

            if (rolling)
            {
                float keep = sliding ? 7f : 4f;
                Vector3 hv = Vector3.MoveTowards(new Vector3(vel.x, 0, vel.z), dashDir * keep, (sliding ? 9f : 14f) * dt);
                vel = hv;
                if (sliding) SlideHits();
            }
            else if (!leaping && !plunging)
            {
                float accel = grounded ? 40f : 18f;
                Vector3 hv = new Vector3(vel.x, 0, vel.z);
                hv = Vector3.MoveTowards(hv, wish * spd, accel * dt);
                vel = hv;
            }

            // jumping: coyote time, input buffer, double jump
            coyote = grounded ? 0.12f : coyote - dt;
            if (grounded) airJumps = 1;
            bool jumpPressed = (kb != null && kb.spaceKey.wasPressedThisFrame) || simJump;
            simJump = false;
            if (jumpPressed) jumpBuffer = 0.15f; else jumpBuffer -= dt;
            if (grounded && yVel < 0f) yVel = -2f;
            if (active && jumpBuffer > 0f && !leaping && !plunging && !sliding)
            {
                if (coyote > 0f)
                {
                    yVel = Mathf.Sqrt(2f * jumpHeight * -gravity);
                    coyote = 0f; jumpBuffer = 0f;
                    JumpAnim(false);
                }
                else if (airJumps > 0 && !grounded)
                {
                    airJumps--;
                    yVel = Mathf.Sqrt(2f * jumpHeight * 0.85f * -gravity);
                    jumpBuffer = 0f;
                    if (wish.sqrMagnitude > 0.01f) vel = wish * Mathf.Max(spd, new Vector3(vel.x, 0, vel.z).magnitude);
                    JumpAnim(true);
                    VFX.Burst(transform.position + Vector3.up * 0.2f, new Color(0.8f, 1f, 0.8f), 14, 2.5f, 0.12f, 0.35f, 0f, true);
                    SFX.Play(Sfx.Swoosh, 0.5f, 0.2f);
                }
            }

            // snappier arc: fall faster than you rise, release Space early for a short hop
            bool jumpHeld = (kb != null && kb.spaceKey.isPressed) || Time.time - jumpT < 0.12f;
            float g = gravity;
            if (plunging) g *= 2.2f;
            else if (yVel < 0f) g *= 1.45f;
            else if (!jumpHeld && !leaping) g *= 1.9f;
            yVel += g * dt;
            knock = Vector3.MoveTowards(knock, Vector3.zero, 18f * dt);
            float fallSpeed = -yVel;
            cc.Move((vel + knock + Vector3.up * yVel) * dt);
            if (cc.isGrounded && !wasGrounded && airTime > 0.22f) Land(fallSpeed, rolling);
            wasGrounded = cc.isGrounded;
            airTime = cc.isGrounded ? 0f : airTime + dt;

            if (leaping)
            {
                if (!cc.isGrounded) leftGround = true;
                else if (leftGround || Time.time - leapStart > 1.6f) LandLeap();
            }
            if (plunging && (cc.isGrounded || Time.time - plungeStart > 2f)) LandPlunge();

            // facing
            Vector3 face = (aiming || charging || Time.time < lockUntil) ? fwd : rolling ? dashDir : new Vector3(vel.x, 0, vel.z);
            if (face.sqrMagnitude > 0.04f)
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(face), 1f - Mathf.Exp(-14f * dt));

            if (active)
            {
                bool atk = ((ms != null && ms.leftButton.isPressed) || simAttack) && !mm.UIOpen;
                bool atkDown = ((ms != null && ms.leftButton.wasPressedThisFrame) || simAttack) && !mm.UIOpen;
                bool abl = (kb != null && kb.qKey.wasPressedThisFrame) || simAbility;
                bool rol = (kb != null && (kb.leftCtrlKey.wasPressedThisFrame || kb.cKey.wasPressedThisFrame)) || simRoll;
                simAbility = false; simRoll = false;

                if (rol && dodgeMeter >= 1f && !leaping && !plunging)
                {
                    bool slide = grounded && sprint && new Vector3(vel.x, 0, vel.z).magnitude > walkSpeed * speedMul + 0.5f;
                    Vector3 dir = wish.sqrMagnitude > 0.01f ? wish : (kind == HeroKind.Gecko && !slide ? -fwd : fwd);
                    Dodge(dir, slide);
                }

                if (kind == HeroKind.Frog)
                {
                    if (atkDown && !grounded && airTime > 0.12f && !leaping && !plunging && !rolling) StartPlunge();
                    else if (atk && Time.time >= attackReady && !leaping && !rolling && !plunging) Attack(fwd);
                }
                else
                {
                    if (!charging && atk && Time.time >= attackReady && !leaping && !rolling) StartCharge();
                    if (charging)
                    {
                        if (!fullPlayed && Charge01 >= 1f) { fullPlayed = true; SFX.Play(Sfx.BowFull, 0.7f, 0f); }
                        if (!atk) ReleaseArrow(fwd, aiming);
                    }
                }
                if (abl && Time.time >= abilityReady && !rolling && !plunging) Ability(fwd);
            }

            if ((!active || rolling) && charging) charging = false;
            UpdateUpperLayer(dt);
            if (tpc) tpc.charge = Charge01;
            hp.armor = rolling ? 1f : ArmorOn ? 0.7f : 0f;
            if (armorLight) armorLight.enabled = ArmorOn;

            // animation
            if (anim == null) return;
            if (hp.Dead) { anim.speed = 0.5f; Play("Idle", 0.3f); return; }
            float hs = new Vector3(vel.x, 0, vel.z).magnitude;
            bool airborne = !cc.isGrounded && (airTime > 0.15f || Time.time - jumpT < 0.3f);
            if (leaping || rolling || plunging || (charging && !hasUpper) || (Time.time < lockUntil && !(kind == HeroKind.Gecko && hasUpper)) || Time.time < landUntil) { }
            else if (airborne)
            {
                if (!hasJumpStates) { anim.speed = 1f; Play("Jump", 0.1f); }
                else if (cur == "JumpUp")
                {
                    var st = anim.IsInTransition(0) ? anim.GetNextAnimatorStateInfo(0) : anim.GetCurrentAnimatorStateInfo(0);
                    if (st.normalizedTime >= 0.95f) { anim.speed = 1f; Play("JumpAir", 0.15f); }
                }
                else { anim.speed = 1f; Play("JumpAir", 0.2f); }
            }
            else if (hs > 0.4f)
            {
                bool run = hs > walkSpeed * speedMul + 0.4f;
                Play(run ? "Run" : "Walk", 0.15f);
                anim.speed = Mathf.Clamp(hs / ((run ? runSpeed : walkSpeed) * speedMul), 0.75f, 1.35f);
            }
            else { anim.speed = 1f; Play("Idle", 0.2f); }
        }

        // ---------- jump feel ----------
        void JumpAnim(bool doubleJump)
        {
            jumpT = Time.time;
            landUntil = 0f;
            squash = doubleJump ? 0.1f : 0.18f; squashVel = 0f;     // stretch on take-off
            if (doubleJump) flipT = 0f;                              // front flip on the second jump
            if (anim == null) return;
            if (hasJumpStates) { Restart("JumpUp", 0.06f); anim.speed = doubleJump ? 1.5f : 1.2f; }
            else Restart("Jump", 0.08f);
        }

        void Land(float impact, bool rolling)
        {
            impact = Mathf.Max(0f, impact);
            squash = -Mathf.Clamp(impact / 14f * 0.2f, 0.06f, 0.24f); squashVel = 0f;
            flipT = -1f;
            float hs = new Vector3(vel.x, 0, vel.z).magnitude;
            if (anim != null && hasJumpStates && !leaping && !plunging && !rolling && !hp.Dead)
            {
                bool moving = hs > 1.5f;
                Restart("JumpLand", 0.06f);
                anim.speed = moving ? 1.8f : 1.2f;
                landUntil = Time.time + (moving ? 0.12f : 0.3f);
            }
            if (impact > 8f)
            {
                VFX.Burst(transform.position + Vector3.up * 0.1f, new Color(0.7f, 0.65f, 0.5f), impact > 13f ? 14 : 8, 2.4f, 0.16f, 0.45f, 0.2f, true);
                SFX.Play(Sfx.Leap, Mathf.Clamp01(impact / 30f), 0.15f);
                if (tpc) tpc.Shake(Mathf.Clamp(impact / 80f, 0.05f, 0.25f));
            }
        }

        void LateUpdate()
        {
            if (model == null) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            // squash & stretch spring
            squashVel += (-squash * 200f - squashVel * 15f) * dt;
            squash += squashVel * dt;
            float s = Mathf.Clamp(squash, -0.3f, 0.3f);
            model.localScale = Vector3.Scale(modelBaseScale, new Vector3(1f - s * 0.5f, 1f + s, 1f - s * 0.5f));

            // lean: forward with speed in the air, back while rising, forward while falling
            float target = 0f;
            if (!cc.isGrounded && !hp.Dead && Time.time >= rollUntil)
            {
                float fs = Vector3.Dot(new Vector3(vel.x, 0, vel.z), transform.forward);
                target = Mathf.Clamp(fs * 1.4f, -6f, 9f) - Mathf.Clamp(yVel * 0.9f, -7f, 7f);
            }
            lean = Mathf.Lerp(lean, target, 1f - Mathf.Exp(-8f * dt));
            Quaternion rot = Quaternion.Euler(lean, 0f, 0f);
            if (flipT >= 0f)
            {
                flipT += dt / 0.42f;
                if (flipT >= 1f) flipT = -1f;
                else { float e = flipT * flipT * (3f - 2f * flipT); rot = rot * Quaternion.Euler(360f * e, 0f, 0f); }
            }
            Vector3 pivot = new Vector3(0f, 0.75f, 0f);
            model.localRotation = modelBaseRot * rot;
            model.localPosition = modelBasePos + pivot - rot * pivot;
        }

        // ---------- bow upper-body layer ----------
        void UpdateUpperLayer(float dt)
        {
            if (!hasUpper || anim == null) return;
            if (charging && upperState == "Draw" && Charge01 >= 1f)
            {
                anim.CrossFadeInFixedTime("Hold", 0.12f, 1);
                upperState = "Hold";
            }
            float want = charging || Time.time < releaseUntil ? 1f : 0f;
            upperW = Mathf.MoveTowards(upperW, want, dt * (want > upperW ? 12f : 4f));
            anim.SetLayerWeight(1, upperW);
            float spd = upperState == "Draw" ? DrawClipLen / ChargeTime : upperState == "Release" ? 2.4f : 1f;
            anim.SetFloat(UpperSpeedId, spd / Mathf.Max(0.1f, anim.speed));
        }

        // ---------- dodge & slide ----------
        void Dodge(Vector3 dir, bool slide)
        {
            dodgeMeter -= 1f;
            dir.y = 0; dir.Normalize();
            dashDir = dir;
            sliding = slide;
            charging = false;
            rollUntil = Time.time + (slide ? 0.6f : 0.38f);
            vel = dir * (slide ? 14f : 11.5f);
            slideHit.Clear();
            if (kind == HeroKind.Frog) { Restart("Roll"); anim.speed = slide ? 1.2f : 1.7f; }
            else { Restart(slide ? "Run" : "Jump", 0.04f); anim.speed = slide ? 1.8f : 2.2f; }
            SFX.Play(Sfx.Swoosh, 0.7f, 0.2f);
            VFX.Burst(transform.position + Vector3.up * 0.15f, new Color(0.75f, 0.7f, 0.55f), slide ? 18 : 10, 2.5f, 0.18f, 0.5f, 0.3f);
        }

        void SlideHits()
        {
            var mm = MatchManager.I;
            float dmg = damage * 0.6f * mm.DamageMul;
            var cols = Physics.OverlapSphere(transform.position + Vector3.up * 0.6f + dashDir * 0.6f, 1.3f, ~0, QueryTriggerInteraction.Ignore);
            foreach (var c in cols)
            {
                var h = c.GetComponentInParent<Health>();
                if (h == null || h == hp || h.Dead || h.team == Team.Player || slideHit.Contains(h)) continue;
                slideHit.Add(h);
                bool crit = RollCrit();
                h.Damage(dmg * (crit ? critMultiplier : 1f), hp, crit);
                var kbk = h.GetComponent<IKnockback>();
                if (kbk != null) kbk.Knock((dashDir + Vector3.up * 0.2f) * 8f);
                SFX.Play(Sfx.MeleeHit, 0.8f, 0.1f);
                MatchManager.I.HitMarker(crit, h.Dead);
                if (tpc) tpc.Shake(0.12f);
            }
        }

        // ---------- melee (frog) ----------
        void Attack(Vector3 fwd)
        {
            var mm = MatchManager.I;
            combo = Time.time - lastSwing < 0.9f ? (combo + 1) % 3 : 0;
            lastSwing = Time.time;
            bool finisher = combo == 2;
            attackReady = Time.time + attackInterval / mm.AttackSpeedMul * (finisher ? 1.35f : 1f);
            float dmg = damage * mm.DamageMul * (combo == 0 ? 1f : combo == 1 ? 1.15f : 1.6f);
            transform.rotation = Quaternion.LookRotation(fwd);
            lockUntil = Time.time + (finisher ? 0.42f : 0.3f);
            Restart(finisher ? "Ability" : "Attack");
            anim.speed = finisher ? 2.2f : 1.9f;
            vel += fwd * (finisher ? 5f : 2.5f);
            SFX.Play(Sfx.Swoosh, finisher ? 0.9f : 0.6f, 0.15f);
            StartCoroutine(MeleeHit(fwd, dmg, finisher));
        }

        IEnumerator MeleeHit(Vector3 fwd, float dmg, bool finisher)
        {
            yield return new WaitForSeconds(finisher ? 0.14f : 0.08f);
            flip = !flip;
            bool crit = finisher || RollCrit();
            Color c = crit ? new Color(1f, 0.8f, 0.3f) : new Color(0.55f, 1f, 0.75f);
            VFX.Slash(transform.position, fwd, c, finisher ? 2.0f : 1.5f, flip);
            if (finisher) VFX.Slash(transform.position, fwd, c, 1.6f, !flip);
            int n = Combat.Melee(transform.position, fwd, finisher ? 2.9f : 2.3f, finisher ? 230f : 160f, dmg * (crit ? critMultiplier : 1f), hp, finisher ? 10f : 5f, crit);
            if (n > 0)
            {
                if (tpc) tpc.Shake(finisher ? 0.3f : 0.14f);
                SFX.Play(crit ? Sfx.ArrowCrit : Sfx.MeleeHit, 0.9f);
                MatchManager.I.HitStop(finisher ? 0.09f : crit ? 0.06f : 0.04f);
                MatchManager.I.HitMarker(crit, false);
            }
        }

        void StartPlunge()
        {
            plunging = true;
            plungeStart = Time.time;
            yVel = -6f;
            vel *= 0.4f;
            Restart("Ability", 0.05f);
            anim.speed = 1.6f;
            SFX.Play(Sfx.Swoosh, 0.9f, 0.1f);
        }

        void LandPlunge()
        {
            plunging = false;
            var mm = MatchManager.I;
            bool crit = RollCrit(0.25f);
            float dmg = damage * 1.5f * mm.DamageMul * (crit ? critMultiplier : 1f);
            int n = Combat.Melee(transform.position, transform.forward, 3.2f, 360f, dmg, hp, 8f, crit);
            SFX.Play(Sfx.Leap, 0.9f);
            VFX.Ring(transform.position + Vector3.up * 0.15f, crit ? new Color(1f, 0.8f, 0.3f) : new Color(0.6f, 1f, 0.7f));
            if (tpc) tpc.Shake(0.35f);
            lockUntil = Time.time + 0.25f;
            if (n > 0) { mm.HitStop(0.06f); mm.HitMarker(crit, false); }
        }

        // ---------- bow (gecko) ----------
        Vector3? AssistTarget()
        {
            Health best = null;
            float bestScore = float.MaxValue;
            Vector3 hf = transform.forward;
            foreach (var h in Health.All)
            {
                if (h == null || h.Dead || h.team == Team.Player) continue;
                var tcc = h.GetComponent<CharacterController>();
                Vector3 c = h.transform.position + (tcc ? tcc.center : Vector3.up);
                Vector3 fromCam = c - cam.position;
                float dist = fromCam.magnitude;
                if (dist > 45f) continue;
                float ang = Vector3.Angle(cam.forward, fromCam);
                Vector3 fromHero = c - transform.position; fromHero.y = 0;
                float score = float.MaxValue;
                if (ang < 7f) score = ang;
                else if (fromHero.magnitude < 4f && Vector3.Angle(hf, fromHero) < 50f) score = 6f + fromHero.magnitude;
                if (score < bestScore) { bestScore = score; best = h; }
            }
            if (best == null) return null;
            var bcc = best.GetComponent<CharacterController>();
            return best.transform.position + (bcc ? bcc.center : Vector3.up);
        }

        void StartCharge()
        {
            charging = true;
            fullPlayed = false;
            chargeStart = Time.time;
            if (hasUpper) { anim.CrossFadeInFixedTime("Draw", 0.1f, 1, 0f); upperState = "Draw"; }
            else { Restart("Attack", 0.08f); anim.speed = 0.3f; }
            SFX.Play(Sfx.BowDraw, 0.5f, 0.05f);
        }

        void ReleaseArrow(Vector3 fwd, bool aimed)
        {
            var mm = MatchManager.I;
            float k = Charge01;
            charging = false;
            bool heavy = k >= 0.999f;
            bool crit = RollCrit(heavy ? 0.2f : 0f);
            float mul = Mathf.Lerp(0.75f, 1.6f, k) * (crit ? critMultiplier : 1f);
            float speed = Mathf.Lerp(46f, 80f, k);
            float spread = (aimed ? 0.4f : 1.8f) * (1f - k);
            Vector3 origin = transform.position + Vector3.up * 1.35f + fwd * 0.45f + transform.right * 0.15f;
            Vector3 aim = AimPoint(!aimed);
            Vector3 dir = (aim - origin).normalized;
            dir = Quaternion.Euler(Random.Range(-spread, spread), Random.Range(-spread, spread), 0) * dir;
            Projectile.Fire(origin, dir * speed, damage * mm.DamageMul * mul, hp, crit ? new Color(1f, 0.6f, 0.2f) : new Color(1f, 0.8f, 0.45f), heavy ? 1 : 0, heavy, crit);
            attackReady = Time.time + 0.18f / mm.AttackSpeedMul;
            lockUntil = Time.time + 0.18f;
            transform.rotation = Quaternion.LookRotation(fwd);
            if (hasUpper) { anim.CrossFadeInFixedTime("Release", 0.03f, 1, 0f); upperState = "Release"; releaseUntil = Time.time + 0.38f; }
            else anim.speed = 2.5f;
            SFX.Play(Sfx.BowRelease, heavy ? 1f : 0.75f, 0.05f);
            if (tpc) { tpc.Kick(heavy ? 2.4f : 1.1f); tpc.Shake(heavy ? 0.12f : 0.04f); }
            VFX.Burst(origin + dir * 0.4f, crit ? new Color(1f, 0.7f, 0.3f) : new Color(1f, 0.9f, 0.6f), heavy || crit ? 10 : 4, 2f, 0.06f, 0.2f, 0f);
        }

        Vector3 AimPoint(bool useAssist = true)
        {
            var assist = useAssist ? AssistTarget() : null;
            if (assist.HasValue) return assist.Value;
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

        // ---------- abilities ----------
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
            bool crit = RollCrit(0.2f);
            float dmg = damage * 2.2f * mm.DamageMul * mm.AbilityPowerMul * (crit ? critMultiplier : 1f);
            int n = Combat.Melee(transform.position, transform.forward, 3.8f, 360f, dmg, hp, 9f, crit);
            SFX.Play(Sfx.Leap, 1f);
            if (n > 0) { mm.HitStop(0.07f); mm.HitMarker(crit, false); }
            VFX.Ring(transform.position + Vector3.up * 0.15f, new Color(0.5f, 1f, 0.6f));
            if (tpc) tpc.Shake(0.5f);
        }
    }
}
