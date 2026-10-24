using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Relict;

namespace Relict.EditorTools
{
    // Brings the Poly Haven downloads (asset/polyhaven) into the game:
    //  * plants and stones (.blend, converted by the installed Blender) → one prefab per mesh with URP materials,
    //    alpha-cut double-sided leaves, mesh LODs, sized like the kit asset they replace;
    //  * tileable ground scans → terrain layers (litter, path), with DirectX normals flipped;
    //  * the scene's foliage scatter and terrain are switched to the new assets.
    public static class PolyHavenImporter
    {
        const string Src = "C:/Users/AndreyD/Documents/relict/asset/polyhaven";
        const string Root = "Assets/_Relict/Art/PolyHaven";
        const string PrefabDir = "Assets/_Relict/Prefabs/PolyHaven";
        const string KitDir = "Assets/RelictEnv/Prefabs/";

        static readonly string[] Plants = { "grass_medium_02", "grass_bermuda_01", "fern_02", "flower_heliophila", "celandine_01", "anthurium_botany_01", "periwinkle_plant", "weed_plant_02" };
        static readonly string[] Stones = { "namaqualand_rocks_01", "namaqualand_stones_01", "cliff_side" };

        [MenuItem("Tools/Relict/Poly Haven/Import + Apply")]
        public static void Menu() { Debug.Log(Run()); }

        public static string Run()
        {
            var log = new System.Text.StringBuilder();
            Copy(log);
            AssetDatabase.Refresh();
            ImportModels(log);
            ImportTextures(log);
            var made = BuildPrefabs(log);
            ApplyToScene(made, log);
            AssetDatabase.SaveAssets();
            return log.ToString();
        }

        // ---------- 1. copy ----------
        static void Copy(System.Text.StringBuilder log)
        {
            Directory.CreateDirectory(Root);
            int n = 0;
            foreach (var dir in Directory.GetDirectories(Src))
            {
                string name = Path.GetFileName(dir);
                if (name.Contains("(")) continue;
                string clean = Clean(name);
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    string fn = Path.GetFileName(f).ToLower();
                    if (fn.Contains("_disp_") || fn.Contains("translucency") || fn.Contains("_dry_") || fn.EndsWith(".blend1")) continue;
                    string to = Path.Combine(Root, clean, f.Substring(dir.Length + 1));
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    if (!File.Exists(to)) { File.Copy(f, to); n++; }
                }
            }
            log.AppendLine("copied " + n + " new files");
        }

        static string Clean(string n) => n.Replace("_4k.blend", "").Replace("_4k", "");

        // ---------- 2. model import settings ----------
        static void ImportModels(System.Text.StringBuilder log)
        {
            foreach (var path in Directory.GetFiles(Root, "*.blend", SearchOption.AllDirectories).Select(p => p.Replace('\\', '/')))
            {
                var imp = AssetImporter.GetAtPath(path) as ModelImporter;
                if (imp == null) { log.AppendLine("NOT IMPORTED (Blender conversion failed?): " + path); continue; }
                bool dirty = false;
                if (imp.materialImportMode != ModelImporterMaterialImportMode.None) { imp.materialImportMode = ModelImporterMaterialImportMode.None; dirty = true; }
                if (!imp.generateMeshLods) { imp.generateMeshLods = true; imp.maximumMeshLod = 6; dirty = true; }
                if (imp.importAnimation) { imp.importAnimation = false; dirty = true; }
                if (imp.importCameras || imp.importLights) { imp.importCameras = false; imp.importLights = false; dirty = true; }
                if (dirty) imp.SaveAndReimport();
            }
        }

        // ---------- 3. textures ----------
        static void ImportTextures(System.Text.StringBuilder log)
        {
            int n = 0;
            foreach (var path in Directory.GetFiles(Root, "*.*", SearchOption.AllDirectories).Select(p => p.Replace('\\', '/'))
                         .Where(p => p.EndsWith(".jpg") || p.EndsWith(".png") || p.EndsWith(".exr")))
            {
                var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                if (ti == null) continue;
                string fn = Path.GetFileName(path).ToLower();
                if (fn.Contains("_baked")) continue;
                bool ground = path.Contains("brown_mud_leaves") || path.Contains("leaf_scattered_gravel");
                if (fn.Contains("_nor_"))
                {
                    ti.textureType = TextureImporterType.NormalMap;
                    var so = new SerializedObject(ti);
                    var flip = so.FindProperty("m_FlipGreenChannel");
                    if (flip != null) { flip.boolValue = fn.Contains("_nor_dx"); so.ApplyModifiedPropertiesWithoutUndo(); }
                }
                else if (fn.Contains("_rough_") || fn.Contains("_alpha_") || fn.Contains("_opacity_") || fn.Contains("_mask_"))
                {
                    ti.textureType = TextureImporterType.Default;
                    ti.sRGBTexture = false;
                }
                ti.maxTextureSize = ground || fn.Contains("_diff_") ? 2048 : 1024;
                ti.textureCompression = TextureImporterCompression.Compressed;
                ti.mipmapEnabled = true;
                ti.SaveAndReimport();
                n++;
            }
            log.AppendLine("textures configured: " + n);
        }

        static string Tex(string asset, string kind)
        {
            string dir = Root + "/" + asset;
            if (!Directory.Exists(dir)) return null;
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(p => p.Replace('\\', '/'))
                .FirstOrDefault(p => Path.GetFileName(p).ToLower().Contains("_" + kind + "_") && !p.EndsWith(".meta"));
        }

        // diffuse RGB + alpha mask → one RGBA texture for alpha-clipped foliage
        static Texture2D BaseWithAlpha(string asset)
        {
            string diff = Tex(asset, "diff"), alpha = Tex(asset, "alpha") ?? Tex(asset, "opacity");
            if (diff == null) return null;
            if (alpha == null) return AssetDatabase.LoadAssetAtPath<Texture2D>(diff);
            string outPath = Root + "/" + asset + "/" + asset + "_baked_base.png";
            if (!File.Exists(outPath))
            {
                var d = ReadPixels(diff, 2048, out int w, out int h);
                var a = ReadPixels(alpha, w, out int aw, out int ah);
                bool useAlpha = false;
                for (int k = 0; k < a.Length; k += 97) if (a[k].a < 0.99f) { useAlpha = true; break; }
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                var px = new Color[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        var c = d[y * w + x];
                        var m = a[(y * ah / h) * aw + (x * aw / w)];
                        c.a = useAlpha ? m.a : m.r;
                        px[y * w + x] = c;
                    }
                tex.SetPixels(px); tex.Apply();
                File.WriteAllBytes(outPath, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(outPath);
            }
            var ti = (TextureImporter)AssetImporter.GetAtPath(outPath);
            ti.alphaIsTransparency = true;
            ti.mipMapsPreserveCoverage = true;     // leaves don't vanish in the distance
            ti.alphaTestReferenceValue = 0.45f;
            ti.maxTextureSize = 2048;
            ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
        }

        static Color[] ReadPixels(string path, int maxSize, out int w, out int h)
        {
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            bool wasReadable = ti.isReadable; var wasComp = ti.textureCompression; int wasSize = ti.maxTextureSize;
            ti.isReadable = true; ti.textureCompression = TextureImporterCompression.Uncompressed; ti.maxTextureSize = maxSize;
            ti.SaveAndReimport();
            var t = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            w = t.width; h = t.height;
            var px = t.GetPixels();
            ti.isReadable = wasReadable; ti.textureCompression = wasComp; ti.maxTextureSize = wasSize;
            ti.SaveAndReimport();
            return px;
        }

        static Material MakeMaterial(string asset, bool foliage)
        {
            Directory.CreateDirectory(Root + "/Materials");
            string path = Root + "/Materials/PH_" + asset + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
            var baseTex = foliage ? BaseWithAlpha(asset) : AssetDatabase.LoadAssetAtPath<Texture2D>(Tex(asset, "diff"));
            m.SetTexture("_BaseMap", baseTex);
            m.SetColor("_BaseColor", Color.white);
            var nor = Tex(asset, "nor");
            if (nor != null) { m.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(nor)); m.EnableKeyword("_NORMALMAP"); m.SetFloat("_BumpScale", 1f); }
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Smoothness", foliage ? 0.32f : 0.2f);
            if (foliage)
            {
                m.SetFloat("_AlphaClip", 1f);
                m.SetFloat("_Cutoff", 0.45f);
                m.EnableKeyword("_ALPHATEST_ON");
                m.SetFloat("_Cull", 0f);
                m.renderQueue = (int)RenderQueue.AlphaTest;
                m.SetOverrideTag("RenderType", "TransparentCutout");
            }
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }

        // ---------- 4. prefabs: one per mesh, pivot at the base ----------
        class Made { public string asset; public GameObject prefab; public float height, size; public int tris; }

        static Dictionary<string, List<Made>> BuildPrefabs(System.Text.StringBuilder log)
        {
            Directory.CreateDirectory(PrefabDir);
            var made = new Dictionary<string, List<Made>>();
            foreach (var asset in Plants.Concat(Stones))
            {
                var blend = Directory.Exists(Root + "/" + asset) ? Directory.GetFiles(Root + "/" + asset, "*.blend", SearchOption.AllDirectories).Select(p => p.Replace('\\', '/')).FirstOrDefault() : null;
                var model = blend != null ? AssetDatabase.LoadAssetAtPath<GameObject>(blend) : null;
                if (model == null) { log.AppendLine(asset + ": model missing"); continue; }
                bool foliage = Plants.Contains(asset);
                var mat = MakeMaterial(asset, foliage);
                var inst = (GameObject)Object.Instantiate(model);
                inst.transform.position = Vector3.zero; inst.transform.rotation = Quaternion.identity; inst.transform.localScale = Vector3.one;
                var list = new List<Made>();
                int i = 0;
                foreach (var mf in inst.GetComponentsInChildren<MeshFilter>())
                {
                    if (mf.sharedMesh == null || mf.sharedMesh.vertexCount < 3) continue;
                    var mr = mf.GetComponent<MeshRenderer>();
                    var b = mr ? mr.bounds : new Bounds(mf.transform.position, Vector3.one);
                    var root = new GameObject("PH_" + asset + "_" + i);
                    var child = new GameObject("Mesh");
                    child.transform.SetParent(root.transform, false);
                    // keep orientation/scale from Blender, move the pivot to bottom centre
                    child.transform.localRotation = mf.transform.rotation;
                    child.transform.localScale = mf.transform.lossyScale;
                    child.transform.localPosition = mf.transform.position - new Vector3(b.center.x, b.min.y, b.center.z);
                    child.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                    var r = child.AddComponent<MeshRenderer>();
                    r.sharedMaterials = Enumerable.Repeat(mat, Mathf.Max(1, mf.sharedMesh.subMeshCount)).ToArray();
                    r.shadowCastingMode = foliage ? ShadowCastingMode.Off : ShadowCastingMode.On;
                    string ppath = PrefabDir + "/PH_" + asset + "_" + i + ".prefab";
                    var prefab = PrefabUtility.SaveAsPrefabAsset(root, ppath);
                    Object.DestroyImmediate(root);
                    int tris = 0; for (int s = 0; s < mf.sharedMesh.subMeshCount; s++) tris += (int)mf.sharedMesh.GetIndexCount(s) / 3;
                    list.Add(new Made { asset = asset, prefab = prefab, height = b.size.y, size = Mathf.Max(b.size.x, b.size.z), tris = tris });
                    i++;
                }
                Object.DestroyImmediate(inst);
                made[asset] = list;
                log.AppendLine(asset + ": " + list.Count + " prefabs, tris " + string.Join("/", list.Select(x => x.tris)) + ", height " + string.Join("/", list.Select(x => x.height.ToString("0.00"))) + "m, LODs " + (list.Count > 0 ? list[0].prefab.GetComponentInChildren<MeshFilter>().sharedMesh.lodCount : 0));
            }
            return made;
        }

        // height of the kit asset a layer used, to keep layer scale ranges meaningful
        static float KitHeight(string name)
        {
            var p = AssetDatabase.LoadAssetAtPath<GameObject>(KitDir + name + ".prefab");
            if (p == null) return 0f;
            var rs = p.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return 0f;
            var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
            return b.size.y;
        }

        static GameObject[] Sized(Dictionary<string, List<Made>> made, float targetHeight, bool byFootprint, params string[] assets)
        {
            var res = new List<GameObject>();
            foreach (var a in assets)
            {
                if (!made.TryGetValue(a, out var list)) continue;
                foreach (var m in list)
                {
                    float cur = byFootprint ? m.size : m.height;
                    if (targetHeight > 0f && cur > 0.01f)
                    {
                        float k = Mathf.Clamp(targetHeight / cur, 0.2f, 6f);
                        var root = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(m.prefab));
                        root.transform.localScale = Vector3.one * k;
                        PrefabUtility.SaveAsPrefabAsset(root, AssetDatabase.GetAssetPath(m.prefab));
                        PrefabUtility.UnloadPrefabContents(root);
                    }
                    res.Add(m.prefab);
                }
            }
            return res.ToArray();
        }

        static GameObject Kit(string n) => AssetDatabase.LoadAssetAtPath<GameObject>(KitDir + n + ".prefab");

        // ---------- 5. scene ----------
        static void ApplyToScene(Dictionary<string, List<Made>> made, System.Text.StringBuilder log)
        {
            var scatter = Object.FindAnyObjectByType<FoliageScatter>();
            if (scatter != null && scatter.layers != null)
            {
                foreach (var L in scatter.layers)
                {
                    GameObject[] np = null;
                    switch (L.name)
                    {
                        case "grass": np = Sized(made, KitHeight("Grass_Clump"), false, "grass_medium_02", "grass_bermuda_01"); break;
                        case "ferns": np = Sized(made, KitHeight("Fern_Light"), false, "fern_02"); break;
                        case "flowers":
                            np = Sized(made, KitHeight("Flower_RedRosette"), false, "flower_heliophila", "celandine_01", "anthurium_botany_01", "periwinkle_plant")
                                .Concat(new[] { Kit("Flower_RedRosette"), Kit("Mushroom_Pair") }.Where(x => x)).ToArray();
                            break;
                        case "bushes":
                            np = L.prefabs.Concat(Sized(made, KitHeight("Plant_SpikyYucca") * 0.8f, false, "weed_plant_02")).ToArray();
                            break;
                        case "slopes":
                            np = L.prefabs.Where(p => p && p.name != "Fern_Light").Concat(made.ContainsKey("fern_02") ? made["fern_02"].Select(m => m.prefab) : new GameObject[0]).ToArray();
                            break;
                        case "stepping stones": np = Sized(made, 0.9f, true, "namaqualand_rocks_01"); break;
                        case "pebbles": np = Sized(made, 0.5f, true, "namaqualand_stones_01"); break;
                    }
                    if (np != null && np.Length > 0) { L.prefabs = np; log.AppendLine("layer " + L.name + " → " + np.Length + " prefabs"); }
                }
                EditorUtility.SetDirty(scatter);
            }

            // terrain: forest-floor litter and leafy gravel paths from the scans
            var t = Object.FindAnyObjectByType<Terrain>();
            if (t != null)
            {
                foreach (var l in t.terrainData.terrainLayers)
                {
                    string asset = l.name == "L_Litter" ? "brown_mud_leaves_01" : l.name == "L_Dirt" ? "leaf_scattered_gravel" : null;
                    if (asset == null || Tex(asset, "diff") == null) continue;
                    l.diffuseTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(Tex(asset, "diff"));
                    var nor = Tex(asset, "nor");
                    if (nor != null) { l.normalMapTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(nor); l.normalScale = 1f; }
                    l.tileSize = new Vector2(3.5f, 3.5f);
                    l.smoothness = 0.08f;
                    EditorUtility.SetDirty(l);
                    log.AppendLine("terrain layer " + l.name + " → " + asset);
                }
            }
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
        }
    }
}
