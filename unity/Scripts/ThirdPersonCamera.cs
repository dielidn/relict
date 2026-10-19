using UnityEngine;
using UnityEngine.InputSystem;

namespace Relict
{
    public class ThirdPersonCamera : MonoBehaviour
    {
        public Transform target;
        public float distance = 4.3f, height = 1.55f, sensitivity = 0.11f;
        public Vector3 shoulder = new Vector3(0.55f, 0.1f, 0f);
        public float aimDistance = 2.6f;
        public bool aiming;
        [Range(0, 1)] public float charge;   // bow draw amount, narrows the view slightly

        float kick, zoom = 1f;
        Camera cam;

        float yaw, pitch = 12f, shake, curDist;
        Vector3 idlePos; Quaternion idleRot; bool idleSet;
        readonly RaycastHit[] hits = new RaycastHit[32];

        public void SetTarget(Transform t)
        {
            target = t;
            yaw = t.eulerAngles.y;
            pitch = 12f;
            curDist = distance;
        }

        public void Shake(float amount) { shake = Mathf.Max(shake, amount); }

        // Recoil: pushes the view up a little, recovers on its own.
        public void Kick(float degrees) { kick += degrees; }

        void LateUpdate()
        {
            float dt = Time.unscaledDeltaTime;
            if (target == null)
            {
                if (!idleSet) { idlePos = transform.position; idleRot = transform.rotation; idleSet = true; }
                float t = Time.unscaledTime * 0.25f;
                transform.position = idlePos + new Vector3(Mathf.Sin(t) * 0.25f, Mathf.Sin(t * 1.3f) * 0.08f, 0);
                transform.rotation = idleRot * Quaternion.Euler(Mathf.Sin(t * 0.7f) * 0.6f, Mathf.Sin(t) * 1.5f, 0);
                return;
            }

            if (Cursor.lockState == CursorLockMode.Locked && Mouse.current != null && Time.timeScale > 0f)
            {
                Vector2 d = Mouse.current.delta.ReadValue();
                float sens = GameSettings.Sensitivity;
                yaw += d.x * sens;
                pitch = Mathf.Clamp(pitch - d.y * sens * (GameSettings.InvertY ? -1f : 1f), -35f, 70f);
            }

            kick = Mathf.Lerp(kick, 0f, 1f - Mathf.Exp(-12f * dt));
            if (cam == null) cam = GetComponent<Camera>();
            float wantZoom = (aiming ? 0.72f : 1f) * (1f - 0.08f * charge);
            zoom = Mathf.Lerp(zoom, wantZoom, 1f - Mathf.Exp(-10f * dt));
            if (cam) cam.fieldOfView = GameSettings.Fov * zoom;

            Quaternion rot = Quaternion.Euler(pitch - kick, yaw, 0);
            Vector3 pivot = target.position + Vector3.up * height;
            float want = aiming ? aimDistance : distance;
            Vector3 offset = rot * (shoulder + Vector3.back * want);
            Vector3 dir = offset.normalized;
            float maxD = offset.magnitude;

            int n = Physics.SphereCastNonAlloc(pivot, 0.22f, dir, hits, maxD, ~0, QueryTriggerInteraction.Ignore);
            float allowed = maxD;
            for (int i = 0; i < n; i++)
            {
                var c = hits[i].collider;
                if (c.GetComponentInParent<Health>() != null) continue;
                if (hits[i].distance <= 0f) continue;
                allowed = Mathf.Min(allowed, hits[i].distance - 0.05f);
            }
            allowed = Mathf.Max(allowed, 0.4f);
            curDist = allowed < curDist ? allowed : Mathf.Lerp(curDist, allowed, 1f - Mathf.Exp(-6f * dt));

            Vector3 pos = pivot + dir * curDist;
            if (shake > 0f)
            {
                pos += Random.insideUnitSphere * shake * 0.15f;
                shake = Mathf.MoveTowards(shake, 0f, dt * 1.8f);
            }
            transform.SetPositionAndRotation(pos, rot);
        }
    }
}
