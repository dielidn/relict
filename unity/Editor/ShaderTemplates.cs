using UnityEditor;
using UnityEngine;

namespace Relict.EditorTools
{
    // Materials in Resources keep the shaders (and keyword variants) used by runtime-created materials in builds.
    public static class ShaderTemplates
    {
        const string Dir = "Assets/_Relict/Resources/RelictShaders";

        [MenuItem("Tools/Relict/Build Shader Templates")]
        public static void Build()
        {
            if (!AssetDatabase.IsValidFolder("Assets/_Relict/Resources")) AssetDatabase.CreateFolder("Assets/_Relict", "Resources");
            if (!AssetDatabase.IsValidFolder(Dir)) AssetDatabase.CreateFolder("Assets/_Relict/Resources", "RelictShaders");

            var lit = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            Save(lit, "Lit");

            var em = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            em.EnableKeyword("_EMISSION");
            em.SetColor("_EmissionColor", Color.white);
            em.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            Save(em, "LitEmissive");

            var add = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));
            add.SetFloat("_Surface", 1f);
            add.SetFloat("_Blend", 2f);
            add.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            add.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            add.SetFloat("_ZWrite", 0f);
            add.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            add.EnableKeyword("_BLENDMODE_ADD");
            add.renderQueue = 3000;
            Save(add, "Additive");

            AssetDatabase.SaveAssets();
            Debug.Log("Relict: shader templates built in " + Dir);
        }

        static void Save(Material m, string name)
        {
            string path = Dir + "/" + name + ".mat";
            var old = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (old) { EditorUtility.CopySerialized(m, old); EditorUtility.SetDirty(old); }
            else AssetDatabase.CreateAsset(m, path);
        }
    }
}
