using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Relict
{
    // All game UI (IMGUI): menu, HUD, Tab upgrades, settings, pause, results.
    public partial class MatchManager
    {
        [Header("UI fonts")]
        public Font headFont;
        public Font bodyFont;
        public Font bodyBoldFont;

        bool settingsOpen, nickPrompt;
        string nickDraft = "";

        // ---------- palette ----------
        static readonly Color CPanel = new Color(0.045f, 0.065f, 0.05f, 0.9f);
        static readonly Color CPanel2 = new Color(0.075f, 0.105f, 0.075f, 0.92f);
        static readonly Color CLine = new Color(0.72f, 0.94f, 0.42f, 0.22f);
        static readonly Color CAccent = new Color(0.72f, 0.94f, 0.42f);
        static readonly Color CAccentDim = new Color(0.72f, 0.94f, 0.42f, 0.16f);
        static readonly Color CGold = new Color(1f, 0.78f, 0.34f);
        static readonly Color CCyan = new Color(0.44f, 0.89f, 1f);
        static readonly Color CDanger = new Color(1f, 0.36f, 0.3f);
        static readonly Color CText = new Color(0.93f, 0.95f, 0.9f);
        static readonly Color CMuted = new Color(0.6f, 0.66f, 0.58f);
        static readonly Color CShade = new Color(0f, 0f, 0f, 0.55f);

        float S => Screen.height / 1080f;

        // ---------- primitives ----------
        static void RR(Rect r, Color c, float radius, float border = 0f)
        {
            if (Event.current.type != EventType.Repaint || c.a <= 0.001f) return;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, c, border > 0 ? new Vector4(border, border, border, border) : Vector4.zero, radius);
        }

        void Card(Rect r, float radius = 16f, bool raised = false)
        {
            float s = S;
            RR(new Rect(r.x + 3 * s, r.y + 6 * s, r.width, r.height), new Color(0, 0, 0, 0.35f), radius * s);
            RR(r, raised ? CPanel2 : CPanel, radius * s);
            RR(new Rect(r.x, r.y, r.width, Mathf.Min(r.height, 60 * s)), new Color(1, 1, 1, 0.025f), radius * s);
            RR(r, CLine, radius * s, Mathf.Max(1f, 1.2f * s));
        }

        readonly Dictionary<string, GUIStyle> styleCache = new Dictionary<string, GUIStyle>();
        int styleHeight;

        GUIStyle St(char face, float size, TextAnchor anchor = TextAnchor.MiddleLeft, bool wrap = false)
        {
            if (styleHeight != Screen.height) { styleCache.Clear(); styleHeight = Screen.height; }
            string key = face + "|" + size + "|" + anchor + "|" + wrap;
            if (!styleCache.TryGetValue(key, out var st))
            {
                st = new GUIStyle(GUI.skin.label)
                {
                    font = face == 'h' ? (headFont ? headFont : null) : face == 'b' ? (bodyBoldFont ? bodyBoldFont : bodyFont) : bodyFont,
                    fontSize = Mathf.Max(8, Mathf.RoundToInt(size * S)),
                    alignment = anchor,
                    wordWrap = wrap,
                    richText = true,
                    clipping = TextClipping.Overflow,
                    padding = new RectOffset(0, 0, 0, 0),
                    margin = new RectOffset(0, 0, 0, 0)
                };
                if (face == 'b' && !bodyBoldFont) st.fontStyle = FontStyle.Bold;
                styleCache[key] = st;
            }
            return st;
        }

        void Txt(Rect r, string text, GUIStyle st, Color col, bool shadow = false)
        {
            if (shadow)
            {
                st.normal.textColor = new Color(0, 0, 0, col.a * 0.6f);
                float o = Mathf.Max(1f, 2f * S);
                GUI.Label(new Rect(r.x + o * 0.5f, r.y + o, r.width, r.height), text, st);
            }
            st.normal.textColor = col;
            GUI.Label(r, text, st);
        }

        enum Btn { Primary, Secondary, Ghost, Danger }

        bool Button(Rect r, string text, Btn kind = Btn.Secondary, bool enabled = true, float textSize = 20f)
        {
            float s = S;
            bool hover = enabled && r.Contains(Event.current.mousePosition);
            bool down = hover && Input_MouseHeld();
            Color fill, edge, tc;
            switch (kind)
            {
                case Btn.Primary:
                    fill = enabled ? (down ? new Color(0.58f, 0.8f, 0.3f) : hover ? new Color(0.82f, 1f, 0.55f) : CAccent) : new Color(0.3f, 0.36f, 0.26f, 0.8f);
                    edge = new Color(1, 1, 1, hover ? 0.5f : 0.25f);
                    tc = enabled ? new Color(0.06f, 0.1f, 0.04f) : new Color(0.6f, 0.65f, 0.55f);
                    break;
                case Btn.Danger:
                    fill = hover ? new Color(0.45f, 0.12f, 0.1f, 0.95f) : new Color(0.3f, 0.08f, 0.07f, 0.9f);
                    edge = new Color(CDanger.r, CDanger.g, CDanger.b, hover ? 0.8f : 0.45f);
                    tc = new Color(1f, 0.85f, 0.82f);
                    break;
                case Btn.Ghost:
                    fill = hover ? new Color(1, 1, 1, 0.08f) : new Color(1, 1, 1, 0.0f);
                    edge = new Color(1, 1, 1, hover ? 0.3f : 0.14f);
                    tc = enabled ? CText : CMuted;
                    break;
                default:
                    fill = enabled ? (down ? new Color(0.2f, 0.3f, 0.15f, 0.95f) : hover ? new Color(0.17f, 0.26f, 0.13f, 0.95f) : new Color(0.1f, 0.15f, 0.08f, 0.92f)) : new Color(0.08f, 0.1f, 0.07f, 0.7f);
                    edge = enabled ? new Color(CAccent.r, CAccent.g, CAccent.b, hover ? 0.7f : 0.32f) : new Color(1, 1, 1, 0.08f);
                    tc = enabled ? CText : new Color(0.5f, 0.55f, 0.48f);
                    break;
            }
            RR(r, fill, 10 * s);
            RR(r, edge, 10 * s, Mathf.Max(1f, 1.2f * s));
            Txt(r, text, St(kind == Btn.Primary ? 'h' : 'b', textSize, TextAnchor.MiddleCenter), tc);
            bool clicked = GUI.Button(r, GUIContent.none, GUIStyle.none) && enabled;
            if (clicked) SFX.Play(Sfx.Click, 0.5f, 0f);
            return clicked;
        }

        static bool Input_MouseHeld() => Mouse.current != null && Mouse.current.leftButton.isPressed;

        void Bar(Rect r, float k, Color fill, string text = null, float radius = 6f)
        {
            float s = S;
            RR(r, new Color(0, 0, 0, 0.55f), radius * s);
            k = Mathf.Clamp01(k);
            if (k > 0.001f)
            {
                float pad = Mathf.Max(1f, 2f * s);
                var fr = new Rect(r.x + pad, r.y + pad, Mathf.Max((r.width - pad * 2) * k, (radius * 2 - pad * 2) * s), r.height - pad * 2);
                RR(fr, fill, (radius - 1) * s);
                RR(new Rect(fr.x, fr.y, fr.width, fr.height * 0.45f), new Color(1, 1, 1, 0.16f), (radius - 1) * s);
            }
            RR(r, new Color(1, 1, 1, 0.08f), radius * s, Mathf.Max(1f, s));
            if (text != null) Txt(r, text, St('b', Mathf.Clamp(r.height / S * 0.62f, 12f, 20f), TextAnchor.MiddleCenter), CText, true);
        }

        void Caption(Rect r, string text, Color? col = null)
        {
            Txt(r, text.ToUpperInvariant(), St('h', 14f), col ?? CMuted);
        }

        void KeyBadge(Rect r, string key)
        {
            float s = S;
            RR(r, new Color(0.9f, 0.95f, 0.85f, 0.95f), 6 * s);
            RR(new Rect(r.x, r.yMax - 3 * s, r.width, 3 * s), new Color(0, 0, 0, 0.25f), 2 * s);
            Txt(r, key, St('h', Mathf.Clamp(r.height / S * 0.5f, 10f, 18f), TextAnchor.MiddleCenter), new Color(0.08f, 0.1f, 0.06f));
        }

        void Coin(Rect r) { RR(r, CGold, r.width * 0.5f); RR(new Rect(r.x + r.width * 0.25f, r.y + r.height * 0.2f, r.width * 0.5f, r.height * 0.6f), new Color(1, 1, 1, 0.35f), r.width * 0.25f); }

        void Diamond(Vector2 c, float size, Color col)
        {
            var m = GUI.matrix;
            GUIUtility.RotateAroundPivot(45f, c);
            RR(new Rect(c.x - size * 0.5f, c.y - size * 0.5f, size, size), col, size * 0.15f);
            GUI.matrix = m;
        }

        void Pips(Rect r, int filled, int total, Color on)
        {
            float s = S;
            float gap = 4 * s, w = (r.width - gap * (total - 1)) / total;
            for (int i = 0; i < total; i++)
                RR(new Rect(r.x + i * (w + gap), r.y, w, r.height), i < filled ? on : new Color(1, 1, 1, 0.1f), r.height * 0.5f);
        }

        void Dim(float a = 0.55f) { RR(new Rect(0, 0, Screen.width, Screen.height), new Color(0.01f, 0.02f, 0.01f, a), 0); }

        static string Clock(float t) { t = Mathf.Max(0, t); return ((int)t / 60) + ":" + ((int)t % 60).ToString("00"); }

        // ---------- entry ----------
        void OnGUI()
        {
            bool modal = settingsOpen || nickPrompt || (upgradeOpen && state == State.Playing);
            GUI.enabled = !modal;
            switch (state)
            {
                case State.Select: DrawMenu(); break;
                case State.Playing: DrawHud(); break;
                case State.Paused: DrawHud(); DrawPause(); break;
                case State.Result: DrawResult(); break;
            }
            GUI.enabled = true;
            if (upgradeOpen && state == State.Playing && !settingsOpen) DrawUpgrades();
            if (nickPrompt) DrawNickPrompt();
            else if (settingsOpen) DrawSettings();
        }

        // ---------- main menu ----------
        void DrawMenu()
        {
            float s = S, W = Screen.width, H = Screen.height;
            // readable gradient on the left and bottom
            for (int i = 0; i < 12; i++)
            {
                float a = 0.5f * (1f - i / 12f);
                RR(new Rect(0, 0, (520 + i * 30) * s, H), new Color(0.01f, 0.02f, 0.01f, a * 0.25f), 0);
                RR(new Rect(0, H - (180 + i * 22) * s, W, (180 + i * 22) * s), new Color(0.01f, 0.02f, 0.01f, a * 0.2f), 0);
            }

            // logo
            Txt(new Rect(56 * s, 40 * s, 700 * s, 100 * s), "РЕЛИКТ", St('h', 96f), new Color(0.86f, 1f, 0.62f), true);
            RR(new Rect(60 * s, 146 * s, 64 * s, 4 * s), CAccent, 2 * s);
            Caption(new Rect(136 * s, 136 * s, 600 * s, 24 * s), "Джунгли · PvPvE · прототип", new Color(0.8f, 0.86f, 0.76f));

            // player chip (top right)
            float cw = 430 * s, cx = W - cw - 48 * s, cy = 44 * s;
            Card(new Rect(cx, cy, cw, 92 * s), 18f);
            string nick = GameSettings.HasNick ? GameSettings.Nick : "—";
            RR(new Rect(cx + 18 * s, cy + 16 * s, 60 * s, 60 * s), CAccentDim, 30 * s);
            RR(new Rect(cx + 18 * s, cy + 16 * s, 60 * s, 60 * s), CAccent, 30 * s, 2 * s);
            Txt(new Rect(cx + 18 * s, cy + 16 * s, 60 * s, 60 * s), nick.Substring(0, 1).ToUpperInvariant(), St('h', 28f, TextAnchor.MiddleCenter), CAccent);
            Txt(new Rect(cx + 92 * s, cy + 14 * s, 220 * s, 34 * s), nick, St('h', 24f), CText);
            Coin(new Rect(cx + 94 * s, cy + 56 * s, 16 * s, 16 * s));
            Txt(new Rect(cx + 118 * s, cy + 50 * s, 200 * s, 28 * s), Bank + " <color=#9AA894>в казне</color>", St('b', 18f), CGold);
            if (Button(new Rect(cx + cw - 128 * s, cy + 24 * s, 110 * s, 44 * s), "Ник", Btn.Ghost, true, 17f)) { nickPrompt = true; nickDraft = GameSettings.Nick; }
            if (Button(new Rect(cx, cy + 108 * s, cw, 52 * s), "Настройки", Btn.Secondary, true, 19f)) settingsOpen = true;

            // leaderboard (left)
            var lb = new Rect(56 * s, 200 * s, 420 * s, 470 * s);
            Card(lb);
            Caption(new Rect(lb.x + 24 * s, lb.y + 18 * s, 300 * s, 24 * s), "Рейтинг вылазок", CAccent);
            var b = Board();
            for (int i = 0; i < Mathf.Min(9, b.Count); i++)
            {
                float y = lb.y + (58 + i * 44) * s;
                bool mine = GameSettings.HasNick && b[i].Key.StartsWith(GameSettings.Nick + " (");
                if (mine) RR(new Rect(lb.x + 12 * s, y - 4 * s, lb.width - 24 * s, 40 * s), CAccentDim, 10 * s);
                Color rc = i == 0 ? CGold : i == 1 ? new Color(0.82f, 0.86f, 0.9f) : i == 2 ? new Color(0.86f, 0.58f, 0.36f) : new Color(1, 1, 1, 0.12f);
                RR(new Rect(lb.x + 22 * s, y + 2 * s, 28 * s, 28 * s), i < 3 ? rc : new Color(1, 1, 1, 0.08f), 14 * s);
                Txt(new Rect(lb.x + 22 * s, y + 2 * s, 28 * s, 28 * s), (i + 1).ToString(), St('h', 15f, TextAnchor.MiddleCenter), i < 3 ? new Color(0.08f, 0.08f, 0.05f) : CMuted);
                Txt(new Rect(lb.x + 62 * s, y, 250 * s, 32 * s), b[i].Key, St(mine ? 'b' : 'r', 18f), mine ? CAccent : CText);
                Txt(new Rect(lb.x + lb.width - 130 * s, y, 106 * s, 32 * s), b[i].Value.ToString(), St('h', 18f, TextAnchor.MiddleRight), i == 0 ? CGold : CText);
            }

            // controls (right)
            var cr = new Rect(W - cw - 48 * s, 0, cw, 380 * s);
            cr.y = cy + 180 * s;
            Card(cr);
            Caption(new Rect(cr.x + 24 * s, cr.y + 18 * s, 300 * s, 24 * s), "Управление", CAccent);
            string[][] keys = {
                new[] { "WASD", "Движение" }, new[] { "Shift", "Бег" }, new[] { "Space", "Прыжок" },
                new[] { "ЛКМ", "Атака · у лука удерживать" }, new[] { "Q", "Способность" }, new[] { "Tab", "Улучшения в матче" },
                new[] { "Esc", "Пауза" }, new[] { "F11", "Полный экран" } };
            for (int i = 0; i < keys.Length; i++)
            {
                float y = cr.y + (56 + i * 38) * s;
                float kw = Mathf.Max(34f, 14f * keys[i][0].Length + 12f) * s;
                KeyBadge(new Rect(cr.x + 24 * s, y, kw, 28 * s), keys[i][0]);
                Txt(new Rect(cr.x + 24 * s + kw + 14 * s, y - 2 * s, 300 * s, 32 * s), keys[i][1], St('r', 17f), CText);
            }

            // hero cards (bottom)
            float hw = 440 * s, hh = 330 * s, gap = 28 * s, hy = H - hh - 44 * s;
            DrawHeroCard(new Rect(W * 0.5f - hw - gap * 0.5f, hy, hw, hh), HeroKind.Frog, "Лягушка-древолаз", "Ближний бой",
                new[] { 4, 4, 5, 1 }, new[] { new[] { "Q", "Прыжок по площади" }, new[] { "Ctrl", "Перекат" } });
            DrawHeroCard(new Rect(W * 0.5f + gap * 0.5f, hy, hw, hh), HeroKind.Gecko, "Геккон-лучник", "Дальний бой",
                new[] { 3, 4, 3, 5 }, new[] { new[] { "Q", "Корковая броня" }, new[] { "ПКМ", "Прицел" } });
        }

        void DrawHeroCard(Rect r, HeroKind k, string name, string role, int[] stats, string[][] abilities)
        {
            float s = S;
            bool hover = r.Contains(Event.current.mousePosition);
            Card(r, 18f, hover);
            if (hover) RR(r, new Color(CAccent.r, CAccent.g, CAccent.b, 0.55f), 18 * s, 2 * s);

            // role pill
            var rp = new Rect(r.x + 24 * s, r.y + 22 * s, 150 * s, 26 * s);
            RR(rp, CAccentDim, 13 * s);
            Txt(rp, role.ToUpperInvariant(), St('h', 12f, TextAnchor.MiddleCenter), CAccent);
            Txt(new Rect(r.x + 24 * s, r.y + 56 * s, r.width, 40 * s), name, St('h', 30f), CText);

            string[] statNames = { "Здоровье", "Урон", "Мобильность", "Дальность" };
            for (int i = 0; i < 4; i++)
            {
                float y = r.y + (110 + i * 28) * s;
                Txt(new Rect(r.x + 24 * s, y - 6 * s, 140 * s, 24 * s), statNames[i], St('r', 15f), CMuted);
                Pips(new Rect(r.x + 150 * s, y, 150 * s, 9 * s), stats[i], 5, CAccent);
            }
            // abilities
            for (int i = 0; i < abilities.Length; i++)
            {
                float y = r.y + (110 + i * 40) * s, x = r.x + 320 * s;
                float kw = Mathf.Max(30f, 13f * abilities[i][0].Length + 12f) * s;
                KeyBadge(new Rect(x, y - 6 * s, kw, 26 * s), abilities[i][0]);
                Txt(new Rect(x - 4 * s, y + 18 * s, 120 * s, 24 * s), abilities[i][1], St('r', 13f, TextAnchor.UpperLeft, true), CMuted);
            }

            // weapon tier
            int tier = WeaponTier(k);
            float wy = r.y + 228 * s;
            Txt(new Rect(r.x + 24 * s, wy, 200 * s, 28 * s), "Оружие · ранг " + WeaponNames[tier], St('b', 16f), CText);
            Pips(new Rect(r.x + 24 * s, wy + 28 * s, 120 * s, 6 * s), tier + 1, 3, CGold);
            if (tier < 2)
            {
                int cost = UpgradeCost(tier);
                if (Button(new Rect(r.x + r.width - 214 * s, wy - 2 * s, 190 * s, 40 * s), "Улучшить · " + cost, Btn.Ghost, Bank >= cost, 15f))
                { Bank -= cost; SetWeaponTier(k, tier + 1); PlayerPrefs.Save(); SFX.Play(Sfx.Buy); }
            }
            else Txt(new Rect(r.x + r.width - 214 * s, wy, 190 * s, 32 * s), "Максимум", St('b', 15f, TextAnchor.MiddleRight), CGold);

            if (Button(new Rect(r.x + 24 * s, r.yMax - 66 * s, r.width - 48 * s, 48 * s), "В ДЖУНГЛИ", Btn.Primary, true, 20f))
                StartMatch(k);
        }

        // ---------- HUD ----------
        void DrawHud()
        {
            float s = S, W = Screen.width, H = Screen.height;
            var cam = Camera.main;

            // damage flash
            float hurtA = Mathf.Clamp01(1f - (Time.unscaledTime - hurtT) / 0.45f) * 0.4f;
            if (Hero != null && Hero.Hp.hp < Hero.Hp.max * 0.3f && !Hero.Hp.Dead) hurtA = Mathf.Max(hurtA, 0.1f + 0.07f * Mathf.Sin(Time.unscaledTime * 5f));
            if (hurtA > 0.01f)
                for (int i = 0; i < 8; i++)
                {
                    float o = i * 10 * s, w = 10 * s;
                    var c = new Color(0.75f, 0.04f, 0.02f, hurtA * (1f - i / 8f));
                    RR(new Rect(0, o, W, w), c, 0); RR(new Rect(0, H - o - w, W, w), c, 0);
                    RR(new Rect(o, 0, w, H), c, 0); RR(new Rect(W - o - w, 0, w, H), c, 0);
                }

            // damage numbers
            for (int i = floaters.Count - 1; i >= 0; i--)
            {
                var f = floaters[i];
                f.t += Time.unscaledDeltaTime;
                floaters[i] = f;
                if (f.t > 0.9f) { floaters.RemoveAt(i); continue; }
                Vector3 sp = cam.WorldToScreenPoint(f.pos + Vector3.up * f.t * 1.2f);
                if (sp.z < 0) continue;
                float pop = f.crit ? 1f + 0.5f * Mathf.Clamp01(1f - f.t * 6f) : 1f;
                var st = St('h', Mathf.Round((f.crit ? 32f : 22f) * pop), TextAnchor.MiddleCenter);
                Txt(new Rect(sp.x - 60 * s, H - sp.y - 20 * s, 120 * s, 40 * s), f.text, st, new Color(f.col.r, f.col.g, f.col.b, 1f - f.t / 0.9f), true);
            }

            // world health bars + rival tags
            foreach (var h in Health.All)
            {
                if (h == null || h.Dead || h.team == Team.Player) continue;
                var ai = h.GetComponent<EnemyAI>();
                if (ai != null && ai.boss) continue;
                bool rival = h.team == Team.Rival;
                if (!rival && h.hp >= h.max) continue;
                var cc = h.GetComponent<CharacterController>();
                Vector3 sp = cam.WorldToScreenPoint(h.transform.position + Vector3.up * ((cc ? cc.height : 1.5f) + 0.45f));
                if (sp.z < 0 || sp.z > (rival ? 45f : 40f)) continue;
                float bw = (rival ? 90 : 70) * s;
                var br = new Rect(sp.x - bw * 0.5f, H - sp.y, bw, 8 * s);
                Bar(br, h.hp / h.max, rival ? CDanger : CGold, null, 4f);
                if (rival) Txt(new Rect(sp.x - 100 * s, br.y - 24 * s, 200 * s, 20 * s), h.displayName, St('b', 15f, TextAnchor.MiddleCenter), new Color(1f, 0.6f, 0.52f), true);
            }

            if (Hero == null) return;
            var hp = Hero.Hp;

            // player frame (bottom left)
            var pf = new Rect(36 * s, H - 136 * s, 470 * s, 100 * s);
            Card(pf, 18f);
            var badge = new Rect(pf.x + 18 * s, pf.y + 18 * s, 64 * s, 64 * s);
            RR(badge, new Color(0, 0, 0, 0.4f), 32 * s);
            float xpK = xp / (float)XpForNext();
            RR(badge, CAccent, 32 * s, 3 * s);
            Txt(badge, level.ToString(), St('h', 28f, TextAnchor.MiddleCenter), CAccent);
            Txt(new Rect(pf.x + 98 * s, pf.y + 12 * s, 260 * s, 30 * s), hp.displayName, St('h', 20f), CText);
            if (skillPoints > 0)
            {
                var sp2 = new Rect(pf.xMax - 142 * s, pf.y + 14 * s, 124 * s, 26 * s);
                float pulse = 0.6f + 0.4f * Mathf.Sin(Time.unscaledTime * 4f);
                RR(sp2, new Color(CAccent.r, CAccent.g, CAccent.b, 0.2f + 0.2f * pulse), 13 * s);
                Txt(sp2, "TAB · +" + skillPoints + " навык", St('h', 12f, TextAnchor.MiddleCenter), CAccent);
            }
            Color hpCol = Hero.ArmorOn ? new Color(1f, 0.62f, 0.22f) : hp.hp < hp.max * 0.3f ? CDanger : new Color(0.42f, 0.86f, 0.36f);
            Bar(new Rect(pf.x + 98 * s, pf.y + 46 * s, 352 * s, 24 * s), hp.hp / hp.max, hpCol, Mathf.CeilToInt(hp.hp) + " / " + Mathf.RoundToInt(hp.max));
            Bar(new Rect(pf.x + 98 * s, pf.y + 76 * s, 352 * s, 7 * s), xpK, new Color(0.5f, 0.72f, 1f), null, 3.5f);

            // ability slots (bottom centre)
            float slot = 78 * s, sg = 14 * s;
            bool frog = Hero.kind == HeroKind.Frog;
            float total = slot * 3 + sg * 2, sx = W * 0.5f - total * 0.5f, sy = H - slot - 52 * s;
            AbilitySlot(new Rect(sx, sy, slot, slot), "ЛКМ", frog ? "Удар" : "Выстрел", frog ? 0f : 0f, 1f, frog ? 0f : Hero.Charge01);
            AbilitySlot(new Rect(sx + slot + sg, sy, slot, slot), "Q", Hero.AbilityName, Hero.AbilityLeft, Hero.AbilityTotal, Hero.ArmorOn ? 1f : 0f, true);
            AbilitySlot(new Rect(sx + (slot + sg) * 2, sy, slot, slot), frog ? "Ctrl" : "ПКМ", frog ? "Перекат" : "Прицел", frog ? Hero.RollLeft : 0f, 1.1f, 0f);

            // timer + status (top centre)
            var tp = new Rect(W * 0.5f - 90 * s, 22 * s, 180 * s, 58 * s);
            Card(tp, 29f);
            Txt(tp, Clock(TimeLeft), St('h', 32f, TextAnchor.MiddleCenter), TimeLeft < 60f ? CDanger : CText);
            string st2 = ExtractOpen ? "Лиана открыта" : "Лиана через " + Clock(extractOpensAfter - (matchLength - TimeLeft));
            var stR = new Rect(W * 0.5f - 130 * s, 88 * s, 260 * s, 30 * s);
            RR(stR, ExtractOpen ? new Color(0.2f, 0.4f, 0.12f, 0.85f) : new Color(0, 0, 0, 0.5f), 15 * s);
            RR(new Rect(stR.x + 14 * s, stR.y + 11 * s, 8 * s, 8 * s), ExtractOpen ? CAccent : CMuted, 4 * s);
            Txt(new Rect(stR.x + 10 * s, stR.y, stR.width, stR.height), st2.ToUpperInvariant(), St('h', 13f, TextAnchor.MiddleCenter), ExtractOpen ? CAccent : new Color(0.85f, 0.88f, 0.82f));
            if (extractProgress > 0f) Bar(new Rect(W * 0.5f - 180 * s, 128 * s, 360 * s, 18 * s), extractProgress / extractHold, CAccent, "ЭВАКУАЦИЯ");

            // loot card (top right)
            float lw = 300 * s, lx = W - lw - 32 * s, ly = 22 * s;
            float lh = (96 + artNames.Count * 30) * s;
            Card(new Rect(lx, ly, lw, lh), 16f);
            Coin(new Rect(lx + 20 * s, ly + 22 * s, 22 * s, 22 * s));
            Txt(new Rect(lx + 52 * s, ly + 14 * s, 150 * s, 38 * s), gold.ToString(), St('h', 28f), CGold);
            Txt(new Rect(lx + lw - 150 * s, ly + 18 * s, 130 * s, 30 * s), kills + " <color=#9AA894>убито</color>", St('b', 17f, TextAnchor.MiddleRight), CText);
            Caption(new Rect(lx + 20 * s, ly + 60 * s, 200 * s, 22 * s), "Артефакты · " + artNames.Count);
            for (int i = 0; i < artNames.Count; i++)
            {
                float y = ly + (90 + i * 30) * s;
                Diamond(new Vector2(lx + 28 * s, y + 12 * s), 10 * s, CCyan);
                Txt(new Rect(lx + 44 * s, y, lw - 120 * s, 24 * s), artNames[i], St('r', 16f), CText);
                Txt(new Rect(lx + lw - 90 * s, y, 70 * s, 24 * s), artValues[i].ToString(), St('b', 15f, TextAnchor.MiddleRight), CGold);
            }

            // notifications (right, under loot)
            float ny = ly + lh + 16 * s;
            for (int i = toasts.Count - 1, n = 0; i >= 0; i--)
            {
                float age = Time.unscaledTime - toasts[i].Value;
                if (age > 5f) continue;
                float a = Mathf.Clamp01(5f - age) * Mathf.Clamp01(age * 6f);
                float slide = (1f - Mathf.Clamp01(age * 5f)) * 40f * s;
                var tr = new Rect(W - 32 * s - 360 * s + slide, ny + n * 52 * s, 360 * s, 44 * s);
                RR(tr, new Color(0.04f, 0.06f, 0.04f, 0.85f * a), 10 * s);
                RR(new Rect(tr.x, tr.y, 4 * s, tr.height), new Color(CAccent.r, CAccent.g, CAccent.b, a), 2 * s);
                Txt(new Rect(tr.x + 16 * s, tr.y, tr.width - 24 * s, tr.height), toasts[i].Key, St('r', 16f, TextAnchor.MiddleLeft, true), new Color(CText.r, CText.g, CText.b, a));
                n++;
            }

            // boss
            if (boss != null && !boss.Hp.Dead && (boss.target == hp || (boss.transform.position - Hero.transform.position).magnitude < 22f))
            {
                float bw = 640 * s, bx = W * 0.5f - bw * 0.5f, by = 160 * s;
                Txt(new Rect(bx, by, bw, 30 * s), "БОГОМОЛ-СТРАЖ", St('h', 20f, TextAnchor.MiddleCenter), new Color(0.95f, 0.75f, 1f), true);
                Bar(new Rect(bx, by + 34 * s, bw, 16 * s), boss.Hp.hp / boss.Hp.max, new Color(0.78f, 0.32f, 0.9f), null, 8f);
            }

            if (ExtractOpen) Marker(extractionPoint.position + Vector3.up * 3f, "Лиана", CAccent);
            DrawCrosshair();

            if (Cursor.lockState != CursorLockMode.Locked && state == State.Playing && !upgradeOpen)
            {
                var hr = new Rect(W * 0.5f - 200 * s, H * 0.62f, 400 * s, 40 * s);
                RR(hr, CShade, 20 * s);
                Txt(hr, "Щёлкните, чтобы управлять камерой", St('b', 17f, TextAnchor.MiddleCenter), CText);
            }
        }

        void AbilitySlot(Rect r, string key, string name, float left, float total, float glow, bool big = false)
        {
            float s = S;
            RR(new Rect(r.x + 2 * s, r.y + 4 * s, r.width, r.height), new Color(0, 0, 0, 0.35f), 14 * s);
            RR(r, CPanel, 14 * s);
            if (glow > 0.01f) RR(r, new Color(CAccent.r, CAccent.g, CAccent.b, 0.18f * glow), 14 * s);
            if (left > 0f && total > 0f)
            {
                float k = Mathf.Clamp01(left / total);
                RR(new Rect(r.x, r.y + r.height * (1f - k), r.width, r.height * k), new Color(0, 0, 0, 0.55f), 14 * s);
                Txt(r, left.ToString(left < 3f ? "0.0" : "0"), St('h', 24f, TextAnchor.MiddleCenter), CText, true);
            }
            else
            {
                Txt(new Rect(r.x, r.y + 6 * s, r.width, r.height * 0.6f), name.Substring(0, 1), St('h', 30f, TextAnchor.MiddleCenter), glow > 0.99f ? CAccent : new Color(0.85f, 0.95f, 0.75f, 0.9f));
            }
            RR(r, glow > 0.99f ? CAccent : CLine, 14 * s, Mathf.Max(1f, (glow > 0.99f ? 2f : 1.2f) * s));
            float kw = Mathf.Max(26f, 11f * key.Length + 10f) * s;
            KeyBadge(new Rect(r.x + r.width * 0.5f - kw * 0.5f, r.y - 12 * s, kw, 22 * s), key);
            Txt(new Rect(r.x - 30 * s, r.yMax + 4 * s, r.width + 60 * s, 22 * s), name, St('b', 14f, TextAnchor.MiddleCenter), CMuted, true);
        }

        void Marker(Vector3 world, string text, Color col)
        {
            float s = S;
            var cam = Camera.main;
            Vector3 sp = cam.WorldToScreenPoint(world);
            float dist = (world - cam.transform.position).magnitude;
            bool behind = sp.z < 0;
            if (behind) sp = -sp;
            float m = 80 * s;
            sp.x = Mathf.Clamp(sp.x, m, Screen.width - m);
            sp.y = Mathf.Clamp(sp.y, m, Screen.height - m);
            if (behind) sp.y = m;
            var c = new Vector2(sp.x, Screen.height - sp.y);
            Diamond(c, 16 * s, col);
            Diamond(c, 8 * s, new Color(0.05f, 0.1f, 0.04f));
            var r = new Rect(c.x - 70 * s, c.y + 14 * s, 140 * s, 26 * s);
            RR(r, CShade, 13 * s);
            Txt(r, text.ToUpperInvariant() + " · " + Mathf.RoundToInt(dist) + " М", St('h', 13f, TextAnchor.MiddleCenter), col);
        }

        void Line(Vector2 a, Vector2 b, float width, Color c)
        {
            var m = GUI.matrix;
            Vector2 d = b - a;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
            RR(new Rect(a.x, a.y - width * 0.5f, d.magnitude, width), c, width * 0.5f);
            GUI.matrix = m;
        }

        void DrawCrosshair()
        {
            float s = S;
            Vector2 c = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            bool bow = Hero.kind == HeroKind.Gecko;
            bool aim = bow && Mouse.current != null && Mouse.current.rightButton.isPressed;
            float k = Hero.Charge01;
            float gap = (bow ? Mathf.Lerp(aim ? 10f : 18f, 3f, k) : 8f) * s;
            float len = (bow ? 9f : 6f) * s;
            float w = Mathf.Max(1.5f, 2f * s);
            Color col = bow && k >= 1f ? CAccent : new Color(1f, 1f, 1f, 0.9f);
            Color sh = new Color(0, 0, 0, 0.45f);
            for (int pass = 0; pass < 2; pass++)
            {
                Color cc = pass == 0 ? sh : col;
                float o = pass == 0 ? 1f : 0f;
                RR(new Rect(c.x - w * 0.5f + o, c.y - gap - len + o, w, len), cc, w * 0.5f);
                RR(new Rect(c.x - w * 0.5f + o, c.y + gap + o, w, len), cc, w * 0.5f);
                RR(new Rect(c.x - gap - len + o, c.y - w * 0.5f + o, len, w), cc, w * 0.5f);
                RR(new Rect(c.x + gap + o, c.y - w * 0.5f + o, len, w), cc, w * 0.5f);
                RR(new Rect(c.x - w * 0.6f + o, c.y - w * 0.6f + o, w * 1.2f, w * 1.2f), cc, w * 0.6f);
            }
            if (bow && Hero.Charging)
            {
                float bw = 64 * s;
                Bar(new Rect(c.x - bw * 0.5f, c.y + 34 * s, bw, 6 * s), k, k >= 1f ? CAccent : new Color(1f, 0.92f, 0.7f), null, 3f);
            }
            float age = Time.unscaledTime - hitT;
            if (age < 0.25f)
            {
                float a = 1f - age / 0.25f;
                Color hc = hitKill ? new Color(CDanger.r, CDanger.g, CDanger.b, a) : hitCrit ? new Color(CGold.r, CGold.g, CGold.b, a) : new Color(1f, 1f, 1f, a);
                float r0 = (hitKill ? 10f : 7f) * s + age * 30f * s, r1 = r0 + (hitKill ? 12f : 8f) * s;
                float hw = (hitKill || hitCrit ? 3f : 2f) * s;
                for (int i = 0; i < 4; i++)
                {
                    float ang = (45f + 90f * i) * Mathf.Deg2Rad;
                    var d = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                    Line(c + d * r0, c + d * r1, hw, hc);
                }
            }
        }

        // ---------- Tab: upgrades ----------
        void DrawUpgrades()
        {
            float s = S;
            Dim(0.5f);
            float w = 1200 * s, h = 760 * s;
            var r = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            Card(r, 22f);
            Caption(new Rect(r.x + 40 * s, r.y + 26 * s, 300 * s, 22 * s), "Во время матча", CAccent);
            Txt(new Rect(r.x + 40 * s, r.y + 48 * s, 600 * s, 46 * s), "Улучшение героя", St('h', 34f), CText);
            // level + xp on the right
            Txt(new Rect(r.xMax - 340 * s, r.y + 30 * s, 300 * s, 30 * s), "Уровень " + level, St('h', 20f, TextAnchor.MiddleRight), CText);
            Bar(new Rect(r.xMax - 340 * s, r.y + 66 * s, 300 * s, 10 * s), xp / (float)XpForNext(), new Color(0.5f, 0.72f, 1f), null, 5f);
            Txt(new Rect(r.xMax - 340 * s, r.y + 78 * s, 300 * s, 22 * s), xp + " / " + XpForNext() + " опыта", St('r', 14f, TextAnchor.MiddleRight), CMuted);

            float colW = (w - 40 * s * 2 - 24 * s) * 0.5f;
            float lx = r.x + 40 * s, rx = lx + colW + 24 * s, top = r.y + 120 * s, colH = 470 * s;

            // skills
            RR(new Rect(lx, top, colW, colH), new Color(1, 1, 1, 0.03f), 16 * s);
            Caption(new Rect(lx + 22 * s, top + 18 * s, 300 * s, 22 * s), "Навыки", CAccent);
            var pr = new Rect(lx + colW - 180 * s, top + 12 * s, 160 * s, 32 * s);
            RR(pr, skillPoints > 0 ? CAccentDim : new Color(1, 1, 1, 0.05f), 16 * s);
            Txt(pr, "Очков: " + skillPoints, St('h', 15f, TextAnchor.MiddleCenter), skillPoints > 0 ? CAccent : CMuted);
            for (int i = 0; i < 4; i++)
            {
                var row = new Rect(lx + 14 * s, top + (60 + i * 100) * s, colW - 28 * s, 90 * s);
                RR(row, new Color(1, 1, 1, 0.035f), 12 * s);
                var ic = new Rect(row.x + 14 * s, row.y + 15 * s, 60 * s, 60 * s);
                RR(ic, new Color(0, 0, 0, 0.35f), 12 * s);
                RR(ic, ranks[i] > 0 ? CAccent : CLine, 12 * s, 1.5f * s);
                Txt(ic, SkillNames[i].Substring(0, 1), St('h', 26f, TextAnchor.MiddleCenter), ranks[i] > 0 ? CAccent : CMuted);
                Txt(new Rect(row.x + 90 * s, row.y + 12 * s, 260 * s, 30 * s), SkillNames[i], St('h', 19f), CText);
                Txt(new Rect(row.x + 90 * s, row.y + 42 * s, 300 * s, 40 * s), SkillDesc[i] + " за ранг", St('r', 14f, TextAnchor.UpperLeft, true), CMuted);
                Pips(new Rect(row.xMax - 166 * s, row.y + 18 * s, 66 * s, 8 * s), ranks[i], 3, CAccent);
                KeyBadge(new Rect(row.xMax - 92 * s, row.y + 10 * s, 26 * s, 22 * s), (i + 1).ToString());
                if (Button(new Rect(row.xMax - 166 * s, row.y + 38 * s, 150 * s, 40 * s), ranks[i] >= 3 ? "Максимум" : "Изучить", Btn.Secondary, skillPoints > 0 && ranks[i] < 3, 16f))
                    BuySkill(i);
            }

            // gear
            RR(new Rect(rx, top, colW, colH), new Color(1, 1, 1, 0.03f), 16 * s);
            Caption(new Rect(rx + 22 * s, top + 18 * s, 300 * s, 22 * s), "Снаряжение", CAccent);
            var gp = new Rect(rx + colW - 180 * s, top + 12 * s, 160 * s, 32 * s);
            RR(gp, new Color(CGold.r, CGold.g, CGold.b, 0.15f), 16 * s);
            Coin(new Rect(gp.x + 16 * s, gp.y + 8 * s, 16 * s, 16 * s));
            Txt(new Rect(gp.x + 20 * s, gp.y, gp.width - 20 * s, gp.height), gold.ToString(), St('h', 16f, TextAnchor.MiddleCenter), CGold);
            for (int i = 0; i < 5; i++)
            {
                var row = new Rect(rx + 14 * s, top + (60 + i * 80) * s, colW - 28 * s, 70 * s);
                RR(row, new Color(1, 1, 1, 0.035f), 12 * s);
                int max = GearCost[i].Length;
                Txt(new Rect(row.x + 18 * s, row.y + 8 * s, 300 * s, 28 * s), GearNames[i], St('h', 17f), CText);
                Txt(new Rect(row.x + 18 * s, row.y + 36 * s, 300 * s, 26 * s), GearDesc[i], St('r', 14f), CMuted);
                if (i < 4) Pips(new Rect(row.xMax - 250 * s, row.y + 31 * s, 56 * s, 7 * s), gear[i], max, CGold);
                int price = GearPrice(i);
                bool maxed = price < 0;
                bool can = !maxed && gold >= price && !(i == 4 && Hero.Hp.hp >= Hero.Hp.max);
                if (Button(new Rect(row.xMax - 170 * s, row.y + 14 * s, 154 * s, 42 * s), maxed ? "Максимум" : "Купить · " + price, Btn.Secondary, can, 16f))
                    BuyGear(i);
            }

            // stat tiles
            float ty = top + colH + 20 * s, tw = (w - 80 * s - 4 * 14 * s) / 5f;
            string rateLabel = Hero.kind == HeroKind.Gecko ? "Натяжение" : "Удары / с";
            string rateVal = Hero.kind == HeroKind.Gecko ? (0.75f / AttackSpeedMul).ToString("0.00") + " с" : (AttackSpeedMul / Hero.attackInterval).ToString("0.0");
            string[][] tiles = {
                new[] { "Здоровье", Mathf.CeilToInt(Hero.Hp.hp) + " / " + Mathf.RoundToInt(Hero.Hp.max) },
                new[] { "Урон", Mathf.RoundToInt(Hero.damage * DamageMul).ToString() },
                new[] { rateLabel, rateVal },
                new[] { "Скорость", "+" + Mathf.RoundToInt((SpeedMul - 1f) * 100f) + "%" },
                new[] { "Перезарядка Q", (Hero.abilityCooldown * CooldownMul).ToString("0.#") + " с" } };
            for (int i = 0; i < 5; i++)
            {
                var t = new Rect(r.x + 40 * s + i * (tw + 14 * s), ty, tw, 66 * s);
                RR(t, new Color(0, 0, 0, 0.3f), 12 * s);
                Caption(new Rect(t.x + 14 * s, t.y + 8 * s, tw, 20 * s), tiles[i][0]);
                Txt(new Rect(t.x + 14 * s, t.y + 28 * s, tw, 32 * s), tiles[i][1], St('h', 20f), CText);
            }
            Txt(new Rect(r.x + 40 * s, r.yMax - 54 * s, 700 * s, 30 * s),
                "Потраченное золото не попадёт в казну лагеря" + (goldSpent > 0 ? " · потрачено " + goldSpent : ""), St('r', 15f), new Color(0.85f, 0.75f, 0.5f));
            if (Button(new Rect(r.xMax - 240 * s, r.yMax - 66 * s, 200 * s, 46 * s), "Закрыть · Tab", Btn.Ghost, true, 16f)) SetUpgrades(false);
        }

        // ---------- pause ----------
        void DrawPause()
        {
            float s = S;
            GUI.enabled = !settingsOpen;
            Dim(0.6f);
            float w = 440 * s, h = 380 * s;
            var r = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            Card(r, 22f);
            Caption(new Rect(r.x, r.y + 34 * s, w, 22 * s), "Вылазка приостановлена");
            Txt(new Rect(r.x, r.y + 56 * s, w, 56 * s), "Пауза", St('h', 44f, TextAnchor.MiddleCenter), CText);
            float bx = r.x + 40 * s, bw = w - 80 * s;
            if (Button(new Rect(bx, r.y + 140 * s, bw, 54 * s), "ПРОДОЛЖИТЬ", Btn.Primary)) SetPaused(false);
            if (Button(new Rect(bx, r.y + 206 * s, bw, 50 * s), "Настройки")) settingsOpen = true;
            if (Button(new Rect(bx, r.y + 268 * s, bw, 50 * s), "Сдаться и в лагерь", Btn.Danger))
            { Time.timeScale = 1f; End(false, "Вы покинули вылазку."); }
        }

        // ---------- results ----------
        void DrawResult()
        {
            float s = S;
            Dim(0.6f);
            float w = 720 * s, h = 700 * s;
            var r = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            Card(r, 24f);
            Color tc = victory ? CAccent : CDanger;
            RR(new Rect(r.x, r.y, w, 6 * s), tc, 3 * s);
            Caption(new Rect(r.x, r.y + 36 * s, w, 22 * s), victory ? "Вылазка завершена" : "Вылазка провалена");
            Txt(new Rect(r.x, r.y + 60 * s, w, 70 * s), victory ? "Эвакуация!" : "Поражение", St('h', 54f, TextAnchor.MiddleCenter), tc, true);
            Txt(new Rect(r.x + 40 * s, r.y + 130 * s, w - 80 * s, 30 * s), resultLine, St('r', 18f, TextAnchor.MiddleCenter), CMuted);

            float tw = (w - 80 * s - 3 * 12 * s) / 4f, ty = r.y + 182 * s;
            string[][] tiles = {
                new[] { "Золото", gold.ToString() }, new[] { "Артефакты", artNames.Count + " · " + artValues.Sum() },
                new[] { "Убито", kills.ToString() }, new[] { "Уровень", level.ToString() } };
            for (int i = 0; i < 4; i++)
            {
                var t = new Rect(r.x + 40 * s + i * (tw + 12 * s), ty, tw, 84 * s);
                RR(t, new Color(0, 0, 0, 0.3f), 12 * s);
                Caption(new Rect(t.x, t.y + 12 * s, tw, 20 * s), tiles[i][0]);
                Txt(new Rect(t.x, t.y + 34 * s, tw, 40 * s), tiles[i][1], St('h', 26f, TextAnchor.MiddleCenter), i == 0 ? CGold : i == 1 ? CCyan : CText);
            }

            var er = new Rect(r.x + 40 * s, ty + 104 * s, w - 80 * s, 64 * s);
            RR(er, victory ? new Color(CGold.r, CGold.g, CGold.b, 0.12f) : new Color(1, 1, 1, 0.04f), 12 * s);
            if (victory)
            {
                Coin(new Rect(er.x + 22 * s, er.y + 21 * s, 22 * s, 22 * s));
                Txt(new Rect(er.x + 56 * s, er.y, 400 * s, er.height), "В казну лагеря", St('b', 19f), CText);
                Txt(new Rect(er.xMax - 260 * s, er.y, 240 * s, er.height), "+" + earned, St('h', 32f, TextAnchor.MiddleRight), CGold);
            }
            else Txt(er, "Добыча потеряна", St('b', 19f, TextAnchor.MiddleCenter), CMuted);

            Caption(new Rect(r.x + 40 * s, er.yMax + 22 * s, 300 * s, 20 * s), "Рейтинг", CAccent);
            var board = Board().Take(5).ToList();
            for (int i = 0; i < board.Count; i++)
            {
                float y = er.yMax + (52 + i * 34) * s;
                bool mine = GameSettings.HasNick && board[i].Key.StartsWith(GameSettings.Nick + " (");
                if (mine) RR(new Rect(r.x + 30 * s, y - 2 * s, w - 60 * s, 32 * s), CAccentDim, 8 * s);
                Txt(new Rect(r.x + 44 * s, y, 40 * s, 28 * s), (i + 1) + ".", St('h', 16f), CMuted);
                Txt(new Rect(r.x + 84 * s, y, 400 * s, 28 * s), board[i].Key, St(mine ? 'b' : 'r', 17f), mine ? CAccent : CText);
                Txt(new Rect(r.xMax - 200 * s, y, 160 * s, 28 * s), board[i].Value.ToString(), St('h', 17f, TextAnchor.MiddleRight), CText);
            }
            if (Button(new Rect(r.x + 40 * s, r.yMax - 82 * s, w - 80 * s, 54 * s), "В ЛАГЕРЬ", Btn.Primary, true, 20f))
                SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        // ---------- nickname ----------
        GUIStyle fieldStyle;
        GUIStyle FieldStyle()
        {
            if (fieldStyle == null || fieldStyle.fontSize != Mathf.RoundToInt(24 * S))
            {
                fieldStyle = new GUIStyle(GUI.skin.textField)
                {
                    font = bodyBoldFont ? bodyBoldFont : bodyFont,
                    fontSize = Mathf.RoundToInt(24 * S),
                    alignment = TextAnchor.MiddleLeft,
                    padding = new RectOffset(Mathf.RoundToInt(16 * S), Mathf.RoundToInt(16 * S), 0, 0)
                };
                fieldStyle.normal.background = fieldStyle.focused.background = fieldStyle.hover.background = fieldStyle.active.background = null;
                fieldStyle.normal.textColor = fieldStyle.focused.textColor = fieldStyle.hover.textColor = CText;
            }
            return fieldStyle;
        }

        string TextInput(Rect r, string value, string controlName)
        {
            float s = S;
            bool focused = GUI.GetNameOfFocusedControl() == controlName;
            RR(r, new Color(0, 0, 0, 0.45f), 12 * s);
            RR(r, focused ? CAccent : new Color(1, 1, 1, 0.15f), 12 * s, (focused ? 2f : 1.2f) * s);
            GUI.SetNextControlName(controlName);
            return GUI.TextField(r, value ?? "", 16, FieldStyle());
        }

        void DrawNickPrompt()
        {
            float s = S;
            Dim(0.7f);
            float w = 640 * s, h = 340 * s;
            var r = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            Card(r, 22f);
            Caption(new Rect(r.x + 40 * s, r.y + 32 * s, 400 * s, 22 * s), GameSettings.HasNick ? "Смена ника" : "Добро пожаловать в Реликт", CAccent);
            Txt(new Rect(r.x + 40 * s, r.y + 56 * s, w - 80 * s, 44 * s), "Как вас звать в джунглях?", St('h', 28f), CText);
            Txt(new Rect(r.x + 40 * s, r.y + 100 * s, w - 80 * s, 26 * s), "Ник виден в рейтинге и над героем · от 2 до 16 символов", St('r', 16f), CMuted);
            nickDraft = TextInput(new Rect(r.x + 40 * s, r.y + 146 * s, w - 80 * s, 58 * s), nickDraft, "nick");
            if (GUI.GetNameOfFocusedControl() != "nick") GUI.FocusControl("nick");
            string clean = GameSettings.CleanNick(nickDraft);
            bool ok = clean.Length >= 2;
            var e = Event.current;
            bool enter = e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);
            bool canCancel = GameSettings.HasNick;
            float bw = canCancel ? (w - 80 * s - 16 * s) * 0.5f : w - 80 * s;
            if (canCancel && Button(new Rect(r.x + 40 * s, r.yMax - 92 * s, bw, 54 * s), "Отмена", Btn.Ghost))
            { nickPrompt = false; GUI.FocusControl(null); }
            if (Button(new Rect(r.xMax - 40 * s - bw, r.yMax - 92 * s, bw, 54 * s), "ГОТОВО", Btn.Primary, ok) || (enter && ok))
            {
                GameSettings.Nick = clean;
                GameSettings.Save();
                nickPrompt = false;
                GUI.FocusControl(null);
            }
        }

        // ---------- settings ----------
        void CloseSettings()
        {
            settingsOpen = false;
            GameSettings.Apply(false);
            GameSettings.Save();
        }

        bool Switch(Rect r, bool v)
        {
            float s = S;
            var track = new Rect(r.x, r.y + r.height * 0.5f - 14 * s, 54 * s, 28 * s);
            bool hover = track.Contains(Event.current.mousePosition);
            RR(track, v ? CAccent : new Color(1, 1, 1, hover ? 0.2f : 0.12f), 14 * s);
            var knob = new Rect(v ? track.xMax - 25 * s : track.x + 3 * s, track.y + 3 * s, 22 * s, 22 * s);
            RR(knob, v ? new Color(0.06f, 0.1f, 0.04f) : CText, 11 * s);
            Txt(new Rect(track.xMax + 12 * s, r.y, 100 * s, r.height), v ? "Вкл" : "Выкл", St('b', 16f), v ? CAccent : CMuted);
            if (GUI.Button(new Rect(track.x, track.y, track.width + 70 * s, track.height), GUIContent.none, GUIStyle.none)) { v = !v; SFX.Play(Sfx.Click, 0.5f, 0f); }
            return v;
        }

        GUIStyle invisible, invisibleThumb;
        float Slider(Rect r, float v, float min, float max, string valueText)
        {
            float s = S;
            if (invisible == null)
            {
                invisible = new GUIStyle();
                invisibleThumb = new GUIStyle();
            }
            invisibleThumb.fixedWidth = 24 * s; invisibleThumb.fixedHeight = 28 * s;
            invisible.fixedHeight = 28 * s;
            float vw = 76 * s;
            var tr = new Rect(r.x, r.y + r.height * 0.5f - 3 * s, r.width - vw - 12 * s, 6 * s);
            float k = Mathf.InverseLerp(min, max, v);
            RR(tr, new Color(1, 1, 1, 0.12f), 3 * s);
            RR(new Rect(tr.x, tr.y, tr.width * k, tr.height), CAccent, 3 * s);
            var kn = new Rect(tr.x + tr.width * k - 10 * s, tr.center.y - 10 * s, 20 * s, 20 * s);
            RR(kn, CText, 10 * s);
            RR(kn, CAccent, 10 * s, 2 * s);
            v = GUI.HorizontalSlider(new Rect(tr.x - 12 * s, tr.center.y - 14 * s, tr.width + 24 * s, 28 * s), v, min, max, invisible, invisibleThumb);
            var vr = new Rect(r.xMax - vw, r.y + r.height * 0.5f - 15 * s, vw, 30 * s);
            RR(vr, new Color(0, 0, 0, 0.35f), 8 * s);
            Txt(vr, valueText, St('h', 15f, TextAnchor.MiddleCenter), CText);
            return v;
        }

        int Stepper(Rect r, int index, int count, string text)
        {
            float s = S;
            float bw = 44 * s;
            RR(r, new Color(0, 0, 0, 0.35f), 10 * s);
            if (Button(new Rect(r.x, r.y, bw, r.height), "‹", Btn.Ghost, true, 22f)) index = (index - 1 + count) % count;
            Txt(new Rect(r.x + bw, r.y, r.width - bw * 2, r.height), text, St('b', 17f, TextAnchor.MiddleCenter), CText);
            if (Button(new Rect(r.xMax - bw, r.y, bw, r.height), "›", Btn.Ghost, true, 22f)) index = (index + 1) % count;
            return index;
        }

        void DrawSettings()
        {
            float s = S;
            Dim(0.65f);
            float w = 880 * s, h = 860 * s;
            var r = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            Card(r, 22f);
            Txt(new Rect(r.x + 44 * s, r.y + 28 * s, w, 46 * s), "Настройки", St('h', 32f), CText);

            float lx = r.x + 44 * s, cx = r.x + w * 0.46f, cw = r.xMax - 44 * s - cx, rh = 44 * s;
            float y = r.y + 96 * s;

            void Section(string title)
            {
                Caption(new Rect(lx, y, 400 * s, 22 * s), title, CAccent);
                RR(new Rect(lx, y + 28 * s, w - 88 * s, 1.2f * s), new Color(1, 1, 1, 0.08f), 0);
                y += 40 * s;
            }
            void Row(string name, string hint = null)
            {
                Txt(new Rect(lx, y + (hint != null ? 0 : 6 * s), cx - lx - 16 * s, 28 * s), name, St('b', 18f), CText);
                if (hint != null) Txt(new Rect(lx, y + 26 * s, cx - lx - 16 * s, 20 * s), hint, St('r', 13f), CMuted);
            }

            Section("Профиль");
            Row("Ник");
            string nick = GameSettings.CleanNick(TextInput(new Rect(cx, y, cw, rh), GameSettings.Nick, "settingsNick"));
            if (nick.Length >= 1) GameSettings.Nick = nick;
            y += 64 * s;

            Section("Управление");
            Row("Чувствительность мыши");
            GameSettings.Sensitivity = Slider(new Rect(cx, y, cw, rh), GameSettings.Sensitivity, 0.03f, 0.3f, Mathf.RoundToInt(GameSettings.Sensitivity * 100f).ToString());
            y += 54 * s;
            Row("Инверсия по вертикали");
            GameSettings.InvertY = Switch(new Rect(cx, y, cw, rh), GameSettings.InvertY);
            y += 64 * s;

            Section("Изображение");
            Row("Поле зрения");
            float fov = Slider(new Rect(cx, y, cw, rh), GameSettings.Fov, 50f, 90f, Mathf.RoundToInt(GameSettings.Fov) + "°");
            if (!Mathf.Approximately(fov, GameSettings.Fov)) { GameSettings.Fov = fov; GameSettings.Apply(false); }
            y += 54 * s;
            Row("Качество графики");
            var qn = QualitySettings.names;
            int q = Mathf.Clamp(GameSettings.Quality, 0, qn.Length - 1);
            int nq = Stepper(new Rect(cx, y, cw, rh), q, qn.Length, qn[q]);
            if (nq != q) { GameSettings.Quality = nq; GameSettings.Apply(false); }
            y += 54 * s;
            Row("Полный экран", Application.isEditor ? "в редакторе — через окно Game" : "F11 — быстрое переключение");
            bool fsv = Switch(new Rect(cx, y, cw, rh), GameSettings.Fullscreen);
            if (fsv != GameSettings.Fullscreen) { GameSettings.Fullscreen = fsv; if (!Application.isEditor) GameSettings.ApplyScreen(); }
            y += 54 * s;
            Row("Разрешение");
            var rs = GameSettings.Resolutions();
            int count = rs.Length + 1;
            int idx = GameSettings.ResolutionIndex < 0 ? 0 : GameSettings.ResolutionIndex + 1;
            int nidx = Stepper(new Rect(cx, y, cw, rh), idx, count, GameSettings.ResolutionLabel());
            if (nidx != idx) { GameSettings.ResolutionIndex = nidx - 1; if (!Application.isEditor) GameSettings.ApplyScreen(); }
            y += 54 * s;
            Row("Вертикальная синхронизация");
            bool vs = Switch(new Rect(cx, y, cw, rh), GameSettings.VSync);
            if (vs != GameSettings.VSync) { GameSettings.VSync = vs; GameSettings.Apply(false); }
            y += 54 * s;
            Row("Цифры урона");
            GameSettings.DamageNumbers = Switch(new Rect(cx, y, cw, rh), GameSettings.DamageNumbers);
            y += 64 * s;

            Section("Звук");
            Row("Громкость");
            float vol = Slider(new Rect(cx, y, cw, rh), GameSettings.Volume, 0f, 1f, Mathf.RoundToInt(GameSettings.Volume * 100f) + "%");
            if (!Mathf.Approximately(vol, GameSettings.Volume)) { GameSettings.Volume = vol; AudioListener.volume = vol; }

            float by = r.yMax - 84 * s, bw2 = 220 * s;
            if (Button(new Rect(r.x + 44 * s, by, bw2, 52 * s), "По умолчанию", Btn.Ghost, true, 17f)) GameSettings.ResetToDefaults();
            if (Button(new Rect(r.xMax - 44 * s - bw2, by, bw2, 52 * s), "ГОТОВО", Btn.Primary, true, 19f)) CloseSettings();
        }
    }
}
