using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ValCraft.Installer;

namespace ValCraft.Launcher
{
    public static class LauncherInfo
    {
        public const string Version = "0.15.1";
        public const string ExeName = "ValCraft-Launcher.exe";
        public const string Pages = "https://lukeroundy.github.io/ValCraft/";
        public static string Repo => Versions.GitHubRepo;
        /// <summary>0.15.1: VALCRAFT_RELEASES_API (or --api) overrides the releases endpoint, e.g. for a mock release server.</summary>
        public static string ApiOverride;
        public static string ReleasesApi => !string.IsNullOrEmpty(ApiOverride) ? ApiOverride : !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VALCRAFT_RELEASES_API")) ? Environment.GetEnvironmentVariable("VALCRAFT_RELEASES_API") : $"https://api.github.com/repos/{Repo}/releases?per_page=6";
        public const string SteamInstallUri = "steam://install/892970", EpicStoreUrl = "https://store.epicgames.com/p/valheim", SteamCmdInfo = "https://api.steamcmd.net/v1/info/892970";
        public static string LatestApi => $"https://api.github.com/repos/{Repo}/releases/latest";
        public static string SteamLaunchUri => "steam://rungameid/" + Versions.ValheimAppId;
        public static readonly (string label, string url)[] Guides =
        {
            ("Getting Started", Pages + "getting-started.html"),
            ("Commands", Pages + "commands.html"),
            ("FAQ", Pages + "faq.html"),
            ("Report a Bug", $"https://github.com/{Versions.GitHubRepo}/issues/new"),
            ("GitHub", $"https://github.com/{Versions.GitHubRepo}"),
        };
        public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ValCraft");
    }

    /// <summary>Tiny JSON reader (objects -> Dictionary, arrays -> List, numbers -> double).</summary>
    public static class MiniJson
    {
        public static object Parse(string s) { int i = 0; var v = Value(s, ref i); return v; }
        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>(); i++; Ws(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i); var k = Str(s, ref i); Ws(s, ref i);
                    if (s[i++] != ':') throw new FormatException(": expected");
                    d[k] = Value(s, ref i); Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw new FormatException(", or } expected");
                }
            }
            if (c == '[')
            {
                var l = new List<object>(); i++; Ws(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i)); Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return l; }
                    throw new FormatException(", or ] expected");
                }
            }
            if (c == '"') return Str(s, ref i);
            if (s.Substring(i).StartsWith("true")) { i += 4; return true; }
            if (s.Substring(i).StartsWith("false")) { i += 5; return false; }
            if (s.Substring(i).StartsWith("null")) { i += 4; return null; }
            int st = i; while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (st == i) throw new FormatException("bad value at " + i);
            return double.Parse(s.Substring(st, i - st), CultureInfo.InvariantCulture);
        }
        static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("string expected");
            i++; var sb = new StringBuilder();
            while (s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break; case 'r': sb.Append('\r'); break; case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break; case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            i++;
            return sb.ToString();
        }
        public static string S(object o, string k) => o is Dictionary<string, object> d && d.TryGetValue(k, out var v) ? v as string : null;
    }

    public sealed class ReleaseInfo
    {
        public string Tag, Name, Body, Published, Url;
        public bool Prerelease, Draft;
        public readonly Dictionary<string, string> Assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // name -> download url
        public string Version => Tag?.TrimStart('v', 'V');
        public string Asset(string name) => Assets.TryGetValue(name, out var u) ? u : null;
        public string JarName => Assets.Keys.FirstOrDefault(n => Regex.IsMatch(n, "^valcraft-[\\d.]+\\.jar$", RegexOptions.IgnoreCase));

        public static ReleaseInfo From(object o)
        {
            var r = new ReleaseInfo { Tag = MiniJson.S(o, "tag_name"), Name = MiniJson.S(o, "name"), Body = MiniJson.S(o, "body") ?? "", Published = MiniJson.S(o, "published_at"), Url = MiniJson.S(o, "html_url") };
            var d = (Dictionary<string, object>)o;
            r.Prerelease = d.TryGetValue("prerelease", out var p) && p is bool b && b; r.Draft = d.TryGetValue("draft", out var dr) && dr is bool b2 && b2;
            if (d.TryGetValue("assets", out var a) && a is List<object> list)
                foreach (var x in list) { var n = MiniJson.S(x, "name"); var u = MiniJson.S(x, "browser_download_url"); if (n != null && u != null) r.Assets[n] = u; }
            return r;
        }
        public static ReleaseInfo ParseOne(string json) => From(MiniJson.Parse(json));
        public static List<ReleaseInfo> ParseList(string json) => ((List<object>)MiniJson.Parse(json)).Select(From).Where(r => !r.Draft).ToList();
    }

    public sealed class NewsCard { public string Title, Text, Date, Url; }

    public static class News
    {
        /// <summary>One card per release: title from the release name, text = the first change bullets (markdown stripped).</summary>
        public static List<NewsCard> FromReleases(IEnumerable<ReleaseInfo> rels)
        {
            var o = new List<NewsCard>();
            foreach (var r in rels)
            {
                var lines = r.Body.Replace("\r", "").Split('\n');
                int start = Array.FindIndex(lines, l => Regex.IsMatch(l, "^#+\\s*Changes", RegexOptions.IgnoreCase));
                var bullets = lines.Skip(start < 0 ? 0 : start + 1).Where(l => l.TrimStart().StartsWith("- ")).Select(l => Plain(l.TrimStart().Substring(2))).Where(l => l.Length > 0).Take(2).ToList();
                var text = string.Join(" ", bullets);
                if (text.Length > 150) text = text.Substring(0, 147).TrimEnd() + "...";
                DateTime dt; string date = DateTime.TryParse(r.Published, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out dt) ? dt.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) : "";
                o.Add(new NewsCard { Title = string.IsNullOrEmpty(r.Name) ? "ValCraft " + r.Version : r.Name, Text = text, Date = date, Url = r.Url });
            }
            return o;
        }
        public static string Plain(string md)
        {
            var s = Regex.Replace(md, "\\[([^\\]]+)\\]\\([^)]+\\)", "$1");
            s = s.Replace("**", "").Replace("`", "").Replace("*", "");
            return s.Trim();
        }
    }

    public static class Sums
    {
        /// <summary>sha256sum format: "&lt;hex&gt;  &lt;name&gt;" (or " *name").</summary>
        public static Dictionary<string, string> Parse(string text)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in (text ?? "").Replace("\r", "").Split('\n'))
            {
                var m = Regex.Match(l.Trim(), "^([0-9a-fA-F]{64})\\s+\\*?(.+)$");
                if (m.Success) d[m.Groups[2].Value.Trim()] = m.Groups[1].Value.ToLowerInvariant();
            }
            return d;
        }
        public static string Of(string file) { using (var s = File.OpenRead(file)) return Of(s); }
        public static string Of(Stream s) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant(); }
    }

    /// <summary>Where ValCraft is installed: written by the installer (%APPDATA%\ValCraft\installer.ini).</summary>
    public sealed class InstallState
    {
        public string Valheim, PrismRoot, Mods, Version; public bool Portable = true;
        public string ModsDir => Portable ? (string.IsNullOrEmpty(PrismRoot) ? null : Prism.ModsDir(PrismRoot)) : (string.IsNullOrEmpty(Mods) ? null : Mods);
        public string PluginDll => string.IsNullOrEmpty(Valheim) ? null : Plugin.Dll(Valheim);
        public static string DefaultFile => Path.Combine(LauncherInfo.DataDir, "installer.ini");
        public static InstallState Load(string file = null)
        {
            file = file ?? DefaultFile;
            if (!File.Exists(file)) return null;
            var d = File.ReadAllLines(file).Where(l => l.Contains("=")).ToDictionary(l => l.Substring(0, l.IndexOf('=')), l => l.Substring(l.IndexOf('=') + 1));
            string G(string k) => d.TryGetValue(k, out var x) ? x : "";
            return new InstallState { Valheim = G("valheim"), PrismRoot = G("prismRoot"), Mods = G("mods"), Version = G("version"), Portable = G("portable") != "False" };
        }
        public static void SetVersion(string file, string version)
        {
            file = file ?? DefaultFile;
            if (!File.Exists(file)) return;
            var lines = File.ReadAllLines(file).Where(l => !l.StartsWith("version=")).ToList(); lines.Add("version=" + version);
            File.WriteAllLines(file, lines);
        }
    }

    public enum ItemKind { Plugin, Jar, Launcher }
    public sealed class UpdateItem { public ItemKind Kind; public string Name, Url, Sha, Target; }

    public static class UpdatePlan
    {
        /// <summary>What has to be downloaded: plugin / jar when the installed file's hash differs from SHA256SUMS; the launcher
        /// only when the release is newer than this launcher (dev builds never downgrade themselves).</summary>
        public static List<UpdateItem> Build(ReleaseInfo rel, Dictionary<string, string> sums, InstallState st, string launcherExe, string launcherVersion)
        {
            var o = new List<UpdateItem>();
            if (rel == null || sums == null) return o;
            if (st != null && st.PluginDll != null && rel.Asset("ValCraft.Valheim.dll") != null && sums.TryGetValue("ValCraft.Valheim.dll", out var ps)
                && (!File.Exists(st.PluginDll) || Sums.Of(st.PluginDll) != ps))
                o.Add(new UpdateItem { Kind = ItemKind.Plugin, Name = "ValCraft.Valheim.dll", Url = rel.Asset("ValCraft.Valheim.dll"), Sha = ps, Target = st.PluginDll });
            var jar = rel.JarName;
            if (st != null && st.ModsDir != null && jar != null && sums.TryGetValue(jar, out var js))
            {
                var t = Path.Combine(st.ModsDir, jar);
                if (!File.Exists(t) || Sums.Of(t) != js) o.Add(new UpdateItem { Kind = ItemKind.Jar, Name = jar, Url = rel.Asset(jar), Sha = js, Target = t });
            }
            if (launcherExe != null && rel.Asset(LauncherInfo.ExeName) != null && sums.TryGetValue(LauncherInfo.ExeName, out var ls)
                && Versions.Compare(rel.Version, launcherVersion) > 0 && (!File.Exists(launcherExe) || Sums.Of(launcherExe) != ls))
                o.Add(new UpdateItem { Kind = ItemKind.Launcher, Name = LauncherInfo.ExeName, Url = rel.Asset(LauncherInfo.ExeName), Sha = ls, Target = launcherExe });
            return o;
        }
    }

    /// <summary>Download speed (smoothed) and time remaining.</summary>
    public sealed class Progress
    {
        public long Total, Done; double _speed; double _lastT = -1; long _lastDone;
        public double Speed => _speed;
        public void Update(long done, double tSec)
        {
            if (_lastT < 0) { _lastT = tSec; _lastDone = done; Done = done; return; }
            double dt = tSec - _lastT;
            if (dt >= 0.25) { double inst = (done - _lastDone) / dt; _speed = _speed <= 0 ? inst : _speed * 0.6 + inst * 0.4; _lastT = tSec; _lastDone = done; }
            Done = done;
        }
        public double Fraction => Total > 0 ? Math.Min(1.0, (double)Done / Total) : 0;
        public TimeSpan? Remaining => _speed > 1 && Total > 0 ? TimeSpan.FromSeconds((Total - Done) / _speed) : (TimeSpan?)null;
        public static string FormatSpeed(double bps) => bps >= 1 << 20 ? (bps / (1 << 20)).ToString("0.00", CultureInfo.InvariantCulture) + " MB/s" : bps >= 1024 ? (bps / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " KB/s" : bps.ToString("0.00", CultureInfo.InvariantCulture) + " B/s";
        public static string FormatRemaining(TimeSpan? t) => t == null ? "0 min" : t.Value.TotalSeconds < 60 ? Math.Ceiling(t.Value.TotalSeconds) + " s" : Math.Ceiling(t.Value.TotalMinutes) + " min";
    }

    /// <summary>Self-update: the running exe can't overwrite itself; a short cmd script swaps the files after we exit.</summary>
    public static class SelfUpdate
    {
        public static string Script(string current, string downloaded, int pid, string args = "") =>
            "@echo off\r\n" +
            $":wait\r\ntasklist /FI \"PID eq {pid}\" | find \"{pid}\" >nul && (ping -n 2 127.0.0.1 >nul & goto wait)\r\n" +
            $"move /y \"{downloaded}\" \"{current}\" >nul\r\n" +
            $"start \"\" \"{current}\"{(string.IsNullOrEmpty(args) ? "" : " " + args)}\r\n" +
            "del \"%~f0\"\r\n";
    }
}


namespace ValCraft.Launcher
{
    using System.IO.Compression;
    using System.Text.RegularExpressions;

    // ---------------- 0.15.1: store detection (Steam / Epic) ----------------
    public enum Store { None, Steam, Epic }
    public sealed class GameInstall
    {
        public Store Store; public string Dir, Manifest, EpicNamespace, EpicItemId, EpicAppName;
        /// <summary>URI that launches Valheim through its store.</summary>
        public string LaunchUri => Store == Store.Epic ? $"com.epicgames.launcher://apps/{Uri.EscapeDataString(EpicNamespace ?? "")}%3A{Uri.EscapeDataString(EpicItemId ?? "")}%3A{Uri.EscapeDataString(EpicAppName ?? "")}?action=launch&silent=true" : LauncherInfo.SteamLaunchUri;
    }
    public static class Stores
    {
        /// <summary>Steam first (appmanifest_892970.acf in any library), then Epic (ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item).</summary>
        public static GameInstall Find(IEnumerable<string> steamRoots, string epicManifestDir)
        {
            foreach (var root in steamRoots.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var lib in ValCraft.Installer.SteamLocator.Libraries(root))
                {
                    var acf = Path.Combine(lib, "steamapps", "appmanifest_892970.acf");
                    if (!File.Exists(acf)) continue;
                    var m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"([^\"]+)\"");
                    var dir = Path.Combine(lib, "steamapps", "common", m.Success ? m.Groups[1].Value : "Valheim");
                    return new GameInstall { Store = Store.Steam, Dir = dir, Manifest = acf };
                }
            var epic = FindEpic(epicManifestDir);
            return epic ?? new GameInstall { Store = Store.None };
        }
        public static GameInstall FindEpic(string manifestDir)
        {
            try
            {
                if (string.IsNullOrEmpty(manifestDir) || !Directory.Exists(manifestDir)) return null;
                foreach (var f in Directory.GetFiles(manifestDir, "*.item"))
                {
                    var o = MiniJson.Parse(File.ReadAllText(f)) as Dictionary<string, object>;
                    if (o == null) continue;
                    string name = MiniJson.S(o, "DisplayName") ?? "", loc = MiniJson.S(o, "InstallLocation");
                    if (name.IndexOf("Valheim", StringComparison.OrdinalIgnoreCase) < 0 && (loc == null || !File.Exists(Path.Combine(loc, "valheim.exe")))) continue;
                    return new GameInstall { Store = Store.Epic, Dir = loc, Manifest = f, EpicNamespace = MiniJson.S(o, "CatalogNamespace"), EpicItemId = MiniJson.S(o, "CatalogItemId"), EpicAppName = MiniJson.S(o, "AppName") };
                }
            }
            catch { }
            return null;
        }
        public static string DefaultEpicManifests => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
    }

    // ---------------- 0.15.1: Valheim update warning (local appmanifest buildid vs Steam public branch) ----------------
    public static class SteamBuild
    {
        public static string LocalBuildId(string acf)
        {
            try { var m = Regex.Match(File.ReadAllText(acf), "\"buildid\"\\s+\"(\\d+)\""); return m.Success ? m.Groups[1].Value : null; } catch { return null; }
        }
        /// <summary>api.steamcmd.net/v1/info/892970 -> data.892970.depots.branches.public.buildid</summary>
        public static string PublicBuildId(string json)
        {
            try
            {
                var o = MiniJson.Parse(json) as Dictionary<string, object>;
                var d = (o?["data"] as Dictionary<string, object>)?["892970"] as Dictionary<string, object>;
                var br = ((d?["depots"] as Dictionary<string, object>)?["branches"] as Dictionary<string, object>)?["public"] as Dictionary<string, object>;
                return br == null ? null : MiniJson.S(br, "buildid");
            }
            catch { return null; }
        }
        public static string Banner(string local, string pub)
        {
            if (string.IsNullOrEmpty(local) || string.IsNullOrEmpty(pub) || local == pub) return null;
            long l, p; if (long.TryParse(local, out l) && long.TryParse(pub, out p) && l > p) return null;
            return $"Valheim update on Steam: build {pub} (you have {local}). After it installs, ValCraft may need an update too.";
        }
    }

    // ---------------- 0.15.1: world backup ----------------
    public static class Backup
    {
        public static string ValheimWorlds => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "IronGate", "Valheim");
        public static string Dir => Path.Combine(LauncherInfo.DataDir, "backups");
        public static string Marker => Path.Combine(LauncherInfo.DataDir, "backup-done.txt");
        public static bool Done => File.Exists(Marker);
        public static string McSaves(InstallState st) => st?.ModsDir == null ? null : Path.Combine(Path.GetDirectoryName(st.ModsDir.TrimEnd('\\', '/')), "saves");

        /// <summary>Files to back up: Valheim worlds_local/worlds (*.db, *.fwl) and every Minecraft world folder in saves.</summary>
        public static List<(string file, string entry)> Plan(string valheimRoot, string mcSaves)
        {
            var l = new List<(string, string)>();
            if (!string.IsNullOrEmpty(valheimRoot))
                foreach (var sub in new[] { "worlds_local", "worlds" })
                {
                    var d = Path.Combine(valheimRoot, sub); if (!Directory.Exists(d)) continue;
                    foreach (var f in Directory.GetFiles(d))
                        if (f.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".fwl", StringComparison.OrdinalIgnoreCase)) l.Add((f, "valheim/" + sub + "/" + Path.GetFileName(f)));
                }
            if (!string.IsNullOrEmpty(mcSaves) && Directory.Exists(mcSaves))
                foreach (var w in Directory.GetDirectories(mcSaves))
                    foreach (var f in Directory.GetFiles(w, "*", SearchOption.AllDirectories))
                    {
                        if (Path.GetFileName(f) == "session.lock") continue;
                        l.Add((f, "minecraft/saves/" + f.Substring(mcSaves.Length).TrimStart('\\', '/').Replace('\\', '/')));
                    }
            return l;
        }
        /// <summary>Writes backup-yyyyMMdd-HHmmss.zip into outDir; returns its path. Files in use are copied via a shared read.</summary>
        public static string Run(List<(string file, string entry)> plan, string outDir, Action<int, int> progress = null)
        {
            Directory.CreateDirectory(outDir);
            var zip = Path.Combine(outDir, "backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
            using (var fs = File.Create(zip))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                int i = 0;
                foreach (var (file, entry) in plan)
                {
                    try
                    {
                        using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var dst = z.CreateEntry(entry, CompressionLevel.Optimal).Open()) src.CopyTo(dst);
                    }
                    catch (IOException) { }
                    progress?.Invoke(++i, plan.Count);
                }
            }
            try { Directory.CreateDirectory(Path.GetDirectoryName(Marker)); File.WriteAllText(Marker, zip); } catch { }
            return zip;
        }
    }

    // ---------------- 0.15.1: settings (plugin BepInEx cfg + mod valcraft.properties) ----------------
    public static class CfgFile
    {
        /// <summary>BepInEx .cfg: set "Key = value" in [Section], adding the section/key if missing; keeps comments.</summary>
        public static void SetBep(string path, string section, string key, string value)
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            int sec = lines.FindIndex(l => l.Trim() == "[" + section + "]");
            if (sec < 0) { if (lines.Count > 0 && lines[lines.Count - 1].Trim() != "") lines.Add(""); lines.Add("[" + section + "]"); lines.Add(""); lines.Add($"{key} = {value}"); }
            else
            {
                int end = lines.FindIndex(sec + 1, l => l.TrimStart().StartsWith("[")); if (end < 0) end = lines.Count;
                int k = -1; for (int i = sec + 1; i < end; i++) if (Regex.IsMatch(lines[i], "^\\s*" + Regex.Escape(key) + "\\s*=")) { k = i; break; }
                if (k >= 0) lines[k] = $"{key} = {value}"; else { int at = end; while (at > sec + 1 && lines[at - 1].Trim() == "") at--; lines.Insert(at, $"{key} = {value}"); }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllLines(path, lines);
        }
        public static string GetBep(string path, string section, string key)
        {
            if (!File.Exists(path)) return null; bool inSec = false;
            foreach (var l in File.ReadAllLines(path))
            {
                var t = l.Trim();
                if (t.StartsWith("[")) { inSec = t == "[" + section + "]"; continue; }
                if (!inSec) continue;
                var m = Regex.Match(t, "^" + Regex.Escape(key) + "\\s*=\\s*(.*)$"); if (m.Success) return m.Groups[1].Value.Trim();
            }
            return null;
        }
        /// <summary>Java .properties key=value (keys we write are plain ASCII).</summary>
        public static void SetProp(string path, string key, string value)
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            int k = lines.FindIndex(l => Regex.IsMatch(l, "^\\s*" + Regex.Escape(key) + "\\s*[=:]"));
            if (k >= 0) lines[k] = key + "=" + value; else lines.Add(key + "=" + value);
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllLines(path, lines);
        }
        public static string GetProp(string path, string key)
        {
            if (!File.Exists(path)) return null;
            foreach (var l in File.ReadAllLines(path)) { var m = Regex.Match(l, "^\\s*" + Regex.Escape(key) + "\\s*[=:]\\s*(.*)$"); if (m.Success) return m.Groups[1].Value.Trim(); }
            return null;
        }
    }
    public sealed class Settings
    {
        public static readonly string[] Families = { "zombie", "skeleton", "creeper", "spider", "enderman", "slime", "illager", "animal", "villager", "other" };
        public Dictionary<string, bool> Swap = Families.ToDictionary(f => f, f => true);
        public bool MuteMusic = true, StartWithValheim = true, CrossGameCombat = true; public int MaxMobs = 24; public double EmeraldChance = 0.005;
        public static string PluginCfg(InstallState st) => st?.Valheim == null ? null : Path.Combine(st.Valheim, "BepInEx", "config", "dev.valcraft.valheim.cfg");
        public static string ModCfg(InstallState st) => st?.ModsDir == null ? null : Path.Combine(Path.GetDirectoryName(st.ModsDir.TrimEnd('\\', '/')), "config", "valcraft.properties");
        static bool B(string s, bool d) => s == null ? d : s.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        public static Settings Load(string pluginCfg, string modCfg)
        {
            var s = new Settings();
            if (pluginCfg != null)
            {
                foreach (var f in Families) s.Swap[f] = B(CfgFile.GetBep(pluginCfg, "MobAudio", "Swap_" + f), B(modCfg == null ? null : CfgFile.GetProp(modCfg, "swapSounds." + f), true));
                s.StartWithValheim = B(CfgFile.GetBep(pluginCfg, "Minecraft", "StartWithValheim"), true);
                s.CrossGameCombat = B(CfgFile.GetBep(pluginCfg, "Mobs", "CrossGameCombat"), true);
                int mm; if (int.TryParse(CfgFile.GetBep(pluginCfg, "Blocks", "MaxMobs"), out mm)) s.MaxMobs = mm;
            }
            if (modCfg != null)
            {
                s.MuteMusic = B(CfgFile.GetProp(modCfg, "muteMusic"), true);
                double e; if (double.TryParse(CfgFile.GetProp(modCfg, "emeraldChance"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out e)) s.EmeraldChance = e;
            }
            return s;
        }
        public void Save(string pluginCfg, string modCfg)
        {
            string L(bool b) => b ? "true" : "false";
            if (pluginCfg != null)
            {
                foreach (var f in Families) CfgFile.SetBep(pluginCfg, "MobAudio", "Swap_" + f, L(Swap[f]));
                CfgFile.SetBep(pluginCfg, "Minecraft", "StartWithValheim", L(StartWithValheim));
                CfgFile.SetBep(pluginCfg, "Mobs", "CrossGameCombat", L(CrossGameCombat));
                CfgFile.SetBep(pluginCfg, "Blocks", "MaxMobs", MaxMobs.ToString());
            }
            if (modCfg != null)
            {
                foreach (var f in Families) CfgFile.SetProp(modCfg, "swapSounds." + f, L(Swap[f]));
                CfgFile.SetProp(modCfg, "muteMusic", L(MuteMusic));
                CfgFile.SetProp(modCfg, "emeraldChance", EmeraldChance.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }
}
