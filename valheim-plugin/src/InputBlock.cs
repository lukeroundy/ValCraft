using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValCraft
{
    /// <summary>
    /// Suppresses Valheim's own input while F7 forwarding is on. Targets are resolved by name at runtime (no compile-time
    /// dependency on assembly_valheim); anything not found is logged and skipped.
    /// - ZInput.GetButton*/GetKey*/GetMouse*/... -> false / 0 / Vector2.zero (Valheim reads all controls through ZInput)
    /// - PlayerController.TakeInput, Player.TakeInput -> false (movement, attacks, hotbar)
    /// - GameCamera.UpdateMouseCapture -> skipped (we manage the cursor)
    /// </summary>
    internal static class InputBlock
    {
        static FieldInfo _localPlayer;

        public static void Apply(Harmony h)
        {
            int n = 0;
            var zinput = AccessTools.TypeByName("ZInput");
            if (zinput != null)
            {
                foreach (var m in zinput.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (m.IsGenericMethodDefinition || m.IsAbstract) continue;
                    bool name = m.Name.StartsWith("GetButton") || m.Name.StartsWith("GetKey") || m.Name.StartsWith("GetMouse") || m.Name.StartsWith("GetJoy") || m.Name == "GetRadialTap" || m.Name == "GetRadialMultiTap";
                    if (!name) continue;
                    string postfix = m.ReturnType == typeof(bool) ? nameof(FalseBool) : m.ReturnType == typeof(float) ? nameof(ZeroFloat) : m.ReturnType == typeof(Vector2) ? nameof(ZeroVector2) : null;
                    if (postfix == null) continue;
                    try { h.Patch(m, postfix: new HarmonyMethod(typeof(InputBlock), postfix)); n++; }
                    catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[block] ZInput.{m.Name}: {e.Message}"); }
                }
            }
            else ValCraftPlugin.Log.LogWarning("[block] ZInput not found");
            n += PatchByName(h, "PlayerController:TakeInput", nameof(FalseBool), postfix: true);
            n += PatchByName(h, "Player:TakeInput", nameof(FalseBool), postfix: true);
            n += PatchByName(h, "GameCamera:UpdateMouseCapture", nameof(SkipWhenActive), postfix: false);
            ValCraftPlugin.Log.LogInfo($"[block] {n} Valheim input methods patched");
            _localPlayer = AccessTools.Field(AccessTools.TypeByName("Player"), "m_localPlayer");
        }

        static int PatchByName(Harmony h, string target, string patch, bool postfix)
        {
            var m = AccessTools.Method(target);
            if (m == null) { ValCraftPlugin.Log.LogWarning($"[block] {target} not found"); return 0; }
            var hm = new HarmonyMethod(typeof(InputBlock), patch);
            try { if (postfix) h.Patch(m, postfix: hm); else h.Patch(m, prefix: hm); return 1; }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning($"[block] {target}: {e.Message}"); return 0; }
        }

        public static bool LocalPlayerExists()
        {
            try { return _localPlayer != null && _localPlayer.GetValue(null) as UnityEngine.Object != null; } catch { return false; }
        }

        static void FalseBool(ref bool __result) { if (ValCraftPlugin.InputActive) __result = false; }
        static void ZeroFloat(ref float __result) { if (ValCraftPlugin.InputActive) __result = 0f; }
        static void ZeroVector2(ref Vector2 __result) { if (ValCraftPlugin.InputActive) __result = Vector2.zero; }
        static bool SkipWhenActive() => !ValCraftPlugin.InputActive;
    }
}
