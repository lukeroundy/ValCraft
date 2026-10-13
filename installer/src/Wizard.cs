using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ValCraft.Installer
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Wizard());
        }
    }

    sealed class Wizard : Form
    {
        readonly TextBox _valheim = new TextBox(), _prismRoot = new TextBox(), _ownExe = new TextBox(), _ownMods = new TextBox(), _instance = new TextBox(), _log = new TextBox();
        readonly RadioButton _portable = new RadioButton(), _own = new RadioButton();
        readonly CheckBox _desktop = new CheckBox { Text = "Create a desktop shortcut for the ValCraft Launcher (Start Menu entry is always added)", Checked = true };
        readonly ComboBox _launcher = new ComboBox();
        readonly Label _status = new Label();
        readonly List<Button> _buttons = new List<Button>();
        static string StateFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ValCraft", "installer.ini");

        public Wizard()
        {
            Text = $"ValCraft {Versions.ValCraft} Installer";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            ClientSize = new Size(760, 626); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; Font = new Font("Segoe UI", 9f);
            int y = 10;
            Add(new Label { Text = $"ValCraft {Versions.ValCraft}: Valheim plugin + Minecraft {Versions.Minecraft} / NeoForge {Versions.NeoForge} mod", Left = 12, Top = y, Width = 730, Font = new Font("Segoe UI", 11f, FontStyle.Bold) }); y += 32;
            Add(new Label { Text = "Valheim folder:", Left = 12, Top = y + 3, Width = 100 });
            _valheim.SetBounds(115, y, 470, 23); Add(_valheim); Btn("Browse...", 590, y, 75, BrowseValheim); Btn("Detect", 670, y, 75, Detect); y += 30;
            _status.SetBounds(115, y, 630, 36); Add(_status); y += 40;

            var g = new GroupBox { Text = "Minecraft", Left = 12, Top = y, Width = 735, Height = 170 }; Add(g);
            _portable.Text = "Install a portable Prism Launcher for ValCraft (recommended; downloads Java 21 automatically)"; _portable.SetBounds(10, 20, 700, 22); _portable.Checked = true; g.Controls.Add(_portable);
            g.Controls.Add(new Label { Text = "Folder:", Left = 30, Top = 48, Width = 60 }); _prismRoot.SetBounds(95, 45, 540, 23); _prismRoot.Text = Prism.DefaultRoot; g.Controls.Add(_prismRoot);
            _own.Text = "Use my own launcher:"; _own.SetBounds(10, 78, 150, 22); g.Controls.Add(_own);
            _launcher.DropDownStyle = ComboBoxStyle.DropDownList; _launcher.Items.AddRange(new object[] { "Prism", "ATLauncher", "CurseForge (manual start)", "Other / official (manual start)" }); _launcher.SelectedIndex = 0; _launcher.SetBounds(165, 77, 200, 23); g.Controls.Add(_launcher);
            g.Controls.Add(new Label { Text = "Instance:", Left = 380, Top = 80, Width = 60 }); _instance.SetBounds(445, 77, 190, 23); _instance.Text = Versions.InstanceName; g.Controls.Add(_instance);
            g.Controls.Add(new Label { Text = "Launcher exe:", Left = 30, Top = 110, Width = 90 }); _ownExe.SetBounds(125, 107, 510, 23); g.Controls.Add(_ownExe);
            var b1 = new Button { Text = "...", Left = 640, Top = 106, Width = 40 }; b1.Click += (s, e) => PickFile(_ownExe); g.Controls.Add(b1);
            g.Controls.Add(new Label { Text = "Mods folder:", Left = 30, Top = 140, Width = 90 }); _ownMods.SetBounds(125, 137, 510, 23); g.Controls.Add(_ownMods);
            var b2 = new Button { Text = "...", Left = 640, Top = 136, Width = 40 }; b2.Click += (s, e) => PickFolder(_ownMods); g.Controls.Add(b2);
            y += 178;
            _desktop.SetBounds(14, y, 500, 22); Add(_desktop); y += 26;

            Btn("Install / Repair", 12, y, 120, () => Run(Install));
            Btn("Uninstall", 137, y, 90, () => Run(Uninstall));
            Btn("Log in now", 232, y, 95, LoginNow);
            Btn("Check link", 332, y, 90, CheckLink);
            Btn("Check for updates", 427, y, 125, () => Run(CheckUpdates));
            Btn("Setup guide", 557, y, 90, () => OpenGuide(true));
            Btn("Close", 652, y, 95, Close);
            y += 36;
            _log.Multiline = true; _log.ReadOnly = true; _log.ScrollBars = ScrollBars.Vertical; _log.SetBounds(12, y, 735, ClientSize.Height - y - 12); Add(_log);

            LoadState();
            if (_valheim.Text == "") Detect(); else Refresh2();
            _valheim.TextChanged += (s, e) => Refresh2();
        }

        void Add(Control c) => Controls.Add(c);
        void Btn(string t, int x, int y, int w, Action a) { var b = new Button { Text = t, Left = x, Top = y, Width = w, Height = 28 }; b.Click += (s, e) => a(); Controls.Add(b); _buttons.Add(b); }
        void Log(string s) { if (InvokeRequired) { BeginInvoke(new Action(() => Log(s))); return; } _log.AppendText(s + Environment.NewLine); }
        bool Ask(string q, string title = "ValCraft") => (bool)Invoke(new Func<bool>(() => MessageBox.Show(this, q, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes));
        void Tell(string q, MessageBoxIcon icon = MessageBoxIcon.Information) => Invoke(new Action(() => MessageBox.Show(this, q, "ValCraft", MessageBoxButtons.OK, icon)));

        void Run(Action work)
        {
            foreach (var b in _buttons) b.Enabled = false;
            new Thread(() =>
            {
                try { work(); }
                catch (UnauthorizedAccessException e) { Log("ERROR: " + e.Message); Tell("Access denied. Close Valheim/Minecraft, or run the installer as administrator.\n\n" + e.Message, MessageBoxIcon.Error); }
                catch (Exception e) { Log("ERROR: " + e); Tell(e.Message, MessageBoxIcon.Error); }
                finally { BeginInvoke(new Action(() => { foreach (var b in _buttons) b.Enabled = true; Refresh2(); })); }
            }) { IsBackground = true }.Start();
        }

        // ---------------- Valheim discovery ----------------
        static IEnumerable<string> SteamRoots()
        {
            string R(RegistryKey hive, string path, string name) { try { using (var k = hive.OpenSubKey(path)) return k?.GetValue(name) as string; } catch { return null; } }
            yield return R(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            yield return R(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            yield return R(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        }

        void Detect()
        {
            var found = SteamLocator.FindValheim(SteamRoots().Where(r => r != null).Select(r => r.Replace('/', '\\')));
            if (found.Count > 0) { _valheim.Text = found[0]; Log("Valheim found: " + found[0] + (found.Count > 1 ? $" (+{found.Count - 1} more)" : "")); }
            else Log("Valheim not found in Steam libraries - use Browse.");
            Refresh2();
        }

        void BrowseValheim()
        {
            using (var d = new FolderBrowserDialog { Description = "Select the Valheim folder (contains valheim.exe)" })
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    if (!SteamLocator.IsValheim(d.SelectedPath)) MessageBox.Show(this, "valheim.exe is not in that folder.", "ValCraft");
                    else _valheim.Text = d.SelectedPath;
                }
        }
        void PickFile(TextBox t) { using (var d = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe" }) if (d.ShowDialog(this) == DialogResult.OK) t.Text = d.FileName; }
        void PickFolder(TextBox t) { using (var d = new FolderBrowserDialog { Description = "Select the instance's mods folder" }) if (d.ShowDialog(this) == DialogResult.OK) t.Text = d.SelectedPath; }

        void Refresh2()
        {
            var v = _valheim.Text.Trim();
            if (!SteamLocator.IsValheim(v)) { _status.Text = "valheim.exe not found here."; _status.ForeColor = Color.DarkRed; return; }
            var miss = BepInExCheck.Missing(v); var inst = Plugin.InstalledVersion(v);
            _status.ForeColor = Color.DarkGreen;
            _status.Text = (miss.Length == 0 ? "BepInEx installed." : "BepInEx missing (will be installed).") + "  " +
                (inst == null ? "ValCraft not installed." : $"ValCraft {inst} installed{(Versions.Compare(inst, Versions.ValCraft) < 0 ? $" -> will update to {Versions.ValCraft}" : Versions.Compare(inst, Versions.ValCraft) > 0 ? " (newer than this installer!)" : " (repair)")}.");
        }

        // ---------------- install ----------------
        static byte[] Res(string name) { using (var s = typeof(Wizard).Assembly.GetManifestResourceStream(name)) using (var m = new MemoryStream()) { s.CopyTo(m); return m.ToArray(); } }
        static WebClient Web() { var w = new WebClient(); w.Headers[HttpRequestHeader.UserAgent] = "ValCraft-Installer/" + Versions.ValCraft; return w; }

        void Install()
        {
            string v = (string)Invoke(new Func<string>(() => _valheim.Text.Trim()));
            if (!SteamLocator.IsValheim(v)) { Tell("Select the Valheim folder first (it must contain valheim.exe)."); return; }
            if (Process.GetProcessesByName("valheim").Length > 0 && !Ask("Valheim is running. Files may be locked. Continue anyway?")) return;
            var inst = Plugin.InstalledVersion(v);
            if (inst != null && Versions.Compare(inst, Versions.ValCraft) > 0 && !Ask($"ValCraft {inst} is newer than this installer ({Versions.ValCraft}). Downgrade?")) return;

            // 1) BepInEx
            var miss = BepInExCheck.Missing(v);
            if (miss.Length > 0)
            {
                if (!Ask("BepInEx (the Valheim mod loader) is not installed:\n  " + string.Join("\n  ", miss) + "\n\nDownload and install BepInExPack_Valheim from Thunderstore?")) { Tell("ValCraft needs BepInEx. Installation cancelled.", MessageBoxIcon.Warning); return; }
                using (var w = Web())
                {
                    var (ver, url) = Thunderstore.Latest(w.DownloadString(Versions.ThunderstoreBepInEx));
                    Log($"downloading BepInExPack_Valheim {ver} ...");
                    var zip = Path.Combine(Path.GetTempPath(), $"BepInExPack_Valheim-{ver}.zip");
                    w.DownloadFile(url, zip);
                    BepInExCheck.ExtractPack(zip, v, Log);
                    File.Delete(zip);
                }
                if (BepInExCheck.Missing(v).Length > 0) throw new Exception("BepInEx still incomplete after extraction: " + string.Join(", ", BepInExCheck.Missing(v)));
            }
            else Log("BepInEx OK");

            // 2) plugin
            Plugin.Install(v, Res("ValCraft.Valheim.dll"), Log);

            // 3) Minecraft
            bool portable = (bool)Invoke(new Func<bool>(() => _portable.Checked));
            var cfg = new Dictionary<string, string>();
            string jarInfo;
            if (portable)
            {
                string root = (string)Invoke(new Func<string>(() => _prismRoot.Text.Trim()));
                if (!File.Exists(Prism.Exe(root)))
                {
                    Log("downloading Prism Launcher (portable) ...");
                    using (var w = Web())
                    {
                        var asset = Prism.PickPortableAsset(w.DownloadString(Versions.PrismReleases)) ?? throw new Exception("no Windows portable zip in Prism's latest release");
                        Log(asset);
                        var zip = Path.Combine(Path.GetTempPath(), "PrismLauncher-portable.zip");
                        w.DownloadFile(asset, zip);
                        Directory.CreateDirectory(root);
                        using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
                            foreach (var e in z.Entries)
                            {
                                var dst = Path.Combine(root, e.FullName.Replace('/', '\\'));
                                if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(dst); continue; }
                                Directory.CreateDirectory(Path.GetDirectoryName(dst)); System.IO.Compression.ZipFileExtensions.ExtractToFile(e, dst, true);
                            }
                        File.Delete(zip);
                    }
                }
                else Log("Prism already present: " + root);
                Prism.WriteInstance(root, Res("valcraft.jar"), Log);
                cfg["StartWithValheim"] = "true"; cfg["Launcher"] = "Prism"; cfg["PrismExe"] = Prism.Exe(root); cfg["InstanceName"] = Versions.InstanceName;
                jarInfo = Prism.ModsDir(root);
            }
            else
            {
                int kind = (int)Invoke(new Func<int>(() => _launcher.SelectedIndex));
                string exe = (string)Invoke(new Func<string>(() => _ownExe.Text.Trim())), mods = (string)Invoke(new Func<string>(() => _ownMods.Text.Trim())), name = (string)Invoke(new Func<string>(() => _instance.Text.Trim()));
                if (mods != "") Mods.InstallJar(mods, Res("valcraft.jar"), Log);
                else
                {
                    var keep = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ValCraft"); Directory.CreateDirectory(keep);
                    File.WriteAllBytes(Path.Combine(keep, Versions.JarName), Res("valcraft.jar")); Log($"no mods folder chosen: {Versions.JarName} saved to {keep} - copy it into your instance's mods folder");
                }
                jarInfo = mods;
                if (kind <= 1 && exe != "") { cfg["StartWithValheim"] = "true"; cfg["Launcher"] = kind == 0 ? "Prism" : "ATLauncher"; cfg["PrismExe"] = exe; cfg["InstanceName"] = name == "" ? Versions.InstanceName : name; }
                else { cfg["StartWithValheim"] = "false"; Log("this launcher cannot be started by Valheim: start Minecraft yourself after Valheim (see the setup guide)"); }
                Invoke(new Action(() => OpenGuide(false)));
            }
            BepCfg.Apply(Plugin.ConfigFile(v), cfg);
            Log("config updated: " + Plugin.ConfigFile(v) + " [" + string.Join(", ", cfg.Select(kv => kv.Key + "=" + kv.Value)) + "]");
            File.WriteAllText(Path.Combine(v, "BepInEx", "plugins", "ValCraft", "ValCraft-Minecraft-Setup.html"), Guide.Html(v));
            SaveState();
            // 0.15.0: ValCraft Launcher (per user), Start Menu always, desktop shortcut if ticked
            var lexe = LauncherInstall.Install(Res("launcher.exe"), Log);
            if (LauncherInstall.CreateShortcut(LauncherInstall.StartMenuLink(), lexe, "ValCraft Launcher")) Log("Start Menu entry: " + LauncherInstall.StartMenuLink());
            bool desk = (bool)Invoke(new Func<bool>(() => _desktop.Checked));
            if (desk && LauncherInstall.CreateShortcut(LauncherInstall.DesktopLink(), lexe, "ValCraft Launcher")) Log("desktop shortcut: " + LauncherInstall.DesktopLink());
            Log($"ValCraft {Versions.ValCraft} installed.");
            if (portable && Prism.Accounts((string)Invoke(new Func<string>(() => _prismRoot.Text.Trim()))) == 0)
            {
                if (Ask("Installed. Prism needs your Microsoft (Minecraft) account once.\n\nOpen Prism now to log in? (Accounts > Add Microsoft)\nIf you skip this, Valheim opens Prism for the login on first start.")) Invoke(new Action(LoginNow));
            }
            if (Ask($"ValCraft {Versions.ValCraft} installed.\n\nOpen the ValCraft Launcher now? (It keeps ValCraft up to date and starts Valheim through Steam.)"))
                try { Process.Start(new ProcessStartInfo(lexe) { UseShellExecute = true }); } catch (Exception e) { Log(e.Message); }
        }

        void Uninstall()
        {
            string v = (string)Invoke(new Func<string>(() => _valheim.Text.Trim()));
            if (!SteamLocator.IsValheim(v) || !Ask("Remove the ValCraft plugin from Valheim?")) return;
            bool cfgToo = Ask("Also delete ValCraft settings and data in BepInEx\\config (launcher settings, item mappings, anchors, beds)?\n\nNo = keep them for a later reinstall.");
            Plugin.Uninstall(v, cfgToo, Log);
            if (!cfgToo && File.Exists(Plugin.ConfigFile(v))) BepCfg.Apply(Plugin.ConfigFile(v), new Dictionary<string, string> { ["StartWithValheim"] = "false" });
            string root = (string)Invoke(new Func<string>(() => _prismRoot.Text.Trim()));
            if (File.Exists(Prism.Exe(root)) && File.Exists(Path.Combine(root, "portable.txt")))
            {
                if (Ask($"Delete the portable Prism Launcher in\n{root}?\n\nThis also deletes the ValCraft Minecraft instance INCLUDING its worlds.\nNo = keep it (only the ValCraft mod jar is removed)."))
                { Directory.Delete(root, true); Log("removed " + root); }
                else Mods.RemoveJar(Prism.ModsDir(root), Log);
            }
            string mods = (string)Invoke(new Func<string>(() => _ownMods.Text.Trim()));
            if (mods != "" && Directory.Exists(mods) && Ask("Remove the ValCraft jar from " + mods + "?")) Mods.RemoveJar(mods, Log);
            if (Ask("Remove the ValCraft Launcher and its shortcuts?")) LauncherInstall.Uninstall(Log);
            Log("uninstalled. BepInEx itself was left in place.");
            Tell("ValCraft removed.");
        }

        void LoginNow()
        {
            var root = _prismRoot.Text.Trim(); var exe = _portable.Checked ? Prism.Exe(root) : _ownExe.Text.Trim();
            if (!File.Exists(exe)) { MessageBox.Show(this, "Prism is not installed yet - click Install first.", "ValCraft"); return; }
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
            Log("Prism opened: Accounts (top right) > Manage Accounts > Add Microsoft. Then close Prism; Valheim launches Minecraft.");
        }

        void CheckLink()
        {
            string gameDir = _portable.Checked ? Path.Combine(Prism.InstanceDir(_prismRoot.Text.Trim()), "minecraft") : (_ownMods.Text.Trim() == "" ? null : Path.GetDirectoryName(_ownMods.Text.Trim().TrimEnd('\\')));
            var res = LinkCheck.Run(_valheim.Text.Trim(), gameDir);
            foreach (var r in res) Log(r);
            MessageBox.Show(this, string.Join("\n", res) + "\n\n(Based on the logs of the last Valheim/Minecraft run.)", "ValCraft link check");
        }

        void CheckUpdates()
        {
            if (!Versions.RepoConfigured) { Log("update check: no release repository configured in this build"); Tell("No update server configured in this build yet."); return; }
            Updates.Release r;
            using (var w = Web()) r = Updates.Parse(w.DownloadString(Updates.LatestUrl));
            if (!Updates.IsNewer(r)) { Log($"up to date (latest release {r.Tag})"); Tell($"ValCraft {Versions.ValCraft} is up to date."); return; }
            Log($"new version {r.Tag} available");
            if (r.InstallerUrl == null) { Tell($"ValCraft {r.Tag} is available: {r.NotesUrl}"); return; }
            if (!Ask($"ValCraft {r.Tag} is available. Download and run its installer now?\n(Your settings and item mappings are kept.)")) return;
            var f = Path.Combine(Path.GetTempPath(), r.InstallerUrl.Substring(r.InstallerUrl.LastIndexOf('/') + 1));
            using (var w = Web()) w.DownloadFile(r.InstallerUrl, f);
            Process.Start(new ProcessStartInfo(f) { UseShellExecute = true });
            Invoke(new Action(Close));
        }

        void OpenGuide(bool show)
        {
            var f = Path.Combine(Path.GetTempPath(), "ValCraft-Minecraft-Setup.html");
            File.WriteAllText(f, Guide.Html(SteamLocator.IsValheim(_valheim.Text.Trim()) ? _valheim.Text.Trim() : null));
            try { Process.Start(new ProcessStartInfo(f) { UseShellExecute = true }); } catch (Exception e) { Log(e.Message); }
        }

        void SaveState()
        {
            Invoke(new Action(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StateFile));
                File.WriteAllLines(StateFile, new[] { "valheim=" + _valheim.Text.Trim(), "portable=" + _portable.Checked, "prismRoot=" + _prismRoot.Text.Trim(), "launcher=" + _launcher.SelectedIndex, "exe=" + _ownExe.Text.Trim(), "mods=" + _ownMods.Text.Trim(), "instance=" + _instance.Text.Trim(), "version=" + Versions.ValCraft });
            }));
        }

        void LoadState()
        {
            try
            {
                if (!File.Exists(StateFile)) return;
                var d = File.ReadAllLines(StateFile).Where(l => l.Contains("=")).ToDictionary(l => l.Substring(0, l.IndexOf('=')), l => l.Substring(l.IndexOf('=') + 1));
                string G(string k) => d.TryGetValue(k, out var x) ? x : "";
                if (SteamLocator.IsValheim(G("valheim"))) _valheim.Text = G("valheim");
                if (G("prismRoot") != "") _prismRoot.Text = G("prismRoot");
                if (G("portable") == "False") _own.Checked = true;
                if (int.TryParse(G("launcher"), out var li) && li >= 0 && li < _launcher.Items.Count) _launcher.SelectedIndex = li;
                _ownExe.Text = G("exe"); _ownMods.Text = G("mods"); if (G("instance") != "") _instance.Text = G("instance");
                Log($"previous install found (ValCraft {G("version")}); Install = update/repair with the same settings");
            }
            catch { }
        }
    }
}
