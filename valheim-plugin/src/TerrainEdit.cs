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
            _ok = _opType != null && _settingsType != null && _settingsField != null;
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
            }
        }

        static bool Apply(Vector3 pos, bool dig)
        {
            var go = new GameObject("ValCraft_TerrainOp");
            try
            {
                go.SetActive(false);
                go.transform.position = pos;
                var op = go.AddComponent(_opType);
                var st = _settingsField.GetValue(op) ?? Activator.CreateInstance(_settingsType);
                void Set(string n, object v) { var f = AccessTools.Field(_settingsType, n); if (f != null) f.SetValue(st, Convert.ChangeType(v, f.FieldType)); }
                Set("m_level", true); Set("m_levelOffset", 0f); Set("m_levelRadius", Radius); Set("m_square", true);
                Set("m_raise", false); Set("m_smooth", false);
                Set("m_paintCleared", dig); Set("m_paintRadius", Radius); Set("m_paintHeightCheck", false);
                _settingsField.SetValue(op, st);
                go.SetActive(true);   // TerrainOp.Awake: find heightmaps, TerrainComp.ApplyOperation (RPC), destroy self
                return true;
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[terrain-edit] TerrainOp failed: {e.GetBaseException().Message}"); UnityEngine.Object.Destroy(go); return false; }
        }
    }
}
