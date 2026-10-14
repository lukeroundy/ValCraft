using System;
using HarmonyLib;

namespace ValCraft
{
    /// <summary>0.15.6: MC's O key (EvMapToggle payload 1) opens Valheim's Esc/pause menu (Menu.instance.Show()).
    /// While it is open Valheim owns input (ValheimUi.AnyOpen watches Menu.IsVisible), so Esc / its buttons close it.</summary>
    internal static class ValheimMenu
    {
        internal static void Show()
        {
            try
            {
                var t = Refl.T("Menu");
                object inst = AccessTools.Property(t, "instance")?.GetValue(null, null);
                if (inst == null) foreach (var f in AccessTools.GetDeclaredFields(t)) if (f.IsStatic && f.FieldType == t) { inst = f.GetValue(null); break; }
                var show = AccessTools.Method(t, "Show", Type.EmptyTypes);
                if (inst == null || show == null) { ValCraftPlugin.Log.LogWarning($"[menu] Menu.instance={inst != null}, Show={show != null}"); return; }
                show.Invoke(inst, null);
                ValCraftPlugin.Log.LogInfo("[menu] O pressed in Minecraft: Valheim menu shown");
            }
            catch (Exception e) { ValCraftPlugin.Log.LogWarning("[menu] " + e.GetBaseException().Message); }
        }
    }
}
