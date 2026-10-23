using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Relict.EditorTools
{
    // Slices the heroes' long authored clips into gameplay pieces:
    //  * JumpUp / JumpAir / JumpLand (with the in-place hop removed from the airborne part,
    //    so the physics jump is the only thing lifting the character)
    //  * gecko bow Draw / Hold / Release, played on an upper-body layer so the legs keep walking.
    public static class HeroAnimBuilder
    {
        const string OutDir = "Assets/_Relict/Animation/Heroes";
        const float Fps = 30f;

        struct JumpSpec { public float upA, upB, air, landA, landB; }

        [MenuItem("Tools/Relict/Build Hero Animations")]
        public static void Build()
        {
            if (!AssetDatabase.IsValidFolder(OutDir)) AssetDatabase.CreateFolder("Assets/_Relict/Animation", "Heroes");
            var mm = Object.FindAnyObjectByType<MatchManager>();
            if (mm == null) { Debug.LogError("Relict: MatchManager not found in the open scene"); return; }

            BuildHero(mm.frogPrefab, "Frog", "sword_jump", new JumpSpec { upA = 0.48f, upB = 0.88f, air = 0.9f, landA = 1.25f, landB = 2.0f }, false);
            BuildHero(mm.geckoPrefab, "Gecko", "bow_jump", new JumpSpec { upA = 0.48f, upB = 0.85f, air = 0.88f, landA = 1.15f, landB = 2.0f }, true);
            AssetDatabase.SaveAssets();
            Debug.Log("Relict: hero animations built");
        }

        static void BuildHero(GameObject prefab, string name, string jumpClip, JumpSpec js, bool bow)
        {
            var inst = (GameObject)Object.Instantiate(prefab);
            try
            {
                var an = inst.GetComponentInChildren<Animator>();
                var model = an.gameObject;
                var ctrl = (AnimatorController)an.runtimeAnimatorController;
                var src = ctrl.animationClips.First(c => c.name == jumpClip);
                var hips = inst.GetComponentsInChildren<Transform>(true).First(t => t.name == "DEF-spine");

                var up = Slice(src, js.upA, js.upB, model, hips, true, false, name + "_JumpUp");
                var air = Slice(src, js.air, js.air + 1f, model, hips, true, true, name + "_JumpAir");
                var land = Slice(src, js.landA, js.landB, model, hips, false, false, name + "_JumpLand");
                SetLoop(air, true);

                var sm = ctrl.layers[0].stateMachine;
                bool wd = sm.states.Length > 0 ? sm.states[0].state.writeDefaultValues : true;
                SetState(sm, "JumpUp", up, wd, new Vector3(500, 300));
                SetState(sm, "JumpAir", air, wd, new Vector3(500, 360));
                SetState(sm, "JumpLand", land, wd, new Vector3(500, 420));

                if (bow)
                {
                    var shoot = ctrl.animationClips.First(c => c.name == "bow_shoot");
                    var draw = Slice(shoot, 3.55f, 5.1f, model, hips, false, false, name + "_BowDraw");
                    var hold = Slice(shoot, 5.6f, 6.6f, model, hips, false, true, name + "_BowHold");
                    var rel = Slice(shoot, 6.95f, 8.0f, model, hips, false, false, name + "_BowRelease");
                    SetLoop(hold, true);
                    var mask = BuildUpperMask(model, name);
                    BuildUpperLayer(ctrl, mask, draw, hold, rel);
                }
                EditorUtility.SetDirty(ctrl);
            }
            finally { Object.DestroyImmediate(inst); }
        }

        // Copies [a,b] of src into a new clip resampled at 30 fps. removeHop strips upward hip travel
        // (applied on the top "rig" node), constant freezes the pose at time a.
        static AnimationClip Slice(AnimationClip src, float a, float b, GameObject model, Transform hips, bool removeHop, bool constant, string clipName)
        {
            float len = b - a;
            int frames = Mathf.Max(2, Mathf.RoundToInt(len * Fps) + 1);
            var clip = new AnimationClip { name = clipName, frameRate = Fps };

            // hip lift relative to the clip's rest pose (first frame)
            var dy = new float[frames];
            Transform rig = model.transform.Find("rig");
            if (removeHop && rig != null)
            {
                src.SampleAnimation(model, 0f);
                float rest = hips.position.y;
                for (int i = 0; i < frames; i++)
                {
                    float t = constant ? a : a + len * i / (frames - 1);
                    src.SampleAnimation(model, t);
                    dy[i] = Mathf.Max(0f, hips.position.y - rest);
                }
            }

            var bindings = AnimationUtility.GetCurveBindings(src);
            bool rigPosFound = false;
            foreach (var bnd in bindings)
            {
                var curve = AnimationUtility.GetEditorCurve(src, bnd);
                if (curve == null) continue;
                bool rigY = removeHop && rig != null && bnd.path == "rig" && bnd.type == typeof(Transform) && bnd.propertyName.StartsWith("m_LocalPosition.");
                if (rigY) rigPosFound = true;
                var keys = new Keyframe[frames];
                for (int i = 0; i < frames; i++)
                {
                    float lt = len * i / (frames - 1);
                    float v = curve.Evaluate(constant ? a : a + lt);
                    if (rigY) v -= HopComponent(model.transform, bnd.propertyName, dy[i]);
                    keys[i] = new Keyframe(lt, v);
                }
                var nc = new AnimationCurve(keys);
                for (int i = 0; i < frames; i++) AnimationUtility.SetKeyLeftTangentMode(nc, i, AnimationUtility.TangentMode.ClampedAuto);
                for (int i = 0; i < frames; i++) AnimationUtility.SetKeyRightTangentMode(nc, i, AnimationUtility.TangentMode.ClampedAuto);
                AnimationUtility.SetEditorCurve(clip, bnd, nc);
            }
            if (removeHop && rig != null && !rigPosFound)
            {
                // rig has no authored position: add one so the hop can be cancelled
                Vector3 basePos = rig.localPosition;
                string[] axes = { "x", "y", "z" };
                for (int ax = 0; ax < 3; ax++)
                {
                    var keys = new Keyframe[frames];
                    for (int i = 0; i < frames; i++)
                        keys[i] = new Keyframe(len * i / (frames - 1), basePos[ax] - HopComponent(model.transform, "m_LocalPosition." + axes[ax], dy[i]));
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("rig", typeof(Transform), "m_LocalPosition." + axes[ax]), new AnimationCurve(keys));
                }
            }

            string path = OutDir + "/" + clipName + ".anim";
            var old = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (old)
            {
                old.ClearCurves();
                EditorUtility.CopySerialized(clip, old);
                old.name = clipName;
                return old;
            }
            AssetDatabase.CreateAsset(clip, path);
            return clip;
        }

        // How much of a world-space upward offset of dy lands on one local-position axis of the rig node.
        static float HopComponent(Transform model, string prop, float dy)
        {
            if (dy == 0f) return 0f;
            Vector3 local = model.InverseTransformVector(new Vector3(0f, dy, 0f));
            char ax = prop[prop.Length - 1];
            return ax == 'x' ? local.x : ax == 'y' ? local.y : local.z;
        }

        static void SetLoop(AnimationClip clip, bool loop)
        {
            var s = AnimationUtility.GetAnimationClipSettings(clip);
            s.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(clip, s);
        }

        static AnimatorState SetState(AnimatorStateMachine sm, string name, Motion motion, bool wd, Vector3 pos)
        {
            var st = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == name);
            if (st == null) st = sm.AddState(name, pos);
            st.motion = motion;
            st.writeDefaultValues = wd;
            return st;
        }

        static readonly string[] UpperKeys = {
            "shoulder", "upper_arm", "forearm", "hand", "palm", "f_index", "f_ring", "f_pinky", "f_middle", "thumb",
            "prop_bow", "prop_string", "prop_arrow", "spine.003", "spine.004", "spine.005", "spine.006", "spine_fk.003" };

        static AvatarMask BuildUpperMask(GameObject model, string name)
        {
            string path = OutDir + "/" + name + "_Upper.mask";
            var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(path);
            if (mask == null) { mask = new AvatarMask(); AssetDatabase.CreateAsset(mask, path); }
            var all = model.GetComponentsInChildren<Transform>(true).Where(t => t != model.transform).ToList();
            mask.transformCount = all.Count;
            for (int i = 0; i < all.Count; i++)
            {
                string p = AnimationUtility.CalculateTransformPath(all[i], model.transform);
                mask.SetTransformPath(i, p);
                string n = all[i].name;
                mask.SetTransformActive(i, UpperKeys.Any(k => n.Contains(k)));
            }
            EditorUtility.SetDirty(mask);
            return mask;
        }

        static void BuildUpperLayer(AnimatorController ctrl, AvatarMask mask, Motion draw, Motion hold, Motion rel)
        {
            if (!ctrl.parameters.Any(p => p.name == "UpperSpeed")) ctrl.AddParameter("UpperSpeed", AnimatorControllerParameterType.Float);
            var layers = ctrl.layers;
            int idx = System.Array.FindIndex(layers, l => l.name == "Upper");
            if (idx < 0)
            {
                ctrl.AddLayer("Upper");
                layers = ctrl.layers;
                idx = layers.Length - 1;
            }
            var layer = layers[idx];
            layer.avatarMask = mask;
            layer.defaultWeight = 0f;
            layer.blendingMode = AnimatorLayerBlendingMode.Override;
            var sm = layer.stateMachine;
            var empty = SetState(sm, "Empty", null, false, new Vector3(250, 100));
            sm.defaultState = empty;
            foreach (var pair in new[] { ("Draw", draw), ("Hold", hold), ("Release", rel) })
            {
                var st = SetState(sm, pair.Item1, pair.Item2, false, new Vector3(250, 160 + 60 * System.Array.IndexOf(new[] { "Draw", "Hold", "Release" }, pair.Item1)));
                st.speedParameterActive = true;
                st.speedParameter = "UpperSpeed";
            }
            layers[idx] = layer;
            ctrl.layers = layers;
            var p0 = ctrl.parameters;
            for (int i = 0; i < p0.Length; i++) if (p0[i].name == "UpperSpeed") p0[i].defaultFloat = 1f;
            ctrl.parameters = p0;
        }
    }
}
