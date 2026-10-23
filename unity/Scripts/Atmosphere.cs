using System.Collections.Generic;
using UnityEngine;

namespace Relict
{
    // Morning mist: soft vertical billboards recycled in a ring around the camera.
    // They fade out up close (no hard intersections at your feet) and far away (fog takes over).
    public class MistField : MonoBehaviour
    {
        public Material material;
        public int count = 36;
        public float innerRadius = 7f, outerRadius = 52f;
        public Vector2 width = new Vector2(12f, 22f), height = new Vector2(3.5f, 6.5f);
        public float alpha = 0.32f;
        public Vector3 wind = new Vector3(0.35f, 0f, 0.2f);

        public static float Density = 1f;   // set from quality settings

        class Puff { public Transform t; public Renderer r; public float a, w, h, phase; }
        readonly List<Puff> puffs = new List<Puff>();
        MaterialPropertyBlock mpb;
        Color baseColor;
        UnityEngine.Mesh quad;
        int activeCount;

        void Start()
        {
            if (material == null) { enabled = false; return; }
            mpb = new MaterialPropertyBlock();
            baseColor = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : Color.white;
            quad = BuildQuad();
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("Mist");
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = quad;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = material;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                var p = new Puff { t = go.transform, r = r, phase = Random.value * 10f };
                puffs.Add(p);
                Respawn(p, true);
            }
        }

        static UnityEngine.Mesh BuildQuad()
        {
            var m = new UnityEngine.Mesh { name = "MistQuad" };
            m.vertices = new[] { new Vector3(-0.5f, 0, 0), new Vector3(0.5f, 0, 0), new Vector3(-0.5f, 1, 0), new Vector3(0.5f, 1, 0) };
            m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            m.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            m.RecalculateNormals();
            m.bounds = new Bounds(new Vector3(0, 0.5f, 0), new Vector3(1, 1, 1));
            return m;
        }

        void Respawn(Puff p, bool anywhere)
        {
            var cam = Camera.main;
            Vector3 c = cam ? cam.transform.position : Vector3.zero;
            Vector3 f = cam ? cam.transform.forward : Vector3.forward; f.y = 0; f.Normalize();
            float ang = anywhere ? Random.value * 360f : Random.Range(-75f, 75f);   // prefer ahead of the camera
            float dist = anywhere ? Random.Range(innerRadius + 3f, outerRadius) : Random.Range(outerRadius * 0.6f, outerRadius);
            Vector3 pos = c + Quaternion.Euler(0, ang, 0) * f * dist;
            p.w = Random.Range(width.x, width.y);
            p.h = Random.Range(height.x, height.y);
            pos.y = LGeo.Ground(pos) - 0.4f;
            p.t.position = pos;
            p.t.localScale = new Vector3(p.w, p.h, 1f);
            p.a = 0f;
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null || puffs.Count == 0) return;
            activeCount = Mathf.Clamp(Mathf.RoundToInt(count * Density), 0, puffs.Count);
            Vector3 c = cam.transform.position;
            float dt = Time.deltaTime;
            for (int i = 0; i < puffs.Count; i++)
            {
                var p = puffs[i];
                bool active = i < activeCount;
                if (p.r.enabled != active) p.r.enabled = active;
                if (!active) continue;
                p.t.position += wind * dt;
                Vector3 d = p.t.position - c; d.y = 0;
                float dist = d.magnitude;
                if (dist > outerRadius + 6f || dist < innerRadius * 0.5f) { Respawn(p, false); continue; }
                // face the camera around the vertical axis
                if (dist > 0.01f) p.t.rotation = Quaternion.LookRotation(d / dist, Vector3.up);
                float near = Mathf.Clamp01((dist - innerRadius) / 8f);
                float far = Mathf.Clamp01((outerRadius + 6f - dist) / 12f);
                p.a = Mathf.MoveTowards(p.a, 1f, dt * 0.5f);   // fade in after respawn
                float breathe = 0.85f + 0.15f * Mathf.Sin(Time.time * 0.3f + p.phase);
                var col = baseColor; col.a = alpha * near * far * p.a * breathe;
                mpb.SetColor("_BaseColor", col);
                p.r.SetPropertyBlock(mpb);
            }
        }
    }

    // A soft shaft of sunlight through the canopy: turns around its long axis to face the camera,
    // fades when the camera is close or looking along it.
    public class Sunbeam : MonoBehaviour
    {
        public float alpha = 0.07f;
        Renderer r;
        MaterialPropertyBlock mpb;
        Color baseColor;
        float phase;

        void Start()
        {
            r = GetComponentInChildren<Renderer>();
            mpb = new MaterialPropertyBlock();
            baseColor = r.sharedMaterial.HasProperty("_BaseColor") ? r.sharedMaterial.GetColor("_BaseColor") : Color.white;
            phase = Random.value * 10f;
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null || r == null) return;
            Vector3 axis = transform.up;
            Vector3 toCam = cam.transform.position - transform.position;
            Vector3 n = Vector3.ProjectOnPlane(toCam, axis);
            if (n.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(-n.normalized, axis);
            float dist = toCam.magnitude;
            float k = Mathf.Clamp01((dist - 5f) / 10f) * Mathf.Clamp01((90f - dist) / 30f);
            float flicker = 0.8f + 0.2f * Mathf.Sin(Time.time * 0.5f + phase) * Mathf.Sin(Time.time * 0.23f + phase * 2f);
            var c = baseColor; c.a = alpha * k * flicker;
            mpb.SetColor("_BaseColor", c);
            r.SetPropertyBlock(mpb);
            r.enabled = c.a > 0.002f;
        }
    }
}
