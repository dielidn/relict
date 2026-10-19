using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Relict
{
    public partial class MatchManager : MonoBehaviour
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

    }

    public static class VecExt
    {
        public static Vector3 Flat(this Vector3 v) { v.y = 0; return v; }
    }
}
