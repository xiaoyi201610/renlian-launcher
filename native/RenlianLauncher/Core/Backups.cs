using System.Security.Cryptography;
using System.Text;

namespace RenlianLauncher.Core;

/// <summary>
/// 备份 / 还原：把播放集数据库（launcher-v2.sqlite）和游戏启停清单（dlc_load.json /
/// content_load.json）复制到配置目录下的 backups。
///
/// 与旧版一致：同名标签最多保留 MAX_BACKUPS 份、内容相同的备份不重复生成；
/// 还原前先把当前文件备份成 before-restore-*，出问题还能切回来。
/// </summary>
public static class Backups
{
    public const int MaxBackups = 12;

    public static string Dir => Paths.BackupDir;

    /// <summary>按修改时间倒序列出全部备份。</summary>
    public static object List()
    {
        var raw = new List<(string Name, long Size, long Mtime, string Tag)>();
        long total = 0;
        try
        {
            if (Directory.Exists(Dir))
            {
                foreach (var f in Directory.EnumerateFiles(Dir))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        total += fi.Length;
                        raw.Add((fi.Name, fi.Length,
                            new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds(), TagOf(fi.Name)));
                    }
                    catch { }
                }
            }
        }
        catch { }

        var items = raw
            .OrderByDescending(x => x.Mtime)
            .Select(x => new { name = x.Name, size = x.Size, mtime = x.Mtime, tag = x.Tag })
            .ToArray();
        return new { ok = true, items, total, dir = Dir, max = MaxBackups };
    }

    /// <summary>立即备份当前数据库 + 启停清单（min_interval=0，玩家手动点必须生成）。</summary>
    public static object Make(ConfigStore cfg)
    {
        var made = new List<string>();
        var skipped = new List<string>();

        var db = PlaysetDb.ForConfig(cfg);
        if (db.Exists)
        {
            var tag = Path.GetFileName(db.DbPath);
            tag = tag.StartsWith("launcher-v2", StringComparison.OrdinalIgnoreCase)
                ? "launcher-v2" : "launcher";
            var f = BackupFile(db.DbPath, tag, 0);
            if (f is null) skipped.Add(tag);
            else made.Add(Path.GetFileName(f));
        }

        var loadPath = LoadPath(cfg);
        if (loadPath.Length > 0 && File.Exists(loadPath))
        {
            var tag = GameDefs.ContentLoad.Contains(cfg.CurrentGame) ? "content_load" : "dlc_load";
            var f = BackupFile(loadPath, tag, 0);
            if (f is null) skipped.Add(tag);
            else made.Add(Path.GetFileName(f));
        }

        return new
        {
            ok = true,
            files = made,
            skipped,
            dir = Dir,
            error = made.Count == 0 && skipped.Count == 0
                ? "没有可备份的文件（找不到 launcher-v2.sqlite 或启停清单）"
                : "",
        };
    }

    /// <summary>把指定备份复制回游戏数据目录（同名文件先自动备份）。</summary>
    public static object Restore(ConfigStore cfg, string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return new { ok = false, error = "缺少备份文件名" };

        // 只认 backups 目录内的文件名，防止 ../ 越界
        var baseName = Path.GetFileName(name);
        var src = Path.Combine(Dir, baseName);
        if (!File.Exists(src)) return new { ok = false, error = "备份文件不存在" };

        var userDir = cfg.GameStr(cfg.CurrentGame, "userDir") ?? "";
        if (string.IsNullOrWhiteSpace(userDir))
            return new { ok = false, error = "找不到玩家数据目录，请先在设置里指定" };

        string dstName;
        if (baseName.StartsWith("launcher-v2", StringComparison.OrdinalIgnoreCase))
            dstName = "launcher-v2.sqlite";
        else if (baseName.StartsWith("launcher", StringComparison.OrdinalIgnoreCase))
            dstName = "launcher.sqlite";
        else if (baseName.StartsWith("dlc_load", StringComparison.OrdinalIgnoreCase))
            dstName = "dlc_load.json";
        else if (baseName.StartsWith("content_load", StringComparison.OrdinalIgnoreCase))
            dstName = "content_load.json";
        else
            return new { ok = false, error = "无法识别的备份类型" };

        var dst = Path.Combine(userDir, dstName);
        try
        {
            if (File.Exists(dst)) BackupFile(dst, "before-restore", 0);
            File.Copy(src, dst, overwrite: true);
            return new { ok = true, restored = dstName, dir = userDir };
        }
        catch (UnauthorizedAccessException)
        {
            return new { ok = false, error = "没有写入权限：官方启动器或游戏可能正在运行，请先关闭后重试" };
        }
        catch (IOException ex)
        {
            var code = ex.HResult & 0xFFFF;
            return new
            {
                ok = false,
                error = code is 32 or 5
                    ? "文件被占用或没有权限：请关闭游戏 / 官方启动器后再恢复"
                    : "恢复失败：" + ex.Message,
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = "恢复失败：" + ex.Message };
        }
    }

    /// <summary>清空备份；传 tag 只清某一类（旧版按文件名前缀过滤）。</summary>
    public static object Clear(string? tag)
    {
        var removed = 0;
        try
        {
            if (Directory.Exists(Dir))
            {
                foreach (var f in Directory.EnumerateFiles(Dir))
                {
                    if (!string.IsNullOrWhiteSpace(tag) &&
                        !Path.GetFileName(f).StartsWith(tag!, StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        File.Delete(f);
                        removed++;
                    }
                    catch { }
                }
            }
        }
        catch { }
        return new { ok = true, removed };
    }

    // ------------------------------------------------------------ 内部

    /// <summary>当前游戏实际使用的启停清单路径。</summary>
    private static string LoadPath(ConfigStore cfg)
    {
        var userDir = cfg.GameStr(cfg.CurrentGame, "userDir") ?? "";
        if (string.IsNullOrWhiteSpace(userDir)) return "";
        return Path.Combine(userDir,
            GameDefs.ContentLoad.Contains(cfg.CurrentGame) ? "content_load.json" : "dlc_load.json");
    }

    private static string TagOf(string name)
    {
        if (name.StartsWith("before-restore", StringComparison.OrdinalIgnoreCase)) return "before-restore";
        if (name.StartsWith("launcher-v2", StringComparison.OrdinalIgnoreCase)) return "launcher-v2";
        if (name.StartsWith("launcher", StringComparison.OrdinalIgnoreCase)) return "launcher";
        if (name.StartsWith("dlc_load", StringComparison.OrdinalIgnoreCase)) return "dlc_load";
        if (name.StartsWith("content_load", StringComparison.OrdinalIgnoreCase)) return "content_load";
        return "other";
    }

    /// <summary>备份单个文件；内容与最新一份相同则跳过（返回 null）。</summary>
    private static string? BackupFile(string src, string tag, int minIntervalSeconds)
    {
        if (!File.Exists(src)) return null;
        try
        {
            Directory.CreateDirectory(Dir);
            var existing = Directory.EnumerateFiles(Dir, tag + "-*")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            if (minIntervalSeconds > 0 && existing.Count > 0 &&
                (DateTime.UtcNow - existing[0].LastWriteTimeUtc).TotalSeconds < minIntervalSeconds)
                return null;

            var sig = QuickSig(src);
            if (existing.Count > 0 && sig is not null && QuickSig(existing[0].FullName) == sig)
                return null;

            var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var ext = Path.GetExtension(src);
            var dst = Path.Combine(Dir, $"{tag}-{ts}{ext}");
            var n = 1;
            while (File.Exists(dst))
            {
                n++;
                dst = Path.Combine(Dir, $"{tag}-{ts}-{n}{ext}");
            }

            File.Copy(src, dst, overwrite: false);
            Prune(tag);
            return dst;
        }
        catch { return null; }
    }

    /// <summary>同标签只保留最新 MAX_BACKUPS 份。</summary>
    private static void Prune(string tag)
    {
        try
        {
            var files = Directory.EnumerateFiles(Dir, tag + "-*")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            foreach (var f in files.Skip(MaxBackups))
            {
                try { f.Delete(); } catch { }
            }
        }
        catch { }
    }

    /// <summary>大小 + 首尾各 64KB 的 SHA256，判断两份大文件是否相同。</summary>
    private static string? QuickSig(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var total = fs.Length;
            using var sha = SHA256.Create();
            using var ms = new MemoryStream();
            var head = new byte[64 * 1024];
            var n = fs.Read(head, 0, head.Length);
            ms.Write(head, 0, n);
            if (total > head.Length)
            {
                fs.Position = Math.Max(head.Length, total - head.Length);
                var tail = new byte[64 * 1024];
                var m = fs.Read(tail, 0, tail.Length);
                ms.Write(tail, 0, m);
            }
            ms.Write(Encoding.UTF8.GetBytes("|" + total));
            ms.Position = 0;
            return Convert.ToHexString(sha.ComputeHash(ms));
        }
        catch { return null; }
    }
}
