using UnityEngine;

namespace Relict
{
    public class Projectile : MonoBehaviour
    {
        Vector3 vel;
        float dmg, life = 4f, grav = 3f;
        Health owner;

        public static void Fire(Vector3 pos, Vector3 velocity, float damage, Health owner, Color trail)
        {
            var go = new GameObject("Arrow");
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation(velocity);
            Art.Part(go.transform, PrimitiveType.Cube, new Vector3(0, 0, -0.3f), new Vector3(0.03f, 0.03f, 0.75f), Art.Lit(new Color(0.32f, 0.22f, 0.12f), 0.2f));
            Art.Part(go.transform, PrimitiveType.Cube, new Vector3(0, 0, 0.08f), new Vector3(0.06f, 0.06f, 0.1f), Art.Lit(new Color(0.5f, 0.85f, 0.95f), 0.9f, 0.2f, new Color(0.2f, 0.6f, 0.8f)), new Vector3(0, 0, 45));
            var tr = go.AddComponent<TrailRenderer>();
            tr.time = 0.12f; tr.startWidth = 0.06f; tr.endWidth = 0f;
            tr.sharedMaterial = Art.Additive(trail);
            tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var p = go.AddComponent<Projectile>();
            p.vel = velocity; p.dmg = damage; p.owner = owner;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            life -= dt;
            if (life <= 0f) { Destroy(gameObject); return; }
            vel += Vector3.down * grav * dt;
            Vector3 step = vel * dt;
            float len = step.magnitude;
            if (len < 1e-4f) return;
            var hits = Physics.RaycastAll(transform.position, step / len, len, ~0, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                var hp = h.collider.GetComponentInParent<Health>();
                if (hp != null && (hp == owner || (owner != null && hp.team == owner.team) || hp.Dead)) continue;
                if (hp != null)
                {
                    hp.Damage(dmg, owner);
                    var kb = hp.GetComponent<IKnockback>();
                    if (kb != null) kb.Knock(vel.normalized * 1.5f);
                    VFX.Burst(h.point, new Color(1f, 0.85f, 0.45f), 10, 3f, 0.1f);
                    Destroy(gameObject);
                    return;
                }
                transform.position = h.point - step / len * 0.25f;
                VFX.Burst(h.point, new Color(0.7f, 0.6f, 0.4f), 5, 1.5f, 0.08f);
                enabled = false;
                Destroy(gameObject, 4f);
                return;
            }
            transform.position += step;
            transform.rotation = Quaternion.LookRotation(vel);
        }
    }
}
