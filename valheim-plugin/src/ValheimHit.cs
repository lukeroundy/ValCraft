using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ValCraft.Protocol;

namespace ValCraft
{
    /// <summary>
    /// Phase B: MC left-click on Valheim rocks, ore deposits, trees, logs, stumps and other destructibles.
    /// Every 0.1 s Valheim rays from its (MC-driven) camera; if the nearest solid hit within Range belongs to an
    /// IDestructible (Destructible, MineRock, MineRock5, TreeBase, TreeLog...) MC is told (EvAttackTarget) and its
    /// left-click then sends EvAttack(tool kind, MC tier) instead of attacking/mining in MC. We build a HitData like a
    /// Valheim tool hit (pickaxe or chop damage, tool tier from the config mapping) and call IDestructible.Damage, which
    /// is Valheim's networked RPC to the object's owner (same path as a real swing), so it works in multiplayer later.
    /// </summary>
    internal static class ValheimHit
    {
        internal static bool Enabled = true;
        internal static float Range = 3.5f;
        // index = MC tier (0 wood, 1 stone, 2 iron, 3 gold, 4 diamond, 5 netherite)
        internal static int[] TierMap = { 0, 1, 2, 1, 3, 4 };
        internal static float[] DamageMap = { 10f, 18f, 30f, 22f, 45f, 60f };
        internal static float HandDamage = 3f;
        // 0.8.1 combat: Valheim damage = MC attack damage (attribute, incl. item) x attack strength x CombatScale
        internal static bool Combat = true;
        internal static float CombatScale = 6f, CombatPush = 30f;
        static Type _characterT;

        static Type _destructibleI, _hitDataT;
        static MethodInfo _damage, _getZdoid;
        static bool _init;
        static int _mask;
        static float _next;
        static Component _target;
        static int _sent = -1;

        static void Init()
        {
            _init = true;
            _destructibleI = AccessTools.TypeByName("IDestructible");
            _hitDataT = AccessTools.TypeByName("HitData");
            _damage = _destructibleI == null || _hitDataT == null ? null : AccessTools.Method(_destructibleI, "Damage", new[] { _hitDataT });
            var ch = AccessTools.TypeByName("Character");
            _getZdoid = ch == null ? null : AccessTools.Method(ch, "GetZDOID");
            _characterT = ch;
            _mask = ~LayerMask.GetMask("character_ghost", "UI", "Water", "smoke", "viewblock", "skybox");
            ValCraftPlugin.Log.LogInfo($"[hit] IDestructible={_destructibleI != null}, HitData={_hitDataT != null}, Damage={_damage != null}");
        }

        internal static void Update(ValCraftLink link, bool active)
        {
            if (!Enabled) return;
            if (!_init) Init();
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.1f;
            _target = null;
            var player = PlayerSync.Player;
            var cam = Camera.main;
            if (active && player != null && cam != null && _destructibleI != null)
            {
                var hits = Physics.RaycastAll(cam.transform.position, cam.transform.forward, Range + 2f, _mask, QueryTriggerInteraction.Ignore);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                foreach (var h in hits)
                {
                    if (h.collider.transform.IsChildOf(player.transform)) continue;
                    if (Vector3.Distance(h.point, player.transform.position + Vector3.up * 1.6f) > Range) break;
                    _target = h.collider.GetComponentInParent(_destructibleI);
                    if (_target != null && !Combat && _characterT != null && _characterT.IsInstanceOfType(_target)) _target = null;
                    _hitPoint = h.point;
                    break; // nearest solid thing decides (terrain/buildings block)
                }
            }
            int v = _target != null ? 1 : 0;
            if (v != _sent && link.TrySend(ValCraftLink.EvAttackTarget, new[] { (byte)v }))
            {
                _sent = v;
                if (_target != null && _target.gameObject.name != _lastLogged) { _lastLogged = _target.gameObject.name; ValCraftPlugin.Log.LogInfo($"[hit] aiming at {_lastLogged} ({_target.GetType().Name}): MC left-click hits it"); }
            }
        }
        static Vector3 _hitPoint;
        static string _lastLogged;

        internal static void Reset() { _sent = -1; _target = null; }

        /// <summary>EvAttack from MC.</summary>
        internal static void Attack(byte[] p)
        {
            ItemBridge.NoteAction();
            if (!Enabled || p.Length < 8) return;
            if (!_init) Init();
            int kind = BitConverter.ToInt32(p, 0), tier = BitConverter.ToInt32(p, 4);
            var t = _target;
            if (t == null || _damage == null || _hitDataT == null) { ValCraftPlugin.Log.LogInfo("[hit] MC attack but no Valheim target in reach"); return; }
            if (_characterT != null && _characterT.IsInstanceOfType(t)) { HitCharacter(t, kind, p); return; }
            try
            {
                var hit = Activator.CreateInstance(_hitDataT);
                float dmg = tier >= 0 && tier < DamageMap.Length ? DamageMap[tier] : HandDamage;
                int vTier = tier >= 0 && tier < TierMap.Length ? TierMap[tier] : 0;
                if (kind != 1 && kind != 2) { dmg = HandDamage; vTier = 0; }   // swords/hands: tiny blunt-ish hit
                var dtF = AccessTools.Field(_hitDataT, "m_damage");
                object dt = dtF.GetValue(hit);
                void SetD(string n, float val) { var f = AccessTools.Field(dt.GetType(), n); if (f != null) f.SetValue(dt, val); }
                if (kind == 1) SetD("m_pickaxe", dmg);
                else if (kind == 2) SetD("m_chop", dmg);
                else { SetD("m_blunt", dmg); SetD("m_chop", dmg * 0.5f); SetD("m_pickaxe", dmg * 0.5f); }
                dtF.SetValue(hit, dt);
                void Set(string n, object val) { var f = AccessTools.Field(_hitDataT, n); if (f != null) try { f.SetValue(hit, Convert.ChangeType(val, f.FieldType)); } catch { } }
                Set("m_toolTier", vTier);
                Set("m_point", _hitPoint);
                var cam = Camera.main;
                if (cam != null) Set("m_dir", cam.transform.forward);
                var aF = AccessTools.Field(_hitDataT, "m_attacker");
                if (aF != null && _getZdoid != null && PlayerSync.Player != null) aF.SetValue(hit, _getZdoid.Invoke(PlayerSync.Player, null));
                _damage.Invoke(t, new[] { hit });
                ValCraftPlugin.Log.LogInfo($"[hit] {t.gameObject.name}: {(kind == 1 ? "pickaxe" : kind == 2 ? "chop" : "blunt")} {dmg} tier {vTier} (MC tool kind {kind}, tier {tier})");
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[hit] damage failed: {e.GetBaseException().Message}"); }
        }

        /// <summary>0.8.1: MC melee on a Valheim creature. Payload v10 tail: f32 mcAttackDamage, f32 strength (0..1).</summary>
        static void HitCharacter(Component t, int kind, byte[] p)
        {
            float mcDmg = p.Length >= 16 ? BitConverter.ToSingle(p, 8) : 1f, str = p.Length >= 16 ? Mathf.Clamp01(BitConverter.ToSingle(p, 12)) : 1f;
            try
            {
                var me = PlayerSync.Player;
                var hit = Activator.CreateInstance(_hitDataT);
                float dmg = Mathf.Max(0f, mcDmg * (0.2f + 0.8f * str * str) * CombatScale); // vanilla-like cooldown curve
                var dtF = AccessTools.Field(_hitDataT, "m_damage");
                object dt = dtF.GetValue(hit);
                string type = kind == 3 || kind == 2 ? "m_slash" : kind == 1 ? "m_pierce" : "m_blunt";
                AccessTools.Field(dt.GetType(), type)?.SetValue(dt, dmg);
                dtF.SetValue(hit, dt);
                AccessTools.Field(_hitDataT, "m_point")?.SetValue(hit, _hitPoint);
                var cam = Camera.main;
                var dir = cam != null ? cam.transform.forward : t.transform.position - me.transform.position;
                AccessTools.Field(_hitDataT, "m_dir")?.SetValue(hit, dir.normalized);
                AccessTools.Field(_hitDataT, "m_pushForce")?.SetValue(hit, CombatPush * str);
                var hitType = AccessTools.Field(_hitDataT, "m_hitType");
                if (hitType != null) hitType.SetValue(hit, Enum.ToObject(hitType.FieldType, 2)); // PlayerHit
                var setAtt = AccessTools.Method(_hitDataT, "SetAttacker");
                if (me != null && setAtt != null) setAtt.Invoke(hit, new object[] { me });
                else if (me != null && _getZdoid != null) AccessTools.Field(_hitDataT, "m_attacker")?.SetValue(hit, _getZdoid.Invoke(me, null));
                _damage.Invoke(t, new[] { hit });
                ValCraftPlugin.Log.LogInfo($"[combat] {t.gameObject.name}: {type.Substring(2)} {dmg:F1} (MC damage {mcDmg:F1}, strength {str:F2}, x{CombatScale})");
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[combat] hit failed: {e.GetBaseException().Message}"); }
        }
    }
}
