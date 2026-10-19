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
        readonly int[] gear = new int[5];
        int goldSpent;
        bool upgradeOpen;
        float hitT = -9f, hurtT = -9f, lastCoinSfx;
        bool hitCrit, hitKill;
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
        public float DamageMul => (1f + 0.15f * ranks[0]) * (1f + 0.25f * WeaponTier(chosen)) * (1f + 0.12f * gear[0]);
        public float SpeedMul => 1f + 0.07f * ranks[2] + 0.06f * gear[3];
        public float AttackSpeedMul => (1f + 0.1f * ranks[2]) * (1f + 0.12f * gear[1]);
        public bool UIOpen => upgradeOpen || settingsOpen;

        // in-match gear bought with gold found in this run
        static readonly string[] GearNames = { "Заточка оружия", "Лёгкая рука", "Толстая шкура", "Быстрые лапы", "Целебный настой" };
        static readonly string[] GearDesc = { "+12% урона", "+12% темпа атаки и натяжения", "+25 к здоровью", "+6% скорости бега", "Сразу восстановить 60 здоровья" };
        static readonly int[][] GearCost = { new[] { 50, 110, 200 }, new[] { 60, 130, 220 }, new[] { 50, 100, 180 }, new[] { 40, 90 }, new[] { 35 } };
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
            GameSettings.Load();
            SFX.Warm();
            Projectile.OnHit = OnProjectileHit;
            GameSettings.Apply(false);
            if (!GameSettings.HasNick) { nickPrompt = true; nickDraft = ""; }
            BuildExtraction();
        }

        void OnDestroy()
        {
            if (Projectile.OnHit == (System.Action<Health, Health, float, bool, bool>)OnProjectileHit) Projectile.OnHit = null;
            Time.timeScale = 1f;
        }

        void OnProjectileHit(Health owner, Health victim, float dmg, bool crit, bool killed)
        {
            if (Hero == null || owner != Hero.Hp) return;
            HitMarker(crit, killed);
            SFX.Play(crit ? Sfx.ArrowCrit : Sfx.ArrowHit, 0.9f);
            if (crit) HitStop(0.035f);
        }

        public void HitMarker(bool crit, bool kill)
        {
            hitT = Time.unscaledTime;
            hitCrit = crit;
            hitKill = kill;
        }

        bool stopping;
        public void HitStop(float seconds)
        {
            if (!stopping && state == State.Playing) StartCoroutine(HitStopCo(seconds));
        }

        IEnumerator HitStopCo(float seconds)
        {
            stopping = true;
            Time.timeScale = 0.05f;
            yield return new WaitForSecondsRealtime(seconds);
            if (state == State.Playing) Time.timeScale = 1f;
            stopping = false;
        }

        public void StartMatch(HeroKind kind)
        {
            chosen = kind;
            if (selectDisplay) selectDisplay.SetActive(false);
            var prefab = kind == HeroKind.Frog ? frogPrefab : geckoPrefab;
            var go = Instantiate(prefab, playerSpawn.position, playerSpawn.rotation);
            Hero = go.GetComponent<HeroController>();
            if (GameSettings.HasNick) Hero.Hp.displayName = GameSettings.Nick;
            Hero.Hp.Damaged += (a, amt) => { if (amt >= 2f) hurtT = Time.unscaledTime; };
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
            if (kb != null && kb.f11Key.wasPressedThisFrame) GameSettings.ToggleFullscreen();
            if (settingsOpen && kb != null && kb.escapeKey.wasPressedThisFrame) { CloseSettings(); return; }
            if (upgradeOpen && kb != null && kb.escapeKey.wasPressedThisFrame) { SetUpgrades(false); return; }
            if (state == State.Playing && kb != null && kb.tabKey.wasPressedThisFrame) SetUpgrades(!upgradeOpen);
            if (state == State.Playing || state == State.Paused)
            {
                if (kb != null && kb.escapeKey.wasPressedThisFrame) SetPaused(state == State.Playing);
                if (state == State.Playing && !UIOpen && Cursor.lockState != CursorLockMode.Locked && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame) LockCursor(true);
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
                    if (key.wasPressedThisFrame) BuySkill(i);
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

        void SetUpgrades(bool open)
        {
            upgradeOpen = open;
            LockCursor(!open);
            SFX.Play(Sfx.Click, 0.6f, 0f);
        }

        bool BuySkill(int i)
        {
            if (skillPoints <= 0 || ranks[i] >= 3) return false;
            ranks[i]++; skillPoints--;
            if (i == 1 && Hero) { Hero.Hp.SetMax(Hero.Hp.max + 30f); Hero.Hp.Heal(30f); }
            SFX.Play(Sfx.Buy, 0.8f, 0f);
            Toast(SkillNames[i] + " → ранг " + ranks[i]);
            return true;
        }

        int GearPrice(int i) => gear[i] < GearCost[i].Length ? GearCost[i][gear[i]] : (i == 4 ? GearCost[4][0] : -1);

        void BuyGear(int i)
        {
            int price = GearPrice(i);
            if (price < 0 || gold < price) return;
            if (i == 4) { if (Hero.Hp.hp >= Hero.Hp.max) return; Hero.Hp.Heal(60f); }
            else gear[i]++;
            if (i == 2) { Hero.Hp.SetMax(Hero.Hp.max + 25f); Hero.Hp.Heal(25f); }
            gold -= price; goldSpent += price;
            SFX.Play(Sfx.Buy, 0.8f, 0f);
        }

        void SetPaused(bool pause)
        {
            if (pause) upgradeOpen = false;
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
            upgradeOpen = false;
            Time.timeScale = 1f;
            state = State.Result;
            LockCursor(false);
            earned = 0;
            if (win)
            {
                earned = gold + artValues.Sum();
                Bank += earned;
                AddToBoard((GameSettings.HasNick ? GameSettings.Nick : "Игрок") + " (" + (chosen == HeroKind.Frog ? "лягушка" : "геккон") + ")", earned);
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
                HitMarker(false, true);
                SFX.Play(Sfx.Kill, 0.7f, 0.03f);
            }
            if (e.boss) Toast("Богомол-Страж повержен!");
        }

        public void OnRivalKilled(RivalAI r, Health killer)
        {
            int coins = Mathf.Clamp(r.gold / 10, 2, 15);
            for (int i = 0; i < coins; i++) Pickup.Gold(r.transform.position, Mathf.Max(5, (r.gold + 60) / coins));
            for (int i = 0; i < r.artifacts.Count; i++)
                Pickup.Artifact(r.transform.position + Random.insideUnitSphere.Flat() * 1.5f, r.artifacts[i], r.artifactValues[i], RandomModel(artifactModels));
            if (killer != null && Hero != null && killer == Hero.Hp) { kills++; AddXp(120); HitMarker(false, true); SFX.Play(Sfx.Kill, 0.9f, 0f); HitStop(0.08f); }
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
                Toast("Уровень " + level + "! Tab — потратить очко навыка");
                SFX.Play(Sfx.LevelUp, 0.8f, 0f);
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
                case PickupKind.Gold:
                    gold += p.amount;
                    if (Time.unscaledTime - lastCoinSfx > 0.06f) { lastCoinSfx = Time.unscaledTime; SFX.Play(Sfx.Coin, 0.6f, 0.08f); }
                    break;
                case PickupKind.Heal: Hero.Hp.Heal(p.amount); SFX.Play(Sfx.Buy, 0.5f); break;
                case PickupKind.Artifact:
                    artNames.Add(p.title); artValues.Add(p.amount);
                    Toast("Артефакт «" + p.title + "» (" + p.amount + " зол.)");
                    VFX.Burst(p.transform.position, new Color(0.4f, 0.9f, 1f), 40, 4f, 0.2f);
                    SFX.Play(Sfx.Artifact, 0.9f, 0f);
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
        struct Floater { public Vector3 pos; public string text; public float t; public Color col; public bool crit; }
        readonly List<Floater> floaters = new List<Floater>();
        readonly List<KeyValuePair<string, float>> toasts = new List<KeyValuePair<string, float>>();

        public void DamageNumber(Vector3 pos, float amount, bool toPlayer, bool crit = false)
        {
            if (amount < 1.5f || !GameSettings.DamageNumbers) return;
            floaters.Add(new Floater
            {
                pos = pos + Random.insideUnitSphere * 0.3f,
                text = Mathf.RoundToInt(amount) + (crit ? "!" : ""),
                crit = crit,
                t = 0f,
                col = toPlayer ? new Color(1f, 0.35f, 0.3f) : crit ? new Color(1f, 0.6f, 0.15f) : new Color(1f, 0.92f, 0.6f)
            });
        }

        public void Toast(string s)
        {
            toasts.Add(new KeyValuePair<string, float>(s, Time.unscaledTime));
            if (toasts.Count > 5) toasts.RemoveAt(0);
        }

        // ---------- UI ----------
        Texture2D white, btnN, btnH, btnA, fieldTex;
        GUIStyle label, big, title, small, center, button, panel, field, slider, thumb, heading;
        float S => Screen.height / 1080f;

        bool settingsOpen, nickPrompt;
        string nickDraft = "";

        static readonly Color Moss = new Color(0.06f, 0.09f, 0.05f, 0.86f);
        static readonly Color Edge = new Color(0.55f, 0.8f, 0.35f, 0.55f);
        static readonly Color Accent = new Color(0.72f, 1f, 0.45f);

        static Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1); t.SetPixel(0, 0, c); t.Apply(); t.hideFlags = HideFlags.DontSave; return t;
        }

        void Styles()
        {
            if (white == null)
            {
                white = Tex(Color.white);
                btnN = Tex(new Color(0.16f, 0.27f, 0.12f, 0.95f));
                btnH = Tex(new Color(0.26f, 0.42f, 0.18f, 0.98f));
                btnA = Tex(new Color(0.38f, 0.58f, 0.22f, 1f));
                fieldTex = Tex(new Color(0.02f, 0.03f, 0.02f, 0.9f));
            }
            int fs(float f) => Mathf.RoundToInt(f * S);
            label = new GUIStyle(GUI.skin.label) { fontSize = fs(22), richText = true };
            label.normal.textColor = new Color(0.95f, 0.95f, 0.88f);
            small = new GUIStyle(label) { fontSize = fs(17) };
            big = new GUIStyle(label) { fontSize = fs(34), fontStyle = FontStyle.Bold };
            heading = new GUIStyle(label) { fontSize = fs(30), fontStyle = FontStyle.Bold };
            heading.normal.textColor = Accent;
            title = new GUIStyle(label) { fontSize = fs(72), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            title.normal.textColor = new Color(0.85f, 1f, 0.6f);
            center = new GUIStyle(label) { alignment = TextAnchor.MiddleCenter };
            button = new GUIStyle(GUI.skin.button) { fontSize = fs(21), richText = true, wordWrap = true, alignment = TextAnchor.MiddleCenter };
            button.padding = new RectOffset(fs(10), fs(10), fs(6), fs(6));
            button.border = new RectOffset(0, 0, 0, 0);
            button.normal.background = btnN; button.hover.background = btnH; button.active.background = btnA; button.focused.background = btnN;
            button.onNormal.background = btnA; button.onHover.background = btnA;
            foreach (var st in new[] { button.normal, button.hover, button.active, button.focused, button.onNormal, button.onHover })
                st.textColor = new Color(0.95f, 1f, 0.88f);
            panel = new GUIStyle(GUI.skin.box);
            field = new GUIStyle(GUI.skin.textField) { fontSize = fs(24), alignment = TextAnchor.MiddleLeft };
            field.padding = new RectOffset(fs(12), fs(12), fs(6), fs(6));
            field.normal.background = fieldTex; field.focused.background = fieldTex; field.hover.background = fieldTex;
            field.normal.textColor = field.focused.textColor = field.hover.textColor = Color.white;
            slider = new GUIStyle(GUI.skin.horizontalSlider) { fixedHeight = fs(8) };
            slider.normal.background = fieldTex;
            slider.margin = new RectOffset(0, 0, fs(10), 0);
            thumb = new GUIStyle(GUI.skin.horizontalSliderThumb) { fixedHeight = fs(24), fixedWidth = fs(16) };
            thumb.normal.background = btnA; thumb.hover.background = Tex(Accent); thumb.active.background = Tex(Accent);
        }

        void Panel(Rect r)
        {
            Box(r, Moss);
            float e = Mathf.Max(1f, 2f * S);
            Box(new Rect(r.x, r.y, r.width, e), Edge);
            Box(new Rect(r.x, r.yMax - e, r.width, e), Edge);
            Box(new Rect(r.x, r.y, e, r.height), Edge);
            Box(new Rect(r.xMax - e, r.y, e, r.height), Edge);
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
            bool modal = settingsOpen || nickPrompt || (upgradeOpen && state == State.Playing);
            GUI.enabled = !modal;
            switch (state)
            {
                case State.Select: DrawSelect(); break;
                case State.Playing: DrawHud(); break;
                case State.Paused: DrawHud(); DrawPause(); break;
                case State.Result: DrawResult(); break;
            }
            GUI.enabled = true;
            if (upgradeOpen && state == State.Playing && !settingsOpen) DrawUpgrades();
            if (nickPrompt) DrawNickPrompt();
            else if (settingsOpen) DrawSettings();
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

            // player + bank + board
            Panel(new Rect(30 * s, 40 * s, 380 * s, 120 * s));
            GUI.Label(new Rect(50 * s, 50 * s, 340 * s, 30 * s), "Игрок", small);
            GUI.Label(new Rect(50 * s, 76 * s, 240 * s, 40 * s), "<b>" + (GameSettings.HasNick ? GameSettings.Nick : "—") + "</b>", big);
            if (GUI.Button(new Rect(280 * s, 82 * s, 115 * s, 36 * s), "Сменить", button)) { nickPrompt = true; nickDraft = GameSettings.Nick; }
            GUI.Label(new Rect(50 * s, 122 * s, 340 * s, 30 * s), "Казна лагеря: <color=#ffd76a><b>" + Bank + "</b> зол.</color>", small);

            Panel(new Rect(30 * s, 190 * s, 380 * s, 400 * s));
            GUI.Label(new Rect(50 * s, 200 * s, 340 * s, 40 * s), "Рейтинг вылазок", heading);
            var b = Board();
            for (int i = 0; i < Mathf.Min(8, b.Count); i++)
            {
                bool mine = GameSettings.HasNick && b[i].Key.StartsWith(GameSettings.Nick + " (");
                string line = (i + 1) + ". " + b[i].Key + " — " + b[i].Value;
                GUI.Label(new Rect(50 * s, (250 + i * 38) * s, 350 * s, 36 * s), mine ? "<color=#b8ff73><b>" + line + "</b></color>" : line, small);
            }

            if (GUI.Button(new Rect(Screen.width - 410 * s, 40 * s, 380 * s, 54 * s), "⚙  Настройки", button)) settingsOpen = true;
            Panel(new Rect(Screen.width - 410 * s, 120 * s, 380 * s, 340 * s));
            GUI.Label(new Rect(Screen.width - 390 * s, 130 * s, 340 * s, 40 * s), "Управление", heading);
            GUI.Label(new Rect(Screen.width - 390 * s, 175 * s, 350 * s, 280 * s),
                "WASD — движение\nShift — бег, Пробел — прыжок\nЛКМ — атака, Q — способность\nCtrl — перекат (лягушка)\n1–4 — навыки при повышении уровня\nEsc — пауза, F11 — полный экран\n\n<b>Цель:</b> собрать артефакты и простоять 6 с в круге лианы, пока не вышло время.", small);
        }

        void DrawHeroCard(Rect r, HeroKind k, string name, string desc)
        {
            float s = S;
            Panel(r);
            GUI.Label(new Rect(r.x + 20 * s, r.y + 15 * s, r.width, 45 * s), name, heading);
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

            // damage flash at the screen edges
            float hurtA = Mathf.Clamp01(1f - (Time.unscaledTime - hurtT) / 0.45f) * 0.45f;
            if (Hero != null && Hero.Hp.hp < Hero.Hp.max * 0.3f) hurtA = Mathf.Max(hurtA, 0.12f + 0.08f * Mathf.Sin(Time.unscaledTime * 5f));
            if (hurtA > 0.01f)
            {
                var hc = new Color(0.8f, 0.05f, 0.02f, hurtA);
                float e = 70 * s;
                for (int i = 0; i < 6; i++)
                {
                    var c = new Color(hc.r, hc.g, hc.b, hc.a * (1f - i / 6f));
                    float o = i * e / 6f, w = e / 6f;
                    Box(new Rect(0, o, Screen.width, w), c);
                    Box(new Rect(0, Screen.height - o - w, Screen.width, w), c);
                    Box(new Rect(o, 0, w, Screen.height), c);
                    Box(new Rect(Screen.width - o - w, 0, w, Screen.height), c);
                }
            }

            // floaters
            for (int i = floaters.Count - 1; i >= 0; i--)
            {
                var f = floaters[i];
                f.t += Time.unscaledDeltaTime;
                floaters[i] = f;
                if (f.t > 0.9f) { floaters.RemoveAt(i); continue; }
                Vector3 sp = cam.WorldToScreenPoint(f.pos + Vector3.up * f.t * 1.2f);
                if (sp.z < 0) continue;
                float pop = f.crit ? 1f + 0.6f * Mathf.Clamp01(1f - f.t * 6f) : 1f;
                var st = new GUIStyle(center) { fontSize = Mathf.RoundToInt((f.crit ? 36 : 26) * s * pop), fontStyle = FontStyle.Bold };
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

            // upgrade hint
            if (!upgradeOpen)
            {
                string hint = skillPoints > 0 ? "<color=#b8ff73><b>Tab</b> — очков навыка: " + skillPoints + "</color>" : "<b>Tab</b> — улучшения";
                float pulse = skillPoints > 0 ? 0.75f + 0.25f * Mathf.Sin(Time.unscaledTime * 4f) : 0.6f;
                var hs = new GUIStyle(label) { fontSize = Mathf.RoundToInt(19 * s) };
                hs.normal.textColor = new Color(1, 1, 1, pulse);
                Shadow(new Rect(40 * s, Screen.height - 200 * s, 420 * s, 30 * s), hint, hs);
            }

            // crosshair
            DrawCrosshair();

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
            if (GUI.Button(new Rect(Screen.width * 0.5f - w * 0.5f, Screen.height * 0.5f + 70 * s, w, 54 * s), "Настройки", button)) settingsOpen = true;
            if (GUI.Button(new Rect(Screen.width * 0.5f - w * 0.5f, Screen.height * 0.5f + 140 * s, w, 54 * s), "Сдаться и в лагерь", button))
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
            Panel(new Rect(x, y, w, 420 * s));
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

        void Line(Vector2 a, Vector2 b, float width, Color c)
        {
            var m = GUI.matrix;
            Vector2 d = b - a;
            float ang = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            GUIUtility.RotateAroundPivot(ang, a);
            Box(new Rect(a.x, a.y - width * 0.5f, d.magnitude, width), c);
            GUI.matrix = m;
        }

        void DrawCrosshair()
        {
            float s = S;
            Vector2 c = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            bool bow = Hero.kind == HeroKind.Gecko;
            bool aim = bow && Mouse.current != null && Mouse.current.rightButton.isPressed;
            float k = Hero.Charge01;
            float gap = bow ? Mathf.Lerp(aim ? 10f : 18f, 3f, k) : 8f;
            float len = bow ? 9f : 6f;
            Color col = bow && k >= 1f ? new Color(0.7f, 1f, 0.5f, 0.95f) : new Color(1f, 1f, 1f, 0.85f);
            float w = Mathf.Max(1.5f, 2f * s);
            gap *= s; len *= s;
            Box(new Rect(c.x - w * 0.5f, c.y - gap - len, w, len), col);
            Box(new Rect(c.x - w * 0.5f, c.y + gap, w, len), col);
            Box(new Rect(c.x - gap - len, c.y - w * 0.5f, len, w), col);
            Box(new Rect(c.x + gap, c.y - w * 0.5f, len, w), col);
            Box(new Rect(c.x - w * 0.5f, c.y - w * 0.5f, w, w), col);
            if (bow && Hero.Charging)
            {
                float bw = 60 * s;
                Box(new Rect(c.x - bw * 0.5f, c.y + 34 * s, bw, 5 * s), new Color(0, 0, 0, 0.5f));
                Box(new Rect(c.x - bw * 0.5f, c.y + 34 * s, bw * k, 5 * s), k >= 1f ? new Color(0.7f, 1f, 0.45f) : new Color(1f, 0.9f, 0.6f));
            }
            // hit marker
            float age = Time.unscaledTime - hitT;
            if (age < 0.25f)
            {
                float a = 1f - age / 0.25f;
                Color hc = hitKill ? new Color(1f, 0.3f, 0.25f, a) : hitCrit ? new Color(1f, 0.75f, 0.2f, a) : new Color(1f, 1f, 1f, a);
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

        // ---------- Tab: character upgrades ----------
        void DrawUpgrades()
        {
            float s = S;
            Box(new Rect(0, 0, Screen.width, Screen.height), new Color(0, 0, 0, 0.45f));
            float w = 1180 * s, h = 720 * s;
            var r = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            Panel(r);
            GUI.Label(new Rect(r.x + 36 * s, r.y + 20 * s, w, 46 * s), "Улучшение героя", heading);
            var rs = new GUIStyle(label) { alignment = TextAnchor.MiddleRight };
            GUI.Label(new Rect(r.xMax - 436 * s, r.y + 22 * s, 400 * s, 40 * s), "ур. " + level + " · опыт " + xp + " / " + XpForNext(), rs);

            float colW = (w - 108 * s) * 0.5f;
            float lx = r.x + 36 * s, rx = lx + colW + 36 * s, top = r.y + 84 * s;
            var desc = new GUIStyle(small) { wordWrap = true };
            desc.normal.textColor = new Color(0.75f, 0.82f, 0.7f);

            // skills
            Panel(new Rect(lx, top, colW, 470 * s));
            GUI.Label(new Rect(lx + 20 * s, top + 12 * s, colW, 36 * s), "Навыки · очков: " + (skillPoints > 0 ? "<color=#b8ff73><b>" + skillPoints + "</b></color>" : "0"), label);
            for (int i = 0; i < 4; i++)
            {
                float y = top + (60 + i * 100) * s;
                GUI.Label(new Rect(lx + 20 * s, y, colW - 180 * s, 32 * s), "<b>" + SkillNames[i] + "</b>  <size=" + Mathf.RoundToInt(15 * s) + "><color=#9fb08f>[" + (i + 1) + "]</color></size>", label);
                GUI.Label(new Rect(lx + 20 * s, y + 32 * s, colW - 180 * s, 50 * s), SkillDesc[i] + " за ранг", desc);
                for (int d = 0; d < 3; d++)
                    Box(new Rect(lx + colW - 160 * s + d * 22 * s, y + 8 * s, 16 * s, 16 * s), d < ranks[i] ? Accent : new Color(1, 1, 1, 0.15f));
                GUI.enabled = skillPoints > 0 && ranks[i] < 3;
                if (GUI.Button(new Rect(lx + colW - 160 * s, y + 34 * s, 130 * s, 40 * s), ranks[i] >= 3 ? "Макс." : "Изучить", button)) BuySkill(i);
                GUI.enabled = true;
            }

            // gear
            Panel(new Rect(rx, top, colW, 470 * s));
            GUI.Label(new Rect(rx + 20 * s, top + 12 * s, colW, 36 * s), "Снаряжение · <color=#ffd76a><b>" + gold + "</b> зол.</color>", label);
            for (int i = 0; i < 5; i++)
            {
                float y = top + (60 + i * 80) * s;
                int max = GearCost[i].Length;
                string lvl = i == 4 ? "" : "  <size=" + Mathf.RoundToInt(15 * s) + "><color=#9fb08f>" + gear[i] + "/" + max + "</color></size>";
                GUI.Label(new Rect(rx + 20 * s, y, colW - 200 * s, 32 * s), "<b>" + GearNames[i] + "</b>" + lvl, label);
                GUI.Label(new Rect(rx + 20 * s, y + 30 * s, colW - 200 * s, 40 * s), GearDesc[i], desc);
                int price = GearPrice(i);
                bool maxed = price < 0;
                GUI.enabled = !maxed && gold >= price && !(i == 4 && Hero.Hp.hp >= Hero.Hp.max);
                if (GUI.Button(new Rect(rx + colW - 180 * s, y + 10 * s, 160 * s, 44 * s), maxed ? "Макс." : price + " зол.", button)) BuyGear(i);
                GUI.enabled = true;
            }

            // stats line
            float sy = top + 490 * s;
            float rate = Hero.kind == HeroKind.Gecko ? AttackSpeedMul / 0.75f : AttackSpeedMul / Hero.attackInterval;
            string stats = "Здоровье <b>" + Mathf.CeilToInt(Hero.Hp.hp) + "/" + Mathf.RoundToInt(Hero.Hp.max) + "</b>   ·   Урон <b>" + Mathf.RoundToInt(Hero.damage * DamageMul) + "</b>" +
                           "   ·   " + (Hero.kind == HeroKind.Gecko ? "Полное натяжение <b>" + (0.75f / AttackSpeedMul).ToString("0.00") + " с</b>" : "Ударов в секунду <b>" + rate.ToString("0.0") + "</b>") +
                           "   ·   Скорость <b>+" + Mathf.RoundToInt((SpeedMul - 1f) * 100f) + "%</b>   ·   Перезарядка Q <b>" + (Hero.abilityCooldown * CooldownMul).ToString("0.#") + " с</b>";
            GUI.Label(new Rect(lx, sy, w - 72 * s, 34 * s), stats, label);
            GUI.Label(new Rect(lx, sy + 40 * s, w - 72 * s, 30 * s),
                "<color=#c9b98a>Потраченное золото не попадёт в казну лагеря" + (goldSpent > 0 ? " (потрачено: " + goldSpent + ")" : "") + ".</color>", small);
            if (GUI.Button(new Rect(r.xMax - 236 * s, r.yMax - 76 * s, 200 * s, 52 * s), "<b>Закрыть</b> · Tab", button)) SetUpgrades(false);
        }

        // ---------- nickname ----------
        void DrawNickPrompt()
        {
            float s = S;
            Box(new Rect(0, 0, Screen.width, Screen.height), new Color(0, 0, 0, 0.6f));
            float w = 640 * s, h = 300 * s;
            var r = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            Panel(r);
            GUI.Label(new Rect(r.x + 30 * s, r.y + 24 * s, w - 60 * s, 44 * s), "Как вас звать в джунглях?", heading);
            GUI.Label(new Rect(r.x + 30 * s, r.y + 72 * s, w - 60 * s, 30 * s), "Ник видно в рейтинге и над героем. От 2 до 16 символов.", small);
            GUI.SetNextControlName("nick");
            nickDraft = GUI.TextField(new Rect(r.x + 30 * s, r.y + 115 * s, w - 60 * s, 52 * s), nickDraft ?? "", 16, field);
            if (GUI.GetNameOfFocusedControl() != "nick") GUI.FocusControl("nick");
            string clean = GameSettings.CleanNick(nickDraft);
            bool ok = clean.Length >= 2;
            var e = Event.current;
            bool enter = e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);
            bool canCancel = GameSettings.HasNick;
            float bw = canCancel ? (w - 80 * s) * 0.5f : w - 60 * s;
            GUI.enabled = ok;
            if (GUI.Button(new Rect(r.x + 30 * s, r.y + h - 90 * s, bw, 56 * s), "<b>Готово</b>", button) || (enter && ok))
            {
                GameSettings.Nick = clean;
                GameSettings.Save();
                nickPrompt = false;
                GUI.FocusControl(null);
            }
            GUI.enabled = true;
            if (canCancel && GUI.Button(new Rect(r.x + 50 * s + bw, r.y + h - 90 * s, bw, 56 * s), "Отмена", button))
            { nickPrompt = false; GUI.FocusControl(null); }
        }

        // ---------- settings ----------
        void CloseSettings()
        {
            settingsOpen = false;
            GameSettings.Apply(false);
            GameSettings.Save();
        }

        bool Toggle(Rect r, bool v)
        {
            if (GUI.Button(r, v ? "<color=#b8ff73><b>Вкл</b></color>" : "Выкл", button)) v = !v;
            return v;
        }

        int Stepper(Rect r, int index, int count, string text)
        {
            float bw = r.height * 1.2f;
            if (GUI.Button(new Rect(r.x, r.y, bw, r.height), "‹", button)) index = (index - 1 + count) % count;
            GUI.Label(new Rect(r.x + bw, r.y, r.width - bw * 2, r.height), text, center);
            if (GUI.Button(new Rect(r.xMax - bw, r.y, bw, r.height), "›", button)) index = (index + 1) % count;
            return index;
        }

        float Slider(Rect r, float v, float min, float max, string valueText)
        {
            float vw = 80 * S;
            v = GUI.HorizontalSlider(new Rect(r.x, r.y + r.height * 0.3f, r.width - vw - 10 * S, r.height), v, min, max, slider, thumb);
            GUI.Label(new Rect(r.xMax - vw, r.y, vw, r.height), valueText, center);
            return v;
        }

        void DrawSettings()
        {
            float s = S;
            Box(new Rect(0, 0, Screen.width, Screen.height), new Color(0, 0, 0, 0.6f));
            float w = 820 * s, h = 790 * s;
            var r = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            Panel(r);
            GUI.Label(new Rect(r.x + 36 * s, r.y + 22 * s, w, 46 * s), "Настройки", heading);

            float lx = r.x + 36 * s, cx = r.x + w * 0.45f, cw = w * 0.55f - 36 * s, rh = 44 * s;
            float y = r.y + 90 * s, step = 58 * s;
            void Row(string name, string hint = null)
            {
                GUI.Label(new Rect(lx, y + 4 * s, cx - lx, rh), name, label);
                if (hint != null) GUI.Label(new Rect(lx, y + 30 * s, cx - lx, rh), "<color=#9fb08f>" + hint + "</color>", new GUIStyle(small) { fontSize = Mathf.RoundToInt(13 * s) });
            }

            Row("Ник");
            string nick = GUI.TextField(new Rect(cx, y, cw, rh), GameSettings.Nick ?? "", 16, field);
            nick = GameSettings.CleanNick(nick);
            if (nick.Length >= 1) GameSettings.Nick = nick;
            y += step + 6 * s;

            GUI.Label(new Rect(lx, y, w, 30 * s), "<b>Управление</b>", new GUIStyle(small) { normal = { textColor = Accent } });
            y += 34 * s;
            Row("Чувствительность мыши");
            GameSettings.Sensitivity = Slider(new Rect(cx, y, cw, rh), GameSettings.Sensitivity, 0.03f, 0.3f, Mathf.RoundToInt(GameSettings.Sensitivity * 100f).ToString());
            y += step;
            Row("Инвертировать мышь по Y");
            GameSettings.InvertY = Toggle(new Rect(cx, y, 140 * s, rh), GameSettings.InvertY);
            y += step + 6 * s;

            GUI.Label(new Rect(lx, y, w, 30 * s), "<b>Изображение</b>", new GUIStyle(small) { normal = { textColor = Accent } });
            y += 34 * s;
            Row("Поле зрения");
            float fov = Slider(new Rect(cx, y, cw, rh), GameSettings.Fov, 50f, 90f, Mathf.RoundToInt(GameSettings.Fov) + "°");
            if (!Mathf.Approximately(fov, GameSettings.Fov)) { GameSettings.Fov = fov; GameSettings.Apply(false); }
            y += step;

            Row("Качество графики");
            var qn = QualitySettings.names;
            int q = Mathf.Clamp(GameSettings.Quality, 0, qn.Length - 1);
            int nq = Stepper(new Rect(cx, y, cw, rh), q, qn.Length, qn[q]);
            if (nq != q) { GameSettings.Quality = nq; GameSettings.Apply(false); }
            y += step;

            Row("Полный экран", Application.isEditor ? "в редакторе не действует — см. Game view" : "F11 — быстро переключить");
            bool fs = Toggle(new Rect(cx, y, 140 * s, rh), GameSettings.Fullscreen);
            if (fs != GameSettings.Fullscreen) { GameSettings.Fullscreen = fs; if (!Application.isEditor) GameSettings.ApplyScreen(); }
            y += step;

            Row("Разрешение");
            var rs = GameSettings.Resolutions();
            int count = rs.Length + 1; // +1 = native
            int idx = GameSettings.ResolutionIndex < 0 ? 0 : GameSettings.ResolutionIndex + 1;
            int nidx = Stepper(new Rect(cx, y, cw, rh), idx, count, GameSettings.ResolutionLabel());
            if (nidx != idx) { GameSettings.ResolutionIndex = nidx - 1; if (!Application.isEditor) GameSettings.ApplyScreen(); }
            y += step;

            Row("Вертикальная синхронизация");
            bool vs = Toggle(new Rect(cx, y, 140 * s, rh), GameSettings.VSync);
            if (vs != GameSettings.VSync) { GameSettings.VSync = vs; GameSettings.Apply(false); }
            y += step;

            Row("Цифры урона");
            GameSettings.DamageNumbers = Toggle(new Rect(cx, y, 140 * s, rh), GameSettings.DamageNumbers);
            y += step;

            Row("Громкость", "звук появится в следующих версиях");
            float vol = Slider(new Rect(cx, y, cw, rh), GameSettings.Volume, 0f, 1f, Mathf.RoundToInt(GameSettings.Volume * 100f) + "%");
            if (!Mathf.Approximately(vol, GameSettings.Volume)) { GameSettings.Volume = vol; AudioListener.volume = vol; }

            float by = r.yMax - 80 * s, bw2 = (w - 72 * s - 20 * s) * 0.5f;
            if (GUI.Button(new Rect(r.x + 36 * s, by, bw2, 54 * s), "По умолчанию", button)) GameSettings.ResetToDefaults();
            if (GUI.Button(new Rect(r.x + 56 * s + bw2, by, bw2, 54 * s), "<b>Готово</b>", button)) CloseSettings();
        }
    }

    public static class VecExt
    {
        public static Vector3 Flat(this Vector3 v) { v.y = 0; return v; }
    }
}
