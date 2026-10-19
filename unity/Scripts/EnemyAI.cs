using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Relict
{
    public enum InsectKind { Ant, Beetle, Mantis, Colossus }

    [RequireComponent(typeof(CharacterController))]
    public class EnemyAI : MonoBehaviour, IKnockback
    {
        public InsectKind kind;
        public float speed = 4f, damage = 8f, range = 1.4f, cooldown = 1.2f, aggro = 14f, leash = 30f;
        public int gold = 10, xp = 15;
        public bool boss, mini;
        public int arm = -1, zone = -1;
        public Vector3 home;
        public string title;

        CharacterController cc;
        Health hp;
        Transform body;
        Vector3 bodyBase;
        readonly List<Transform> legs = new List<Transform>();
        readonly List<Quaternion> legBase = new List<Quaternion>();
        Transform armL, armR;
        public Health target;
        float atkReady, yVel, phase, retarget, wanderT, lunge, deathT, chargeReady, chargeUntil;
        Vector3 knock, wander, chargeDir;
        bool dying, attacking;

        public Health Hp => hp;
        public bool Dying => dying;

        // ---------- construction ----------
        public static EnemyAI Create(InsectKind k, Vector3 pos, bool isBoss = false)
        {
            var go = new GameObject(k.ToString());
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(0, Random.Range(0, 360f), 0);
            var cc = go.AddComponent<CharacterController>();
            var hp = go.AddComponent<Health>();
            var ai = go.AddComponent<EnemyAI>();
            ai.kind = k; ai.home = pos; ai.boss = isBoss;
            var body = new GameObject("Body").transform;
            body.SetParent(go.transform, false);
            ai.body = body;

            switch (k == InsectKind.Colossus ? InsectKind.Beetle : k)
            {
                case InsectKind.Ant:
                    {
                        cc.radius = 0.45f; cc.height = 0.9f; cc.center = new Vector3(0, 0.5f, 0);
                        hp.Init(Team.Insect, 34f, "Муравей-солдат");
                        ai.speed = 5.2f; ai.damage = 8f; ai.range = 1.5f; ai.cooldown = 1.1f; ai.gold = 8; ai.xp = 15; ai.aggro = 15f;
                        body.localPosition = new Vector3(0, 0.55f, 0);
                        var shell = Art.Lit(new Color(0.28f, 0.07f, 0.04f), 0.75f, 0.1f);
                        var dark = Art.Lit(new Color(0.08f, 0.04f, 0.03f), 0.6f);
                        Art.Part(body, PrimitiveType.Sphere, new Vector3(0, 0, 0), new Vector3(0.32f, 0.28f, 0.42f), shell);
                        Art.Part(body, PrimitiveType.Sphere, new Vector3(0, 0.08f, -0.55f), new Vector3(0.5f, 0.42f, 0.68f), shell);
                        Art.Part(body, PrimitiveType.Sphere, new Vector3(0, 0.05f, 0.42f), new Vector3(0.34f, 0.28f, 0.32f), shell);
                        Art.Part(body, PrimitiveType.Cube, new Vector3(0.09f, -0.02f, 0.6f), new Vector3(0.04f, 0.04f, 0.2f), dark, new Vector3(0, -25, 0));
                        Art.Part(body, PrimitiveType.Cube, new Vector3(-0.09f, -0.02f, 0.6f), new Vector3(0.04f, 0.04f, 0.2f), dark, new Vector3(0, 25, 0));
                        Art.Part(body, PrimitiveType.Cylinder, new Vector3(0.1f, 0.3f, 0.6f), new Vector3(0.02f, 0.2f, 0.02f), dark, new Vector3(50, 20, 0));
                        Art.Part(body, PrimitiveType.Cylinder, new Vector3(-0.1f, 0.3f, 0.6f), new Vector3(0.02f, 0.2f, 0.02f), dark, new Vector3(50, -20, 0));
                        var eye = Art.Lit(Color.black, 0.9f, 0f, new Color(1.5f, 0.2f, 0.1f));
                        Art.Part(body, PrimitiveType.Sphere, new Vector3(0.13f, 0.1f, 0.52f), Vector3.one * 0.07f, eye);
                        Art.Part(body, PrimitiveType.Sphere, new Vector3(-0.13f, 0.1f, 0.52f), Vector3.one * 0.07f, eye);
                        ai.BuildLegs(dark, 0.17f, 0.42f, 0.03f, new[] { 0.2f, 0f, -0.2f });
                        break;
                    }
                case InsectKind.Beetle:
                    {
                        cc.radius = 0.8f; cc.height = 1.3f; cc.center = new Vector3(0, 0.7f, 0);
                        hp.Init(Team.Insect, 120f, "Жук-броненосец");
                        ai.speed = 3.2f; ai.damage = 18f; ai.range = 1.9f; ai.cooldown = 1.6f; ai.gold = 30; ai.xp = 40; ai.aggro = 13f;
                        body.localPosition = new Vector3(0, 0.7f, 0);
                        var shell = k == InsectKind.Colossus ? Art.Lit(new Color(0.35f, 0.12f, 0.05f), 0.9f, 0.8f, new Color(0.15f, 0.04f, 0f)) : Art.Lit(new Color(0.06f, 0.3f, 0.2f), 0.9f, 0.7f);
                        var dark = Art.Lit(new Color(0.05f, 0.06f, 0.05f), 0.5f);
                        Art.Part(body, PrimitiveType.Sphere, new Vector3(0, 0.1f, -0.15f), new Vector3(1.25f, 0.8f, 1.55f), shell);
                        Art.Part(body, PrimitiveType.Sphere, new Vector3(0, -0.05f, 0.7f), new Vector3(0.6f, 0.45f, 0.45f), dark);
                        Art.Part(body, PrimitiveType.Cylinder, new Vector3(0, 0.25f, 1.0f), new Vector3(0.09f, 0.32f, 0.09f), dark, new Vector3(55, 0, 0));
                        Art.Part(body, PrimitiveType.Cube, new Vector3(0, -0.15f, -0.15f), new Vector3(0.03f, 0.02f, 1.5f), dark);
                        var eye = Art.Lit(Color.black, 0.9f, 0f, new Color(1.2f, 0.9f, 0.1f));
                        Art.Part(body, PrimitiveType.Sphere, new Vector3(0.2f, 0.05f, 0.88f), Vector3.one * 0.09f, eye);
                        Art.Part(body, PrimitiveType.Sphere, new Vector3(-0.2f, 0.05f, 0.88f), Vector3.one * 0.09f, eye);
                        ai.BuildLegs(dark, 0.45f, 0.7f, 0.06f, new[] { 0.45f, 0f, -0.45f });
                        break;
                    }
                default:
                    {
                        cc.radius = 0.9f; cc.height = 3.2f; cc.center = new Vector3(0, 1.6f, 0);
                        hp.Init(Team.Insect, 1100f, "Богомол-Страж");
                        ai.speed = 4.3f; ai.damage = 32f; ai.range = 3.2f; ai.cooldown = 1.5f; ai.gold = 250; ai.xp = 260; ai.aggro = 16f; ai.leash = 24f;
                        body.localPosition = new Vector3(0, 1.3f, 0);
                        var skin = Art.Lit(new Color(0.32f, 0.62f, 0.16f), 0.6f, 0.05f);
                        var pale = Art.Lit(new Color(0.55f, 0.75f, 0.3f), 0.5f);
                        var violet = Art.Lit(new Color(0.35f, 0.12f, 0.4f), 0.7f);
                        Art.Part(body, PrimitiveType.Sphere, new Vector3(0, 0.0f, -0.9f), new Vector3(0.75f, 0.6f, 1.7f), skin, new Vector3(-12, 0, 0));
                        Art.Part(body, PrimitiveType.Cylinder, new Vector3(0, 0.75f, 0.15f), new Vector3(0.3f, 0.75f, 0.3f), pale, new Vector3(20, 0, 0));
                        Art.Part(body, PrimitiveType.Sphere, new Vector3(0, 1.65f, 0.45f), new Vector3(0.6f, 0.45f, 0.4f), skin);
                        var eye = Art.Lit(Color.black, 0.95f, 0f, new Color(2.2f, 1.6f, 0.2f));
                        Art.Part(body, PrimitiveType.Sphere, new Vector3(0.27f, 1.72f, 0.5f), Vector3.one * 0.22f, eye);
                        Art.Part(body, PrimitiveType.Sphere, new Vector3(-0.27f, 1.72f, 0.5f), Vector3.one * 0.22f, eye);
                        Art.Part(body, PrimitiveType.Cube, new Vector3(0.35f, -0.05f, -1.0f), new Vector3(0.05f, 0.5f, 1.4f), violet, new Vector3(-20, 8, 0));
                        Art.Part(body, PrimitiveType.Cube, new Vector3(-0.35f, -0.05f, -1.0f), new Vector3(0.05f, 0.5f, 1.4f), violet, new Vector3(-20, -8, 0));
                        ai.armL = ai.BuildScythe(new Vector3(0.28f, 1.15f, 0.4f), skin, pale);
                        ai.armR = ai.BuildScythe(new Vector3(-0.28f, 1.15f, 0.4f), skin, pale);
                        ai.BuildLegs(Art.Lit(new Color(0.25f, 0.5f, 0.12f), 0.5f), 0.3f, 1.45f, 0.07f, new[] { 0.15f, -0.35f, -0.85f }, -1.0f);
                        var l = new GameObject("BossGlow").AddComponent<Light>();
                        l.type = LightType.Point; l.color = new Color(1f, 0.85f, 0.3f); l.intensity = 2f; l.range = 4f;
                        l.transform.SetParent(body, false); l.transform.localPosition = new Vector3(0, 1.8f, 1f);
                        break;
                    }
            }
            if (k == InsectKind.Colossus)
            {
                go.transform.localScale = Vector3.one * 1.75f;
                hp.Init(Team.Insect, 520f, "Жук-Колосс");
                ai.mini = true;
                ai.speed = 3.6f; ai.damage = 26f; ai.range = 2.2f; ai.cooldown = 2.2f; ai.gold = 120; ai.xp = 140; ai.aggro = 18f; ai.leash = 26f;
                var l = new GameObject("ColossusGlow").AddComponent<Light>();
                l.type = LightType.Point; l.color = new Color(1f, 0.5f, 0.2f); l.intensity = 1.5f; l.range = 5f;
                l.transform.SetParent(body, false); l.transform.localPosition = new Vector3(0, 1f, 0.8f);
            }
            ai.bodyBase = body.localPosition;
            ai.title = hp.displayName;
            return ai;
        }

        void BuildLegs(Material mat, float halfWidth, float length, float thick, float[] zs, float yOffset = 0f)
        {
            int i = 0;
            foreach (float z in zs)
            {
                for (int s = -1; s <= 1; s += 2)
                {
                    var pivot = new GameObject("Leg").transform;
                    pivot.SetParent(body, false);
                    pivot.localPosition = new Vector3(s * halfWidth, yOffset, z);
                    pivot.localRotation = Quaternion.Euler(0, s * (z * 40f), s * -55f);
                    Art.Part(pivot, PrimitiveType.Cylinder, new Vector3(0, -length * 0.5f, 0), new Vector3(thick, length * 0.5f, thick), mat);
                    var knee = Art.Part(pivot, PrimitiveType.Cylinder, new Vector3(s * -length * 0.25f, -length * 0.95f, 0), new Vector3(thick * 0.8f, length * 0.4f, thick * 0.8f), mat, new Vector3(0, 0, s * -50f));
                    legs.Add(pivot);
                    legBase.Add(pivot.localRotation);
                    i++;
                }
            }
        }

        Transform BuildScythe(Vector3 shoulder, Material a, Material b)
        {
            var pivot = new GameObject("Scythe").transform;
            pivot.SetParent(body, false);
            pivot.localPosition = shoulder;
            pivot.localRotation = Quaternion.Euler(-30, 0, 0);
            Art.Part(pivot, PrimitiveType.Cube, new Vector3(0, 0, 0.45f), new Vector3(0.14f, 0.16f, 0.9f), a);
            Art.Part(pivot, PrimitiveType.Cube, new Vector3(0, -0.35f, 0.85f), new Vector3(0.08f, 0.8f, 0.12f), b, new Vector3(-20, 0, 0));
            Art.Part(pivot, PrimitiveType.Cube, new Vector3(0, -0.72f, 0.95f), new Vector3(0.05f, 0.25f, 0.08f), Art.Lit(new Color(0.9f, 0.9f, 0.75f), 0.8f), new Vector3(30, 0, 0));
            return pivot;
        }

        // ---------- behaviour ----------
        void Start()
        {
            cc = GetComponent<CharacterController>();
            hp = GetComponent<Health>();
            hp.Damaged += (a, amt) =>
            {
                if (a != null && !a.Dead) { target = a; retarget = 2.5f; }
                StartCoroutine(Flash());
            };
            hp.Died += k =>
            {
                dying = true;
                cc.enabled = false;
                VFX.Burst(transform.position + Vector3.up * 0.7f, new Color(0.6f, 1f, 0.3f), boss ? 80 : 25, boss ? 7f : 4f, boss ? 0.4f : 0.18f);
                if (MatchManager.I) MatchManager.I.OnInsectKilled(this, k);
            };
            chargeReady = Time.time + 5f;
        }

        IEnumerator Flash()
        {
            body.localScale = Vector3.one * 1.08f;
            yield return new WaitForSeconds(0.08f);
            body.localScale = Vector3.one;
        }

        public void Knock(Vector3 v) { knock += v * (boss ? 0.15f : mini ? 0.2f : kind == InsectKind.Beetle ? 0.5f : 1f); }

        Health FindTarget()
        {
            float r = aggro;
            return Combat.Nearest(transform.position, r, h => h.team != Team.Insect);
        }

        void Face(Vector3 d, float dt, float rate = 8f)
        {
            d.y = 0;
            if (d.sqrMagnitude < 0.01f) return;
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(d), 1f - Mathf.Exp(-rate * dt));
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            if (dying)
            {
                deathT += dt;
                body.localRotation = Quaternion.Euler(0, 0, Mathf.Min(deathT * 400f, 175f));
                if (deathT > 1.5f) transform.position += Vector3.down * dt * 0.5f;
                if (deathT > 4f) Destroy(gameObject);
                return;
            }

            bool playing = MatchManager.I && MatchManager.I.Playing;
            retarget -= dt;
            if (retarget <= 0f || (target != null && target.Dead))
            {
                retarget = 0.4f;
                var t = FindTarget();
                if (t != null || target == null || target.Dead) target = t;
            }

            Vector3 move = Vector3.zero;
            bool leashed = (transform.position - home).magnitude > leash;
            if (playing && target != null && !leashed)
            {
                Vector3 d = target.transform.position - transform.position; d.y = 0;
                float dist = d.magnitude;
                var tcc = target.GetComponent<CharacterController>();
                float reach = range + (tcc ? tcc.radius : 0.4f);
                if (boss && Time.time < chargeUntil)
                {
                    move = chargeDir * speed * 3.2f;
                    Combat.Melee(transform.position, chargeDir, 1.6f, 120f, damage * 2.2f * dt, hp, 6f);
                }
                else if (boss && Time.time >= chargeReady && dist > 6f && dist < 18f)
                {
                    chargeReady = Time.time + 9f;
                    chargeDir = d / dist;
                    chargeUntil = Time.time + 0.7f;
                    MatchManager.I.Toast("Богомол бросается в атаку!");
                }
                else
                {
                    Face(d, dt);
                    if (!attacking)
                    {
                        if (dist > reach * 0.85f) move = d / Mathf.Max(dist, 0.01f) * speed;
                        else if (Time.time >= atkReady) StartCoroutine(Attack());
                    }
                }
            }
            else
            {
                if (leashed) { target = null; wander = home; wanderT = 3f; }
                wanderT -= dt;
                if (wanderT <= 0f)
                {
                    wanderT = Random.Range(2.5f, 5f);
                    var r = Random.insideUnitCircle * (boss ? 5f : 8f);
                    wander = home + new Vector3(r.x, 0, r.y);
                }
                Vector3 d = wander - transform.position; d.y = 0;
                if (d.magnitude > 1f) { move = d.normalized * speed * (leashed ? 1f : 0.35f); Face(d, dt, 4f); }
            }

            if (cc.isGrounded && yVel < 0f) yVel = -2f;
            yVel -= 25f * dt;
            knock = Vector3.MoveTowards(knock, Vector3.zero, 15f * dt);
            cc.Move((move + knock + Vector3.up * yVel) * dt);

            // procedural legs and body
            float sp = move.magnitude;
            float k = Mathf.Clamp01(sp / speed);
            phase += dt * (sp * (boss ? 1.6f : 3.2f) + 0.6f);
            for (int i = 0; i < legs.Count; i++)
            {
                float side = (i % 2 == 0) ? 1f : -1f;
                float tri = ((i / 2) % 2 == 0) ? 0f : Mathf.PI;
                float a = Mathf.Sin(phase * 2f + tri + (side > 0 ? 0 : Mathf.PI)) * 28f * k;
                float lift = Mathf.Max(0, Mathf.Cos(phase * 2f + tri + (side > 0 ? 0 : Mathf.PI))) * 12f * k;
                legs[i].localRotation = legBase[i] * Quaternion.Euler(lift, a, 0);
            }
            body.localPosition = bodyBase + Vector3.up * Mathf.Abs(Mathf.Sin(phase * 2f)) * 0.04f * k + Vector3.forward * lunge;
            if (armL)
            {
                float swing = attacking ? 1f : 0f;
                armL.localRotation = Quaternion.Euler(Mathf.Lerp(-30f, 60f, lunge * 2f) + Mathf.Sin(Time.time * 2f) * 4f, 0, 0);
                armR.localRotation = Quaternion.Euler(Mathf.Lerp(-30f, 60f, lunge * 2f) + Mathf.Sin(Time.time * 2f + 1f) * 4f, 0, 0);
            }
        }

        // Red ground marker that fills up during the wind-up: dodge before it completes.
        static void Telegraph(Vector3 pos, float radius, float time)
        {
            pos = LGeo.OnGround(pos) + Vector3.up * 0.06f;
            var root = new GameObject("Telegraph");
            root.transform.position = pos;
            Art.Part(root.transform, PrimitiveType.Cylinder, Vector3.zero, new Vector3(radius * 2f, 0.01f, radius * 2f), Art.Additive(new Color(1f, 0.2f, 0.1f, 0.35f)));
            var inner = Art.Part(root.transform, PrimitiveType.Cylinder, Vector3.up * 0.01f, new Vector3(0.1f, 0.012f, 0.1f), Art.Additive(new Color(1f, 0.35f, 0.15f, 0.55f)));
            foreach (var r in root.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            root.AddComponent<TelegraphFill>().Init(inner.transform, radius, time);
        }

        IEnumerator Attack()
        {
            attacking = true;
            atkReady = Time.time + cooldown;
            float wind = boss ? 0.55f : mini ? 0.7f : kind == InsectKind.Beetle ? 0.45f : 0.32f;
            if (mini || boss) Telegraph(transform.position, mini ? range + 1.6f : range + 0.5f, wind);
            else if (kind == InsectKind.Beetle) Telegraph(transform.position + transform.forward * range * 0.6f, range * 0.75f, wind);
            for (float t = 0; t < wind; t += Time.deltaTime) { lunge = -0.15f * (t / wind); body.localScale = Vector3.one * (1f + 0.1f * (t / wind)); yield return null; }
            body.localScale = Vector3.one;
            for (float t = 0; t < 0.1f; t += Time.deltaTime) { lunge = Mathf.Lerp(-0.15f, 0.45f, t / 0.1f); yield return null; }
            if (!dying)
            {
                if (mini)
                {
                    VFX.Ring(transform.position + Vector3.up * 0.1f, new Color(1f, 0.6f, 0.3f));
                    SFX.Play(Sfx.Leap, 0.8f, 0.05f, transform.position);
                    Combat.Melee(transform.position, transform.forward, range + 1.6f, 360f, damage, hp, 9f);
                }
                else if (boss)
                {
                    VFX.Slash(transform.position + transform.forward * 0.6f, transform.forward, new Color(1f, 0.9f, 0.4f), 2.6f);
                    Combat.Melee(transform.position, transform.forward, range + 0.5f, 140f, damage, hp, 10f);
                }
                else if (target != null && !target.Dead)
                {
                    Vector3 d = target.transform.position - transform.position; d.y = 0;
                    var tcc = target.GetComponent<CharacterController>();
                    if (d.magnitude < range + (tcc ? tcc.radius : 0.4f) + 0.6f && Vector3.Angle(transform.forward, d) < 70f)
                    {
                        target.Damage(damage, hp);
                        var kb = target.GetComponent<IKnockback>();
                        if (kb != null) kb.Knock(d.normalized * (kind == InsectKind.Beetle ? 7f : 3f));
                    }
                }
            }
            for (float t = 0; t < 0.25f; t += Time.deltaTime) { lunge = Mathf.Lerp(0.45f, 0f, t / 0.25f); yield return null; }
            lunge = 0f;
            attacking = false;
        }
    }

    public class TelegraphFill : MonoBehaviour
    {
        Transform inner; float radius, time, t;
        public void Init(Transform i, float r, float duration) { inner = i; radius = r; time = Mathf.Max(0.05f, duration); }
        void Update()
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / time);
            if (inner) inner.localScale = new Vector3(radius * 2f * k, 0.012f, radius * 2f * k);
            if (t > time + 0.1f) Destroy(gameObject);
        }
    }
}
