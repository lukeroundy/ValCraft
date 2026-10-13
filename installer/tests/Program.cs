using System; using System.IO; using System.Linq; using System.Text.Json; using ValCraft.Installer;
static class T
{
    static int fails;
    static void Ok(bool c, string what) { Console.WriteLine((c ? "PASS " : "FAIL ") + what); if (!c) fails++; }
    static int Main(string[] a)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "vcinst-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmp);
        // fake Steam: root library without Valheim, second library (new vdf format) with renamed installdir, third (old format)
        var steam = Path.Combine(tmp, "Steam"); var lib2 = Path.Combine(tmp, "Games", "SteamLibrary"); var lib3 = Path.Combine(tmp, "Old Lib");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + steam.Replace("\\", "\\\\") + "\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"228980\"\t\t\"1\"\n\t\t}\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"" + lib2 + "\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"892970\"\t\t\"123\"\n\t\t}\n\t}\n}\n\"2\"\t\"" + lib3 + "\"\n");
        var vh = Path.Combine(lib2, "steamapps", "common", "Valheim Game"); Directory.CreateDirectory(vh); File.WriteAllText(Path.Combine(vh, "valheim.exe"), "");
        File.WriteAllText(Path.Combine(lib2, "steamapps", "appmanifest_892970.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"892970\"\n\t\"installdir\"\t\t\"Valheim Game\"\n}\n");
        var vh3 = Path.Combine(lib3, "steamapps", "common", "Valheim"); Directory.CreateDirectory(vh3); File.WriteAllText(Path.Combine(vh3, "valheim.exe"), "");
        var libs = SteamLocator.Libraries(steam);
        Ok(libs.Count == 3, $"libraryfolders.vdf: 3 libraries ({string.Join(" | ", libs)})");
        var found = SteamLocator.FindValheim(new[] { steam, null, steam });
        Ok(found.Count == 2 && found[0] == vh && found[1] == vh3, $"Valheim found via appmanifest installdir + old vdf format ({string.Join(" | ", found)})");
        Ok(SteamLocator.FindValheim(new[] { Path.Combine(tmp, "nope") }).Count == 0, "missing Steam -> none");

        // BepInEx
        Ok(BepInExCheck.Missing(vh).Length == 3, "BepInEx missing detected");
        var pack = a.Length > 0 ? a[0] : null;
        if (pack != null && File.Exists(pack))
        {
            Directory.CreateDirectory(Path.Combine(vh, "BepInEx", "config")); File.WriteAllText(Path.Combine(vh, "BepInEx", "config", "BepInEx.cfg"), "MINE");
            int n = BepInExCheck.ExtractPack(pack, vh, Console.WriteLine);
            Ok(n > 5 && BepInExCheck.Missing(vh).Length == 0, "real BepInExPack_Valheim zip extracted, BepInEx complete");
            Ok(File.ReadAllText(Path.Combine(vh, "BepInEx", "config", "BepInEx.cfg")) == "MINE", "existing BepInEx config kept");
        }

        // plugin install / update / uninstall
        Directory.CreateDirectory(Path.Combine(vh, "BepInEx", "plugins")); File.WriteAllText(Path.Combine(vh, "BepInEx", "plugins", "ValCraft.Valheim.dll"), "stray");
        File.WriteAllText(Path.Combine(vh, "BepInEx", "config", "valcraft-items.json"), "{\"map\":1}");
        Plugin.Install(vh, new byte[] { 1 }, null); Plugin.Install(vh, new byte[] { 2 }, null);
        Ok(File.ReadAllBytes(Plugin.Dll(vh))[0] == 2 && File.ReadAllBytes(Plugin.Dll(vh) + ".bak")[0] == 1 && !File.Exists(Path.Combine(vh, "BepInEx", "plugins", "ValCraft.Valheim.dll")), "plugin installed to plugins/ValCraft, old -> .bak, stray root copy moved");

        // cfg editing
        var cfgText = "## Settings file was created by plugin ValCraft\n\n[General]\n\nX = 1\n\n[Minecraft]\n\n## Full path\n# Setting type: String\n# Default value: \nPrismExe = \n\n## inst\nInstanceName = Old\n\n[Z]\nPrismExe = keep\n";
        var t2 = BepCfg.Set(BepCfg.Set(BepCfg.Set(cfgText, "Minecraft", "PrismExe", @"C:\P\prismlauncher.exe"), "Minecraft", "Launcher", "Prism"), "Minecraft", "InstanceName", "ValCraft");
        Ok(BepCfg.Get(t2, "Minecraft", "PrismExe") == @"C:\P\prismlauncher.exe" && BepCfg.Get(t2, "Minecraft", "Launcher") == "Prism" && BepCfg.Get(t2, "Minecraft", "InstanceName") == "ValCraft" && BepCfg.Get(t2, "Z", "PrismExe") == "keep" && BepCfg.Get(t2, "General", "X") == "1" && t2.Contains("## Full path"), "BepInEx cfg: keys set in [Minecraft], comments + other sections kept");
        var t3 = BepCfg.Set("", "Minecraft", "PrismExe", "a");
        Ok(BepCfg.Get(t3, "Minecraft", "PrismExe") == "a", "cfg created from scratch");
        BepCfg.Apply(Plugin.ConfigFile(vh), new System.Collections.Generic.Dictionary<string, string> { ["PrismExe"] = "x", ["StartWithValheim"] = "true" });
        Ok(BepCfg.Get(File.ReadAllText(Plugin.ConfigFile(vh)), "Minecraft", "StartWithValheim") == "true", "cfg file written: " + Plugin.ConfigFile(vh));

        // Prism instance
        var root = Path.Combine(tmp, "Docs", "ValCraft", "PrismLauncher"); Directory.CreateDirectory(Prism.ModsDir(root));
        File.WriteAllText(Path.Combine(Prism.ModsDir(root), "valcraft-0.13.5.jar"), "old"); File.WriteAllText(Path.Combine(Prism.ModsDir(root), "create.jar"), "c");
        File.WriteAllText(Path.Combine(Prism.InstanceDir(root), "instance.cfg"), "[General]\nMaxMemAlloc=8192\nname=ValCraft\n");
        Prism.WriteInstance(root, new byte[] { 9 }, null);
        var pk = JsonDocument.Parse(File.ReadAllText(Path.Combine(Prism.InstanceDir(root), "mmc-pack.json"))).RootElement;
        var comps = pk.GetProperty("components").EnumerateArray().ToList();
        Ok(pk.GetProperty("formatVersion").GetInt32() == 1 && comps[0].GetProperty("uid").GetString() == "net.minecraft" && comps[0].GetProperty("version").GetString() == "1.21.1" && comps[1].GetProperty("uid").GetString() == "net.neoforged" && comps[1].GetProperty("version").GetString() == "21.1.259", "mmc-pack.json valid JSON, minecraft 1.21.1 + neoforge 21.1.259");
        var ic = File.ReadAllText(Path.Combine(Prism.InstanceDir(root), "instance.cfg"));
        Ok(ic.Contains("InstanceType=OneSix") && ic.Contains("MaxMemAlloc=8192") && ic.Contains("OverrideMemory=true") && !ic.Contains("MaxMemAlloc=4096"), "instance.cfg: OneSix, user's memory kept");
        Ok(File.Exists(Path.Combine(root, "portable.txt")) && File.ReadAllText(Path.Combine(root, "prismlauncher.cfg")).Contains("AutomaticJavaDownload=true"), "portable.txt + Java auto-download");
        Ok(File.Exists(Path.Combine(Prism.ModsDir(root), Versions.JarName)) && File.Exists(Path.Combine(Prism.ModsDir(root), "valcraft-0.13.5.jar.disabled")) && File.Exists(Path.Combine(Prism.ModsDir(root), "create.jar")), "jar installed, old valcraft jar disabled, other mods kept");
        Ok(Prism.Accounts(root) == 0, "no accounts.json -> 0");
        File.WriteAllText(Path.Combine(root, "accounts.json"), "{\"accounts\": [], \"formatVersion\": 3}"); Ok(Prism.Accounts(root) == 0, "empty accounts -> 0");
        File.WriteAllText(Path.Combine(root, "accounts.json"), "{\"accounts\": [{\"type\": \"MSA\", \"profile\": {\"name\": \"x\"}}], \"formatVersion\": 3}"); Ok(Prism.Accounts(root) == 1, "one MSA account");

        // remote JSON parsing (real API responses if given)
        if (a.Length > 1) Ok(Thunderstore.Latest(File.ReadAllText(a[1])).url.StartsWith("https://thunderstore.io/package/download/denikson/BepInExPack_Valheim/"), "Thunderstore latest: " + Thunderstore.Latest(File.ReadAllText(a[1])).version);
        if (a.Length > 2) { var u = Prism.PickPortableAsset(File.ReadAllText(a[2])); Ok(u != null && u.Contains("Windows-MSVC-Portable") && !u.Contains("arm64"), "Prism asset: " + u); }
        var rel = Updates.Parse("{\"tag_name\":\"v0.14.2\",\"html_url\":\"https://github.com/o/ValCraft/releases/tag/v0.14.2\",\"assets\":[{\"browser_download_url\":\"https://github.com/o/ValCraft/releases/download/v0.14.2/ValCraft-Installer-0.14.2.exe\"},{\"browser_download_url\":\"https://github.com/o/ValCraft/releases/download/v0.14.2/ValCraft.Valheim.dll\"},{\"browser_download_url\":\"https://github.com/o/ValCraft/releases/download/v0.14.2/valcraft-0.14.2.jar\"}]}");
        Ok(Updates.IsNewer(rel) && rel.InstallerUrl.EndsWith(".exe") && rel.DllUrl != null && rel.JarUrl != null, "GitHub release parsing + newer");
        Ok(Versions.Compare("v0.14.10", "0.14.9") > 0 && Versions.Compare("0.14.1.0", "0.14.1") == 0, "version compare");
        Ok(Guide.Html(vh).Contains("21.1.259") && Guide.Html(null).Contains("--launch"), "guide html");

        Plugin.Uninstall(vh, false, null);
        Ok(!Directory.Exists(Plugin.Dir(vh)) && File.Exists(Path.Combine(vh, "BepInEx", "config", "valcraft-items.json")), "uninstall keeps config/item mappings");
        Plugin.Uninstall(vh, true, null);
        Ok(!File.Exists(Path.Combine(vh, "BepInEx", "config", "valcraft-items.json")) && !File.Exists(Plugin.ConfigFile(vh)), "uninstall + remove config");
        Directory.Delete(tmp, true);
        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        return fails == 0 ? 0 : 1;
    }
}
