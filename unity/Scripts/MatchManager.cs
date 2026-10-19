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
        public GameObject rivalModel;                       // gecko archer model
        public RuntimeAnimatorController rivalController;
        public GameObject rivalFrogModel;                   // frog swordsman model
        public RuntimeAnimatorController rivalFrogController;
        public GameObject[] artifactModels;
        public GameObject[] vineModels;

        [Header("Places")]
        public Transform playerSpawn;
        public Transform extractionPoint;   // arena centre
        public Transform bossSpot;
        public Transform rivalSpawn;
        public GameObject selectDisplay;

        [Header("Rules")]
        public float matchLength = 720f;    // after this, poison fog in the arena
        public float gatesFallAt = 480f;    // all barriers drop by themselves

        public enum State { Select, Playing, Paused, Result }
        public State state = State.Select;
        public bool Playing => state == State.Playing;

        public HeroController Hero { get; private set; }
        public RivalAI Rival { get; private set; }
        public float TimeLeft { get; private set; }
        public float Elapsed => matchLength - TimeLeft;
        public bool GatesForced { get; private set; }
        public bool Poison => TimeLeft <= 0f;
        public int ZonesCleared(int arm) => cleared[arm];
        public EnemyAI Boss => boss;

        // run state
        HeroKind chosen = HeroKind.Frog;
        int gold, kills, xp, level = 1, skillPoints;
        readonly int[] ranks = new int[4];
        readonly int[] gear = new int[5];
        int goldSpent;
        bool upgradeOpen, heart;
        float hitT = -9f, hurtT = -9f, lastCoinSfx, zoneCheck, poisonTick;
        bool hitCrit, hitKill;
        readonly List<string> artNames = new List<string>();
        readonly List<int> artValues = new List<int>();
        bool victory;
        string resultLine = "";
        int earned;
        EnemyAI boss;
        readonly int[] cleared = new int[2];
        readonly List<EnemyAI>[,] zoneMobs = new List<EnemyAI>[2, 3];

        // multipliers used by the hero
        public float DamageMul => (1f + 0.15f * ranks[0]) * (1f + 0.25f * WeaponTier(chosen)) * (1f + 0.12f * gear[0]) * (heart ? 1.3f : 1f);
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
            var go = Instantiate(prefab, playerSpawn.position + Vector3.up * 0.2f, playerSpawn.rotation);
            Hero = go.GetComponent<HeroController>();
            if (GameSettings.HasNick) Hero.Hp.displayName = GameSettings.Nick;
            Hero.Hp.Damaged += (a, amt) => { if (amt >= 2f) hurtT = Time.unscaledTime; };
            Hero.Hp.Died += k => StartCoroutine(EndAfter(2.5f, false, k != null && k.team == Team.Rival ? "Соперник оказался сильнее." : "Вы пали в джунглях."));
            Camera.main.GetComponent<ThirdPersonCamera>().SetTarget(go.transform);
            TimeLeft = matchLength;
            state = State.Playing;
            LockCursor(true);
            SpawnWorld();
            Toast("Зачищайте зоны — барьеры лиан откроют путь к арене");
            Toast("Соперник идёт к арене с другой стороны");
        }

        void LockCursor(bool on)
        {
            Cursor.lockState = on ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !on;
        }

        // Point inside an arm zone, offset sideways from the centreline.
        static Vector3 ZonePoint(int arm, float dist, float lateral)
        {
            var c = LGeo.ArmPoint(arm, dist) + LGeo.ArmRight(arm, dist) * lateral;
            return LGeo.OnGround(c);
        }

        EnemyAI Mob(InsectKind k, int arm, int zone, Vector3 pos)
        {
            var e = EnemyAI.Create(k, LGeo.OnGround(pos) + Vector3.up * 0.3f);
            e.arm = arm; e.zone = zone;
            if (arm >= 0) zoneMobs[arm, zone].Add(e);
            return e;
        }

        void Pack(int arm, int zone, Vector3 at, int count)
        {
            for (int i = 0; i < count; i++) Mob(InsectKind.Ant, arm, zone, at + Random.insideUnitSphere.Flat() * 2.5f);
        }

        void SpawnWorld()
        {
            for (int a = 0; a < 2; a++) for (int z = 0; z < 3; z++) zoneMobs[a, z] = new List<EnemyAI>();
            var arts = ArtifactNames.OrderBy(_ => Random.value).ToList();
            for (int arm = 0; arm < 2; arm++)
            {
                // zone 1: ant packs
                Pack(arm, 0, ZonePoint(arm, 136f, Random.Range(-4f, 4f)), 3);
                Pack(arm, 0, ZonePoint(arm, 122f, Random.Range(-5f, 5f)), 4);
                Pickup.Heal(ZonePoint(arm, 128f, 7f), 40);
                // zone 2: beetles with escorts, an artifact in the clearing
                Pack(arm, 1, ZonePoint(arm, 106f, Random.Range(-4f, 4f)), 3);
                Mob(InsectKind.Beetle, arm, 1, ZonePoint(arm, 95f, -4f));
                Mob(InsectKind.Beetle, arm, 1, ZonePoint(arm, 86f, 4f));
                Pickup.Artifact(ZonePoint(arm, 92f, 9f), arts[arm * 2], Random.Range(140, 260), RandomModel(artifactModels));
                Pickup.Heal(ZonePoint(arm, 100f, -8f), 45);
                // zone 3: the colossus and its guard
                Mob(InsectKind.Colossus, arm, 2, ZonePoint(arm, 54f, 0f));
                Pack(arm, 2, ZonePoint(arm, 64f, Random.Range(-4f, 4f)), 3);
                Pickup.Heal(ZonePoint(arm, 70f, 8f), 50);
            }
            // arena boss
            boss = EnemyAI.Create(InsectKind.Mantis, LGeo.OnGround(bossSpot.position) + Vector3.up * 0.3f, true);
            boss.Hp.max = boss.Hp.hp = 1800f;
            boss.aggro = 18f; boss.leash = LGeo.ArenaRadius - 4f;
            for (int i = 0; i < 4; i++)
            {
                float a = i * Mathf.PI * 0.5f + 0.4f;
                Pickup.Heal(LGeo.OnGround(new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 22f), 50);
            }

            // rival champion: the other hero type
            bool rivalMelee = chosen == HeroKind.Gecko;
            var model = rivalMelee ? rivalFrogModel : rivalModel;
            var ctrl = rivalMelee ? rivalFrogController : rivalController;
            if (model == null) { model = rivalModel; ctrl = rivalController; rivalMelee = false; }
            Rival = RivalAI.Create(model, ctrl, rivalSpawn.position + Vector3.up * 0.2f, rivalSpawn.rotation, rivalMelee ? "Жаба-Гром" : "Варан Скар", rivalMelee);
        }

        static GameObject RandomModel(GameObject[] arr) => arr != null && arr.Length > 0 ? arr[Random.Range(0, arr.Length)] : null;

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

            if (skillPoints > 0 && kb != null)
                for (int i = 0; i < 4; i++)
                {
                    var key = i == 0 ? kb.digit1Key : i == 1 ? kb.digit2Key : i == 2 ? kb.digit3Key : kb.digit4Key;
                    if (key.wasPressedThisFrame) BuySkill(i);
                }

            // zone clearing opens the next gate
            zoneCheck -= dt;
            if (zoneCheck <= 0f)
            {
                zoneCheck = 0.4f;
                for (int arm = 0; arm < 2; arm++)
                {
                    int z = cleared[arm];
                    if (z >= 3) continue;
                    var list = zoneMobs[arm, z];
                    list.RemoveAll(e => e == null || e.Dying);
                    if (list.Count == 0) OpenGate(arm, z, false);
                }
                if (!GatesForced && Elapsed >= gatesFallAt)
                {
                    GatesForced = true;
                    for (int arm = 0; arm < 2; arm++) while (cleared[arm] < 3) OpenGate(arm, cleared[arm], true);
                    Toast("Джунгли расступились: все барьеры пали!");
                }
            }

            // poison fog after the timer
            if (Poison)
            {
                poisonTick -= dt;
                if (poisonTick <= 0f)
                {
                    poisonTick = 1f;
                    if (Hero && !Hero.Hp.Dead) Hero.Hp.Damage(Hero.Hp.max * 0.04f, null);
                    if (Rival && !Rival.IsDead) Rival.Hp.Damage(Rival.Hp.max * 0.04f, null);
                }
            }
        }

        void OpenGate(int arm, int index, bool silent)
        {
            var gate = LMap.I ? LMap.I.GateOf(arm, index) : null;
            if (gate) gate.Open();
            cleared[arm] = Mathf.Max(cleared[arm], index + 1);
            if (silent) return;
            if (arm == 0)
            {
                Toast(index < 2 ? "Барьер пал — путь в зону " + (index + 2) + " открыт" : "Путь на арену открыт!");
                SFX.Play(Sfx.LevelUp, 0.6f, 0f);
            }
            else Toast(index < 2 ? "Соперник прорвался в зону " + (index + 2) : "Соперник вышел к арене!");
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
                earned = gold + artValues.Sum() + 300;
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
            if (Random.value < (e.boss || e.mini ? 1f : 0.12f)) Pickup.Heal(e.transform.position + Vector3.right, 40);
            if (e.mini) Pickup.Artifact(e.transform.position + Vector3.forward * 1.5f, "Панцирь Колосса", 220, RandomModel(artifactModels));
            bool byHero = killer != null && Hero != null && killer == Hero.Hp;
            bool byRival = killer != null && Rival != null && killer == Rival.Hp;
            if (byHero)
            {
                kills++;
                AddXp(e.xp);
                HitMarker(false, true);
                SFX.Play(Sfx.Kill, 0.7f, 0.03f);
            }
            if (byRival) Rival.AddXp(e.xp);
            if (e.boss)
            {
                if (byHero)
                {
                    heart = true;
                    Hero.Hp.SetMax(Hero.Hp.max + 60f); Hero.Hp.Heal(Hero.Hp.max);
                    Toast("Сердце Реликта ваше: +30% урона, +60 здоровья!");
                    SFX.Play(Sfx.Artifact, 1f, 0f);
                }
                else if (byRival)
                {
                    Rival.TakeHeart();
                    Toast("Соперник забрал Сердце Реликта!");
                }
                else Toast("Богомол-Страж повержен");
            }
            else if (e.mini && e.arm == 0) Toast("Жук-Колосс повержен");
        }

        public void OnRivalKilled(RivalAI r, Health killer)
        {
            int coins = Mathf.Clamp(r.gold / 10, 2, 15);
            for (int i = 0; i < coins; i++) Pickup.Gold(r.transform.position, Mathf.Max(5, (r.gold + 80) / coins));
            for (int i = 0; i < r.artifacts.Count; i++)
                Pickup.Artifact(r.transform.position + Random.insideUnitSphere.Flat() * 1.5f, r.artifacts[i], r.artifactValues[i], RandomModel(artifactModels));
            if (killer != null && Hero != null && killer == Hero.Hp) { kills++; AddXp(150); HitMarker(false, true); SFX.Play(Sfx.Kill, 0.9f, 0f); HitStop(0.08f); }
            Toast(r.Hp.displayName + " повержен!");
            StartCoroutine(EndAfter(2.5f, true, "Соперник повержен. Вы — последний выживший!"));
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

        // Where the rival walks when it has nothing to fight.
        public Vector3 RivalGoal(RivalAI r)
        {
            int z = cleared[r.arm];
            if (z < 3)
            {
                var list = zoneMobs[r.arm, z];
                EnemyAI best = null; float bd = float.MaxValue;
                foreach (var e in list)
                {
                    if (e == null || e.Dying) continue;
                    float d = (e.transform.position - r.transform.position).sqrMagnitude;
                    if (d < bd) { bd = d; best = e; }
                }
                if (best != null) return best.transform.position;
                return LGeo.ArmPoint(r.arm, LGeo.ZoneMid[z]);
            }
            // arena: hunt the hero if they are there, otherwise fight the boss
            if (Hero != null && !Hero.Hp.Dead && LGeo.Locate(Hero.transform.position, out _) == -1) return Hero.transform.position;
            if (boss != null && !boss.Hp.Dead) return boss.transform.position;
            if (Hero != null && !Hero.Hp.Dead && cleared[0] >= 3) return Hero.transform.position;
            return Vector3.zero;
        }

        public void RivalTick(RivalAI r)
        {
            Vector3 pos = r.transform.position;
            for (int i = Pickup.All.Count - 1; i >= 0; i--)
            {
                var p = Pickup.All[i];
                if (p == null || p.kind == PickupKind.Heal) continue;
                Vector3 d = p.transform.position - pos; d.y = 0;
                if (d.magnitude < 1.4f) { Collect(p, r); Destroy(p.gameObject); }
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
