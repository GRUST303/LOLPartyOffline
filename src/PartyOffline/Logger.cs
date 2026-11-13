using System;
using System.IO;
using System.Text;

namespace PartyOffline
{
    /// <summary>
    ///     带容量上限的日志。
    ///
    ///     旧版本没有任何日志；这里补上，但同时必须封顶，否则会一直吃硬盘。
    ///     策略：单个文件超过上限就滚动一次，只保留 1 份历史，
    ///     所以磁盘占用最多约为上限的 2 倍（默认 1MB → 最多约 2MB）。
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static int _maxBytes = 1024 * 1024;
        private static bool _ready;

        internal static string Directory => AppSettings.DataDir;

        internal static string CurrentPath => Path.Combine(Directory, "party-offline.log");

        internal static string ArchivePath => Path.Combine(Directory, "party-offline.old.log");

        /// <summary>按设置初始化（上限单位 KB）。</summary>
        internal static void Init(int maxKb)
        {
            lock (Gate)
            {
                if (maxKb < 64) maxKb = 64;
                _maxBytes = maxKb * 1024;
                _ready = true;

                try
                {
                    System.IO.Directory.CreateDirectory(Directory);
                }
                catch
                {
                    // 目录建不了也不能让程序挂掉
                }
            }
        }

        internal static void Write(string message)
        {
            lock (Gate)
            {
                if (!_ready)
                    Init(1024);

                try
                {
                    var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                               + "  " + message + Environment.NewLine;
                    var bytes = Encoding.UTF8.GetByteCount(line);

                    // 写之前先判断会不会超限，超了就滚动
                    if (File.Exists(CurrentPath))
                    {
                        var size = new FileInfo(CurrentPath).Length;
                        if (size + bytes > _maxBytes)
                            Rotate();
                    }

                    File.AppendAllText(CurrentPath, line, new UTF8Encoding(false));
                }
                catch
                {
                    // 日志写失败绝不影响主流程
                }
            }
        }

        private static void Rotate()
        {
            try
            {
                if (File.Exists(ArchivePath))
                    File.Delete(ArchivePath);

                if (File.Exists(CurrentPath))
                    File.Move(CurrentPath, ArchivePath);
            }
            catch
            {
                // 滚动失败就退化成继续追加，最多略超上限
            }
        }

        /// <summary>当前磁盘占用（字节），用于设置界面显示。</summary>
        internal static long Footprint()
        {
            long total = 0;
            try
            {
                if (File.Exists(CurrentPath)) total += new FileInfo(CurrentPath).Length;
                if (File.Exists(ArchivePath)) total += new FileInfo(ArchivePath).Length;
            }
            catch
            {
                // 忽略
            }
            return total;
        }

        /// <summary>清空日志（设置界面提供）。</summary>
        internal static void Clear()
        {
            lock (Gate)
            {
                try
                {
                    if (File.Exists(CurrentPath)) File.Delete(CurrentPath);
                    if (File.Exists(ArchivePath)) File.Delete(ArchivePath);
                    Write("日志已清空");
                }
                catch
                {
                    // 忽略
                }
            }
        }
    }
}
