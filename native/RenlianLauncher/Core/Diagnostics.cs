using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace RenlianLauncher.Core;

/// <summary>
/// 崩溃诊断收集：系统/硬件、游戏版本、崩溃记录、游戏日志、模组与存档、
/// 启动器日志、路径探测结果，打包成一个 zip。
///
/// 与旧版一致：超大日志只保留「前 800 行 + 后 3000 行」，
/// 崩溃目录只取最近 2 次且只复制小文件，避免诊断包失控。
/// </summary>
public static class Diagnostics
{
    private static readonly string[] LogFiles =
    {
        "error.log", "game.log", "setup.log", "system.log",
        "gui_warnings.log", "multiplayer.log", "debug.log",
    };

    private const int BigKb = 2048;

    public static object Collect(ConfigStore cfg, bool includeDxdiag)
    {
        var game = cfg.CurrentGame;
        var def = GameDefs.Get(game);
        var userDir = cfg.GameStr(game, "userDir") ?? "";
        var gameRoot = cfg.GameStr(game, "gameRoot") ?? "";

        var host = Environment.MachineName;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var outDir = Path.Combine(Path.GetTempPath(), "ck3diag_" + Guid.NewGuid().ToString("N")[..8],
            $"CK3_diag_{host}_{stamp}");

        var summary = new List<string>();
        var items = new List<object>();
        void Item(string name, bool ok, string note = "") =>
            items.Add(new { name, ok, note });

        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex) { return new { ok = false, error = "无法创建临时目录：" + ex.Message }; }

        try
        {
            // ---------- 00 系统与硬件
            var mem = ShellOps.Snapshot();
            var sb = new StringBuilder();
            sb.AppendLine("==== 操作系统 ====");
            sb.AppendLine("名称: " + RuntimeInformation.OSDescription);
            sb.AppendLine("版本: " + Environment.OSVersion.VersionString);
            sb.AppendLine("架构: " + RuntimeInformation.OSArchitecture);
            sb.AppendLine();
            sb.AppendLine("==== CPU ====");
            sb.AppendLine("型号: " + (Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? ""));
            sb.AppendLine("逻辑处理器: " + Environment.ProcessorCount);
            sb.AppendLine();
            sb.AppendLine("==== 物理内存 ====");
            sb.AppendLine("TotalPhysicalMemory(GB): " + mem.TotalPhysGB);
            sb.AppendLine("AvailablePhysicalMemory(GB): " + mem.AvailPhysGB);
            sb.AppendLine();
            sb.AppendLine("==== 提交上限 / 虚拟内存（关键：大包靠这个撑住）====");
            sb.AppendLine("CommitLimit(GB): " + mem.TotalCommitGB);
            sb.AppendLine("CommitAvailable(GB): " + mem.AvailCommitGB);
            foreach (var pf in mem.Pagefiles)
                sb.AppendLine($"页文件: {pf.Path}  分配 {pf.InitialMB} MB  最大 {pf.MaxMB} MB  系统管理={pf.Auto}");
            if (mem.Pagefiles.Count == 0)
                sb.AppendLine("！！没有检测到页文件：物理内存不足时游戏会被系统直接结束，且不留任何日志");
            sb.AppendLine();
            sb.AppendLine("==== 磁盘 ====");
            foreach (var d in FixedDrives())
            {
                try
                {
                    sb.AppendLine($"{d.Name}  {d.DriveFormat}  剩余 {d.AvailableFreeSpace / 1024d / 1024 / 1024:0.0} GB / 共 {d.TotalSize / 1024d / 1024 / 1024:0.0} GB");
                }
                catch { }
            }
            sb.AppendLine();
            sb.AppendLine("==== 启动器 ====");
            sb.AppendLine("版本: " + ShellOps.CurrentVersion);
            sb.AppendLine("数据目录: " + Paths.DataDir);
            sb.AppendLine("当前游戏: " + game + " (" + def.En + ")");
            WriteText(Path.Combine(outDir, "00_系统与硬件.txt"), sb.ToString());
            Item("系统与硬件", true, $"内存 {mem.TotalPhysGB} GB / 提交上限 {mem.TotalCommitGB} GB");
            summary.Add($"物理内存 {mem.TotalPhysGB} GB，提交上限 {mem.TotalCommitGB} GB");
            if (mem.TotalPhysGB > 0 && mem.TotalCommitGB - mem.TotalPhysGB < 4)
                summary.Add("【注意】提交上限与物理内存相差不到 4 GB —— 页文件可能被关闭或设得过小，" +
                            "大包启动时容易被系统直接结束进程，且不留日志。");

            // ---------- 01 游戏版本
            var verLines = new List<string>();
            foreach (var rel in new[] { "launcher/launcher-settings.json", "launcher-settings.json" })
            {
                var p = gameRoot.Length > 0 ? Path.Combine(gameRoot, rel.Replace('/', '\\')) : "";
                if (p.Length == 0 || !File.Exists(p)) continue;
                try
                {
                    var o = JsonNode.Parse(File.ReadAllText(p, Encoding.UTF8)) as JsonObject;
                    verLines.Add("launcher-settings: " + (ConfigStore.Str(o, "version") ?? ""));
                    verLines.Add("rawVersion: " + (ConfigStore.Str(o, "rawVersion") ?? ""));
                    break;
                }
                catch { }
            }
            var syslog = Path.Combine(userDir, "logs", "system.log");
            if (File.Exists(syslog))
            {
                string[] keys = { "Git Version", "Build Timestamp", "System Memory", "GPU Name",
                                  "GPU Dedicated", "CPU Model", "OS: " };
                try
                {
                    foreach (var line in File.ReadLines(syslog))
                        if (keys.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase)))
                            verLines.Add(line.Trim());
                }
                catch { }
            }
            if (verLines.Count > 0)
            {
                WriteText(Path.Combine(outDir, "01_游戏版本与运行环境.txt"), string.Join(Environment.NewLine, verLines));
                Item("游戏版本", true, "已读取版本信息");
            }
            else Item("游戏版本", false, "没有读到版本信息");

            // ---------- 02 崩溃记录
            var crashDir = Path.Combine(userDir, "crashes");
            var crashDirs = new List<DirectoryInfo>();
            try
            {
                if (Directory.Exists(crashDir))
                    crashDirs = new DirectoryInfo(crashDir).GetDirectories()
                        .OrderByDescending(d => d.LastWriteTimeUtc).Take(2).ToList();
            }
            catch { }

            if (crashDirs.Count > 0)
            {
                foreach (var d in crashDirs)
                {
                    var dst = Path.Combine(outDir, "02_崩溃_" + d.Name);
                    Directory.CreateDirectory(dst);
                    foreach (var fn in new[] { "exception.txt", "meta.yml", "pdx_settings.txt" })
                    {
                        var src = Path.Combine(d.FullName, fn);
                        if (File.Exists(src))
                            try { File.Copy(src, Path.Combine(dst, fn), true); } catch { }
                    }
                }
                Item("崩溃记录", true, $"最近 {crashDirs.Count} 次");
                summary.Add("最近崩溃时间：" + crashDirs[0].LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
            }
            else
            {
                WriteText(Path.Combine(outDir, "02_没有crashes文件夹.txt"),
                    "没有 crashes 文件夹。\n\n说明游戏不是「崩溃」（崩溃会生成 crashes 目录），\n" +
                    "更可能是被系统 / 杀毒软件直接结束了进程，或者白屏卡死后手动关闭。\n" +
                    "这条信息本身很关键，请一并反馈。");
                Item("崩溃记录", false, "没有 crashes 目录（不是崩溃，可能被强杀）");
                summary.Add("没有任何崩溃记录 —— 更可能是被强制结束 / 白屏卡住后手动关闭。");
            }

            var exc = Path.Combine(userDir, "exceptions");
            if (Directory.Exists(exc))
            {
                try { CopyDir(exc, Path.Combine(outDir, "03_exceptions")); } catch { }
            }

            // ---------- 04 游戏日志
            var logOut = Path.Combine(outDir, "04_logs");
            Directory.CreateDirectory(logOut);
            var logCount = 0;
            foreach (var name in LogFiles)
            {
                if (CopyTrimmed(Path.Combine(userDir, "logs", name), Path.Combine(logOut, name),
                        name == "debug.log" ? 1024 : BigKb)) logCount++;
            }
            var errPath = Path.Combine(userDir, "logs", "error.log");
            var errNote = File.Exists(errPath)
                ? $"error.log {new FileInfo(errPath).Length / 1024d:0.0} KB"
                : "没拿到 error.log";
            Item("游戏日志", logCount > 0, $"{logCount} 个 · {errNote}");

            // ---------- 05 模组 / 设置
            foreach (var fn in new[] { "dlc_load.json", "content_load.json", "pdx_settings.txt" })
            {
                var src = Path.Combine(userDir, fn);
                if (File.Exists(src))
                    try { File.Copy(src, Path.Combine(outDir, "05_" + fn), true); } catch { }
            }
            var modCount = 0;
            foreach (var fn in new[] { "dlc_load.json", "content_load.json" })
            {
                var p = Path.Combine(userDir, fn);
                if (!File.Exists(p)) continue;
                try
                {
                    var o = JsonNode.Parse(File.ReadAllText(p, Encoding.UTF8)) as JsonObject;
                    if (o?["enabled_mods"] is JsonArray arr) modCount = arr.Count;
                    break;
                }
                catch { }
            }
            WriteText(Path.Combine(outDir, "05_启用mod数量.txt"), "启用 mod 数量：" + modCount);
            Item("模组列表", true, $"启用 {modCount} 个");
            summary.Add("启用 mod 数量：" + modCount);

            // ---------- 06 启动器日志
            var launchOut = Path.Combine(outDir, "06_启动器日志");
            Directory.CreateDirectory(launchOut);
            var launchLogs = 0;
            var ownLog = Paths.LogPath;
            if (File.Exists(ownLog) && CopyTrimmed(ownLog, Path.Combine(launchOut, "启动日志.log"), 1024)) launchLogs++;
            var ldir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Paradox Interactive", "launcher-v2", "logs");
            if (Directory.Exists(ldir))
            {
                try
                {
                    var cands = new DirectoryInfo(ldir).GetFiles("*", SearchOption.AllDirectories)
                        .Where(f => f.Length > 0 &&
                                    (f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase) ||
                                     f.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                                     f.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase)))
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .Take(15);
                    foreach (var f in cands)
                        if (CopyTrimmed(f.FullName, Path.Combine(launchOut, f.Name), 512)) launchLogs++;
                }
                catch { }
            }
            Item("启动器日志", launchLogs > 0, $"{launchLogs} 个");

            // ---------- 07 dxdiag（可选）
            if (includeDxdiag)
            {
                var dx = Path.Combine(outDir, "07_dxdiag.txt");
                var ok = false;
                try
                {
                    Process.Start(new ProcessStartInfo("dxdiag.exe", $"/t \"{dx}\"") { UseShellExecute = false });
                    for (int i = 0; i < 45; i++)
                    {
                        Thread.Sleep(1000);
                        if (File.Exists(dx) && new FileInfo(dx).Length > 2000) { ok = true; break; }
                    }
                }
                catch { }
                Item("dxdiag", ok, ok ? "已生成" : "超时未生成，已跳过");
            }
            else Item("dxdiag", true, "已跳过（可选）");

            // ---------- 08 存档列表
            try
            {
                var rows = new List<(DateTime Mtime, string Name, long Size)>();
                var saveDir = Path.Combine(userDir, "save games");
                if (Directory.Exists(saveDir))
                {
                    foreach (var f in new DirectoryInfo(saveDir).GetFiles("*", SearchOption.AllDirectories))
                    {
                        if (!f.Extension.Equals(def.SaveExt, StringComparison.OrdinalIgnoreCase) &&
                            !f.Name.EndsWith(def.SaveExt, StringComparison.OrdinalIgnoreCase)) continue;
                        rows.Add((f.LastWriteTime, f.Name, f.Length));
                    }
                }
                var text = new StringBuilder("存档名\t大小(MB)\t修改时间\n");
                foreach (var r in rows.OrderByDescending(x => x.Mtime))
                    text.AppendLine($"{r.Name}\t{r.Size / 1048576d:0.0}\t{r.Mtime:yyyy-MM-dd HH:mm}");
                WriteText(Path.Combine(outDir, "08_存档列表.txt"), text.ToString());
                Item("存档列表", true, $"{rows.Count} 个（只记名字，不含本体）");
            }
            catch (Exception ex) { Item("存档列表", false, ex.Message); }

            // ---------- 09 路径探测结果
            var pb = new StringBuilder();
            pb.AppendLine("==== 配置中的路径 ====");
            pb.AppendLine($"gameRoot: {gameRoot}  (存在={Directory.Exists(gameRoot)})");
            pb.AppendLine($"userDir: {userDir}  (存在={Directory.Exists(userDir)})");
            pb.AppendLine($"workshopDir: {cfg.GameStr(game, "workshopDir")}  (存在={Directory.Exists(cfg.GameStr(game, "workshopDir") ?? "")})");
            pb.AppendLine();
            pb.AppendLine("==== Steam 库 ====");
            foreach (var s in Paths.SteamRoots()) pb.AppendLine(s);
            pb.AppendLine();
            pb.AppendLine("==== 文档目录 ====");
            foreach (var s in Paths.DocumentsDirs()) pb.AppendLine(s);
            pb.AppendLine();
            pb.AppendLine("==== Paradox 数据根 ====");
            foreach (var s in Paths.ParadoxRoots()) pb.AppendLine(s);
            pb.AppendLine();
            pb.AppendLine("==== Steam 清单 ====");
            pb.AppendLine(SteamBeta.ManifestPath(def.AppId, gameRoot));
            WriteText(Path.Combine(outDir, "09_路径探测.txt"), pb.ToString());
            Item("路径探测", true, "已记录配置路径与探测结果");

            // ---------- 99 摘要
            WriteText(Path.Combine(outDir, "99_摘要.txt"), string.Join(Environment.NewLine, new[]
            {
                "==== 诊断摘要 ====",
                "采集时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                "计算机名：" + host,
                "游戏版本：" + (cfg.GameStr(game, "gameVersion") ?? ""),
                "",
            }.Concat(summary)));

            // ---------- 打包
            var zipPath = outDir + ".zip";
            try
            {
                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(outDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
            }
            catch (Exception ex)
            {
                return new { ok = false, error = "打包失败：" + ex.Message, dir = outDir };
            }

            return new
            {
                ok = true,
                zip = zipPath,
                dir = outDir,
                name = Path.GetFileName(zipPath),
                size = new FileInfo(zipPath).Length,
                summary,
                items,
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = "收集失败：" + ex.Message, dir = outDir };
        }
    }

    // ------------------------------------------------------------ 保存 / 打开

    /// <summary>POST /api/diag/save-as：只允许保存本程序生成的诊断包。</summary>
    public static object SaveAs(string zipPath)
    {
        var src = (zipPath ?? "").Trim();
        if (src.Length == 0 || !File.Exists(src) ||
            !src.Contains("ck3diag_", StringComparison.OrdinalIgnoreCase) ||
            !src.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return new { ok = false, error = "诊断包不存在或已过期" };

        var target = UiInvoke(() =>
        {
            var dlg = new SaveFileDialog
            {
                Title = "保存诊断包",
                FileName = Path.GetFileName(src),
                DefaultExt = ".zip",
                AddExtension = true,
                OverwritePrompt = true,
                Filter = "ZIP 压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            };
            var owner = System.Windows.Application.Current?.MainWindow;
            return (owner is not null ? dlg.ShowDialog(owner) == true : dlg.ShowDialog() == true)
                ? dlg.FileName : "";
        });

        if (target.Length == 0) return new { ok = true, cancelled = true, path = "" };
        try
        {
            File.Copy(src, target, overwrite: true);
            return new { ok = true, cancelled = false, path = target };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = "保存失败：" + ex.Message };
        }
    }

    /// <summary>POST /api/diag/open-dir：用资源管理器打开诊断临时目录。</summary>
    public static object OpenDir(string dir)
    {
        var d = (dir ?? "").Trim();
        if (d.Length == 0 || !d.Contains("ck3diag_", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(d))
            return new { ok = false, error = "目录不存在" };
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{d}\"") { UseShellExecute = true });
            return new { ok = true };
        }
        catch (Exception ex) { return new { ok = false, error = ex.Message }; }
    }

    // ------------------------------------------------------------ 辅助

    private static IEnumerable<DriveInfo> FixedDrives()
    {
        DriveInfo[] all;
        try { all = DriveInfo.GetDrives(); } catch { yield break; }
        foreach (var d in all)
        {
            bool ok;
            try { ok = d.DriveType == DriveType.Fixed && d.IsReady; } catch { ok = false; }
            if (ok) yield return d;
        }
    }

    private static void WriteText(string path, string text)
    {
        try { File.WriteAllText(path, text, new UTF8Encoding(false)); } catch { }
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            try { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); } catch { }
    }

    /// <summary>复制文件；超过 maxKb 的只保留前 800 行 + 后 3000 行。</summary>
    private static bool CopyTrimmed(string src, string dst, int maxKb)
    {
        if (!File.Exists(src)) return false;
        try
        {
            var fi = new FileInfo(src);
            if (fi.Length <= maxKb * 1024L)
            {
                File.Copy(src, dst, true);
                return true;
            }

            var head = new List<string>();
            using (var f = new StreamReader(src, Encoding.UTF8))
            {
                for (int i = 0; i < 800 && !f.EndOfStream; i++)
                {
                    var line = f.ReadLine();
                    if (line is not null) head.Add(line);
                }
            }

            using var rb = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            rb.Seek(Math.Max(0, fi.Length - 400000), SeekOrigin.Begin);
            var buf = new byte[Math.Min(400000L, fi.Length)];
            var read = rb.Read(buf, 0, buf.Length);
            var tail = Encoding.UTF8.GetString(buf, 0, read).Split('\n').TakeLast(3000);

            using var w = new StreamWriter(dst, false, new UTF8Encoding(false));
            foreach (var line in head) w.WriteLine(line);
            w.WriteLine();
            w.WriteLine($"==== 原文件 {fi.Length / 1048576d:0.0} MB 过大，这里只保留 前800行 + 后3000行 ====");
            foreach (var line in tail) w.WriteLine(line);
            return true;
        }
        catch { return false; }
    }

    /// <summary>文件对话框必须回到界面线程上弹。</summary>
    private static T UiInvoke<T>(Func<T> fn)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return fn();
        return app.Dispatcher.Invoke(fn);
    }
}
