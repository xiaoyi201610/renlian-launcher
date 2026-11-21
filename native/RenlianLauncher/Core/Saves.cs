using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace RenlianLauncher.Core;

/// <summary>
/// 存档管理：列出「save games」下的存档（含顶层与按分组存放的子目录），
/// 删除走 Windows 回收站（SHFileOperation + FOF_ALLOWUNDO），绝不永久删除。
/// 旧版没有「铁人」标记；这里按存档内 meta/gamestate 的 ironman=yes 做只读探测。
/// </summary>
public static class Saves
{
    /// <summary>存档根目录（与旧版一致：userDir\save games）。</summary>
    public static string RootDir(ConfigStore cfg) =>
        Path.Combine(cfg.GameStr(cfg.CurrentGame, "userDir") ?? "", "save games");

    /// <summary>只读列出存档，按修改时间倒序。</summary>
    public static List<JsonObjectLite> Items(ConfigStore cfg)
    {
        var list = new List<JsonObjectLite>();
        var root = RootDir(cfg);
        var ext = GameDefs.Get(cfg.CurrentGame).SaveExt.ToLowerInvariant();
        if (root.Length == 0 || !Directory.Exists(root)) return list;

        void Add(string path, string group)
        {
            try
            {
                if (!File.Exists(path)) return;
                var fi = new FileInfo(path);
                list.Add(new JsonObjectLite
                {
                    Name = Path.GetFileNameWithoutExtension(path),
                    File = fi.Name,
                    Path = fi.FullName,
                    Group = group,
                    Size = fi.Length,
                    Mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds(),
                    Ironman = DetectIronman(path),
                });
            }
            catch { }
        }

        try
        {
            // CK3 默认直接把 .ck3 放在 save games/ 根目录
            foreach (var f in Directory.EnumerateFiles(root))
            {
                if (f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) Add(f, "");
            }
            // 子文件夹 = 一个「所在集」
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var group = Path.GetFileName(dir);
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir))
                    {
                        if (f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) Add(f, group);
                    }
                }
                catch { }
            }
        }
        catch { }

        return list
            .OrderByDescending(x => x.Mtime)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static object List(ConfigStore cfg)
    {
        var items = Items(cfg);
        return new
        {
            ok = true,
            items = items.Select(x => new
            {
                name = x.Name,
                file = x.File,
                path = x.Path,
                group = x.Group,
                size = x.Size,
                mtime = x.Mtime,
                ironman = x.Ironman,
                isIronman = x.Ironman,
            }).ToArray(),
            count = items.Count,
            dir = RootDir(cfg),
        };
    }

    /// <summary>只数数量（游戏文件页用），不做铁人探测。</summary>
    public static int Count(ConfigStore cfg)
    {
        var root = RootDir(cfg);
        var ext = GameDefs.Get(cfg.CurrentGame).SaveExt.ToLowerInvariant();
        if (root.Length == 0 || !Directory.Exists(root)) return 0;
        var n = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(root))
                if (f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) n++;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir))
                        if (f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) n++;
                }
                catch { }
            }
        }
        catch { }
        return n;
    }

    /// <summary>把勾选的存档移入回收站。返回 done / failed 供前端提示。</summary>
    public static object Delete(ConfigStore cfg, IEnumerable<string> paths)
    {
        var root = RootDir(cfg);
        if (root.Length == 0 || !Directory.Exists(root))
            return new { ok = false, error = "找不到存档目录（save games），请先在设置里确认玩家数据目录" };

        var done = new List<string>();
        var failed = new List<object>();
        foreach (var raw in paths)
        {
            var p = (raw ?? "").Trim();
            if (p.Length == 0) continue;
            string full;
            try { full = Path.GetFullPath(p); }
            catch { failed.Add(new { path = p, reason = "路径无效" }); continue; }

            // 只允许删 save games 目录内的文件，防止前端传错路径
            var rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                failed.Add(new { path = p, reason = "只允许删除存档目录内的文件" });
                continue;
            }
            if (!File.Exists(full))
            {
                failed.Add(new { path = p, reason = "文件不存在" });
                continue;
            }
            try
            {
                if (MoveToRecycleBin(full))
                {
                    done.Add(Path.GetFileName(full));
                }
                else
                {
                    failed.Add(new { path = p, reason = "系统未接受回收站操作（文件可能已被占用）" });
                }
            }
            catch (Exception ex)
            {
                failed.Add(new { path = p, reason = ex.Message });
            }
        }

        return new
        {
            ok = done.Count > 0 && failed.Count == 0,
            done,
            failed,
            error = done.Count == 0 && failed.Count > 0 ? "没有成功删除任何存档" : "",
        };
    }

    // ------------------------------------------------------------ 回收站

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    private static bool MoveToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            // pFrom 必须是「路径\0\0」结尾的双空字符列表
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        var code = SHFileOperation(ref op);
        return code == 0 && !op.fAnyOperationsAborted;
    }

    // ------------------------------------------------------------ 铁人探测

    /// <summary>
    /// 只读探测：CK3 / Stellaris / HOI4 等存档都是 zip，meta（或 gamestate）里
    /// 带 ironman=yes。读不到就返回 false，绝不影响列表。
    /// </summary>
    public static bool DetectIronman(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var magic = new byte[4];
            if (fs.Read(magic, 0, 4) < 4) return false;
            fs.Position = 0;

            if (magic[0] == 0x50 && magic[1] == 0x4B)   // PK.. zip
            {
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
                foreach (var name in new[] { "meta", "gamestate" })
                {
                    var entry = zip.GetEntry(name) ?? zip.Entries.FirstOrDefault(e =>
                        string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));
                    if (entry is null) continue;
                    using var es = entry.Open();
                    if (HasIronman(es)) return true;
                }
                return false;
            }

            // 少数未压缩文本存档：直接扫前 512KB
            return HasIronman(fs);
        }
        catch { return false; }
    }

    private static bool HasIronman(Stream stream)
    {
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: true);
            var buf = new char[64 * 1024];
            var total = 0;
            var tail = "";
            while (total < 1024 * 1024)
            {
                var n = reader.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                var text = tail + new string(buf, 0, n);
                if (text.Contains("ironman=yes", StringComparison.OrdinalIgnoreCase)) return true;
                tail = text.Length > 16 ? text[^16..] : text;
                total += n;
            }
        }
        catch { }
        return false;
    }

    /// <summary>内部精简记录，避免为一个列表创建一大堆匿名对象。</summary>
    public sealed class JsonObjectLite
    {
        public string Name = "";
        public string File = "";
        public string Path = "";
        public string Group = "";
        public long Size;
        public long Mtime;
        public bool Ironman;
    }
}
