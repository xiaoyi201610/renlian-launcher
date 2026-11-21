using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace RenlianLauncher.Core;

/// <summary>
/// 上传本地 mod 到 Steam 创意工坊。
/// 走官方 SteamCMD 的 workshop_build_item：先生成 workshop.vdf，再启动 steamcmd 上传。
/// 密码只在本次进程命令行里用，绝不写盘。
/// </summary>
public static class WorkshopUpload
{
    private static readonly ConcurrentQueue<string> Lines = new();
    private static Process? _process;
    private static readonly object Gate = new();
    private static string _state = "idle"; // idle | running | done | error

    public static string WorkDir => Path.Combine(Paths.DataDir, "workshop-upload");

    public static JsonObject Info(ConfigStore cfg)
    {
        var def = GameDefs.Get(cfg.CurrentGame);
        var cmd = FindSteamCmd();
        return new JsonObject
        {
            ["ok"] = true,
            ["game"] = cfg.CurrentGame,
            ["appid"] = def.AppId,
            ["steamcmd"] = cmd ?? "",
            ["found"] = !string.IsNullOrEmpty(cmd),
            ["workDir"] = WorkDir,
        };
    }

    private static string? FindSteamCmd()
    {
        var candidates = new List<string>();
        foreach (var root in Paths.SteamRoots())
        {
            candidates.Add(Path.Combine(root, "steamcmd", "steamcmd.exe"));
            candidates.Add(Path.Combine(root, "steamcmd.exe"));
        }
        candidates.Add(Path.Combine(WorkDir, "steamcmd", "steamcmd.exe"));
        candidates.Add(@"C:\Program Files (x86)\Steam\steamcmd\steamcmd.exe");
        candidates.Add(@"D:\steam\steamcmd\steamcmd.exe");
        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in pathEnv.Split(Path.PathSeparator))
        {
            try
            {
                var f = Path.Combine(p.Trim('"'), "steamcmd.exe");
                if (File.Exists(f)) return f;
            }
            catch { }
        }
        return null;
    }

    /// <summary>根据选中的本地 mod 生成 workshop.vdf（首次 publishedfileid=0，更新时填已有 ID）。</summary>
    public static JsonObject Prepare(ConfigStore cfg, string modKey, string title, string desc,
        string preview, string visibility, string publishedId)
    {
        try
        {
            var mods = ModIndex.Build(cfg);
            var m = mods.FirstOrDefault(x =>
                string.Equals(x["modFile"]?.ToString(), modKey, StringComparison.OrdinalIgnoreCase));
            if (m is null) return new JsonObject { ["ok"] = false, ["error"] = "找不到这个 mod" };

            var dir = m["dirPath"]?.ToString() ?? "";
            if (!Directory.Exists(dir)) return new JsonObject { ["ok"] = false, ["error"] = "mod 目录不存在：" + dir };

            if (string.IsNullOrWhiteSpace(title)) title = m["name"]?.ToString() ?? "unnamed mod";
            if (string.IsNullOrWhiteSpace(preview)) preview = m["thumb"]?.ToString() ?? "";
            if (!File.Exists(preview)) preview = "";

            var def = GameDefs.Get(cfg.CurrentGame);
            var pid = string.IsNullOrWhiteSpace(publishedId) ? "0" : publishedId.Trim();
            var vis = string.IsNullOrWhiteSpace(visibility) ? "0" : visibility.Trim();

            Directory.CreateDirectory(WorkDir);
            var vdf = Path.Combine(WorkDir, "workshop.vdf");
            var sb = new StringBuilder();
            sb.AppendLine("\"workshopitem\"");
            sb.AppendLine("{");
            sb.AppendLine($"  \"appid\" \"{def.AppId}\"");
            sb.AppendLine($"  \"publishedfileid\" \"{pid}\"");
            sb.AppendLine($"  \"contentfolder\" \"{Esc(dir)}\"");
            if (preview.Length > 0) sb.AppendLine($"  \"previewfile\" \"{Esc(preview)}\"");
            sb.AppendLine($"  \"visibility\" \"{vis}\"");
            sb.AppendLine($"  \"title\" \"{Esc(title)}\"");
            sb.AppendLine($"  \"description\" \"{Esc(desc)}\"");
            sb.AppendLine("}");
            File.WriteAllText(vdf, sb.ToString(), new UTF8Encoding(false));

            return new JsonObject
            {
                ["ok"] = true,
                ["vdf"] = vdf,
                ["vdfText"] = sb.ToString(),
                ["contentFolder"] = dir,
                ["preview"] = preview,
                ["appid"] = def.AppId,
                ["publishedId"] = pid,
            };
        }
        catch (Exception ex) { return new JsonObject { ["ok"] = false, ["error"] = ex.Message }; }
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>启动 SteamCMD 上传；日志可从 /api/workshop/upload-status 实时读取。</summary>
    public static JsonObject Run(string steamcmd, string user, string pass)
    {
        lock (Gate)
        {
            if (_process is not null && !_process.HasExited)
                return new JsonObject { ["ok"] = false, ["error"] = "已有上传任务在运行" };
            if (string.IsNullOrWhiteSpace(steamcmd) || !File.Exists(steamcmd))
                return new JsonObject { ["ok"] = false, ["error"] = "找不到 steamcmd.exe，请手动指定路径" };
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
                return new JsonObject { ["ok"] = false, ["error"] = "请填写 Steam 账号和密码（仅本次使用，不保存）" };

            var vdf = Path.Combine(WorkDir, "workshop.vdf");
            if (!File.Exists(vdf))
                return new JsonObject { ["ok"] = false, ["error"] = "请先生成上传配置" };

            while (Lines.TryDequeue(out _)) { }

            var psi = new ProcessStartInfo(steamcmd)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("+login");
            psi.ArgumentList.Add(user.Trim());
            psi.ArgumentList.Add(pass);
            psi.ArgumentList.Add("+workshop_build_item");
            psi.ArgumentList.Add(vdf);
            psi.ArgumentList.Add("+quit");

            try
            {
                _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _process.OutputDataReceived += (_, e) => { if (e.Data is not null) Lines.Enqueue(e.Data); };
                _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Lines.Enqueue(e.Data); };
                _process.Exited += (_, _) =>
                {
                    Lines.Enqueue("=== steamcmd 已退出，代码 " + _process.ExitCode + " ===");
                    _state = "done";
                };
                _state = "running";
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                return new JsonObject { ["ok"] = true };
            }
            catch (Exception ex)
            {
                _state = "error";
                Lines.Enqueue("启动失败：" + ex.Message);
                return new JsonObject { ["ok"] = false, ["error"] = ex.Message };
            }
        }
    }

    public static JsonObject Status()
    {
        var lines = new JsonArray();
        foreach (var l in Lines.ToArray().TakeLast(300)) lines.Add(l);
        var running = _process is not null && !_process.HasExited;
        return new JsonObject
        {
            ["ok"] = true,
            ["state"] = running ? "running" : _state,
            ["lines"] = lines,
        };
    }
}
