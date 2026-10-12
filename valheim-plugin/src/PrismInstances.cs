using System;
using System.IO;
using System.Linq;

namespace ValCraft
{
    /// <summary>
    /// Prism's --launch takes the instance ID = the instance's folder name (e.g. "1.21.1"), not the display name shown
    /// in the launcher, and on Windows a wrong ID just makes Prism open without launching. Resolve the configured name.
    /// </summary>
    internal static class PrismInstances
    {
        public static string Resolve(string prismExe, string wanted)
        {
            string dir = FindInstancesDir(prismExe);
            if (dir == null) { ValCraftPlugin.Log.LogWarning($"[launch] Prism instances folder not found (looked next to the exe and in %APPDATA%\\PrismLauncher); passing \"{wanted}\" as-is"); return wanted; }
            var inst = Directory.GetDirectories(dir).Where(d => File.Exists(Path.Combine(d, "instance.cfg"))).ToArray();
            var list = string.Join(", ", inst.Select(d => $"{Path.GetFileName(d)} (\"{DisplayName(d)}\")").ToArray());
            ValCraftPlugin.Log.LogInfo($"[launch] Prism instances in {dir}: {list}");

            string Pick(string d, string why) { var id = Path.GetFileName(d); ValCraftPlugin.Log.LogInfo($"[launch] instance \"{wanted}\" -> ID \"{id}\" ({why})"); return id; }
            foreach (var d in inst) if (Path.GetFileName(d) == wanted) return Pick(d, "folder name");
            foreach (var d in inst) if (string.Equals(Path.GetFileName(d), wanted, StringComparison.OrdinalIgnoreCase)) return Pick(d, "folder name, ignoring case");
            foreach (var d in inst) if (string.Equals(DisplayName(d), wanted, StringComparison.OrdinalIgnoreCase)) return Pick(d, "display name in instance.cfg");
            var withMod = inst.Where(d => HasValCraftJar(d)).ToArray();
            if (withMod.Length == 1) return Pick(withMod[0], "only instance with a valcraft jar in mods");
            ValCraftPlugin.Log.LogWarning($"[launch] no Prism instance matches \"{wanted}\"{(withMod.Length > 1 ? $" and {withMod.Length} have a valcraft jar" : "")}; set [Minecraft] PrismInstance to one of the IDs above");
            return withMod.Length > 0 ? Pick(withMod[0], "first instance with a valcraft jar") : null;
        }

        static string FindInstancesDir(string exe)
        {
            var candidates = new[]
            {
                Path.Combine(Path.GetDirectoryName(exe) ?? "", "instances"), // portable install
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrismLauncher", "instances"),
            };
            // prismlauncher.cfg can point InstanceDir elsewhere
            foreach (var cfgDir in new[] { Path.GetDirectoryName(exe) ?? "", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrismLauncher") })
            {
                var cfg = Path.Combine(cfgDir, "prismlauncher.cfg");
                if (!File.Exists(cfg)) continue;
                var line = File.ReadAllLines(cfg).FirstOrDefault(l => l.StartsWith("InstanceDir=", StringComparison.OrdinalIgnoreCase));
                if (line == null) continue;
                var v = line.Substring("InstanceDir=".Length).Trim();
                var p = Path.IsPathRooted(v) ? v : Path.Combine(cfgDir, v);
                if (Directory.Exists(p)) return p;
            }
            return candidates.FirstOrDefault(Directory.Exists);
        }

        static string DisplayName(string instDir)
        {
            try
            {
                var line = File.ReadAllLines(Path.Combine(instDir, "instance.cfg")).FirstOrDefault(l => l.StartsWith("name=", StringComparison.OrdinalIgnoreCase));
                return line?.Substring(5).Trim() ?? "";
            }
            catch { return ""; }
        }

        static bool HasValCraftJar(string instDir)
        {
            foreach (var sub in new[] { "minecraft", ".minecraft" })
            {
                var mods = Path.Combine(Path.Combine(instDir, sub), "mods");
                if (Directory.Exists(mods) && Directory.GetFiles(mods, "valcraft*.jar").Length > 0) return true;
            }
            return false;
        }
    }
}
