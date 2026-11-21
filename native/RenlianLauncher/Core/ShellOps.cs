using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RenlianLauncher.Core;

/// <summary>
/// 系统与窗口操作：内存信息 / 一键优化 / 提权重启 / 性能选项 / 最小化 / 退出 /
/// 桌面快捷方式 / 官方启动器 / 使用教程 / 版本自检 / EU4 强制排序。
/// 所有方法都不向外抛异常，失败时返回 {ok:false,error}。
/// </summary>
public static class ShellOps
{
    // ------------------------------------------------------------ 内存信息

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint cb;
        public IntPtr CommitTotal;
        public IntPtr CommitLimit;
        public IntPtr CommitPeak;
        public IntPtr PhysicalTotal;
        public IntPtr PhysicalAvailable;
        public IntPtr SystemCache;
        public IntPtr KernelTotal;
        public IntPtr KernelPaged;
        public IntPtr KernelNonpaged;
        public IntPtr PageSize;
        public IntPtr HandleCount;
        public IntPtr ProcessCount;
        public IntPtr ThreadCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetPerformanceInfo(out PerformanceInformation info, uint cb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcesses(int[] processIds, uint size, out uint needed);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out long luid);

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public long Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privilege;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
        ref TokenPrivileges newState, uint bufferLength, IntPtr previous, IntPtr returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int SystemMemoryListInformation = 0x50;
    private const int MemoryPurgeStandbyList = 4;
    private const uint ProcessQueryLimited = 0x1000;
    private const uint ProcessSetQuota = 0x0100;

    public sealed record PagefileInfo(string Path, long InitialMB, long MaxMB, bool Auto);

    public sealed record MemorySnapshot(
        double TotalPhysGB, double AvailPhysGB, double TotalCommitGB, double AvailCommitGB,
        int LoadPercent, double CacheMB, List<PagefileInfo> Pagefiles, bool IsAdmin);

    private static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static List<PagefileInfo> ReadPagefiles()
    {
        var list = new List<PagefileInfo>();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            if (k?.GetValue("PagingFiles") is string[] rows)
            {
                foreach (var row in rows)
                {
                    var parts = row.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;
                    long.TryParse(parts.Length > 1 ? parts[1] : "0", out var initial);
                    long.TryParse(parts.Length > 2 ? parts[2] : "0", out var max);
                    list.Add(new PagefileInfo(parts[0], initial, max, initial == 0 && max == 0));
                }
            }
        }
        catch { }
        return list;
    }

    public static MemorySnapshot Snapshot()
    {
        var ms = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref ms))
            return new MemorySnapshot(0, 0, 0, 0, 0, 0, new List<PagefileInfo>(), IsAdmin());

        const double gb = 1024d * 1024 * 1024;
        double cacheMb = 0;
        try
        {
            if (GetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<PerformanceInformation>()) &&
                pi.PageSize != IntPtr.Zero)
                cacheMb = Math.Round(pi.SystemCache.ToInt64() * pi.PageSize.ToInt64() / 1048576d, 1);
        }
        catch { }

        return new MemorySnapshot(
            Math.Round(ms.ullTotalPhys / gb, 2),
            Math.Round(ms.ullAvailPhys / gb, 2),
            Math.Round(ms.ullTotalPageFile / gb, 2),
            Math.Round(ms.ullAvailPageFile / gb, 2),
            (int)ms.dwMemoryLoad,
            cacheMb,
            ReadPagefiles(),
            IsAdmin());
    }

    /// <summary>GET /api/mem：字段与旧版 memory_info 一致。</summary>
    public static object MemoryInfo()
    {
        var s = Snapshot();
        if (s.TotalPhysGB <= 0) return new { ok = false, error = "读取内存信息失败" };
        return new
        {
            ok = true,
            totalPhysGB = s.TotalPhysGB,
            availPhysGB = s.AvailPhysGB,
            usedPhysGB = Math.Round(s.TotalPhysGB - s.AvailPhysGB, 2),
            totalCommitGB = s.TotalCommitGB,
            usedCommitGB = Math.Round(s.TotalCommitGB - s.AvailCommitGB, 2),
            loadPercent = s.LoadPercent,
            cacheMB = s.CacheMB,
            pagefiles = s.Pagefiles.Select(p => new
            {
                path = p.Path,
                initialMB = p.InitialMB,
                maxMB = p.MaxMB,
                auto = p.Auto,
            }).ToArray(),
            pagefileCount = s.Pagefiles.Count,
            isAdmin = s.IsAdmin,
        };
    }

    /// <summary>POST /api/mem/optimize：整理各进程工作集；管理员下额外清空待机内存。</summary>
    public static object OptimizeMemory()
    {
        var before = Snapshot();
        var purged = false;
        var trimmed = 0;

        if (before.IsAdmin) purged = TryPurgeStandby();

        try
        {
            var ids = new int[65536];
            if (EnumProcesses(ids, (uint)(ids.Length * sizeof(int)), out var needed))
            {
                var count = (int)(needed / sizeof(int));
                for (int i = 0; i < count; i++)
                {
                    var pid = ids[i];
                    if (pid <= 0) continue;
                    var h = OpenProcess(ProcessQueryLimited | ProcessSetQuota, false, pid);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        if (SetProcessWorkingSetSize(h, new IntPtr(-1), new IntPtr(-1))) trimmed++;
                    }
                    finally { CloseHandle(h); }
                }
            }
            try { EmptyWorkingSet(GetCurrentProcess()); } catch { }
        }
        catch (Exception ex) { return new { ok = false, error = ex.Message }; }

        Thread.Sleep(600);
        var after = Snapshot();
        var freed = Math.Round(Math.Max(0, after.AvailPhysGB - before.AvailPhysGB), 2);
        var cacheFreed = Math.Round(Math.Max(0, before.CacheMB - after.CacheMB), 1);

        return new
        {
            ok = true,
            admin = before.IsAdmin,
            purged,
            trimmed,
            freedGB = freed,
            cacheFreedMB = cacheFreed,
            after = MemoryInfo(),
        };
    }

    private static bool TryPurgeStandby()
    {
        try
        {
            EnablePrivilege("SeProfileSingleProcessPrivilege");
            EnablePrivilege("SeIncreaseQuotaPrivilege");
            var command = MemoryPurgeStandbyList;
            return NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int)) == 0;
        }
        catch { return false; }
    }

    private static void EnablePrivilege(string name)
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token)) return;
            if (!LookupPrivilegeValue(null, name, out var luid)) return;
            var tp = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privilege = new LuidAndAttributes { Luid = luid, Attributes = SePrivilegeEnabled },
            };
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    // ------------------------------------------------------------ 窗口 / 提权 / 退出

    /// <summary>POST /api/mem/admin-restart：以管理员身份重启自己。</summary>
    public static object RestartAsAdmin()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
                return new { ok = false, error = "无法定位程序路径" };

            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Paths.ExeDir,
            });
            Task.Delay(600).ContinueWith(_ => Exit());
            return new { ok = true };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = "用户取消或提权失败：" + ex.Message };
        }
    }

    /// <summary>POST /api/launcher/minimize：最小化启动器主窗口。</summary>
    public static bool Minimize()
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app?.MainWindow is null) return false;
            app.Dispatcher.Invoke(() => app.MainWindow.WindowState = System.Windows.WindowState.Minimized);
            return true;
        }
        catch { return false; }
    }

    /// <summary>POST /api/exit：关闭启动器（游戏不受影响）。</summary>
    public static bool Exit()
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app is null) return false;
            app.Dispatcher.BeginInvoke(new Action(() => app.Shutdown()));
            return true;
        }
        catch { return false; }
    }

    /// <summary>POST /api/shortcut：在桌面创建「人联启动器」快捷方式（WScript.Shell）。</summary>
    public static object CreateShortcut()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                return new { ok = false, error = "找不到启动器程序文件" };

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop)) desktop = Paths.ExeDir;
            var lnk = Path.Combine(desktop, "人联启动器.lnk");

            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return new { ok = false, error = "系统缺少 WScript.Shell，无法创建快捷方式" };

            object? shell = null;
            object? shortcut = null;
            try
            {
                shell = Activator.CreateInstance(type);
                shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                    null, shell, new object[] { lnk });
                var st = shortcut!.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exe });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Paths.ExeDir });
                st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { exe + ",0" });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "人联启动器" });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                if (shortcut is not null) try { Marshal.ReleaseComObject(shortcut); } catch { }
                if (shell is not null) try { Marshal.ReleaseComObject(shell); } catch { }
            }
            return new { ok = true, path = lnk };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = "创建快捷方式失败：" + ex.Message };
        }
    }

    // ------------------------------------------------------------ 启动 / 打开

    /// <summary>POST /api/launch-official：优先通过 Steam 拉起，保证官方启动器能连上 Steam。</summary>
    public static object LaunchOfficial(ConfigStore cfg)
    {
        var game = cfg.CurrentGame;
        var def = GameDefs.Get(game);

        try
        {
            Process.Start(new ProcessStartInfo($"steam://rungameid/{def.AppId}") { UseShellExecute = true });
            return new { ok = true, via = "steam" };
        }
        catch { }

        var root = cfg.GameStr(game, "gameRoot") ?? "";
        var candidates = new[]
        {
            Path.Combine(root, "launcher", "dowser.exe"),
            Path.Combine(root, "launcher", "launcher.exe"),
            Path.Combine(root, "dowser.exe"),
            Path.Combine(root, "launcher.exe"),
        };
        foreach (var p in candidates)
        {
            if (!File.Exists(p)) continue;
            try
            {
                Process.Start(new ProcessStartInfo(p)
                {
                    WorkingDirectory = Path.GetDirectoryName(p)!,
                    UseShellExecute = true,
                });
                return new { ok = true, via = "dowser" };
            }
            catch (Exception ex) { return new { ok = false, error = ex.Message }; }
        }

        // 老游戏（CK2 / 群星等）没有独立 dowser，直接启动游戏 exe（自带启动器界面）
        if (root.Length > 0)
        {
            var exe = GameDefs.ExePath(def, root);
            if (File.Exists(exe))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(exe)
                    {
                        WorkingDirectory = Path.GetDirectoryName(exe)!,
                        UseShellExecute = true,
                    });
                    return new { ok = true, via = "game" };
                }
                catch (Exception ex) { return new { ok = false, error = ex.Message }; }
            }
        }
        return new { ok = false, error = "找不到官方启动器" };
    }

    /// <summary>POST /api/open-tutorial：用默认浏览器打开「使用教程.html」。</summary>
    public static object OpenTutorial()
    {
        var candidates = new[]
        {
            Path.Combine(Paths.ExeDir, "使用教程.html"),
            Path.Combine(Paths.ExeDir, "tutorial", "使用教程.html"),
            Path.Combine(Paths.MetaDir, "使用教程.html"),
        };
        foreach (var p in candidates)
        {
            if (!File.Exists(p)) continue;
            try
            {
                Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
                return new { ok = true, path = p };
            }
            catch
            {
                return new { ok = false, error = "系统里没有能打开 HTML 的浏览器" };
            }
        }
        return new { ok = false, error = "找不到「使用教程.html」，可能被杀毒软件误删了" };
    }

    /// <summary>POST /api/win/perf：打开系统「性能选项」（调虚拟内存的地方）。</summary>
    public static bool OpenPerfOptions()
    {
        try
        {
            Process.Start(new ProcessStartInfo("SystemPropertiesPerformance.exe") { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------ 版本自检

    /// <summary>版本号占位：csproj 未设置 Version 时程序集版本恒为 1.0.0.0，这里兜底报 1.7.1。</summary>
    public const string FallbackVersion = "1.7.1";

    public static string CurrentVersion
    {
        get
        {
            try
            {
                var v = typeof(ShellOps).Assembly.GetName().Version;
                if (v is not null && v > new Version(1, 0, 0, 0))
                    return v.Revision > 0 ? v.ToString(4) : v.ToString(3);
            }
            catch { }
            return FallbackVersion;
        }
    }

    public static string DefaultUpdateUrl =>
        "https://gitee.com/xiaoyi2233/renlian-launcher/raw/master/version.json";

    /// <summary>a 是否比 b 新（按点分数字逐段比较）。</summary>
    private static bool VersionGreater(string a, string b)
    {
        static int[] Nums(string v) => (v ?? "").Split('.')
            .Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0)
            .ToArray();

        var na = Nums(a);
        var nb = Nums(b);
        for (int i = 0; i < Math.Min(na.Length, nb.Length); i++)
            if (na[i] != nb[i]) return na[i] > nb[i];
        return na.Length > nb.Length;
    }

    /// <summary>POST /api/check-update：本地后端代理拉取 version.json，绕开 CORS。</summary>
    public static async Task<object> CheckUpdateAsync(string url, string current)
    {
        var target = (url ?? "").Trim();
        if (target.Length == 0) target = DefaultUpdateUrl;
        if (!target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new { ok = false, error = "更新检查地址无效（需以 http:// 或 https:// 开头）" };

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RenlianLauncher");
            var raw = await http.GetStringAsync(target);
            var data = JsonNode.Parse(raw) as JsonObject;
            var latest = (ConfigStore.Str(data, "version") ?? "").Trim();
            if (latest.Length == 0) return new { ok = false, error = "远程文件缺少 version 字段" };

            var cur = (current ?? "").Trim();
            if (cur.Length == 0) cur = CurrentVersion;
            return new
            {
                ok = true,
                latest,
                url = ConfigStore.Str(data, "url") ?? "",
                notes = ConfigStore.Str(data, "notes") ?? "",
                date = ConfigStore.Str(data, "date") ?? "",
                size = ConfigStore.Str(data, "size") ?? "",
                current = cur,
                updateAvailable = VersionGreater(latest, cur),
            };
        }
        catch (TaskCanceledException)
        {
            return new { ok = false, error = "网络错误：请求超时" };
        }
        catch (HttpRequestException ex)
        {
            return new { ok = false, error = "网络错误：" + ex.Message };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = "读取失败：" + ex.Message };
        }
    }

    // ------------------------------------------------------------ EU4 强制排序

    private static JsonObject LoadEu4State()
    {
        try
        {
            var p = Path.Combine(Paths.MetaDir, "eu4-force-order.json");
            if (!File.Exists(p)) return new JsonObject();
            return JsonNode.Parse(File.ReadAllText(p, Encoding.UTF8)) as JsonObject ?? new JsonObject();
        }
        catch { return new JsonObject(); }
    }

    private static void SaveEu4State(JsonObject state)
    {
        try
        {
            var p = Path.Combine(Paths.MetaDir, "eu4-force-order.json");
            if (state["items"] is JsonObject items && items.Count == 0)
            {
                if (File.Exists(p)) File.Delete(p);
                return;
            }
            File.WriteAllText(p, state.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }), new UTF8Encoding(false));
        }
        catch { }
    }

    private static readonly Regex Eu4Prefix = new(@"^\d+\s+", RegexOptions.Compiled);
    private static readonly Regex ModNameRegex = new("(?m)^\\s*name\\s*=\\s*\"([^\"]*)\"", RegexOptions.Compiled);

    private static string ReadModName(string path)
    {
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            var m = ModNameRegex.Match(text);
            return m.Success ? m.Groups[1].Value : "";
        }
        catch { return ""; }
    }

    private static bool WriteModName(string path, string name)
    {
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            var m = ModNameRegex.Match(text);
            if (!m.Success) return false;
            var idx = m.Groups[1].Index;
            var text2 = text[..idx] + name + text[(idx + m.Groups[1].Length)..];
            File.WriteAllText(path, text2, new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    /// <summary>GET /api/eu4/force-order：当前开关与被改写的 .mod 数量。</summary>
    public static object Eu4Status(ConfigStore cfg)
    {
        var count = LoadEu4State()["items"] is JsonObject items ? items.Count : 0;
        return new
        {
            ok = true,
            enabled = ConfigStore.Bool(cfg.Root, "eu4ForceOrder", false),
            count,
        };
    }

    /// <summary>
    /// POST /api/eu4/force-order：开启时把播放集顺序写进各 .mod 的 name= 前缀
    /// （EU4 只按 name 排序加载），关闭/还原时恢复原名。
    /// </summary>
    public static object Eu4Toggle(ConfigStore cfg, JsonObject body, IReadOnlyList<JsonObject> index)
    {
        var enabled = ConfigStore.Bool(body, "enabled", false);
        var restore = ConfigStore.Bool(body, "restore", false);

        cfg.Root["eu4ForceOrder"] = enabled;
        cfg.Save();

        if (restore || !enabled)
        {
            var r = Eu4Restore();
            return new
            {
                ok = true,
                enabled = false,
                restored = (int)r.Restored,
                failed = (int)r.Failed,
            };
        }

        var db = PlaysetDb.ForConfig(cfg);
        var psid = db.ActivePlayset() ?? db.ListPlaysets().FirstOrDefault()?.Id;
        if (string.IsNullOrWhiteSpace(psid))
            return new { ok = false, enabled = true, error = "没有可用的播放集" };
        var ps = db.GetPlayset(psid);
        if (ps is null) return new { ok = false, enabled = true, error = "播放集不存在" };

        var res = Eu4Apply(cfg, db, ps, index);
        return new
        {
            ok = true,
            enabled = true,
            applied = (int)res.Applied,
            skipped = (int)res.Skipped,
        };
    }

    private static (long Applied, long Skipped) Eu4Apply(
        ConfigStore cfg, PlaysetDb db, PlaysetDetail ps, IReadOnlyList<JsonObject> index)
    {
        var userDir = cfg.GameStr(cfg.CurrentGame, "userDir") ?? "";
        if (cfg.CurrentGame != "eu4" || userDir.Length == 0) return (0, 0);

        var id2file = db.ModIdToFileMap(index);
        var ordered = new List<(string ModFile, string Full)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in ps.Mods)
        {
            if (!m.Enabled) continue;
            if (!id2file.TryGetValue(m.ModId, out var file) || string.IsNullOrWhiteSpace(file)) continue;
            if (!seen.Add(file)) continue;
            var full = Path.Combine(new[] { userDir }.Concat(file.Replace('\\', '/').Split('/')).ToArray());
            if (!File.Exists(full)) continue;
            ordered.Add((file, full));
        }
        if (ordered.Count == 0) return (0, 0);

        var state = LoadEu4State();
        var items = state["items"] as JsonObject ?? new JsonObject();
        var width = ordered.Count <= 99 ? 2 : 3;
        long applied = 0, skipped = 0;

        for (int i = 0; i < ordered.Count; i++)
        {
            var (mf, full) = ordered[i];
            var key = mf.ToLowerInvariant();
            var cur = ReadModName(full);
            var rec = items[key] as JsonObject;
            var orig = (ConfigStore.Str(rec, "orig") ?? "").Trim();
            if (orig.Length == 0) orig = Eu4Prefix.Replace(cur, "").Trim();
            if (orig.Length == 0) { skipped++; continue; }

            items[key] = new JsonObject { ["file"] = full, ["orig"] = orig };
            var want = (i + 1).ToString("D" + width) + " " + orig;
            if (cur == want) { applied++; continue; }
            if (WriteModName(full, want)) applied++; else skipped++;
        }

        state["userDir"] = userDir;
        state["items"] = items;
        SaveEu4State(state);
        return (applied, skipped);
    }

    private static (long Restored, long Failed) Eu4Restore()
    {
        var state = LoadEu4State();
        if (state["items"] is not JsonObject items) return (0, 0);

        long restored = 0, failed = 0;
        var remaining = new JsonObject();
        foreach (var kv in items)
        {
            var rec = kv.Value as JsonObject;
            var full = ConfigStore.Str(rec, "file") ?? "";
            var orig = ConfigStore.Str(rec, "orig") ?? "";
            if (full.Length == 0 || !File.Exists(full)) continue;
            if (WriteModName(full, orig)) restored++;
            else
            {
                failed++;
                remaining[kv.Key] = JsonNode.Parse(rec!.ToJsonString());
            }
        }

        state["items"] = remaining;
        SaveEu4State(state);
        return (restored, failed);
    }
}
