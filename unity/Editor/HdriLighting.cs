using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Relict.EditorTools
{
    // Lighting matched to an HDRI: the HDRI becomes the sky, ambient and reflections; the sun's direction,
    // colour and strength are measured from the image so the realtime/baked sun lines up with the sun in the sky.
    public static class HdriLighting
    {
        const string SourceDir = "C:/Users/AndreyD/Documents/relict/asset/hdri";
        const string SkyDir = "Assets/_Relict/Art/Sky";
        const float SunClamp = 24f;          // radiance the sun disc is clamped to in the sky texture (keeps ambient sane)
        const float TargetAmbient = 0.55f;   // diffuse sky irradiance / pi the scene is normalised to
        const float SunWorldAzimuth = 45f;   // put the sun over +X +Z: in front of the players on both arms

        [MenuItem("Tools/Relict/Lighting/HDRI Setup + Bake")]
        public static void SetupAndBakeMenu() { Setup(); LightingBuilder.Bake(); }

        [MenuItem("Tools/Relict/Lighting/HDRI Setup")]
        public static void SetupMenu() { Setup(); }

        public static string Setup()
        {
            var log = new System.Text.StringBuilder();
            // shared scene setup: static GI flags, lightmap UVs, probes, mist/sunbeam objects, bake settings
            log.Append(LightingBuilder.Setup());

            if (!AssetDatabase.IsValidFolder(SkyDir)) AssetDatabase.CreateFolder("Assets/_Relict/Art", "Sky");
            var src = Directory.GetFiles(SourceDir).FirstOrDefault(f => f.EndsWith(".hdr", System.StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exr", System.StringComparison.OrdinalIgnoreCase));
            if (src == null) { log.AppendLine("no HDRI in " + SourceDir); return log.ToString(); }
            string name = Path.GetFileNameWithoutExtension(src);
            string rawPath = SkyDir + "/" + Path.GetFileName(src);
            if (!File.Exists(rawPath)) File.Copy(src, rawPath);
            AssetDatabase.ImportAsset(rawPath);

            // --- read the HDRI at full precision ---
            var imp = (TextureImporter)AssetImporter.GetAtPath(rawPath);
            imp.textureShape = TextureImporterShape.Texture2D;
            imp.isReadable = true;
            imp.mipmapEnabled = false;
            imp.sRGBTexture = false;
            imp.maxTextureSize = 8192;
            imp.textureCompression = TextureImporterCompression.Uncompressed;
            imp.SaveAndReimport();
            var raw = AssetDatabase.LoadAssetAtPath<Texture2D>(rawPath);
            int W = raw.width, H = raw.height;

            // pass 1: brightest pixel = sun
            float best = 0f; int sx = 0, sy = 0;
            const int stripe = 64;
            for (int y0 = 0; y0 < H; y0 += stripe)
            {
                int h = Mathf.Min(stripe, H - y0);
                var px = raw.GetPixels(0, y0, W, h);
                for (int i = 0; i < px.Length; i++)
                {
                    float l = Lum(px[i]);
                    if (l > best) { best = l; sx = i % W; sy = y0 + i / W; }
                }
            }
            Vector3 sunTex = DirFromUV((sx + 0.5f) / W, (sy + 0.5f) / H);

            // pass 2: sun energy (everything above the clamp near the sun), sky irradiance, horizon colour
            Color sunSum = Color.black; double skyE = 0; Color horizon = Color.black; int horizonN = 0;
            var clamped = new Texture2D(W, H, TextureFormat.RGBAHalf, false, true);
            float dTheta = Mathf.PI / H, dPhi = 2f * Mathf.PI / W;
            for (int y0 = 0; y0 < H; y0 += stripe)
            {
                int h = Mathf.Min(stripe, H - y0);
                var px = raw.GetPixels(0, y0, W, h);
                for (int i = 0; i < px.Length; i++)
                {
                    int x = i % W, y = y0 + i / W;
                    float v = (y + 0.5f) / H;
                    float theta = (1f - v) * Mathf.PI;                 // from zenith
                    float dOmega = Mathf.Sin(theta) * dTheta * dPhi;
                    var c = px[i];
                    float l = Lum(c);
                    if (l > SunClamp)
                    {
                        var dir = DirFromUV((x + 0.5f) / W, v);
                        if (Vector3.Angle(dir, sunTex) < 6f) sunSum += (c - c * (SunClamp / l)) * dOmega;
                        c *= SunClamp / l;
                        px[i] = new Color(c.r, c.g, c.b, 1f);
                    }
                    if (theta < Mathf.PI * 0.5f) skyE += Lum(c) * Mathf.Cos(theta) * dOmega;
                    if (v > 0.49f && v < 0.54f) { horizon += c; horizonN++; }
                }
                clamped.SetPixels(0, y0, W, h, px);
            }
            clamped.Apply(false);
            horizon /= Mathf.Max(1, horizonN);
            string skyPath = SkyDir + "/" + name + "_sky.exr";
            File.WriteAllBytes(skyPath, clamped.EncodeToEXR(Texture2D.EXRFlags.CompressZIP));
            Object.DestroyImmediate(clamped);

            // raw copy no longer needs to be readable / uncompressed (it stays only as the source)
            imp.isReadable = false;
            imp.textureCompression = TextureImporterCompression.CompressedHQ;
            imp.maxTextureSize = 2048;
            imp.SaveAndReimport();

            AssetDatabase.ImportAsset(skyPath);
            var simp = (TextureImporter)AssetImporter.GetAtPath(skyPath);
            simp.textureShape = TextureImporterShape.Texture2D;
            simp.sRGBTexture = false;
            simp.mipmapEnabled = false;                 // no mips: avoids the seam of a lat-long panorama
            simp.wrapModeU = TextureWrapMode.Repeat;
            simp.wrapModeV = TextureWrapMode.Clamp;
            simp.maxTextureSize = 4096;
            simp.textureCompression = TextureImporterCompression.CompressedHQ;
            simp.SaveAndReimport();
            var skyTex = AssetDatabase.LoadAssetAtPath<Texture2D>(skyPath);

            // --- calibration ---
            float k = TargetAmbient / Mathf.Max(1e-4f, (float)skyE / Mathf.PI);          // global exposure
            float sunLum = Lum(sunSum);
            Color sunCol = sunLum > 0f ? sunSum / Mathf.Max(sunSum.r, Mathf.Max(sunSum.g, sunSum.b)) : new Color(1f, 0.95f, 0.88f);
            float sunI = Mathf.Clamp(k * sunLum / Mathf.PI, 1.2f, 5f);
            if (sunLum <= 0f) sunI = 2.2f;

            // rotate the sky so the sun stands where we want it in the world
            float texAz = Mathf.Atan2(sunTex.z, sunTex.x) * Mathf.Rad2Deg;
            // Skybox/Panoramic rotates the sky mesh (not the lookup): world = RotY(+rot) * texture direction
            float rot = Mathf.Repeat(SunWorldAzimuth - texAz, 360f);
            float a = rot * Mathf.Deg2Rad;
            Vector3 sunWorld = new Vector3(Mathf.Cos(a) * sunTex.x - Mathf.Sin(a) * sunTex.z, sunTex.y, Mathf.Sin(a) * sunTex.x + Mathf.Cos(a) * sunTex.z).normalized;
            float elevation = Mathf.Asin(Mathf.Clamp(sunWorld.y, -1f, 1f)) * Mathf.Rad2Deg;

            // --- sky material ---
            string matPath = SkyDir + "/HdriSky.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null) { mat = new Material(Shader.Find("Skybox/Panoramic")); AssetDatabase.CreateAsset(mat, matPath); }
            mat.shader = Shader.Find("Skybox/Panoramic");
            mat.SetTexture("_MainTex", skyTex);
            mat.SetFloat("_Mapping", 1f);
            mat.SetFloat("_ImageType", 0f);
            mat.SetFloat("_Rotation", rot);
            mat.SetFloat("_Exposure", k);
            mat.SetColor("_Tint", new Color(0.5f, 0.5f, 0.5f, 1f));
            EditorUtility.SetDirty(mat);
            RenderSettings.skybox = mat;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity = 1f;

            // --- sun ---
            var sun = Object.FindObjectsByType<Light>().First(l => l.type == LightType.Directional);
            sun.transform.rotation = Quaternion.LookRotation(-sunWorld);
            sun.color = sunCol;
            sun.intensity = sunI;
            RenderSettings.sun = sun;

            // --- haze in the colour of the HDRI horizon, lighter than the misty preset ---
            var fogCol = horizon * k;
            float fm = Mathf.Max(fogCol.r, Mathf.Max(fogCol.g, fogCol.b));
            if (fm > 1f) fogCol /= fm;
            RenderSettings.fogColor = new Color(fogCol.r, fogCol.g, fogCol.b, 1f);
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.0075f;

            // --- atmosphere: subtle ground haze, beams follow the new sun ---
            var mist = Object.FindAnyObjectByType<MistField>();
            if (mist) { mist.alpha = 0.12f; mist.material.SetColor("_BaseColor", Color.Lerp(RenderSettings.fogColor, Color.white, 0.35f)); EditorUtility.SetDirty(mist); EditorUtility.SetDirty(mist.material); }
            foreach (var b in Object.FindObjectsByType<Sunbeam>())
            {
                b.transform.rotation = Quaternion.FromToRotation(Vector3.up, sunWorld);
                b.alpha = 0.045f;
                EditorUtility.SetDirty(b);
            }

            // --- grading: neutral, true to the HDRI ---
            var vol = Object.FindObjectsByType<Volume>().FirstOrDefault(v => v.isGlobal);
            if (vol && vol.sharedProfile)
            {
                var p = vol.sharedProfile;
                if (p.TryGet<SplitToning>(out var st)) st.active = false;
                if (p.TryGet<WhiteBalance>(out var wb)) { wb.temperature.Override(0f); wb.tint.Override(0f); }
                if (p.TryGet<LiftGammaGain>(out var lgg)) lgg.active = false;
                if (p.TryGet<ColorAdjustments>(out var ca)) { ca.postExposure.Override(0.1f); ca.contrast.Override(10f); ca.saturation.Override(6f); ca.colorFilter.Override(Color.white); }
                if (p.TryGet<Bloom>(out var bl)) { bl.threshold.Override(1.0f); bl.intensity.Override(0.4f); bl.tint.Override(Color.white); }
                EditorUtility.SetDirty(p);
            }

            // --- sharper bake ---
            var ls = Lightmapping.lightingSettings;
            ls.lightmapResolution = 4f;
            EditorUtility.SetDirty(ls);
            foreach (var t in Object.FindObjectsByType<Terrain>())
            {
                var so = new SerializedObject(t);
                so.FindProperty("m_ScaleInLightmap").floatValue = 1f;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(t);
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            AssetDatabase.SaveAssets();

            log.AppendLine("HDRI " + name + " " + W + "x" + H + ": sun at pixel (" + sx + "," + sy + ") peak " + best.ToString("0") +
                           ", elevation " + elevation.ToString("0.0") + "°, sky rotated " + rot.ToString("0") + "°");
            log.AppendLine("calibration: exposure " + k.ToString("0.000") + ", sun intensity " + sunI.ToString("0.00") + " colour " + sunCol + ", sky irradiance/pi " + ((float)skyE / Mathf.PI).ToString("0.00"));
            log.AppendLine("fog colour " + RenderSettings.fogColor + ", lightmaps 4 texels/unit, terrain scale 1");
            Debug.Log("Relict HDRI lighting:\n" + log);
            return log.ToString();
        }

        static float Lum(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        // Inverse of Skybox/Panoramic's lat-long mapping: u = 0.5 - atan2(z,x)/2pi, v = 1 - acos(y)/pi
        static Vector3 DirFromUV(float u, float v)
        {
            float lon = (0.5f - u) * 2f * Mathf.PI;
            float theta = (1f - v) * Mathf.PI;
            return new Vector3(Mathf.Sin(theta) * Mathf.Cos(lon), Mathf.Cos(theta), Mathf.Sin(theta) * Mathf.Sin(lon));
        }
    }
}
