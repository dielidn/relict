#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Relict;

// Builds the L-shaped map: terrain with corridors and ridges, vegetation, gates and spawn points.
// Menu: Tools > Relict > Build L Map (run with the Prototype scene open).
public static class LMapBuilder
{
    const string Env = "Assets/RelictEnv/Prefabs/";
    const string Out = "Assets/_Relict/Art/Terrain";
    const float TerrainSize = 260f, TerrainHeight = 40f, OriginY = -4f;
    static readonly Vector3 TerrainPos = new Vector3(-200f, OriginY, -200f);

    // ---------- shape ----------
    static float ArmWiggle(int arm, float t) => arm == 0 ? LGeo.ArmPoint(0, t).x : LGeo.ArmPoint(1, t).z;

    public static float Outside(float x, float z, out float pathDist) => LGeo.Outside(x, z, out pathDist);

    static float HeightAt(float x, float z)
    {
        float e = Outside(x, z, out _);
        float floor = 0.5f * (Mathf.PerlinNoise(x * 0.06f + 10f, z * 0.06f + 10f) - 0.5f) + 0.25f * (Mathf.PerlinNoise(x * 0.2f, z * 0.2f) - 0.5f);
        floor -= 0.7f * LGeo.PondDepth(x, z);
        if (e <= 0f) return floor;
        float ridge = 15f + 9f * Mathf.PerlinNoise(x * 0.025f + 40f, z * 0.025f + 40f);
        float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(e / 9f));
        float roll = 5f * Mathf.PerlinNoise(x * 0.05f + 70f, z * 0.05f + 70f) * Mathf.Clamp01((e - 6f) / 10f);
        return floor + ridge * k + roll;
    }

    // ---------- textures ----------
    static float Tile(float x, float y, float size, float scale, float seed)
    {
        float a = Mathf.PerlinNoise(x * scale + seed, y * scale + seed);
        float b = Mathf.PerlinNoise((x - size) * scale + seed, y * scale + seed);
        float c = Mathf.PerlinNoise(x * scale + seed, (y - size) * scale + seed);
        float d = Mathf.PerlinNoise((x - size) * scale + seed, (y - size) * scale + seed);
        float wx = x / size, wy = y / size;
        return Mathf.Lerp(Mathf.Lerp(a, b, wx), Mathf.Lerp(c, d, wx), wy);
    }

    static Texture2D MakeTex(string name, System.Func<int, int, Color> f)
    {
        const int N = 512;
        var t = new Texture2D(N, N, TextureFormat.RGB24, true);
        var px = new Color[N * N];
        for (int y = 0; y < N; y++) for (int x = 0; x < N; x++) px[y * N + x] = f(x, y);
        t.SetPixels(px); t.Apply();
        string path = Out + "/" + name + ".png";
        File.WriteAllBytes(path, t.EncodeToPNG());
        Object.DestroyImmediate(t);
        AssetDatabase.ImportAsset(path);
        var imp = (TextureImporter)AssetImporter.GetAtPath(path);
        imp.wrapMode = TextureWrapMode.Repeat; imp.mipmapEnabled = true; imp.anisoLevel = 4;
        imp.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    static TerrainLayer Layer(string name, Texture2D tex, float tile)
    {
        string path = Out + "/" + name + ".terrainlayer";
        var l = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
        if (l == null) { l = new TerrainLayer(); AssetDatabase.CreateAsset(l, path); }
        l.diffuseTexture = tex; l.tileSize = new Vector2(tile, tile); l.smoothness = 0.05f; l.metallic = 0f;
        EditorUtility.SetDirty(l);
        return l;
    }

    // ---------- placement helpers ----------
    static readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();
    static GameObject P(string n)
    {
        if (!prefabs.TryGetValue(n, out var p)) { p = AssetDatabase.LoadAssetAtPath<GameObject>(Env + n + ".prefab"); prefabs[n] = p; }
        return p;
    }

    static GameObject Place(string name, float x, float z, float yaw, float scale, Transform parent, float sink = 0.05f)
    {
        var p = P(name);
        if (p == null) return null;
        var go = (GameObject)PrefabUtility.InstantiatePrefab(p, parent);
        var pos = new Vector3(x, 0, z);
        pos.y = LGeo.Ground(pos) - sink * scale;
        go.transform.position = pos;
        go.transform.rotation = Quaternion.Euler(0, yaw, 0) * go.transform.rotation;
        go.transform.localScale = go.transform.localScale * scale;
        go.isStatic = true;
        return go;
    }

    static void TrunkCollider(GameObject palm, float radius)
    {
        var col = new GameObject("TrunkCollider").transform;
        col.SetParent(palm.transform, false);
        col.position = palm.transform.position + Vector3.up * 2.5f;
        col.rotation = Quaternion.identity;
        var ls = palm.transform.localScale;
        col.localScale = new Vector3(1f / ls.x, 1f / ls.y, 1f / ls.z);
        var cap = col.gameObject.AddComponent<CapsuleCollider>();
        cap.radius = radius; cap.height = 5f; cap.direction = 1;
        col.gameObject.isStatic = true;
    }

    static void MeshColliders(GameObject go)
    {
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
            if (!mf.GetComponent<Collider>()) mf.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
    }

    static string Pick(string[] arr) => arr[Random.Range(0, arr.Length)];

    [MenuItem("Tools/Relict/Build L Map")]
    public static void Build()
    {
        Random.InitState(1234);
        if (!AssetDatabase.IsValidFolder(Out)) AssetDatabase.CreateFolder("Assets/_Relict/Art", "Terrain");
        var scene = EditorSceneManager.GetActiveScene();

        foreach (var n in new[] { "Ground", "Environment", "MapBoundary", "Places", "LMap", "Terrain_L", "Map" })
        {
            var old = GameObject.Find(n);
            if (old) Object.DestroyImmediate(old);
        }

        // enable GPU instancing on the kit materials
        foreach (var g in AssetDatabase.FindAssets("t:Material", new[] { "Assets/RelictEnv/Materials" }))
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g));
            if (m && !m.enableInstancing) { m.enableInstancing = true; EditorUtility.SetDirty(m); }
        }

        // ---------- textures and layers ----------
        var grassT = MakeTex("T_Grass", (x, y) =>
        {
            float n = Tile(x, y, 512, 0.02f, 1f), f = Tile(x, y, 512, 0.15f, 7f), sp = Tile(x, y, 512, 0.5f, 3f);
            var c = Color.Lerp(new Color(0.2f, 0.32f, 0.1f), new Color(0.34f, 0.47f, 0.15f), n);
            c = Color.Lerp(c, new Color(0.42f, 0.52f, 0.2f), Mathf.Clamp01((f - 0.55f) * 3f));
            return c * (0.85f + 0.3f * sp);
        });
        var litterT = MakeTex("T_Litter", (x, y) =>
        {
            float n = Tile(x, y, 512, 0.03f, 11f), f = Tile(x, y, 512, 0.22f, 5f), sp = Tile(x, y, 512, 0.6f, 9f);
            var c = Color.Lerp(new Color(0.18f, 0.16f, 0.08f), new Color(0.3f, 0.28f, 0.12f), n);
            c = Color.Lerp(c, new Color(0.24f, 0.34f, 0.12f), Mathf.Clamp01((f - 0.5f) * 2.5f));
            return c * (0.8f + 0.35f * sp);
        });
        var dirtT = MakeTex("T_Dirt", (x, y) =>
        {
            float n = Tile(x, y, 512, 0.025f, 21f), f = Tile(x, y, 512, 0.3f, 13f);
            var c = Color.Lerp(new Color(0.36f, 0.27f, 0.17f), new Color(0.5f, 0.4f, 0.26f), n);
            if (f > 0.72f) c = Color.Lerp(c, new Color(0.55f, 0.52f, 0.46f), (f - 0.72f) * 3f);
            return c * (0.9f + 0.2f * Tile(x, y, 512, 0.8f, 2f));
        });
        var rockT = MakeTex("T_Rock", (x, y) =>
        {
            float n = Tile(x, y, 512, 0.012f, 31f), st = Tile(x * 0.35f, y * 2.2f, 512, 0.05f, 17f), f = Tile(x, y, 512, 0.25f, 19f);
            var c = Color.Lerp(new Color(0.26f, 0.27f, 0.24f), new Color(0.44f, 0.44f, 0.4f), st);
            c = Color.Lerp(c, new Color(0.2f, 0.3f, 0.14f), Mathf.Clamp01((n - 0.5f) * 2.5f));
            return c * (0.8f + 0.35f * f);
        });
        var mossT = MakeTex("T_Moss", (x, y) =>
        {
            float n = Tile(x, y, 512, 0.03f, 41f), f = Tile(x, y, 512, 0.2f, 43f), sp = Tile(x, y, 512, 0.7f, 47f);
            var c = Color.Lerp(new Color(0.16f, 0.32f, 0.06f), new Color(0.38f, 0.6f, 0.12f), n);
            c = Color.Lerp(c, new Color(0.2f, 0.22f, 0.18f), Mathf.Clamp01((0.35f - f) * 3f));
            return c * (0.8f + 0.35f * sp);
        });
        var layers = new[] { Layer("L_Grass", grassT, 7f), Layer("L_Litter", litterT, 6f), Layer("L_Dirt", dirtT, 5f), Layer("L_Rock", rockT, 12f), Layer("L_Moss", mossT, 8f) };

        // ---------- terrain ----------
        var data = new TerrainData();
        data.heightmapResolution = 513;
        data.size = new Vector3(TerrainSize, TerrainHeight, TerrainSize);
        int R = data.heightmapResolution;
        var h = new float[R, R];
        for (int i = 0; i < R; i++)
            for (int j = 0; j < R; j++)
            {
                float x = TerrainPos.x + j / (float)(R - 1) * TerrainSize;
                float z = TerrainPos.z + i / (float)(R - 1) * TerrainSize;
                h[i, j] = Mathf.Clamp01((HeightAt(x, z) - OriginY) / TerrainHeight);
            }
        data.SetHeights(0, 0, h);
        data.terrainLayers = layers;
        data.alphamapResolution = 512;
        int A = data.alphamapResolution;
        var al = new float[A, A, 5];
        for (int i = 0; i < A; i++)
            for (int j = 0; j < A; j++)
            {
                float x = TerrainPos.x + (j + 0.5f) / A * TerrainSize;
                float z = TerrainPos.z + (i + 0.5f) / A * TerrainSize;
                float e = Outside(x, z, out float pd);
                float steep = data.GetSteepness((j + 0.5f) / A, (i + 0.5f) / A);
                float nz = Mathf.PerlinNoise(x * 0.15f, z * 0.15f);
                float rock = Mathf.Clamp01((steep - 26f) / 14f);
                float path = e < 0f ? Mathf.Clamp01((3.2f + nz * 1.5f - pd) / 1.5f) : 0f;
                float litter = Mathf.Clamp01(1f - Mathf.Abs(e + 1.5f) / 4f) * (0.5f + nz);
                if (e > 2f) litter = Mathf.Max(litter, 0.6f);
                float grass = 1f;
                float mossN = Mathf.PerlinNoise(x * 0.07f + 3f, z * 0.07f + 9f);
                float moss = Mathf.Clamp01((steep - 14f) / 10f) * (0.4f + mossN) * 1.6f;
                rock *= Mathf.Clamp01(1.4f - mossN);
                float pond = LGeo.PondDepth(x, z);
                litter += pond * 2f;
                float[] w = { grass, litter, path * 2f, rock * 3f, moss };
                float sum = w.Sum();
                for (int k = 0; k < 5; k++) al[i, j, k] = w[k] / sum;
            }
        data.SetAlphamaps(0, 0, al);
        AssetDatabase.CreateAsset(data, Out + "/LMap_TerrainData.asset");

        var tgo = Terrain.CreateTerrainGameObject(data);
        tgo.name = "Terrain_L";
        tgo.transform.position = TerrainPos;
        tgo.isStatic = true;
        var terrain = tgo.GetComponent<Terrain>();
        var tmat = new Material(Shader.Find("Universal Render Pipeline/Terrain/Lit"));
        AssetDatabase.CreateAsset(tmat, Out + "/M_Terrain.mat");
        terrain.materialTemplate = tmat;
        terrain.drawInstanced = true;
        terrain.basemapDistance = 120f;
        terrain.heightmapPixelError = 4f;

        var map = new GameObject("Map").transform;
        var veg = new GameObject("Vegetation").transform; veg.SetParent(map);
        var rocks = new GameObject("Rocks").transform; rocks.SetParent(map);
        var crystals = new GameObject("Crystals").transform; crystals.SetParent(map);
        var lights = new GameObject("Lights").transform; lights.SetParent(map);

        string[] palms = { "Palm_CoconutDouble", "Palm_CoconutTriple", "Palm_Fan", "Palm_FanBranching", "Palm_Grove" };
        string[] bushes = { "Fern_Bush", "Plant_BananaLeaves", "Plant_SpikyYucca", "Fern_Bush", "Plant_BananaLeaves", "Agave_BlueGrass" };
        string[] small = { "Fern_Light", "Flower_RedRosette", "Succulent_GreenRosette", "Plant_StarRosette", "Plant_Sprout", "Mushroom_Group4", "Mushroom_Group5", "Mushroom_Pair", "Pineapple_Green", "Pineapple_Ripe" };
        string[] rockN = { "Rock_BlackFlat", "Rock_BlackRound", "Rock_Mossy", "Rock_Mossy" };
        string[] cryG = { "Crystal_BlueCluster_Glow", "Crystal_GreenCluster_Glow" };
        string[] cry = { "Crystal_BlueCluster", "Crystal_GreenCluster", "Crystal_BlueShard", "Crystal_GreenShard", "Crystal_GreenChunk" };

        // ---------- walls of palms along corridors ----------
        int palmCount = 0;
        for (int arm = 0; arm < 2; arm++)
            for (float t = 32f; t <= LGeo.ArmLength + 6f; t += 3.6f)
                for (int side = -1; side <= 1; side += 2)
                {
                    var c = LGeo.ArmPoint(arm, Mathf.Min(t, LGeo.ArmLength));
                    var rgt = LGeo.ArmRight(arm, Mathf.Min(t, LGeo.ArmLength));
                    float hw = LGeo.HalfWidthAt(t);
                    // inner row: crowns hang over the corridor
                    float off = hw + Random.Range(-1.5f, 3f);
                    var p = c + rgt * side * off + LGeo.ArmForward(arm, t) * Random.Range(-1.5f, 1.5f);
                    var palm = Place(Pick(palms), p.x, p.z, Random.Range(0, 360f), Random.Range(1.9f, 2.9f), veg);
                    if (palm) { TrunkCollider(palm, 0.45f); palmCount++; }
                    // outer rows on the slopes
                    for (int row = 1; row <= 2; row++)
                    {
                        if (Random.value < 0.35f) continue;
                        var q = c + rgt * side * (hw + 5f + row * 6f + Random.Range(-2f, 2f)) + LGeo.ArmForward(arm, t) * Random.Range(-2f, 2f);
                        if (Place(Pick(palms), q.x, q.z, Random.Range(0, 360f), Random.Range(2.2f, 3.2f), veg)) palmCount++;
                    }
                }
        // around the arena
        for (float a = 0f; a < 360f; a += 7f)
        {
            float rad = a * Mathf.Deg2Rad;
            var dir = new Vector3(Mathf.Cos(rad), 0, Mathf.Sin(rad));
            var p = dir * (LGeo.ArenaRadius + Random.Range(0f, 4f));
            Outside(p.x, p.z, out _);
            // skip the two corridor mouths
            if (Mathf.Abs(Mathf.DeltaAngle(a, 270f)) < 22f || Mathf.Abs(Mathf.DeltaAngle(a, 180f)) < 22f) continue;
            var palm = Place(Pick(palms), p.x, p.z, Random.Range(0, 360f), Random.Range(2.2f, 3.2f), veg);
            if (palm) { TrunkCollider(palm, 0.45f); palmCount++; }
            var q = dir * (LGeo.ArenaRadius + 10f + Random.Range(0f, 8f));
            if (Place(Pick(palms), q.x, q.z, Random.Range(0, 360f), Random.Range(2.4f, 3.4f), veg)) palmCount++;
        }
        // distant canopy on the ridges
        for (int i = 0; i < 420; i++)
        {
            float x = Random.Range(TerrainPos.x + 5f, TerrainPos.x + TerrainSize - 5f), z = Random.Range(TerrainPos.z + 5f, TerrainPos.z + TerrainSize - 5f);
            float e = Outside(x, z, out _);
            if (e < 16f || e > 60f) continue;
            if (Place(Pick(palms), x, z, Random.Range(0, 360f), Random.Range(2.2f, 3.6f), veg)) palmCount++;
        }

        // ---------- mossy cliff faces along the walls ----------
        for (int arm = 0; arm < 2; arm++)
            for (float t = 36f; t <= LGeo.ArmLength; t += 7.5f)
                for (int side = -1; side <= 1; side += 2)
                {
                    if (Random.value < 0.3f) continue;
                    var c = LGeo.ArmPoint(arm, t);
                    var p = c + LGeo.ArmRight(arm, t) * side * (LGeo.HalfWidthAt(t) + Random.Range(3f, 6f));
                    var r = Place(Random.value < 0.7f ? "Rock_Mossy" : "Rock_BlackRound", p.x, p.z, Random.Range(0, 360f), Random.Range(7f, 12f), rocks, 0.25f);
                    if (r) { r.transform.rotation = Quaternion.Euler(Random.Range(-12f, 12f), 0, Random.Range(-12f, 12f)) * r.transform.rotation; MeshColliders(r); }
                }
        for (float a = 0f; a < 360f; a += 16f)
        {
            if (Mathf.Abs(Mathf.DeltaAngle(a, 270f)) < 24f || Mathf.Abs(Mathf.DeltaAngle(a, 180f)) < 24f) continue;
            float rad = a * Mathf.Deg2Rad;
            var p = new Vector3(Mathf.Cos(rad), 0, Mathf.Sin(rad)) * (LGeo.ArenaRadius + Random.Range(4f, 7f));
            var r = Place("Rock_Mossy", p.x, p.z, Random.Range(0, 360f), Random.Range(8f, 13f), rocks, 0.25f);
            if (r) MeshColliders(r);
        }

        // ---------- ponds ----------
        var water = AssetDatabase.LoadAssetAtPath<Material>(Out + "/M_Water.mat");
        if (water == null) { water = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(water, Out + "/M_Water.mat"); }
        water.SetFloat("_Surface", 1f); water.SetFloat("_Blend", 0f);
        water.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        water.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        water.SetFloat("_ZWrite", 0f);
        water.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        water.renderQueue = 3000;
        water.SetColor("_BaseColor", new Color(0.06f, 0.2f, 0.18f, 0.62f));
        water.SetFloat("_Smoothness", 0.96f); water.SetFloat("_Metallic", 0.1f);
        EditorUtility.SetDirty(water);
        var ponds = new GameObject("Ponds").transform; ponds.SetParent(map);
        foreach (var pd in LGeo.Ponds)
        {
            var w = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Object.DestroyImmediate(w.GetComponent<Collider>());
            w.name = "Pond";
            w.transform.SetParent(ponds);
            float wy = LGeo.Ground(new Vector3(pd.x + pd.y * 1.3f, 0, pd.z)) - 0.12f;
            w.transform.position = new Vector3(pd.x, Mathf.Min(wy, -0.1f), pd.z);
            w.transform.localScale = new Vector3(pd.y * 2.3f, 0.005f, pd.y * 2.3f);
            w.GetComponent<Renderer>().sharedMaterial = water;
            w.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            w.isStatic = true;
            // crystals and stones at the shore
            for (int i = 0; i < 4; i++)
            {
                float a = Random.Range(0f, Mathf.PI * 2f);
                var q = new Vector3(pd.x, 0, pd.z) + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * pd.y * Random.Range(0.6f, 1.1f);
                var go = Place(i == 0 ? Pick(cryG) : Pick(cry), q.x, q.z, Random.Range(0, 360f), Random.Range(1.2f, 2.4f), crystals, 0.1f);
                if (go && i == 0) MeshColliders(go);
            }
            var pl = new GameObject("PondGlow").AddComponent<Light>();
            pl.type = LightType.Point; pl.range = 9f; pl.intensity = 1.6f; pl.color = new Color(0.4f, 0.95f, 0.8f);
            pl.transform.position = new Vector3(pd.x, 1.2f, pd.z);
            pl.transform.SetParent(lights, true);
        }

        // ---------- dense instanced vegetation (drawn at runtime) ----------
        var scatter = map.gameObject.AddComponent<FoliageScatter>();
        GameObject[] G(params string[] n) => n.Select(P).Where(x => x != null).ToArray();
        scatter.layers = new[]
        {
            new FoliageScatter.Layer { name = "grass", prefabs = G("Grass_Clump"), count = 26000, minE = -16f, maxE = 1.5f, minPath = 0.9f, scale = new Vector2(0.9f, 1.7f), drawDistance = 55f },
            new FoliageScatter.Layer { name = "ferns", prefabs = G("Fern_Light", "Fern_Bush", "Fern_Light"), count = 9000, minE = -12f, maxE = 3f, edgeBias = 0.6f, minPath = 1.8f, scale = new Vector2(1.0f, 2.2f), drawDistance = 75f },
            new FoliageScatter.Layer { name = "bushes", prefabs = G("Plant_BananaLeaves", "Plant_SpikyYucca", "Agave_BlueGrass", "Fern_Bush"), count = 3800, minE = -7f, maxE = 4f, edgeBias = 0.8f, minPath = 2.6f, scale = new Vector2(1.5f, 3.2f), drawDistance = 110f, shadows = true },
            new FoliageScatter.Layer { name = "flowers", prefabs = G("Flower_RedRosette", "Succulent_GreenRosette", "Plant_StarRosette", "Plant_Sprout", "Mushroom_Group4", "Mushroom_Group5", "Mushroom_Pair", "Pineapple_Green", "Pineapple_Ripe"), count = 3600, minE = -14f, maxE = 0.5f, minPath = 1.6f, scale = new Vector2(1.1f, 2.8f), drawDistance = 60f },
            new FoliageScatter.Layer { name = "moss", prefabs = G("Ground_MossPatch"), count = 2200, minE = -16f, maxE = 2f, minPath = 1.4f, scale = new Vector2(1.4f, 3f), drawDistance = 65f, avoidPonds = false },
            new FoliageScatter.Layer { name = "slopes", prefabs = G("Fern_Bush", "Plant_BananaLeaves", "Fern_Light", "Plant_SpikyYucca"), count = 7000, minE = 1f, maxE = 14f, scale = new Vector2(1.8f, 3.4f), drawDistance = 130f },
            new FoliageScatter.Layer { name = "stepping stones", prefabs = G("Rock_BlackFlat", "Rock_BlackRound"), count = 1500, onPath = true, scale = new Vector2(0.9f, 1.5f), flatten = 0.22f, drawDistance = 90f, shadows = true },
            new FoliageScatter.Layer { name = "pebbles", prefabs = G("Stones_Scatter"), count = 1400, minE = -16f, maxE = 0f, minPath = 0f, scale = new Vector2(1.2f, 2.4f), drawDistance = 60f, avoidPonds = false },
        };

        // ---------- rocks ----------
        for (int i = 0; i < 6000; i++)
        {
            float x = Random.Range(-175f, 45f), z = Random.Range(-175f, 45f);
            float e = Outside(x, z, out float pd);
            bool edge = e > -2.5f && e < 4f && Random.value < 0.05f;
            bool cover = e < -4f && pd > 4f && Random.value < 0.004f;
            if (!edge && !cover) continue;
            var r = Place(Pick(rockN), x, z, Random.Range(0, 360f), edge ? Random.Range(2.5f, 5.5f) : Random.Range(1.6f, 3f), rocks, 0.12f);
            if (r && e < 2.5f) MeshColliders(r);
        }

        // ---------- crystals: arena ring and colossus lairs ----------
        for (int i = 0; i < 26; i++)
        {
            float a = i / 26f * 360f + Random.Range(-5f, 5f);
            if (Mathf.Abs(Mathf.DeltaAngle(a, 270f)) < 18f || Mathf.Abs(Mathf.DeltaAngle(a, 180f)) < 18f) continue;
            float rad = a * Mathf.Deg2Rad, d = LGeo.ArenaRadius - Random.Range(1f, 5f);
            var go = Place(i % 3 == 0 ? Pick(cryG) : Pick(cry), Mathf.Cos(rad) * d, Mathf.Sin(rad) * d, Random.Range(0, 360f), Random.Range(2.2f, 4.2f), crystals, 0.1f);
            if (go) MeshColliders(go);
        }
        for (int i = 0; i < 5; i++)
        {
            float a = i / 5f * Mathf.PI * 2f + 0.5f;
            var l = new GameObject("ArenaGlow").AddComponent<Light>();
            l.type = LightType.Point; l.range = 16f; l.intensity = 3f;
            l.color = i % 2 == 0 ? new Color(0.35f, 0.75f, 1f) : new Color(0.45f, 1f, 0.6f);
            var p = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * (LGeo.ArenaRadius - 4f);
            l.transform.position = LGeo.OnGround(p) + Vector3.up * 3f;
            l.transform.SetParent(lights, true);
        }
        for (int arm = 0; arm < 2; arm++)
        {
            float t = LGeo.ZoneMid[2] - 4f;
            var c = LGeo.ArmPoint(arm, t);
            var rgt = LGeo.ArmRight(arm, t);
            float hw = LGeo.HalfWidthAt(t);
            for (int i = 0; i < 10; i++)
            {
                int side = i % 2 == 0 ? -1 : 1;
                var p = c + rgt * side * (hw - Random.Range(0.5f, 4f)) + LGeo.ArmForward(arm, t) * Random.Range(-12f, 12f);
                var go = Place(i % 4 == 0 ? Pick(cryG) : Pick(cry), p.x, p.z, Random.Range(0, 360f), Random.Range(1.8f, 3.4f), crystals, 0.1f);
                if (go) MeshColliders(go);
            }
            var l = new GameObject("LairGlow").AddComponent<Light>();
            l.type = LightType.Point; l.range = 18f; l.intensity = 2.5f; l.color = new Color(1f, 0.55f, 0.3f);
            l.transform.position = LGeo.OnGround(c) + Vector3.up * 4f;
            l.transform.SetParent(lights, true);
        }

        // ---------- hanging vines from the canopy ----------
        for (int arm = 0; arm < 2; arm++)
            for (float t = 40f; t < LGeo.ArmLength; t += 3.5f)
            {
                if (Random.value < 0.25f) continue;
                int side = Random.value < 0.5f ? -1 : 1;
                var p = LGeo.ArmPoint(arm, t) + LGeo.ArmRight(arm, t) * side * (LGeo.HalfWidthAt(t) - Random.Range(0.5f, 3f));
                var v = Place(Random.value < 0.5f ? "Vines_HangingStrands" : "Vines_HangingLoops", p.x, p.z, Random.Range(0, 360f), Random.Range(1.8f, 2.8f), veg, 0f);
                if (v) v.transform.position += Vector3.up * Random.Range(5f, 8f);
            }

        // ---------- gates ----------
        var lmapGo = new GameObject("LMap");
        lmapGo.transform.SetParent(map);
        var lmap = lmapGo.AddComponent<LMap>();
        var vine = P("Vines_HangingStrands");
        for (int arm = 0; arm < 2; arm++)
            for (int g = 0; g < 3; g++)
            {
                float t = LGeo.GateDist[g];
                var c = LGeo.OnGround(LGeo.ArmPoint(arm, t));
                var go = new GameObject("Gate_" + (arm == 0 ? "A" : "B") + (g + 1));
                go.transform.SetParent(lmapGo.transform);
                go.transform.position = c;
                go.transform.rotation = Quaternion.LookRotation(LGeo.ArmForward(arm, t));
                var gate = go.AddComponent<Gate>();
                gate.arm = arm; gate.index = g;
                gate.width = 2f * (LGeo.HalfWidthAt(t) + 4f);
                gate.vinePrefab = vine;
                var bc = go.AddComponent<BoxCollider>();
                bc.size = new Vector3(gate.width, 12f, 1.6f); bc.center = new Vector3(0, 5f, 0);
                if (arm == 0) lmap.gatesA[g] = gate; else lmap.gatesB[g] = gate;
                // rock pillars at both ends
                for (int side = -1; side <= 1; side += 2)
                {
                    var p = c + go.transform.right * side * (gate.width * 0.5f - 1f);
                    var r = Place("Rock_Mossy", p.x, p.z, Random.Range(0, 360f), 5.5f, rocks, 0.15f);
                    if (r) MeshColliders(r);
                }
            }

        // ---------- places, manager wiring, menu view ----------
        var places = new GameObject("Places").transform; places.SetParent(map);
        Transform Mark(string n, Vector3 p, Vector3 fwd)
        {
            var t = new GameObject(n).transform; t.SetParent(places);
            t.position = LGeo.OnGround(p); t.rotation = Quaternion.LookRotation(fwd); return t;
        }
        var spawnA = Mark("PlayerSpawn", LGeo.ArmPoint(0, LGeo.SpawnDist), LGeo.ArmForward(0, LGeo.SpawnDist));
        var spawnB = Mark("RivalSpawn", LGeo.ArmPoint(1, LGeo.SpawnDist), LGeo.ArmForward(1, LGeo.SpawnDist));
        var arena = Mark("Arena", Vector3.zero, Vector3.forward);

        var mm = Object.FindAnyObjectByType<MatchManager>();
        if (mm)
        {
            mm.playerSpawn = spawnA; mm.rivalSpawn = spawnB; mm.bossSpot = arena; mm.extractionPoint = arena;
            EditorUtility.SetDirty(mm);
        }

        // menu: heroes stand in the first clearing, camera looks down the corridor towards the arena
        var display = mm ? mm.selectDisplay : GameObject.Find("SelectDisplay");
        var origin = LGeo.OnGround(LGeo.ArmPoint(0, 136f));
        if (display)
        {
            display.SetActive(true);
            var kids = display.transform.Cast<Transform>().ToList();
            var offs = new[] { new Vector3(-1.1f, 0, 0), new Vector3(0.9f, 0, 0.3f) };
            for (int i = 0; i < kids.Count && i < 2; i++)
            {
                var p = origin + offs[i];
                kids[i].position = LGeo.OnGround(p);
            }
        }
        var cam = Camera.main;
        if (cam)
        {
            var cp = origin + new Vector3(0.6f, 0, -5.2f);
            cam.transform.position = LGeo.OnGround(cp) + Vector3.up * 1.75f;
            cam.transform.LookAt(origin + new Vector3(-0.1f, 1.0f, 1.5f));
            cam.farClipPlane = 260f;
        }
        RenderSettings.fogDensity = 0.012f;
        RenderSettings.fogColor = new Color(0.5f, 0.66f, 0.55f);
        RenderSettings.ambientEquatorColor = new Color(0.42f, 0.56f, 0.36f);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log($"L map built: palms {palmCount}, plants {plantCount}, objects {map.GetComponentsInChildren<Transform>().Length}");
    }
}
#endif
