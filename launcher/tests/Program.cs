using System; using System.IO; using System.Linq; using System.Collections.Generic; using ValCraft.Launcher; using ValCraft.Installer;
static class T
{
    static int fails;
    static void Ok(bool c, string w) { Console.WriteLine((c ? "PASS " : "FAIL ") + w); if (!c) fails++; }
    static int Main(string[] a)
    {
        // JSON + release list (real API response if given)
        var j = MiniJson.Parse("{\"a\":[1,2.5,{\"b\":\"x\\n\\u00e9\\\"\"}],\"t\":true,\"n\":null}") as Dictionary<string, object>;
        Ok(((List<object>)j["a"]).Count == 3 && MiniJson.S(((List<object>)j["a"])[2], "b") == "x\né\"" && (bool)j["t"] && j["n"] == null, "MiniJson");
        string relJson = "[{\"tag_name\":\"v0.15.0\",\"name\":\"ValCraft 0.15.0\",\"draft\":false,\"prerelease\":false,\"published_at\":\"2026-10-13T07:00:00Z\",\"html_url\":\"https://github.com/lukeroundy/ValCraft/releases/tag/v0.15.0\",\"body\":\"# ValCraft 0.15.0\\n\\n## Download\\n- **x**\\n\\n## Changes\\n- New **ValCraft Launcher** with `news`.\\n- [Docs](https://x) updated.\\n- third\",\"assets\":[{\"name\":\"ValCraft.Valheim.dll\",\"browser_download_url\":\"https://d/dll\"},{\"name\":\"valcraft-0.15.0.jar\",\"browser_download_url\":\"https://d/jar\"},{\"name\":\"ValCraft-Launcher.exe\",\"browser_download_url\":\"https://d/exe\"},{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"https://d/sums\"}]},{\"tag_name\":\"v0.14.9\",\"name\":\"\",\"draft\":true,\"body\":\"\",\"assets\":[]}]";
        var rels = ReleaseInfo.ParseList(relJson);
        Ok(rels.Count == 1 && rels[0].Version == "0.15.0" && rels[0].JarName == "valcraft-0.15.0.jar" && rels[0].Asset("sha256sums.txt") == "https://d/sums", "release list parse (drafts skipped)");
        var news = News.FromReleases(rels);
        Ok(news[0].Title == "ValCraft 0.15.0" && news[0].Text == "New ValCraft Launcher with news. Docs updated." && news[0].Date == "Oct 13, 2026", "news card: " + news[0].Text);
        if (a.Length > 0) { var real = ReleaseInfo.ParseList(File.ReadAllText(a[0])); Ok(real.Count > 0 && real[0].Asset("SHA256SUMS.txt") != null && News.FromReleases(real)[0].Text.Length > 10, $"real GitHub releases: {real.Count}, latest {real[0].Tag}: {News.FromReleases(real)[0].Text}"); }

        // sums
        var sums = Sums.Parse("4068de528aad37a7730030652405c306d697bd09fdbb8dda767a2ad5f2aed44e  ValCraft.Valheim.dll\r\nABCDEF0000000000000000000000000000000000000000000000000000000001 *valcraft-0.15.0.jar\nbad line\n");
        Ok(sums.Count == 2 && sums["valcraft-0.15.0.jar"].StartsWith("abcdef"), "SHA256SUMS parse");

        // update plan against a fake install
        var tmp = Path.Combine(Path.GetTempPath(), "vcl-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmp);
        var vh = Path.Combine(tmp, "Valheim"); var prism = Path.Combine(tmp, "Prism"); Directory.CreateDirectory(Path.GetDirectoryName(Plugin.Dll(vh))); Directory.CreateDirectory(Prism.ModsDir(prism));
        File.WriteAllText(Plugin.Dll(vh), "old"); File.WriteAllText(Path.Combine(Prism.ModsDir(prism), "valcraft-0.15.0.jar"), "jar");
        var ini = Path.Combine(tmp, "installer.ini"); File.WriteAllLines(ini, new[] { "valheim=" + vh, "portable=True", "prismRoot=" + prism, "version=0.14.3" });
        var st = InstallState.Load(ini);
        Ok(st.ModsDir == Prism.ModsDir(prism) && st.PluginDll == Plugin.Dll(vh) && st.Version == "0.14.3", "installer.ini read");
        var lexe = Path.Combine(tmp, "ValCraft-Launcher.exe"); File.WriteAllText(lexe, "me");
        var s2 = new Dictionary<string, string> { ["ValCraft.Valheim.dll"] = Sums.Of(Plugin.Dll(vh)) == "x" ? "" : new string('0', 64), ["valcraft-0.15.0.jar"] = Hash("jar"), ["ValCraft-Launcher.exe"] = new string('1', 64) };
        var plan = UpdatePlan.Build(rels[0], s2, st, lexe, "0.14.9");
        Ok(plan.Select(p => p.Kind).SequenceEqual(new[] { ItemKind.Plugin, ItemKind.Launcher }), "plan: plugin differs, jar matches, launcher newer -> " + string.Join(",", plan.Select(p => p.Kind)));
        Ok(!UpdatePlan.Build(rels[0], s2, st, lexe, "0.15.0").Any(p => p.Kind == ItemKind.Launcher), "launcher not replaced by same version");
        Ok(UpdatePlan.Build(rels[0], s2, null, lexe, "0.15.0").Count == 0, "not installed -> nothing to do");
        InstallState.SetVersion(ini, "0.15.0"); Ok(InstallState.Load(ini).Version == "0.15.0" && File.ReadAllLines(ini).Count(l => l.StartsWith("version=")) == 1, "version written back");

        // progress
        var pr = new Progress { Total = 10_000_000 }; pr.Update(0, 0); pr.Update(1_000_000, 1); pr.Update(2_000_000, 2);
        Ok(Math.Abs(pr.Speed - 1_000_000) < 1 && pr.Remaining.Value.TotalSeconds > 7.9 && pr.Remaining.Value.TotalSeconds < 8.1 && Progress.FormatSpeed(pr.Speed) == "976.6 KB/s" && Progress.FormatRemaining(pr.Remaining) == "8 s", $"progress: {Progress.FormatSpeed(pr.Speed)}, {Progress.FormatRemaining(pr.Remaining)}");
        Ok(Progress.FormatRemaining(TimeSpan.FromSeconds(130)) == "3 min" && Progress.FormatSpeed(0) == "0.00 B/s", "format");
        var sc = SelfUpdate.Script(@"C:\a\L.exe", @"C:\t\n.exe", 42);
        Ok(sc.Contains("PID eq 42") && sc.Contains("move /y \"C:\\t\\n.exe\" \"C:\\a\\L.exe\"") && sc.Contains("start \"\" \"C:\\a\\L.exe\""), "self-update script");
        Ok(LauncherInfo.Guides.Length == 5 && LauncherInfo.Guides.All(g => g.url.StartsWith("https://")) && LauncherInfo.SteamLaunchUri == "steam://rungameid/892970", "guide links + steam uri");

        // launcher install paths (installer side)
        var lad = Path.Combine(tmp, "Local"); var exe = LauncherInstall.Install(new byte[] { 1, 2 }, null, lad);
        Ok(exe == Path.Combine(lad, "Programs", "ValCraft", "ValCraft-Launcher.exe") && File.ReadAllBytes(exe).Length == 2, "launcher installed to LocalAppData\\Programs\\ValCraft");
        LauncherInstall.Install(new byte[] { 3 }, null, lad); Ok(File.ReadAllBytes(exe)[0] == 3 && !File.Exists(exe + ".bak"), "launcher reinstall");
        var pd = Path.Combine(tmp, "Programs"); var dd = Path.Combine(tmp, "Desktop"); Directory.CreateDirectory(pd); Directory.CreateDirectory(dd);
        File.WriteAllText(LauncherInstall.StartMenuLink(pd), "l"); File.WriteAllText(LauncherInstall.DesktopLink(dd), "l");
        LauncherInstall.Uninstall(null, lad, pd, dd);
        Ok(!File.Exists(exe) && !File.Exists(LauncherInstall.StartMenuLink(pd)) && !File.Exists(LauncherInstall.DesktopLink(dd)), "launcher uninstall removes exe + shortcuts");
        Directory.Delete(tmp, true);
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED"); return fails == 0 ? 0 : 1;
    }
    static string Hash(string s) { using (var m = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(s))) return Sums.Of(m); }
}
