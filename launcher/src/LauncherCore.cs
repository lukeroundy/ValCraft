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
        public const string Version = "0.15.0";
        public const string ExeName = "ValCraft-Launcher.exe";
        public const string Pages = "https://lukeroundy.github.io/ValCraft/";
        public static string Repo => Versions.GitHubRepo;
        public static string ReleasesApi => $"https://api.github.com/repos/{Repo}/releases?per_page=6";
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
        public static string Script(string current, string downloaded, int pid) =>
            "@echo off\r\n" +
            $":wait\r\ntasklist /FI \"PID eq {pid}\" | find \"{pid}\" >nul && (ping -n 2 127.0.0.1 >nul & goto wait)\r\n" +
            $"move /y \"{downloaded}\" \"{current}\" >nul\r\n" +
            $"start \"\" \"{current}\"\r\n" +
            "del \"%~f0\"\r\n";
    }
}
