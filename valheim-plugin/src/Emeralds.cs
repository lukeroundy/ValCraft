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
                var t = AccessTools.TypeByName(type); var m = t == null ? null : AccessTools.Method(t, method);
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
        static bool _init; static float _openedAt = -1f; static bool _checked;
        static void Init()
        {
            if (_init) return; _init = true;
            _t = AccessTools.TypeByName("Minimap");
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
                if (!open) PlayerSync.SetHud(true); // before opening: the map UI is under the hidden HUD
                _set.Invoke(inst, new[] { Enum.ToObject(_mode, open ? 1 : 2) });
                _openedAt = open ? -1f : Time.unscaledTime; _checked = false;
                ValCraftPlugin.Log.LogInfo(open ? "[map] closed from MC" : "[map] large map opened from MC (Valheim has input; M / Esc closes)");
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning("[map] toggle failed: " + e.GetBaseException().Message); }
        }
        internal static bool Open()
        {
            try { Init(); return _isOpen != null && (bool)_isOpen.Invoke(null, null); } catch { return false; }
        }
        /// <summary>Every frame: keep the map's UI chain visible; never leave input on an invisible map.</summary>
        internal static void Update()
        {
            if (_openedAt < 0) return;
            if (!Open()) { _openedAt = -1f; return; }
            try
            {
                var root = _large?.GetValue(Inst()) as GameObject;
                if (root != null && !root.activeInHierarchy)
                    for (var t = root.transform; t != null; t = t.parent)
                        if (!t.gameObject.activeSelf) { ValCraftPlugin.Log.LogInfo($"[map] activating hidden map ancestor '{t.name}'"); t.gameObject.SetActive(true); }
                if (!_checked && Time.unscaledTime - _openedAt > 1.5f)
                {
                    _checked = true;
                    bool vis = root != null && root.activeInHierarchy;
                    ValCraftPlugin.Log.LogInfo($"[map] large map visible={vis} (root {(root != null ? root.name : "null")})");
                    if (!vis) { _set.Invoke(Inst(), new[] { Enum.ToObject(_mode, 1) }); _openedAt = -1f; ValCraftPlugin.Log.LogWarning("[map] large map not visible: closed again, input back to Minecraft"); }
                }
            }
            catch (Exception e) { Diag.Warn("map", "[map] " + e.GetBaseException().Message); }
        }
    }

    /// <summary>0.12.0: Valheim weather -> MC (EvWeather every 2 s): u8 flags (1 rain, 2 thunder, 4 snow), u8 len, env name.</summary>
    internal static class WeatherLink
    {
        internal static bool Enabled = true;
        static float _next; static string _last;
        internal static void Update(ValCraft.Protocol.ValCraftLink link)
        {
            if (!Enabled || link == null || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;
            try
            {
                var t = AccessTools.TypeByName("EnvMan");
                var inst = AccessTools.Property(t, "instance")?.GetValue(null, null); if (inst == null) return;
                var env = AccessTools.Method(t, "GetCurrentEnvironment")?.Invoke(inst, null); if (env == null) return;
                string name = (string)AccessTools.Field(env.GetType(), "m_name").GetValue(env) ?? "";
                bool wet = (bool)AccessTools.Field(env.GetType(), "m_isWet").GetValue(env);
                string n = name.ToLowerInvariant();
                bool snow = n.Contains("snow") || n.Contains("blizzard");
                bool thunder = n.Contains("thunder") || (n.Contains("storm") && !snow);
                bool rain = wet || snow || thunder || n.Contains("rain");
                byte flags = (byte)((rain ? 1 : 0) | (thunder ? 2 : 0) | (snow ? 4 : 0));
                var nb = System.Text.Encoding.UTF8.GetBytes(name); int nl = Math.Min(nb.Length, 64);
                var p = new byte[2 + nl]; p[0] = flags; p[1] = (byte)nl; Array.Copy(nb, 0, p, 2, nl);
                link.TrySend(ValCraft.Protocol.ValCraftLink.EvWeather, p);
                if (name != _last) { _last = name; ValCraftPlugin.Log.LogInfo($"[weather] Valheim environment '{name}' -> MC rain {rain}, thunder {thunder}, snow {snow}"); }
            }
            catch (Exception e) { Diag.Warn("weather", "[weather] " + e.GetBaseException().Message); }
        }
    }
}
