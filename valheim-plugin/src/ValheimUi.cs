using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ValCraft
{
    /// <summary>Is any Valheim UI open that should own the keyboard/mouse? Resolved by name; missing checks are skipped.</summary>
    internal static class ValheimUi
    {
        static List<Func<bool>> _checks;

        static MethodInfo _menuVisible; static bool _menuInit;
        public static bool MenuOpen()
        {
            if (!_menuInit) { _menuInit = true; _menuVisible = AccessTools.Method("Menu:IsVisible", Type.EmptyTypes); }
            try { return _menuVisible != null && (bool)_menuVisible.Invoke(null, null); } catch { return false; }
        }

        static System.Reflection.MethodInfo _invVisible; static bool _invInit;
        /// <summary>0.10.1: Valheim inventory / workbench / crafting station UI open -> MC HUD hidden.</summary>
        public static bool InventoryOpen()
        {
            if (!_invInit) { _invInit = true; _invVisible = AccessTools.Method("InventoryGui:IsVisible", Type.EmptyTypes); }
            try { return _invVisible != null && (bool)_invVisible.Invoke(null, null); } catch { return false; }
        }

        public static bool AnyOpen()
        {
            if (_checks == null) Init();
            foreach (var c in _checks) { try { if (c()) return true; } catch { } }
            return false;
        }

        static void Init()
        {
            _checks = new List<Func<bool>>();
            var found = new List<string>();
            void Static(string type, string method)
            {
                var m = AccessTools.Method($"{type}:{method}", Type.EmptyTypes);
                if (m == null || !m.IsStatic || m.ReturnType != typeof(bool)) return;
                _checks.Add(() => (bool)m.Invoke(null, null)); found.Add($"{type}.{method}");
            }
            void Instance(string type, string method)
            {
                var t = Refl.T(type);
                var m = t == null ? null : AccessTools.Method(t, method, Type.EmptyTypes);
                var inst = t == null ? null : PlayerSync.FindSingleton(t);
                if (m == null || inst == null || m.ReturnType != typeof(bool)) return;
                _checks.Add(() => { var o = inst(); return o != null && (bool)m.Invoke(o, null); }); found.Add($"{type}.{method}");
            }
            Static("Menu", "IsVisible");          // Esc menu
            Static("InventoryGui", "IsVisible");  // inventory / crafting / containers
            Static("Minimap", "IsOpen");          // big map
            Static("TextInput", "IsVisible");     // sign / rename text input
            Static("StoreGui", "IsVisible");      // trader
            Static("Console", "IsVisible");       // F5 console
            Static("TextViewer", "IsVisible");
            Static("PlayerCustomizaton", "IsBarberGuiVisible");
            Instance("Chat", "HasFocus");
            Instance("Console", "IsVisible");
            ValCraftPlugin.Log.LogInfo($"[input] auto-input watches Valheim UI: {string.Join(", ", found.ToArray())}");
        }
    }
}
