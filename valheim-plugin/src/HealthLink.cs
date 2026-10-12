using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ValCraft.Protocol;

namespace ValCraft
{
    /// <summary>
    /// 0.8.1 health link, Minecraft authoritative.
    ///  - MC streams EvHealth (hp, max, food, dead). The hidden Valheim player's health is set to hp/max * Valheim max
    ///    every frame (Valheim food/rest regen is overridden).
    ///  - Damage that reaches the hidden player (Character.ApplyDamage, i.e. after armor/resistances) is cancelled and sent
    ///    to MC as EvDamage, scaled by MC max / Valheim max. Fall damage is dropped (MC does its own falls).
    ///  - MC death -> Valheim Character.OnDeath (normal tombstone + respawn flow). The respawned player is re-hidden and
    ///    re-anchored by PlayerSync when MC respawns.
    /// Also: ZNetScene self-heal (removes destroyed ZNetViews from m_instances; see 0.8.1 notes) and error rate limiting.
    /// </summary>
    internal static class HealthLink
    {
        internal static bool Enabled = true;
        internal static float LastBurnForward = -100f;
        static float _hp = -1, _max = 20; static int _food; static bool _mcDead, _killedForMc;
        static bool _init;
        static Type _charT, _hitT;
        static MethodInfo _setHealth, _getMax, _isDead, _onDeath, _total;
        static FieldInfo _hitType, _dir, _push;
        static ValCraftLink _link;
        static Component _lastPlayer;

        static void Init()
        {
            _init = true;
            _charT = AccessTools.TypeByName("Character"); _hitT = AccessTools.TypeByName("HitData");
            _setHealth = AccessTools.Method(_charT, "SetHealth", new[] { typeof(float) });
            _getMax = AccessTools.Method(_charT, "GetMaxHealth", Type.EmptyTypes);
            _isDead = AccessTools.Method(_charT, "IsDead", Type.EmptyTypes);
            _onDeath = AccessTools.Method(_charT, "OnDeath", Type.EmptyTypes);
            _total = AccessTools.Method(_hitT, "GetTotalDamage", Type.EmptyTypes);
            _hitType = AccessTools.Field(_hitT, "m_hitType"); _dir = AccessTools.Field(_hitT, "m_dir"); _push = AccessTools.Field(_hitT, "m_pushForce");
            ValCraftPlugin.Log.LogInfo($"[health] SetHealth={_setHealth != null} GetMaxHealth={_getMax != null} OnDeath={_onDeath != null} GetTotalDamage={_total != null}");
        }

        internal static void Apply(Harmony h)
        {
            var ch = AccessTools.TypeByName("Character");
            var apply = ch == null ? null : AccessTools.Method(ch, "ApplyDamage");
            if (apply != null) h.Patch(apply, prefix: new HarmonyMethod(typeof(HealthLink), nameof(ApplyDamagePre)));
            var zs = AccessTools.TypeByName("ZNetScene");
            var rm = zs == null ? null : AccessTools.Method(zs, "RemoveObjects");
            if (rm != null) h.Patch(rm, prefix: new HarmonyMethod(typeof(HealthLink), nameof(RemoveObjectsPre)));
            ValCraftPlugin.Log.LogInfo($"[health] ApplyDamage hook {(apply != null ? "on" : "missing")}, ZNetScene self-heal {(rm != null ? "on" : "missing")}");
        }

        static bool Linked(object c) => Enabled && PlayerSync.Following && _hp >= 0 && PlayerSync.Player != null && ReferenceEquals(c, PlayerSync.Player);

        static bool ApplyDamagePre(object __instance, object hit)
        {
            try
            {
                if (hit == null) return true;
                if (!_init) Init();
                if (MobBridge.IsProxy(__instance))
                {
                    float vd = _total == null ? 0 : (float)_total.Invoke(hit, null);
                    var pd = _dir == null ? Vector3.zero : (Vector3)_dir.GetValue(hit);
                    MobBridge.ForwardProxyHit(__instance, vd, pd, hit);
                    return false; // proxies never take Valheim damage themselves
                }
                if (!Linked(__instance)) return true;
                int ht = _hitType == null ? 0 : Convert.ToInt32(_hitType.GetValue(hit));
                if (ht == 3) return false; // Fall: MC handles falls
                if (ht == 5) LastBurnForward = Time.unscaledTime;
                float dmg = _total == null ? 0 : (float)_total.Invoke(hit, null);
                float vmax = _getMax == null ? 25f : Mathf.Max(1f, (float)_getMax.Invoke(__instance, null));
                float mc = dmg / vmax * _max;
                if (mc <= 0.001f || _link == null) return false;
                var d = _dir == null ? Vector3.zero : (Vector3)_dir.GetValue(hit);
                float push = _push == null ? 0 : (float)_push.GetValue(hit);
                // MC +Z = Unity -Z
                _link.TrySend(ValCraftLink.EvDamage, HealthCodec.Damage(mc, (byte)ht, d.x, -d.z, push));
                Diag.Info("dmg", $"[health] Valheim hit ({ht}) {dmg:F1}/{vmax:F0} -> MC {mc:F2}/{_max:F0}");
                return false;
            }
            catch (Exception e) { Diag.Warn("dmgerr", "[health] damage forward failed: " + e.GetBaseException().Message); return true; }
        }

        // Valheim spams NRE from RemoveObjects forever if a ZNetView in m_instances was destroyed without ZNetScene.Destroy.
        static FieldInfo _instF; static readonly List<object> _dead = new List<object>();
        static void RemoveObjectsPre(object __instance)
        {
            try
            {
                _instF = _instF ?? AccessTools.Field(__instance.GetType(), "m_instances");
                if (!(_instF?.GetValue(__instance) is IDictionary d)) return;
                _dead.Clear();
                foreach (DictionaryEntry kv in d) if (!(kv.Value is UnityEngine.Object o) || o == null) _dead.Add(kv.Key);
                if (_dead.Count == 0) return;
                foreach (var k in _dead) d.Remove(k);
                Diag.Warn("znet", $"[fix] removed {_dead.Count} destroyed object(s) from ZNetScene (prevents the RemoveObjects NullReferenceException spam)");
            }
            catch (Exception e) { Diag.Warn("znetfix", "[fix] ZNetScene self-heal failed: " + e.Message); }
        }

        internal static void Receive(byte[] p)
        {
            var (hp, max, food, dead) = HealthCodec.ReadHealth(p);
            if (dead && !_mcDead) ValCraftPlugin.Log.LogInfo("[health] Minecraft player died");
            if (!dead && _mcDead) { ValCraftPlugin.Log.LogInfo("[health] Minecraft player respawned"); _killedForMc = false; PlayerSync.ReAnchor(); }
            _hp = hp; _max = Mathf.Max(1f, max); _food = food; _mcDead = dead;
        }

        internal static void Reset() { _hp = -1; _mcDead = false; _killedForMc = false; }

        internal static void Update(ValCraftLink link)
        {
            _link = link;
            if (!Enabled || _hp < 0) return;
            if (!_init) Init();
            var pl = PlayerSync.Player;
            if (pl == null || !PlayerSync.Following || _setHealth == null) return;
            if (!ReferenceEquals(pl, _lastPlayer)) { _lastPlayer = pl; ValCraftPlugin.Log.LogInfo("[health] linked to Valheim player " + pl.name); }
            try
            {
                if (_isDead != null && (bool)_isDead.Invoke(pl, null)) return;
                if (_mcDead)
                {
                    if (!_killedForMc && _onDeath != null)
                    {
                        _killedForMc = true;
                        _setHealth.Invoke(pl, new object[] { 0f });
                        ValCraftPlugin.Log.LogInfo("[health] killing the Valheim player (Minecraft death): tombstone + respawn");
                        _onDeath.Invoke(pl, null);
                    }
                    return;
                }
                float vmax = (float)_getMax.Invoke(pl, null);
                float want = Mathf.Max(0.1f, _hp / _max * vmax);
                _setHealth.Invoke(pl, new object[] { want });
            }
            catch (Exception e) { Diag.Warn("hpupd", "[health] update failed: " + e.GetBaseException().Message); }
        }
    }

    /// <summary>Rate-limited logging: each key at most once per 10 s, with a suppressed count.</summary>
    internal static class Diag
    {
        static readonly Dictionary<string, (float t, int n)> _s = new Dictionary<string, (float, int)>();
        static bool Allow(string k, out int sup)
        {
            float now = Time.unscaledTime; sup = 0;
            if (_s.TryGetValue(k, out var e) && now - e.t < 10f) { _s[k] = (e.t, e.n + 1); return false; }
            sup = e.n; _s[k] = (now, 0); return true;
        }
        internal static void Warn(string k, string m) { if (Allow(k, out int s)) ValCraftPlugin.Log.LogWarning(s > 0 ? $"{m} (+{s} suppressed)" : m); }
        internal static void Info(string k, string m) { if (Allow(k, out int s)) ValCraftPlugin.Log.LogInfo(s > 0 ? $"{m} (+{s} more)" : m); }
    }
}
