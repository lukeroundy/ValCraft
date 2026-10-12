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
        internal static float Radius = 0.75f; // half-size of the square of vertices edited (1 m vertex grid -> 1-4 vertices)
        static Type _opType, _settingsType;
        static FieldInfo _settingsField;
        static MethodInfo _checkAccess;
        static bool _init, _ok;
        static readonly List<KeyValuePair<float, Action>> _verify = new List<KeyValuePair<float, Action>>();
        static readonly Dictionary<Component, object> _pendingSave = new Dictionary<Component, object>();
        static void FlushSaves()
        {
            if (_pendingSave.Count == 0) return;
            foreach (var kv in _pendingSave)
            {
                var tc = kv.Key; var hm = kv.Value; if (tc == null) continue;
                if (_save != null) { try { _save.Invoke(tc, Args(_save)); } catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[terrain-edit] {Sig(_save)} failed: {e.GetBaseException().Message}"); } }
                else if (!_warnedNoSave) { _warnedNoSave = true; ValCraftPlugin.Log.LogWarning("[terrain-edit] no TerrainComp save method found: edits are visible this session but won't persist"); }
                try { _poke?.Invoke(_poke.IsStatic ? null : hm, Args(_poke)); } catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[terrain-edit] {Sig(_poke)} failed: {e.GetBaseException().Message}"); }
            }
            _pendingSave.Clear();
        }
        internal static void Update()
        {
            FlushSaves();
            for (int i = _verify.Count - 1; i >= 0; i--)
                if (Time.unscaledTime >= _verify[i].Key) { var f = _verify[i].Value; _verify.RemoveAt(i); try { f(); } catch { } }
        }

        static readonly HashSet<string> TerrainIds = new HashSet<string>
        {
            "minecraft:grass_block", "minecraft:dirt", "minecraft:coarse_dirt", "minecraft:rooted_dirt", "minecraft:podzol",
            "minecraft:mycelium", /* 0.10.0: farmland is a player block */ "minecraft:dirt_path", "minecraft:mud", "minecraft:packed_mud",
            /* 0.10.0: minecraft:stone is a player block */ "minecraft:cobblestone", "minecraft:deepslate", "minecraft:cobbled_deepslate", "minecraft:granite",
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
                if (top <= ground + 0.05f || bottom > ground + 1.05f) { ValCraftPlugin.Log.LogInfo($"[terrain-edit] placed {id} not resting on Valheim ground (block {bottom:F2}..{top:F2}, ground {ground:F2}): no terrain change"); return; }
                target = top;
            }
            var pos = new Vector3(x + 0.5f + a.x, target, -(z + 0.5f) + a.z);
            if (_checkAccess != null)
            {
                try { if (!(bool)_checkAccess.Invoke(null, new object[] { pos, 0f, true, false })) { ValCraftPlugin.Log.LogInfo("[terrain-edit] blocked by a ward (PrivateArea)"); return; } } catch { }
            }
            if (Apply(pos, kind == 0))
            {
                ValCraftPlugin.Log.LogInfo($"[terrain-edit] {(kind == 0 ? "lowered" : "raised")} Valheim terrain at {pos} ({ground:F2} -> {target:F2}) for MC {(kind == 0 ? "break" : "place")} of {id} at ({x},{y},{z})");
                TerrainExport.Invalidate(x, z);
                float t0 = Time.unscaledTime; var p0 = pos;
                _verify.Add(new KeyValuePair<float, Action>(Time.unscaledTime + 3.0f, () => { if (!EditedSince(p0, t0)) Verify(x, z, target, ground); }));
            }
        }

        // History: 0.6.0/0.6.1 spawned a TerrainOp (its RPC resolves settings by prefab hash -> "Failed to deserialize
        // TerrainOp settings", cancelled); 0.6.2 called TerrainComp.DoOperation, which doesn't exist (by that signature)
        // in Luke's Valheim build, so nothing ran.
        // 0.6.3: the approach of JereKuusela's World Edit Commands (public domain / Unlicense,
        // https://github.com/JereKuusela/valheim-world_edit_commands, Terrain/TerrainOperations.cs + TerrainSave.cs +
        // TerrainSelect.cs), used by many players on current Valheim: write the TerrainComp's per-vertex arrays directly.
        //   compilers: Heightmap.FindHeightmap(pos, r+1, list) -> hmap.GetAndCreateTerrainCompiler()
        //   vertex (x,z) of a compiler: index = z*(m_width+1)+x, world = hmap.pos + ((x - hm.m_width/2)*m_scale, 0, (z - hm.m_width/2)*m_scale)
        //   level:  m_levelDelta[i] += y - hmap.m_heights[i]; m_smoothDelta[i] = 0; m_modifiedHeight[i] = m_levelDelta[i] != 0
        //   paint:  m_paintMask[i] = Color.red (dirt); m_modifiedPaint[i] = true
        //   save:   ZNetView.ClaimOwnership(); m_operations++; m_lastOpPoint = 0; m_lastOpRadius = 0; Save(); hmap.Poke(false)
        // Save() writes the arrays into the TerrainComp ZDO, so it persists with the world and replicates.
        static MethodInfo _findHms, _getComp, _save, _poke, _claim, _resetGrass;
        static FieldInfo _fWidth, _fOps, _fModH, _fLevel, _fSmooth, _fModP, _fPaint, _fHmap, _fLastPt, _fLastR, _fHmWidth, _fHmScale, _fHmHeights;
        static Type _hmType, _tcType;
        static bool _apiChecked, _apiOk;

        static bool _warnedNoSave;
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        static void DumpMembers(Type t)
        {
            try
            {
                var ms = new List<string>();
                foreach (var m in t.GetMethods(All)) ms.Add(Sig(m));
                ValCraftPlugin.Log.LogInfo($"[terrain-edit] {t.FullName} ({t.Assembly.GetName().Name} {t.Assembly.GetName().Version}) methods: {string.Join("; ", ms)}");
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[terrain-edit] member dump of {t} failed: {e.Message}"); }
        }
        internal static string Sig(MethodInfo m)
        {
            if (m == null) return "none";
            var ps = new List<string>(); foreach (var p in m.GetParameters()) ps.Add(p.ParameterType.Name);
            return $"{(m.IsStatic ? "static " : "")}{m.Name}({string.Join(",", ps)})";
        }
        // Only parameter types we can fill with defaults (bool -> false, numbers -> 0); exact names first, then name contains.
        static bool Fillable(MethodInfo m)
        {
            foreach (var p in m.GetParameters()) { var pt = p.ParameterType; if (pt != typeof(bool) && pt != typeof(int) && pt != typeof(float)) return false; }
            return !m.ContainsGenericParameters;
        }
        static MethodInfo FindMethod(Type t, string[] names, bool instanceOnly)
        {
            MethodInfo[] ms; try { ms = t.GetMethods(All); } catch { return null; }
            foreach (var n in names) { MethodInfo best = null; foreach (var m in ms) if (m.Name == n && Fillable(m) && !(instanceOnly && m.IsStatic) && (best == null || m.GetParameters().Length < best.GetParameters().Length)) best = m; if (best != null) return best; }
            foreach (var n in names) foreach (var m in ms) if (m.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0 && !m.Name.StartsWith("RPC_") && Fillable(m) && !(instanceOnly && m.IsStatic)) return m;
            return null;
        }
        static object[] Args(MethodInfo m)
        {
            var ps = m.GetParameters(); if (ps.Length == 0) return null;
            var a = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++) a[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType == typeof(bool) ? (object)false : ps[i].ParameterType == typeof(int) ? (object)0 : 0f);
            return a;
        }

        static bool CheckApi()
        {
            if (_apiChecked) return _apiOk;
            _apiChecked = true;
            _hmType = AccessTools.TypeByName("Heightmap");
            _tcType = AccessTools.TypeByName("TerrainComp");
            if (_hmType == null || _tcType == null) { ValCraftPlugin.Log.LogWarning("[terrain-edit] Heightmap/TerrainComp types not found"); return false; }
            var listT = typeof(List<>).MakeGenericType(_hmType);
            _findHms = AccessTools.Method(_hmType, "FindHeightmap", new[] { typeof(Vector3), typeof(float), listT });
            _getComp = AccessTools.Method(_hmType, "GetAndCreateTerrainCompiler");
            DumpMembers(_tcType); DumpMembers(_hmType);
            // Confirmed on Valheim 1.0.17 (0.6.4 log): TerrainComp.Save(Boolean), Heightmap.Poke(Int32,Boolean). Scan is the fallback.
            _poke = AccessTools.DeclaredMethod(_hmType, "Poke", new[] { typeof(int), typeof(bool) }) ?? FindMethod(_hmType, new[] { "Poke", "Regenerate", "RequestRebuild", "Rebuild" }, false);
            _save = AccessTools.DeclaredMethod(_tcType, "Save", new[] { typeof(bool) }) ?? FindMethod(_tcType, new[] { "Save", "SaveData", "SaveTerrain", "Store", "Serialize" }, true);
            var znv = AccessTools.TypeByName("ZNetView");
            _claim = znv == null ? null : AccessTools.Method(znv, "ClaimOwnership");
            var cs = AccessTools.TypeByName("ClutterSystem");
            _resetGrass = cs == null ? null : AccessTools.Method(cs, "ResetGrass", new[] { typeof(Vector3), typeof(float) });
            _fWidth = AccessTools.Field(_tcType, "m_width"); _fOps = AccessTools.Field(_tcType, "m_operations");
            _fModH = AccessTools.Field(_tcType, "m_modifiedHeight"); _fLevel = AccessTools.Field(_tcType, "m_levelDelta");
            _fSmooth = AccessTools.Field(_tcType, "m_smoothDelta"); _fModP = AccessTools.Field(_tcType, "m_modifiedPaint");
            _fPaint = AccessTools.Field(_tcType, "m_paintMask"); _fHmap = AccessTools.Field(_tcType, "m_hmap");
            _fLastPt = AccessTools.Field(_tcType, "m_lastOpPoint"); _fLastR = AccessTools.Field(_tcType, "m_lastOpRadius");
            _fHmWidth = AccessTools.Field(_hmType, "m_width"); _fHmScale = AccessTools.Field(_hmType, "m_scale"); _fHmHeights = AccessTools.Field(_hmType, "m_heights");
            var need = new (string, object)[] { ("Heightmap.FindHeightmap(pos,r,list)", _findHms), ("GetAndCreateTerrainCompiler", _getComp),
                ("m_width", _fWidth), ("m_modifiedHeight", _fModH), ("m_levelDelta", _fLevel), ("m_smoothDelta", _fSmooth), ("m_hmap", _fHmap),
                ("Heightmap.m_width", _fHmWidth), ("Heightmap.m_scale", _fHmScale), ("Heightmap.m_heights", _fHmHeights) };
            var missing = new List<string>();
            foreach (var (n, o) in need) if (o == null) missing.Add(n);
            _apiOk = missing.Count == 0;
            ValCraftPlugin.Log.Log(_apiOk ? BepInEx.Logging.LogLevel.Info : BepInEx.Logging.LogLevel.Warning,
                _apiOk ? $"[terrain-edit] TerrainComp API OK (save={Sig(_save)}, poke={Sig(_poke)}, paint={_fPaint != null && _fModP != null}, ops={_fOps != null}, claim={_claim != null}, poke={_poke != null})"
                       : $"[terrain-edit] TerrainComp API missing: {string.Join(", ", missing)} -> terrain edits disabled");
            return _apiOk;
        }

        /// <summary>Level the vertices within a square of half-size Radius around pos (x,z) to pos.y.</summary>
        static bool Apply(Vector3 pos, bool dig) => Apply(pos, dig, false, Radius);

        /// <summary>level = 0.9.1 hoe: flatten to pos.y within a circle of radius r (full inside r/2, smooth falloff to r).</summary>
        static bool Apply(Vector3 pos, bool dig, bool levelMode, float Radius)
        {
            if (!CheckApi()) return false;
            try
            {
                var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_hmType));
                _findHms.Invoke(null, new object[] { pos, Radius + 1f, list });
                if (list.Count == 0) { ValCraftPlugin.Log.LogWarning($"[terrain-edit] no heightmap at {pos}"); return false; }
                int verts = 0, comps = 0;
                foreach (var hmObj in list)
                {
                    var tc = _getComp.Invoke(hmObj, null) as Component;
                    if (tc == null) { ValCraftPlugin.Log.LogWarning("[terrain-edit] GetAndCreateTerrainCompiler returned null"); continue; }
                    var hm = _fHmap.GetValue(tc) as Component ?? hmObj as Component;
                    int w = (int)_fWidth.GetValue(tc), max = w + 1;
                    int hw = (int)_fHmWidth.GetValue(hm); float sc = (float)_fHmScale.GetValue(hm);
                    var heights = _fHmHeights.GetValue(hm) as List<float>;
                    var level = (float[])_fLevel.GetValue(tc); var smooth = (float[])_fSmooth.GetValue(tc); var modH = (bool[])_fModH.GetValue(tc);
                    var paint = _fPaint?.GetValue(tc) as Color[]; var modP = _fModP?.GetValue(tc) as bool[];
                    var origin = hm.transform.position;
                    int n = 0;
                    for (int x = 0; x < max; x++)
                        for (int z = 0; z < max; z++)
                        {
                            float vx = origin.x + (x - hw / 2) * sc, vz = origin.z + (z - hw / 2) * sc;
                            if (Mathf.Abs(vx - pos.x) > Radius || Mathf.Abs(vz - pos.z) > Radius) continue;
                            int i = z * max + x;
                            if (heights == null || i >= heights.Count || i >= level.Length) continue;
                            float cur = heights[i];
                            float delta;
                            if (levelMode)
                            {
                                float d = new Vector2(vx - pos.x, vz - pos.z).magnitude;
                                if (d > Radius) continue;
                                float wgt = d <= Radius * 0.5f ? 1f : 1f - (d - Radius * 0.5f) / (Radius * 0.5f);
                                delta = (pos.y - cur) * wgt;
                                if (Mathf.Abs(delta) < 0.01f) continue;
                            }
                            else
                            {
                                if (dig ? cur <= pos.y : cur >= pos.y) continue;   // only lower when digging / raise when placing
                                delta = pos.y - cur;
                            }
                            level[i] += delta;
                            smooth[i] = 0f;
                            modH[i] = level[i] != 0f;
                            if (dig && paint != null && modP != null && i < paint.Length) { paint[i] = Color.red; modP[i] = true; }
                            n++;
                        }
                    if (n == 0) continue;
                    var nv = tc.GetComponent(AccessTools.TypeByName("ZNetView"));
                    if (nv != null && _claim != null) _claim.Invoke(nv, null);
                    if (_fOps != null) _fOps.SetValue(tc, (int)_fOps.GetValue(tc) + 1);
                    _fLastPt?.SetValue(tc, Vector3.zero); _fLastR?.SetValue(tc, 0f);
                    // 0.10.0: Save + Poke deferred to once per frame per TerrainComp (TNT = dozens of column edits in one frame)
                    _pendingSave[tc] = hm;
                    verts += n; comps++;
                }
                try { var csT = AccessTools.TypeByName("ClutterSystem"); var inst = csT == null ? null : (AccessTools.Field(csT, "m_instance")?.GetValue(null) ?? AccessTools.Property(csT, "instance")?.GetValue(null, null)); if (inst != null) _resetGrass?.Invoke(inst, new object[] { pos, Radius + 0.5f }); } catch { }
                ValCraftPlugin.Log.LogInfo($"[terrain-edit] wrote {verts} vertices on {comps}/{list.Count} TerrainComp(s) to y={pos.y:F2}");
                _edits.Add(new KeyValuePair<float, Vector3>(Time.unscaledTime, pos));
                if (_edits.Count > 64) _edits.RemoveAt(0);
                if (verts == 0) ValCraftPlugin.Log.LogInfo($"[terrain-edit] no terrain vertex within {Radius} m of {pos} needed changing");
                return verts > 0;
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[terrain-edit] direct TerrainComp edit failed: {e.GetBaseException()}"); return false; }
        }

        /// <summary>0.9.1 EvLevelGround (MC shift+right-click with a hoe): level Valheim ground at the aimed point to the player's feet height.</summary>
        internal static void LevelGround()
        {
            if (!Enabled || !TerrainExport.Active || PlayerSync.Player == null) return;
            if (!_init) Init();
            var cam = Camera.main; if (cam == null) return;
            int mask = LayerMask.GetMask("terrain");
            if (!Physics.Raycast(cam.transform.position, cam.transform.forward, out var hit, 8f, mask, QueryTriggerInteraction.Ignore)) { ValCraftPlugin.Log.LogInfo("[terrain-edit] level: no Valheim ground within 8 m of the aim"); return; }
            var feet = PlayerSync.Player.transform.position.y;
            var pos = new Vector3(hit.point.x, feet, hit.point.z);
            if (_checkAccess != null) { try { if (!(bool)_checkAccess.Invoke(null, new object[] { pos, 0f, true, false })) { ValCraftPlugin.Log.LogInfo("[terrain-edit] level blocked by a ward (PrivateArea)"); return; } } catch { } }
            const float r = 2f;
            if (Apply(pos, false, true, r))
            {
                var a = PlayerSync.Anchor;
                int cx = Mathf.FloorToInt(pos.x - a.x), cz = Mathf.FloorToInt(-(pos.z - a.z));
                for (int dx = -3; dx <= 3; dx += 3) for (int dz = -3; dz <= 3; dz += 3) TerrainExport.Invalidate(cx + dx, cz + dz);
                ValCraftPlugin.Log.LogInfo($"[terrain-edit] levelled ground at {hit.point} (r {r} m) to the player's feet y={feet:F2}");
            }
        }

        /// <summary>0.10.1: recent edits; a verify is skipped when a later edit (hoe levelling, place after break) touched the spot,
        /// which produced false "NOT APPLIED" warnings in the 0.10.0 log.</summary>
        static readonly List<KeyValuePair<float, Vector3>> _edits = new List<KeyValuePair<float, Vector3>>();
        static bool EditedSince(Vector3 p, float t0)
        {
            foreach (var e in _edits)
                if (e.Key > t0 + 0.001f && new Vector2(e.Value.x - p.x, e.Value.z - p.z).sqrMagnitude < 9f) return true;
            return false;
        }

        /// <summary>Called on tile re-sample after an edit: report whether Valheim's ground actually moved.</summary>
        internal static void Verify(int x, int z, float target, float before = float.NaN)
        {
            var a = PlayerSync.Anchor;
            float h = TerrainExport.HeightAtMc(x + 0.5f, z + 0.5f, a);
            // 0.10.0: the column centre sits between 1 m vertices, so a 1-block edit shows up only partly there: moved toward target = applied
            bool ok = !float.IsNaN(h) && (Mathf.Abs(h - target) < 0.35f || (!float.IsNaN(before) && Mathf.Abs(h - target) < Mathf.Abs(before - target) - 0.02f));
            if (ok) return;
            ValCraftPlugin.Log.Log(ok ? BepInEx.Logging.LogLevel.Info : BepInEx.Logging.LogLevel.Warning,
                $"[terrain-edit] check MC ({x},{z}): Valheim ground now {h:F2}, target {target:F2} -> {(ok ? "OK" : "NOT APPLIED (see Unity errors above / ward / 8 m limit)")}");
        }
    }
}
