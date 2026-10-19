using UnityEngine;

namespace Relict
{
    // Player settings: stored in PlayerPrefs and applied to camera, screen and quality.
    public static class GameSettings
    {
        public static string Nick = "";
        public static float Sensitivity = 0.11f;
        public static bool InvertY;
        public static float Fov = 60f;
        public static int Quality = -1;
        public static bool Fullscreen = true;
        public static int ResolutionIndex = -1;
        public static bool VSync = true;
        public static bool DamageNumbers = true;
        public static float Volume = 0.8f;

        static bool loaded;

        public static bool HasNick => !string.IsNullOrWhiteSpace(Nick);

        public static void Load()
        {
            if (loaded) return;
            loaded = true;
            Nick = PlayerPrefs.GetString("relict_nick", "");
            Sensitivity = PlayerPrefs.GetFloat("relict_sens", 0.11f);
            InvertY = PlayerPrefs.GetInt("relict_invert", 0) == 1;
            Fov = PlayerPrefs.GetFloat("relict_fov", 60f);
            Quality = PlayerPrefs.GetInt("relict_quality", QualitySettings.GetQualityLevel());
            Fullscreen = PlayerPrefs.GetInt("relict_fullscreen", 1) == 1;
            ResolutionIndex = PlayerPrefs.GetInt("relict_res", -1);
            VSync = PlayerPrefs.GetInt("relict_vsync", 1) == 1;
            DamageNumbers = PlayerPrefs.GetInt("relict_dmgnum", 1) == 1;
            Volume = PlayerPrefs.GetFloat("relict_volume", 0.8f);
            Apply(true);
        }

        public static void Save()
        {
            PlayerPrefs.SetString("relict_nick", Nick);
            PlayerPrefs.SetFloat("relict_sens", Sensitivity);
            PlayerPrefs.SetInt("relict_invert", InvertY ? 1 : 0);
            PlayerPrefs.SetFloat("relict_fov", Fov);
            PlayerPrefs.SetInt("relict_quality", Quality);
            PlayerPrefs.SetInt("relict_fullscreen", Fullscreen ? 1 : 0);
            PlayerPrefs.SetInt("relict_res", ResolutionIndex);
            PlayerPrefs.SetInt("relict_vsync", VSync ? 1 : 0);
            PlayerPrefs.SetInt("relict_dmgnum", DamageNumbers ? 1 : 0);
            PlayerPrefs.SetFloat("relict_volume", Volume);
            PlayerPrefs.Save();
        }

        public static void ResetToDefaults()
        {
            Sensitivity = 0.11f; InvertY = false; Fov = 60f;
            Quality = QualitySettings.names.Length - 1;
            Fullscreen = true; ResolutionIndex = -1; VSync = true; DamageNumbers = true; Volume = 0.8f;
            Apply(true);
        }

        public static Resolution[] Resolutions()
        {
            var all = Screen.resolutions;
            // keep one entry per size (highest refresh rate)
            var list = new System.Collections.Generic.List<Resolution>();
            foreach (var r in all)
            {
                int i = list.FindIndex(x => x.width == r.width && x.height == r.height);
                if (i < 0) list.Add(r);
                else if (r.refreshRateRatio.value > list[i].refreshRateRatio.value) list[i] = r;
            }
            return list.ToArray();
        }

        public static string ResolutionLabel()
        {
            var rs = Resolutions();
            if (ResolutionIndex < 0 || ResolutionIndex >= rs.Length) return "Как у монитора";
            return rs[ResolutionIndex].width + " × " + rs[ResolutionIndex].height;
        }

        // screen = also change window mode/resolution (skipped in the editor, where the Game view decides)
        public static void Apply(bool screen)
        {
            if (Quality >= 0 && Quality < QualitySettings.names.Length && QualitySettings.GetQualityLevel() != Quality)
                QualitySettings.SetQualityLevel(Quality, true);
            QualitySettings.vSyncCount = VSync ? 1 : 0;
            Application.targetFrameRate = VSync ? -1 : 144;
            AudioListener.volume = Volume;
            var cam = Camera.main;
            if (cam) cam.fieldOfView = Fov;
            if (screen && !Application.isEditor) ApplyScreen();
        }

        public static void ApplyScreen()
        {
            var mode = Fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
            var rs = Resolutions();
            if (ResolutionIndex >= 0 && ResolutionIndex < rs.Length)
                Screen.SetResolution(rs[ResolutionIndex].width, rs[ResolutionIndex].height, mode);
            else if (Fullscreen)
                Screen.SetResolution(Display.main.systemWidth, Display.main.systemHeight, mode);
            else
                Screen.SetResolution(Mathf.RoundToInt(Display.main.systemWidth * 0.75f), Mathf.RoundToInt(Display.main.systemHeight * 0.75f), mode);
        }

        public static void ToggleFullscreen()
        {
            Fullscreen = !Fullscreen;
            ApplyScreen();
            Save();
        }

        public static string CleanNick(string s)
        {
            if (s == null) return "";
            s = s.Replace("|", "").Replace(";", "").Replace("\n", "").Trim();
            return s.Length > 16 ? s.Substring(0, 16) : s;
        }
    }
}
