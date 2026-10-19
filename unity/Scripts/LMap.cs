using System.Collections;
using UnityEngine;

namespace Relict
{
    // Geometry of the L-shaped map. Arm 0 (player) runs along -Z, arm 1 (rival) along -X;
    // both meet at the boss arena around the world origin.
    public static class LGeo
    {
        public const float ArmLength = 160f;      // centre → closed far end
        public const float SpawnDist = 148f;
        public const float ArenaRadius = 34f;
        public const float HalfWidth = 13f;
        // gate positions (distance from centre); zone 0 = [160..115], 1 = [115..76], 2 = [76..40], arena < 40
        public static readonly float[] GateDist = { 115f, 76f, 40f };
        public static readonly float[] ZoneMid = { 132f, 95f, 56f };

        // Corridor half-width: wider clearings at zone centres, choke points at gates.
        public static float HalfWidthAt(float dist)
        {
            float w = HalfWidth + 3f * (Mathf.PerlinNoise(dist * 0.08f, 3.1f) - 0.5f);
            foreach (float m in ZoneMid) w += 5f * Mathf.Exp(-(dist - m) * (dist - m) / 128f);
            foreach (float g in GateDist) w -= 3.5f * Mathf.Exp(-(dist - g) * (dist - g) / 18f);
            return w;
        }

        public static Vector3 ArmPoint(int arm, float dist)
        {
            float k = Mathf.Clamp01((dist - ArenaRadius) / 25f);
            float wiggle = 7f * Mathf.Sin(dist * 0.045f + (arm == 0 ? 0f : 1.7f)) * k;
            return arm == 0 ? new Vector3(wiggle, 0, -dist) : new Vector3(-dist, 0, wiggle);
        }

        // Direction along the arm towards the arena.
        public static Vector3 ArmForward(int arm, float dist)
        {
            Vector3 a = ArmPoint(arm, dist), b = ArmPoint(arm, Mathf.Max(0f, dist - 2f));
            Vector3 d = b - a; d.y = 0;
            return d.sqrMagnitude > 0.0001f ? d.normalized : (arm == 0 ? Vector3.forward : Vector3.right);
        }

        public static Vector3 ArmRight(int arm, float dist) => Vector3.Cross(Vector3.up, ArmForward(arm, dist));

        // Which arm a world position belongs to (-1 = arena) and its distance from the centre.
        public static int Locate(Vector3 p, out float dist)
        {
            float d0 = -p.z, d1 = -p.x;
            dist = Mathf.Max(d0, d1);
            if (new Vector2(p.x, p.z).magnitude < ArenaRadius + 2f) return -1;
            return d0 >= d1 ? 0 : 1;
        }

        public static int ZoneAt(float dist)
        {
            if (dist > GateDist[0]) return 0;
            if (dist > GateDist[1]) return 1;
            if (dist > GateDist[2]) return 2;
            return 3;
        }

        public static float Ground(Vector3 p)
        {
            var t = Terrain.activeTerrain;
            return t ? t.SampleHeight(p) + t.transform.position.y : 0f;
        }

        public static Vector3 OnGround(Vector3 p) { p.y = Ground(p) + 0.05f; return p; }
    }

    public class LMap : MonoBehaviour
    {
        public static LMap I;
        public Gate[] gatesA = new Gate[3];   // player arm
        public Gate[] gatesB = new Gate[3];   // rival arm

        void Awake() { I = this; }

        public Gate GateOf(int arm, int index) => arm == 0 ? gatesA[index] : gatesB[index];
    }
}
