using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PartyOffline
{
    /// <summary>
    ///     与国服客户端通信。
    ///
    ///     关键事实（已实测确认）：
    ///     · 聊天由 **Riot Client** 的 LCU 管理，不是 LeagueClient。
    ///     · 端点与令牌可以从 LeagueClient 启动日志里的
    ///       `--riotclient-auth-token=` / `--riotclient-app-port=` 拿到。
    ///     · 国服这个构建**不写 lockfile**（日志里报 length: 0），
    ///       所以只能从日志解析，不能走 lockfile。
    ///     · 断开聊天用 POST /chat/v1/suspend，body 必须带 config：
    ///       {"disconnect":true,"reconnects":false}
    ///       用 /chat/v1/session/disconnect 会返回 404（正式服禁用了该调试端点）。
    /// </summary>
    internal static class LcuClient
    {
        private static readonly HttpClient Http;

        static LcuClient()
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (a, b, c, d) => true
            };
            Http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        }

        internal sealed class Endpoint
        {
            public string BaseUrl = string.Empty;
            public string Token = string.Empty;
            public int Port;
        }

        /// <summary>
        ///     定位英雄联盟安装根目录。
        ///     preferred 来自设置文件；其余是注册表与常见路径的兜底。
        /// </summary>
        internal static string? FindLolRoot(string? preferred)
        {
            if (!string.IsNullOrWhiteSpace(preferred) && IsLolRoot(preferred!))
                return preferred;

            foreach (var probe in RegistryProbes())
            {
                if (probe != null && IsLolRoot(probe))
                    return probe;
            }

            foreach (var c in CommonCandidates())
            {
                if (IsLolRoot(c))
                    return c;
            }

            return null;
        }

        /// <summary>
        ///     扫描各固定磁盘上的常见安装位置。
        ///     不写死盘符，任何人装在任何盘都能找到。
        /// </summary>
        private static IEnumerable<string> CommonCandidates()
        {
            DriveInfo[] drives;
            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch
            {
                yield break;
            }

            foreach (var d in drives)
            {
                bool usable;
                try
                {
                    usable = d.DriveType == DriveType.Fixed && d.IsReady;
                }
                catch
                {
                    usable = false;
                }

                if (!usable)
                    continue;

                string root;
                try
                {
                    root = d.RootDirectory.FullName;
                }
                catch
                {
                    continue;
                }

                yield return Path.Combine(root, @"WeGameApps\LOL");
                yield return Path.Combine(root, @"Games\LOL");
                yield return Path.Combine(root, @"Program Files\WeGameApps\LOL");
                yield return Path.Combine(root, @"Program Files (x86)\WeGameApps\LOL");
                yield return Path.Combine(root, @"Program Files\腾讯游戏\英雄联盟");
                yield return Path.Combine(root, @"Program Files (x86)\腾讯游戏\英雄联盟");
            }
        }

        private static IEnumerable<string?> RegistryProbes()
        {
            yield return ReadReg(Registry.LocalMachine,
                @"SOFTWARE\WOW6432Node\Tencent\LOL", "InstallPath");
            yield return ReadReg(Registry.LocalMachine,
                @"SOFTWARE\Tencent\LOL", "InstallPath");
            yield return ReadReg(Registry.CurrentUser,
                @"Software\Tencent\LOL", "InstallPath");
            yield return ReadReg(Registry.LocalMachine,
                @"SOFTWARE\WOW6432Node\Tencent\WeGame", "InstallPath");
        }

        private static string? ReadReg(RegistryKey root, string path, string value)
        {
            try
            {
                using var key = root.OpenSubKey(path, false);
                var v = key?.GetValue(value) as string;
                if (string.IsNullOrWhiteSpace(v))
                    return null;
                return v!.TrimEnd('\\');
            }
            catch
            {
                return null;
            }
        }

        private static bool IsLolRoot(string path)
        {
            try
            {
                return File.Exists(Path.Combine(path, @"Game\Logs")) ||
                       Directory.Exists(Path.Combine(path, @"Game\Logs")) ||
                       File.Exists(Path.Combine(path, @"TCLS\client.exe"));
            }
            catch
            {
                return false;
            }
        }

        private static string LogDirectory(string lolRoot) =>
            Path.Combine(lolRoot, @"Game\Logs\LeagueClient Logs");

        /// <summary>从最新的 LeagueClient 日志里解析 Riot Client 的端口和令牌。</summary>
        internal static Endpoint? FindEndpoint(string lolRoot)
        {
            var dir = LogDirectory(lolRoot);
            if (!Directory.Exists(dir))
                return null;

            FileInfo? latest;
            try
            {
                latest = new DirectoryInfo(dir).GetFiles("*LeagueClient.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }

            if (latest == null)
                return null;

            string? line = null;
            try
            {
                // 日志可能正被写入，必须用共享模式读
                using var fs = new FileStream(latest.FullName, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                for (var i = 0; i < 40 && !sr.EndOfStream; i++)
                {
                    var l = sr.ReadLine();
                    if (l != null && l.Contains("riotclient-auth-token="))
                    {
                        line = l;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine("ReadEndpoint failed: " + e.Message);
                return null;
            }

            if (line == null)
                return null;

            var tokenMatch = Regex.Match(line, @"riotclient-auth-token=([^\s]+)");
            var portMatch = Regex.Match(line, @"riotclient-app-port=(\d+)");
            if (!tokenMatch.Success || !portMatch.Success)
                return null;

            if (!int.TryParse(portMatch.Groups[1].Value, out var port))
                return null;

            return new Endpoint
            {
                Token = tokenMatch.Groups[1].Value,
                Port = port,
                BaseUrl = "https://127.0.0.1:" + port
            };
        }

        private static HttpRequestMessage Authorized(HttpMethod method, Endpoint ep, string path)
        {
            var req = new HttpRequestMessage(method, ep.BaseUrl + path);
            var raw = Encoding.ASCII.GetBytes("riot:" + ep.Token);
            req.Headers.TryAddWithoutValidation("Authorization",
                "Basic " + Convert.ToBase64String(raw));
            return req;
        }

        /// <summary>读取聊天状态：connected / disconnected / connecting / unreachable。</summary>
        internal static string GetChatState(Endpoint? ep)
        {
            if (ep == null)
                return "unreachable";

            try
            {
                using var req = Authorized(HttpMethod.Get, ep, "/chat/v1/session");
                using var resp = Http.SendAsync(req).Result;
                if (!resp.IsSuccessStatusCode)
                    return "unreachable";

                var body = resp.Content.ReadAsStringAsync().Result;
                var m = Regex.Match(body, "\"state\"\\s*:\\s*\"([^\"]+)\"");
                return m.Success ? m.Groups[1].Value : "unknown";
            }
            catch
            {
                return "unreachable";
            }
        }

        /// <summary>断开聊天：好友看不到你在线，队伍不解散，且不会自动重连。</summary>
        internal static bool Suspend(Endpoint ep, out string detail)
        {
            return Post(ep, "/chat/v1/suspend",
                "{\"disconnect\":true,\"reconnects\":false}", out detail);
        }

        /// <summary>恢复聊天。</summary>
        internal static bool Resume(Endpoint ep, out string detail)
        {
            return Post(ep, "/chat/v1/resume", null, out detail);
        }

        private static bool Post(Endpoint ep, string path, string? json, out string detail)
        {
            try
            {
                using var req = Authorized(HttpMethod.Post, ep, path);
                if (json != null)
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using var resp = Http.SendAsync(req).Result;
                var body = resp.Content.ReadAsStringAsync().Result;

                if (resp.IsSuccessStatusCode)
                {
                    detail = "HTTP " + (int)resp.StatusCode;
                    return true;
                }

                if (body.Length > 220)
                    body = body.Substring(0, 220);
                detail = "HTTP " + (int)resp.StatusCode + " " + body;
                return false;
            }
            catch (Exception e)
            {
                detail = e.Message;
                return false;
            }
        }
    }
}
