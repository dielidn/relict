using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Relict
{
    public class MatchManager : MonoBehaviour
    {
        public static MatchManager I;

        [Header("Prefabs")]
        public GameObject frogPrefab;
        public GameObject geckoPrefab;
        public GameObject rivalModel;
        public RuntimeAnimatorController rivalController;
        public GameObject[] artifactModels;
        public GameObject[] vineModels;

        [Header("Places")]
        public Transform playerSpawn;
        public Transform extractionPoint;
        public Transform bossSpot;
        public Transform rivalSpawn;
        public GameObject selectDisplay;

        [Header("Rules")]
        public float matchLength = 420f;
        public float extractOpensAfter = 120f;
        public float extractHold = 6f;
        public float extractRadius = 3.5f;
        public float mapRadius = 46f;

        public enum State { Select, Playing, Paused, Result }
        public State state = State.Select;
        public bool Playing => state == State.Playing;

        public HeroController Hero { get; private set; }
        public float TimeLeft { get; private set; }
        public bool ExtractOpen { get; private set; }

        // run state
        HeroKind chosen = HeroKind.Frog;
        int gold, kills, xp, level = 1, skillPoints;
        readonly int[] ranks = new int[4];
        readonly List<string> artNames = new List<string>();
        readonly List<int> artValues = new List<int>();
        float extractProgress, nextWave;
        bool victory;
        string resultLine = "";
        int earned;
        EnemyAI boss;
        Light extractLight;
        Renderer extractBeam;
        readonly List<RivalAI> rivals = new List<RivalAI>();

        // multipliers used by the hero
        public float DamageMul => (1f + 0.15f * ranks[0]) * (1f + 0.25f * WeaponTier(chosen));
        public float SpeedMul => 1f + 0.07f * ranks[2];
        public float AttackSpeedMul => 1f + 0.1f * ranks[2];
        public float CooldownMul => 1f - 0.15f * ranks[3];
        public float AbilityPowerMul => 1f + 0.25f * ranks[3];

        static readonly string[] SkillNames = { "Сила", "Живучесть", "Ловкость", "Инстинкт" };
        static readonly string[] SkillDesc = { "+15% урона", "+30 к здоровью", "+7% скорости, +10% темпа атаки", "−15% перезарядки, +25% силы способности" };
        static readonly string[] ArtifactNames = { "Янтарный глаз", "Коготь предка", "Семя лианы", "Панцирь древних", "Слеза кристалла", "Перо первоящера" };
        static readonly string[] WeaponNames = { "I", "II", "III" };

        // ---------- persistence ----------
        static int Bank { get => PlayerPrefs.GetInt("relict_bank", 0); set => PlayerPrefs.SetInt("relict_bank", value); }
        static int WeaponTier(HeroKind k) => PlayerPrefs.GetInt("relict_tier_" + k, 0);
        static void SetWeaponTier(HeroKind k, int t) => PlayerPrefs.SetInt("relict_tier_" + k, t);
        static int UpgradeCost(int tier) => tier == 0 ? 400 : 1000;

        static List<KeyValuePair<string, int>> Board()
        {
            string s = PlayerPrefs.GetString("relict_board", "Скар-Варан|1450;Тихий Хамелеон|1180;Кваква|920;Ящер Ксаль|760;Жаба-Гром|540");
            var list = new List<KeyValuePair<string, int>>();
            foreach (var e in s.Split(';'))
            {
                var p = e.Split('|');
                if (p.Length == 2 && int.TryParse(p[1], out int v)) list.Add(new KeyValuePair<string, int>(p[0], v));
            }
            return list.OrderByDescending(x => x.Value).ToList();
        }

        static void AddToBoard(string name, int score)
        {
            var b = Board();
            b.Add(new KeyValuePair<string, int>(name, score));
            b = b.OrderByDescending(x => x.Value).Take(10).ToList();
            PlayerPrefs.SetString("relict_board", string.Join(";", b.Select(x => x.Key + "|" + x.Value)));
        }

        // ---------- lifecycle ----------
        void Awake() { I = this; }

        void Start()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            Time.timeScale = 1f;
            BuildExtraction();
        }

        public void StartMatch(HeroKind kind)
        {
            chosen = kind;
            if (selectDisplay) selectDisplay.SetActive(false);
            var prefab = kind == HeroKind.Frog ? frogPrefab : geckoPrefab;
            var go = Instantiate(prefab, playerSpawn.position, playerSpawn.rotation);
            Hero = go.GetComponent<HeroController>();
            Hero.Hp.Died += k => StartCoroutine(EndAfter(2.5f, false, "Вы пали в джунглях. Добыча потеряна."));
            Camera.main.GetComponent<ThirdPersonCamera>().SetTarget(go.transform);
            TimeLeft = matchLength;
            nextWave = 45f;
            state = State.Playing;
            LockCursor(true);
            SpawnWorld();
            Toast("Найдите артефакты и доберитесь до лианы эвакуации");
        }

        void LockCursor(bool on)
        {
            Cursor.lockState = on ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !on;
        }

        Vector3 FreePoint(float minR, float maxR, Vector3 avoid, float avoidDist)
        {
            for (int i = 0; i < 40; i++)
            {
                var r = Random.insideUnitCircle.normalized * Random.Range(minR, maxR);
                var p = new Vector3(r.x, 0, r.y);
                if ((p - avoid).magnitude < avoidDist) continue;
                if ((p - extractionPoint.position).magnitude < 6f) continue;
                if (Physics.CheckSphere(p + Vector3.up * 1.1f, 0.9f, ~0, QueryTriggerInteraction.Ignore)) continue;
                return p;
            }
            return new Vector3(Random.Range(-20f, 20f), 0, Random.Range(-20f, 20f));
        }

        void SpawnWorld()
        {
            Vector3 sp = playerSpawn.position;
            // artifacts, each guarded
            var names = ArtifactNames.OrderBy(_ => Random.value).Take(5).ToList();
            var placed = new List<Vector3>();
            foreach (var n in names)
            {
                Vector3 p = sp;
                for (int k = 0; k < 20; k++)
                {
                    p = FreePoint(14f, mapRadius - 4f, sp, 16f);
                    if (placed.All(q => (q - p).magnitude > 14f)) break;
                }
                placed.Add(p);
                Pickup.Artifact(p, n, Random.Range(120, 320), RandomModel(artifactModels));
                if (Random.value < 0.5f) EnemyAI.Create(InsectKind.Beetle, p + new Vector3(2.5f, 0, 1.5f));
                else for (int a = 0; a < 3; a++) EnemyAI.Create(InsectKind.Ant, p + Random.insideUnitSphere.Flat() * 3f + Vector3.right * 2f);
            }
            // roaming packs
            for (int i = 0; i < 4; i++)
            {
                var p = FreePoint(15f, mapRadius - 3f, sp, 18f);
                for (int a = 0; a < 3; a++) EnemyAI.Create(InsectKind.Ant, p + Random.insideUnitSphere.Flat() * 2.5f);
            }
            for (int i = 0; i < 3; i++) EnemyAI.Create(InsectKind.Beetle, FreePoint(18f, mapRadius - 3f, sp, 20f));
            // boss guarding the heart
            boss = EnemyAI.Create(InsectKind.Mantis, bossSpot.position, true);
            Pickup.Artifact(bossSpot.position + bossSpot.forward * 3f, "Сердце Реликта", 600, RandomModel(artifactModels));
            // heal spores
            for (int i = 0; i < 7; i++) Pickup.Heal(FreePoint(8f, mapRadius - 4f, sp, 6f), 45);
            // rival squad
            for (int i = 0; i < 2; i++)
            {
                var rp = rivalSpawn.position + new Vector3(i * 2f, 0, i * 1.5f);
                var r = RivalAI.Create(rivalModel, rivalController, rp, i == 0 ? "Варан Скар" : "Ящерица Ксаль");
                rivals.Add(r);
            }
        }

        static GameObject RandomModel(GameObject[] arr) => arr != null && arr.Length > 0 ? arr[Random.Range(0, arr.Length)] : null;

        void BuildExtraction()
        {
            var root = extractionPoint;
            var beam = Art.Part(root, PrimitiveType.Cylinder, Vector3.up * 15f, new Vector3(1.2f, 15f, 1.2f), Art.Additive(new Color(0.4f, 1f, 0.5f, 0.12f)));
            beam.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            extractBeam = beam.GetComponent<Renderer>();
            beam.SetActive(false);
            for (int i = 0; i < 12; i++)
            {
                float a = i / 12f * Mathf.PI * 2f;
                var s = Art.Part(root, PrimitiveType.Sphere, new Vector3(Mathf.Cos(a), 0.15f, Mathf.Sin(a)) * extractRadius, Vector3.one * 0.25f,
                    Art.Lit(new Color(0.3f, 0.9f, 0.4f), 0.5f, 0f, new Color(0.3f, 1.2f, 0.4f)));
                s.transform.localPosition = new Vector3(Mathf.Cos(a) * extractRadius, 0.15f, Mathf.Sin(a) * extractRadius);
            }
            if (vineModels != null)
                for (int i = 0; i < 5; i++)
                {
                    var vm = RandomModel(vineModels);
                    if (vm == null) break;
                    var v = Instantiate(vm, root);
                    v.transform.localPosition = new Vector3(Random.Range(-1.2f, 1.2f), 2.5f + i * 1.6f, Random.Range(-1.2f, 1.2f));
                    v.transform.localScale = v.transform.localScale * 1.8f;
                }
            extractLight = new GameObject("ExtractLight").AddComponent<Light>();
            extractLight.type = LightType.Point; extractLight.range = 12f; extractLight.intensity = 1f; extractLight.color = new Color(1f, 0.4f, 0.3f);
            extractLight.transform.SetParent(root, false);
            extractLight.transform.localPosition = Vector3.up * 3f;
        }

        // ---------- update ----------
        void Update()
        {
            var kb = Keyboard.current;
            if (state == State.Playing || state == State.Paused)
            {
                if (kb != null && kb.escapeKey.wasPressedThisFrame) SetPaused(state == State.Playing);
                if (state == State.Playing && Cursor.lockState != CursorLockMode.Locked && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame) LockCursor(true);
            }
            if (state != State.Playing) return;

            float dt = Time.deltaTime;
            TimeLeft -= dt;

            if (!ExtractOpen && matchLength - TimeLeft >= extractOpensAfter)
            {
                ExtractOpen = true;
                extractBeam.gameObject.SetActive(true);
                extractLight.color = new Color(0.4f, 1f, 0.5f);
                extractLight.intensity = 6f;
                Toast("Лиана эвакуации раскрылась! Рой просыпается…");
            }
            if (extractLight) extractLight.intensity = ExtractOpen ? 5f + Mathf.Sin(Time.time * 3f) * 1.5f : 1f;

            // skills
            if (skillPoints > 0 && kb != null)
            {
                for (int i = 0; i < 4; i++)
                {
                    var key = i == 0 ? kb.digit1Key : i == 1 ? kb.digit2Key : i == 2 ? kb.digit3Key : kb.digit4Key;
                    if (key.wasPressedThisFrame && ranks[i] < 3)
                    {
                        ranks[i]++; skillPoints--;
                        if (i == 1) { Hero.Hp.SetMax(Hero.Hp.max + 30f); Hero.Hp.Heal(30f); }
                        Toast(SkillNames[i] + " → ранг " + ranks[i]);
                    }
                }
            }

            // extraction
            if (ExtractOpen && Hero != null && !Hero.Hp.Dead)
            {
                Vector3 d = Hero.transform.position - extractionPoint.position; d.y = 0;
                if (d.magnitude < extractRadius)
                {
                    extractProgress += dt;
                    if (extractProgress >= extractHold) { End(true, "Вы ушли по лиане с добычей!"); return; }
                }
                else extractProgress = Mathf.Max(0f, extractProgress - dt * 2f);
            }

            // insect director
            nextWave -= dt;
            if (nextWave <= 0f)
            {
                nextWave = ExtractOpen ? 25f : 40f;
                int alive = FindObjectsByType<EnemyAI>(FindObjectsInactive.Exclude).Count(e => !e.Dying);
                if (alive < 26 && Hero != null)
                {
                    Vector3 hp = Hero.transform.position;
                    Vector3 p = FreePoint(mapRadius * 0.6f, mapRadius - 2f, hp, 20f);
                    int n = ExtractOpen ? 5 : 3;
                    for (int a = 0; a < n; a++)
                    {
                        var e = EnemyAI.Create(InsectKind.Ant, p + Random.insideUnitSphere.Flat() * 2.5f);
                        e.aggro = 38f; // hunting wave
                    }
                    if (ExtractOpen) EnemyAI.Create(InsectKind.Beetle, p + Vector3.right * 3f).aggro = 38f;
                }
            }

            if (TimeLeft <= 0f) End(false, "Джунгли сомкнулись. Вы не успели к лиане.");
        }

        void SetPaused(bool pause)
        {
            state = pause ? State.Paused : State.Playing;
            Time.timeScale = pause ? 0f : 1f;
            LockCursor(!pause);
        }

        IEnumerator EndAfter(float t, bool win, string line)
        {
            yield return new WaitForSeconds(t);
            if (state == State.Playing) End(win, line);
        }

        void End(bool win, string line)
        {
            victory = win;
            resultLine = line;
            state = State.Result;
            LockCursor(false);
            earned = 0;
            if (win)
            {
                earned = gold + artValues.Sum();
                Bank += earned;
                AddToBoard("Вы (" + (chosen == HeroKind.Frog ? "Лягушка" : "Геккон") + ")", earned);
                PlayerPrefs.Save();
            }
        }

        // ---------- events ----------
        public void OnInsectKilled(EnemyAI e, Health killer)
        {
            int g = e.gold;
            int coins = Mathf.Clamp(g / 8, 1, 12);
            for (int i = 0; i < coins; i++) Pickup.Gold(e.transform.position, Mathf.Max(1, g / coins));
            if (Random.value < (e.boss ? 1f : 0.12f)) Pickup.Heal(e.transform.position + Vector3.right, 35);
            if (killer != null && Hero != null && killer == Hero.Hp)
            {
                kills++;
                AddXp(e.xp);
            }
            if (e.boss) Toast("Богомол-Страж повержен!");
        }

        public void OnRivalKilled(RivalAI r, Health killer)
        {
            int coins = Mathf.Clamp(r.gold / 10, 2, 15);
            for (int i = 0; i < coins; i++) Pickup.Gold(r.transform.position, Mathf.Max(5, (r.gold + 60) / coins));
            for (int i = 0; i < r.artifacts.Count; i++)
                Pickup.Artifact(r.transform.position + Random.insideUnitSphere.Flat() * 1.5f, r.artifacts[i], r.artifactValues[i], RandomModel(artifactModels));
            if (killer != null && Hero != null && killer == Hero.Hp) { kills++; AddXp(120); }
            Toast(r.Hp.displayName + " выбит из матча");
        }

        void AddXp(int amount)
        {
            xp += amount;
            while (xp >= XpForNext())
            {
                xp -= XpForNext();
                level++;
                skillPoints++;
                if (Hero) Hero.Hp.Heal(Hero.Hp.max * 0.3f);
                Toast("Уровень " + level + "! Очко навыка: клавиши 1–4");
            }
        }

        int XpForNext() => 60 + 50 * level;

        public void Collect(Pickup p, RivalAI rival)
        {
            if (rival != null)
            {
                if (p.kind == PickupKind.Artifact) { rival.artifacts.Add(p.title); rival.artifactValues.Add(p.amount); Toast(rival.Hp.displayName + " забрал артефакт «" + p.title + "»"); }
                else if (p.kind == PickupKind.Gold) rival.gold += p.amount;
                return;
            }
            switch (p.kind)
            {
                case PickupKind.Gold: gold += p.amount; break;
                case PickupKind.Heal: Hero.Hp.Heal(p.amount); break;
                case PickupKind.Artifact:
                    artNames.Add(p.title); artValues.Add(p.amount);
                    Toast("Артефакт «" + p.title + "» (" + p.amount + " зол.)");
                    VFX.Burst(p.transform.position, new Color(0.4f, 0.9f, 1f), 40, 4f, 0.2f);
                    break;
            }
        }

        public Vector3 RivalGoal(Vector3 from)
        {
            if (ExtractOpen && (TimeLeft < 150f)) return extractionPoint.position;
            Pickup best = null; float bd = float.MaxValue;
            foreach (var p in Pickup.All)
            {
                if (p == null || p.kind != PickupKind.Artifact) continue;
                float d = (p.transform.position - from).sqrMagnitude;
                if (boss != null && !boss.Hp.Dead && (p.transform.position - bossSpot.position).magnitude < 8f) continue;
                if (d < bd) { bd = d; best = p; }
            }
            if (best != null) return best.transform.position;
            return ExtractOpen ? extractionPoint.position : new Vector3(Mathf.Sin(Time.time * 0.05f) * 25f, 0, Mathf.Cos(Time.time * 0.05f) * 25f);
        }

        public void RivalTick(RivalAI r, float dt)
        {
            Vector3 pos = r.transform.position;
            for (int i = Pickup.All.Count - 1; i >= 0; i--)
            {
                var p = Pickup.All[i];
                if (p == null || p.kind == PickupKind.Heal) continue;
                Vector3 d = p.transform.position - pos; d.y = 0;
                if (d.magnitude < 1.3f) { Collect(p, r); Destroy(p.gameObject); }
            }
            if (ExtractOpen)
            {
                Vector3 d = pos - extractionPoint.position; d.y = 0;
                if (d.magnitude < extractRadius)
                {
                    r.extractProgress += dt;
                    if (r.extractProgress >= extractHold)
                    {
                        Toast(r.Hp.displayName + " эвакуировался с " + r.artifacts.Count + " артеф.");
                        AddToBoard(r.Hp.displayName, r.gold + r.artifactValues.Sum());
                        rivals.Remove(r);
                        Destroy(r.gameObject);
                    }
                }
                else r.extractProgress = 0f;
            }
        }

        // ---------- feedback ----------
        struct Floater { public Vector3 pos; public string text; public float t; public Color col; }
        readonly List<Floater> floaters = new List<Floater>();
        readonly List<KeyValuePair<string, float>> toasts = new List<KeyValuePair<string, float>>();

        public void DamageNumber(Vector3 pos, float amount, bool toPlayer)
        {
            if (amount < 1.5f) return;
            floaters.Add(new Floater
            {
                pos = pos + Random.insideUnitSphere * 0.3f,
                text = Mathf.RoundToInt(amount).ToString(),
                t = 0f,
                col = toPlayer ? new Color(1f, 0.35f, 0.3f) : new Color(1f, 0.92f, 0.6f)
            });
        }

        public void Toast(string s)
        {
            toasts.Add(new KeyValuePair<string, float>(s, Time.unscaledTime));
            if (toasts.Count > 5) toasts.RemoveAt(0);
        }

        // ---------- UI ----------
        Texture2D white;
        GUIStyle label, big, title, small, center, button, panel;
        float S => Screen.height / 1080f;

        void Styles()
        {
            if (white == null) { white = new Texture2D(1, 1); white.SetPixel(0, 0, Color.white); white.Apply(); }
            int fs(float f) => Mathf.RoundToInt(f * S);
            label = new GUIStyle(GUI.skin.label) { fontSize = fs(22), richText = true };
            label.normal.textColor = new Color(0.95f, 0.95f, 0.88f);
            small = new GUIStyle(label) { fontSize = fs(17) };
            big = new GUIStyle(label) { fontSize = fs(34), fontStyle = FontStyle.Bold };
            title = new GUIStyle(label) { fontSize = fs(72), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            title.normal.textColor = new Color(0.85f, 1f, 0.6f);
            center = new GUIStyle(label) { alignment = TextAnchor.MiddleCenter };
            button = new GUIStyle(GUI.skin.button) { fontSize = fs(22), richText = true, wordWrap = true };
            button.padding = new RectOffset(fs(10), fs(10), fs(8), fs(8));
            panel = new GUIStyle(GUI.skin.box);
        }

        void Box(Rect r, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(r, white); GUI.color = o; }

        void Bar(Rect r, float k, Color fill, string text = null)
        {
            Box(r, new Color(0, 0, 0, 0.55f));
            Box(new Rect(r.x + 2, r.y + 2, (r.width - 4) * Mathf.Clamp01(k), r.height - 4), fill);
            if (text != null) GUI.Label(r, text, center);
        }

        void Shadow(Rect r, string text, GUIStyle st)
        {
            var c = st.normal.textColor;
            st.normal.textColor = new Color(0, 0, 0, 0.7f);
            GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), text, st);
            st.normal.textColor = c;
            GUI.Label(r, text, st);
        }

        static string Clock(float t) { t = Mathf.Max(0, t); return ((int)t / 60) + ":" + ((int)t % 60).ToString("00"); }

        void OnGUI()
        {
            Styles();
            switch (state)
            {
                case State.Select: DrawSelect(); break;
                case State.Playing: DrawHud(); break;
                case State.Paused: DrawHud(); DrawPause(); break;
                case State.Result: DrawResult(); break;
            }
        }

        void DrawSelect()
        {
            float s = S;
            Shadow(new Rect(0, 30 * s, Screen.width, 90 * s), "РЕЛИКТ", title);
            var sub = new GUIStyle(center) { fontSize = Mathf.RoundToInt(22 * s) };
            Shadow(new Rect(0, 110 * s, Screen.width, 40 * s), "Прототип матча · джунгли · PvPvE", sub);

            float w = 380 * s, h = 330 * s, y = Screen.height - h - 40 * s;
            DrawHeroCard(new Rect(Screen.width * 0.5f - w - 20 * s, y, w, h), HeroKind.Frog,
                "ЛЯГУШКА-ДРЕВОЛАЗ", "Ближний бой, меч\nЗдоровье 170 · урон 26\n[Q] Прыжок — удар по площади\n[Ctrl] Перекат");
            DrawHeroCard(new Rect(Screen.width * 0.5f + 20 * s, y, w, h), HeroKind.Gecko,
                "ГЕККОН-ЛУЧНИК", "Дальний бой, лук\nЗдоровье 130 · урон 20\n[ПКМ] Прицел\n[Q] Корковая броня −70% урона");

            // bank + board
            Box(new Rect(30 * s, 190 * s, 360 * s, 420 * s), new Color(0, 0, 0, 0.5f));
            GUI.Label(new Rect(50 * s, 200 * s, 340 * s, 40 * s), "<b>Казна лагеря:</b> " + Bank + " зол.", label);
            GUI.Label(new Rect(50 * s, 245 * s, 340 * s, 40 * s), "<b>Рейтинг вылазок</b>", label);
            var b = Board();
            for (int i = 0; i < Mathf.Min(8, b.Count); i++)
                GUI.Label(new Rect(50 * s, (285 + i * 36) * s, 340 * s, 36 * s), (i + 1) + ". " + b[i].Key + " — " + b[i].Value, small);

            Box(new Rect(Screen.width - 390 * s, 190 * s, 360 * s, 300 * s), new Color(0, 0, 0, 0.5f));
            GUI.Label(new Rect(Screen.width - 370 * s, 200 * s, 340 * s, 290 * s),
                "<b>Управление</b>\nWASD — движение\nShift — бег, Пробел — прыжок\nЛКМ — атака, Q — способность\n1–4 — навыки при повышении уровня\nEsc — пауза\n\n<b>Цель:</b> собрать артефакты и встать в круг лианы на 6 секунд, пока не закончилось время.", small);
        }

        void DrawHeroCard(Rect r, HeroKind k, string name, string desc)
        {
            float s = S;
            Box(r, new Color(0.05f, 0.08f, 0.04f, 0.72f));
            GUI.Label(new Rect(r.x + 20 * s, r.y + 15 * s, r.width, 45 * s), "<b>" + name + "</b>", big);
            GUI.Label(new Rect(r.x + 20 * s, r.y + 65 * s, r.width - 40 * s, 140 * s), desc, small);
            int tier = WeaponTier(k);
            string wl = "Оружие: ранг " + WeaponNames[tier] + " (+" + (25 * tier) + "% урона)";
            GUI.Label(new Rect(r.x + 20 * s, r.y + 190 * s, r.width, 30 * s), wl, small);
            if (tier < 2)
            {
                int cost = UpgradeCost(tier);
                GUI.enabled = Bank >= cost;
                if (GUI.Button(new Rect(r.x + 20 * s, r.y + 222 * s, r.width - 40 * s, 40 * s), "Улучшить оружие — " + cost + " зол.", button))
                { Bank -= cost; SetWeaponTier(k, tier + 1); PlayerPrefs.Save(); }
                GUI.enabled = true;
            }
            if (GUI.Button(new Rect(r.x + 20 * s, r.y + r.height - 60 * s, r.width - 40 * s, 48 * s), "<b>В джунгли</b>", button))
                StartMatch(k);
        }

        void DrawHud()
        {
            float s = S;
            var cam = Camera.main;

            // floaters
            for (int i = floaters.Count - 1; i >= 0; i--)
            {
                var f = floaters[i];
                f.t += Time.unscaledDeltaTime;
                floaters[i] = f;
                if (f.t > 0.9f) { floaters.RemoveAt(i); continue; }
                Vector3 sp = cam.WorldToScreenPoint(f.pos + Vector3.up * f.t * 1.2f);
                if (sp.z < 0) continue;
                var st = new GUIStyle(center) { fontSize = Mathf.RoundToInt(26 * s), fontStyle = FontStyle.Bold };
                st.normal.textColor = new Color(f.col.r, f.col.g, f.col.b, 1f - f.t / 0.9f);
                Shadow(new Rect(sp.x - 50 * s, Screen.height - sp.y - 20 * s, 100 * s, 40 * s), f.text, st);
            }

            // enemy health bars
            foreach (var h in Health.All)
            {
                if (h == null || h.Dead || h.team == Team.Player || h.hp >= h.max) continue;
                var ai = h.GetComponent<EnemyAI>();
                if (ai != null && ai.boss) continue;
                var cc = h.GetComponent<CharacterController>();
                Vector3 sp = cam.WorldToScreenPoint(h.transform.position + Vector3.up * ((cc ? cc.height : 1.5f) + 0.4f));
                if (sp.z < 0 || sp.z > 40f) continue;
                Bar(new Rect(sp.x - 40 * s, Screen.height - sp.y, 80 * s, 9 * s), h.hp / h.max, h.team == Team.Rival ? new Color(1f, 0.4f, 0.3f) : new Color(0.95f, 0.75f, 0.25f));
            }
            // rival name tags
            foreach (var r in rivals)
            {
                if (r == null || r.IsDead) continue;
                Vector3 sp = cam.WorldToScreenPoint(r.transform.position + Vector3.up * 2.4f);
                if (sp.z < 0 || sp.z > 45f) continue;
                var st = new GUIStyle(center) { fontSize = Mathf.RoundToInt(17 * s) };
                st.normal.textColor = new Color(1f, 0.5f, 0.4f);
                Shadow(new Rect(sp.x - 100 * s, Screen.height - sp.y - 24 * s, 200 * s, 24 * s), r.Hp.displayName, st);
            }

            if (Hero == null) return;
            var hp = Hero.Hp;

            // health + xp
            float x = 40 * s, y = Screen.height - 120 * s;
            Shadow(new Rect(x, y - 40 * s, 400 * s, 36 * s), "<b>" + hp.displayName + "</b>  ур. " + level, label);
            Bar(new Rect(x, y, 420 * s, 30 * s), hp.hp / hp.max, Hero.ArmorOn ? new Color(1f, 0.6f, 0.2f) : new Color(0.35f, 0.85f, 0.35f), Mathf.CeilToInt(hp.hp) + " / " + Mathf.RoundToInt(hp.max));
            Bar(new Rect(x, y + 36 * s, 420 * s, 10 * s), xp / (float)XpForNext(), new Color(0.5f, 0.7f, 1f));

            // ability
            float ax = Screen.width * 0.5f - 60 * s, ay = Screen.height - 130 * s;
            Box(new Rect(ax, ay, 120 * s, 90 * s), new Color(0, 0, 0, 0.55f));
            float left = Hero.AbilityLeft;
            if (left > 0) Box(new Rect(ax, ay + 90 * s * (1f - left / Hero.AbilityTotal), 120 * s, 90 * s * (left / Hero.AbilityTotal)), new Color(0.2f, 0.3f, 0.2f, 0.7f));
            GUI.Label(new Rect(ax, ay + 6 * s, 120 * s, 40 * s), "<b>Q</b>", center);
            var abl = new GUIStyle(center) { fontSize = Mathf.RoundToInt(16 * s), wordWrap = true };
            GUI.Label(new Rect(ax, ay + 38 * s, 120 * s, 50 * s), left > 0 ? left.ToString("0.0") + " с" : Hero.AbilityName, abl);

            // loot
            float rx = Screen.width - 360 * s;
            Box(new Rect(rx, 30 * s, 330 * s, (80 + artNames.Count * 28) * s), new Color(0, 0, 0, 0.45f));
            GUI.Label(new Rect(rx + 16 * s, 38 * s, 320 * s, 34 * s), "<color=#ffd76a><b>" + gold + "</b> золота</color>   убито: " + kills, label);
            GUI.Label(new Rect(rx + 16 * s, 72 * s, 320 * s, 30 * s), "Артефакты: " + artNames.Count, small);
            for (int i = 0; i < artNames.Count; i++)
                GUI.Label(new Rect(rx + 26 * s, (100 + i * 28) * s, 320 * s, 28 * s), "<color=#8fe8ff>◆ " + artNames[i] + "</color> · " + artValues[i], small);

            // timer + extraction status
            var tst = new GUIStyle(center) { fontSize = Mathf.RoundToInt(40 * s), fontStyle = FontStyle.Bold };
            tst.normal.textColor = TimeLeft < 60f ? new Color(1f, 0.45f, 0.35f) : Color.white;
            Shadow(new Rect(0, 20 * s, Screen.width, 50 * s), Clock(TimeLeft), tst);
            string ext = ExtractOpen ? "Лиана открыта — встаньте в круг на " + extractHold + " с"
                                     : "Лиана раскроется через " + Clock(extractOpensAfter - (matchLength - TimeLeft));
            Shadow(new Rect(0, 68 * s, Screen.width, 30 * s), ext, center);
            if (extractProgress > 0f) Bar(new Rect(Screen.width * 0.5f - 200 * s, 105 * s, 400 * s, 22 * s), extractProgress / extractHold, new Color(0.4f, 1f, 0.5f), "Эвакуация…");

            // extraction marker
            if (ExtractOpen) Marker(extractionPoint.position + Vector3.up * 3f, "Лиана", new Color(0.5f, 1f, 0.55f));

            // boss bar
            if (boss != null && !boss.Hp.Dead && (boss.target == hp || (boss.transform.position - Hero.transform.position).magnitude < 22f))
            {
                float bw = 700 * s;
                Shadow(new Rect(Screen.width * 0.5f - bw * 0.5f, 140 * s, bw, 30 * s), "<b>Богомол-Страж</b>", center);
                Bar(new Rect(Screen.width * 0.5f - bw * 0.5f, 172 * s, bw, 18 * s), boss.Hp.hp / boss.Hp.max, new Color(0.85f, 0.3f, 0.9f));
            }

            // skill points
            if (skillPoints > 0)
            {
                float sx = 40 * s, sy = Screen.height * 0.5f - 120 * s;
                Box(new Rect(sx, sy, 400 * s, 200 * s), new Color(0.05f, 0.1f, 0.03f, 0.7f));
                GUI.Label(new Rect(sx + 14 * s, sy + 8 * s, 380 * s, 30 * s), "<b>Очки навыков: " + skillPoints + "</b>", label);
                for (int i = 0; i < 4; i++)
                    GUI.Label(new Rect(sx + 14 * s, sy + (44 + i * 38) * s, 390 * s, 36 * s),
                        "[" + (i + 1) + "] " + SkillNames[i] + " " + new string('●', ranks[i]) + new string('○', 3 - ranks[i]) + "  <size=" + Mathf.RoundToInt(14 * s) + ">" + SkillDesc[i] + "</size>", small);
            }

            // crosshair
            float cs = Hero.kind == HeroKind.Gecko && Mouse.current != null && Mouse.current.rightButton.isPressed ? 5f : 3f;
            Box(new Rect(Screen.width * 0.5f - cs * s, Screen.height * 0.5f - cs * s, cs * 2 * s, cs * 2 * s), new Color(1, 1, 1, 0.8f));

            // toasts
            for (int i = 0; i < toasts.Count; i++)
            {
                float age = Time.unscaledTime - toasts[i].Value;
                if (age > 5f) continue;
                var st = new GUIStyle(center) { fontSize = Mathf.RoundToInt(22 * s) };
                st.normal.textColor = new Color(1f, 0.95f, 0.75f, Mathf.Clamp01(5f - age));
                Shadow(new Rect(0, Screen.height * 0.3f + i * 34 * s, Screen.width, 34 * s), toasts[i].Key, st);
            }

            if (Cursor.lockState != CursorLockMode.Locked && state == State.Playing)
                Shadow(new Rect(0, Screen.height * 0.6f, Screen.width, 40 * s), "Щёлкните, чтобы управлять камерой", center);
        }

        void Marker(Vector3 world, string text, Color col)
        {
            float s = S;
            var cam = Camera.main;
            Vector3 sp = cam.WorldToScreenPoint(world);
            float dist = (world - cam.transform.position).magnitude;
            bool behind = sp.z < 0;
            if (behind) sp = -sp;
            float m = 60 * s;
            sp.x = Mathf.Clamp(sp.x, m, Screen.width - m);
            sp.y = Mathf.Clamp(sp.y, m, Screen.height - m);
            if (behind) sp.y = m;
            var st = new GUIStyle(center) { fontSize = Mathf.RoundToInt(20 * s), fontStyle = FontStyle.Bold };
            st.normal.textColor = col;
            Shadow(new Rect(sp.x - 100 * s, Screen.height - sp.y - 20 * s, 200 * s, 40 * s), "▼ " + text + " " + Mathf.RoundToInt(dist) + " м", st);
        }

        void DrawPause()
        {
            float s = S;
            Box(new Rect(0, 0, Screen.width, Screen.height), new Color(0, 0, 0, 0.5f));
            Shadow(new Rect(0, Screen.height * 0.3f, Screen.width, 90 * s), "Пауза", title);
            float w = 340 * s;
            if (GUI.Button(new Rect(Screen.width * 0.5f - w * 0.5f, Screen.height * 0.5f, w, 54 * s), "Продолжить", button)) SetPaused(false);
            if (GUI.Button(new Rect(Screen.width * 0.5f - w * 0.5f, Screen.height * 0.5f + 70 * s, w, 54 * s), "Сдаться и в лагерь", button))
            { Time.timeScale = 1f; End(false, "Вы покинули вылазку."); }
        }

        void DrawResult()
        {
            float s = S;
            Box(new Rect(0, 0, Screen.width, Screen.height), new Color(0, 0, 0, 0.55f));
            title.normal.textColor = victory ? new Color(0.6f, 1f, 0.5f) : new Color(1f, 0.5f, 0.4f);
            Shadow(new Rect(0, 90 * s, Screen.width, 100 * s), victory ? "Эвакуация!" : "Поражение", title);
            Shadow(new Rect(0, 190 * s, Screen.width, 40 * s), resultLine, center);
            float w = 600 * s, x = Screen.width * 0.5f - w * 0.5f, y = 260 * s;
            Box(new Rect(x, y, w, 420 * s), new Color(0.04f, 0.06f, 0.03f, 0.75f));
            string body =
                "Золото: " + gold + "\n" +
                "Артефакты: " + (artNames.Count == 0 ? "—" : string.Join(", ", artNames)) + " (" + artValues.Sum() + " зол.)\n" +
                "Убито: " + kills + " · Уровень: " + level + "\n\n" +
                (victory ? "<b>В казну лагеря: +" + earned + " зол.</b>" : "Добыча потеряна.") + "\n" +
                "Казна: " + Bank + " зол.\n\n<b>Рейтинг</b>\n" +
                string.Join("\n", Board().Take(5).Select((e, i) => (i + 1) + ". " + e.Key + " — " + e.Value));
            GUI.Label(new Rect(x + 24 * s, y + 18 * s, w - 48 * s, 400 * s), body, label);
            if (GUI.Button(new Rect(Screen.width * 0.5f - 170 * s, y + 440 * s, 340 * s, 56 * s), "<b>В лагерь</b>", button))
                SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }
    }

    public static class VecExt
    {
        public static Vector3 Flat(this Vector3 v) { v.y = 0; return v; }
    }
}
