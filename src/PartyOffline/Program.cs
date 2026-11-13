using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PartyOffline
{
    /// <summary>
    ///     常驻托盘程序：组好队后断开聊天，好友就看不到你在线。
    ///
    ///     默认热键 Ctrl+Alt+1 / Ctrl+Alt+2，可在托盘菜单「设置」里改。
    ///     日志有容量上限，超了就滚动，最多约上限的 2 倍。
    /// </summary>
    internal static class Program
    {
        private const string MutexName = @"Global\PartyOffline_SingleInstance_9F2A";

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [STAThread]
        private static void Main(string[] args)
        {
            var settings = AppSettings.Load();
            Log.Init(settings.MaxLogKb);

            if (args.Length > 0 && args[0] == "--selftest")
            {
                RunSelfTest(settings);
                return;
            }

            if (args.Length > 0 && args[0] == "--logtest")
            {
                RunLogTest(settings);
                return;
            }

            using var mutex = new Mutex(true, MutexName, out var isFirst);
            if (!isFirst)
            {
                MessageBox.Show("程序已经在运行了，请看系统托盘。",
                    "PartyOffline", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Log.Write("启动 v" + typeof(Program).Assembly.GetName().Version);
            Log.Write("热键：离线=" + settings.HotkeyOffline + "  在线=" + settings.HotkeyOnline);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext(settings));

            Log.Write("退出");
            GC.KeepAlive(mutex);
        }

        /// <summary>验证日志滚动：狂写一轮，然后报告实际占用。</summary>
        private static void RunLogTest(AppSettings settings)
        {
            var capKb = Math.Min(settings.MaxLogKb, 256); // 用小上限，快速触发滚动
            Log.Init(capKb);

            var line = new string('x', 200);
            for (var i = 0; i < 4000; i++)
                Log.Write("LOGTEST " + i.ToString("D5") + " " + line);

            var cur = 0L;
            var old = 0L;
            try
            {
                if (System.IO.File.Exists(Log.CurrentPath))
                    cur = new System.IO.FileInfo(Log.CurrentPath).Length;
            }
            catch { }
            try
            {
                if (System.IO.File.Exists(Log.ArchivePath))
                    old = new System.IO.FileInfo(Log.ArchivePath).Length;
            }
            catch { }

            var capBytes = capKb * 1024L;
            var sb = new StringBuilder();
            sb.AppendLine("=== logtest ===");
            sb.AppendLine("cap per file : " + capKb + " KB (" + capBytes + " bytes)");
            sb.AppendLine("current      : " + cur + " bytes");
            sb.AppendLine("archive      : " + old + " bytes");
            sb.AppendLine("total        : " + (cur + old) + " bytes");
            sb.AppendLine("current <= cap : " + (cur <= capBytes ? "PASS" : "FAIL"));
            sb.AppendLine("archive <= cap : " + (old <= capBytes ? "PASS" : "FAIL"));
            sb.AppendLine("total <= 2*cap : " + (cur + old <= capBytes * 2 ? "PASS" : "FAIL"));
            sb.AppendLine("wrote lines    : 4000 (每条约 220 字节，合计约 860KB)");

            try
            {
                System.IO.Directory.CreateDirectory(AppSettings.DataDir);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(AppSettings.DataDir, "party-offline-logtest.log"),
                    sb.ToString(), new UTF8Encoding(false));
            }
            catch
            {
                // 忽略
            }

            // 测试完把日志恢复成正常上限并清掉测试内容
            Log.Clear();
            Log.Init(settings.MaxLogKb);
            Log.Write("logtest 完成，日志上限恢复为 " + settings.MaxLogKb + " KB");
        }

        /// <summary>自检：把定位与接口结果写到日志，便于排查。</summary>
        private static void RunSelfTest(AppSettings settings)
        {
            var sb = new StringBuilder();
            void W(string s) => sb.AppendLine(s);

            W("=== PartyOffline selftest ===");
            W("time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            W("version: " + typeof(Program).Assembly.GetName().Version);
            W("settings: " + AppSettings.SettingsPath);
            W("log: " + Log.CurrentPath + "  cap=" + settings.MaxLogKb + "KB  used=" + Log.Footprint() + "B");

            try
            {
                var root = LcuClient.FindLolRoot(settings.LolRoot);
                W("lol root: " + (root ?? "<not found>"));

                if (root != null)
                {
                    var ep = LcuClient.FindEndpoint(root);
                    if (ep == null)
                    {
                        W("endpoint: <null>  (日志里没找到凭据，游戏可能没启动完)");
                    }
                    else
                    {
                        W("endpoint: port=" + ep.Port + " token=" + ep.Token);
                        W("chat state: " + LcuClient.GetChatState(ep));
                    }
                }

                W("hotkey offline: " + settings.HotkeyOffline);
                W("hotkey online : " + settings.HotkeyOnline);

                var probe = new MessageWindow();
                var id1 = RegisterHotKey(probe.Handle, 9901, settings.HotkeyOffline.Win32Modifiers,
                    settings.HotkeyOffline.VirtualKey);
                var id2 = RegisterHotKey(probe.Handle, 9902, settings.HotkeyOnline.Win32Modifiers,
                    settings.HotkeyOnline.VirtualKey);
                W("register offline: " + (id1 ? "OK" : "FAILED"));
                W("register online : " + (id2 ? "OK" : "FAILED"));
                if (id1) UnregisterHotKey(probe.Handle, 9901);
                if (id2) UnregisterHotKey(probe.Handle, 9902);
                probe.DestroyHandle();

                // 确认设置窗口能正常构建（字体/布局问题会在这里暴露）
                try
                {
                    using var form = new SettingsForm(settings);
                    W("settings form: OK (" + form.ClientSize.Width + "x" + form.ClientSize.Height + ")");
                }
                catch (Exception fe)
                {
                    W("settings form: FAIL " + fe.Message);
                }

                W("RESULT: OK");
            }
            catch (Exception e)
            {
                W("RESULT: FAIL " + e);
            }

            try
            {
                System.IO.Directory.CreateDirectory(AppSettings.DataDir);
            }
            catch
            {
                // 忽略
            }

            var path = System.IO.Path.Combine(AppSettings.DataDir, "party-offline-selftest.log");
            try
            {
                System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>只用来接收 WM_HOTKEY 的隐藏消息窗口。</summary>
    internal sealed class MessageWindow : NativeWindow
    {
        internal const int WmHotKey = 0x0312;
        internal event Action<int>? HotKeyPressed;

        internal MessageWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "PartyOfflineMessageWindow",
                Parent = new IntPtr(-3) // HWND_MESSAGE：消息专用，不显示
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotKey)
            {
                HotKeyPressed?.Invoke(m.WParam.ToInt32());
                return;
            }

            base.WndProc(ref m);
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HotKeyOfflineId = 9001;
        private const int HotKeyOnlineId = 9002;

        private readonly AppSettings _settings;
        private readonly NotifyIcon _icon;
        private readonly MessageWindow _window;
        private readonly System.Windows.Forms.Timer _pollTimer;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _offlineItem;
        private readonly ToolStripMenuItem _onlineItem;

        private string _chatState = "unknown";
        private bool _busy;

        internal TrayContext(AppSettings settings)
        {
            _settings = settings;

            _window = new MessageWindow();
            _window.HotKeyPressed += OnHotKey;

            _statusItem = new ToolStripMenuItem("状态：检测中…") { Enabled = false };
            _offlineItem = new ToolStripMenuItem("切离线", null, (s, e) => DoOffline());
            _onlineItem = new ToolStripMenuItem("切在线", null, (s, e) => DoOnline());
            UpdateHotkeyLabels();

            var menu = new ContextMenuStrip();
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_offlineItem);
            menu.Items.Add(_onlineItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("设置…", null, (s, e) => OpenSettings()));
            menu.Items.Add(new ToolStripMenuItem("查看日志", null, (s, e) => OpenLogFolder()));
            menu.Items.Add(BuildStartupItem());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出", null, (s, e) => ExitApp()));

            _icon = new NotifyIcon
            {
                Icon = BuildIcon(Color.FromArgb(120, 120, 120)),
                Text = "PartyOffline - 检测中",
                Visible = true,
                ContextMenuStrip = menu
            };
            _icon.DoubleClick += (s, e) => Toggle();

            if (!RegisterHotKeys(_settings.HotkeyOffline, _settings.HotkeyOnline, out var error))
            {
                Log.Write("热键注册失败：" + error);
                _icon.ShowBalloonTip(6000, "热键注册失败",
                    error + "\n\n可以用托盘右键菜单操作，或在「设置」里换一组热键。",
                    ToolTipIcon.Warning);
            }

            _pollTimer = new System.Windows.Forms.Timer { Interval = 4000 };
            _pollTimer.Tick += (s, e) => RefreshState(silent: true);
            _pollTimer.Start();

            RefreshState(silent: false);
        }

        private void UpdateHotkeyLabels()
        {
            _offlineItem.Text = "切离线    " + _settings.HotkeyOffline;
            _onlineItem.Text = "切在线    " + _settings.HotkeyOnline;
        }

        /// <summary>注册热键。失败时会尽量把旧的恢复回去。</summary>
        private bool RegisterHotKeys(HotkeySpec offline, HotkeySpec online, out string error)
        {
            error = string.Empty;

            var ok1 = RegisterHotKey(_window.Handle, HotKeyOfflineId,
                offline.Win32Modifiers, offline.VirtualKey);
            var ok2 = RegisterHotKey(_window.Handle, HotKeyOnlineId,
                online.Win32Modifiers, online.VirtualKey);

            if (ok1 && ok2)
            {
                Log.Write("热键已注册：离线=" + offline + "  在线=" + online);
                return true;
            }

            if (ok1) UnregisterHotKey(_window.Handle, HotKeyOfflineId);
            if (ok2) UnregisterHotKey(_window.Handle, HotKeyOnlineId);

            error = !ok1 && !ok2
                ? "两个热键都被其他程序占用了。"
                : (!ok1 ? "「" + offline + "」已被其他程序占用。" : "「" + online + "」已被其他程序占用。");
            return false;
        }

        private bool TryRebind(HotkeySpec offline, HotkeySpec online, out string error)
        {
            // 先撤掉旧的，避免和自己冲突
            UnregisterHotKey(_window.Handle, HotKeyOfflineId);
            UnregisterHotKey(_window.Handle, HotKeyOnlineId);

            if (RegisterHotKeys(offline, online, out error))
                return true;

            // 失败则恢复原设置
            if (!RegisterHotKeys(_settings.HotkeyOffline, _settings.HotkeyOnline, out var restoreError))
                Log.Write("恢复原热键也失败：" + restoreError);
            return false;
        }

        private void OnHotKey(int id)
        {
            if (id == HotKeyOfflineId) DoOffline();
            else if (id == HotKeyOnlineId) DoOnline();
        }

        private void Toggle()
        {
            if (_chatState == "connected") DoOffline();
            else DoOnline();
        }

        private void OpenSettings()
        {
            using var dlg = new SettingsForm(_settings);
            if (dlg.ShowDialog() != DialogResult.OK)
                return;

            var off = dlg.ResultOffline;
            var on = dlg.ResultOnline;
            var logKb = dlg.ResultMaxLogKb;

            var hotkeyChanged = off.Modifiers != _settings.HotkeyOffline.Modifiers
                                || off.VirtualKey != _settings.HotkeyOffline.VirtualKey
                                || on.Modifiers != _settings.HotkeyOnline.Modifiers
                                || on.VirtualKey != _settings.HotkeyOnline.VirtualKey;

            if (hotkeyChanged)
            {
                if (!TryRebind(off, on, out var error))
                {
                    MessageBox.Show("热键设置失败：\n\n" + error + "\n\n已恢复原来的热键。",
                        "设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                _settings.HotkeyOffline = off;
                _settings.HotkeyOnline = on;
                UpdateHotkeyLabels();
                _icon.ShowBalloonTip(3000, "热键已更新",
                    "切离线 " + off + "\n切在线 " + on, ToolTipIcon.Info);
            }

            if (logKb != _settings.MaxLogKb)
            {
                _settings.MaxLogKb = logKb;
                Log.Init(logKb);
                Log.Write("日志上限改为 " + logKb + " KB");
            }

            _settings.Save();
            Log.Write("设置已保存");
        }

        private void DoOffline()
        {
            if (_busy) return;
            _busy = true;
            Log.Write("操作：切离线");

            Task.Run(() =>
            {
                var (ok, message) = Switch(toOffline: true);
                Post(() =>
                {
                    _busy = false;
                    Log.Write("切离线结果：" + (ok ? "成功 " : "失败 ") + message);
                    if (ok)
                        _icon.ShowBalloonTip(3000, "已切离线",
                            "好友现在看不到你在线。队伍不解散，可以正常排队。",
                            ToolTipIcon.Info);
                    else
                        _icon.ShowBalloonTip(5000, "切离线失败", message, ToolTipIcon.Warning);

                    RefreshState(silent: true);
                });
            });
        }

        private void DoOnline()
        {
            if (_busy) return;
            _busy = true;
            Log.Write("操作：切在线");

            Task.Run(() =>
            {
                var (ok, message) = Switch(toOffline: false);
                Post(() =>
                {
                    _busy = false;
                    Log.Write("切在线结果：" + (ok ? "成功 " : "失败 ") + message);
                    if (ok)
                        _icon.ShowBalloonTip(3000, "已恢复在线",
                            "好友列表和邀请功能可以用了。", ToolTipIcon.Info);
                    else
                        _icon.ShowBalloonTip(5000, "恢复在线失败", message, ToolTipIcon.Warning);

                    RefreshState(silent: true);
                });
            });
        }

        private (bool ok, string message) Switch(bool toOffline)
        {
            var root = LcuClient.FindLolRoot(_settings.LolRoot);
            if (root == null)
                return (false, "找不到英雄联盟安装目录。请确认游戏已安装。");

            _settings.SaveLolRoot(root);

            var ep = LcuClient.FindEndpoint(root);
            if (ep == null)
                return (false, "读不到客户端凭据。请确认游戏已经启动并登录。");

            var state = LcuClient.GetChatState(ep);
            if (state == "unreachable")
                return (false, "连不上客户端。请确认游戏正在运行。");

            if (toOffline)
            {
                if (state == "disconnected")
                    return (true, "聊天本来就是断开的。");

                return LcuClient.Suspend(ep, out var d) ? (true, d) : (false, d);
            }

            if (state == "connected")
                return (true, "聊天本来就是连接的。");

            if (!LcuClient.Resume(ep, out var detail))
                return (false, detail);

            for (var i = 0; i < 12; i++)
            {
                Thread.Sleep(500);
                if (LcuClient.GetChatState(ep) == "connected")
                    return (true, "connected");
            }

            return (false, "已发送恢复指令，但聊天还没连上。可以稍等或重启客户端。");
        }

        private void RefreshState(bool silent)
        {
            Task.Run(() =>
            {
                var root = LcuClient.FindLolRoot(_settings.LolRoot);
                var ep = root == null ? null : LcuClient.FindEndpoint(root);
                var state = LcuClient.GetChatState(ep);

                Post(() => ApplyState(state, silent));
            });
        }

        private void ApplyState(string state, bool silent)
        {
            var changed = state != _chatState;
            if (changed)
                Log.Write("状态变化：" + _chatState + " -> " + state);

            _chatState = state;

            switch (state)
            {
                case "connected":
                    _icon.Icon = BuildIcon(Color.FromArgb(46, 160, 67));
                    _icon.Text = "PartyOffline - 在线（好友能看到你）";
                    _statusItem.Text = "状态：在线";
                    _offlineItem.Enabled = true;
                    _onlineItem.Enabled = false;
                    break;

                case "disconnected":
                    _icon.Icon = BuildIcon(Color.FromArgb(150, 150, 150));
                    _icon.Text = "PartyOffline - 离线（好友看不到你）";
                    _statusItem.Text = "状态：离线";
                    _offlineItem.Enabled = false;
                    _onlineItem.Enabled = true;
                    break;

                case "unreachable":
                    _icon.Icon = BuildIcon(Color.FromArgb(200, 60, 60));
                    _icon.Text = "PartyOffline - 游戏未运行";
                    _statusItem.Text = "状态：游戏未运行";
                    _offlineItem.Enabled = false;
                    _onlineItem.Enabled = false;
                    break;

                default:
                    _icon.Icon = BuildIcon(Color.FromArgb(220, 170, 40));
                    _icon.Text = "PartyOffline - " + state;
                    _statusItem.Text = "状态：" + state;
                    _offlineItem.Enabled = true;
                    _onlineItem.Enabled = true;
                    break;
            }

            if (changed && !silent && state == "connected")
            {
                _icon.ShowBalloonTip(2500, "PartyOffline 已就绪",
                    _settings.HotkeyOffline + " 切离线    " + _settings.HotkeyOnline + " 切在线",
                    ToolTipIcon.Info);
            }
        }

        private static void Post(Action action)
        {
            if (SynchronizationContext.Current != null)
                SynchronizationContext.Current.Post(_ => action(), null);
            else
                action();
        }

        private static Icon BuildIcon(Color color)
        {
            const int size = 16;
            using var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var brush = new SolidBrush(color);
                g.FillEllipse(brush, 1, 1, size - 3, size - 3);
                using var pen = new Pen(Color.FromArgb(70, 70, 70), 1f);
                g.DrawEllipse(pen, 1, 1, size - 3, size - 3);
            }

            var handle = bmp.GetHicon();
            return Icon.FromHandle(handle);
        }

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "PartyOffline";

        private ToolStripMenuItem BuildStartupItem()
        {
            var item = new ToolStripMenuItem("开机自启")
            {
                CheckOnClick = true,
                Checked = IsStartupEnabled()
            };

            item.Click += (s, e) =>
            {
                try
                {
                    SetStartup(item.Checked);
                    Log.Write("开机自启：" + (item.Checked ? "开" : "关"));
                    _icon.ShowBalloonTip(2000, "开机自启",
                        item.Checked ? "已开启" : "已关闭", ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    Log.Write("开机自启设置失败：" + ex.Message);
                    MessageBox.Show("设置失败：" + ex.Message, "PartyOffline");
                    item.Checked = IsStartupEnabled();
                }
            };

            return item;
        }

        private static bool IsStartupEnabled()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                return key?.GetValue(RunValueName) is string;
            }
            catch
            {
                return false;
            }
        }

        private static void SetStartup(bool enabled)
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                            ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (enabled)
            {
                var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                key!.SetValue(RunValueName, "\"" + exe + "\"");
            }
            else
            {
                key!.DeleteValue(RunValueName, false);
            }
        }

        private void OpenLogFolder()
        {
            try
            {
                System.IO.Directory.CreateDirectory(AppSettings.DataDir);
                System.Diagnostics.Process.Start("explorer.exe",
                    "\"" + AppSettings.DataDir + "\"");
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "PartyOffline");
            }
        }

        private void ExitApp()
        {
            _pollTimer.Stop();
            UnregisterHotKey(_window.Handle, HotKeyOfflineId);
            UnregisterHotKey(_window.Handle, HotKeyOnlineId);
            _icon.Visible = false;
            _icon.Dispose();
            _window.DestroyHandle();
            ExitThread();
        }
    }
}
