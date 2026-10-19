using System.Collections.Generic;
using UnityEngine;

namespace Relict
{
    public class Projectile : MonoBehaviour
    {
        // owner, victim, damage dealt, crit, killed
        public static System.Action<Health, Health, float, bool, bool> OnHit;

        Vector3 vel;
        float dmg, life = 4f, grav = 3f;
        int pierce;
        bool forceCrit;
        Health owner;
        readonly HashSet<Health> hitAlready = new HashSet<Health>();

        public static void Fire(Vector3 pos, Vector3 velocity, float damage, Health owner, Color trail, int pierce = 0, bool heavy = false, bool crit = false)
        {
            var go = new GameObject("Arrow");
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation(velocity);
            BuildArrowMesh(go.transform);
            var tr = go.AddComponent<TrailRenderer>();
            tr.time = heavy ? 0.22f : 0.12f;
            tr.startWidth = heavy ? 0.11f : 0.06f;
            tr.endWidth = 0f;
            tr.sharedMaterial = Art.Additive(heavy ? new Color(0.6f, 1f, 0.9f) : trail);
            tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var p = go.AddComponent<Projectile>();
            p.vel = velocity; p.dmg = damage; p.owner = owner; p.pierce = pierce; p.forceCrit = crit;
            p.grav = heavy ? 1.2f : 3f;
        }

        static void BuildArrowMesh(Transform t)
        {
            Art.Part(t, PrimitiveType.Cube, new Vector3(0, 0, -0.3f), new Vector3(0.03f, 0.03f, 0.75f), Art.Lit(new Color(0.32f, 0.22f, 0.12f), 0.2f));
            Art.Part(t, PrimitiveType.Cube, new Vector3(0, 0, 0.08f), new Vector3(0.06f, 0.06f, 0.1f), Art.Lit(new Color(0.5f, 0.85f, 0.95f), 0.9f, 0.2f, new Color(0.2f, 0.6f, 0.8f)), new Vector3(0, 0, 45));
            Art.Part(t, PrimitiveType.Cube, new Vector3(0, 0.03f, -0.62f), new Vector3(0.005f, 0.06f, 0.14f), Art.Lit(new Color(0.85f, 0.25f, 0.2f), 0.2f));
        }

        // A spent arrow that stays stuck in whatever it hit.
        static void Stick(Vector3 point, Vector3 dir, Transform parent)
        {
            var go = new GameObject("StuckArrow");
            go.transform.position = point - dir * 0.15f;
            go.transform.rotation = Quaternion.LookRotation(dir) * Quaternion.Euler(Random.Range(-8f, 8f), Random.Range(-8f, 8f), 0);
            BuildArrowMesh(go.transform);
            if (parent) go.transform.SetParent(parent, true);
            Destroy(go, parent ? 5f : 8f);
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            life -= dt;
            if (life <= 0f) { Destroy(gameObject); return; }
            vel += Vector3.down * grav * dt;
            Vector3 step = vel * dt;
            float len = step.magnitude;
            if (len < 1e-4f) return;
            Vector3 dir = step / len;
            var hits = Physics.RaycastAll(transform.position, dir, len, ~0, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                var hp = h.collider.GetComponentInParent<Health>();
                if (hp != null && (hp == owner || (owner != null && hp.team == owner.team) || hp.Dead || hitAlready.Contains(hp))) continue;
                if (hp != null)
                {
                    hitAlready.Add(hp);
                    // weak spot: upper part of the body
                    var b = h.collider.bounds;
                    float rel = b.size.y > 0.01f ? (h.point.y - b.min.y) / b.size.y : 0f;
                    bool head = rel > 0.62f;
                    bool crit = head || forceCrit;
                    float d = dmg * (head ? 1.5f : 1f);
                    hp.Damage(d, owner, crit);
                    bool killed = hp.Dead;
                    var kb = hp.GetComponent<IKnockback>();
                    if (kb != null) kb.Knock(dir * (pierce > 0 ? 4f : 1.5f));
                    VFX.Burst(h.point, crit ? new Color(1f, 0.75f, 0.2f) : new Color(1f, 0.9f, 0.55f), crit ? 22 : 12, crit ? 5f : 3f, 0.12f);
                    OnHit?.Invoke(owner, hp, d, crit, killed);
                    if (!killed) Stick(h.point, dir, hp.transform);
                    if (pierce-- > 0) continue;
                    Destroy(gameObject);
                    return;
                }
                // world hit
                Stick(h.point, dir, null);
                VFX.Burst(h.point, new Color(0.7f, 0.6f, 0.4f), 6, 1.6f, 0.08f);
                SFX.Play(Sfx.ArrowWood, 0.5f, 0.1f, h.point);
                Destroy(gameObject);
                return;
            }
            transform.position += step;
            transform.rotation = Quaternion.LookRotation(vel);
        }
    }
}
