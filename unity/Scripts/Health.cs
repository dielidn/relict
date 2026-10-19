using System.Collections.Generic;
using UnityEngine;

namespace Relict
{
    public class Health : MonoBehaviour
    {
        public static readonly List<Health> All = new List<Health>();

        public Team team;
        public float max = 100f;
        public float hp = 100f;
        [Range(0, 1)] public float armor;
        public string displayName = "";
        public Health lastAttacker;
        public System.Action<Health, float> Damaged;
        public System.Action<Health> Died;

        public bool Dead => hp <= 0f;
        public Vector3 Center => transform.position + Vector3.up * 1f;

        void OnEnable() { All.Add(this); }
        void OnDisable() { All.Remove(this); }

        public void Init(Team t, float m, string n) { team = t; max = m; hp = m; displayName = n; }

        public void SetMax(float m)
        {
            float k = max > 0 ? hp / max : 1f;
            max = m;
            hp = Mathf.Max(1f, m * k);
        }

        public void Damage(float amount, Health attacker)
        {
            if (Dead) return;
            if (attacker != null && attacker.team == team) return;
            amount *= 1f - Mathf.Clamp01(armor);
            hp -= amount;
            lastAttacker = attacker;
            Damaged?.Invoke(attacker, amount);
            if (MatchManager.I) MatchManager.I.DamageNumber(transform.position + Vector3.up * 2.1f, amount, team == Team.Player);
            if (hp <= 0f) { hp = 0f; Died?.Invoke(attacker); }
        }

        public void Heal(float a) { if (!Dead) hp = Mathf.Min(max, hp + a); }
    }
}
