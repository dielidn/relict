using System.Collections.Generic;
using UnityEngine;

namespace Relict
{
    // Rival raider squad member: a tinted gecko archer that hunts artifacts, fights anyone and tries to extract.
    [RequireComponent(typeof(CharacterController))]
    public class RivalAI : MonoBehaviour, IKnockback
    {
        public float speed = 4.6f, damage = 11f, engage = 24f;
        public int gold;
        public readonly List<string> artifacts = new List<string>();
        public readonly List<int> artifactValues = new List<int>();
        public float extractProgress;

        CharacterController cc;
        Health hp;
        Animator anim;
        Health target;
        string cur;
        float shootReady, retarget, yVel, strafe = 1f, strafeT, deadT, goalT;
        Vector3 knock, goal;
        bool dead;

        public Health Hp => hp;
        public bool IsDead => dead;

        public static RivalAI Create(GameObject model, RuntimeAnimatorController ctrl, Vector3 pos, string name)
        {
            var go = new GameObject(name);
            go.transform.position = pos;
            var cc = go.AddComponent<CharacterController>();
            cc.radius = 0.38f; cc.height = 1.7f; cc.center = new Vector3(0, 0.88f, 0);
            var hp = go.AddComponent<Health>();
            hp.Init(Team.Rival, 140f, name);
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
                mpb.SetColor("_BaseColor", new Color(1f, 0.5f, 0.42f));
                r.SetPropertyBlock(mpb);
            }
            // squad scarf so rivals read at a distance
            var scarf = Art.Part(go.transform, PrimitiveType.Cylinder, new Vector3(0, 1.32f, 0.02f), new Vector3(0.36f, 0.05f, 0.36f), Art.Lit(new Color(0.85f, 0.12f, 0.08f), 0.3f, 0f, new Color(0.6f, 0.05f, 0.02f)));
            var ai = go.AddComponent<RivalAI>();
            return ai;
        }

        void Start()
        {
            cc = GetComponent<CharacterController>();
            hp = GetComponent<Health>();
            anim = GetComponentInChildren<Animator>();
            hp.Damaged += (a, amt) => { if (a != null && !a.Dead) { target = a; retarget = 3f; } };
            hp.Died += k =>
            {
                dead = true;
                cc.enabled = false;
                Play("Idle");
                if (MatchManager.I) MatchManager.I.OnRivalKilled(this, k);
            };
            shootReady = Time.time + Random.Range(1f, 2f);
        }

        public void Knock(Vector3 v) { knock += v * 0.7f; }

        void Play(string s, float fade = 0.15f)
        {
            if (anim == null || cur == s) return;
            cur = s;
            anim.CrossFadeInFixedTime(s, fade);
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            if (dead)
            {
                deadT += dt;
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(-85f, transform.eulerAngles.y, 0), dt * 6f);
                if (deadT > 6f) Destroy(gameObject);
                return;
            }
            var mm = MatchManager.I;
            if (mm == null || !mm.Playing) { Play("Idle"); return; }

            retarget -= dt;
            if (retarget <= 0f || (target != null && target.Dead))
            {
                retarget = 0.5f;
                var hero = mm.Hero ? mm.Hero.Hp : null;
                if (hero != null && !hero.Dead && (hero.transform.position - transform.position).magnitude < engage + 6f) target = hero;
                else target = Combat.Nearest(transform.position, engage, h => h.team != Team.Rival);
            }

            Vector3 move = Vector3.zero;
            bool shooting = false;
            if (target != null && !(mm.ExtractOpen && mm.TimeLeft < 60f))
            {
                Vector3 d = target.transform.position - transform.position; d.y = 0;
                float dist = d.magnitude;
                Vector3 dir = d / Mathf.Max(dist, 0.01f);
                Vector3 side = Vector3.Cross(Vector3.up, dir) * strafe;
                strafeT -= dt;
                if (strafeT <= 0f) { strafeT = Random.Range(1.2f, 2.6f); strafe = -strafe; }
                if (dist > 16f) move = dir * speed;
                else if (dist < 7f) move = (-dir * 0.8f + side * 0.6f) * speed;
                else move = side * speed * 0.55f;
                Face(dir, dt);
                shooting = true;
                if (Time.time >= shootReady && dist < engage + 4f)
                {
                    shootReady = Time.time + Random.Range(1.1f, 1.7f);
                    Shoot(target);
                }
            }
            else
            {
                goalT -= dt;
                if (goalT <= 0f) { goalT = 1f; goal = mm.RivalGoal(transform.position); }
                Vector3 d = goal - transform.position; d.y = 0;
                if (d.magnitude > 1.2f) { move = d.normalized * speed; Face(d, dt); }
            }

            if (cc.isGrounded && yVel < 0f) yVel = -2f;
            yVel -= 24f * dt;
            knock = Vector3.MoveTowards(knock, Vector3.zero, 18f * dt);
            cc.Move((move + knock + Vector3.up * yVel) * dt);

            if (Time.time < shootReady - 0.9f && shooting) { }
            else if (move.magnitude > 0.5f)
            {
                bool run = move.magnitude > 3.5f;
                Play(run ? "Run" : "Walk");
                if (anim) anim.speed = 1f;
            }
            else Play("Idle");

            // artifacts and extraction
            mm.RivalTick(this, dt);
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
            Vector3 aim = t.transform.position + Vector3.up * 1.0f;
            var tcc = t.GetComponent<CharacterController>();
            if (tcc) aim += tcc.velocity * ((aim - origin).magnitude / 45f) * 0.7f;
            aim += Random.insideUnitSphere * 0.9f;
            float dist = (aim - origin).magnitude;
            aim += Vector3.up * dist * dist * 0.0007f;
            if (anim) { cur = "Attack"; anim.CrossFadeInFixedTime("Attack", 0.05f, 0, 0f); anim.speed = 2.5f; }
            Projectile.Fire(origin, (aim - origin).normalized * 45f, damage, hp, new Color(1f, 0.35f, 0.25f));
        }
    }
}
