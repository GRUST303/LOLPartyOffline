using System;
using System.Drawing;
using System.Windows.Forms;

namespace PartyOffline
{
    /// <summary>设置窗口：改热键、改日志上限。</summary>
    internal sealed class SettingsForm : Form
    {
        private readonly TextBox _offlineBox;
        private readonly TextBox _onlineBox;
        private readonly NumericUpDown _logKb;
        private readonly Label _logUsage;
        private readonly Label _hint;

        internal HotkeySpec ResultOffline { get; private set; }
        internal HotkeySpec ResultOnline { get; private set; }
        internal int ResultMaxLogKb { get; private set; }

        internal SettingsForm(AppSettings current)
        {
            ResultOffline = current.HotkeyOffline;
            ResultOnline = current.HotkeyOnline;
            ResultMaxLogKb = current.MaxLogKb;

            Text = "设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(430, 316);
            Font = new Font("Microsoft YaHei UI", 9f);

            var title1 = new Label
            {
                Text = "热键",
                Location = new Point(18, 14),
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold)
            };

            var lblOff = new Label { Text = "切离线", Location = new Point(24, 44), AutoSize = true };
            _offlineBox = MakeHotkeyBox(current.HotkeyOffline, new Point(100, 40));
            _offlineBox.KeyDown += OnCaptureOffline;

            var lblOn = new Label { Text = "切在线", Location = new Point(24, 78), AutoSize = true };
            _onlineBox = MakeHotkeyBox(current.HotkeyOnline, new Point(100, 74));
            _onlineBox.KeyDown += OnCaptureOnline;

            _hint = new Label
            {
                Text = "点一下输入框，然后直接按想要的组合键",
                Location = new Point(100, 108),
                AutoSize = true,
                ForeColor = Color.Gray
            };

            var title2 = new Label
            {
                Text = "日志",
                Location = new Point(18, 142),
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold)
            };

            var lblCap = new Label { Text = "单个文件上限", Location = new Point(24, 172), AutoSize = true };
            _logKb = new NumericUpDown
            {
                Location = new Point(140, 168),
                Width = 100,
                Minimum = 64,
                Maximum = 65536,
                Increment = 256,
                Value = Math.Min(65536, Math.Max(64, current.MaxLogKb))
            };
            var kbUnit = new Label { Text = "KB", Location = new Point(246, 172), AutoSize = true };

            _logUsage = new Label
            {
                Text = LogUsageText(current.MaxLogKb),
                Location = new Point(24, 200),
                Size = new Size(360, 36),
                ForeColor = Color.Gray
            };

            var btnClear = new Button
            {
                Text = "清空日志",
                Location = new Point(140, 238),
                Size = new Size(92, 26)
            };
            btnClear.Click += (s, e) =>
            {
                Log.Clear();
                _logUsage.Text = LogUsageText((int)_logKb.Value);
                MessageBox.Show(this, "日志已清空。", "设置",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            var btnDefault = new Button
            {
                Text = "恢复默认热键",
                Location = new Point(24, 274),
                Size = new Size(108, 26)
            };
            btnDefault.Click += (s, e) =>
            {
                var d = new AppSettings();
                _offlineBox.Text = d.HotkeyOffline.ToString();
                _onlineBox.Text = d.HotkeyOnline.ToString();
            };

            var btnOk = new Button
            {
                Text = "保存",
                Location = new Point(232, 274),
                Size = new Size(80, 26)
            };
            btnOk.Click += OnSave;

            var btnCancel = new Button
            {
                Text = "取消",
                Location = new Point(322, 274),
                Size = new Size(80, 26),
                DialogResult = DialogResult.Cancel
            };

            Controls.AddRange(new Control[]
            {
                title1, lblOff, _offlineBox, lblOn, _onlineBox, _hint,
                title2, lblCap, _logKb, kbUnit, _logUsage, btnClear,
                btnDefault, btnOk, btnCancel
            });

            AcceptButton = null;
            CancelButton = btnCancel;
        }

        private static string LogUsageText(int maxKb)
        {
            var used = Log.Footprint() / 1024.0;
            var cap = Math.Max(1, maxKb) / 1024.0;
            return string.Format("当前占用 {0:N1} MB，最多约 {1:N1} MB（滚动保留 1 份历史）",
                used, cap * 2);
        }

        private TextBox MakeHotkeyBox(HotkeySpec spec, Point location)
        {
            return new TextBox
            {
                Location = location,
                Width = 180,
                ReadOnly = true,
                Text = spec.ToString(),
                TextAlign = HorizontalAlignment.Center,
                TabStop = false,
                BackColor = Color.White,
                ShortcutsEnabled = false
            };
        }

        private void OnCaptureOffline(object? sender, KeyEventArgs e) => CaptureHotkey(_offlineBox, e);

        private void OnCaptureOnline(object? sender, KeyEventArgs e) => CaptureHotkey(_onlineBox, e);

        private static void CaptureHotkey(TextBox box, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;

            var key = e.KeyCode;

            // 只按修饰键时先不处理，等主键
            if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu ||
                key == Keys.LWin || key == Keys.RWin || key == Keys.None)
                return;

            uint mods = 0;
            var mk = Control.ModifierKeys;
            if ((mk & Keys.Control) != 0) mods |= HotkeySpec.ModControl;
            if ((mk & Keys.Alt) != 0) mods |= HotkeySpec.ModAlt;
            if ((mk & Keys.Shift) != 0) mods |= HotkeySpec.ModShift;

            box.Text = HotkeySpec.Describe(key, mods);
        }

        private void OnSave(object? sender, EventArgs e)
        {
            var off = HotkeySpec.Parse(_offlineBox.Text);
            var on = HotkeySpec.Parse(_onlineBox.Text);

            if (off == null || on == null)
            {
                MessageBox.Show(this, "热键格式无法识别。", "设置",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!off.HasModifier || !on.HasModifier)
            {
                MessageBox.Show(this,
                    "热键必须带至少一个修饰键（Ctrl / Alt / Shift）。\n\n" +
                    "只用单个按键会抢占全局输入，让你在游戏里打不出那个键。",
                    "设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (off.Modifiers == on.Modifiers && off.VirtualKey == on.VirtualKey)
            {
                MessageBox.Show(this, "两个热键不能相同。", "设置",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ResultOffline = off;
            ResultOnline = on;
            ResultMaxLogKb = (int)_logKb.Value;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
