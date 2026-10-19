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
            public int lodNear = 2;                     // mesh LOD used right next to the camera
            public float lodStep = 12f;                 // metres per extra LOD level
            public float shadowDistance = 30f;
        }

        // Quality knobs (set from GameSettings).
        public static float Density = 1f, DistanceMul = 1f;
        public static FoliageScatter I;
        static readonly float[] TierReach = { 1f, 0.7f, 0.45f };

        public Layer[] layers;
        public int seed = 77;
        public float cellSize = 24f;

        class Part { public Mesh mesh; public Material[] mats; public Matrix4x4 local; }
        class Batch
        {
            public Mesh mesh; public int sub; public Material mat; public Layer layer;
            public List<Matrix4x4[]>[] tiers = { new List<Matrix4x4[]>(), new List<Matrix4x4[]>(), new List<Matrix4x4[]>() };
        }
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

        void Awake() { I = this; }
        void Start() { GameSettings.Load(); Generate(); }

        public void Generate()
        {
            cells.Clear();
            TotalInstances = 0;
            var rng = new System.Random(seed);
            float R() => (float)rng.NextDouble();
            var temp = new Dictionary<(Vector2Int, Mesh, int, Material, int), List<Matrix4x4>>();
            var batchLayer = new Dictionary<(Vector2Int, Mesh, int, Material, int), Layer>();

            foreach (var layer in layers)
            {
                if (layer.prefabs == null || layer.prefabs.Length == 0) continue;
                var partsPer = new List<List<Part>>();
                foreach (var p in layer.prefabs) if (p) partsPer.Add(Parts(p));
                if (partsPer.Count == 0) continue;

                int target = Mathf.RoundToInt(layer.count * Mathf.Clamp(Density, 0.1f, 1.5f));
                int placed = 0, tries = 0;
                while (placed < target && tries < target * 12)
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
                    float tr = R();
                    int tier = tr < 0.4f ? 0 : tr < 0.7f ? 1 : 2;
                    foreach (var part in parts)
                        for (int sub = 0; sub < part.mesh.subMeshCount && sub < part.mats.Length; sub++)
                        {
                            var key = (key2, part.mesh, sub, part.mats[sub], tier);
                            if (!temp.TryGetValue(key, out var l)) { l = new List<Matrix4x4>(); temp[key] = l; batchLayer[key] = layer; }
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
                var layer = batchLayer[kv.Key];
                var b = cell.batches.Find(x => x.mesh == kv.Key.Item2 && x.sub == kv.Key.Item3 && x.mat == kv.Key.Item4 && x.layer == layer);
                if (b == null) { b = new Batch { mesh = kv.Key.Item2, sub = kv.Key.Item3, mat = kv.Key.Item4, layer = layer }; cell.batches.Add(b); }
                var all = kv.Value;
                for (int i = 0; i < all.Count; i += 1000)
                    b.tiers[kv.Key.Item5].Add(all.GetRange(i, Mathf.Min(1000, all.Count - i)).ToArray());
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
                    var L = b.layer;
                    float reach = L.drawDistance * DistanceMul;
                    if (d > reach || b.mat == null) continue;
                    int lods = Mathf.Max(1, b.mesh.lodCount);
                    int lod = Mathf.Min(lods - 1, L.lodNear + Mathf.FloorToInt(d / Mathf.Max(1f, L.lodStep)));
                    var rp = new RenderParams(b.mat)
                    {
                        shadowCastingMode = L.shadows && d < L.shadowDistance ? ShadowCastingMode.On : ShadowCastingMode.Off,
                        receiveShadows = true,
                        worldBounds = cell.bounds,
                        layer = gameObject.layer,
                        forceMeshLod = lods > 1 ? lod : -1
                    };
                    for (int t = 0; t < 3; t++)
                    {
                        if (d > reach * TierReach[t]) continue;
                        foreach (var chunk in b.tiers[t]) Graphics.RenderMeshInstanced(rp, b.mesh, b.sub, chunk);
                    }
                }
            }
        }
    }
}
