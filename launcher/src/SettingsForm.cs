using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ValCraft.Launcher
{
    /// 0.15.1: launcher settings page: per mob family sound swap, music, and common plugin/mod options.
    sealed class SettingsForm : Form
    {
        public Settings Result;
        readonly CheckBox[] _swap; readonly CheckBox _music, _start, _combat; readonly NumericUpDown _maxMobs, _emerald;
        static readonly Color Bg = Color.FromArgb(24, 26, 30), Gold = Color.FromArgb(242, 183, 5);

        public SettingsForm(Settings s)
        {
            Text = "ValCraft Settings"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 470); BackColor = Bg; ForeColor = Color.White; Font = new Font("Segoe UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            int y = 14;
            Label H(string t) { var l = new Label { Text = t, Left = 16, Top = y, Width = 520, Height = 22, ForeColor = Gold, Font = new Font(Font, FontStyle.Bold) }; Controls.Add(l); y += 24; return l; }
            H("Mob sounds - checked: Minecraft sound plays, the Valheim stand-in is silent");
            _swap = Settings.Families.Select((f, i) =>
            {
                var c = new CheckBox { Text = char.ToUpper(f[0]) + f.Substring(1) + (f == "animal" ? "s" : f == "other" ? " mobs" : "s"), Checked = s.Swap[f], Left = 24 + (i % 3) * 175, Top = y + (i / 3) * 26, Width = 170, ForeColor = Color.White, Tag = f };
                Controls.Add(c); return c;
            }).ToArray();
            y += 26 * ((Settings.Families.Length + 2) / 3) + 6;
            Controls.Add(new Label { Text = "Unchecked: Minecraft's sounds for that family are muted and the Valheim stand-in keeps its Valheim sounds.", Left = 24, Top = y, Width = 520, Height = 32, ForeColor = Color.Silver }); y += 40;
            H("Minecraft");
            _music = new CheckBox { Text = "Mute Minecraft music while linked to Valheim", Checked = s.MuteMusic, Left = 24, Top = y, Width = 500, ForeColor = Color.White }; Controls.Add(_music); y += 28;
            Controls.Add(new Label { Text = "Emerald chance per stone block broken:", Left = 24, Top = y + 3, Width = 260 });
            _emerald = new NumericUpDown { Left = 290, Top = y, Width = 90, DecimalPlaces = 3, Increment = 0.001m, Minimum = 0, Maximum = 1, Value = (decimal)Math.Max(0, Math.Min(1, s.EmeraldChance)) }; Controls.Add(_emerald); y += 36;
            H("Valheim");
            _start = new CheckBox { Text = "Start Minecraft automatically with Valheim", Checked = s.StartWithValheim, Left = 24, Top = y, Width = 500, ForeColor = Color.White }; Controls.Add(_start); y += 28;
            _combat = new CheckBox { Text = "Cross-game combat (Valheim monsters and Minecraft mobs fight each other)", Checked = s.CrossGameCombat, Left = 24, Top = y, Width = 520, ForeColor = Color.White }; Controls.Add(_combat); y += 28;
            Controls.Add(new Label { Text = "Max Minecraft mobs drawn in Valheim:", Left = 24, Top = y + 3, Width = 260 });
            _maxMobs = new NumericUpDown { Left = 290, Top = y, Width = 90, Minimum = 0, Maximum = 128, Value = Math.Max(0, Math.Min(128, s.MaxMobs)) }; Controls.Add(_maxMobs); y += 44;
            var ok = new Button { Text = "Save", Left = 330, Top = ClientSize.Height - 48, Width = 100, Height = 32, BackColor = Gold, ForeColor = Color.Black, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 440, Top = ClientSize.Height - 48, Width = 100, Height = 32, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };
            Controls.Add(ok); Controls.Add(cancel); AcceptButton = ok; CancelButton = cancel;
            FormClosing += (a, e) =>
            {
                if (DialogResult != DialogResult.OK) return;
                Result = new Settings { MuteMusic = _music.Checked, StartWithValheim = _start.Checked, CrossGameCombat = _combat.Checked, MaxMobs = (int)_maxMobs.Value, EmeraldChance = (double)_emerald.Value };
                foreach (var c in _swap) Result.Swap[(string)c.Tag] = c.Checked;
            };
        }
    }
}
