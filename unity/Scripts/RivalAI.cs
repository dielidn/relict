using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Relict
{
    // Rival champion bot: clears its own arm zone by zone, levels up, then fights at the arena.
    [RequireComponent(typeof(CharacterController))]
    public class RivalAI : MonoBehaviour, IKnockback
    {
        public bool melee;
        public int arm = 1;
        public int level = 1, xp, gold;
        public float baseDamage = 13f, speed = 5.2f;
        public readonly List<string> artifacts = new List<string>();
        public readonly List<int> artifactValues = new List<int>();
        public bool heart;   // took the boss heart

        CharacterController cc;
        Health hp;
        Animator anim;
        Health target;
        string cur;
        float attackReady, retarget, yVel, strafe = 1f, strafeT, deadT, lastHurt = -99f, dashReady, lockUntil;
        Vector3 knock, dashVel;
        bool dead;

        public Health Hp => hp != null ? hp : (hp = GetComponent<Health>());
        public bool IsDead => dead;
        public float Damage => baseDamage * (1f + 0.12f * (level - 1)) * (heart ? 1.3f : 1f);

        public static RivalAI Create(GameObject model, RuntimeAnimatorController ctrl, Vector3 pos, Quaternion rot, string name, bool melee)
        {
            var go = new GameObject(name);
            go.transform.SetPositionAndRotation(pos, rot);
            var cc = go.AddComponent<CharacterController>();
            cc.radius = 0.38f; cc.height = 1.7f; cc.center = new Vector3(0, 0.88f, 0); cc.slopeLimit = 50f; cc.stepOffset = 0.45f;
            var hp = go.AddComponent<Health>();
            hp.Init(Team.Rival, melee ? 240f : 200f, name);
            var m = Instantiate(model, go.transform);
            m.transform.localPosition = Vector3.zero;
            m.transform.localRotation = Quaternion.identity;
            foreach (var c in m.GetComponentsInChildren<Collider>()) Destroy(c);
            var an = m.GetComponent<Animator>();
            if (an == null) an = m.AddComponent<Animator>();
            an.runtimeAnimatorController = ctrl;
            an.applyRootMotion = false;
            var mpb = new MaterialPropertyBlock();
            foreach (var r in m.GetComponentsInChildren<Renderer>())
            {
                r.GetPropertyBlock(mpb);
                mpb.SetColor("_BaseColor", new Color(1f, 0.55f, 0.45f));
                r.SetPropertyBlock(mpb);
            }
            Art.Part(go.transform, PrimitiveType.Cylinder, new Vector3(0, 1.32f, 0.02f), new Vector3(0.36f, 0.05f, 0.36f),
                Art.Lit(new Color(0.85f, 0.12f, 0.08f), 0.3f, 0f, new Color(0.6f, 0.05f, 0.02f)));
            var ai = go.AddComponent<RivalAI>();
            ai.melee = melee;
            ai.baseDamage = melee ? 22f : 13f;
            return ai;
        }

        void Start()
        {
            cc = GetComponent<CharacterController>();
            hp = GetComponent<Health>();
            anim = GetComponentInChildren<Animator>();
            hp.Damaged += (a, amt) =>
            {
                lastHurt = Time.time;
                if (a != null && !a.Dead && (target == null || a.team == Team.Player)) { target = a; retarget = 2.5f; }
            };
            hp.Died += k =>
            {
                dead = true;
                cc.enabled = false;
                Play("Idle");
                if (MatchManager.I) MatchManager.I.OnRivalKilled(this, k);
            };
        }

        public void Knock(Vector3 v) { knock += v * 0.6f; }

        public void AddXp(int amount)
        {
            xp += amount;
            while (xp >= 60 + 50 * level)
            {
                xp -= 60 + 50 * level;
                level++;
                hp.SetMax(hp.max + 22f);
                hp.Heal(hp.max * 0.3f);
            }
        }

        public void TakeHeart() { heart = true; hp.SetMax(hp.max + 60f); hp.Heal(hp.max); }

        void Play(string s, float fade = 0.15f)
        {
            if (anim == null || cur == s) return;
            cur = s;
            anim.CrossFadeInFixedTime(s, fade);
        }

        void Restart(string s, float speedMul)
        {
            if (anim == null) return;
            cur = s;
            anim.CrossFadeInFixedTime(s, 0.05f, 0, 0f);
            anim.speed = speedMul;
        }

        bool InArena => new Vector2(transform.position.x, transform.position.z).magnitude < LGeo.ArenaRadius + 3f;

        Health PickTarget(MatchManager mm)
        {
            var hero = mm.Hero ? mm.Hero.Hp : null;
            if (hero != null && !hero.Dead && (hero.transform.position - transform.position).magnitude < 30f && LineOfSight(hero)) return hero;
            var bug = Combat.Nearest(transform.position, InArena ? 30f : 22f, h => h.team == Team.Insect);
            if (bug != null) return bug;
            return null;
        }

        bool LineOfSight(Health h)
        {
            Vector3 a = transform.position + Vector3.up * 1.4f, b = h.transform.position + Vector3.up * 1.0f;
            foreach (var hit in Physics.RaycastAll(a, (b - a).normalized, (b - a).magnitude, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider.GetComponentInParent<Health>() != null) continue;
                return false;
            }
            return true;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            if (dead)
            {
                deadT += dt;
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(-85f, transform.eulerAngles.y, 0), dt * 6f);
                if (deadT > 8f) Destroy(gameObject);
                return;
            }
            var mm = MatchManager.I;
            if (mm == null || !mm.Playing) { Play("Idle"); return; }

            // regeneration out of combat
            if (Time.time - lastHurt > 5f) hp.Heal(6f * dt);

            retarget -= dt;
            if (retarget <= 0f || target == null || target.Dead)
            {
                retarget = 0.4f;
                target = PickTarget(mm);
            }

            Vector3 move = Vector3.zero;
            bool fighting = false;
            if (target != null)
            {
                fighting = true;
                Vector3 d = target.transform.position - transform.position; d.y = 0;
                float dist = d.magnitude;
                Vector3 dir = d / Mathf.Max(dist, 0.01f);
                Face(dir, dt);
                if (melee) move = MeleeTick(dir, dist, dt);
                else move = ArcherTick(dir, dist, dt);
            }
            else
            {
                // advance along the arm: clear the current zone, then walk through the opened gate
                Vector3 goal = mm.RivalGoal(this);
                Vector3 d = goal - transform.position; d.y = 0;
                if (d.magnitude > 1.5f) { move = d.normalized * speed; Face(d, dt); }
            }

            if (Time.time < lockUntil && !melee) move *= 0.4f;
            if (cc.isGrounded && yVel < 0f) yVel = -2f;
            yVel -= 24f * dt;
            knock = Vector3.MoveTowards(knock, Vector3.zero, 18f * dt);
            dashVel = Vector3.MoveTowards(dashVel, Vector3.zero, 30f * dt);
            cc.Move((move + knock + dashVel + Vector3.up * yVel) * dt);

            if (Time.time < lockUntil) { }
            else if (move.magnitude > 0.5f || dashVel.magnitude > 1f)
            {
                Play(move.magnitude > 3.6f ? "Run" : "Walk");
                if (anim) anim.speed = 1f;
            }
            else { Play("Idle"); if (anim) anim.speed = 1f; }

            mm.RivalTick(this);
        }

        Vector3 ArcherTick(Vector3 dir, float dist, float dt)
        {
            Vector3 side = Vector3.Cross(Vector3.up, dir) * strafe;
            strafeT -= dt;
            if (strafeT <= 0f) { strafeT = Random.Range(1.0f, 2.4f); strafe = -strafe; }
            Vector3 move;
            if (dist > 18f) move = dir * speed;
            else if (dist < 8f) move = (-dir * 0.85f + side * 0.5f) * speed;
            else move = side * speed * 0.6f;
            if (Time.time >= attackReady && dist < 30f && LineOfSight(target))
            {
                attackReady = Time.time + Random.Range(0.85f, 1.25f);
                Shoot(target);
            }
            return move;
        }

        Vector3 MeleeTick(Vector3 dir, float dist, float dt)
        {
            var tcc = target.GetComponent<CharacterController>();
            float reach = 2.0f + (tcc ? tcc.radius : 0.5f);
            if (dist > 4f && dist < 11f && Time.time >= dashReady)
            {
                dashReady = Time.time + 5f;
                dashVel = dir * 14f;
                Restart("Ability", 1.4f);
                lockUntil = Time.time + 0.4f;
            }
            if (dist <= reach + 0.3f && Time.time >= attackReady)
            {
                attackReady = Time.time + 0.75f;
                StartCoroutine(Swing(dir));
                return Vector3.zero;
            }
            return dist > reach * 0.8f ? dir * speed : Vector3.zero;
        }

        IEnumerator Swing(Vector3 dir)
        {
            Restart("Attack", 1.9f);
            lockUntil = Time.time + 0.35f;
            SFX.Play(Sfx.Swoosh, 0.6f, 0.15f, transform.position);
            yield return new WaitForSeconds(0.12f);
            if (dead) yield break;
            VFX.Slash(transform.position, transform.forward, new Color(1f, 0.45f, 0.35f), 1.5f, Random.value < 0.5f);
            int n = Combat.Melee(transform.position, transform.forward, 2.3f, 150f, Damage, hp, 5f);
            if (n > 0) SFX.Play(Sfx.MeleeHit, 0.8f, 0.1f, transform.position);
        }

        void Face(Vector3 d, float dt)
        {
            d.y = 0;
            if (d.sqrMagnitude < 0.01f) return;
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(d), 1f - Mathf.Exp(-10f * dt));
        }

        void Shoot(Health t)
        {
            Vector3 origin = transform.position + Vector3.up * 1.35f + transform.forward * 0.45f;
            var tcc = t.GetComponent<CharacterController>();
            Vector3 aim = t.transform.position + (tcc ? tcc.center : Vector3.up);
            if (tcc) aim += tcc.velocity * ((aim - origin).magnitude / 50f) * 0.75f;
            aim += Random.insideUnitSphere * 0.7f;
            float dist = (aim - origin).magnitude;
            aim += Vector3.up * dist * dist * 0.0006f;
            Restart("Attack", 2.5f);
            lockUntil = Time.time + 0.25f;
            SFX.Play(Sfx.BowRelease, 0.6f, 0.1f, transform.position);
            Projectile.Fire(origin, (aim - origin).normalized * 50f, Damage, hp, new Color(1f, 0.35f, 0.25f));
        }
    }
}
