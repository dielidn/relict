using System.Collections.Generic;
using UnityEngine;

namespace Relict
{
    // Ground telegraphs and camera helpers shared by insect attacks.
    public static class Tele
    {
        static readonly Color Outer = new Color(1f, 0.2f, 0.1f, 0.35f), Inner = new Color(1f, 0.35f, 0.15f, 0.55f);

        public static void Circle(Vector3 pos, float radius, float time)
        {
            pos = LGeo.OnGround(pos) + Vector3.up * 0.06f;
            var root = new GameObject("Telegraph");
            root.transform.position = pos;
            Art.Part(root.transform, PrimitiveType.Cylinder, Vector3.zero, new Vector3(radius * 2f, 0.01f, radius * 2f), Art.Additive(Outer));
            var inner = Art.Part(root.transform, PrimitiveType.Cylinder, Vector3.up * 0.01f, new Vector3(0.1f, 0.012f, 0.1f), Art.Additive(Inner));
            NoShadows(root);
            root.AddComponent<TelegraphFill>().Init(inner.transform, radius, time);
        }

        public static void Line(Vector3 pos, Vector3 dir, float length, float width, float time)
        {
            dir.y = 0; dir.Normalize();
            pos = LGeo.OnGround(pos) + Vector3.up * 0.07f;
            var root = new GameObject("TelegraphLine");
            root.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(dir));
            Art.Part(root.transform, PrimitiveType.Cube, new Vector3(0, 0, length * 0.5f), new Vector3(width, 0.01f, length), Art.Additive(Outer));
            var inner = Art.Part(root.transform, PrimitiveType.Cube, new Vector3(0, 0.01f, 0), new Vector3(width * 0.8f, 0.012f, 0.01f), Art.Additive(Inner));
            NoShadows(root);
            root.AddComponent<TelegraphLineFill>().Init(inner.transform, length, width * 0.8f, time);
        }

        static void NoShadows(GameObject g)
        {
            foreach (var r in g.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        // Camera shake that fades with distance from the camera target.
        public static void Shake(Vector3 at, float amount, float radius)
        {
            var cam = Camera.main;
            if (cam == null) return;
            var tpc = cam.GetComponent<ThirdPersonCamera>();
            if (tpc == null || tpc.target == null) return;
            float d = Vector3.Distance(tpc.target.position, at);
            float k = Mathf.Clamp01(1f - d / radius);
            if (k > 0f) tpc.Shake(amount * k);
        }

        // Damage every non-insect in a radius (horizontal distance to the feet, with a height window).
        public static int Blast(Vector3 at, float radius, float dmg, Health owner, float knock, float upKnock, HashSet<Health> skip = null, bool groundedOnly = false, float innerRadius = -1f)
        {
            int n = 0;
            for (int i = Health.All.Count - 1; i >= 0; i--)
            {
                var h = Health.All[i];
                if (h == null || h.Dead || h.team == Team.Insect) continue;
                if (skip != null && skip.Contains(h)) continue;
                Vector3 d = h.transform.position - at;
                if (Mathf.Abs(d.y) > 2.5f) continue;
                d.y = 0;
                var cc = h.GetComponent<CharacterController>();
                float rad = cc ? cc.radius : 0.4f;
                float dist = d.magnitude;
                if (dist - rad > radius) continue;
                if (innerRadius > 0f && dist + rad < innerRadius) continue;
                if (groundedOnly && cc != null && !cc.isGrounded) continue;
                h.Damage(dmg, owner);
                skip?.Add(h);
                n++;
                var kb = h.GetComponent<IKnockback>();
                if (kb != null) kb.Knock((dist > 0.01f ? d / dist : Vector3.forward) * knock + Vector3.up * upKnock);
            }
            return n;
        }
    }

    // A lobbed stone: ballistic flight, dust trail, splash damage and debris on impact.
    public class Rock : MonoBehaviour
    {
        const float G = 22f;
        Vector3 vel, spin;
        float dmg, splash, size, t, life;
        Health owner;
        bool big, done;
        float puffT;

        static UnityEngine.Mesh rockMesh;
        static Material rockMat, rockMatDark;

        public static Rock Throw(Vector3 from, Vector3 to, float flightTime, float damage, Health owner, float size, float splash, bool telegraph, bool big = false)
        {
            flightTime = Mathf.Max(0.25f, flightTime);
            var go = new GameObject(big ? "Boulder" : "Pebble");
            go.transform.position = from;
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = RockMesh();
            var mr = go.AddComponent<MeshRenderer>();
            if (rockMat == null)
            {
                rockMat = Art.Lit(new Color(0.42f, 0.38f, 0.33f), 0.15f);
                rockMatDark = Art.Lit(new Color(0.3f, 0.26f, 0.22f), 0.1f, 0f, new Color(0.35f, 0.12f, 0.02f));
            }
            mr.sharedMaterial = big ? rockMatDark : rockMat;
            go.transform.localScale = new Vector3(size * Random.Range(0.85f, 1.15f), size * Random.Range(0.7f, 0.95f), size * Random.Range(0.85f, 1.15f)) * 2f;
            go.transform.rotation = Random.rotation;
            var r = go.AddComponent<Rock>();
            r.vel = (to - from) / flightTime + 0.5f * G * flightTime * Vector3.up;
            r.spin = Random.onUnitSphere * Random.Range(200f, 520f);
            r.dmg = damage; r.owner = owner; r.size = size; r.splash = splash; r.big = big;
            r.life = flightTime + 1.5f;
            if (big)
            {
                var l = new GameObject("Ember").AddComponent<Light>();
                l.type = LightType.Point; l.color = new Color(1f, 0.5f, 0.15f); l.intensity = 2.5f; l.range = 3f; l.shadows = LightShadows.None;
                l.transform.SetParent(go.transform, false);
            }
            if (telegraph) Tele.Circle(to, splash, flightTime);
            return r;
        }

        // Low-poly lumpy rock, built once.
        static UnityEngine.Mesh RockMesh()
        {
            if (rockMesh) return rockMesh;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var src = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(tmp);
            var v = src.vertices;
            var rng = new System.Random(5);
            var offset = new Dictionary<Vector3, float>();
            for (int i = 0; i < v.Length; i++)
            {
                var key = new Vector3(Mathf.Round(v[i].x * 100f), Mathf.Round(v[i].y * 100f), Mathf.Round(v[i].z * 100f));
                if (!offset.TryGetValue(key, out float o)) { o = 0.8f + (float)rng.NextDouble() * 0.35f; offset[key] = o; }
                v[i] *= o;
            }
            rockMesh = new UnityEngine.Mesh { name = "Rock" };
            rockMesh.vertices = v;
            rockMesh.triangles = src.triangles;
            rockMesh.RecalculateNormals();
            rockMesh.RecalculateBounds();
            return rockMesh;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f || done) return;
            t += dt;
            Vector3 p0 = transform.position;
            vel += Vector3.down * G * dt;
            Vector3 p1 = p0 + vel * dt;
            transform.Rotate(spin * dt, Space.World);

            puffT -= dt;
            if (puffT <= 0f)
            {
                puffT = big ? 0.05f : 0.09f;
                VFX.Burst(p0, big ? new Color(1f, 0.55f, 0.2f, 0.8f) : new Color(0.7f, 0.62f, 0.5f, 0.6f), big ? 3 : 1, 0.5f, size * 0.8f, 0.35f, 0f);
            }

            // direct hit on a hero / rival
            for (int i = Health.All.Count - 1; i >= 0; i--)
            {
                var h = Health.All[i];
                if (h == null || h.Dead || h.team == Team.Insect) continue;
                var cc = h.GetComponent<CharacterController>();
                Vector3 c = h.transform.position + (cc ? cc.center : Vector3.up);
                float hr = (cc ? cc.radius : 0.4f) + size;
                Vector3 d = p1 - c;
                if (Mathf.Abs(d.y) < (cc ? cc.height * 0.5f : 1f) + size && new Vector2(d.x, d.z).magnitude < hr) { Impact(p1); return; }
            }

            // ground / walls (insects are ignored so stones fly over the pack)
            Vector3 seg = p1 - p0;
            if (Physics.Raycast(p0, seg.normalized, out var hit, seg.magnitude + size * 0.5f, ~0, QueryTriggerInteraction.Ignore))
            {
                var hh = hit.collider.GetComponentInParent<Health>();
                if (hh == null || hh.team != Team.Insect) { Impact(hit.point); return; }
            }
            if (p1.y < LGeo.Ground(p1) - 0.2f) { Impact(LGeo.OnGround(p1)); return; }
            transform.position = p1;
            if (t > life) Destroy(gameObject);
        }

        void Impact(Vector3 at)
        {
            done = true;
            Tele.Blast(at, splash, dmg, owner, big ? 9f : 4f, big ? 3f : 1f);
            var dust = new Color(0.62f, 0.54f, 0.42f);
            if (big)
            {
                VFX.Burst(at + Vector3.up * 0.3f, dust, 26, 5f, 0.4f, 0.8f, 0.8f);
                VFX.Burst(at + Vector3.up * 0.2f, new Color(1f, 0.55f, 0.2f), 18, 7f, 0.14f, 0.45f, 1.4f);
                VFX.Burst(at + Vector3.up * 0.1f, new Color(0.35f, 0.3f, 0.26f), 12, 6f, 0.2f, 0.9f, 2.5f);
                Juice.Splat(at, new Color(0.18f, 0.14f, 0.1f), 0.8f);
                Juice.LightFlash(at + Vector3.up * 0.6f, new Color(1f, 0.6f, 0.25f), 5f, 6f, 0.3f);
                SFX.Play(Sfx.Leap, 0.7f, 0.15f, at);
                Tele.Shake(at, 0.3f, 14f);
            }
            else
            {
                VFX.Burst(at + Vector3.up * 0.15f, dust, 10, 2.8f, 0.2f, 0.55f, 0.8f);
                VFX.Burst(at + Vector3.up * 0.1f, new Color(0.4f, 0.36f, 0.31f), 6, 3.5f, 0.09f, 0.6f, 2.2f);
                SFX.Play(Sfx.ArrowWood, 0.6f, 0.2f, at);
                Tele.Shake(at, 0.08f, 6f);
            }
            Destroy(gameObject);
        }
    }

    // Expanding ground ring. Damages grounded targets as it passes: jump over it.
    public class Shockwave : MonoBehaviour
    {
        float r0, r1, time, t, dmg;
        Health owner;
        Transform ring;
        Material mat;
        Color col;
        readonly HashSet<Health> hit = new HashSet<Health>();
        static UnityEngine.Mesh annulus;

        public static void Spawn(Vector3 at, float fromRadius, float toRadius, float duration, float damage, Health owner, Color col, IEnumerable<Health> alreadyHit = null)
        {
            var go = new GameObject("Shockwave");
            go.transform.position = LGeo.OnGround(at) + Vector3.up * 0.12f;
            var s = go.AddComponent<Shockwave>();
            s.r0 = fromRadius; s.r1 = toRadius; s.time = duration; s.dmg = damage; s.owner = owner; s.col = col;
            if (alreadyHit != null) foreach (var h in alreadyHit) s.hit.Add(h);
            var rg = new GameObject("Ring");
            rg.transform.SetParent(go.transform, false);
            rg.AddComponent<MeshFilter>().sharedMesh = Annulus();
            var mr = rg.AddComponent<MeshRenderer>();
            s.mat = new Material(Art.Additive(col));
            mr.sharedMaterial = s.mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            s.ring = rg.transform;
            s.ring.localScale = Vector3.one * fromRadius;
        }

        static UnityEngine.Mesh Annulus()
        {
            if (annulus) return annulus;
            const int seg = 72;
            var v = new Vector3[seg * 2]; var uv = new Vector2[seg * 2]; var tri = new int[seg * 6];
            for (int i = 0; i < seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                v[i * 2] = dir * 0.82f; v[i * 2 + 1] = dir * 1f;
                uv[i * 2] = new Vector2(0.5f, 0.5f); uv[i * 2 + 1] = new Vector2(0.5f, 0.5f);
                int n = (i + 1) % seg;
                tri[i * 6] = i * 2; tri[i * 6 + 1] = n * 2; tri[i * 6 + 2] = i * 2 + 1;
                tri[i * 6 + 3] = i * 2 + 1; tri[i * 6 + 4] = n * 2; tri[i * 6 + 5] = n * 2 + 1;
            }
            annulus = new UnityEngine.Mesh { name = "Annulus", vertices = v, uv = uv, triangles = tri };
            annulus.RecalculateNormals();
            annulus.RecalculateBounds();
            return annulus;
        }

        void Update()
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / time);
            float e = 1f - (1f - k) * (1f - k);
            float r = Mathf.Lerp(r0, r1, e);
            ring.localScale = new Vector3(r, 1f, r);
            var c = col; c.a = col.a * (1f - k);
            mat.SetColor("_BaseColor", c);
            // dust kicked up along the front
            if (Random.value < 0.7f)
            {
                float a = Random.value * Mathf.PI * 2f;
                VFX.Burst(transform.position + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * r, new Color(0.62f, 0.54f, 0.42f), 2, 1.5f, 0.3f, 0.5f, 0.2f);
            }
            if (k < 1f) Tele.Blast(transform.position, r, dmg, owner, 8f, 4f, hit, true, r - 1.3f);
            if (t > time) { Destroy(mat); Destroy(gameObject); }
        }
    }

    public class TelegraphLineFill : MonoBehaviour
    {
        Transform inner; float length, width, time, t;
        public void Init(Transform i, float len, float w, float duration) { inner = i; length = len; width = w; time = Mathf.Max(0.05f, duration); }
        void Update()
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / time);
            if (inner)
            {
                inner.localScale = new Vector3(width, 0.012f, length * k);
                inner.localPosition = new Vector3(0, 0.01f, length * k * 0.5f);
            }
            if (t > time + 0.1f) Destroy(gameObject);
        }
    }
}
