using System;
using HarmonyLib;
using UnityEngine;

namespace ValCraft
{
    /// <summary>0.12.0: a mined-out Valheim rock piece near the player (MineRock5 area health 0, MineRock area hidden,
    /// destructible rocks) gives an MC emerald with a very small chance ([Items] EmeraldChance).</summary>
    internal static class Emeralds
    {
        internal static float Chance = 0.005f;
        static readonly System.Random Rng = new System.Random();

        internal static void Apply(Harmony h)
        {
            int n = 0;
            void P(string type, string method, string post)
            {
                var t = Refl.T(type); var m = t == null ? null : AccessTools.Method(t, method);
                if (m == null) { ValCraftPlugin.Log.LogInfo($"[emerald] {type}.{method} missing"); return; }
                h.Patch(m, postfix: new HarmonyMethod(typeof(Emeralds), post)); n++;
            }
            P("MineRock5", "RPC_SetAreaHealth", nameof(AreaHealthPost));
            P("MineRock", "RPC_Hide", nameof(HidePost));
            P("Destructible", "Destroy", nameof(DestroyPost));
            ValCraftPlugin.Log.LogInfo($"[emerald] {n} rock hooks (chance {Chance:P1})");
        }

        static void AreaHealthPost(Component __instance, float health) { if (health <= 0f) Roll(__instance); }
        static void HidePost(Component __instance) => Roll(__instance);
        static void DestroyPost(Component __instance)
        {
            if (__instance == null) return;
            var n = __instance.gameObject.name.ToLowerInvariant();
            if (n.Contains("rock") || n.Contains("stone") || n.Contains("boulder")) Roll(__instance);
        }

        static void Roll(Component c)
        {
            try
            {
                if (Chance <= 0 || c == null || PlayerSync.Player == null) return;
                if (Vector3.Distance(c.transform.position, PlayerSync.Player.transform.position) > 30f) return;
                if (Rng.NextDouble() >= Chance) return;
                ItemBridge.GiveMc("minecraft:emerald", 1);
                ValCraftPlugin.Log.LogInfo($"[emerald] {c.gameObject.name} mined: +1 minecraft:emerald");
            }
            catch (Exception e) { ValCraftPlugin.Log.LogDebug("[emerald] " + e.Message); }
        }
    }

    /// <summary>0.12.0: MC's M key (EvMapToggle) opens / closes Valheim's large map. While it is open Valheim owns input
    /// (auto-input watches Minimap.IsOpen), so panning, zoom, pins and M/Esc to close are Valheim's own.
    /// 0.12.1: the map lives under UI that ValCraft hides while following (HideValheimHud), so 0.12.0 opened it invisibly
    /// and input went to an invisible map (looked like an MC freeze). Now the HUD is shown while the map is open, any
    /// inactive ancestor of the large map root is activated, and if the map still isn't visible after 1.5 s it is closed
    /// again so input returns to Minecraft.</summary>
    internal static class MapToggle
    {
        static Type _t, _mode; static System.Reflection.MethodInfo _set, _isOpen; static System.Reflection.PropertyInfo _instP; static System.Reflection.FieldInfo _instF, _large;
        static bool _init; static float _openedAt = -1f; static bool _checked; static float _closedAt = -10f;
        /// <summary>0.12.3: a map opened with M must not be closed by the lore guard (it closes maps opened by interacts).</summary>
        internal static bool OpenedByUs => _openedAt >= 0;
        static void Init()
        {
            if (_init) return; _init = true;
            _t = Refl.T("Minimap");
            if (_t == null) { ValCraftPlugin.Log.LogWarning("[map] type Minimap not found"); return; }
            _instP = AccessTools.Property(_t, "instance");
            if (_instP == null) foreach (var f in AccessTools.GetDeclaredFields(_t)) if (f.IsStatic && f.FieldType == _t) { _instF = f; break; }
            _set = AccessTools.Method(_t, "SetMapMode"); _isOpen = AccessTools.Method(_t, "IsOpen", Type.EmptyTypes);
            _mode = AccessTools.Inner(_t, "MapMode") ?? _set?.GetParameters()[0].ParameterType;
            _large = AccessTools.Field(_t, "m_largeRoot");
            ValCraftPlugin.Log.LogInfo($"[map] Minimap: instance={(_instP != null ? "property" : _instF != null ? "field " + _instF.Name : "missing")}, SetMapMode={_set != null}, IsOpen={_isOpen != null}, MapMode={_mode != null}, m_largeRoot={_large != null}");
        }
        static object Inst() { try { return _instP != null ? _instP.GetValue(null, null) : _instF?.GetValue(null); } catch { return null; } }
        internal static void Toggle()
        {
            try
            {
                Init();
                var inst = Inst();
                if (inst == null || _set == null || _mode == null) { ValCraftPlugin.Log.LogInfo("[map] Minimap not available"); return; }
                bool open = Open();
                if (open) { Close("M in Minecraft"); return; }
                // 0.12.3: the M press that closed the map in Valheim is then forwarded to MC (input returns to it) and came
                // back as another open request: ignore opens shortly after a close
                if (Time.unscaledTime - _closedAt < 0.6f) { ValCraftPlugin.Log.LogDebug("[map] open request right after close ignored"); return; }
                PlayerSync.SetHud(true); // before opening: the map UI is under the hidden HUD
                _set.Invoke(inst, new[] { Enum.ToObject(_mode, 2) });
                _openedAt = Time.unscaledTime; _checked = false;
                ValCraftPlugin.Log.LogInfo("[map] large map opened from MC (Valheim has input; M / Esc closes)");
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning("[map] toggle failed: " + e.GetBaseException().Message); }
        }
        internal static bool Open()
        {
            try { Init(); return _isOpen != null && (bool)_isOpen.Invoke(null, null); } catch { return false; }
        }
        /// <summary>Every frame: keep the map's UI chain visible while it opens; close it on M / Esc (0.12.2: Valheim's own
        /// Map key is swallowed by ValCraft's input handling, and re-activating the root every frame fought Valheim's close).
        /// Closing restores HUD hiding (HudWanted) and auto-input gives control back to Minecraft.</summary>
        internal static void Update()
        {
            bool open = Open();
            if (!open) { if (_openedAt >= 0) { ValCraftPlugin.Log.LogInfo("[map] large map closed by Valheim; input back to Minecraft"); _closedAt = Time.unscaledTime; } _openedAt = -1f; return; }
            float now = Time.unscaledTime;
            if (_openedAt < 0) { _openedAt = now; _checked = true; } // opened by Valheim itself: still closable with M / Esc
            try
            {
                var root = _large?.GetValue(Inst()) as GameObject;
                if (!_checked)
                {
                    if (root != null && !root.activeInHierarchy)
                        for (var t = root.transform; t != null; t = t.parent)
                            if (!t.gameObject.activeSelf) { ValCraftPlugin.Log.LogInfo($"[map] activating hidden map ancestor '{t.name}'"); t.gameObject.SetActive(true); }
                    if (now - _openedAt > 1.5f)
                    {
                        _checked = true;
                        bool vis = root != null && root.activeInHierarchy;
                        ValCraftPlugin.Log.LogInfo($"[map] large map visible={vis} (root {(root != null ? root.name : "null")})");
                        if (!vis) { Close("not visible"); return; }
                    }
                }
                if (now - _openedAt < 0.3f) return; // the M press that opened it
                var kb = BepInEx.UnityInput.Current;
                if (kb.GetKeyDown(KeyCode.M)) Close("M");
                else if (kb.GetKeyDown(KeyCode.Escape)) Close("Esc");
                else if (_checked && root != null && !root.activeInHierarchy && now - _openedAt > 2f) Close("map hidden by Valheim");
            }
            catch (Exception e) { Diag.Warn("map", "[map] " + e.GetBaseException().Message); }
        }
        internal static void Close(string why)
        {
            try { Init(); if (Open()) _set.Invoke(Inst(), new[] { Enum.ToObject(_mode, 1) }); } catch (Exception e) { ValCraftPlugin.Log.LogWarning("[map] close failed: " + e.GetBaseException().Message); }
            _openedAt = -1f; _closedAt = Time.unscaledTime;
            ValCraftPlugin.Log.LogInfo($"[map] large map closed ({why}); input back to Minecraft");
        }
    }

    /// <summary>0.12.0: Valheim weather -> MC (EvWeather every 2 s): u8 flags (1 rain, 2 thunder, 4 snow), u8 len, env name.</summary>
    internal static class WeatherLink
    {
        internal static bool Enabled = true;
        static float _next, _nextLog; static string _last;
        internal static void Update(ValCraft.Protocol.ValCraftLink link)
        {
            if (!Enabled || link == null || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;
            try
            {
                var t = Refl.T("EnvMan");
                var inst = AccessTools.Property(t, "instance")?.GetValue(null, null); if (inst == null) return;
                var env = AccessTools.Method(t, "GetCurrentEnvironment")?.Invoke(inst, null) ?? AccessTools.Field(t, "m_currentEnv")?.GetValue(inst);
                if (env == null) { Diag.Warn("weather", "[weather] EnvMan has no current environment"); return; }
                string name = (string)AccessTools.Field(env.GetType(), "m_name")?.GetValue(env) ?? "";
                bool wet = (bool)(AccessTools.Field(env.GetType(), "m_isWet")?.GetValue(env) ?? false);
                // 0.12.3: also Valheim's own wet check (rain in the current biome/env), and the forced environment if any
                try { var iw = AccessTools.Method(t, "IsWet", Type.EmptyTypes); if (iw != null && iw.IsStatic) wet |= (bool)iw.Invoke(null, null); } catch { }
                try { var fe = AccessTools.Field(t, "m_forceEnv")?.GetValue(inst) as string; if (!string.IsNullOrEmpty(fe)) name = fe; } catch { }
                string n = name.ToLowerInvariant();
                bool snow = n.Contains("snow") || n.Contains("blizzard");
                bool thunder = n.Contains("thunder") || (n.Contains("storm") && !snow);
                bool rain = wet || snow || thunder || n.Contains("rain");
                byte flags = (byte)((rain ? 1 : 0) | (thunder ? 2 : 0) | (snow ? 4 : 0));
                var nb = System.Text.Encoding.UTF8.GetBytes(name); int nl = Math.Min(nb.Length, 64);
                var p = new byte[2 + nl]; p[0] = flags; p[1] = (byte)nl; Array.Copy(nb, 0, p, 2, nl);
                link.TrySend(ValCraft.Protocol.ValCraftLink.EvWeather, p);
                string key = name + flags;
                if (key != _last || Time.unscaledTime > _nextLog) { _last = key; _nextLog = Time.unscaledTime + 120f; ValCraftPlugin.Log.LogInfo($"[weather] Valheim environment '{name}' (wet {wet}) -> MC rain {rain}, thunder {thunder}, snow {snow}"); }
            }
            catch (Exception e) { Diag.Warn("weather", "[weather] " + e.GetBaseException().Message); }
        }
    }
}
