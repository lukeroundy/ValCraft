using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValCraft
{
    /// <summary>
    /// Phase B: MC terrain-block edits -> Valheim terrain, using Valheim's own TerrainOp (the component pickaxe/hoe
    /// spawn): its Awake finds the heightmaps and calls TerrainComp.ApplyOperation, which is an RPC to the TerrainComp's
    /// ZDO owner and is saved with the world (networked + persistent). We use a small square "level" op:
    ///  - break a terrain block that is in the ground -> level that cell down to the block's bottom
    ///  - place a terrain-type block resting on the ground -> level that cell up to the block's top
    /// Valheim limits terrain edits to +-8 m from the original height (TerrainComp clamps), like a pickaxe/hoe.
    /// Afterwards the tile is re-sampled and re-sent so MC's blocks + smooth heightfield follow (fall into holes).
    /// </summary>
    internal static class TerrainEdit
    {
        internal static bool Enabled = true;
        internal static float Radius = 0.6f;
        static Type _opType, _settingsType;
        static FieldInfo _settingsField;
        static MethodInfo _checkAccess;
        static bool _init, _ok;
        static readonly List<KeyValuePair<float, Action>> _verify = new List<KeyValuePair<float, Action>>();
        internal static void Update()
        {
            for (int i = _verify.Count - 1; i >= 0; i--)
                if (Time.unscaledTime >= _verify[i].Key) { var f = _verify[i].Value; _verify.RemoveAt(i); try { f(); } catch { } }
        }

        static readonly HashSet<string> TerrainIds = new HashSet<string>
        {
            "minecraft:grass_block", "minecraft:dirt", "minecraft:coarse_dirt", "minecraft:rooted_dirt", "minecraft:podzol",
            "minecraft:mycelium", "minecraft:farmland", "minecraft:dirt_path", "minecraft:mud", "minecraft:packed_mud",
            "minecraft:stone", "minecraft:cobblestone", "minecraft:deepslate", "minecraft:cobbled_deepslate", "minecraft:granite",
            "minecraft:diorite", "minecraft:andesite", "minecraft:tuff", "minecraft:sand", "minecraft:red_sand", "minecraft:gravel",
            "minecraft:clay", "minecraft:snow_block", "minecraft:blackstone", "minecraft:basalt", "minecraft:netherrack",
        };

        static void Init()
        {
            _init = true;
            _opType = AccessTools.TypeByName("TerrainOp");
            _settingsType = _opType == null ? null : AccessTools.Inner(_opType, "Settings");
            _settingsField = _opType == null ? null : AccessTools.Field(_opType, "m_settings");
            var pa = AccessTools.TypeByName("PrivateArea");
            _checkAccess = pa == null ? null : AccessTools.Method(pa, "CheckAccess", new[] { typeof(Vector3), typeof(float), typeof(bool), typeof(bool) });
            _ok = _settingsType != null;
            ValCraftPlugin.Log.LogInfo($"[terrain-edit] TerrainOp={_opType != null}, Settings={_settingsType != null}, m_settings={_settingsField != null}, PrivateArea.CheckAccess={_checkAccess != null}");
        }

        /// <summary>EvBlockChanged from MC (block coords in MC space). kind 0 = broken, 1 = placed.</summary>
        internal static void OnBlockChanged(int x, int y, int z, int kind, string id)
        {
            if (!Enabled || !TerrainExport.Active) return;
            if (!_init) Init();
            if (!TerrainIds.Contains(id)) { ValCraftPlugin.Log.LogInfo($"[terrain-edit] {(kind == 0 ? "broke" : "placed")} {id} at MC ({x},{y},{z}): not a terrain block, MC-only"); return; }
            var a = PlayerSync.Anchor;
            float ground = TerrainExport.HeightAtMc(x + 0.5f, z + 0.5f, a);    // Valheim world Y
            if (float.IsNaN(ground)) return;
            float bottom = y + a.y, top = y + 1 + a.y;
            float target;
            if (kind == 0)
            {
                if (bottom >= ground - 0.05f) { ValCraftPlugin.Log.LogInfo($"[terrain-edit] broke {id} above Valheim ground ({bottom:F2} >= {ground:F2}): no terrain change"); return; }
                target = bottom;
            }
            else
            {
                if (bottom > ground + 0.3f || top <= ground + 0.05f) { ValCraftPlugin.Log.LogInfo($"[terrain-edit] placed {id} not resting on Valheim ground: no terrain change"); return; }
                target = top;
            }
            var pos = new Vector3(x + 0.5f + a.x, target, -(z + 0.5f) + a.z);
            if (_checkAccess != null)
            {
                try { if (!(bool)_checkAccess.Invoke(null, new object[] { pos, 0f, true, false })) { ValCraftPlugin.Log.LogInfo("[terrain-edit] blocked by a ward (PrivateArea)"); return; } } catch { }
            }
            if (!_ok) { ValCraftPlugin.Log.LogWarning("[terrain-edit] TerrainOp not found; terrain edits disabled"); return; }
            if (Apply(pos, kind == 0))
            {
                ValCraftPlugin.Log.LogInfo($"[terrain-edit] {(kind == 0 ? "lowered" : "raised")} Valheim terrain at {pos} ({ground:F2} -> {target:F2}) for MC {(kind == 0 ? "break" : "place")} of {id} at ({x},{y},{z})");
                TerrainExport.Invalidate(x, z);
                _verify.Add(new KeyValuePair<float, Action>(Time.unscaledTime + 1.0f, () => Verify(x, z, target)));
            }
        }

        // History: 0.6.0/0.6.1 spawned a TerrainOp object. Its Awake -> TerrainComp.ApplyOperation sends only the op's
        // prefab hash, and the RPC handler resolves the settings from a prefab table we can't reliably add to
        // ("Failed to deserialize TerrainOp settings for prefab hash ..., cancelling TerrainOp" in both versions).
        // 0.6.2: skip the RPC: for every heightmap the edit touches, get/create its TerrainComp and call the owner-side
        // TerrainComp.DoOperation(pos, TerrainOp.Settings) (InternalDoOperation + Save into the TerrainComp ZDO + heightmap
        // poke), the same method the RPC handler ends in. The ZDO replicates/persists like any terrain edit.
        // We must own the TerrainComp's ZDO (always true in single player / as host); otherwise we claim ownership first.
        static MethodInfo _findHms, _getComp, _doOp, _isOwner, _claim, _poke;
        static FieldInfo _nviewF;
        static Type _hmType;

        static bool Apply(Vector3 pos, bool dig)
        {
            try
            {
                if (_doOp == null)
                {
                    _hmType = AccessTools.TypeByName("Heightmap");
                    var tcT = AccessTools.TypeByName("TerrainComp");
                    var listT = typeof(List<>).MakeGenericType(_hmType);
                    _findHms = AccessTools.Method(_hmType, "FindHeightmap", new[] { typeof(Vector3), typeof(float), listT });
                    _getComp = AccessTools.Method(_hmType, "GetAndCreateTerrainCompiler");
                    _poke = AccessTools.Method(_hmType, "Poke", new[] { typeof(bool) });
                    _doOp = AccessTools.Method(tcT, "DoOperation", new[] { typeof(Vector3), _settingsType });
                    _isOwner = AccessTools.Method(tcT, "IsOwner");
                    _nviewF = AccessTools.Field(tcT, "m_nview");
                    var znv = AccessTools.TypeByName("ZNetView");
                    _claim = znv == null ? null : AccessTools.Method(znv, "ClaimOwnership");
                    ValCraftPlugin.Log.LogInfo($"[terrain-edit] Heightmap.FindHeightmap(pos,r,list)={_findHms != null}, GetAndCreateTerrainCompiler={_getComp != null}, TerrainComp.DoOperation={_doOp != null}, IsOwner={_isOwner != null}, ClaimOwnership={_claim != null}");
                    if (_findHms == null || _getComp == null || _doOp == null) { _ok = false; return false; }
                }
                var st = Activator.CreateInstance(_settingsType);
                void Set(string n, object v) { var f = AccessTools.Field(_settingsType, n); if (f != null) f.SetValue(st, Convert.ChangeType(v, f.FieldType)); else ValCraftPlugin.Log.LogWarning($"[terrain-edit] TerrainOp.Settings.{n} missing"); }
                Set("m_level", true); Set("m_levelOffset", 0f); Set("m_levelRadius", Radius); Set("m_square", true);
                Set("m_raise", false); Set("m_smooth", false);
                Set("m_paintCleared", dig); Set("m_paintRadius", Radius); Set("m_paintHeightCheck", false);
                var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_hmType));
                _findHms.Invoke(null, new object[] { pos, Radius + 1f, list });
                if (list.Count == 0) { ValCraftPlugin.Log.LogWarning($"[terrain-edit] no heightmap at {pos}"); return false; }
                int done = 0;
                foreach (var hm in list)
                {
                    var tc = _getComp.Invoke(hm, null);
                    if (tc == null) { ValCraftPlugin.Log.LogWarning("[terrain-edit] GetAndCreateTerrainCompiler returned null"); continue; }
                    bool owner = _isOwner == null || (bool)_isOwner.Invoke(tc, null);
                    if (!owner)
                    {
                        var nv = _nviewF?.GetValue(tc);
                        if (nv != null && _claim != null) { _claim.Invoke(nv, null); ValCraftPlugin.Log.LogInfo("[terrain-edit] claimed TerrainComp ownership (another peer owned it)"); }
                    }
                    _doOp.Invoke(tc, new object[] { pos, st });
                    try { _poke?.Invoke(hm, new object[] { false }); } catch { }
                    done++;
                }
                ValCraftPlugin.Log.LogInfo($"[terrain-edit] TerrainComp.DoOperation applied on {done}/{list.Count} heightmap(s)");
                return done > 0;
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[terrain-edit] DoOperation failed: {e.GetBaseException()}"); return false; }
        }

        /// <summary>Called on tile re-sample after an edit: report whether Valheim's ground actually moved.</summary>
        internal static void Verify(int x, int z, float target)
        {
            var a = PlayerSync.Anchor;
            float h = TerrainExport.HeightAtMc(x + 0.5f, z + 0.5f, a);
            bool ok = !float.IsNaN(h) && Mathf.Abs(h - target) < 0.35f;
            ValCraftPlugin.Log.Log(ok ? BepInEx.Logging.LogLevel.Info : BepInEx.Logging.LogLevel.Warning,
                $"[terrain-edit] check MC ({x},{z}): Valheim ground now {h:F2}, target {target:F2} -> {(ok ? "OK" : "NOT APPLIED (see Unity errors above / ward / 8 m limit)")}");
        }
    }
}
