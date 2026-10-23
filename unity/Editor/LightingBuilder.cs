using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Relict.EditorTools
{
    // "Misty morning" lighting for the L map: mixed sun with shadowmask, baked GI + AO,
    // baked emissive crystals, light probes, reflection probes, mist and sunbeams, colour grading.
    public static class LightingBuilder
    {
        const string ArtDir = "Assets/_Relict/Art/Atmosphere";
        static readonly Color FogColor = new Color(0.64f, 0.71f, 0.69f);
        static readonly Color SunColor = new Color(1f, 0.84f, 0.68f);
        static readonly Vector3 SunEuler = new Vector3(22f, 225f, 0f);

        [MenuItem("Tools/Relict/Lighting/Setup (Misty Morning)")]
        public static void SetupMenu() { Setup(); }

        [MenuItem("Tools/Relict/Lighting/Setup + Bake")]
        public static void SetupAndBake()
        {
            Setup();
            Bake();
        }

        [MenuItem("Tools/Relict/Lighting/Bake Only")]
        public static void Bake()
        {
            Lightmapping.BakeAsync();
            Debug.Log("Relict: lighting bake started");
        }

        public static string Setup()
        {
            var log = new System.Text.StringBuilder();
            if (!AssetDatabase.IsValidFolder(ArtDir)) AssetDatabase.CreateFolder("Assets/_Relict/Art", "Atmosphere");

            Sun(log);
            Environment(log);
            Lights(log);
            StaticGI(log);
            Probes(log);
            Atmosphere(log);
            Grading(log);
            Settings(log);

            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("Relict lighting setup:\n" + log);
            return log.ToString();
        }

        // ---------- sun & environment ----------
        static void Sun(System.Text.StringBuilder log)
        {
            var sun = Object.FindObjectsByType<Light>().FirstOrDefault(l => l.type == LightType.Directional);
            if (sun == null) { sun = new GameObject("Sun").AddComponent<Light>(); sun.type = LightType.Directional; }
            sun.transform.rotation = Quaternion.Euler(SunEuler);
            sun.color = SunColor;
            sun.intensity = 1.7f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.85f;
            sun.lightmapBakeType = LightmapBakeType.Mixed;
            sun.bounceIntensity = 1.2f;
            RenderSettings.sun = sun;
            log.AppendLine("sun: mixed, low morning angle");
        }

        static void Environment(System.Text.StringBuilder log)
        {
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.56f, 0.64f, 0.68f);
            RenderSettings.ambientEquatorColor = new Color(0.47f, 0.55f, 0.50f);
            RenderSettings.ambientGroundColor = new Color(0.21f, 0.23f, 0.19f);
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = FogColor;
            RenderSettings.fogDensity = 0.019f;

            // sky that melts into the fog at the horizon
            var skyPath = ArtDir + "/MorningSky.mat";
            var sky = AssetDatabase.LoadAssetAtPath<Material>(skyPath);
            if (sky == null) { sky = new Material(Shader.Find("Skybox/Procedural")); AssetDatabase.CreateAsset(sky, skyPath); }
            sky.SetFloat("_SunDisk", 2f);
            sky.SetFloat("_SunSize", 0.05f);
            sky.SetFloat("_SunSizeConvergence", 6f);
            sky.SetFloat("_AtmosphereThickness", 1.35f);
            sky.SetColor("_SkyTint", new Color(0.62f, 0.68f, 0.70f));
            sky.SetColor("_GroundColor", FogColor);
            sky.SetFloat("_Exposure", 1.15f);
            EditorUtility.SetDirty(sky);
            RenderSettings.skybox = sky;

            var cam = Camera.main;
            if (cam) { cam.clearFlags = CameraClearFlags.Skybox; cam.backgroundColor = FogColor; }
            log.AppendLine("environment: trilight ambient, dense pale fog (0.019), procedural morning sky");
        }

        // ---------- local lights ----------
        static void Lights(System.Text.StringBuilder log)
        {
            int n = 0;
            foreach (var l in Object.FindObjectsByType<Light>().Where(l => l.type != LightType.Directional))
            {
                l.lightmapBakeType = LightmapBakeType.Baked;
                l.range *= l.range < 14f ? 1.25f : 1f;
                n++;
            }
            // glowing crystal materials throw their colour onto the surroundings
            int em = 0;
            foreach (var r in Object.FindObjectsByType<MeshRenderer>().Where(r => r.name.Contains("Crystal")))
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !m.IsKeywordEnabled("_EMISSION")) continue;
                    if (m.globalIlluminationFlags != MaterialGlobalIlluminationFlags.BakedEmissive)
                    {
                        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
                        EditorUtility.SetDirty(m);
                        em++;
                    }
                }
            log.AppendLine("point lights → baked: " + n + ", emissive crystal materials → baked emission: " + em);
        }

        // ---------- what goes into lightmaps ----------
        static bool IsLightmapped(string name) =>
            name.StartsWith("Rock") || name.StartsWith("Cliff") || name.StartsWith("Stone") || name.StartsWith("Ruin");

        static void StaticGI(System.Text.StringBuilder log)
        {
            int lm = 0, probe = 0, off = 0;
            var needUv = new HashSet<string>();
            foreach (var r in Object.FindObjectsByType<MeshRenderer>())
            {
                var go = r.gameObject;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var flags = GameObjectUtility.GetStaticEditorFlags(go);
                if ((flags & StaticEditorFlags.ContributeGI) == 0) continue;     // dynamic / runtime stuff stays out
                string meshName = mf.sharedMesh.name;
                bool water = go.name.Contains("Pond") || go.name.Contains("Water");
                if (water)
                {
                    GameObjectUtility.SetStaticEditorFlags(go, flags & ~StaticEditorFlags.ContributeGI);
                    off++;
                    continue;
                }
                if (IsLightmapped(meshName))
                {
                    r.receiveGI = ReceiveGI.Lightmaps;
                    r.scaleInLightmap = meshName == "Rock_Mossy" ? 0.8f : 1f;
                    var path = AssetDatabase.GetAssetPath(mf.sharedMesh);
                    if (!string.IsNullOrEmpty(path) && path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase)) needUv.Add(path);
                    lm++;
                }
                else
                {
                    // palms, vines, crystals: cast baked shadows/bounce, lit by light probes
                    r.receiveGI = ReceiveGI.LightProbes;
                    r.lightProbeUsage = LightProbeUsage.BlendProbes;
                    probe++;
                }
            }
            int reimported = 0;
            foreach (var path in needUv)
            {
                var imp = AssetImporter.GetAtPath(path) as ModelImporter;
                if (imp == null || imp.generateSecondaryUV) continue;
                imp.generateSecondaryUV = true;
                imp.secondaryUVHardAngle = 70f;
                imp.secondaryUVPackMargin = 8f;
                imp.SaveAndReimport();
                reimported++;
            }
            foreach (var t in Object.FindObjectsByType<Terrain>())
            {
                GameObjectUtility.SetStaticEditorFlags(t.gameObject, GameObjectUtility.GetStaticEditorFlags(t.gameObject) | StaticEditorFlags.ContributeGI);
                var so = new SerializedObject(t);
                var p = so.FindProperty("m_ScaleInLightmap");
                if (p != null) { p.floatValue = 0.55f; so.ApplyModifiedPropertiesWithoutUndo(); }
            }
            log.AppendLine("lightmapped renderers: " + lm + " (lightmap UVs generated for " + reimported + " models), probe-lit casters: " + probe + ", excluded water: " + off);
        }

        // ---------- probes ----------
        static void Probes(System.Text.StringBuilder log)
        {
            var old = GameObject.Find("LightProbes");
            if (old) Object.DestroyImmediate(old);
            var go = new GameObject("LightProbes");
            var grp = go.AddComponent<LightProbeGroup>();
            var pts = new List<Vector3>();
            void AddColumn(Vector3 p)
            {
                float g = LGeo.Ground(p);
                pts.Add(new Vector3(p.x, g + 0.7f, p.z));
                pts.Add(new Vector3(p.x, g + 3.2f, p.z));
            }
            for (int arm = 0; arm < 2; arm++)
                for (float t = LGeo.ArenaRadius - 4f; t <= LGeo.ArmLength - 2f; t += 7f)
                {
                    float hw = LGeo.HalfWidthAt(t);
                    for (float lat = -hw; lat <= hw + 0.01f; lat += Mathf.Max(5f, hw / 2.5f))
                        AddColumn(LGeo.ArmPoint(arm, t) + LGeo.ArmRight(arm, t) * lat);
                }
            AddColumn(Vector3.zero);
            for (float r = 6f; r <= LGeo.ArenaRadius; r += 7f)
            {
                int n = Mathf.Max(6, Mathf.RoundToInt(2f * Mathf.PI * r / 7f));
                for (int i = 0; i < n; i++)
                {
                    float a = i * Mathf.PI * 2f / n;
                    AddColumn(new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r));
                }
            }
            grp.probePositions = pts.ToArray();

            var oldR = GameObject.Find("ReflectionProbes");
            if (oldR) Object.DestroyImmediate(oldR);
            var rp = new GameObject("ReflectionProbes");
            int rc = 0;
            void Probe(Vector3 at, Vector3 size)
            {
                var p = new GameObject("Reflection").AddComponent<ReflectionProbe>();
                p.transform.SetParent(rp.transform, false);
                p.transform.position = LGeo.OnGround(at) + Vector3.up * 2.5f;
                p.mode = ReflectionProbeMode.Baked;
                p.size = size;
                p.resolution = 128;
                p.hdr = true;
                p.boxProjection = false;
                p.importance = 1;
                rc++;
            }
            Probe(Vector3.zero, new Vector3(80f, 30f, 80f));
            for (int arm = 0; arm < 2; arm++)
                foreach (float z in LGeo.ZoneMid) Probe(LGeo.ArmPoint(arm, z), new Vector3(46f, 24f, 46f));
            log.AppendLine("light probes: " + pts.Count + ", reflection probes: " + rc);
        }

        // ---------- mist & sunbeams ----------
        static Texture2D SaveTexture(string name, int w, int h, System.Func<float, float, float> alpha)
        {
            string path = ArtDir + "/" + name + ".png";
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    tex.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(alpha(x / (float)(w - 1), y / (float)(h - 1)))));
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.alphaIsTransparency = true;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.mipmapEnabled = true;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static float Fbm(float x, float y)
        {
            float v = 0f, a = 0.5f, f = 1f;
            for (int i = 0; i < 4; i++) { v += a * Mathf.PerlinNoise(x * f + 13.1f * i, y * f + 7.7f * i); a *= 0.5f; f *= 2.1f; }
            return v;
        }

        static Material SaveParticleMaterial(string name, Texture2D tex, Color col, bool additive)
        {
            string path = ArtDir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit")); AssetDatabase.CreateAsset(m, path); }
            m.shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", additive ? 2f : 0f);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)(additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Cull", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            if (additive) m.EnableKeyword("_BLENDMODE_ADD"); else m.DisableKeyword("_BLENDMODE_ADD");
            m.renderQueue = 3000;
            m.SetTexture("_BaseMap", tex);
            m.SetColor("_BaseColor", col);
            EditorUtility.SetDirty(m);
            return m;
        }

        static void Atmosphere(System.Text.StringBuilder log)
        {
            var mistTex = SaveTexture("MistPuff", 128, 64, (u, v) =>
            {
                float edgeX = Mathf.SmoothStep(0f, 1f, Mathf.Min(u, 1f - u) * 3.2f);
                float bottom = Mathf.SmoothStep(0f, 1f, v * 4f);
                float top = Mathf.SmoothStep(0f, 1f, (1f - v) * 1.6f);
                float n = Fbm(u * 3f, v * 2f);
                return edgeX * bottom * top * Mathf.Lerp(0.35f, 1.1f, n);
            });
            var beamTex = SaveTexture("Sunbeam", 64, 128, (u, v) =>
            {
                float across = Mathf.Pow(Mathf.Sin(Mathf.PI * u), 2.2f);
                float streaks = 0.65f + 0.35f * Mathf.PerlinNoise(u * 9f, 0.5f);
                float along = Mathf.SmoothStep(0f, 1f, v * 3f) * Mathf.SmoothStep(0f, 1f, (1f - v) * 1.4f);
                return across * streaks * along;
            });
            var mistMat = SaveParticleMaterial("Mist", mistTex, new Color(0.86f, 0.9f, 0.88f, 1f), false);
            var beamMat = SaveParticleMaterial("Sunbeam", beamTex, new Color(1f, 0.88f, 0.66f, 1f), true);

            var old = GameObject.Find("Atmosphere");
            if (old) Object.DestroyImmediate(old);
            var root = new GameObject("Atmosphere");
            var mist = root.AddComponent<MistField>();
            mist.material = mistMat;

            // sunbeams leaning along the sun direction, scattered over the corridor
            var beams = new GameObject("Sunbeams").transform;
            beams.SetParent(root.transform, false);
            Vector3 toSun = -(Quaternion.Euler(SunEuler) * Vector3.forward);
            var quadMesh = Resources.GetBuiltinResource<UnityEngine.Mesh>("Quad.fbx");
            var rng = new System.Random(11);
            int count = 0;
            for (int arm = 0; arm < 2; arm++)
                for (float t = 45f; t < 155f; t += 9f)
                {
                    float hw = LGeo.HalfWidthAt(t);
                    float lat = ((float)rng.NextDouble() * 2f - 1f) * hw * 0.8f;
                    var p = LGeo.OnGround(LGeo.ArmPoint(arm, t) + LGeo.ArmRight(arm, t) * lat);
                    AddBeam(beams, p, toSun, quadMesh, beamMat, 2.5f + 3f * (float)rng.NextDouble(), 22f + 10f * (float)rng.NextDouble());
                    count++;
                }
            for (int i = 0; i < 6; i++)
            {
                float a = i * 60f * Mathf.Deg2Rad;
                AddBeam(beams, LGeo.OnGround(new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 18f), toSun, quadMesh, beamMat, 4f, 30f);
                count++;
            }
            log.AppendLine("atmosphere: mist field + " + count + " sunbeams");
        }

        static void AddBeam(Transform parent, Vector3 ground, Vector3 toSun, UnityEngine.Mesh quad, Material mat, float width, float length)
        {
            var b = new GameObject("Sunbeam");
            b.transform.SetParent(parent, false);
            b.transform.position = ground - Vector3.up * 0.5f;
            b.transform.rotation = Quaternion.FromToRotation(Vector3.up, toSun);
            var q = new GameObject("Quad");
            q.transform.SetParent(b.transform, false);
            q.transform.localPosition = new Vector3(0, length * 0.5f, 0);
            q.transform.localScale = new Vector3(width, length, 1f);
            q.AddComponent<MeshFilter>().sharedMesh = quad;
            var r = q.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            b.AddComponent<Sunbeam>();
        }

        // ---------- colour grading ----------
        static void Grading(System.Text.StringBuilder log)
        {
            var vol = Object.FindObjectsByType<Volume>().FirstOrDefault(v => v.isGlobal);
            if (vol == null || vol.sharedProfile == null) { log.AppendLine("no global volume"); return; }
            var p = vol.sharedProfile;
            T Get<T>() where T : VolumeComponent { if (!p.TryGet<T>(out var c)) c = p.Add<T>(true); c.active = true; return c; }

            var tm = Get<Tonemapping>(); tm.mode.Override(TonemappingMode.ACES);
            var ca = Get<ColorAdjustments>();
            ca.postExposure.Override(0.25f);
            ca.contrast.Override(8f);
            ca.saturation.Override(-6f);
            ca.colorFilter.Override(new Color(1f, 0.98f, 0.95f));
            var wb = Get<WhiteBalance>(); wb.temperature.Override(-6f); wb.tint.Override(3f);
            var st = Get<SplitToning>();
            st.shadows.Override(new Color(0.36f, 0.52f, 0.55f));
            st.highlights.Override(new Color(0.62f, 0.55f, 0.44f));
            st.balance.Override(-15f);
            var lgg = Get<LiftGammaGain>();
            lgg.lift.Override(new Vector4(0.97f, 1.0f, 1.02f, 0.02f));
            lgg.gamma.Override(new Vector4(1f, 1f, 1f, 0f));
            lgg.gain.Override(new Vector4(1.02f, 1f, 0.97f, 0f));
            var bl = Get<Bloom>();
            bl.threshold.Override(0.95f);
            bl.intensity.Override(0.55f);
            bl.scatter.Override(0.78f);
            bl.tint.Override(new Color(1f, 0.92f, 0.82f));
            var vg = Get<Vignette>(); vg.intensity.Override(0.24f); vg.smoothness.Override(0.5f);
            EditorUtility.SetDirty(p);
            log.AppendLine("grading: ACES, cool shadows / warm highlights, soft bloom");
        }

        // ---------- bake settings ----------
        static void Settings(System.Text.StringBuilder log)
        {
            string path = "Assets/_Relict/Scenes/Prototype_Lighting.lighting";
            var ls = AssetDatabase.LoadAssetAtPath<LightingSettings>(path);
            if (ls == null) { ls = new LightingSettings { name = "Prototype_Lighting" }; AssetDatabase.CreateAsset(ls, path); }
            ls.bakedGI = true;
            ls.realtimeGI = false;
            ls.lightmapper = LightingSettings.Lightmapper.ProgressiveGPU;
            ls.mixedBakeMode = MixedLightingMode.Shadowmask;
            ls.lightmapResolution = 2.5f;
            ls.lightmapMaxSize = 2048;
            ls.lightmapPadding = 3;
            ls.lightmapCompression = LightmapCompression.NormalQuality;
            ls.directionalityMode = LightmapsMode.NonDirectional;
            ls.ao = true;
            ls.aoMaxDistance = 1.4f;
            ls.aoExponentIndirect = 1f;
            ls.aoExponentDirect = 0.2f;
            ls.directSampleCount = 32;
            ls.indirectSampleCount = 192;
            ls.environmentSampleCount = 128;
            ls.maxBounces = 2;
            ls.filteringMode = LightingSettings.FilterMode.Auto;
            ls.indirectScale = 1.1f;
            ls.albedoBoost = 1.1f;
            EditorUtility.SetDirty(ls);
            Lightmapping.lightingSettings = ls;
            QualitySettings.shadowmaskMode = ShadowmaskMode.DistanceShadowmask;
            log.AppendLine("bake: GPU progressive, shadowmask, 2.5 texels/unit, AO, 2 bounces");
        }
    }
}
