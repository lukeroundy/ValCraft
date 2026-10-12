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
    /// (auto-input watches Minimap.IsOpen), so panning, zoom, pins and M/Esc to close are Valheim's own.</summary>
    internal static class MapToggle
    {
        static Type _t; static MethodInfo2 _m;
        sealed class MethodInfo2 { public System.Reflection.MethodInfo Set, IsOpen; public System.Reflection.FieldInfo Inst; public Type Mode; }
        internal static void Toggle()
        {
            try
            {
                if (_m == null)
                {
                    _t = AccessTools.TypeByName("Minimap");
                    _m = new MethodInfo2 { Set = AccessTools.Method(_t, "SetMapMode"), IsOpen = AccessTools.Method(_t, "IsOpen"), Inst = AccessTools.Field(_t, "m_instance"), Mode = AccessTools.Inner(_t, "MapMode") };
                }
                var inst = _m.Inst?.GetValue(null) ?? AccessTools.Property(_t, "instance")?.GetValue(null, null);
                if (inst == null || _m.Set == null || _m.Mode == null) { ValCraftPlugin.Log.LogInfo("[map] Minimap not available"); return; }
                bool open = _m.IsOpen != null && (bool)_m.IsOpen.Invoke(null, null);
                _m.Set.Invoke(inst, new[] { Enum.ToObject(_m.Mode, open ? 1 : 2) });
                ValCraftPlugin.Log.LogInfo(open ? "[map] closed from MC" : "[map] large map opened from MC (Valheim has input; M / Esc closes)");
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning("[map] toggle failed: " + e.GetBaseException().Message); }
        }
        internal static bool Open()
        {
            try { if (_t == null) _t = AccessTools.TypeByName("Minimap"); var m = AccessTools.Method(_t, "IsOpen"); return m != null && (bool)m.Invoke(null, null); } catch { return false; }
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
