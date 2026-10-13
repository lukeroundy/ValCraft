using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ValCraft.Installer
{
    public static class Versions
    {
        public const string ValCraft = "0.15.3";
        public const string Minecraft = "1.21.1";
        /// <summary>NeoForge build the mod is compiled against (user confirmed 21.1.259; 21.1.251+ also loads).</summary>
        public const string NeoForge = "21.1.259";
        public const string PluginGuid = "dev.valcraft.valheim";
        public const string ValheimAppId = "892970";
        /// <summary>GitHub repo for updates ("owner/name"). TBD: the repo does not exist yet; update checks are skipped while this is a placeholder.</summary>
        public const string GitHubRepo = "lukeroundy/ValCraft";
        public const string ThunderstoreBepInEx = "https://thunderstore.io/api/experimental/package/denikson/BepInExPack_Valheim/";
        public const string PrismReleases = "https://api.github.com/repos/PrismLauncher/PrismLauncher/releases/latest";
        public const string InstanceName = "ValCraft";
        public const int MaxMemMb = 4096;
        public static string JarName => $"valcraft-{ValCraft}.jar";
        public static bool RepoConfigured => !GitHubRepo.StartsWith("OWNER/");

        /// <summary>Compare dotted versions ("v0.14.10" > "0.14.9"). Returns &lt;0, 0, &gt;0.</summary>
        public static int Compare(string a, string b)
        {
            int[] P(string s) => Regex.Matches(s ?? "", "\\d+").Cast<Match>().Select(m => int.Parse(m.Value)).ToArray();
            var x = P(a); var y = P(b);
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                int d = (i < x.Length ? x[i] : 0) - (i < y.Length ? y[i] : 0);
                if (d != 0) return d;
            }
            return 0;
        }
    }

    /// <summary>Steam library discovery: SteamPath from the registry (passed in), libraryfolders.vdf, appmanifest_892970.acf.</summary>
    public static class SteamLocator
    {
        /// <summary>All library roots listed in steamapps/libraryfolders.vdf (new "path" format and the old "1" "D:\\..." format), plus the Steam root.</summary>
        public static List<string> Libraries(string steamRoot)
        {
            var libs = new List<string>();
            if (string.IsNullOrEmpty(steamRoot)) return libs;
            libs.Add(Norm(steamRoot));
            var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"(?:path|\\d+)\"\\s+\"([^\"]+)\""))
                {
                    var p = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (p.Length >= 2 && (p[1] == ':' || p.StartsWith("/") || p.StartsWith("\\\\"))) { p = Norm(p); if (!libs.Contains(p, StringComparer.OrdinalIgnoreCase)) libs.Add(p); }
                }
            }
            return libs;
        }

        /// <summary>Valheim folders found in the given Steam roots (deduplicated), manifest-backed first.</summary>
        public static List<string> FindValheim(IEnumerable<string> steamRoots)
        {
            var found = new List<string>();
            foreach (var root in steamRoots.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var lib in Libraries(root))
                {
                    string apps = Path.Combine(lib, "steamapps");
                    string dir = Path.Combine(apps, "common", "Valheim");
                    var acf = Path.Combine(apps, $"appmanifest_{Versions.ValheimAppId}.acf");
                    if (File.Exists(acf))
                    {
                        var m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"([^\"]+)\"");
                        if (m.Success) dir = Path.Combine(apps, "common", m.Groups[1].Value);
                    }
                    if (IsValheim(dir) && !found.Contains(dir, StringComparer.OrdinalIgnoreCase)) found.Add(dir);
                }
            return found;
        }

        public static bool IsValheim(string dir) => !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "valheim.exe"));
        static string Norm(string p) => p.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
    }

    public static class BepInExCheck
    {
        public static string[] Missing(string valheim)
        {
            var need = new[] { Path.Combine("BepInEx", "core", "BepInEx.dll"), "winhttp.dll", "doorstop_config.ini" };
            return need.Where(n => !File.Exists(Path.Combine(valheim, n))).ToArray();
        }

        /// <summary>Extract a Thunderstore BepInExPack_Valheim zip: the contents of its BepInExPack_Valheim/ folder go into the Valheim dir. Existing configs are kept.</summary>
        public static int ExtractPack(string zip, string valheim, Action<string> log)
        {
            int n = 0;
            using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
                foreach (var e in z.Entries)
                {
                    var name = e.FullName.Replace('\\', '/');
                    const string pre = "BepInExPack_Valheim/";
                    if (!name.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) continue;
                    var rel = name.Substring(pre.Length);
                    if (rel.Length == 0 || rel.Contains("..")) continue;
                    var dst = Path.Combine(valheim, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (name.EndsWith("/")) { Directory.CreateDirectory(dst); continue; }
                    if (rel.StartsWith("BepInEx/config/", StringComparison.OrdinalIgnoreCase) && File.Exists(dst)) continue; // keep the user's configs
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    using (var s = e.Open()) using (var f = File.Create(dst)) s.CopyTo(f);
                    n++;
                }
            log?.Invoke($"BepInEx: {n} files extracted");
            return n;
        }
    }

    public static class Plugin
    {
        public static string Dir(string valheim) => Path.Combine(valheim, "BepInEx", "plugins", "ValCraft");
        public static string Dll(string valheim) => Path.Combine(Dir(valheim), "ValCraft.Valheim.dll");
        public static string ConfigFile(string valheim) => Path.Combine(valheim, "BepInEx", "config", Versions.PluginGuid + ".cfg");

        /// <summary>Install/replace the DLL. Old DLL -> .bak; a stray copy directly in plugins/ (manual installs) is moved aside so it does not load twice.
        /// BepInEx/config (ValCraft cfg, valcraft-items.json, anchors, beds) is never touched.</summary>
        public static void Install(string valheim, byte[] dll, Action<string> log)
        {
            Directory.CreateDirectory(Dir(valheim));
            var dst = Dll(valheim);
            if (File.Exists(dst)) { File.Copy(dst, dst + ".bak", true); log?.Invoke("previous ValCraft.Valheim.dll backed up (.bak)"); }
            var stray = Path.Combine(valheim, "BepInEx", "plugins", "ValCraft.Valheim.dll");
            if (File.Exists(stray)) { File.Copy(stray, dst + ".old-root.bak", true); File.Delete(stray); log?.Invoke("moved old BepInEx/plugins/ValCraft.Valheim.dll aside"); }
            File.WriteAllBytes(dst, dll);
            log?.Invoke("plugin installed: " + dst);
        }

        public static void Uninstall(string valheim, bool removeConfig, Action<string> log)
        {
            if (Directory.Exists(Dir(valheim))) { Directory.Delete(Dir(valheim), true); log?.Invoke("removed " + Dir(valheim)); }
            if (removeConfig)
                foreach (var f in new[] { ConfigFile(valheim) }.Concat(Directory.Exists(Path.Combine(valheim, "BepInEx", "config")) ? Directory.GetFiles(Path.Combine(valheim, "BepInEx", "config"), "valcraft-*") : new string[0]))
                    if (File.Exists(f)) { File.Delete(f); log?.Invoke("removed " + f); }
        }

        /// <summary>Installed plugin version (from the DLL's file version), or null.</summary>
        public static string InstalledVersion(string valheim)
        {
            try { return File.Exists(Dll(valheim)) ? System.Diagnostics.FileVersionInfo.GetVersionInfo(Dll(valheim)).FileVersion : null; } catch { return null; }
        }
    }

    /// <summary>Minimal BepInEx .cfg editor: sets "Key = value" in [Section], keeps everything else (comments, other keys).</summary>
    public static class BepCfg
    {
        public static string Set(string text, string section, string key, string value)
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n').ToList();
            if (lines.Count == 1 && lines[0] == "") lines.Clear();
            int sec = lines.FindIndex(l => l.Trim().Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase));
            if (sec < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim() != "") lines.Add("");
                lines.Add("[" + section + "]"); lines.Add(""); lines.Add($"{key} = {value}"); lines.Add("");
                return string.Join("\r\n", lines);
            }
            int end = lines.FindIndex(sec + 1, l => l.TrimStart().StartsWith("["));
            if (end < 0) end = lines.Count;
            for (int i = sec + 1; i < end; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("#")) continue;
                int eq = t.IndexOf('=');
                if (eq > 0 && t.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) { lines[i] = $"{key} = {value}"; return string.Join("\r\n", lines); }
            }
            int at = end; while (at > sec + 1 && lines[at - 1].Trim() == "") at--;
            lines.Insert(at, $"{key} = {value}");
            return string.Join("\r\n", lines);
        }

        public static string Get(string text, string section, string key)
        {
            bool inSec = false;
            foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                var t = raw.Trim();
                if (t.StartsWith("[")) { inSec = t.Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!inSec || t.StartsWith("#")) continue;
                int eq = t.IndexOf('=');
                if (eq > 0 && t.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return t.Substring(eq + 1).Trim();
            }
            return null;
        }

        public static void Apply(string file, IDictionary<string, string> minecraftKeys)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string text = File.Exists(file) ? File.ReadAllText(file) : $"## Settings file was created by the ValCraft installer\r\n## Plugin GUID: {Versions.PluginGuid}\r\n";
            foreach (var kv in minecraftKeys) text = Set(text, "Minecraft", kv.Key, kv.Value);
            File.WriteAllText(file, text);
        }
    }

    /// <summary>Portable Prism Launcher + the ValCraft instance (Prism's own on-disk format).</summary>
    public static class Prism
    {
        public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ValCraft", "PrismLauncher");
        public static string Exe(string root) => Path.Combine(root, "prismlauncher.exe");
        public static string InstanceDir(string root) => Path.Combine(root, "instances", Versions.InstanceName);
        public static string ModsDir(string root) => Path.Combine(InstanceDir(root), "minecraft", "mods");

        public static string MmcPack() =>
            "{\n    \"components\": [\n" +
            "        {\n            \"cachedName\": \"Minecraft\",\n            \"important\": true,\n            \"uid\": \"net.minecraft\",\n            \"version\": \"" + Versions.Minecraft + "\"\n        },\n" +
            "        {\n            \"cachedName\": \"NeoForge\",\n            \"uid\": \"net.neoforged\",\n            \"version\": \"" + Versions.NeoForge + "\"\n        }\n" +
            "    ],\n    \"formatVersion\": 1\n}\n";

        public static string InstanceCfg(string existing)
        {
            var keys = new Dictionary<string, string>
            {
                ["ConfigVersion"] = "1.2", ["InstanceType"] = "OneSix", ["name"] = Versions.InstanceName, ["iconKey"] = "default",
                ["OverrideMemory"] = "true", ["MaxMemAlloc"] = Versions.MaxMemMb.ToString(), ["MinMemAlloc"] = "1024",
                ["AutomaticJava"] = "true", ["notes"] = "Created by the ValCraft installer. Launched automatically by Valheim.",
            };
            return MergeIni(existing, "General", keys, keepExisting: new[] { "MaxMemAlloc", "MinMemAlloc", "notes", "iconKey" });
        }

        /// <summary>prismlauncher.cfg: Java auto-download/switch (Prism fetches Java 21 for 1.21.1), skip the Java wizard.</summary>
        public static string LauncherCfg(string existing) => MergeIni(existing, "General", new Dictionary<string, string>
        {
            ["AutomaticJavaDownload"] = "true", ["AutomaticJavaSwitch"] = "true", ["UserAskedAboutAutomaticJavaDownload"] = "true", ["IgnoreJavaWizard"] = "true",
        }, new string[0]);

        static string MergeIni(string existing, string section, Dictionary<string, string> keys, string[] keepExisting)
        {
            string t = existing ?? "";
            foreach (var kv in keys)
            {
                if (keepExisting.Contains(kv.Key) && BepCfg.Get(t, section, kv.Key) != null) continue;
                t = BepCfg.Set(t, section, kv.Key, kv.Value).Replace($"{kv.Key} = {kv.Value}", $"{kv.Key}={kv.Value}");
            }
            return t.Replace("\r\n", "\n");
        }

        /// <summary>Create/refresh the instance; keeps the user's minecraft/ folder (worlds, configs, other mods).</summary>
        public static void WriteInstance(string root, byte[] jar, Action<string> log)
        {
            var inst = InstanceDir(root);
            Directory.CreateDirectory(ModsDir(root));
            File.WriteAllText(Path.Combine(inst, "mmc-pack.json"), MmcPack());
            var cfg = Path.Combine(inst, "instance.cfg");
            File.WriteAllText(cfg, InstanceCfg(File.Exists(cfg) ? File.ReadAllText(cfg) : null));
            var lcfg = Path.Combine(root, "prismlauncher.cfg");
            File.WriteAllText(lcfg, LauncherCfg(File.Exists(lcfg) ? File.ReadAllText(lcfg) : null));
            if (!File.Exists(Path.Combine(root, "portable.txt"))) File.WriteAllText(Path.Combine(root, "portable.txt"), "");
            var opt = Path.Combine(inst, "minecraft", "options.txt");
            File.WriteAllText(opt, McOptions(File.Exists(opt) ? File.ReadAllText(opt) : null));
            Mods.InstallJar(ModsDir(root), jar, log);
            log?.Invoke("Prism instance ready: " + inst);
        }

        /// <summary>0.14.3: Minecraft options.txt defaults for ValCraft (no accessibility welcome screen, narrator off, music off);
        /// only keys that are absent are added, the user's values are never overwritten.</summary>
        public static string McOptions(string existing)
        {
            var lines = (existing ?? "").Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToList();
            foreach (var kv in new[] { ("onboardAccessibility", "false"), ("narrator", "0"), ("soundCategory_music", "0.0") })
                if (!lines.Any(l => l.StartsWith(kv.Item1 + ":"))) lines.Add(kv.Item1 + ":" + kv.Item2);
            return string.Join("\n", lines) + "\n";
        }

        public static int Accounts(string root)
        {
            var f = Path.Combine(root, "accounts.json");
            if (!File.Exists(f)) return 0;
            var m = Regex.Match(File.ReadAllText(f), "\"accounts\"\\s*:\\s*\\[(.*)\\]", RegexOptions.Singleline);
            if (!m.Success || m.Groups[1].Value.Trim().Length == 0) return 0;
            return Math.Max(1, Regex.Matches(m.Groups[1].Value, "\"type\"\\s*:").Count);
        }

        /// <summary>The asset to download from Prism's latest GitHub release (x64 MSVC portable zip).</summary>
        public static string PickPortableAsset(string releaseJson)
        {
            var urls = Regex.Matches(releaseJson, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            return urls.FirstOrDefault(u => Regex.IsMatch(u, "Windows-MSVC-Portable-[\\d.]+\\.zip$"))
                ?? urls.FirstOrDefault(u => Regex.IsMatch(u, "Windows-MinGW-w64-Portable-[\\d.]+\\.zip$"));
        }
    }

    /// <summary>0.15.0: the ValCraft Launcher, per-user (no admin): %LOCALAPPDATA%\Programs\ValCraft\ValCraft-Launcher.exe,
    /// Start Menu entry always, desktop shortcut optional.</summary>
    public static class LauncherInstall
    {
        public const string ExeName = "ValCraft-Launcher.exe";
        public static string Dir(string localAppData = null) => Path.Combine(localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ValCraft");
        public static string Exe(string localAppData = null) => Path.Combine(Dir(localAppData), ExeName);
        public static string StartMenuLink(string programsDir = null) => Path.Combine(programsDir ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ValCraft Launcher.lnk");
        public static string DesktopLink(string desktopDir = null) => Path.Combine(desktopDir ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ValCraft Launcher.lnk");

        /// <summary>Write the launcher exe (old one kept as .bak while replacing). Returns the exe path.</summary>
        public static string Install(byte[] exe, Action<string> log, string localAppData = null)
        {
            var dst = Exe(localAppData);
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            if (File.Exists(dst)) File.Copy(dst, dst + ".bak", true);
            File.WriteAllBytes(dst, exe);
            if (File.Exists(dst + ".bak")) File.Delete(dst + ".bak");
            log?.Invoke("launcher installed: " + dst);
            return dst;
        }

        public static void Uninstall(Action<string> log, string localAppData = null, string programsDir = null, string desktopDir = null)
        {
            foreach (var l in new[] { StartMenuLink(programsDir), DesktopLink(desktopDir) }) if (File.Exists(l)) { File.Delete(l); log?.Invoke("removed " + l); }
            var d = Dir(localAppData);
            if (File.Exists(Path.Combine(d, ExeName))) { File.Delete(Path.Combine(d, ExeName)); log?.Invoke("removed " + Path.Combine(d, ExeName)); }
            try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
        }

        /// <summary>Create a .lnk through the Windows Script Host (COM, Windows only).</summary>
        public static bool CreateShortcut(string lnk, string target, string description)
        {
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return false;
                dynamic shell = Activator.CreateInstance(t);
                dynamic sc = shell.CreateShortcut(lnk);
                sc.TargetPath = target; sc.WorkingDirectory = Path.GetDirectoryName(target); sc.Description = description; sc.IconLocation = target + ",0";
                sc.Save();
                return true;
            }
            catch { return false; }
        }
    }

    public static class Mods
    {
        /// <summary>Put valcraft-X.jar into a mods folder; older valcraft-*.jar are renamed *.jar.disabled (NeoForge ignores them).</summary>
        public static void InstallJar(string mods, byte[] jar, Action<string> log)
        {
            Directory.CreateDirectory(mods);
            foreach (var old in Directory.GetFiles(mods, "valcraft-*.jar"))
                if (!Path.GetFileName(old).Equals(Versions.JarName, StringComparison.OrdinalIgnoreCase))
                {
                    var dis = old + ".disabled"; if (File.Exists(dis)) File.Delete(dis);
                    File.Move(old, dis); log?.Invoke("disabled old " + Path.GetFileName(old));
                }
            File.WriteAllBytes(Path.Combine(mods, Versions.JarName), jar);
            log?.Invoke($"{Versions.JarName} -> {mods}");
        }

        public static void RemoveJar(string mods, Action<string> log)
        {
            if (!Directory.Exists(mods)) return;
            foreach (var f in Directory.GetFiles(mods, "valcraft-*.jar")) { File.Delete(f); log?.Invoke("removed " + f); }
        }
    }

    public static class Thunderstore
    {
        public static (string version, string url) Latest(string json)
        {
            var i = json.IndexOf("\"latest\"", StringComparison.Ordinal);
            var s = i >= 0 ? json.Substring(i) : json;
            var v = Regex.Match(s, "\"version_number\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
            var u = Regex.Match(s, "\"download_url\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
            if (u == "" && v != "") u = $"https://thunderstore.io/package/download/denikson/BepInExPack_Valheim/{v}/";
            return (v, u);
        }
    }

    /// <summary>ValCraft updates from GitHub Releases: tag vX.Y.Z with assets ValCraft.Valheim.dll, valcraft-X.Y.Z.jar, ValCraft-Installer-X.Y.Z.exe.</summary>
    public static class Updates
    {
        public static string LatestUrl => $"https://api.github.com/repos/{Versions.GitHubRepo}/releases/latest";
        public sealed class Release { public string Tag, InstallerUrl, DllUrl, JarUrl, NotesUrl; }
        public static Release Parse(string json)
        {
            var r = new Release { Tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value, NotesUrl = Regex.Match(json, "\"html_url\"\\s*:\\s*\"([^\"]+/releases/tag/[^\"]+)\"").Groups[1].Value };
            foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
            {
                var u = m.Groups[1].Value; var n = u.Substring(u.LastIndexOf('/') + 1);
                if (Regex.IsMatch(n, "^ValCraft-Installer-.*\\.exe$", RegexOptions.IgnoreCase)) r.InstallerUrl = u;
                else if (n.Equals("ValCraft.Valheim.dll", StringComparison.OrdinalIgnoreCase)) r.DllUrl = u;
                else if (Regex.IsMatch(n, "^valcraft-[\\d.]+\\.jar$", RegexOptions.IgnoreCase)) r.JarUrl = u;
            }
            return r;
        }
        public static bool IsNewer(Release r) => r != null && r.Tag != "" && Versions.Compare(r.Tag, Versions.ValCraft) > 0;
    }

    /// <summary>Post-install link check from the logs of the last run.</summary>
    public static class LinkCheck
    {
        public static List<string> Run(string valheim, string mcGameDir)
        {
            var o = new List<string>();
            var vlog = Path.Combine(valheim, "BepInEx", "LogOutput.log");
            if (!File.Exists(vlog)) o.Add("Valheim: no BepInEx log yet - start Valheim once (BepInEx creates BepInEx/LogOutput.log).");
            else
            {
                var t = Read(vlog);
                o.Add(t.Contains("[link] created") ? "Valheim: ValCraft loaded and created the link." : t.Contains("ValCraft") ? "Valheim: ValCraft mentioned in the log but no link was created - see BepInEx/LogOutput.log." : "Valheim: BepInEx ran but ValCraft did not load.");
                if (t.Contains("[launch] started")) o.Add("Valheim: Minecraft launch command was run.");
                if (t.Contains("[launch] PrismExe not found")) o.Add("Valheim: the launcher path in the config does not exist.");
            }
            if (!string.IsNullOrEmpty(mcGameDir))
            {
                var mlog = Path.Combine(mcGameDir, "logs", "latest.log");
                if (!File.Exists(mlog)) o.Add("Minecraft: no logs/latest.log yet.");
                else
                {
                    var t = Read(mlog);
                    o.Add(t.Contains("ValCraft loaded") ? "Minecraft: ValCraft mod loaded." : "Minecraft: the ValCraft mod did not load (is it in mods/ and is NeoForge " + Versions.NeoForge + " selected?).");
                    if (t.Contains("[link] connected to Valheim")) o.Add("Minecraft: connected to Valheim - the link works.");
                    else if (t.Contains("ValCraft loaded")) o.Add("Minecraft: not connected to Valheim in the last run (start Valheim first, then Minecraft).");
                }
            }
            return o;
        }
        static string Read(string f) { using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (var r = new StreamReader(s)) return r.ReadToEnd(); }
    }
}
