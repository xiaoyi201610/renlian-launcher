using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;

namespace RenlianLauncher.Core;

/// <summary>
/// 启动器直接下载创意工坊 MOD（不走网页订阅）。
/// 三层策略：
/// ① SteamCMD 匿名下载（首次自动从 Steam 官方 CDN 下载 steamcmd 到纯 ASCII 路径
///    %LOCALAPPDATA%\RenlianLauncher\steamcmd —— steamcmd 不允许运行在含非英文的路径）；
/// ② Steam 客户端在运行 → steam://url/CommunityFilePage/&lt;id&gt; 在客户端内打开 MOD 页，
///    用户点订阅后 Steam 直接下载，启动器监视工坊目录识别完成；
/// ③ 都不行 → 前端弹窗询问是否跳转 Steam 网页手动订阅。
/// </summary>
public static class WorkshopDownload
{
    private static readonly string[] SteamcmdCdns =
    {
        "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip",
        "https://cdn.steamstatic.com/client/installer/steamcmd.zip",
    };

    /// <summary>steamcmd 必须放在纯 ASCII 路径（含中文路径会拒绝启动）。</summary>
    public static string SteamcmdDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RenlianLauncher", "steamcmd");
    private static string SteamcmdExe => Path.Combine(SteamcmdDir, "steamcmd.exe");

    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<string> Lines = new();
    private static Process? _process;
    private static string _state = "idle";
    private static string _lastId = "";
    /// <summary>SteamCMD 环境性故障（如自更新被网络拦、路径不兼容）记忆：24h 内不再尝试，直接走降级，避免用户每次点订阅都白等。</summary>
    private static DateTime _steamcmdBrokenUntil = DateTime.MinValue;
    private static readonly string[] BrokenSigns =
    {
        "needs to be online to update",
        "cannot run from a folder path",
        "Unable to access Steam files",
    };

    private static void Ua(HttpRequestMessage req) =>
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/122");

    /// <summary>SteamCMD 就绪状态：ready=可用 / missing=不存在 / error=下载失败（带原因）。</summary>
    public static async Task<JsonObject> EnsureSteamcmdAsync()
    {
        if (File.Exists(SteamcmdExe)) return new JsonObject { ["ok"] = true, ["ready"] = true };
        try
        {
            Directory.CreateDirectory(SteamcmdDir);
            var zip = Path.Combine(SteamcmdDir, "steamcmd.zip");
            byte[]? data = null;
            Exception? last = null;
            foreach (var cdn in SteamcmdCdns)
            {
                try
                {
                    using var hc = new HttpClient(new HttpClientHandler
                    { AutomaticDecompression = System.Net.DecompressionMethods.All })
                    { Timeout = TimeSpan.FromSeconds(60) };
                    using var req = new HttpRequestMessage(HttpMethod.Get, cdn);
                    Ua(req);
                    using var resp = await hc.SendAsync(req);
                    if (!resp.IsSuccessStatusCode) continue;
                    data = await resp.Content.ReadAsByteArrayAsync();
                    break;
                }
                catch (Exception ex) { last = ex; }
            }
            if (data is null || data.Length < 10000)
                return new JsonObject
                {
                    ["ok"] = false, ["ready"] = false,
                    ["error"] = "无法下载 SteamCMD（" + (last?.Message ?? "网络不通") + "）",
                };
            File.WriteAllBytes(zip, data);
            if (Directory.Exists(Path.Combine(SteamcmdDir, "steamapps")))
                try { Directory.Delete(Path.Combine(SteamcmdDir, "steamapps"), true); } catch { }
            ZipFile.ExtractToDirectory(zip, SteamcmdDir, overwriteFiles: true);
            File.Delete(zip);
            return File.Exists(SteamcmdExe)
                ? new JsonObject { ["ok"] = true, ["ready"] = true }
                : new JsonObject { ["ok"] = false, ["ready"] = false, ["error"] = "解压后未找到 steamcmd.exe" };
        }
        catch (Exception ex)
        {
            return new JsonObject { ["ok"] = false, ["ready"] = false, ["error"] = "SteamCMD 准备失败：" + ex.Message };
        }
    }

    /// <summary>当前游戏的工坊内容目录（steamapps\workshop\content\&lt;appid&gt;）。</summary>
    private static string? WorkshopDir(ConfigStore cfg)
    {
        try
        {
            var game = cfg.CurrentGame;
            var ws = cfg.GameStr(game, "workshopDir");
            if (!string.IsNullOrEmpty(ws) && Directory.Exists(ws)) return ws;
            var def = GameDefs.Get(game);
            return Paths.FindWorkshopDir(def, null);
        }
        catch { return null; }
    }

    /// <summary>
    /// 启动 SteamCMD 直接下载。返回 mode=steamcmd（已启动）/ client（Steam 客户端在运行，建议客户端内订阅）/ web（都不行）。
    /// </summary>
    public static JsonObject Start(string id, ConfigStore cfg)
    {
        id = (id ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^\d{1,12}$"))
            return Fallback("无效的工坊 ID");

        lock (Gate)
        {
            if (_process is not null && !_process.HasExited)
                return new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = "已有下载任务在运行，请先等待完成",
                    ["mode"] = "busy",
                };

            var ws = WorkshopDir(cfg);
            var gameDef = GameDefs.Get(cfg.CurrentGame);
            if (string.IsNullOrEmpty(ws))
                return Fallback($"没有找到 {gameDef.Name} 的创意工坊目录（请先在设置里关联 Steam）");
            var installDir = Path.GetFullPath(Path.Combine(ws, "..", "..", "..")); // → Steam 库根
            var target = Path.Combine(ws, id);

            // SteamCMD 路径不可用时直接走 fallback，避免再走一次注定失败的下载
            if (SteamcmdDir.Any(c => c > 127))
                return Fallback("SteamCMD 无法放在含非英文字符的路径");
            if (DateTime.UtcNow < _steamcmdBrokenUntil)
                return Fallback("SteamCMD 此前启动失败（" + _steamcmdBrokenUntil.AddHours(24).ToString("HH:mm") + " 前不再重试）");
            if (!Directory.Exists(installDir))
                return Fallback("Steam 库目录不存在：" + installDir);

            var prep = EnsureSteamcmdAsync().GetAwaiter().GetResult();
            var ready = prep.TryGetPropertyValue("ready", out var readyNode)
                        && readyNode is System.Text.Json.Nodes.JsonValue jv
                        && jv.TryGetValue<bool>(out var b) && b;
            if (!ready)
            {
                var err = prep["error"]?.ToString() ?? "SteamCMD 不可用";
                Lines.Enqueue("SteamCMD 准备失败：" + err);
                return Fallback(err);
            }

            while (Lines.TryDequeue(out _)) { }
            Lines.Enqueue("=== 启动 SteamCMD 下载 MOD " + id + " ===");
            Lines.Enqueue("目标：" + target);

            var psi = new ProcessStartInfo(SteamcmdExe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = SteamcmdDir,
            };
            psi.ArgumentList.Add("+force_install_dir");
            psi.ArgumentList.Add(installDir);
            psi.ArgumentList.Add("+login");
            psi.ArgumentList.Add("anonymous");
            psi.ArgumentList.Add("+workshop_download_item");
            psi.ArgumentList.Add(gameDef.AppId); // 跟随当前游戏，不再是固定 CK3
            psi.ArgumentList.Add(id);
            psi.ArgumentList.Add("+quit");

            try
            {
                _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _process.OutputDataReceived += (_, e) => { if (e.Data is not null) Lines.Enqueue(e.Data); };
                _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Lines.Enqueue(e.Data); };
                _process.Exited += (_, _) =>
                {
                    var done = Directory.Exists(target);
                    if (done)
                    {
                        Lines.Enqueue("=== 下载完成：" + target + " ===");
                        _steamcmdBrokenUntil = DateTime.MinValue; // 成功则清除故障记忆
                    }
                    else
                    {
                        var recent = string.Join("\n", Lines.ToArray().TakeLast(40));
                        if (BrokenSigns.Any(s => recent.Contains(s, StringComparison.OrdinalIgnoreCase)))
                        {
                            _steamcmdBrokenUntil = DateTime.UtcNow.AddHours(24);
                            Lines.Enqueue("=== SteamCMD 环境故障（24 小时内不再尝试，将自动改用 Steam 客户端/网页订阅） ===");
                        }
                        else
                        {
                            Lines.Enqueue("=== steamcmd 已退出（代码 " + _process.ExitCode + "），下载未完成 ===");
                        }
                    }
                    _state = done ? "done" : "error";
                };
                _state = "running";
                _lastId = id;
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                return new JsonObject { ["ok"] = true, ["mode"] = "steamcmd", ["target"] = target };
            }
            catch (Exception ex)
            {
                _state = "error";
                Lines.Enqueue("启动失败：" + ex.Message);
                return Fallback(ex.Message);
            }
        }
    }

    private static JsonObject Fallback(string why)
    {
        var client = SteamBeta.SteamRunning();
        return new JsonObject
        {
            ["ok"] = false,
            ["mode"] = client ? "client" : "web",
            ["steamRunning"] = client,
            ["error"] = why,
        };
    }

    public static JsonObject Status(ConfigStore cfg)
    {
        var lines = new JsonArray();
        foreach (var l in Lines.ToArray().TakeLast(300)) lines.Add(l);
        var running = _process is not null && !_process.HasExited;
        var o = new JsonObject
        {
            ["ok"] = true,
            ["state"] = running ? "running" : _state,
            ["lines"] = lines,
        };
        if (!string.IsNullOrEmpty(_lastId))
        {
            o["id"] = _lastId;
            var ws = WorkshopDir(cfg);
            if (ws is not null) o["downloaded"] = Directory.Exists(Path.Combine(ws, _lastId));
        }
        return o;
    }

    /// <summary>检查某 MOD 是否已出现在本机工坊目录（监视 Steam 客户端下载完成）。</summary>
    public static JsonObject Check(string id, ConfigStore cfg)
    {
        id = (id ?? "").Trim();
        var ws = WorkshopDir(cfg);
        var p = ws is null ? null : Path.Combine(ws, id);
        var exists = p is not null && Directory.Exists(p);
        long size = 0;
        if (exists)
        {
            try
            {
                var di = new DirectoryInfo(p!);
                size = di.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch { }
        }
        return new JsonObject { ["ok"] = true, ["exists"] = exists, ["size"] = size, ["dir"] = p ?? "" };
    }

    /// <summary>在 Steam 客户端内打开 MOD 详情页（steam:// 协议，非浏览器网页）。</summary>
    public static JsonObject OpenInClient(string id)
    {
        id = (id ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^\d{1,12}$"))
            return new JsonObject { ["ok"] = false, ["error"] = "无效的工坊 ID" };
        try
        {
            Process.Start(new ProcessStartInfo("steam://url/CommunityFilePage/" + id)
            { UseShellExecute = true });
            return new JsonObject { ["ok"] = true };
        }
        catch (Exception ex)
        {
            return new JsonObject { ["ok"] = false, ["error"] = ex.Message };
        }
    }
}
