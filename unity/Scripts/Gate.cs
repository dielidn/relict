using System.Collections;
using UnityEngine;

namespace Relict
{
    // A wall of glowing vines across a corridor. Blocks the way until its zone is cleared.
    public class Gate : MonoBehaviour
    {
        public int arm, index;
        public float width = 30f;
        public GameObject vinePrefab;
        public bool IsOpen { get; private set; }

        Transform visual;
        BoxCollider col;
        Light glow;

        void Awake()
        {
            col = gameObject.GetComponent<BoxCollider>();
            if (col == null) col = gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(width, 12f, 1.6f);
            col.center = new Vector3(0, 5f, 0);

            visual = new GameObject("Visual").transform;
            visual.SetParent(transform, false);
            var bark = Art.Lit(new Color(0.28f, 0.2f, 0.12f), 0.15f);
            Art.Part(visual, PrimitiveType.Cylinder, new Vector3(0, 7.2f, 0), new Vector3(0.7f, width * 0.5f, 0.7f), bark, new Vector3(0, 0, 90));
            // energy curtain
            var curtain = Art.Part(visual, PrimitiveType.Cube, new Vector3(0, 3.6f, 0), new Vector3(width, 7.2f, 0.05f), Art.Additive(new Color(0.35f, 1f, 0.45f, 0.18f)));
            curtain.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            // hanging vines
            if (vinePrefab)
            {
                int n = Mathf.RoundToInt(width / 2.2f);
                for (int i = 0; i < n; i++)
                {
                    var v = Instantiate(vinePrefab, visual);
                    float x = Mathf.Lerp(-width * 0.5f + 1f, width * 0.5f - 1f, i / (float)(n - 1)) + Random.Range(-0.4f, 0.4f);
                    v.transform.localPosition = new Vector3(x, 7f, Random.Range(-0.3f, 0.3f));
                    v.transform.localRotation = Quaternion.Euler(0, Random.Range(0, 360f), 0) * v.transform.localRotation;
                    v.transform.localScale = v.transform.localScale * Random.Range(2.6f, 3.4f);
                    foreach (var c in v.GetComponentsInChildren<Collider>()) Destroy(c);
                }
            }
            // glowing knots
            var knot = Art.Lit(new Color(0.3f, 0.9f, 0.4f), 0.6f, 0f, new Color(0.4f, 1.6f, 0.5f));
            for (int i = 0; i < 6; i++)
                Art.Part(visual, PrimitiveType.Sphere, new Vector3(Mathf.Lerp(-width * 0.4f, width * 0.4f, i / 5f), 7.2f, 0.4f), Vector3.one * 0.5f, knot);
            glow = new GameObject("GateGlow").AddComponent<Light>();
            glow.type = LightType.Point; glow.color = new Color(0.45f, 1f, 0.5f); glow.intensity = 2.5f; glow.range = 12f;
            glow.transform.SetParent(visual, false);
            glow.transform.localPosition = new Vector3(0, 4f, 0);
        }

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = true;
            col.enabled = false;
            StartCoroutine(Fall());
        }

        IEnumerator Fall()
        {
            SFX.Play(Sfx.Leap, 0.9f, 0f, transform.position);
            SFX.Play(Sfx.Artifact, 0.6f, 0f, transform.position);
            for (int i = 0; i < 8; i++)
                VFX.Burst(transform.position + transform.right * Mathf.Lerp(-width * 0.45f, width * 0.45f, i / 7f) + Vector3.up * 3f, new Color(0.45f, 1f, 0.5f), 25, 5f, 0.25f, 0.9f, 0.3f);
            float t = 0f;
            Vector3 p0 = visual.localPosition;
            while (t < 1.4f)
            {
                t += Time.deltaTime;
                float k = t / 1.4f;
                visual.localPosition = p0 + Vector3.up * (k * k * 9f);
                visual.localScale = new Vector3(1f, 1f - k * 0.9f, 1f);
                if (glow) glow.intensity = 2.5f * (1f - k) + 6f * Mathf.Sin(k * Mathf.PI);
                yield return null;
            }
            visual.gameObject.SetActive(false);
        }
    }
}
