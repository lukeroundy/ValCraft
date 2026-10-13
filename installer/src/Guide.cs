namespace ValCraft.Installer
{
    /// <summary>"Use my own launcher" instructions (shown in the wizard and written as ValCraft-Minecraft-Setup.html).</summary>
    public static class Guide
    {
        public static string Html(string valheimDir)
        {
            string cfg = valheimDir == null ? "BepInEx\\config\\" + Versions.PluginGuid + ".cfg" : Plugin.ConfigFile(valheimDir);
            return $@"<!doctype html><html><head><meta charset=""utf-8""><title>ValCraft {Versions.ValCraft} - Minecraft setup</title>
<style>body{{font-family:Segoe UI,Arial,sans-serif;max-width:860px;margin:2em auto;line-height:1.5}}code,pre{{background:#f2f2f2;padding:2px 4px}}pre{{padding:8px;overflow:auto}}h2{{border-bottom:1px solid #ccc}}</style></head><body>
<h1>ValCraft {Versions.ValCraft}: Minecraft side</h1>
<p>ValCraft needs <b>Minecraft {Versions.Minecraft}</b> with <b>NeoForge {Versions.NeoForge}</b> (21.1.251 or newer also loads) and <code>{Versions.JarName}</code> in the instance's <code>mods</code> folder. Java 21 is required; Prism and ATLauncher download it automatically.</p>
<p>Valheim starts Minecraft for you through the settings in <code>{cfg}</code>, section <code>[Minecraft]</code>:</p>
<pre>StartWithValheim = true
Launcher = Prism | ATLauncher | Custom
PrismExe = full path of the launcher exe
InstanceName = ValCraft
ExtraArgs =</pre>
<h2>Prism Launcher</h2>
<ol><li>Add Instance &rarr; Minecraft {Versions.Minecraft}, Mod loader NeoForge {Versions.NeoForge}. Name it <b>ValCraft</b>.</li>
<li>Right-click the instance &rarr; Folder &rarr; open <code>minecraft\mods</code> (create it if missing) and copy <code>{Versions.JarName}</code> into it.</li>
<li>Config: <code>Launcher = Prism</code>, <code>PrismExe = C:\...\prismlauncher.exe</code>, <code>InstanceName = ValCraft</code> (folder name or display name). Valheim runs <code>prismlauncher.exe --launch ValCraft</code>.</li></ol>
<h2>ATLauncher</h2>
<ol><li>Create Pack &rarr; Minecraft {Versions.Minecraft}, loader NeoForge {Versions.NeoForge}, name <b>ValCraft</b>.</li>
<li>Instances &rarr; ValCraft &rarr; Open Folder &rarr; <code>mods</code>: copy <code>{Versions.JarName}</code> in.</li>
<li>Config: <code>Launcher = ATLauncher</code>, <code>PrismExe = C:\...\ATLauncher.exe</code>, <code>InstanceName = ValCraft</code>. Valheim runs <code>ATLauncher.exe --launch ""ValCraft"" --close-launcher</code> (ATLauncher's documented <code>--launch</code> option; name or folder name).</li></ol>
<h2>CurseForge app</h2>
<p>CurseForge has <b>no supported command line</b> for launching a profile, so Valheim cannot start it. Create a custom profile (Minecraft {Versions.Minecraft}, NeoForge {Versions.NeoForge}), open its folder and copy the jar into <code>mods</code>, set <code>StartWithValheim = false</code>, and start Minecraft yourself after Valheim is running (it connects within a few seconds). Alternatively import the same setup into Prism, which can launch automatically.</p>
<h2>Official Minecraft launcher</h2>
<p>Run the NeoForge {Versions.NeoForge} installer (neoforged.net) &rarr; ""Install client"", copy the jar to <code>%APPDATA%\.minecraft\mods</code>, set <code>StartWithValheim = false</code> and start Minecraft yourself. <code>Launcher = Custom</code> with any exe plus <code>ExtraArgs</code> (<code>{{instance}}</code> is replaced) works for other launchers that have a command line.</p>
<h2>Order</h2><p>Start Valheim first (it creates the link), load a world; Minecraft connects automatically. Updates: run the newest installer; configs and item mappings in <code>BepInEx\config</code> are kept.</p>
</body></html>";
        }
    }
}
