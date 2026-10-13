using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using ValCraft.Installer;

namespace ValCraft.Launcher
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var f = new LauncherForm(offline: args.Contains("--offline"));
            int i = Array.IndexOf(args, "--screenshot");
            if (i >= 0 && i + 1 < args.Length) f.ScreenshotPath = args[i + 1];
            Application.Run(f);
        }
    }

    sealed class LauncherForm : Form
    {
        // ---- look ----
        static readonly Color Gold = Color.FromArgb(242, 183, 5), GoldDark = Color.FromArgb(196, 132, 0), TextDim = Color.FromArgb(170, 170, 170), Panel = Color.FromArgb(20, 22, 26);
        readonly Image _hero, _logoIcon; readonly Image[] _cardImgs;
        readonly Font _fTop = new Font(UiFont, 9f), _fTopB = new Font(UiFont, 9f, FontStyle.Bold), _fStatus = new Font(UiFont, 10f), _fSmall = new Font(UiFont, 9f),
            _fSmallB = new Font(UiFont, 9f, FontStyle.Bold), _fCardT = new Font(UiFont, 13f, FontStyle.Bold), _fCard = new Font(UiFont, 8.5f), _fBtn = new Font(UiFont, 12f, FontStyle.Bold),
            _fLink = new Font(UiFont, 8.5f, FontStyle.Underline), _fLogoSub = new Font(UiFont, 10f, FontStyle.Bold);
        static string UiFont => FontFamily.Families.Any(f => f.Name == "Segoe UI") ? "Segoe UI" : FontFamily.GenericSansSerif.Name;
        static string LogoFont => new[] { "Georgia", "Palatino Linotype", "Times New Roman", "DejaVu Serif" }.FirstOrDefault(n => FontFamily.Families.Any(f => f.Name == n)) ?? FontFamily.GenericSerif.Name;

        // ---- layout ----
        static readonly Rectangle RClose = new Rectangle(784, 8, 28, 28), RMin = new Rectangle(752, 8, 28, 28), RCard = new Rectangle(32, 172, 330, 150),
            RPrev = new Rectangle(36, 226, 26, 40), RNext = new Rectangle(332, 226, 26, 40), RLaunch = new Rectangle(566, 388, 228, 54), RBar = new Rectangle(28, 422, 510, 10);
        readonly List<(Rectangle r, string url)> _links = new List<(Rectangle, string)>();

        // ---- state ----
        List<NewsCard> _news = new List<NewsCard>();
        int _card; string _status = "Checking for updates..."; string _hover; bool _busy = true, _running, _offline, _canLaunch;
        readonly Progress _prog = new Progress { Total = 1 }; string _latest;
        readonly System.Windows.Forms.Timer _tick = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly bool _forceOffline; int _autoAdvance;
        public string ScreenshotPath;

        public LauncherForm(bool offline)
        {
            _forceOffline = offline;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Text = "ValCraft Launcher"; FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(820, 500); DoubleBuffered = true; BackColor = Panel;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            _hero = Res("hero.jpg"); _cardImgs = new[] { Res("card1.jpg"), Res("card2.jpg"), Res("card3.jpg") }; _logoIcon = null;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            _news = CachedNews() ?? new List<NewsCard> { new NewsCard { Title = "Welcome to ValCraft", Text = "Real Minecraft inside Valheim: build with Minecraft blocks in your Valheim world." } };
            _tick.Tick += (s, e) => { UpdateRunning(); if (++_autoAdvance % 8 == 0 && _news.Count > 1) { _card = (_card + 1) % _news.Count; Invalidate(RCard); } };
            _tick.Start();
            UpdateRunning();
            Shown += (s, e) =>
            {
                if (ScreenshotPath != null) { var t = new System.Windows.Forms.Timer { Interval = 2500 }; t.Tick += (a, b) => { t.Stop(); Snap(ScreenshotPath); Close(); }; t.Start(); }
                new Thread(CheckAndUpdate) { IsBackground = true }.Start();
            };
        }

        static Image Res(string name)
        {
            var s = typeof(LauncherForm).Assembly.GetManifestResourceStream(name);
            return s == null ? null : Image.FromStream(s);
        }

        void Snap(string path)
        {
            using (var bmp = new Bitmap(ClientSize.Width, ClientSize.Height)) { DrawToBitmap(bmp, new Rectangle(Point.Empty, ClientSize)); bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
        }

        void UpdateRunning()
        {
            bool r = false; try { r = Process.GetProcessesByName("valheim").Length > 0; } catch { }
            if (r != _running) { _running = r; Invalidate(); }
        }

        void Ui(Action a) { if (IsDisposed) return; try { if (InvokeRequired) BeginInvoke(a); else a(); } catch { } }
        void SetStatus(string s) => Ui(() => { _status = s; Invalidate(new Rectangle(0, 380, 560, 120)); });

        // ---------------- update logic ----------------
        static WebClient Web() { var w = new WebClient(); w.Headers[HttpRequestHeader.UserAgent] = "ValCraft-Launcher/" + LauncherInfo.Version; return w; }

        void CheckAndUpdate()
        {
            var st = InstallState.Load();
            try
            {
                if (_forceOffline) throw new WebException("offline (--offline)");
                List<ReleaseInfo> rels;
                using (var w = Web()) rels = ReleaseInfo.ParseList(w.DownloadString(LauncherInfo.ReleasesApi));
                var rel = rels.FirstOrDefault(r => !r.Prerelease);
                var news = News.FromReleases(rels.Take(6));
                if (news.Count > 0) { Ui(() => { _news = news; _card = 0; Invalidate(); }); SaveNews(news); }
                if (rel == null) { Done("No releases published yet.", true); return; }
                _latest = rel.Version;
                if (st == null) { Done("ValCraft is not installed yet: run ValCraft-Installer first. (Latest: " + rel.Version + ")", true); return; }
                var sumsUrl = rel.Asset("SHA256SUMS.txt");
                if (sumsUrl == null) { Done("Latest release has no SHA256SUMS.txt; not updating.", true); return; }
                Dictionary<string, string> sums; using (var w = Web()) sums = Sums.Parse(w.DownloadString(sumsUrl));
                var plan = UpdatePlan.Build(rel, sums, st, Application.ExecutablePath, LauncherInfo.Version);
                if (plan.Count == 0) { InstallState.SetVersion(null, rel.Version); Done($"No update required. ValCraft {rel.Version} is up to date.", true); return; }
                if (_running && plan.Any(p => p.Kind == ItemKind.Plugin)) { Done("An update is available - close Valheim to install it, then reopen the launcher.", true); return; }
                Ui(() => { _prog.Total = 1; Invalidate(); });
                string relaunch = null;
                foreach (var it in plan)
                {
                    SetStatus($"Downloading {it.Name} ...");
                    var tmp = Path.Combine(Path.GetTempPath(), "valcraft-" + Guid.NewGuid().ToString("N") + "-" + it.Name);
                    Download(it.Url, tmp);
                    var got = Sums.Of(tmp);
                    if (got != it.Sha) { File.Delete(tmp); throw new InvalidDataException($"{it.Name}: checksum mismatch (expected {it.Sha.Substring(0, 12)}..., got {got.Substring(0, 12)}...)"); }
                    SetStatus($"Installing {it.Name} ...");
                    if (it.Kind == ItemKind.Launcher) { relaunch = tmp; continue; }
                    if (it.Kind == ItemKind.Jar)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(it.Target));
                        Mods.InstallJar(Path.GetDirectoryName(it.Target), File.ReadAllBytes(tmp), null);
                    }
                    else Plugin.Install(st.Valheim, File.ReadAllBytes(tmp), null);
                    File.Delete(tmp);
                }
                InstallState.SetVersion(null, rel.Version);
                if (relaunch != null)
                {
                    SetStatus("Updating the launcher - restarting...");
                    var cmd = Path.Combine(Path.GetTempPath(), "valcraft-launcher-update.cmd");
                    File.WriteAllText(cmd, SelfUpdate.Script(Application.ExecutablePath, relaunch, Process.GetCurrentProcess().Id));
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + cmd + "\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
                    Ui(Close); return;
                }
                Done($"ValCraft {rel.Version} installed. Your game is up to date.", true);
            }
            catch (WebException e)
            {
                _offline = true;
                Done(st == null ? "Offline - can't check for updates. Install ValCraft with ValCraft-Installer first." : $"Offline - using the installed version{(string.IsNullOrEmpty(st.Version) ? "" : " (" + st.Version + ")")}. " + Short(e.Message), true);
            }
            catch (Exception e) { Done("Update failed: " + Short(e.Message) + " - you can still launch the installed version.", true); }
        }

        static string Short(string s) => s == null ? "" : s.Length > 80 ? s.Substring(0, 77) + "..." : s;

        void Done(string status, bool canLaunch) => Ui(() => { _status = status; _busy = false; _canLaunch = canLaunch; _prog.Total = 1; _prog.Done = 1; Invalidate(); });

        void Download(string url, string file)
        {
            var req = (HttpWebRequest)WebRequest.Create(url); req.UserAgent = "ValCraft-Launcher/" + LauncherInfo.Version; req.AllowAutoRedirect = true;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var f = File.Create(file))
            {
                var p = new Progress { Total = Math.Max(1, resp.ContentLength) };
                var sw = Stopwatch.StartNew(); var buf = new byte[65536]; long done = 0; int n; double lastUi = 0;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    f.Write(buf, 0, n); done += n; p.Update(done, sw.Elapsed.TotalSeconds);
                    if (sw.Elapsed.TotalSeconds - lastUi > 0.1) { lastUi = sw.Elapsed.TotalSeconds; Show(p); }
                }
                p.Update(done, sw.Elapsed.TotalSeconds + 0.3); Show(p);
            }
        }
        void Show(Progress p) => Ui(() => { _prog.Total = p.Total; _prog.Done = p.Done; _speedText = Progress.FormatSpeed(p.Speed); _remainText = Progress.FormatRemaining(p.Remaining); Invalidate(new Rectangle(0, 380, 560, 120)); });
        string _speedText = "0.00 B/s", _remainText = "0 min";

        static string NewsCache => Path.Combine(LauncherInfo.DataDir, "launcher-news.txt");
        static void SaveNews(List<NewsCard> n)
        {
            try { Directory.CreateDirectory(LauncherInfo.DataDir); File.WriteAllLines(NewsCache, n.Select(c => string.Join("\t", new[] { c.Title, c.Text, c.Date, c.Url }.Select(x => (x ?? "").Replace("\t", " ").Replace("\n", " "))))); } catch { }
        }
        static List<NewsCard> CachedNews()
        {
            try
            {
                if (!File.Exists(NewsCache)) return null;
                var l = File.ReadAllLines(NewsCache).Select(x => x.Split('\t')).Where(a => a.Length >= 4).Select(a => new NewsCard { Title = a[0], Text = a[1], Date = a[2], Url = a[3] }).ToList();
                return l.Count > 0 ? l : null;
            }
            catch { return null; }
        }

        // ---------------- input ----------------
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string h = RClose.Contains(e.Location) ? "close" : RMin.Contains(e.Location) ? "min" : RLaunch.Contains(e.Location) ? "launch" : RPrev.Contains(e.Location) ? "prev" : RNext.Contains(e.Location) ? "next" : null;
            foreach (var l in _links) if (l.r.Contains(e.Location)) h = l.url;
            if (h == null && RCard.Contains(e.Location) && _news.Count > 0 && _news[_card].Url != null) h = "card";
            Cursor = h != null && h != "close" && h != "min" ? Cursors.Hand : Cursors.Default;
            if (h != _hover) { _hover = h; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = null; Invalidate(); }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, int wp, int lp);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (_hover == null) { try { ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0); } catch { } return; } // drag the borderless window
            switch (_hover)
            {
                case "close": Close(); break;
                case "min": WindowState = FormWindowState.Minimized; break;
                case "prev": if (_news.Count > 0) { _card = (_card + _news.Count - 1) % _news.Count; _autoAdvance = 0; Invalidate(); } break;
                case "next": if (_news.Count > 0) { _card = (_card + 1) % _news.Count; _autoAdvance = 0; Invalidate(); } break;
                case "card": Open(_news[_card].Url); break;
                case "launch": Launch(); break;
                default: Open(_hover); break;
            }
        }

        void Open(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { _status = "Could not open " + url + ": " + ex.Message; Invalidate(); } }

        void Launch()
        {
            if (_busy || !_canLaunch) return;
            if (_running) { _status = "Valheim is already running."; Invalidate(); return; }
            try
            {
                Process.Start(new ProcessStartInfo(LauncherInfo.SteamLaunchUri) { UseShellExecute = true });
                _status = "Starting Valheim through Steam... Minecraft starts automatically once Valheim is up.";
            }
            catch (Exception ex) { _status = "Could not start Steam: " + ex.Message; }
            Invalidate();
        }

        // ---------------- painting ----------------
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            int W = ClientSize.Width, H = ClientSize.Height;
            if (_hero != null) g.DrawImage(_hero, new Rectangle(0, 0, W, H)); else g.Clear(Panel);
            using (var lg = new LinearGradientBrush(new Rectangle(0, 0, 520, H), Color.FromArgb(235, 12, 13, 16), Color.FromArgb(0, 12, 13, 16), 0f)) g.FillRectangle(lg, 0, 0, 520, H);
            using (var bg = new LinearGradientBrush(new Rectangle(0, H - 190, W, 190), Color.FromArgb(0, 10, 10, 12), Color.FromArgb(245, 10, 10, 12), 90f)) g.FillRectangle(bg, 0, H - 189, W, 190);
            using (var tb = new SolidBrush(Color.FromArgb(150, 10, 10, 12))) g.FillRectangle(tb, 0, 0, W, 44);
            using (var p = new Pen(Color.FromArgb(60, 255, 255, 255))) { g.DrawLine(p, 24, 44, W - 24, 44); g.DrawRectangle(p, 0, 0, W - 1, H - 1); }

            // top bar
            int x = 26;
            x = Txt(g, "ValCraft Launcher", _fTopB, Color.White, x, 14) + 12;
            using (var p = new Pen(Color.FromArgb(90, 255, 255, 255))) g.DrawLine(p, x, 15, x, 29); x += 12;
            x = Txt(g, "Game Status:", _fTop, TextDim, x, 14) + 4;
            Txt(g, _running ? "Valheim is running" : "Game is not running", _fTopB, _running ? Gold : Color.White, x, 14);
            using (var p = new Pen(_hover == "min" ? Color.White : TextDim, 1.6f)) g.DrawLine(p, RMin.X + 8, RMin.Y + 15, RMin.Right - 8, RMin.Y + 15);
            using (var p = new Pen(_hover == "close" ? Gold : TextDim, 1.6f)) { g.DrawLine(p, RClose.X + 8, RClose.Y + 8, RClose.Right - 8, RClose.Bottom - 8); g.DrawLine(p, RClose.Right - 8, RClose.Y + 8, RClose.X + 8, RClose.Bottom - 8); }

            DrawLogo(g, 36, 62);
            DrawCard(g);

            // guide links
            _links.Clear();
            int lx = 34;
            foreach (var (label, url) in LauncherInfo.Guides)
            {
                var sz = g.MeasureString(label, _fLink);
                var r = new Rectangle(lx, 334, (int)sz.Width, (int)sz.Height);
                _links.Add((r, url));
                using (var b = new SolidBrush(_hover == url ? Gold : Color.FromArgb(215, 215, 215))) g.DrawString(label, _fLink, b, lx, 334);
                lx += (int)sz.Width + 8;
            }

            // status + progress
            Txt(g, _status, _fStatus, Color.FromArgb(225, 225, 225), 26, 394, 530);
            using (var b = new SolidBrush(Color.FromArgb(70, 70, 70))) g.FillRectangle(b, RBar);
            int fw = (int)(RBar.Width * (_busy && _prog.Done == 0 ? 0 : _prog.Fraction));
            if (fw > 0) using (var gb = new LinearGradientBrush(RBar, Gold, Color.FromArgb(255, 205, 40), 90f)) g.FillRectangle(gb, RBar.X, RBar.Y, fw, RBar.Height);
            x = Txt(g, "Speed:", _fSmall, TextDim, 26, 446) + 3; x = Txt(g, _speedText, _fSmallB, Color.White, x, 446) + 18;
            x = Txt(g, "Time remaining:", _fSmall, TextDim, x, 446) + 3; Txt(g, _remainText, _fSmallB, Color.White, x, 446);
            if (_offline) Txt(g, "OFFLINE", _fSmallB, Color.FromArgb(230, 120, 80), 470, 446);

            // launch button
            bool en = !_busy && _canLaunch && !_running, hov = _hover == "launch" && en;
            var rb = RLaunch;
            if (en) for (int i = 6; i >= 1; i--) using (var glow = new Pen(Color.FromArgb(hov ? 26 : 16, Gold), i * 2)) g.DrawRectangle(glow, rb);
            using (var lb = new LinearGradientBrush(rb, en ? (hov ? Color.FromArgb(255, 214, 70) : Gold) : Color.FromArgb(110, 100, 70), en ? GoldDark : Color.FromArgb(80, 72, 50), 90f)) g.FillRectangle(lb, rb);
            using (var p = new Pen(Color.FromArgb(120, 255, 240, 180))) g.DrawRectangle(p, rb.X, rb.Y, rb.Width - 1, rb.Height - 1);
            string bt = _busy ? "UPDATING..." : _running ? "RUNNING" : "LAUNCH GAME";
            var bs = g.MeasureString(bt, _fBtn);
            using (var b = new SolidBrush(en ? Color.FromArgb(45, 30, 0) : Color.FromArgb(200, 190, 160))) g.DrawString(bt, _fBtn, b, rb.X + (rb.Width - bs.Width) / 2, rb.Y + (rb.Height - bs.Height) / 2);

            // client version
            var iv = InstallState.Load()?.Version;
            string cv = (string.IsNullOrEmpty(iv) ? "not installed" : "V " + iv) + "  \u00b7  launcher " + LauncherInfo.Version;
            var cvs = g.MeasureString(cv, _fSmallB);
            float cx = RLaunch.Right - cvs.Width;
            var lw = g.MeasureString("Client Version:", _fSmall).Width;
            Txt(g, "Client Version:", _fSmall, TextDim, (int)(cx - lw - 2), 452); Txt(g, cv, _fSmallB, Color.White, (int)cx, 452);
        }

        int Txt(Graphics g, string s, Font f, Color c, int x, int y, int maxW = 0)
        {
            using (var b = new SolidBrush(c))
            {
                if (maxW > 0) { g.DrawString(s, f, b, new RectangleF(x, y, maxW, 24), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap }); return x + maxW; }
                g.DrawString(s, f, b, x, y);
                return x + (int)g.MeasureString(s, f).Width;
            }
        }

        void DrawLogo(Graphics g, int x, int y)
        {
            using (var fam = new FontFamily(LogoFont))
            using (var path = new GraphicsPath())
            {
                path.AddString("ValCraft", fam, (int)FontStyle.Bold, 62f, new Point(x, y), StringFormat.GenericDefault);
                using (var sh = (GraphicsPath)path.Clone())
                {
                    using (var m = new Matrix()) { m.Translate(3, 4); sh.Transform(m); }
                    using (var b = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillPath(b, sh);
                }
                var bounds = path.GetBounds();
                using (var fill = new LinearGradientBrush(new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height + 1), Color.FromArgb(255, 252, 240), Color.FromArgb(232, 196, 120), 90f)) g.FillPath(fill, path);
                using (var p = new Pen(Color.FromArgb(120, 70, 45, 10), 1.2f)) g.DrawPath(p, path);
                using (var p = new Pen(Gold, 2f)) g.DrawLine(p, bounds.X + 4, bounds.Bottom + 8, bounds.X + 150, bounds.Bottom + 8);
                using (var b = new SolidBrush(Gold)) g.DrawString("MINECRAFT  \u00b7  INSIDE  \u00b7  VALHEIM", _fLogoSub, b, bounds.X + 2, bounds.Bottom + 13);
            }
        }

        void DrawCard(Graphics g)
        {
            var r = RCard;
            using (var path = Round(r, 8))
            {
                g.SetClip(path);
                var img = _cardImgs[_card % _cardImgs.Length];
                if (img != null) g.DrawImage(img, r); else using (var b = new SolidBrush(Color.FromArgb(40, 44, 52))) g.FillRectangle(b, r);
                using (var lg = new LinearGradientBrush(new Rectangle(r.X, r.Y + 30, r.Width, r.Height - 30), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(230, 8, 8, 10), 90f)) g.FillRectangle(lg, r.X, r.Y + 31, r.Width, r.Height - 31);
                g.ResetClip();
                using (var p = new Pen(Color.FromArgb(_hover == "card" ? 200 : 110, _hover == "card" ? Gold : Color.White), 1.5f)) g.DrawPath(p, path);
            }
            if (_news.Count == 0) return;
            var c = _news[_card % _news.Count];
            g.DrawString(c.Title, _fCardT, Brushes.White, new RectangleF(r.X + 34, r.Y + 62, r.Width - 68, 26), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            using (var b = new SolidBrush(Color.FromArgb(225, 225, 225))) g.DrawString(c.Text ?? "", _fCard, b, new RectangleF(r.X + 34, r.Y + 88, r.Width - 68, 34), new StringFormat { Trimming = StringTrimming.EllipsisWord });
            if (!string.IsNullOrEmpty(c.Date)) using (var b = new SolidBrush(Gold)) { var ds = g.MeasureString(c.Date, _fCard); g.DrawString(c.Date, _fCard, b, r.Right - 14 - ds.Width, r.Y + 10); }
            // dots
            int n = Math.Min(_news.Count, 8), dx = r.X + r.Width / 2 - n * 6;
            for (int i = 0; i < n; i++) using (var b = new SolidBrush(i == _card ? Gold : Color.FromArgb(120, 255, 255, 255))) g.FillEllipse(b, dx + i * 12, r.Bottom - 13, 6, 6);
            // arrows
            DrawArrow(g, RPrev, true, _hover == "prev"); DrawArrow(g, RNext, false, _hover == "next");
        }

        void DrawArrow(Graphics g, Rectangle r, bool left, bool hov)
        {
            using (var p = new Pen(hov ? Color.White : Gold, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, d = left ? 1 : -1;
                g.DrawLines(p, new[] { new Point(cx + 5 * d, cy - 12), new Point(cx - 5 * d, cy), new Point(cx + 5 * d, cy + 12) });
            }
        }

        static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath(); int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
    }
}
