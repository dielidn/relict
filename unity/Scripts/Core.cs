using System.Collections.Generic;
using UnityEngine;

namespace Relict
{
    public enum Team { Player, Insect, Rival }

    public interface IKnockback { void Knock(Vector3 v); }

    public static class Combat
    {
        static readonly Collider[] buf = new Collider[128];
        static readonly HashSet<Health> seen = new HashSet<Health>();

        // Hits everything hostile in a cone (arc in degrees, 360 = full circle) around origin.
        public static int Melee(Vector3 origin, Vector3 fwd, float range, float arc, float dmg, Health attacker, float knock, bool crit = false)
        {
            int n = Physics.OverlapSphereNonAlloc(origin + Vector3.up, range + 0.6f, buf, ~0, QueryTriggerInteraction.Ignore);
            seen.Clear();
            int count = 0;
            fwd.y = 0;
            for (int i = 0; i < n; i++)
            {
                var h = buf[i].GetComponentInParent<Health>();
                if (h == null || h == attacker || h.Dead || seen.Contains(h)) continue;
                if (attacker != null && h.team == attacker.team) continue;
                Vector3 d = h.transform.position - origin; d.y = 0;
                float dist = d.magnitude;
                var cc = h.GetComponent<CharacterController>();
                float rad = cc ? cc.radius : 0.4f;
                if (dist - rad > range) continue;
                if (arc < 359f && dist > 0.3f && Vector3.Angle(fwd, d) > arc * 0.5f) continue;
                seen.Add(h);
                h.Damage(dmg, attacker, crit);
                count++;
                if (knock > 0f)
                {
                    var kb = h.GetComponent<IKnockback>();
                    if (kb != null) kb.Knock((dist > 0.01f ? d / dist : fwd.normalized) * knock);
                }
            }
            return count;
        }

        public static Health Nearest(Vector3 pos, float maxDist, System.Func<Health, bool> filter)
        {
            Health best = null;
            float bd = maxDist * maxDist;
            foreach (var h in Health.All)
            {
                if (h == null || h.Dead || !filter(h)) continue;
                float d = (h.transform.position - pos).sqrMagnitude;
                if (d < bd) { bd = d; best = h; }
            }
            return best;
        }
    }

    public static class Art
    {
        static readonly Dictionary<string, Material> cache = new Dictionary<string, Material>();
        static Texture2D dot;

        public static Material Lit(Color c, float smooth = 0.35f, float metal = 0f, Color emission = default)
        {
            string key = "L" + c + smooth + metal + emission;
            if (cache.TryGetValue(key, out var m) && m) return m;
            m = FromTemplate(emission.maxColorComponent > 0 ? "LitEmissive" : "Lit", "Universal Render Pipeline/Lit");
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", smooth);
            m.SetFloat("_Metallic", metal);
            if (emission.maxColorComponent > 0)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", emission);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            cache[key] = m;
            return m;
        }

        // Runtime materials are cloned from templates in Resources/RelictShaders so their shaders and
        // keyword variants ship with the build (Shader.Find returns null for stripped shaders in a player).
        static Material FromTemplate(string template, string shader)
        {
            var t = Resources.Load<Material>("RelictShaders/" + template);
            if (t) return new Material(t);
            var sh = Shader.Find(shader);
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            return new Material(sh);
        }

        public static Texture2D Dot()
        {
            if (dot) return dot;
            dot = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), new Vector2(15.5f, 15.5f)) / 15.5f;
                    float a = Mathf.Clamp01(1f - d); a *= a;
                    dot.SetPixel(x, y, new Color(1, 1, 1, a));
                }
            dot.Apply();
            return dot;
        }

        // Additive unlit material for trails, particles and beams.
        public static Material Additive(Color c)
        {
            string key = "A" + c;
            if (cache.TryGetValue(key, out var m) && m) return m;
            m = FromTemplate("Additive", "Universal Render Pipeline/Particles/Unlit");
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 2f);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            m.SetFloat("_ZWrite", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.EnableKeyword("_BLENDMODE_ADD");
            m.renderQueue = 3000;
            m.SetTexture("_BaseMap", Dot());
            m.SetColor("_BaseColor", c);
            cache[key] = m;
            return m;
        }

        public static GameObject Part(Transform parent, PrimitiveType type, Vector3 pos, Vector3 scale, Material mat, Vector3 euler = default)
        {
            var g = GameObject.CreatePrimitive(type);
            Object.Destroy(g.GetComponent<Collider>());
            g.transform.SetParent(parent, false);
            g.transform.localPosition = pos;
            g.transform.localRotation = Quaternion.Euler(euler);
            g.transform.localScale = scale;
            var r = g.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            return g;
        }
    }

    public static class VFX
    {
        public static void Burst(Vector3 pos, Color col, int count, float speed, float size = 0.14f, float life = 0.55f, float gravity = 0.6f, bool flat = false)
        {
            var go = new GameObject("Burst");
            go.transform.position = pos;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.duration = 0.2f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.5f, life);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed * 0.4f, speed);
            main.startSize = new ParticleSystem.MinMaxCurve(size * 0.5f, size);
            main.startColor = col;
            main.gravityModifier = gravity;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var em = ps.emission;
            em.rateOverTime = 0;
            em.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });
            var sh = ps.shape;
            sh.shapeType = flat ? ParticleSystemShapeType.Circle : ParticleSystemShapeType.Sphere;
            sh.radius = flat ? 0.6f : 0.1f;
            if (flat) go.transform.rotation = Quaternion.Euler(90, 0, 0);
            var col2 = ps.colorOverLifetime;
            col2.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                      new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0, 1) });
            col2.color = g;
            go.GetComponent<ParticleSystemRenderer>().sharedMaterial = Art.Additive(Color.white);
            ps.Play();
            Object.Destroy(go, life + 0.5f);
        }

        public static void Ring(Vector3 pos, Color col)
        {
            Burst(pos, col, 60, 9f, 0.35f, 0.45f, 0f, true);
            Burst(pos + Vector3.up * 0.3f, new Color(0.55f, 0.45f, 0.3f), 30, 4f, 0.4f, 0.8f, 1.2f);
        }

        public static void Slash(Vector3 origin, Vector3 fwd, Color col, float radius = 1.5f, bool flip = false)
        {
            var go = new GameObject("Slash");
            var s = go.AddComponent<SlashMover>();
            s.Init(origin + Vector3.up * 1.1f, fwd, radius, flip);
            go.transform.position = s.Point(0);
            var tr = go.AddComponent<TrailRenderer>();
            tr.time = 0.16f;
            tr.startWidth = 0.4f;
            tr.endWidth = 0f;
            tr.minVertexDistance = 0.04f;
            tr.sharedMaterial = Art.Additive(col);
            tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
    }

    public class SlashMover : MonoBehaviour
    {
        Vector3 c, f, r; float t, radius; bool flip;
        public void Init(Vector3 center, Vector3 fwd, float rad, bool fl)
        {
            c = center; fwd.y = 0; f = fwd.normalized; r = Vector3.Cross(Vector3.up, f); radius = rad; flip = fl;
        }
        public Vector3 Point(float k)
        {
            float a = Mathf.Lerp(flip ? 85f : -85f, flip ? -85f : 85f, k) * Mathf.Deg2Rad;
            return c + (f * Mathf.Cos(a) + r * Mathf.Sin(a)) * radius + Vector3.up * Mathf.Lerp(0.45f, -0.35f, k);
        }
        void Update()
        {
            t += Time.deltaTime / 0.13f;
            transform.position = Point(Mathf.Min(t, 1f));
            if (t > 3f) Destroy(gameObject);
        }
    }

    // ---------- kill juice ----------
    public static class Juice
    {
        static readonly MaterialPropertyBlock mpb = new MaterialPropertyBlock();

        // Tint renderers (white hit flash); pass null to clear.
        public static void Tint(Renderer[] rs, Color? c)
        {
            foreach (var r in rs)
            {
                if (r == null) continue;
                if (c == null) { r.SetPropertyBlock(null); continue; }
                r.GetPropertyBlock(mpb);
                mpb.SetColor("_BaseColor", c.Value);
                mpb.SetColor("_EmissionColor", c.Value * 0.6f);
                r.SetPropertyBlock(mpb);
            }
        }

        // Breaks a body built from primitives into flying, bouncing pieces.
        public static void Shatter(Transform body, Vector3 center, Vector3 push, float power, int maxPieces = 40)
        {
            var rs = body.GetComponentsInChildren<Renderer>();
            int n = 0;
            foreach (var r in rs)
            {
                if (n++ >= maxPieces) { r.enabled = false; continue; }
                var t = r.transform;
                t.SetParent(null, true);
                var g = t.gameObject.AddComponent<Gib>();
                Vector3 dir = (t.position - center); dir.y = Mathf.Abs(dir.y) + 0.3f;
                dir = dir.sqrMagnitude > 0.001f ? dir.normalized : Random.onUnitSphere;
                g.Init(dir * Random.Range(0.6f, 1.2f) * power + push + Vector3.up * Random.Range(2f, 4.5f) * (power / 5f),
                       Random.insideUnitSphere * Random.Range(360f, 900f), Random.Range(1.6f, 2.6f));
            }
        }

        public static void Splat(Vector3 pos, Color color, float size)
        {
            for (int i = 0; i < 3; i++)
            {
                var p = pos + new Vector3(Random.Range(-0.6f, 0.6f), 0, Random.Range(-0.6f, 0.6f)) * size * (i == 0 ? 0f : 1f);
                p.y = LGeo.Ground(p) + 0.03f + i * 0.004f;
                var go = new GameObject("Splat");
                go.transform.position = p;
                float sc = size * (i == 0 ? 1f : Random.Range(0.3f, 0.55f));
                Art.Part(go.transform, PrimitiveType.Cylinder, Vector3.zero, new Vector3(sc * Random.Range(1.6f, 2.2f), 0.004f, sc * Random.Range(1.3f, 1.9f)),
                    Art.Lit(color * 0.7f, 0.85f, 0f, color * 0.25f), new Vector3(0, Random.Range(0, 360f), 0)).GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                go.AddComponent<Fader>().Init(7f, 1.5f);
            }
        }

        public static void LightFlash(Vector3 pos, Color c, float intensity, float range, float time)
        {
            var l = new GameObject("Flash").AddComponent<Light>();
            l.type = LightType.Point; l.color = c; l.intensity = intensity; l.range = range;
            l.transform.position = pos;
            l.gameObject.AddComponent<LightFade>().Init(time);
        }

        // A light that stays until destroyed by the caller (used for wind-up glows).
        public static Light LightFlashHold(Vector3 pos, Color c, float range)
        {
            var l = new GameObject("Glow").AddComponent<Light>();
            l.type = LightType.Point; l.color = c; l.intensity = 0f; l.range = range; l.shadows = LightShadows.None;
            l.transform.position = pos;
            return l;
        }

        public static void XpOrbs(Vector3 from, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("XpOrb");
                go.transform.position = from + Random.insideUnitSphere * 0.4f;
                Art.Part(go.transform, PrimitiveType.Sphere, Vector3.zero, Vector3.one * 0.16f, Art.Lit(new Color(0.4f, 0.7f, 1f), 0.9f, 0f, new Color(0.6f, 1.2f, 2.4f)));
                var tr = go.AddComponent<TrailRenderer>();
                tr.time = 0.25f; tr.startWidth = 0.12f; tr.endWidth = 0f;
                tr.sharedMaterial = Art.Additive(new Color(0.5f, 0.8f, 1f));
                tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                go.AddComponent<XpOrb>().Init(Random.onUnitSphere * 4f + Vector3.up * 5f, 0.25f + i * 0.04f);
            }
        }
    }

    public class Gib : MonoBehaviour
    {
        Vector3 vel, spin, baseScale;
        float life, t;
        Renderer r;
        Color baseColor;
        static readonly MaterialPropertyBlock mpb = new MaterialPropertyBlock();

        public void Init(Vector3 v, Vector3 angular, float lifetime)
        {
            vel = v; spin = angular; life = lifetime; baseScale = transform.localScale;
            r = GetComponent<Renderer>();
            if (r && r.sharedMaterial && r.sharedMaterial.HasProperty("_BaseColor")) baseColor = r.sharedMaterial.GetColor("_BaseColor");
        }

        void Update()
        {
            float dt = Time.deltaTime;
            t += dt;
            vel += Vector3.down * 18f * dt;
            var p = transform.position + vel * dt;
            float g = LGeo.Ground(p) + 0.05f;
            if (p.y < g)
            {
                p.y = g;
                if (vel.y < 0f) vel.y = -vel.y * 0.35f;
                vel.x *= 0.6f; vel.z *= 0.6f; spin *= 0.6f;
            }
            transform.position = p;
            transform.Rotate(spin * dt, Space.World);
            if (r && t < 0.15f)
            {
                r.GetPropertyBlock(mpb);
                mpb.SetColor("_BaseColor", Color.Lerp(Color.white, baseColor, t / 0.15f));
                r.SetPropertyBlock(mpb);
            }
            else if (r && t < 0.2f) r.SetPropertyBlock(null);
            if (t > life - 0.5f) transform.localScale = baseScale * Mathf.Clamp01((life - t) / 0.5f);
            if (t > life) Destroy(gameObject);
        }
    }

    public class Fader : MonoBehaviour
    {
        float hold, fade, t; Vector3 s0;
        public void Init(float holdTime, float fadeTime) { hold = holdTime; fade = fadeTime; s0 = transform.localScale; }
        void Update()
        {
            t += Time.deltaTime;
            if (t > hold) transform.localScale = s0 * Mathf.Clamp01(1f - (t - hold) / fade);
            if (t > hold + fade) Destroy(gameObject);
        }
    }

    public class LightFade : MonoBehaviour
    {
        Light l; float time, t, i0;
        public void Init(float duration) { l = GetComponent<Light>(); time = duration; i0 = l.intensity; }
        void Update()
        {
            t += Time.deltaTime;
            l.intensity = i0 * Mathf.Clamp01(1f - t / time);
            if (t > time) Destroy(gameObject);
        }
    }

    public class XpOrb : MonoBehaviour
    {
        Vector3 vel; float delay, t;
        public void Init(Vector3 v, float wait) { vel = v; delay = wait; }
        void Update()
        {
            float dt = Time.deltaTime;
            t += dt;
            var mm = MatchManager.I;
            if (mm == null || mm.Hero == null) { Destroy(gameObject); return; }
            Vector3 target = mm.Hero.transform.position + Vector3.up * 1.1f;
            if (t < delay) vel = Vector3.Lerp(vel, Vector3.zero, dt * 3f);
            else
            {
                Vector3 d = target - transform.position;
                vel = Vector3.Lerp(vel, d.normalized * (12f + t * 20f), dt * 6f);
                if (d.magnitude < 0.5f)
                {
                    VFX.Burst(target, new Color(0.5f, 0.8f, 1f), 4, 1.5f, 0.06f, 0.25f, 0f);
                    SFX.Play(Sfx.Coin, 0.25f, 0.15f);
                    Destroy(gameObject);
                    return;
                }
            }
            transform.position += vel * dt;
            if (t > 4f) Destroy(gameObject);
        }
    }
}
