using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ValCraft
{
    /// <summary>0.14.1: Prism first-run login. accounts.json lives in Prism's data dir (exe dir when portable.txt is next to it).</summary>
    internal static class LauncherLogin
    {
        public static string PrismDataDir(string exe)
        {
            var dir = Path.GetDirectoryName(exe) ?? "";
            if (File.Exists(Path.Combine(dir, "portable.txt"))) return dir;
            if (Directory.Exists(Path.Combine(dir, "UserData"))) return Path.Combine(dir, "UserData");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrismLauncher");
        }

        /// <summary>Number of accounts in Prism's accounts.json; 0 = none / missing; -1 = could not tell (launch anyway).</summary>
        public static int PrismAccounts(string exe)
        {
            try
            {
                var f = Path.Combine(PrismDataDir(exe), "accounts.json");
                if (!File.Exists(f)) return 0;
                return CountAccounts(File.ReadAllText(f));
            }
            catch { return -1; }
        }

        public static int CountAccounts(string json)
        {
            var m = Regex.Match(json, "\"accounts\"\\s*:\\s*\\[(.*)\\]", RegexOptions.Singleline);
            if (!m.Success) return -1;
            if (m.Groups[1].Value.Trim().Length == 0) return 0;
            int n = Regex.Matches(m.Groups[1].Value, "\"type\"\\s*:").Count;
            return n > 0 ? n : 1;
        }

        public static IEnumerator WaitForLogin(string exe, Action launch)
        {
            ValCraftPlugin.Log.LogWarning("[launch] Prism has no Minecraft account yet: opening Prism so you can log in (Accounts > Add Microsoft); Minecraft starts once an account exists");
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); } catch (Exception e) { ValCraftPlugin.Log.LogError("[launch] could not open Prism: " + e.Message); yield break; }
            float until = Time.realtimeSinceStartup + 15 * 60, nextMsg = 0;
            while (Time.realtimeSinceStartup < until)
            {
                if (Time.realtimeSinceStartup >= nextMsg) { nextMsg = Time.realtimeSinceStartup + 30; Show("ValCraft: log in to Minecraft in the Prism Launcher window (Accounts > Add Microsoft). Minecraft starts automatically afterwards."); }
                yield return new WaitForSecondsRealtime(3);
                if (PrismAccounts(exe) != 0) { ValCraftPlugin.Log.LogInfo("[launch] Prism account found, launching"); Show("ValCraft: Minecraft account found, starting Minecraft..."); launch(); yield break; }
            }
            ValCraftPlugin.Log.LogWarning("[launch] gave up waiting for a Prism login (15 min); restart Valheim after logging in");
        }

        static void Show(string text)
        {
            try
            {
                var t = Type.GetType("MessageHud, assembly_valheim");
                var inst = t?.GetField("m_instance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null);
                if (inst == null) return;
                foreach (var m in t.GetMethods())
                {
                    if (m.Name != "ShowMessage") continue;
                    var ps = m.GetParameters(); var a = new object[ps.Length];
                    a[0] = Enum.ToObject(ps[0].ParameterType, 2); a[1] = text; // MessageType.Center
                    for (int i = 2; i < ps.Length; i++) a[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                    m.Invoke(inst, a); return;
                }
            }
            catch { }
        }
    }
}
