using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RenlianLauncher.Core;

/// <summary>
/// 路径探测层 —— 这是 v2 重写的重点。
///
/// 旧版只认 %USERPROFILE%\Documents，因此下面这些用户的「数据目录」永远找不到：
///   · 把「文档」移到了 D 盘 / 其它分区
///   · 开了 OneDrive 文档备份（真实目录在 OneDrive 下，甚至带「文档」中文名）
///   · 用系统安装时改过用户目录
/// 现在按优先级依次尝试：
///   1. Windows Known Folder（SHGetKnownFolderPath）—— 系统权威答案
///   2. 注册表 User Shell Folders\Personal —— 覆盖被重定向的「文档」
///   3. %USERPROFILE%\Documents / OneDrive 各种变体
///   4. 所有固定磁盘浅层扫描 "Paradox Interactive" / "Colossal Order"
/// </summary>
public static class Paths
{
    // ------------------------------------------------------------ 程序目录

    /// <summary>真正的程序文件目录。runtime 布局下是 release\runtime。</summary>
    public static string CoreDir { get; } =
        Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');

    /// <summary>
    /// 启动器根目录。为了目录清爽，程序文件都放在 runtime 子目录里，
    /// 这种情况下根目录就是 runtime 的父目录；普通布局下与 CoreDir 相同。
    /// </summary>
    public static string ExeDir { get; } = ResolveAppRoot();

    /// <summary>可写根目录：exe 同目录不可写时回退到 %LOCALAPPDATA%。</summary>
    public static string Root { get; } = ResolveRoot();

    /// <summary>
    /// 运行数据根目录。所有启动器自己产生的东西都收在 RenlianData 下，
    /// 不再和 exe / dll / wwwroot 混在一起：
    ///   RenlianData\config\      配置
    ///   RenlianData\logs\        日志
    ///   RenlianData\data\        别名、收藏夹、DLC 中文名等小型数据
    ///   RenlianData\cache\       百科 / 图标 / 缩略图 / mod 索引 / WebView2 缓存
    ///   RenlianData\backups\     备份
    ///   RenlianData\tmp\         导入解压等临时文件
    /// </summary>
    public static string DataDir { get; } = Path.Combine(Root, "RenlianData");

    public static string ConfigDir { get; } = Path.Combine(DataDir, "config");
    public static string LogDir { get; } = Path.Combine(DataDir, "logs");
    public static string MetaDir { get; } = Path.Combine(DataDir, "data");
    public static string BackupDir { get; } = Path.Combine(DataDir, "backups");
    public static string TmpDir { get; } = Path.Combine(DataDir, "tmp");

    /// <summary>兼容补丁文件目录。启动器只管理、不生成也不注入任何补丁文件。</summary>
    public static string PatchesDir { get; } = Path.Combine(DataDir, "patches");

    /// <summary>缓存根目录。</summary>
    public static string CacheDir { get; } = Path.Combine(DataDir, "cache");
    public static string WikiCacheDir { get; } = Path.Combine(CacheDir, "wiki");
    public static string IconCacheDir { get; } = Path.Combine(CacheDir, "icons");
    public static string ThumbCacheDir { get; } = Path.Combine(CacheDir, "thumbs");
    public static string ModIndexCacheDir { get; } = Path.Combine(CacheDir, "mod-index");
    public static string AchievementCacheDir { get; } = Path.Combine(CacheDir, "achievements");
    public static string DlcCacheDir { get; } = Path.Combine(CacheDir, "dlc");
    public static string WebViewCacheDir { get; } = Path.Combine(CacheDir, "webview2");

    public static string ConfigPath { get; } = Path.Combine(ConfigDir, "launcher-config.json");
    public static string LogPath { get; } = Path.Combine(LogDir, "启动日志.log");

    /// <summary>静态资源目录（wwwroot，随程序文件一起发布）。</summary>
    public static string WebRoot { get; } = Path.Combine(CoreDir, "wwwroot");

    private static string ResolveAppRoot()
    {
        if (string.Equals(Path.GetFileName(CoreDir), "runtime", StringComparison.OrdinalIgnoreCase)
            && Directory.GetParent(CoreDir) is { } parent)
            return parent.FullName.TrimEnd('\\');
        return CoreDir;
    }

    private static string ResolveRoot()
    {
        if (IsWritable(ExeDir)) return ExeDir;
        var fb = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RenlianLauncher");
        try { Directory.CreateDirectory(fb); } catch { /* 交给最后兜底 */ }
        return fb;
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".write-probe-" + Guid.NewGuid().ToString("N")[..8]);
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                fs.WriteByte(0);
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>配置目录是否落在 exe 旁边（false 表示已回退到 LOCALAPPDATA）。</summary>
    public static bool DataDirIsPortable =>
        string.Equals(Root.TrimEnd('\\'), ExeDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>确保兼容补丁目录存在后，用资源管理器打开它。</summary>
    public static object OpenPatchesDir()
    {
        try
        {
            Directory.CreateDirectory(PatchesDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{PatchesDir}\"") { UseShellExecute = true });
            return new { ok = true, path = PatchesDir };
        }
        catch (Exception ex)
        {
            return new { ok = false, path = PatchesDir, error = "打开兼容补丁目录失败：" + ex.Message };
        }
    }

    // ------------------------------------------------------------ 数据目录初始化

    private static readonly object LayoutLock = new();
    private static bool _layoutReady;

    /// <summary>创建全部数据子目录，并把旧版散落在根目录的文件搬进来（只搬一次，不覆盖已有文件）。</summary>
    public static void EnsureLayout()
    {
        if (_layoutReady) return;
        lock (LayoutLock)
        {
            if (_layoutReady) return;
            foreach (var d in new[]
                     {
                         DataDir, ConfigDir, LogDir, MetaDir, BackupDir, TmpDir, PatchesDir,
                         CacheDir, WikiCacheDir, IconCacheDir, ThumbCacheDir,
                         ModIndexCacheDir, AchievementCacheDir, DlcCacheDir, WebViewCacheDir,
                     })
            {
                try { Directory.CreateDirectory(d); } catch { }
            }
            try { MigrateLegacy(); } catch { }
            _layoutReady = true;
        }
    }

    // 旧版直接放在 exe 旁边的文件 / 通配符 → 对应新目录
    private static readonly (string pattern, string dir)[] LegacyFiles =
    {
        ("launcher-config.json", ConfigDir),
        ("启动日志.log", LogDir),
        ("wiki_cache_*.json", WikiCacheDir),
        ("achievement_data_*.json", AchievementCacheDir),
        ("achievement_global_*.json", AchievementCacheDir),
        ("mod-index-*.json", ModIndexCacheDir),
        ("dlc_detail_cache.json", DlcCacheDir),
        ("mod-aliases.json", MetaDir),
        ("mod-source-overrides.json", MetaDir),
        ("mod-collections.json", MetaDir),
        ("dlc-names-cn.json", MetaDir),
        ("eu4-force-order.json", MetaDir),
        ("update-check.json", MetaDir),
        ("使用教程.html", MetaDir),
    };

    private static readonly (string name, string dir)[] LegacyDirs =
    {
        ("thumb_cache", ThumbCacheDir),
        ("webview2", WebViewCacheDir),
        ("achievement_icons", Path.Combine(IconCacheDir, "achievement_icons")),
        ("hoi4_focus_icons", Path.Combine(IconCacheDir, "hoi4_focus")),
        ("eu4_mission_icons", Path.Combine(IconCacheDir, "eu4_mission")),
        ("vic3_law_icons", Path.Combine(IconCacheDir, "vic3_law")),
        ("backups", BackupDir),
        ("tmp-import", TmpDir),
    };

    /// <summary>把旧版留在 Root 下的运行数据搬进 RenlianData，保持根目录干净。</summary>
    private static void MigrateLegacy()
    {
        foreach (var (pattern, dir) in LegacyFiles)
        {
            var wildcard = pattern.Contains('*') || pattern.Contains('?');
            IEnumerable<string> files;
            try
            {
                files = wildcard
                    ? Directory.EnumerateFiles(Root, pattern, SearchOption.TopDirectoryOnly)
                    : (File.Exists(Path.Combine(Root, pattern))
                        ? new[] { Path.Combine(Root, pattern) }
                        : Array.Empty<string>());
            }
            catch { continue; }

            foreach (var f in files)
            {
                try
                {
                    var dest = Path.Combine(dir, Path.GetFileName(f));
                    Directory.CreateDirectory(dir);
                    if (File.Exists(dest))
                    {
                        // 新位置已有同名文件，说明这份是旧位置残留的过期副本，直接清掉
                        File.Delete(f);
                        continue;
                    }
                    File.Move(f, dest);
                }
                catch { /* 被占用就留着，下次再搬 */ }
            }
        }

        foreach (var (name, dir) in LegacyDirs)
        {
            var src = Path.Combine(Root, name);
            if (!Directory.Exists(src)) continue;
            try { MergeMove(src, dir); } catch { }
        }
    }

    private static void MergeMove(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            try
            {
                var rel = Path.GetRelativePath(src, f);
                var target = Path.Combine(dest, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!File.Exists(target)) File.Move(f, target);
            }
            catch { }
        }
        try
        {
            // 从最深的目录往回删，嵌套的空目录才能一起清掉
            foreach (var d in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories)
                         .OrderByDescending(x => x.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
            }
            if (!Directory.EnumerateFileSystemEntries(src).Any()) Directory.Delete(src);
        }
        catch { }
    }

    // ------------------------------------------------------------ 文档目录

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    private static readonly Guid FolderIdDocuments = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");

    private static string? KnownDocuments()
    {
        IntPtr p = IntPtr.Zero;
        try
        {
            var id = FolderIdDocuments;
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out p) == 0 && p != IntPtr.Zero)
            {
                var s = Marshal.PtrToStringUni(p);
                if (!string.IsNullOrWhiteSpace(s) && Directory.Exists(s)) return s;
            }
        }
        catch { /* 老系统 / 权限问题，走下面的兜底 */ }
        finally
        {
            if (p != IntPtr.Zero) CoTaskMemFree(p);
        }
        return null;
    }

    private static string? RegistryDocuments()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            var raw = k?.GetValue("Personal") as string;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var p = Environment.ExpandEnvironmentVariables(raw);
            return Directory.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    /// <summary>所有可能的「文档」目录，已去重。</summary>
    public static IReadOnlyList<string> DocumentsDirs()
    {
        var list = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            var full = Path.GetFullPath(p).TrimEnd('\\');
            if (!list.Contains(full, StringComparer.OrdinalIgnoreCase)) list.Add(full);
        }

        Add(KnownDocuments());
        Add(RegistryDocuments());

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(Path.Combine(home, "Documents"));
        foreach (var sub in new[] { "OneDrive", @"OneDrive\Documents", @"OneDrive\文档" })
            Add(Path.Combine(home, sub));

        // OneDrive 商业版目录名形如 OneDrive - 公司名
        try
        {
            foreach (var d in Directory.EnumerateDirectories(home, "OneDrive*"))
            {
                Add(Path.Combine(d, "Documents"));
                Add(Path.Combine(d, "文档"));
            }
        }
        catch { }

        return list;
    }

    // ------------------------------------------------------------ Paradox 用户数据

    private static readonly Lazy<List<string>> _paradoxRoots = new(FindParadoxRoots);

    /// <summary>本机所有 Paradox Interactive / Colossal Order 数据根目录。</summary>
    public static IReadOnlyList<string> ParadoxRoots() => _paradoxRoots.Value;

    private static List<string> FindParadoxRoots()
    {
        var found = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p) || !Directory.Exists(p)) return;
            var full = Path.GetFullPath(p).TrimEnd('\\');
            if (!found.Contains(full, StringComparer.OrdinalIgnoreCase)) found.Add(full);
        }

        foreach (var docs in DocumentsDirs())
        {
            Add(Path.Combine(docs, "Paradox Interactive"));
            Add(Path.Combine(docs, "Colossal Order"));
        }

        // 文档被搬到别的盘、或不在标准位置的：扫固定磁盘浅层
        foreach (var d in SafeScan("Paradox Interactive", maxDepth: 2))
            Add(d);
        foreach (var d in SafeScan("Colossal Order", maxDepth: 2))
            Add(d);

        return found;
    }

    /// <summary>找某游戏的用户数据目录（含 mod / 存档 / launcher-v2.sqlite）。</summary>
    public static string? FindUserDir(GameDef g)
    {
        foreach (var root in ParadoxRoots())
        {
            var cand = Path.Combine(root, g.Pdx);
            if (Directory.Exists(cand)) return cand;
        }

        // 少数游戏数据在 AppData 下
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var app in new[] { @"AppData\LocalLow", @"AppData\Local", @"AppData\Roaming" })
        {
            var cand = Path.Combine(home, app, g.UserBase, g.Pdx);
            if (Directory.Exists(cand)) return cand;
        }
        return null;
    }

    // ------------------------------------------------------------ Steam

    private static Lazy<List<string>> _steamRoots = new(FindSteamRoots);

    /// <summary>用户在设置里手动关联的 Steam 目录（优先于注册表探测，重启后由配置恢复）。</summary>
    private static string? _manualSteamRoot;

    /// <summary>当前手动关联的 Steam 目录；未关联时为空串。</summary>
    public static string ManualSteamRoot => _manualSteamRoot ?? "";

    /// <summary>设置/清除手动关联的 Steam 目录，并立即重新探测。传空表示恢复自动探测。</summary>
    public static void SetManualSteamRoot(string? dir)
    {
        lock (LayoutLock) { _manualSteamRoot = string.IsNullOrWhiteSpace(dir) ? null : dir.Trim(); }
        RefreshSteamRoots();
    }

    public static IReadOnlyList<string> SteamRoots() => _steamRoots.Value;

    /// <summary>强制重新探测 Steam 路径（Steam 在启动器之后才打开时也能读到）。</summary>
    public static void RefreshSteamRoots()
    {
        lock (LayoutLock) { _steamRoots = new Lazy<List<string>>(FindSteamRoots); }
    }

    private static List<string> FindSteamRoots()
    {
        var roots = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p) || !Directory.Exists(p)) return;
            var full = Path.GetFullPath(p).TrimEnd('\\');
            if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
        }

        // 手动关联的目录优先级最高：注册表缺失、非标准安装、移动硬盘盘符变化时仍可用
        var manual = _manualSteamRoot;
        if (!string.IsNullOrWhiteSpace(manual))
        {
            var m = manual.Replace('/', '\\').TrimEnd('\\');
            if (Path.GetFileName(m).Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                m = Path.GetFullPath(Path.Combine(m, ".."));
            if (Directory.Exists(m))
            {
                Add(m);
                foreach (var lib in ParseLibraryFolders(m)) Add(lib);
            }
        }

        string? steam = null;
        foreach (var (hive, sub) in new[]
                 {
                     (RegistryHive.CurrentUser, @"Software\Valve\Steam"),
                     (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
                     (RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam"),
                 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var k = baseKey.OpenSubKey(sub);
                if (k is null) continue;
                steam ??= k.GetValue("SteamPath") as string;
                steam ??= k.GetValue("InstallPath") as string;
            }
            catch { }
        }
        if (!string.IsNullOrWhiteSpace(steam))
        {
            steam = steam.Replace('/', '\\');
            Add(steam);
            foreach (var lib in ParseLibraryFolders(steam)) Add(lib);
        }

        // 注册表拿不到 Steam 时，磁盘上找 steamapps\common
        if (roots.Count == 0)
        {
            foreach (var common in SafeScan("common", maxDepth: 3, requireParentName: "steamapps"))
            {
                var lib = Path.GetFullPath(Path.Combine(common, "..", ".."));
                Add(lib);
            }
        }
        return roots;
    }

    private static IEnumerable<string> ParseLibraryFolders(string steam)
    {
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        string text;
        try { text = File.ReadAllText(vdf); } catch { yield break; }
        foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
        {
            var p = m.Groups[1].Value.Replace(@"\\", @"\");
            if (Directory.Exists(p)) yield return p;
        }
    }

    /// <summary>找游戏本体目录（steamapps\common\&lt;folder&gt;）。</summary>
    public static string? FindGameRoot(GameDef g)
    {
        foreach (var lib in SteamRoots())
        {
            var cand = Path.Combine(lib, "steamapps", "common", g.Folder);
            if (File.Exists(GameDefs.ExePath(g, cand))) return cand;
            if (Directory.Exists(cand)) return cand;
        }

        foreach (var common in SafeScan("common", maxDepth: 3, requireParentName: "steamapps"))
        {
            var cand = Path.Combine(common, g.Folder);
            if (Directory.Exists(cand) && File.Exists(GameDefs.ExePath(g, cand))) return cand;
        }
        return null;
    }

    /// <summary>找创意工坊目录 steamapps\workshop\content\&lt;appid&gt;。</summary>
    public static string? FindWorkshopDir(GameDef g, string? gameRoot)
    {
        var libs = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameRoot))
        {
            try
            {
                var lib = Path.GetFullPath(Path.Combine(gameRoot, "..", ".."));
                libs.Add(lib);
            }
            catch { }
        }
        foreach (var r in SteamRoots())
            if (!libs.Contains(r, StringComparer.OrdinalIgnoreCase)) libs.Add(r);

        foreach (var lib in libs)
        {
            var cand = Path.Combine(lib, "steamapps", "workshop", "content", g.AppId);
            if (Directory.Exists(cand)) return cand;
        }
        return null;
    }

    // ------------------------------------------------------------ 磁盘浅扫

    private static readonly string[] SkipNames =
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin",
        "System Volume Information", "AppData", "node_modules", ".git", "Recovery",
        "PerfLogs", "Config.Msi", "Windows.old"
    };

    public static IEnumerable<string> FixedDrives() => _fixedDrives.Value;

    private static readonly Lazy<List<string>> _fixedDrives = new(() =>
    {
        var list = new List<string>();
        DriveInfo[] all;
        try { all = DriveInfo.GetDrives(); } catch { return list; }
        foreach (var d in all)
        {
            bool ok;
            try { ok = d.DriveType == DriveType.Fixed && d.IsReady; } catch { ok = false; }
            if (ok) list.Add(d.RootDirectory.FullName);
        }
        return list;
    });

    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> _scanCache = new();

    /// <summary>
    /// 在固定磁盘上按名称找目录，限制深度，跳过系统目录。
    /// requireParentName：只认父目录名匹配的（用于 steamapps\common）。
    /// 结果按 (名称, 深度, 父名) 缓存 —— AutoHeal 会为 10 款游戏各查一次，缓存后只扫一遍。
    /// </summary>
    public static IReadOnlyList<string> SafeScan(string name, int maxDepth, string? requireParentName = null)
    {
        var cacheKey = name + "|" + maxDepth + "|" + (requireParentName ?? "");
        return _scanCache.GetOrAdd(cacheKey, _ => SafeScanCore(name, maxDepth, requireParentName));
    }

    private static List<string> SafeScanCore(string name, int maxDepth, string? requireParentName)
    {
        var hits = new List<string>();
        foreach (var drive in FixedDrives())
        {
            var queue = new Queue<(string path, int depth)>();
            queue.Enqueue((drive, 0));
            while (queue.Count > 0)
            {
                var (dir, depth) = queue.Dequeue();
                if (depth > maxDepth) continue;
                IEnumerable<string> subs;
                try { subs = Directory.EnumerateDirectories(dir); } catch { continue; }
                foreach (var sub in subs)
                {
                    var leaf = Path.GetFileName(sub);
                    if (SkipNames.Contains(leaf, StringComparer.OrdinalIgnoreCase)) continue;
                    if (string.Equals(leaf, name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (requireParentName is null ||
                            string.Equals(Path.GetFileName(Path.GetDirectoryName(sub)), requireParentName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            hits.Add(sub);
                        }
                        continue;
                    }
                    if (depth + 1 <= maxDepth) queue.Enqueue((sub, depth + 1));
                }
            }
        }
        return hits;
    }

    /// <summary>把 \\?\ 长路径前缀去掉，前端展示更干净。</summary>
    public static string Clean(string p) =>
        string.IsNullOrEmpty(p) ? p : p.Replace(@"\\?\", "").TrimEnd('\\');
}
