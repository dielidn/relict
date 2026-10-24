using UnityEngine;

namespace Relict
{
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
