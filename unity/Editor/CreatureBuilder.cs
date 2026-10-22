#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// Imports the rigged creatures, builds URP materials, bakes procedural clips (Idle / Walk / Attack / Death)
// into real AnimationClips, creates Animator controllers and prefabs.
// Menu: Tools > Relict > Build Creatures
public static class CreatureBuilder
{
    const string Root = "Assets/_Relict/Art/Creatures";
    const string AnimDir = "Assets/_Relict/Animation/Creatures";
    const string PrefabDir = "Assets/_Relict/Prefabs/Creatures";
    const int Fps = 30;

    // ---------- pose helper ----------
    class Pose
    {
        public Transform model;
        public Vector3 fwd, up = Vector3.up, right;
        public Vector3 center;
        public readonly Dictionary<string, Transform> b = new Dictionary<string, Transform>();
        public readonly Dictionary<Transform, Quaternion> restRot = new Dictionary<Transform, Quaternion>();
        public readonly Dictionary<Transform, Vector3> restPos = new Dictionary<Transform, Vector3>();
        public List<Transform> order = new List<Transform>();

        public Pose(GameObject inst, Vector3 forward)
        {
            model = inst.transform;
            fwd = forward.normalized;
            right = Vector3.Cross(up, fwd);
            var smr = inst.GetComponentInChildren<SkinnedMeshRenderer>();
            foreach (var t in inst.GetComponentsInChildren<Transform>())
            {
                if (!b.ContainsKey(t.name)) b[t.name] = t;
                restRot[t] = t.localRotation;
                restPos[t] = t.localPosition;
            }
            order = smr.bones.Where(x => x != null).OrderBy(Depth).ToList();
            var root = smr.rootBone ? smr.rootBone : order[0];
            if (!order.Contains(root)) order.Insert(0, root);
            center = smr.bounds.center;
        }

        int Depth(Transform t) { int d = 0; while (t != null && t != model) { d++; t = t.parent; } return d; }

        public void Reset() { foreach (var kv in restRot) { kv.Key.localRotation = kv.Value; kv.Key.localPosition = restPos[kv.Key]; } }

        public Transform T(string name) => b.TryGetValue(name, out var t) ? t : null;

        public float Side(Transform t) => Mathf.Sign(Vector3.Dot(t.position - center, right) + 1e-5f);

        public void Rot(string name, Vector3 axis, float deg) { var t = T(name); if (t) t.rotation = Quaternion.AngleAxis(deg, axis) * t.rotation; }
        public void Pitch(string name, float deg) => Rot(name, right, deg);   // + = tip forward-down
        public void Yaw(string name, float deg) => Rot(name, up, deg);
        public void Roll(string name, float deg) => Rot(name, fwd, deg);      // + = right side up
        public void Move(string name, Vector3 world) { var t = T(name); if (t) t.position += world; }
    }

    static float S(float x) => Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(x));
    // rear-back amount and strike amount over an attack (k = 0..1, strike at ~0.6)
    static float Rear(float k) => k < 0.55f ? S(k / 0.55f) : k < 0.68f ? 1f - (k - 0.55f) / 0.13f : 0f;
    static float Strike(float k) => k < 0.55f ? 0f : k < 0.68f ? (k - 0.55f) / 0.13f : 1f - S((k - 0.68f) / 0.32f);

    // ---------- creature specs ----------
    class Spec
    {
        public string name, displayName;
        public Vector3 forward = Vector3.forward;
        public float modelYaw;        // rotation applied in the prefab so the creature faces +Z
        public float idleLen = 2f, walkLen = 0.8f, attackLen = 0.8f, deathLen = 1.2f;
        public System.Action<Pose, string, float, float> pose;   // pose, clip, time, normalized
    }

    static readonly string[] AntA = { "coxa.F.L", "coxa.M.R", "coxa.H.L" }, AntB = { "coxa.F.R", "coxa.M.L", "coxa.H.R" };

    static void InsectLegs(Pose p, string[] a, string[] bGroup, string clip, float t, float len, float amp, float lift)
    {
        float ph = 2f * Mathf.PI * t / len;
        int i = 0;
        foreach (var grp in new[] { a, bGroup })
        {
            foreach (var coxa in grp)
            {
                var c = p.T(coxa);
                if (c == null) continue;
                float s = p.Side(c);
                string femur = coxa.Replace("coxa", "femur");
                if (clip == "Walk")
                {
                    float q = ph + (grp == bGroup ? Mathf.PI : 0f);
                    float up = Mathf.Max(0f, Mathf.Cos(q));
                    p.Yaw(coxa, -s * amp * Mathf.Sin(q));
                    p.Roll(coxa, s * lift * up);
                    p.Roll(femur, s * lift * 0.6f * up);
                }
                else if (clip == "Idle")
                {
                    p.Yaw(coxa, -s * 3f * Mathf.Sin(ph + i * 1.3f));
                    p.Roll(femur, s * 2f * Mathf.Sin(ph * 0.5f + i));
                }
                i++;
            }
        }
    }

    static void InsectDeathLegs(Pose p, IEnumerable<string> coxas, float k)
    {
        float d = S(k / 0.45f);
        float twitch = k > 0.45f ? Mathf.Sin(k * 45f) * (1f - k) * 6f : 0f;
        foreach (var coxa in coxas)
        {
            var c = p.T(coxa);
            if (c == null) continue;
            float s = p.Side(c);
            p.Roll(coxa, -s * 40f * d + twitch);
            p.Roll(coxa.Replace("coxa", "femur"), -s * 35f * d);
            p.Yaw(coxa, s * 12f * d);
        }
    }

    static void Pair(Pose p, string l, string r, System.Action<string, float> f)
    {
        foreach (var n in new[] { l, r }) { var t = p.T(n); if (t) f(n, p.Side(t)); }
    }

    static List<Spec> Specs()
    {
        var list = new List<Spec>();

        // ---- ant ----
        list.Add(new Spec
        {
            name = "Ant", displayName = "Муравей", walkLen = 0.6f,
            pose = (p, clip, t, k) =>
            {
                float ph = 2f * Mathf.PI * t / (clip == "Idle" ? 2f : 0.6f);
                if (clip == "Idle" || clip == "Walk")
                {
                    InsectLegs(p, AntA, AntB, clip, t, clip == "Idle" ? 2f : 0.6f, 24f, 22f);
                    p.Pitch("body", 1.2f * Mathf.Sin(ph));
                    if (clip == "Walk") p.Roll("body", 3f * Mathf.Sin(ph));
                    Pair(p, "antenna_scape.L", "antenna_scape.R", (n, s) => { p.Yaw(n, s * 9f * Mathf.Sin(ph * 2f + s)); p.Pitch(n, -5f * Mathf.Sin(ph * 1.5f)); });
                    Pair(p, "mandible.L", "mandible.R", (n, s) => p.Yaw(n, s * 6f * (0.5f + 0.5f * Mathf.Sin(ph * 3f))));
                    p.Pitch("petiole", 2.5f * Mathf.Sin(ph));
                    if (clip == "Walk") p.Yaw("gaster", 5f * Mathf.Sin(ph));
                }
                else if (clip == "Attack")
                {
                    float r = Rear(k), f = Strike(k);
                    p.Pitch("body", -10f * r + 12f * f);
                    p.Pitch("head", -12f * r + 16f * f);
                    Pair(p, "mandible.L", "mandible.R", (n, s) => p.Yaw(n, s * (32f * r - 6f * f)));
                    Pair(p, "antenna_scape.L", "antenna_scape.R", (n, s) => p.Pitch(n, -14f * r));
                    Pair(p, "coxa.F.L", "coxa.F.R", (n, s) => p.Roll(n, s * 16f * r));
                    p.Pitch("petiole", 8f * r);
                }
                else // Death
                {
                    float d = S(k / 0.45f);
                    InsectDeathLegs(p, AntA.Concat(AntB), k);
                    p.Move("body", Vector3.down * 0.1f * d);
                    p.Roll("body", 14f * d);
                    Pair(p, "antenna_scape.L", "antenna_scape.R", (n, s) => p.Pitch(n, 30f * d));
                    Pair(p, "mandible.L", "mandible.R", (n, s) => p.Yaw(n, s * 22f * d));
                    p.Pitch("petiole", 10f * d);
                }
            }
        });

        // ---- ladybug ----
        list.Add(new Spec
        {
            name = "Ladybug", displayName = "Божья коровка", walkLen = 0.75f,
            pose = (p, clip, t, k) =>
            {
                float ph = 2f * Mathf.PI * t / (clip == "Idle" ? 2f : 0.75f);
                if (clip == "Idle" || clip == "Walk")
                {
                    InsectLegs(p, AntA, AntB, clip, t, clip == "Idle" ? 2f : 0.75f, 18f, 16f);
                    p.Pitch("body", 1f * Mathf.Sin(ph));
                    if (clip == "Walk") p.Roll("body", 2.5f * Mathf.Sin(ph));
                    Pair(p, "antenna_01.L", "antenna_01.R", (n, s) => { p.Yaw(n, s * 8f * Mathf.Sin(ph * 2f + s)); p.Pitch(n, -4f * Mathf.Sin(ph)); });
                    Pair(p, "mandible.L", "mandible.R", (n, s) => p.Yaw(n, s * 5f * (0.5f + 0.5f * Mathf.Sin(ph * 2f))));
                    p.Pitch("abdomen", 1.5f * Mathf.Sin(ph));
                }
                else if (clip == "Attack")
                {
                    float r = Rear(k), f = Strike(k);
                    p.Pitch("body", -9f * r + 12f * f);
                    p.Pitch("head", -10f * r + 14f * f);
                    Pair(p, "mandible.L", "mandible.R", (n, s) => p.Yaw(n, s * (28f * r - 5f * f)));
                    Pair(p, "elytron.L", "elytron.R", (n, s) => p.Roll(n, s * 18f * r));
                    Pair(p, "coxa.F.L", "coxa.F.R", (n, s) => p.Roll(n, s * 12f * r));
                }
                else
                {
                    float d = S(k / 0.45f);
                    InsectDeathLegs(p, AntA.Concat(AntB), k);
                    p.Move("body", Vector3.down * 0.08f * d);
                    p.Roll("body", 12f * d);
                    Pair(p, "elytron.L", "elytron.R", (n, s) => p.Roll(n, s * 35f * d));
                    Pair(p, "antenna_01.L", "antenna_01.R", (n, s) => p.Pitch(n, 25f * d));
                }
            }
        });

        // ---- mantis (faces +X in the file) ----
        string[] mA = { "coxa.M.L", "coxa.H.R" }, mB = { "coxa.M.R", "coxa.H.L" };
        list.Add(new Spec
        {
            name = "Mantis", displayName = "Богомол", forward = Vector3.right, modelYaw = -90f, walkLen = 0.9f, attackLen = 0.9f,
            pose = (p, clip, t, k) =>
            {
                float ph = 2f * Mathf.PI * t / (clip == "Idle" ? 2.2f : 0.9f);
                if (clip == "Idle" || clip == "Walk")
                {
                    InsectLegs(p, mA, mB, clip, t, clip == "Idle" ? 2.2f : 0.9f, 20f, 20f);
                    p.Pitch("body", 1.2f * Mathf.Sin(ph));
                    if (clip == "Walk") p.Roll("body", 2.5f * Mathf.Sin(ph));
                    p.Pitch("prothorax", 2f * Mathf.Sin(ph * 0.5f));
                    p.Yaw("head", 10f * Mathf.Sin(ph * 0.5f));
                    Pair(p, "antenna.01.L", "antenna.01.R", (n, s) => p.Yaw(n, s * 8f * Mathf.Sin(ph * 2f + s)));
                    Pair(p, "r_coxa.L", "r_coxa.R", (n, s) => p.Pitch(n, -6f - 4f * Mathf.Sin(ph + s)));
                    Pair(p, "r_tibia.L", "r_tibia.R", (n, s) => p.Pitch(n, 3f * Mathf.Sin(ph * 2f)));
                    p.Pitch("abdomen", 2f * Mathf.Sin(ph));
                    if (clip == "Walk") p.Yaw("abdomen.001", 4f * Mathf.Sin(ph));
                }
                else if (clip == "Attack")
                {
                    float r = Rear(k), f = Strike(k);
                    p.Pitch("prothorax", -14f * r + 16f * f);
                    p.Pitch("head", -8f * r + 10f * f);
                    Pair(p, "r_coxa.L", "r_coxa.R", (n, s) => p.Pitch(n, -60f * r + 40f * f));
                    Pair(p, "r_femur.L", "r_femur.R", (n, s) => p.Pitch(n, -25f * r + 20f * f));
                    Pair(p, "r_tibia.L", "r_tibia.R", (n, s) => p.Pitch(n, 40f * r - 35f * f));
                    Pair(p, "wing.L", "wing.R", (n, s) => p.Roll(n, s * 30f * r));
                    p.Pitch("body", -6f * r + 8f * f);
                }
                else
                {
                    float d = S(k / 0.45f);
                    InsectDeathLegs(p, mA.Concat(mB), k);
                    p.Move("body", Vector3.down * 0.12f * d);
                    p.Roll("body", 16f * d);
                    Pair(p, "r_coxa.L", "r_coxa.R", (n, s) => p.Pitch(n, 35f * d));
                    Pair(p, "wing.L", "wing.R", (n, s) => p.Roll(n, s * 30f * d));
                    p.Pitch("prothorax", 25f * d);
                }
            }
        });

        // ---- reptile boss (biped) ----
        list.Add(new Spec
        {
            name = "Reptileboss", displayName = "Ящер-вождь", idleLen = 2.4f, walkLen = 1.0f, attackLen = 1.0f, deathLen = 1.5f,
            pose = (p, clip, t, k) =>
            {
                float ph = 2f * Mathf.PI * t / (clip == "Idle" ? 2.4f : 1f);
                if (clip == "Idle" || clip == "Walk")
                {
                    bool walk = clip == "Walk";
                    p.Pitch("DEF-spine.001", 1.5f * Mathf.Sin(ph));
                    p.Yaw("DEF-spine.006", (walk ? 4f : 7f) * Mathf.Sin(ph * 0.5f));
                    p.Pitch("DEF-jaw", 3f * Mathf.Max(0f, Mathf.Sin(ph * 0.5f)));
                    for (int i = 7; i <= 18; i++) p.Yaw("DEF-spine." + i.ToString("000"), (walk ? 7f : 4f) * Mathf.Sin(ph - (i - 7) * 0.45f));
                    if (walk)
                    {
                        Pair(p, "DEF-thigh.L", "DEF-thigh.R", (n, s) =>
                        {
                            float q = ph + (s > 0 ? Mathf.PI : 0f);
                            p.Pitch(n, -26f * Mathf.Sin(q));
                            p.Pitch(n.Replace("thigh", "shin"), 32f * Mathf.Max(0f, Mathf.Cos(q)));
                        });
                        Pair(p, "DEF-upper_arm.L", "DEF-upper_arm.R", (n, s) => p.Pitch(n, 18f * Mathf.Sin(ph + (s > 0 ? Mathf.PI : 0f)) * -1f));
                        p.Roll("DEF-spine.001", 3f * Mathf.Sin(ph));
                        p.Move("DEF-spine", Vector3.up * 0.012f * Mathf.Abs(Mathf.Sin(ph)));
                    }
                    else Pair(p, "DEF-upper_arm.L", "DEF-upper_arm.R", (n, s) => p.Pitch(n, 4f * Mathf.Sin(ph + s)));
                }
                else if (clip == "Attack")
                {
                    float r = Rear(k), f = Strike(k);
                    p.Pitch("DEF-upper_arm.R", -85f * r + 55f * f);
                    p.Pitch("DEF-forearm.R", -30f * r + 25f * f);
                    p.Pitch("DEF-upper_arm.L", -25f * r);
                    p.Yaw("DEF-spine.002", -16f * r + 22f * f);
                    p.Pitch("DEF-spine.003", -8f * r + 12f * f);
                    p.Pitch("DEF-jaw", 28f * r + 10f * f);
                    p.Pitch("DEF-spine.006", -10f * r + 8f * f);
                }
                else
                {
                    float d = S(k / 0.7f);
                    p.Pitch("root", 80f * d);
                    p.Pitch("DEF-jaw", 22f * d);
                    Pair(p, "DEF-upper_arm.L", "DEF-upper_arm.R", (n, s) => p.Roll(n, -s * 35f * d));
                    for (int i = 7; i <= 18; i++) p.Yaw("DEF-spine." + i.ToString("000"), 3f * d);
                }
            }
        });

        // ---- snake boss (naga) ----
        string[] chain = { "hips", "spine.01", "spine.02", "chest", "neck.01", "neck.02", "head" };
        list.Add(new Spec
        {
            name = "Snakeboss", displayName = "Змей-владыка", idleLen = 2.6f, walkLen = 1.0f, attackLen = 1.0f, deathLen = 1.6f,
            pose = (p, clip, t, k) =>
            {
                float ph = 2f * Mathf.PI * t / (clip == "Idle" ? 2.6f : 1f);
                if (clip == "Idle" || clip == "Walk")
                {
                    bool walk = clip == "Walk";
                    for (int i = 0; i < chain.Length; i++) p.Yaw(chain[i], (walk ? 5f : 2.5f) * Mathf.Sin(ph - i * 0.55f));
                    p.Pitch("chest", 1.5f * Mathf.Sin(ph));
                    p.Pitch("head", 3f * Mathf.Sin(ph * 0.7f));
                    Pair(p, "hood.L", "hood.R", (n, s) => p.Yaw(n, s * 5f * Mathf.Sin(ph)));
                    Pair(p, "upper_arm.L", "upper_arm.R", (n, s) => p.Pitch(n, (walk ? 14f : 4f) * Mathf.Sin(ph + (s > 0 ? Mathf.PI : 0f))));
                    for (int i = 1; i <= 32; i++) p.Yaw("tail." + i.ToString("000"), (walk ? 7f : 2.5f) * Mathf.Sin(ph - i * 0.4f));
                }
                else if (clip == "Attack")
                {
                    float r = Rear(k), f = Strike(k);
                    p.Pitch("spine.01", -14f * r + 16f * f);
                    p.Pitch("chest", -12f * r + 14f * f);
                    p.Pitch("neck.01", -10f * r + 14f * f);
                    p.Pitch("head", -10f * r + 12f * f);
                    Pair(p, "hood.L", "hood.R", (n, s) => p.Yaw(n, s * 25f * r));
                    Pair(p, "upper_arm.L", "upper_arm.R", (n, s) => p.Pitch(n, -55f * r + 40f * f));
                    p.Pitch("tongue.01", 18f * f);
                }
                else
                {
                    float d = S(k / 0.8f);
                    p.Roll("root", 70f * d);
                    p.Pitch("chest", 12f * d);
                    p.Pitch("head", 20f * d);
                    Pair(p, "upper_arm.L", "upper_arm.R", (n, s) => p.Roll(n, -s * 30f * d));
                }
            }
        });
        return list;
    }

    // ---------- baking ----------
    static AnimationClip Bake(Spec spec, Pose p, string clipName, float len, bool loop)
    {
        var clip = new AnimationClip { name = spec.name + "_" + clipName, frameRate = Fps };
        int frames = Mathf.RoundToInt(len * Fps);
        var bones = p.order;
        var rx = bones.Select(_ => new AnimationCurve()).ToArray();
        var ry = bones.Select(_ => new AnimationCurve()).ToArray();
        var rz = bones.Select(_ => new AnimationCurve()).ToArray();
        var rw = bones.Select(_ => new AnimationCurve()).ToArray();
        var px = bones.Select(_ => new AnimationCurve()).ToArray();
        var py = bones.Select(_ => new AnimationCurve()).ToArray();
        var pz = bones.Select(_ => new AnimationCurve()).ToArray();
        var prev = new Quaternion[bones.Count];
        for (int f = 0; f <= frames; f++)
        {
            float t = f / (float)Fps;
            float k = frames > 0 ? f / (float)frames : 0f;
            p.Reset();
            spec.pose(p, clipName, loop ? t % len : t, k);
            for (int i = 0; i < bones.Count; i++)
            {
                var q = bones[i].localRotation;
                if (f > 0 && Quaternion.Dot(q, prev[i]) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                prev[i] = q;
                rx[i].AddKey(t, q.x); ry[i].AddKey(t, q.y); rz[i].AddKey(t, q.z); rw[i].AddKey(t, q.w);
                var lp = bones[i].localPosition;
                px[i].AddKey(t, lp.x); py[i].AddKey(t, lp.y); pz[i].AddKey(t, lp.z);
            }
        }
        p.Reset();
        for (int i = 0; i < bones.Count; i++)
        {
            string path = AnimationUtility.CalculateTransformPath(bones[i], p.model);
            clip.SetCurve(path, typeof(Transform), "localRotation.x", rx[i]);
            clip.SetCurve(path, typeof(Transform), "localRotation.y", ry[i]);
            clip.SetCurve(path, typeof(Transform), "localRotation.z", rz[i]);
            clip.SetCurve(path, typeof(Transform), "localRotation.w", rw[i]);
            bool moves = px[i].keys.Any(kf => Mathf.Abs(kf.value - px[i].keys[0].value) > 1e-5f) || py[i].keys.Any(kf => Mathf.Abs(kf.value - py[i].keys[0].value) > 1e-5f) || pz[i].keys.Any(kf => Mathf.Abs(kf.value - pz[i].keys[0].value) > 1e-5f);
            if (moves)
            {
                clip.SetCurve(path, typeof(Transform), "localPosition.x", px[i]);
                clip.SetCurve(path, typeof(Transform), "localPosition.y", py[i]);
                clip.SetCurve(path, typeof(Transform), "localPosition.z", pz[i]);
            }
        }
        clip.EnsureQuaternionContinuity();
        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = loop;
        AnimationUtility.SetAnimationClipSettings(clip, settings);
        string file = AnimDir + "/" + clip.name + ".anim";
        AssetDatabase.DeleteAsset(file);
        AssetDatabase.CreateAsset(clip, file);
        return clip;
    }

    // ---------- materials ----------
    static Texture2D Load(string path) => AssetDatabase.LoadAssetAtPath<Texture2D>(path);

    static Material BuildMaterial(string name, string texDir)
    {
        string baseP = $"{texDir}/{name}_BaseMap.png", nrmP = $"{texDir}/{name}_Normal.png", metP = $"{texDir}/{name}_Metallic.png", rghP = $"{texDir}/{name}_Roughness.png";
        var ni = (TextureImporter)AssetImporter.GetAtPath(nrmP);
        if (ni && ni.textureType != TextureImporterType.NormalMap) { ni.textureType = TextureImporterType.NormalMap; ni.SaveAndReimport(); }
        // pack metallic (R) + smoothness (A = 1 - roughness)
        string msP = $"{texDir}/{name}_MetallicSmoothness.png";
        if (!File.Exists(msP) && File.Exists(metP) && File.Exists(rghP))
        {
            var met = new Texture2D(2, 2); met.LoadImage(File.ReadAllBytes(metP));
            var rgh = new Texture2D(2, 2); rgh.LoadImage(File.ReadAllBytes(rghP));
            int w = met.width, h = met.height;
            if (rgh.width != w || rgh.height != h) { var rt = RenderTexture.GetTemporary(w, h); Graphics.Blit(rgh, rt); var tmp = new Texture2D(w, h); RenderTexture.active = rt; tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0); tmp.Apply(); RenderTexture.active = null; RenderTexture.ReleaseTemporary(rt); rgh = tmp; }
            var mp = met.GetPixels32(); var rp = rgh.GetPixels32();
            var outT = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var o = new Color32[mp.Length];
            for (int i = 0; i < mp.Length; i++) o[i] = new Color32(mp[i].r, mp[i].r, mp[i].r, (byte)(255 - rp[i].r));
            outT.SetPixels32(o); outT.Apply();
            File.WriteAllBytes(msP, outT.EncodeToPNG());
            AssetDatabase.ImportAsset(msP);
            var mi = (TextureImporter)AssetImporter.GetAtPath(msP);
            mi.sRGBTexture = false; mi.alphaSource = TextureImporterAlphaSource.FromInput; mi.SaveAndReimport();
        }
        foreach (var dp in new[] { metP, rghP })
        {
            var di = (TextureImporter)AssetImporter.GetAtPath(dp);
            if (di && di.sRGBTexture) { di.sRGBTexture = false; di.SaveAndReimport(); }
        }
        string matP = $"{Root}/{name}/{name}_Mat.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(matP);
        if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, matP); }
        m.SetTexture("_BaseMap", Load(baseP)); m.SetColor("_BaseColor", Color.white);
        var n = Load(nrmP); if (n) { m.SetTexture("_BumpMap", n); m.EnableKeyword("_NORMALMAP"); }
        var ms = Load(msP);
        if (ms) { m.SetTexture("_MetallicGlossMap", ms); m.EnableKeyword("_METALLICSPECGLOSSMAP"); m.SetFloat("_Smoothness", 1f); m.SetFloat("_SmoothnessTextureChannel", 0f); }
        m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", Color.black);
        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        m.enableInstancing = true;
        EditorUtility.SetDirty(m);
        return m;
    }

    [MenuItem("Tools/Relict/Build Creatures")]
    public static void Build()
    {
        foreach (var d in new[] { AnimDir, PrefabDir })
            if (!AssetDatabase.IsValidFolder(d)) { var parent = Path.GetDirectoryName(d).Replace('\\', '/'); AssetDatabase.CreateFolder(parent, Path.GetFileName(d)); }

        var built = new List<string>();
        foreach (var spec in Specs())
        {
            string fbx = $"{Root}/{spec.name}/{spec.name}.fbx";
            var imp = (ModelImporter)AssetImporter.GetAtPath(fbx);
            if (imp == null) { Debug.LogWarning("Missing " + fbx); continue; }
            imp.animationType = ModelImporterAnimationType.Generic;
            imp.importAnimation = false;
            imp.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            var mat = BuildMaterial(spec.name, $"{Root}/{spec.name}/Textures");
            foreach (var kv in imp.GetExternalObjectMap()) imp.RemoveRemap(kv.Key);
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<Material>())
                imp.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), o.name), mat);
            imp.SaveAndReimport();

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            var inst = (GameObject)Object.Instantiate(model);
            inst.name = spec.name;
            var pose = new Pose(inst, spec.forward);
            var clips = new Dictionary<string, AnimationClip>
            {
                ["Idle"] = Bake(spec, pose, "Idle", spec.idleLen, true),
                ["Walk"] = Bake(spec, pose, "Walk", spec.walkLen, true),
                ["Attack"] = Bake(spec, pose, "Attack", spec.attackLen, false),
                ["Death"] = Bake(spec, pose, "Death", spec.deathLen, false),
            };
            Object.DestroyImmediate(inst);

            string ctrlP = $"{AnimDir}/{spec.name}.controller";
            AssetDatabase.DeleteAsset(ctrlP);
            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ctrlP);
            var sm = ctrl.layers[0].stateMachine;
            foreach (var kv in clips)
            {
                var st = sm.AddState(kv.Key);
                st.motion = kv.Value;
                if (kv.Key == "Idle") sm.defaultState = st;
            }

            // prefab: root faces +Z, model child carries the Animator
            var root = new GameObject("Creature_" + spec.name);
            var m = (GameObject)PrefabUtility.InstantiatePrefab(model);
            m.name = "Model";
            m.transform.SetParent(root.transform, false);
            m.transform.localRotation = Quaternion.Euler(0, spec.modelYaw, 0);
            var an = m.GetComponent<Animator>();
            if (an == null) an = m.AddComponent<Animator>();
            an.runtimeAnimatorController = ctrl;
            an.applyRootMotion = false;
            an.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            foreach (var smr in m.GetComponentsInChildren<SkinnedMeshRenderer>()) { smr.updateWhenOffscreen = false; smr.quality = SkinQuality.Bone2; }
            PrefabUtility.SaveAsPrefabAsset(root, $"{PrefabDir}/Creature_{spec.name}.prefab");
            Object.DestroyImmediate(root);
            built.Add(spec.name);
        }

        // wire into the match manager
        var mm = Object.FindAnyObjectByType<Relict.MatchManager>();
        if (mm)
        {
            mm.antPrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{PrefabDir}/Creature_Ant.prefab");
            mm.ladybugPrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{PrefabDir}/Creature_Ladybug.prefab");
            mm.mantisPrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{PrefabDir}/Creature_Mantis.prefab");
            mm.reptileBossPrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{PrefabDir}/Creature_Reptileboss.prefab");
            mm.snakeBossPrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{PrefabDir}/Creature_Snakeboss.prefab");
            EditorUtility.SetDirty(mm);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(mm.gameObject.scene);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(mm.gameObject.scene);
        }
        AssetDatabase.SaveAssets();
        Debug.Log("Creatures built: " + string.Join(", ", built));
    }
}
#endif
