using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Relict
{
    // Dense GPU-instanced vegetation for the L map: tens of thousands of plants with no GameObjects.
    // Instances are generated deterministically at start, grouped into cells and culled by distance and frustum.
    public class FoliageScatter : MonoBehaviour
    {
        [System.Serializable]
        public class Layer
        {
            public string name;
            public GameObject[] prefabs;
            public int count = 1000;
            public float minE = -14f, maxE = 0f;        // signed distance to the walkable edge (<0 inside the corridor)
            public float edgeBias;                      // >0 prefers spots near the walls
            public float minPath = 1.2f;                // keep this far from the trodden path
            public Vector2 scale = new Vector2(1f, 1.6f);
            public float flatten = 1f;                  // vertical squash (stones)
            public float drawDistance = 70f;
            public bool shadows;
            public bool avoidPonds = true;
            public bool onPath;                         // stepping stones along the path
        }

        public Layer[] layers;
        public int seed = 77;
        public float cellSize = 24f;

        class Part { public Mesh mesh; public Material[] mats; public Matrix4x4 local; }
        class Batch { public Mesh mesh; public int sub; public Material mat; public bool shadows; public float dist; public List<Matrix4x4[]> chunks = new List<Matrix4x4[]>(); }
        class Cell { public Bounds bounds; public List<Batch> batches = new List<Batch>(); }

        readonly Dictionary<Vector2Int, Cell> cells = new Dictionary<Vector2Int, Cell>();
        readonly Plane[] planes = new Plane[6];
        public int TotalInstances { get; private set; }

        static List<Part> Parts(GameObject prefab)
        {
            var list = new List<Part>();
            var root = prefab.transform;
            var toRoot = Matrix4x4.TRS(Vector3.zero, root.rotation, root.lossyScale) * root.worldToLocalMatrix;
            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>())
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mf.sharedMesh == null || mr == null) continue;
                var mats = mr.sharedMaterials;
                foreach (var m in mats) if (m && !m.enableInstancing) m.enableInstancing = true;
                list.Add(new Part { mesh = mf.sharedMesh, mats = mats, local = toRoot * mf.transform.localToWorldMatrix });
            }
            return list;
        }

        void Start() { Generate(); }

        public void Generate()
        {
            cells.Clear();
            TotalInstances = 0;
            var rng = new System.Random(seed);
            float R() => (float)rng.NextDouble();
            var temp = new Dictionary<(Vector2Int, Mesh, int, Material), List<Matrix4x4>>();
            var batchInfo = new Dictionary<(Vector2Int, Mesh, int, Material), (bool, float)>();

            foreach (var layer in layers)
            {
                if (layer.prefabs == null || layer.prefabs.Length == 0) continue;
                var partsPer = new List<List<Part>>();
                foreach (var p in layer.prefabs) if (p) partsPer.Add(Parts(p));
                if (partsPer.Count == 0) continue;

                int placed = 0, tries = 0;
                while (placed < layer.count && tries < layer.count * 12)
                {
                    tries++;
                    Vector3 pos;
                    if (layer.onPath)
                    {
                        // stepping stones: along the centreline of an arm or around the arena ring
                        int arm = rng.Next(3);
                        if (arm < 2)
                        {
                            float t = Mathf.Lerp(LGeo.ArenaRadius - 2f, LGeo.ArmLength - 4f, R());
                            pos = LGeo.ArmPoint(arm, t) + LGeo.ArmRight(arm, t) * ((R() - 0.5f) * 2.6f);
                        }
                        else
                        {
                            float a = R() * Mathf.PI * 2f, rr = 14f + (R() - 0.5f) * 2.4f;
                            pos = new Vector3(Mathf.Cos(a) * rr, 0, Mathf.Sin(a) * rr);
                        }
                        if (LGeo.PondDepth(pos.x, pos.z) > 0.05f) continue;
                    }
                    else
                    {
                        pos = new Vector3(Mathf.Lerp(-178f, 50f, R()), 0, Mathf.Lerp(-178f, 50f, R()));
                        float e = LGeo.Outside(pos.x, pos.z, out float pd);
                        if (e < layer.minE || e > layer.maxE) continue;
                        if (pd < layer.minPath) continue;
                        if (layer.avoidPonds && LGeo.PondDepth(pos.x, pos.z) > 0.2f) continue;
                        if (layer.edgeBias > 0f)
                        {
                            float k = Mathf.Clamp01((e - layer.minE) / Mathf.Max(0.01f, layer.maxE - layer.minE));
                            if (R() > Mathf.Lerp(1f - layer.edgeBias, 1f, k)) continue;
                        }
                    }
                    pos.y = LGeo.Ground(pos) - 0.03f;
                    float sc = Mathf.Lerp(layer.scale.x, layer.scale.y, R() * R());
                    var trs = Matrix4x4.TRS(pos, Quaternion.Euler(0, R() * 360f, 0), new Vector3(sc, sc * layer.flatten, sc));
                    var parts = partsPer[rng.Next(partsPer.Count)];
                    var key2 = new Vector2Int(Mathf.FloorToInt(pos.x / cellSize), Mathf.FloorToInt(pos.z / cellSize));
                    foreach (var part in parts)
                        for (int sub = 0; sub < part.mesh.subMeshCount && sub < part.mats.Length; sub++)
                        {
                            var key = (key2, part.mesh, sub, part.mats[sub]);
                            if (!temp.TryGetValue(key, out var l)) { l = new List<Matrix4x4>(); temp[key] = l; batchInfo[key] = (layer.shadows, layer.drawDistance); }
                            l.Add(trs * part.local);
                        }
                    placed++;
                }
                TotalInstances += placed;
            }

            foreach (var kv in temp)
            {
                var ck = kv.Key.Item1;
                if (!cells.TryGetValue(ck, out var cell))
                {
                    cell = new Cell();
                    var c = new Vector3((ck.x + 0.5f) * cellSize, 0, (ck.y + 0.5f) * cellSize);
                    c.y = LGeo.Ground(c);
                    cell.bounds = new Bounds(c, new Vector3(cellSize + 8f, 60f, cellSize + 8f));
                    cells[ck] = cell;
                }
                var info = batchInfo[kv.Key];
                var b = new Batch { mesh = kv.Key.Item2, sub = kv.Key.Item3, mat = kv.Key.Item4, shadows = info.Item1, dist = info.Item2 };
                var all = kv.Value;
                for (int i = 0; i < all.Count; i += 1000)
                    b.chunks.Add(all.GetRange(i, Mathf.Min(1000, all.Count - i)).ToArray());
                cell.batches.Add(b);
            }
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            GeometryUtility.CalculateFrustumPlanes(cam, planes);
            Vector3 cp = cam.transform.position;
            foreach (var cell in cells.Values)
            {
                if (!GeometryUtility.TestPlanesAABB(planes, cell.bounds)) continue;
                float d = Mathf.Sqrt(cell.bounds.SqrDistance(cp));
                foreach (var b in cell.batches)
                {
                    if (d > b.dist || b.mat == null) continue;
                    var rp = new RenderParams(b.mat)
                    {
                        shadowCastingMode = b.shadows && d < 45f ? ShadowCastingMode.On : ShadowCastingMode.Off,
                        receiveShadows = true,
                        worldBounds = cell.bounds,
                        layer = gameObject.layer
                    };
                    foreach (var chunk in b.chunks) Graphics.RenderMeshInstanced(rp, b.mesh, b.sub, chunk);
                }
            }
        }
    }
}
