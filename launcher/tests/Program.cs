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
        // ---- 0.15.1 ----
        var sroot = Path.Combine(tmp, "Steam"); var lib2 = Path.Combine(tmp, "Lib2");
        Directory.CreateDirectory(Path.Combine(sroot, "steamapps")); Directory.CreateDirectory(Path.Combine(lib2, "steamapps", "common", "Valheim"));
        File.WriteAllText(Path.Combine(sroot, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" { \"1\" { \"path\" \"" + lib2.Replace("\\", "\\\\") + "\" } }");
        var acf = Path.Combine(lib2, "steamapps", "appmanifest_892970.acf");
        File.WriteAllText(acf, "\"AppState\" { \"appid\" \"892970\" \"installdir\" \"Valheim\" \"buildid\" \"18000001\" }");
        var gi = Stores.Find(new[] { sroot }, Path.Combine(tmp, "nope"));
        Ok(gi.Store == Store.Steam && gi.Manifest == acf && gi.LaunchUri == "steam://rungameid/892970", "Steam install found via libraryfolders.vdf");
        Ok(SteamBuild.LocalBuildId(acf) == "18000001", "local buildid");
        var sj = "{\"data\":{\"892970\":{\"depots\":{\"branches\":{\"public\":{\"buildid\":\"18000555\",\"timeupdated\":\"1\"}}}}},\"status\":\"success\"}";
        Ok(SteamBuild.PublicBuildId(sj) == "18000555", "steamcmd public buildid");
        Ok(SteamBuild.Banner("18000001", "18000555")?.Contains("18000555") == true && SteamBuild.Banner("18000555", "18000555") == null && SteamBuild.Banner("19", "18") == null && SteamBuild.Banner(null, "1") == null, "update banner only when Steam is newer");
        var em = Path.Combine(tmp, "EpicManifests"); Directory.CreateDirectory(em);
        File.WriteAllText(Path.Combine(em, "abc.item"), "{\"DisplayName\":\"Valheim\",\"InstallLocation\":\"D:\\\\Epic\\\\Valheim\",\"CatalogNamespace\":\"ns1\",\"CatalogItemId\":\"item1\",\"AppName\":\"Oak\"}");
        var ge = Stores.Find(new[] { Path.Combine(tmp, "noSteam") }, em);
        Ok(ge.Store == Store.Epic && ge.Dir == "D:\\Epic\\Valheim" && ge.LaunchUri == "com.epicgames.launcher://apps/ns1%3Aitem1%3AOak?action=launch&silent=true", "Epic install + launch URI: " + ge.LaunchUri);
        Ok(Stores.Find(new string[0], Path.Combine(tmp, "x")).Store == Store.None, "no store -> None (install prompt)");
        // backup
        var vr = Path.Combine(tmp, "IronGate"); Directory.CreateDirectory(Path.Combine(vr, "worlds_local"));
        File.WriteAllText(Path.Combine(vr, "worlds_local", "Midgard.db"), "db"); File.WriteAllText(Path.Combine(vr, "worlds_local", "Midgard.fwl"), "fwl"); File.WriteAllText(Path.Combine(vr, "worlds_local", "x.txt"), "no");
        var saves = Path.Combine(tmp, "mc", "saves"); Directory.CreateDirectory(Path.Combine(saves, "ValCraft", "region"));
        File.WriteAllText(Path.Combine(saves, "ValCraft", "level.dat"), "lvl"); File.WriteAllText(Path.Combine(saves, "ValCraft", "region", "r.0.0.mca"), "mca"); File.WriteAllText(Path.Combine(saves, "ValCraft", "session.lock"), "x");
        var bp = Backup.Plan(vr, saves);
        Ok(bp.Count == 4 && bp.Any(b => b.entry == "valheim/worlds_local/Midgard.db") && bp.Any(b => b.entry == "minecraft/saves/ValCraft/region/r.0.0.mca"), "backup plan: " + string.Join(", ", bp.Select(b => b.entry)));
        var zipPath = Backup.Run(bp, Path.Combine(tmp, "backups"));
        using (var z = System.IO.Compression.ZipFile.OpenRead(zipPath)) Ok(z.Entries.Count == 4 && new StreamReader(z.GetEntry("valheim/worlds_local/Midgard.fwl").Open()).ReadToEnd() == "fwl", "backup zip written: " + Path.GetFileName(zipPath));
        Ok(Backup.McSaves(st) == Path.Combine(Path.GetDirectoryName(Prism.ModsDir(prism)), "saves"), "MC saves next to mods");
        // settings
        var pcfg = Path.Combine(tmp, "dev.valcraft.valheim.cfg");
        File.WriteAllLines(pcfg, new[] { "## Settings file", "", "[Minecraft]", "", "## Start MC", "StartWithValheim = true", "", "[Blocks]", "MaxMobs = 24", "" });
        var mcfg = Path.Combine(tmp, "config", "valcraft.properties");
        Directory.CreateDirectory(Path.GetDirectoryName(mcfg)); File.WriteAllLines(mcfg, new[] { "#ValCraft", "autoWorld=true", "muteMusic=true" });
        var set = Settings.Load(pcfg, mcfg);
        Ok(set.StartWithValheim && set.MaxMobs == 24 && set.MuteMusic && set.Swap.Values.All(v => v), "settings load defaults");
        set.Swap["creeper"] = false; set.MuteMusic = false; set.StartWithValheim = false; set.MaxMobs = 40; set.EmeraldChance = 0.01;
        set.Save(pcfg, mcfg);
        var set2 = Settings.Load(pcfg, mcfg);
        Ok(!set2.Swap["creeper"] && set2.Swap["zombie"] && !set2.MuteMusic && !set2.StartWithValheim && set2.MaxMobs == 40 && Math.Abs(set2.EmeraldChance - 0.01) < 1e-9, "settings round trip");
        var plines = File.ReadAllLines(pcfg);
        Ok(plines.Contains("## Start MC") && plines.Count(l => l.StartsWith("StartWithValheim")) == 1 && plines.Contains("[MobAudio]") && plines.Contains("Swap_creeper = false"), "BepInEx cfg edited in place (comments kept, section added)");
        Ok(File.ReadAllLines(mcfg).Contains("swapSounds.creeper=false") && File.ReadAllLines(mcfg).Contains("autoWorld=true") && CfgFile.GetProp(mcfg, "muteMusic") == "false", "valcraft.properties edited");
        Directory.Delete(tmp, true);
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED"); return fails == 0 ? 0 : 1;
    }
    static string Hash(string s) { using (var m = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(s))) return Sums.Of(m); }
}
