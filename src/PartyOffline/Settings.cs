using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace PartyOffline
{
    /// <summary>一个热键：修饰键 + 虚拟键码。</summary>
    internal sealed class HotkeySpec
    {
        internal const uint ModAlt = 0x0001;
        internal const uint ModControl = 0x0002;
        internal const uint ModShift = 0x0004;
        internal const uint ModWin = 0x0008;

        internal uint Modifiers;
        internal uint VirtualKey;

        internal HotkeySpec(uint modifiers, uint vk)
        {
            Modifiers = modifiers;
            VirtualKey = vk;
        }

        /// <summary>是否至少带一个修饰键。不带修饰键会抢占全局按键，不允许。</summary>
        internal bool HasModifier =>
            (Modifiers & (ModAlt | ModControl | ModShift | ModWin)) != 0;

        /// <summary>Win32 RegisterHotKey 用的修饰键（含不重复触发）。</summary>
        internal uint Win32Modifiers => Modifiers | 0x4000; // MOD_NOREPEAT

        /// <summary>解析 "Ctrl+Alt+1" 这类文本。失败返回 null。</summary>
        internal static HotkeySpec? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var parts = text!.Split(new[] { '+', '-' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => p.Trim())
                            .Where(p => p.Length > 0)
                            .ToList();
            if (parts.Count == 0)
                return null;

            uint mods = 0;
            uint vk = 0;
            var sawKey = false;

            foreach (var part in parts)
            {
                var p = part.ToLowerInvariant();
                switch (p)
                {
                    case "ctrl":
                    case "control":
                        mods |= ModControl;
                        continue;
                    case "alt":
                        mods |= ModAlt;
                        continue;
                    case "shift":
                        mods |= ModShift;
                        continue;
                    case "win":
                    case "windows":
                        mods |= ModWin;
                        continue;
                }

                if (sawKey)
                    return null; // 出现了两个主键

                if (TryParseKey(part, out vk))
                    sawKey = true;
                else
                    return null;
            }

            if (!sawKey || vk == 0)
                return null;

            return new HotkeySpec(mods, vk);
        }

        /// <summary>把 Keys 转成可显示文本。</summary>
        internal static string Describe(Keys key, uint modifiers)
        {
            var sb = new StringBuilder();
            if ((modifiers & ModControl) != 0) sb.Append("Ctrl+");
            if ((modifiers & ModAlt) != 0) sb.Append("Alt+");
            if ((modifiers & ModShift) != 0) sb.Append("Shift+");
            if ((modifiers & ModWin) != 0) sb.Append("Win+");
            sb.Append(KeyName((uint)key));
            return sb.ToString();
        }

        public override string ToString() => Describe((Keys)VirtualKey, Modifiers);

        private static bool TryParseKey(string part, out uint vk)
        {
            vk = 0;
            var s = part.Trim();

            if (s.Length == 1)
            {
                var c = char.ToUpperInvariant(s[0]);
                if (c >= '0' && c <= '9') { vk = (uint)c; return true; }
                if (c >= 'A' && c <= 'Z') { vk = (uint)c; return true; }
                return false;
            }

            if (s.Length == 2 && (s[0] == 'F' || s[0] == 'f'))
            {
                if (int.TryParse(s.Substring(1), out var fn) && fn >= 1 && fn <= 24)
                {
                    vk = (uint)(0x70 + fn - 1);
                    return true;
                }
                return false;
            }

            // 少数常用特殊键
            switch (s.ToLowerInvariant())
            {
                case "space": vk = 0x20; return true;
                case "tab": vk = 0x09; return true;
                case "insert": vk = 0x2D; return true;
                case "delete": vk = 0x2E; return true;
                case "home": vk = 0x24; return true;
                case "end": vk = 0x23; return true;
                case "pageup": vk = 0x21; return true;
                case "pagedown": vk = 0x22; return true;
                case "`":
                case "tilde": vk = 0xC0; return true;
            }

            return false;
        }

        private static string KeyName(uint vk)
        {
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);

            switch (vk)
            {
                case 0x20: return "Space";
                case 0x09: return "Tab";
                case 0x2D: return "Insert";
                case 0x2E: return "Delete";
                case 0x24: return "Home";
                case 0x23: return "End";
                case 0x21: return "PageUp";
                case 0x22: return "PageDown";
                case 0xC0: return "`";
                default: return "0x" + vk.ToString("X2");
            }
        }
    }

    /// <summary>程序设置，存在 %APPDATA%\LOLDeceive\settings.ini。</summary>
    internal sealed class AppSettings
    {
        private const int DefaultMaxLogKb = 1024;

        internal string? LolRoot;
        internal HotkeySpec HotkeyOffline = new HotkeySpec(
            HotkeySpec.ModControl | HotkeySpec.ModAlt, 0x31); // Ctrl+Alt+1
        internal HotkeySpec HotkeyOnline = new HotkeySpec(
            HotkeySpec.ModControl | HotkeySpec.ModAlt, 0x32); // Ctrl+Alt+2
        internal int MaxLogKb = DefaultMaxLogKb;

        internal static string DataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LOLDeceive");

        internal static string SettingsPath => Path.Combine(DataDir, "settings.ini");

        /// <summary>旧版本只存了一行安装路径，这里做一次迁移。</summary>
        private static string LegacyConfigPath => Path.Combine(DataDir, "party-offline.cfg");

        internal static AppSettings Load()
        {
            var s = new AppSettings();

            try
            {
                if (File.Exists(SettingsPath))
                {
                    foreach (var line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                    {
                        var t = line.Trim();
                        if (t.Length == 0 || t.StartsWith("#") || t.StartsWith(";"))
                            continue;

                        var idx = t.IndexOf('=');
                        if (idx <= 0)
                            continue;

                        var key = t.Substring(0, idx).Trim().ToLowerInvariant();
                        var value = t.Substring(idx + 1).Trim();

                        switch (key)
                        {
                            case "lolroot":
                                if (value.Length > 0) s.LolRoot = value;
                                break;
                            case "hotkeyoffline":
                                var off = HotkeySpec.Parse(value);
                                if (off != null) s.HotkeyOffline = off;
                                break;
                            case "hotkeyonline":
                                var on = HotkeySpec.Parse(value);
                                if (on != null) s.HotkeyOnline = on;
                                break;
                            case "maxlogkb":
                                if (int.TryParse(value, out var kb) && kb >= 64 && kb <= 65536)
                                    s.MaxLogKb = kb;
                                break;
                        }
                    }
                }
                else if (File.Exists(LegacyConfigPath))
                {
                    var legacy = File.ReadAllText(LegacyConfigPath).Trim();
                    if (legacy.Length > 0)
                        s.LolRoot = legacy;
                    s.Save();
                }
            }
            catch (Exception e)
            {
                Log.Write("读取设置失败：" + e.Message);
            }

            return s;
        }

        internal void Save()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var sb = new StringBuilder();
                sb.AppendLine("# LOLPartyOffline 设置");
                sb.AppendLine("# 热键写法：Ctrl+Alt+1 / Ctrl+Shift+F9 之类，必须带修饰键");
                sb.AppendLine();
                sb.AppendLine("lolRoot=" + (LolRoot ?? string.Empty));
                sb.AppendLine("hotkeyOffline=" + HotkeyOffline);
                sb.AppendLine("hotkeyOnline=" + HotkeyOnline);
                sb.AppendLine("maxLogKb=" + MaxLogKb);

                File.WriteAllText(SettingsPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Log.Write("保存设置失败：" + e.Message);
            }
        }

        internal void SaveLolRoot(string root)
        {
            if (string.Equals(LolRoot, root, StringComparison.OrdinalIgnoreCase))
                return;
            LolRoot = root;
            Save();
        }
    }
}
