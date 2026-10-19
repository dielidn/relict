using System.Collections.Generic;
using UnityEngine;

namespace Relict
{
    public enum Sfx { BowDraw, BowRelease, BowFull, ArrowHit, ArrowCrit, ArrowWood, Kill, Swoosh, MeleeHit, Hurt, LevelUp, Coin, Artifact, Leap, Buy, Click }

    // Procedurally synthesised sound effects: no audio assets needed.
    public static class SFX
    {
        const int Rate = 44100;
        static readonly Dictionary<Sfx, AudioClip> clips = new Dictionary<Sfx, AudioClip>();
        static AudioSource[] pool;
        static int next;
        static System.Random rng = new System.Random(7);

        static float Noise() => (float)(rng.NextDouble() * 2.0 - 1.0);

        static AudioClip Make(string name, float seconds, System.Func<float, float> f)
        {
            int n = Mathf.CeilToInt(seconds * Rate);
            var data = new float[n];
            for (int i = 0; i < n; i++) data[i] = Mathf.Clamp(f(i / (float)Rate), -1f, 1f);
            var c = AudioClip.Create(name, n, 1, Rate, false);
            c.SetData(data, 0);
            return c;
        }

        static float Env(float t, float attack, float decay) => t < attack ? t / attack : Mathf.Exp(-(t - attack) / decay);

        static AudioClip Build(Sfx id)
        {
            float lp = 0f; // one-pole low-pass state
            float phase = 0f, phase2 = 0f;
            switch (id)
            {
                case Sfx.BowDraw:
                    return Make("draw", 0.45f, t =>
                    {
                        lp += (Noise() - lp) * 0.08f;
                        float creak = Mathf.Sin(2 * Mathf.PI * (180 + 260 * t) * t) * 0.15f * (0.5f + 0.5f * Mathf.Sin(t * 90f));
                        return (lp * 0.5f + creak) * Mathf.Clamp01(t * 6f) * Mathf.Clamp01((0.45f - t) * 8f) * 0.5f;
                    });
                case Sfx.BowRelease:
                    return Make("release", 0.32f, t =>
                    {
                        phase += 2 * Mathf.PI * (240f - 120f * t) / Rate;
                        lp += (Noise() - lp) * 0.35f;
                        float twang = Mathf.Sin(phase) * Mathf.Exp(-t * 16f) + Mathf.Sin(phase * 2.01f) * 0.4f * Mathf.Exp(-t * 22f);
                        float whoosh = lp * Mathf.Exp(-t * 14f) * 0.6f;
                        return (twang * 0.7f + whoosh) * 0.8f;
                    });
                case Sfx.BowFull:
                    return Make("full", 0.18f, t =>
                    {
                        phase += 2 * Mathf.PI * 1320f / Rate;
                        return Mathf.Sin(phase) * Env(t, 0.005f, 0.05f) * 0.35f;
                    });
                case Sfx.ArrowHit:
                    return Make("hit", 0.16f, t =>
                    {
                        phase += 2 * Mathf.PI * (140f - 260f * t) / Rate;
                        lp += (Noise() - lp) * 0.5f;
                        return (Mathf.Sin(phase) * 0.9f + lp * 0.5f * Mathf.Exp(-t * 60f)) * Env(t, 0.002f, 0.045f);
                    });
                case Sfx.ArrowCrit:
                    return Make("crit", 0.3f, t =>
                    {
                        phase += 2 * Mathf.PI * (160f - 200f * t) / Rate;
                        phase2 += 2 * Mathf.PI * 1760f / Rate;
                        return Mathf.Sin(phase) * Env(t, 0.002f, 0.05f) * 0.8f + Mathf.Sin(phase2) * Env(t, 0.003f, 0.09f) * 0.35f;
                    });
                case Sfx.ArrowWood:
                    return Make("wood", 0.1f, t =>
                    {
                        lp += (Noise() - lp) * 0.6f;
                        phase += 2 * Mathf.PI * 420f / Rate;
                        return (lp * 0.6f + Mathf.Sin(phase) * 0.4f) * Env(t, 0.001f, 0.02f);
                    });
                case Sfx.Kill:
                    return Make("kill", 0.42f, t =>
                    {
                        float f = t < 0.09f ? 660f : 990f;
                        phase += 2 * Mathf.PI * f / Rate;
                        float tt = t < 0.09f ? t : t - 0.09f;
                        return (Mathf.Sin(phase) + 0.3f * Mathf.Sin(phase * 2f)) * Env(tt, 0.004f, 0.11f) * 0.4f;
                    });
                case Sfx.Swoosh:
                    return Make("swoosh", 0.22f, t =>
                    {
                        float k = 0.05f + 0.4f * Mathf.Sin(Mathf.PI * t / 0.22f);
                        lp += (Noise() - lp) * k;
                        return lp * Mathf.Sin(Mathf.PI * t / 0.22f) * 0.9f;
                    });
                case Sfx.MeleeHit:
                    return Make("melee", 0.2f, t =>
                    {
                        phase += 2 * Mathf.PI * (110f - 150f * t) / Rate;
                        lp += (Noise() - lp) * 0.4f;
                        return (Mathf.Sin(phase) + lp * 0.8f * Mathf.Exp(-t * 40f)) * Env(t, 0.002f, 0.06f);
                    });
                case Sfx.Hurt:
                    return Make("hurt", 0.25f, t =>
                    {
                        phase += 2 * Mathf.PI * (190f - 120f * t) / Rate;
                        lp += (Noise() - lp) * 0.2f;
                        return (Mathf.Sin(phase) * 0.6f + lp * 0.5f) * Env(t, 0.005f, 0.08f) * 0.8f;
                    });
                case Sfx.LevelUp:
                    return Make("level", 0.7f, t =>
                    {
                        int step = Mathf.Min(3, (int)(t / 0.1f));
                        float[] notes = { 523f, 659f, 784f, 1047f };
                        phase += 2 * Mathf.PI * notes[step] / Rate;
                        float tt = t - step * 0.1f;
                        return (Mathf.Sin(phase) + 0.25f * Mathf.Sin(phase * 3f)) * Env(tt, 0.005f, step == 3 ? 0.25f : 0.08f) * 0.35f;
                    });
                case Sfx.Coin:
                    return Make("coin", 0.16f, t =>
                    {
                        float f = t < 0.05f ? 1568f : 2093f;
                        phase += 2 * Mathf.PI * f / Rate;
                        return Mathf.Sin(phase) * Env(t < 0.05f ? t : t - 0.05f, 0.002f, 0.04f) * 0.22f;
                    });
                case Sfx.Artifact:
                    return Make("artifact", 0.9f, t =>
                    {
                        phase += 2 * Mathf.PI * (880f + 40f * Mathf.Sin(t * 30f)) / Rate;
                        phase2 += 2 * Mathf.PI * 1320f / Rate;
                        return (Mathf.Sin(phase) + Mathf.Sin(phase2) * 0.6f) * Env(t, 0.02f, 0.3f) * 0.3f;
                    });
                case Sfx.Leap:
                    return Make("leap", 0.5f, t =>
                    {
                        phase += 2 * Mathf.PI * (70f - 40f * t) / Rate;
                        lp += (Noise() - lp) * 0.15f;
                        return (Mathf.Sin(phase) + lp) * Env(t, 0.003f, 0.14f);
                    });
                case Sfx.Buy:
                    return Make("buy", 0.3f, t =>
                    {
                        float f = t < 0.08f ? 784f : 1175f;
                        phase += 2 * Mathf.PI * f / Rate;
                        return Mathf.Sin(phase) * Env(t < 0.08f ? t : t - 0.08f, 0.003f, 0.07f) * 0.3f;
                    });
                default:
                    return Make("click", 0.05f, t =>
                    {
                        phase += 2 * Mathf.PI * 900f / Rate;
                        return Mathf.Sin(phase) * Env(t, 0.001f, 0.012f) * 0.25f;
                    });
            }
        }

        static AudioClip Get(Sfx id)
        {
            if (!clips.TryGetValue(id, out var c) || c == null) { c = Build(id); clips[id] = c; }
            return c;
        }

        static void EnsurePool()
        {
            if (pool != null && pool.Length > 0 && pool[0] != null) return;
            var go = new GameObject("SFX");
            Object.DontDestroyOnLoad(go);
            pool = new AudioSource[16];
            for (int i = 0; i < pool.Length; i++)
            {
                var s = go.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.spatialBlend = 0f;
                pool[i] = s;
            }
        }

        // Plays a sound; world position (optional) attenuates volume by distance from the camera.
        public static void Play(Sfx id, float volume = 1f, float pitchJitter = 0.06f, Vector3? at = null)
        {
            EnsurePool();
            if (at.HasValue && Camera.main)
            {
                float d = Vector3.Distance(Camera.main.transform.position, at.Value);
                volume *= Mathf.Clamp01(1f - (d - 6f) / 40f);
                if (volume < 0.02f) return;
            }
            var s = pool[next];
            next = (next + 1) % pool.Length;
            s.pitch = 1f + (float)(rng.NextDouble() * 2 - 1) * pitchJitter;
            s.PlayOneShot(Get(id), volume);
        }

        public static void Warm()
        {
            foreach (Sfx id in System.Enum.GetValues(typeof(Sfx))) Get(id);
            EnsurePool();
        }
    }
}
