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
        static float _hp = -1, _max = 20; static int _food; static bool _mcDead, _killedForMc, _killFailed, _bypass; static Component _killedPlayer; static float _nextReady;
        static bool _init;
        static Type _charT, _hitT;
        static MethodInfo _setHealth, _getMax, _isDead, _onDeath, _total;
        static FieldInfo _hitType, _dir, _push;
        static ValCraftLink _link;
        static Component _lastPlayer;

        static void Init()
        {
            _init = true;
            _charT = Refl.T("Character"); _hitT = Refl.T("HitData");
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
            var ch = Refl.T("Character");
            var apply = ch == null ? null : AccessTools.Method(ch, "ApplyDamage");
            if (apply != null) h.Patch(apply, prefix: new HarmonyMethod(typeof(HealthLink), nameof(ApplyDamagePre)));
            var zs = Refl.T("ZNetScene");
            var rm = zs == null ? null : AccessTools.Method(zs, "RemoveObjects");
            if (rm != null) h.Patch(rm, prefix: new HarmonyMethod(typeof(HealthLink), nameof(RemoveObjectsPre)));
            ValCraftPlugin.Log.LogInfo($"[health] ApplyDamage hook {(apply != null ? "on" : "missing")}, ZNetScene self-heal {(rm != null ? "on" : "missing")}");
        }

        static bool Linked(object c) => Enabled && PlayerSync.Following && _hp >= 0 && PlayerSync.Player != null && ReferenceEquals(c, PlayerSync.Player);

        /// <summary>0.13.4: the 0.13.3 log showed Player.OnDeath throwing a NullReferenceException (m_lastHit is null when nothing hit
        /// us in Valheim) -> no tombstone, no Valheim respawn. Give it a last hit first; if it still fails, die through ApplyDamage.</summary>
        static void Kill(Component pl)
        {
            _killedPlayer = pl; _killFailed = false;
            object hit = null;
            try { hit = Activator.CreateInstance(_hitT); _lastHitF = _lastHitF ?? AccessTools.Field(_charT, "m_lastHit"); if (_lastHitF != null && _lastHitF.GetValue(pl) == null) _lastHitF.SetValue(pl, hit); } catch { }
            try { _onDeath.Invoke(pl, null); ValCraftPlugin.Log.LogInfo("[health] Valheim player died (OnDeath)"); return; }
            catch (Exception e) { var b = e.GetBaseException(); ValCraftPlugin.Log.LogWarning($"[health] OnDeath failed: {b.GetType().Name}: {b.Message} {b.StackTrace?.Split('\n')[0]}"); }
            try
            {
                var apply = AccessTools.Method(_charT, "ApplyDamage");
                var dmgF = AccessTools.Field(_hitT, "m_damage"); var dmg = dmgF.GetValue(hit); AccessTools.Field(dmg.GetType(), "m_damage").SetValue(dmg, 1e7f); dmgF.SetValue(hit, dmg);
                var args = new object[apply.GetParameters().Length]; args[0] = hit; for (int i = 1; i < args.Length; i++) { var pt = apply.GetParameters()[i]; args[i] = pt.HasDefaultValue ? pt.DefaultValue : (pt.ParameterType == typeof(bool) ? (object)false : Activator.CreateInstance(pt.ParameterType)); }
                _setHealth.Invoke(pl, new object[] { 1f });
                _bypass = true; try { apply.Invoke(pl, args); } finally { _bypass = false; }
                bool dead = _isDead != null && (bool)_isDead.Invoke(pl, null);
                ValCraftPlugin.Log.LogInfo($"[health] lethal ApplyDamage fallback: dead={dead}");
                if (!dead) _killFailed = true;
            }
            catch (Exception e) { _killFailed = true; ValCraftPlugin.Log.LogWarning("[health] lethal damage fallback failed: " + e.GetBaseException().Message); }
            if (_killFailed) ValCraftPlugin.Log.LogWarning("[health] Valheim player could not be killed: letting Minecraft respawn now");
        }
        static FieldInfo _lastHitF; static MethodInfo _isTeleporting; static FieldInfo _introF;

        /// <summary>0.13.4: EvRespawnReady (every 0.5 s while MC is dead) once Valheim's new player is alive and placed.</summary>
        internal static void RespawnGate(ValCraftLink link)
        {
            if (!_mcDead || !_killedForMc || link == null || Time.unscaledTime < _nextReady) return;
            _nextReady = Time.unscaledTime + 0.5f;
            bool ready = _killFailed;
            if (!ready)
            {
                var lp = PlayerSync.LocalPlayerNow();
                if (lp != null && !ReferenceEquals(lp, _killedPlayer))
                {
                    var pt = lp.GetType();
                    _isTeleporting = _isTeleporting ?? AccessTools.Method(pt, "IsTeleporting", Type.EmptyTypes); _introF = _introF ?? AccessTools.Field(pt, "m_intro");
                    bool dead = false, tp = false, intro = false;
                    try { dead = _isDead != null && (bool)_isDead.Invoke(lp, null); } catch { }
                    try { tp = _isTeleporting != null && (bool)_isTeleporting.Invoke(lp, null); } catch { }
                    try { intro = _introF != null && (bool)_introF.GetValue(lp); } catch { }
                    ready = !dead && !tp && !intro;
                }
            }
            if (!ready) { link.TrySend(ValCraftLink.EvRespawnReady, new byte[] { 0 }); return; } // MC: keep the respawn button disabled
            if (link.TrySend(ValCraftLink.EvRespawnReady, new byte[] { 1 })) Diag.Info("respready", "[health] Valheim player respawned: telling Minecraft to respawn");
        }

        static bool ApplyDamagePre(object __instance, object hit)
        {
            if (_bypass) return true;
            try
            {
                if (hit == null) return true;
                if (!_init) Init();
                if (_getAttacker == null) _getAttacker = HarmonyLib.AccessTools.Method(hit.GetType(), "GetAttacker", Type.EmptyTypes);
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
                uint att = 0;
                try { var ac = _getAttacker?.Invoke(hit, null) as Component; if (ac != null && !ReferenceEquals(ac, __instance)) att = (uint)ac.GetInstanceID(); } catch { }
                _link.TrySend(ValCraftLink.EvDamage, HealthCodec.Damage(mc, (byte)ht, d.x, -d.z, push, att));
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
            if (!dead && _mcDead) { ValCraftPlugin.Log.LogInfo("[health] Minecraft player respawned"); _killedForMc = false; _killFailed = false; _killedPlayer = null; if (!PlayerSync.AlignPending) PlayerSync.ReAnchor(); }
            _hp = hp; _max = Mathf.Max(1f, max); _food = food; _mcDead = dead;
        }

        static System.Reflection.MethodInfo _getAttacker;
        internal static bool McDead => _mcDead;
        internal static void Reset() { _hp = -1; _mcDead = false; _killedForMc = false; }

        internal static void Update(ValCraftLink link)
        {
            _link = link;
            try { RespawnGate(link); } catch (Exception e) { Diag.Warn("respgate", "[health] respawn gate: " + e.GetBaseException().Message); }
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
                        PlayerSync.ExpectRespawn();
                        Kill(pl);
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
