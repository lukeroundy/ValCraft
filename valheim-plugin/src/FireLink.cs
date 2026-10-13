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
    /// 0.13.2: lit MC campfires (EvMcFires) count as Valheim fires: an invisible EffectArea Heat+Fire (warmth against cold/freezing,
    /// the fire needed for resting) and a Fire comfort piece (SE_Rested.GetNearbyComfortPieces postfix). Never of type Burning, so
    /// neither Valheim nor our own fire-damage check (Hazards) burns the player. Removed when the campfire goes out or breaks.
    /// </summary>
    internal static class FireLink
    {
        const float Radius = 3f;
        static bool _init, _ok;
        static Type _eaT, _pieceT; static FieldInfo _eaType, _eaSe, _pComfort, _pGroup; static object _fireGroup;
        static readonly Dictionary<long, GameObject> _fires = new Dictionary<long, GameObject>();
        static readonly Dictionary<long, Component> _pieces = new Dictionary<long, Component>();
        static byte[] _last; static Vector3 _anchorSeen; static bool _logged;

        internal static void Apply(Harmony h)
        {
            Init();
            var m = AccessTools.Method(Refl.T("SE_Rested"), "GetNearbyComfortPieces", new[] { typeof(Vector3) });
            if (m != null) { try { h.Patch(m, postfix: new HarmonyMethod(typeof(FireLink), nameof(ComfortPost))); } catch (Exception e) { ValCraftPlugin.Log.LogWarning("[fire] comfort hook: " + e.Message); m = null; } }
            int n = 0;
            n += Hook(h, Refl.T("CookingStation"), "IsFireLit", nameof(CookFirePost));
            n += Hook(h, Refl.T("CraftingStation"), "CheckFire", nameof(CraftFirePost));
            n += Hook(h, Refl.T("Player"), "UpdatePlacementGhost", nameof(GhostPost));
            ValCraftPlugin.Log.LogInfo($"[fire] cooking/crafting fire hooks: {n}/3");
            ValCraftPlugin.Log.LogInfo($"[fire] EffectArea={_eaT != null} m_type={_eaType != null} Piece.m_comfortGroup Fire={_fireGroup != null} comfort hook={m != null}");
        }

        static void Init()
        {
            if (_init) return; _init = true;
            _eaT = Refl.T("EffectArea"); _pieceT = Refl.T("Piece");
            _eaType = AccessTools.Field(_eaT, "m_type"); _eaSe = AccessTools.Field(_eaT, "m_statusEffect");
            _pComfort = AccessTools.Field(_pieceT, "m_comfort"); _pGroup = AccessTools.Field(_pieceT, "m_comfortGroup");
            try { if (_pGroup != null) _fireGroup = Enum.Parse(_pGroup.FieldType, "Fire"); } catch { }
            _ok = _eaT != null && _eaType != null;
        }

        static long Key(int x, int y, int z) => ((long)(x & 0x3FFFFF) << 42) | ((long)(y & 0xFFFFF) << 22) | (long)(z & 0x3FFFFF);

        /// <summary>EvMcFires: u16 n, n × i32 x,y,z (MC block positions of lit campfires).</summary>
        internal static void Receive(byte[] p) { _last = p; if (PlayerSync.Anchored) Sync(); }

        internal static void Update(bool connected)
        {
            if (!connected) { if (_fires.Count > 0) Clear("disconnected"); _last = null; return; }
            if (!PlayerSync.Anchored) return;
            if (PlayerSync.Anchor != _anchorSeen) { _anchorSeen = PlayerSync.Anchor; if (_fires.Count > 0) Clear("anchor moved"); if (_last != null) Sync(); }
        }

        static void Sync()
        {
            Init(); if (!_ok || _last == null || _last.Length < 2) return;
            _anchorSeen = PlayerSync.Anchor;
            int n = BitConverter.ToUInt16(_last, 0);
            var keep = new HashSet<long>();
            for (int i = 0; i < n && 2 + i * 12 + 12 <= _last.Length; i++)
            {
                int x = BitConverter.ToInt32(_last, 2 + i * 12), y = BitConverter.ToInt32(_last, 6 + i * 12), z = BitConverter.ToInt32(_last, 10 + i * 12);
                long k = Key(x, y, z); keep.Add(k);
                if (_fires.TryGetValue(k, out var go) && go != null) continue;
                Create(k, x, y, z);
            }
            foreach (var k in new List<long>(_fires.Keys)) if (!keep.Contains(k)) Remove(k);
        }

        static void Create(long k, int x, int y, int z)
        {
            try
            {
                var a = PlayerSync.Anchor;
                var pos = new Vector3(a.x + x + 0.5f, a.y + y + 0.5f, a.z - (z + 0.5f));
                var go = new GameObject($"ValCraft MC campfire ({x},{y},{z})");
                go.SetActive(false);
                go.transform.position = pos;
                int layer = LayerMask.NameToLayer("character_trigger"); if (layer >= 0) go.layer = layer;
                var sc = go.AddComponent<SphereCollider>(); sc.isTrigger = true; sc.radius = Radius;
                var ea = go.AddComponent(_eaT);
                _eaType.SetValue(ea, Enum.ToObject(_eaType.FieldType, 1 | 2)); // Heat | Fire, never Burning (8)
                _eaSe?.SetValue(ea, "");
                if (_pieceT != null && _pComfort != null && _fireGroup != null)
                {
                    var cgo = new GameObject("comfort"); cgo.SetActive(false); cgo.transform.SetParent(go.transform, false);
                    var pc = cgo.AddComponent(_pieceT); // inactive: Piece.Awake never runs (no ZNetView needed)
                    _pComfort.SetValue(pc, 1); _pGroup.SetValue(pc, _fireGroup);
                    _pieces[k] = pc;
                }
                go.SetActive(true);
                _fires[k] = go;
                if (!_logged || _fires.Count <= 4) { _logged = true; ValCraftPlugin.Log.LogInfo($"[fire] MC campfire at ({x},{y},{z}) -> Valheim heat/fire area at {pos} (r {Radius} m) + comfort"); }
            }
            catch (Exception e) { Diag.Warn("firearea", "[fire] creating the Valheim fire area: " + e.GetBaseException().Message); }
        }

        static void Remove(long k)
        {
            if (_fires.TryGetValue(k, out var go) && go != null) UnityEngine.Object.Destroy(go);
            _fires.Remove(k); _pieces.Remove(k);
            ValCraftPlugin.Log.LogInfo($"[fire] MC campfire gone: Valheim fire area removed ({_fires.Count} left)");
        }

        static void Clear(string why) { foreach (var k in new List<long>(_fires.Keys)) { if (_fires[k] != null) UnityEngine.Object.Destroy(_fires[k]); } _fires.Clear(); _pieces.Clear(); ValCraftPlugin.Log.LogInfo("[fire] MC campfire areas cleared: " + why); }

        internal static int Count => _fires.Count;
        internal static bool Near(Vector3 p, float r) { foreach (var go in _fires.Values) if (go != null && (go.transform.position - p).sqrMagnitude < r * r) return true; return false; }

        static int Hook(Harmony h, Type t, string name, string post)
        {
            var m = t == null ? null : AccessTools.Method(t, name);
            if (m == null) { ValCraftPlugin.Log.LogWarning($"[fire] {t?.Name}.{name} not found"); return 0; }
            try { h.Patch(m, postfix: new HarmonyMethod(typeof(FireLink), post)); return 1; }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[fire] {t.Name}.{name}: {e.Message}"); return 0; }
        }

        /// <summary>An MC campfire within <paramref name="r"/> m horizontally and -0.5..+2 m vertically (station above the fire).</summary>
        internal static bool FireAt(Vector3 p, float r)
        {
            foreach (var go in _fires.Values)
            {
                if (go == null) continue;
                var f = go.transform.position; float dx = f.x - p.x, dz = f.z - p.z, dy = p.y - f.y;
                if (dx * dx + dz * dz <= r * r && dy > -1f && dy < 2.5f) return true;
            }
            return false;
        }

        static FieldInfo _cpPoints, _cpReq, _csReq, _csHave, _csHaveObj, _cpHaveObj, _status, _ghostF;
        static float _nextCookLog;

        /// <summary>CookingStation.IsFireLit: Valheim looks for a Burning area / fireplace at m_fireCheckPoints; our MC fire area is never Burning.</summary>
        static void CookFirePost(Component __instance, ref bool __result)
        {
            if (__result || _fires.Count == 0 || __instance == null) return;
            try
            {
                var t = __instance.GetType();
                _cpPoints = _cpPoints ?? AccessTools.Field(t, "m_fireCheckPoints");
                bool hit = false;
                if (_cpPoints?.GetValue(__instance) is Transform[] pts && pts.Length > 0) { foreach (var pt in pts) if (pt != null && FireAt(pt.position, 1f)) { hit = true; break; } }
                else hit = FireAt(__instance.transform.position, 1f);
                if (!hit) return;
                __result = true;
                if (Time.unscaledTime >= _nextCookLog) { _nextCookLog = Time.unscaledTime + 30f; ValCraftPlugin.Log.LogInfo($"[fire] {__instance.name}: lit by an MC campfire"); }
            }
            catch { }
        }

        /// <summary>CraftingStation.CheckFire (cauldron etc. with m_craftRequireFire): also accept an MC campfire under/near the station.</summary>
        static void CraftFirePost(Component __instance)
        {
            if (_fires.Count == 0 || __instance == null) return;
            try
            {
                var t = __instance.GetType();
                _csReq = _csReq ?? AccessTools.Field(t, "m_craftRequireFire"); _csHave = _csHave ?? AccessTools.Field(t, "m_haveFire"); _csHaveObj = _csHaveObj ?? AccessTools.Field(t, "m_haveFireObject");
                if (_csReq == null || _csHave == null || !(bool)_csReq.GetValue(__instance) || (bool)_csHave.GetValue(__instance)) return;
                if (!FireAt(__instance.transform.position, 1.5f)) return;
                _csHave.SetValue(__instance, true);
                (_csHaveObj?.GetValue(__instance) as GameObject)?.SetActive(true);
            }
            catch { }
        }

        /// <summary>Placement: if this Valheim version refuses a fire-needing station for lack of fire, accept an MC campfire there.</summary>
        static void GhostPost(Component __instance)
        {
            if (_fires.Count == 0 || __instance == null || !ReferenceEquals(__instance, PlayerSync.Player)) return;
            try
            {
                var t = __instance.GetType();
                _status = _status ?? AccessTools.Field(t, "m_placementStatus"); _ghostF = _ghostF ?? AccessTools.Field(t, "m_placementGhost");
                var st = _status?.GetValue(__instance); if (st == null || st.ToString().IndexOf("Fire", StringComparison.OrdinalIgnoreCase) < 0) return;
                var g = _ghostF?.GetValue(__instance) as GameObject; if (g == null || !FireAt(g.transform.position, 1.5f)) return;
                _status.SetValue(__instance, Enum.ToObject(_status.FieldType, 0)); // Valid
            }
            catch { }
        }

        static void ComfortPost(Vector3 point, object __result)
        {
            if (_pieces.Count == 0 || !(__result is IList list)) return;
            try
            {
                foreach (var pc in _pieces.Values)
                {
                    if (pc == null || (pc.transform.position - point).sqrMagnitude > 100f || list.Contains(pc)) continue;
                    list.Add(pc);
                    break; // one Fire comfort is all Valheim counts per group
                }
            }
            catch { }
        }
    }
}
