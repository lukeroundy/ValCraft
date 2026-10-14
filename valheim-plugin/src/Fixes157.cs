using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValCraft.Protocol;
namespace ValCraft
{
    /// <summary>0.15.7 Valheim -> MC links: re-seat offsets of MC mobs (MC picks on the drawn box), XP for Valheim kills,
    /// and Valheim's Rested effect.</summary>
    internal static class Fixes157
    {
        static float _nextOff, _nextRested; static float _sentRested = -1f;
        static Type _charT, _playerT; static System.Reflection.FieldInfo _lastHit, _boss, _health; static System.Reflection.MethodInfo _getAttacker, _getMaxHealth, _getLevel, _getSeMan, _getSes;
        static readonly Queue<int> _xp = new Queue<int>();

        internal static void Apply(Harmony h)
        {
            _charT = Refl.T("Character"); _playerT = Refl.T("Player");
            _lastHit = AccessTools.Field(_charT, "m_lastHit"); _boss = AccessTools.Field(_charT, "m_boss");
            _getMaxHealth = AccessTools.Method(_charT, "GetMaxHealth", Type.EmptyTypes); _getLevel = AccessTools.Method(_charT, "GetLevel", Type.EmptyTypes);
            _getAttacker = _lastHit == null ? null : AccessTools.Method(_lastHit.FieldType, "GetAttacker", Type.EmptyTypes);
            _getSeMan = AccessTools.Method(_charT, "GetSEMan", Type.EmptyTypes);
            var od = AccessTools.Method(_charT, "OnDeath");
            if (od != null) h.Patch(od, prefix: new HarmonyMethod(typeof(Fixes157), nameof(OnDeathPre)));
            ValCraftPlugin.Log.LogInfo($"[xp] Character.OnDeath hook={od != null}, m_lastHit={_lastHit != null}, GetAttacker={_getAttacker != null}, GetSEMan={_getSeMan != null}");
        }

        internal static int XpFor(float maxHealth, int level, bool boss) => ValheimXp.For(maxHealth, level, boss);

        static void OnDeathPre(object __instance)
        {
            try
            {
                if (!PlayerSync.Following || __instance == null || (_playerT != null && _playerT.IsInstanceOfType(__instance)) || MobBridge.IsProxy(__instance)) return;
                var me = PlayerSync.Player; if (me == null) return;
                object attacker = null;
                try { var hit = _lastHit?.GetValue(__instance); if (hit != null) attacker = _getAttacker?.Invoke(hit, null); } catch { }
                var c = (Component)__instance;
                bool mine = ReferenceEquals(attacker, me) || (attacker == null && Vector3.Distance(c.transform.position, me.transform.position) < 30f);
                if (!mine) return;
                float hp = _getMaxHealth != null ? (float)_getMaxHealth.Invoke(__instance, null) : 10f;
                int lv = _getLevel != null ? (int)_getLevel.Invoke(__instance, null) : 1;
                bool boss = _boss != null && (bool)_boss.GetValue(__instance);
                int xp = XpFor(hp, lv, boss);
                lock (_xp) _xp.Enqueue(xp);
                ValCraftPlugin.Log.LogInfo($"[xp] {c.name} killed (max HP {hp:F0}, level {lv}{(boss ? ", boss" : "")}) -> {xp} MC XP");
            }
            catch (Exception e) { Diag.Warn("xp", "[xp] " + e.GetBaseException().Message); }
        }

        internal static void Update(ValCraftLink link)
        {
            if (link == null) return;
            lock (_xp) while (_xp.Count > 0) { var b = BitConverter.GetBytes(_xp.Peek()); if (!link.TrySend(ValCraftLink.EvXp, b)) break; _xp.Dequeue(); }
            float t = Time.unscaledTime;
            if (t >= _nextOff)
            {
                _nextOff = t + 0.1f;
                var list = new List<KeyValuePair<uint, float>>();
                foreach (var kv in BlockRender.Offsets) if (Math.Abs(kv.Value) > 0.02f) { list.Add(kv); if (list.Count >= 256) break; }
                link.TrySend(ValCraftLink.EvMobOffsets, MobOffsetCodec.Encode(list));
            }
            if (t >= _nextRested && PlayerSync.Following)
            {
                _nextRested = t + 2f;
                float rem = RestedRemaining();
                if (Math.Abs(rem - _sentRested) > 3f || (rem <= 0) != (_sentRested <= 0))
                {
                    if (link.TrySend(ValCraftLink.EvRested, BitConverter.GetBytes(rem)))
                    {
                        if ((rem > 0) != (_sentRested > 0)) ValCraftPlugin.Log.LogInfo(rem > 0 ? $"[rested] Valheim Rested ({rem:F0} s left) -> MC effect" : "[rested] Valheim Rested ended -> MC effect removed");
                        _sentRested = rem;
                    }
                }
            }
        }

        static float RestedRemaining()
        {
            try
            {
                var p = PlayerSync.Player; if (p == null || _getSeMan == null) return 0f;
                var se = _getSeMan.Invoke(p, null); if (se == null) return 0f;
                _getSes = _getSes ?? AccessTools.Method(se.GetType(), "GetStatusEffects", Type.EmptyTypes);
                if (!(_getSes?.Invoke(se, null) is System.Collections.IEnumerable all)) return 0f;
                foreach (var e in all)
                {
                    if (!(e is UnityEngine.Object o) || !o.name.StartsWith("Rested")) continue;
                    var tt = e.GetType();
                    float ttl = (float)(AccessTools.Field(tt, "m_ttl")?.GetValue(e) ?? 0f), time = (float)(AccessTools.Field(tt, "m_time")?.GetValue(e) ?? 0f);
                    return ttl > 0 ? Math.Max(1f, ttl - time) : 600f;
                }
            }
            catch { }
            return 0f;
        }
    }
}
