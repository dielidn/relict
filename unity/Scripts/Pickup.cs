using System.Collections.Generic;
using UnityEngine;

namespace Relict
{
    public enum PickupKind { Gold, Artifact, Heal }

    public class Pickup : MonoBehaviour
    {
        public static readonly List<Pickup> All = new List<Pickup>();

        public PickupKind kind;
        public int amount;
        public string title;
        Vector3 basePos, vel;
        float t, settle;
        Transform visual;

        void OnEnable() { All.Add(this); }
        void OnDisable() { All.Remove(this); }

        public static Pickup Gold(Vector3 pos, int amount)
        {
            var go = new GameObject("Gold");
            go.transform.position = pos + Vector3.up * 0.6f;
            var p = go.AddComponent<Pickup>();
            p.kind = PickupKind.Gold; p.amount = amount; p.title = amount + " золота";
            var v = Art.Part(go.transform, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.28f, 0.03f, 0.28f),
                Art.Lit(new Color(1f, 0.75f, 0.2f), 0.85f, 1f, new Color(0.6f, 0.4f, 0.05f)), new Vector3(90, 0, 0));
            p.visual = v.transform;
            var r = Random.insideUnitCircle * 2.2f;
            p.vel = new Vector3(r.x, Random.Range(3f, 5f), r.y);
            p.settle = 0.8f;
            return p;
        }

        public static Pickup Heal(Vector3 pos, int amount)
        {
            var go = new GameObject("HealSpore");
            go.transform.position = pos + Vector3.up * 0.7f;
            var p = go.AddComponent<Pickup>();
            p.kind = PickupKind.Heal; p.amount = amount; p.title = "Целебная спора";
            var v = Art.Part(go.transform, PrimitiveType.Sphere, Vector3.zero, Vector3.one * 0.35f,
                Art.Lit(new Color(0.4f, 1f, 0.5f), 0.6f, 0f, new Color(0.3f, 1.4f, 0.4f)));
            p.visual = v.transform;
            return p;
        }

        public static Pickup Artifact(Vector3 pos, string name, int value, GameObject model)
        {
            var go = new GameObject("Artifact_" + name);
            go.transform.position = pos + Vector3.up * 1.0f;
            var p = go.AddComponent<Pickup>();
            p.kind = PickupKind.Artifact; p.amount = value; p.title = name;
            Transform v;
            if (model != null)
            {
                var m = Instantiate(model, go.transform);
                m.transform.localPosition = Vector3.down * 0.35f;
                m.transform.localScale = m.transform.localScale * 0.6f;
                foreach (var c in m.GetComponentsInChildren<Collider>()) Destroy(c);
                m.isStatic = false;
                v = m.transform;
            }
            else v = Art.Part(go.transform, PrimitiveType.Cube, Vector3.zero, Vector3.one * 0.4f, Art.Lit(Color.cyan, 0.9f, 0, Color.cyan)).transform;
            p.visual = v;
            var l = new GameObject("Glow").AddComponent<Light>();
            l.type = LightType.Point; l.color = new Color(0.4f, 0.9f, 1f); l.intensity = 3f; l.range = 5f;
            l.transform.SetParent(go.transform, false);
            var beam = Art.Part(go.transform, PrimitiveType.Cylinder, Vector3.up * 6f, new Vector3(0.12f, 6f, 0.12f), Art.Additive(new Color(0.3f, 0.8f, 1f, 0.35f)));
            beam.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return p;
        }

        void Start() { basePos = transform.position; }

        void Update()
        {
            float dt = Time.deltaTime;
            t += dt;
            if (settle > 0f)
            {
                settle -= dt;
                vel += Vector3.down * 14f * dt;
                transform.position += vel * dt;
                if (transform.position.y < 0.35f) { var p = transform.position; p.y = 0.35f; transform.position = p; vel *= 0.3f; vel.y = Mathf.Abs(vel.y); }
                basePos = transform.position;
                return;
            }
            if (visual) visual.Rotate(0, 90f * dt, 0, Space.World);
            var mm = MatchManager.I;
            if (mm == null || !mm.Playing) return;
            var hero = mm.Hero;
            if (hero != null && !hero.Hp.Dead)
            {
                Vector3 hp = hero.transform.position + Vector3.up * 0.8f;
                float d = Vector3.Distance(hp, transform.position);
                float magnet = kind == PickupKind.Artifact ? 1.8f : 4f;
                if (d < magnet && (kind != PickupKind.Heal || hero.Hp.hp < hero.Hp.max))
                {
                    transform.position = Vector3.MoveTowards(transform.position, hp, (10f - d) * dt * 1.5f);
                    if (d < 0.8f) { mm.Collect(this, null); Destroy(gameObject); }
                    return;
                }
            }
            transform.position = basePos + Vector3.up * Mathf.Sin(t * 2f) * 0.12f;
        }
    }
}
