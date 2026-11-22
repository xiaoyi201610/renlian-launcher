using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using RenlianLauncher.Core;

namespace RenlianLauncher.Api;

/// <summary>
/// 本地 API 服务：Kestrel 跑在 127.0.0.1 的随机端口上，界面（wwwroot）通过它取数据。
/// 路由契约与旧版 Python 服务保持一致，前端不需要改。
/// </summary>
public sealed class ApiServer
{
    public int Port { get; private set; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    private WebApplication? _app;
    private readonly ConfigStore _cfg;
    private List<JsonObject>? _mods;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public ApiServer(ConfigStore cfg) => _cfg = cfg;

    public async Task StartAsync()
    {
        Port = FreePort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "RenlianLauncher",
            ContentRootPath = Paths.ExeDir,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.AddServerHeader = false;
            o.Limits.MaxRequestBodySize = 1024L * 1024 * 1024;
        });
        builder.WebHost.UseUrls(BaseUrl);

        var app = builder.Build();

        // ---------------- 静态资源
        app.MapGet("/", (HttpContext ctx) => SendFile(ctx, Path.Combine(Paths.WebRoot, "index.html")));
        app.MapGet("/index.html", (HttpContext ctx) => SendFile(ctx, Path.Combine(Paths.WebRoot, "index.html")));
        app.MapGet("/static/{**rel}", (HttpContext ctx, string rel) =>
        {
            var safe = rel.Replace("..", "").TrimStart('/', '\\');
            var full = Path.GetFullPath(Path.Combine(Paths.WebRoot, safe));
            if (!full.StartsWith(Paths.WebRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                return Results.NotFound();
            return SendFile(ctx, full);
        });

        // ---------------- API
        app.MapGet("/api/ping", () => Json(new { ok = true }));

        app.MapGet("/api/state", () => Json(BuildState()));

        app.MapGet("/api/paths", () => Json(new { ok = true, items = new object[0] }));

        app.MapGet("/api/mods", (HttpContext ctx) =>
        {
            var force = ctx.Request.Query["force"] == "1";
            var items = GetMods(force);
            return Json(new { ok = true, items, count = items.Count });
        });

        // ---------------- 播放集（直接读写 launcher-v2.sqlite）

        app.MapGet("/api/playsets", () =>
        {
            var items = PlaysetDb.ForConfig(_cfg).ListPlaysets().Select(SummaryJson).ToArray();
            return Json(new { ok = true, items });
        });

        app.MapGet("/api/playset", (HttpContext ctx) =>
        {
            var db = PlaysetDb.ForConfig(_cfg);
            var id = ctx.Request.Query["id"].ToString();
            if (string.IsNullOrWhiteSpace(id)) id = db.ActivePlayset() ?? "";
            return Json(BuildPlaysetDetail(db, id));
        });

        app.MapPost("/api/playset/create", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var name = ConfigStore.Str(body, "name");
            var cloneFrom = ConfigStore.Str(body, "cloneFrom");
            return Json(Guard(() =>
            {
                var id = PlaysetDb.ForConfig(_cfg).CreatePlayset(
                    string.IsNullOrWhiteSpace(name) ? "新播放集" : name!, cloneFrom);
                return new { ok = true, id };
            }));
        });

        app.MapPost("/api/playset/rename", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var id = ConfigStore.Str(body, "id") ?? "";
            var name = ConfigStore.Str(body, "name");
            return Json(Guard(() =>
            {
                PlaysetDb.ForConfig(_cfg).RenamePlayset(id,
                    string.IsNullOrWhiteSpace(name) ? "未命名" : name!);
                return new { ok = true };
            }));
        });

        app.MapPost("/api/playset/delete", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var id = ConfigStore.Str(body, "id") ?? "";
            return Json(Guard(() =>
            {
                PlaysetDb.ForConfig(_cfg).DeletePlayset(id);
                return new { ok = true };
            }));
        });

        app.MapPost("/api/playset/activate", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var id = ConfigStore.Str(body, "id") ?? "";
            return Json(Guard(() =>
            {
                var db = PlaysetDb.ForConfig(_cfg);
                db.SetActive(id);
                db.SyncLoadFile(id, GetMods());
                return new { ok = true };
            }));
        });

        app.MapPost("/api/playset/sync", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            return Json(Guard(() =>
            {
                var db = PlaysetDb.ForConfig(_cfg);
                var id = ConfigStore.Str(body, "id");
                if (string.IsNullOrWhiteSpace(id)) id = db.ActivePlayset();
                // 兜底：官方启动器可能留下 isActive 全 0（实测 EU4：库里有 9 个 mod，
                // 播放集未激活 → dlc_load.json 一直是空的 → 游戏里 mod 全没生效）。
                // 此时自动激活第一个播放集再同步，与 playset/save 的兜底保持一致。
                var autoActivated = false;
                if (string.IsNullOrWhiteSpace(id))
                {
                    id = db.ListPlaysets().FirstOrDefault()?.Id ?? "";
                    if (id.Length > 0)
                    {
                        try { db.SetActive(id); autoActivated = true; } catch { }
                    }
                }
                if (string.IsNullOrWhiteSpace(id))
                    return Fail("没有可同步的播放集");
                var r = db.SyncLoadFile(id, GetMods());
                return new
                {
                    ok = r.Written,
                    backup = r.BackupPath is null ? "" : Path.GetFileName(r.BackupPath),
                    autoActivated,
                };
            }));
        });

        app.MapPost("/api/playset/save", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var psid = ConfigStore.Str(body, "id") ?? "";
            if (psid.Length == 0)
                return Json(new { ok = false, error = "缺少播放集 id" });

            return Json(Guard(() =>
            {
                var db = PlaysetDb.ForConfig(_cfg);
                if (!db.Exists)
                    return Fail($"找不到启动器数据库（{Path.GetFileName(db.DbPath)}）");
                if (db.IsLocked())
                    return Fail("官方启动器正在运行，请先关闭它再保存");

                var index = GetMods();
                var items = BuildWriteItems(body["items"] as JsonArray, index);

                if (ConfigStore.Bool(body, "setActive", false))
                {
                    try { db.SetActive(psid); } catch { /* 备份/写入失败不该中断保存 */ }
                }
                // 官方启动器可能留下 isActive 全 0：默认激活本次保存的播放集，
                // 否则 write_playset 不会同步到游戏读取的启停清单
                if (db.ActivePlayset() is null)
                {
                    try { db.SetActive(psid); } catch { }
                }

                var res = db.WritePlayset(psid, items, DlcmapOf(body), index);
                return new
                {
                    ok = true,
                    saved = items.Count,
                    synced = res.Synced,
                    autoRegistered = Array.Empty<string>(),
                    backup = res.BackupPath is null ? "" : Path.GetFileName(res.BackupPath),
                };
            }));
        });

        app.MapPost("/api/playset/save-as", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var id = ConfigStore.Str(body, "id");
            var db = PlaysetDb.ForConfig(_cfg);
            if (string.IsNullOrWhiteSpace(id)) id = db.ActivePlayset();
            if (string.IsNullOrWhiteSpace(id))
                return Json(new { ok = false, error = "没有可导出的播放集" });

            var export = BuildExport(db, id!);
            if (export is null)
                return Json(new { ok = false, error = "播放集不存在" });

            var safe = new string((export.Data["name"]?.ToString() ?? "playset")
                .Where(ch => ch is not ('\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
                .ToArray());
            if (safe.Length == 0) safe = "playset";

            var saved = UiInvoke(() => SaveExportFile(safe + ".json", export.Data));
            return Json(new
            {
                ok = saved.Ok,
                cancelled = saved.Cancelled,
                path = saved.Path,
                error = saved.Error,
                exported = (export.Data["mods"] as JsonArray)?.Count ?? 0,
                skippedLocal = export.SkippedLocal,
            });
        });

        app.MapPost("/api/playset/open-file", () =>
            Json(UiInvoke(PickAndReadPlaysetFile)));

        app.MapPost("/api/playset/import", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            return Json(Guard(() => ImportPlayset(body)));
        });

        app.MapPost("/api/switch-game", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var key = ConfigStore.Str(body, "game") ?? "";
            if (!GameDefs.All.ContainsKey(key))
                return Json(new { ok = false, error = "未知的游戏代号" });
            _cfg.CurrentGame = key;
            _cfg.AutoHealPaths();
            _cfg.Save();
            _mods = null;
            return Json(new { ok = true, state = BuildState() });
        });

        app.MapPost("/api/ui-lang", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var lang = ConfigStore.Str(body, "lang") ?? "zh";
            _cfg.UiLang = lang;
            _cfg.Save();
            return Json(new { ok = true, lang });
        });

        app.MapPost("/api/launch", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var debug = ConfigStore.Bool(body, "debug", false);
            return Json(LaunchGame(debug));
        });

        app.MapPost("/api/open", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var target = ConfigStore.Str(body, "path") ?? "";
            return Json(OpenPath(target));
        });

        // 新增：一键重新探测所有路径（老版本用户「找不到数据目录」的应急按钮）
        app.MapPost("/api/repair-paths", () =>
        {
            _cfg.AutoHealPaths();
            _cfg.Save();
            _mods = null;
            return Json(new { ok = true, state = BuildState() });
        });

        // ---------------- mod 文件操作 / 冲突排查 / 收藏夹

        app.MapGet("/api/collections", () =>
            Json(new { ok = true, items = ModOps.ListCollections(_cfg.CurrentGame) }));

        app.MapPost("/api/collection/create", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var name = (ConfigStore.Str(body, "name") ?? "").Trim();
            var ok = ModOps.CreateCollection(_cfg.CurrentGame, name);
            return Json(new { ok, error = ok ? "" : "名字已存在或为空" });
        });

        app.MapPost("/api/collection/rename", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var ok = ModOps.RenameCollection(_cfg.CurrentGame,
                (ConfigStore.Str(body, "old") ?? "").Trim(),
                (ConfigStore.Str(body, "new") ?? "").Trim());
            return Json(new { ok, error = ok ? "" : "重命名失败" });
        });

        app.MapPost("/api/collection/delete", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            ModOps.DeleteCollection(_cfg.CurrentGame, (ConfigStore.Str(body, "name") ?? "").Trim());
            return Json(new { ok = true });
        });

        app.MapPost("/api/collection/toggle", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var added = ModOps.ToggleInCollection(_cfg.CurrentGame,
                (ConfigStore.Str(body, "name") ?? "").Trim(),
                (ConfigStore.Str(body, "modFile") ?? "").Trim());
            return Json(new { ok = true, added });
        });

        // 自定义显示名：只改启动器里的名字，不动 .mod 文件
        app.MapPost("/api/mod/rename", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var modFile = (ConfigStore.Str(body, "modFile") ?? "").Trim();
            if (modFile.Length == 0) return Json(new { ok = false, error = "缺少 modFile" });
            try
            {
                ModOps.SetAlias(_cfg.CurrentGame, modFile, ConfigStore.Str(body, "alias") ?? "");
                RefreshModMeta();
                return Json(new { ok = true });
            }
            catch (Exception ex) { return Json(new { ok = false, error = "写入失败：" + ex.Message }); }
        });

        // 手动覆盖来源标签（local/steam/空=恢复自动判定）
        app.MapPost("/api/mod/source", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var modFile = (ConfigStore.Str(body, "modFile") ?? "").Trim();
            if (modFile.Length == 0) return Json(new { ok = false, error = "缺少 modFile" });
            try
            {
                ModOps.SetSourceOverride(_cfg.CurrentGame, modFile, ConfigStore.Str(body, "source") ?? "");
                RefreshModMeta();
                return Json(new { ok = true });
            }
            catch (Exception ex) { return Json(new { ok = false, error = "写入失败：" + ex.Message }); }
        });

        // 删除 mod（先备份描述文件，内容走回收站）
        app.MapPost("/api/mod/delete", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var modFile = (ConfigStore.Str(body, "modFile") ?? "").Trim();
            if (modFile.Length == 0) return Json(new { ok = false, error = "缺少 modFile" });
            var res = Guard(() =>
            {
                var r = ModOps.DeleteMod(_cfg, modFile);
                if (ConfigStore.Bool(r, "ok", false)) _mods = null;
                return r;
            });
            return Json(res);
        });

        // 流式冲突排查：逐 mod 回报进度，大播放集也不会「卡住没反馈」
        app.MapGet("/api/mod/conflicts/stream", async (HttpContext ctx) =>
        {
            var psid = ctx.Request.Query["playset"].ToString();
            ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var queue = new BlockingCollection<string>();
            var worker = Task.Run(() =>
            {
                try
                {
                    var result = ModConflicts.Detect(_cfg,
                        string.IsNullOrWhiteSpace(psid) ? null : psid, GetMods(),
                        (scanned, total) => queue.TryAdd(JsonSerializer.Serialize(
                            new { type = "progress", scanned, total }, JsonOpts)));
                    queue.TryAdd(JsonSerializer.Serialize(new { type = "done", result }, JsonOpts));
                }
                catch (Exception ex)
                {
                    queue.TryAdd(JsonSerializer.Serialize(
                        new { type = "error", ok = false, error = ex.Message }, JsonOpts));
                }
                finally { queue.CompleteAdding(); }
            });

            foreach (var line in queue.GetConsumingEnumerable())
            {
                try
                {
                    await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
                    await ctx.Response.Body.FlushAsync();
                }
                catch { break; }   // 页面关掉了就别再写了
            }
            try { await worker; } catch { }
        });

        app.MapGet("/api/consistency", () =>
            Json(Guard(() => ModConflicts.Consistency(_cfg, GetMods()))));

        // 批量给「已订阅但还没 .mod」的工坊 mod 生成描述文件
        app.MapPost("/api/register-all", () =>
        {
            var index = GetMods(true);
            var done = new List<string>();
            var failed = 0;
            var firstErr = "";
            foreach (var m in index)
            {
                if (!ConfigStore.Bool(m, "pending", false)) continue;
                var sid = (ConfigStore.Str(m, "steamId") ?? "").Trim();
                if (sid.Length == 0) continue;
                // 单个失败不能中断整批
                var r = ModOps.RegisterDescriptor(_cfg, sid);
                if (ConfigStore.Bool(r, "ok", false)) done.Add(sid);
                else
                {
                    failed++;
                    if (firstErr.Length == 0) firstErr = ConfigStore.Str(r, "error") ?? "未知错误";
                }
            }
            if (done.Count > 0) GetMods(true);
            return Json(new { ok = true, registered = done, failed, error = firstErr });
        });

        // 缩略图：压成 160px JPEG 落盘缓存，转不了的格式回原图
        app.MapGet("/api/thumb", (HttpContext ctx) =>
        {
            var p = ctx.Request.Query["p"].ToString();
            if (string.IsNullOrWhiteSpace(p) || !ModOps.IsPathAllowed(_cfg, p) || !File.Exists(p))
                return Results.StatusCode(StatusCodes.Status404NotFound);
            var file = ModOps.ThumbFile(p);
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var mime = ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => "application/octet-stream",
            };
            try
            {
                var fi = new FileInfo(file);
                ctx.Response.Headers.CacheControl = "public, max-age=3600";
                ctx.Response.Headers.ETag = $"\"{fi.LastWriteTimeUtc.Ticks:x}-{fi.Length:x}\"";
            }
            catch { }
            return Results.File(file, mime);
        });

        // 原生「打开」选压缩包 → 就地解压安装（拖放不灵时的备用入口）
        app.MapPost("/api/mod/pick-archive", () =>
        {
            var picked = UiInvoke(() =>
            {
                var dlg = new OpenFileDialog
                {
                    Title = "选择 Mod 压缩包",
                    Filter = "Mod 压缩包 (*.zip;*.7z;*.rar)|*.zip;*.7z;*.rar|ZIP 压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
                    CheckFileExists = true,
                };
                return ShowDialogOwned(dlg) ? dlg.FileName : "";
            });
            if (picked.Length == 0) return Json(new { ok = false, cancelled = true });

            var res = ModOps.InstallArchive(_cfg, picked, Path.GetFileName(picked));
            if (ConfigStore.Bool(res, "ok", false)) GetMods(true);
            return Json(res);
        });

        // 拖放上传：原始字节流 + X-File-Name 头（前端用 XHR 才有上传进度）
        app.MapPost("/api/mod/import-archive", async (HttpContext ctx) =>
        {
            var rawName = ctx.Request.Headers["X-File-Name"].ToString();
            string name;
            try { name = Uri.UnescapeDataString(rawName); } catch { name = rawName; }
            if (string.IsNullOrWhiteSpace(name)) name = "import.zip";

            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext is not (".zip" or ".7z" or ".rar"))
                return Json(new { ok = false, error = "只支持 .zip / .7z / .rar 压缩包" });
            if (ctx.Request.ContentLength is > 400L * 1024 * 1024)
                return Json(new { ok = false, error = "压缩包太大（超过 400MB）" });

            var tmpDir = Paths.TmpDir;
            var tmp = Path.Combine(tmpDir, "up_" + Guid.NewGuid().ToString("N")[..8] + ext);
            try
            {
                Directory.CreateDirectory(tmpDir);
                await using (var fs = File.Create(tmp))
                    await ctx.Request.Body.CopyToAsync(fs);
                var res = ModOps.InstallArchive(_cfg, tmp, name);
                if (ConfigStore.Bool(res, "ok", false)) GetMods(true);
                return Json(res);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = "接收上传失败：" + ex.Message });
            }
            finally { try { File.Delete(tmp); } catch { } }
        });

        // 打包第一步：统计 + 弹「另存为」
        app.MapPost("/api/mod/pack/prepare", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var modFile = (ConfigStore.Str(body, "modFile") ?? "").Trim();
            if (modFile.Length == 0) return Json(new { ok = false, error = "缺少 modFile" });

            var info = ModOps.PreparePack(_cfg, modFile);
            if (!info.Ok)
            {
                ModPack.MarkError(info.Error);
                return Json(new { ok = false, error = info.Error });
            }

            var safe = new string(info.Name
                .Where(ch => ch is not ('\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
                .ToArray()).Trim();
            if (safe.Length == 0) safe = "mod";

            // 先告诉前端「我在弹窗」，界面能显示提示而不是干等
            ModPack.MarkChoosing();
            var target = UiInvoke(() => PickSaveZip(safe + ".zip"));
            if (target.Length == 0)
            {
                ModPack.Reset();
                return Json(new { ok = true, cancelled = true, path = "" });
            }

            ModPack.MarkReady(info.FileCount + 1);
            return Json(new
            {
                ok = true,
                cancelled = false,
                path = target,
                name = info.Name,
                folder = info.Folder,
                fileCount = info.FileCount + 1,   // +1：会额外写顶层 .mod
                sizeBytes = info.SizeBytes,
            });
        });

        // 打包第二步：真正写 zip，过程中持续更新进度供前端轮询
        app.MapPost("/api/mod/pack/run", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var modFile = (ConfigStore.Str(body, "modFile") ?? "").Trim();
            var dest = (ConfigStore.Str(body, "path") ?? "").Trim();
            if (modFile.Length == 0) return Json(new { ok = false, error = "缺少 modFile" });
            if (dest.Length == 0) return Json(new { ok = false, error = "缺少保存路径" });

            var guess = ModOps.PreparePack(_cfg, modFile);
            ModPack.MarkPacking(guess.Ok ? guess.FileCount + 1 : 0);

            var res = await Task.Run(() =>
                ModOps.WritePack(_cfg, modFile, dest, (d, t, cur) => ModPack.Progress(d, t, cur)));
            if (ConfigStore.Bool(res, "ok", false)) ModPack.MarkDone((int)Long(res, "fileCount"));
            else ModPack.MarkError(ConfigStore.Str(res, "error") ?? "打包失败");
            return Json(res);
        });

        app.MapGet("/api/mod/pack/progress", () => Json(ModPack.Snapshot()));

        // ---------------- DLC / 存档 / 备份 / 游戏库（第 4 批）

        app.MapGet("/api/dlcs", (HttpContext ctx) => Json(Safe(() => Dlc.Build(
            _cfg,
            ctx.Request.Query["id"].ToString(),
            ctx.Request.Query["force"] == "1",
            ctx.Request.Query["steam"] == "1"))));

        app.MapGet("/api/dlc/detail", async (HttpContext ctx) =>
        {
            var detail = await SafeAsync(() => Dlc.DetailAsync(ctx.Request.Query["id"].ToString()));
            return Json(detail);
        });

        app.MapPost("/api/dlc/names-file", () => Json(Safe(Dlc.OpenNamesFile)));

        // 手动标记 DLC 拥有情况（owned = true / false / null 清除标记）
        app.MapPost("/api/dlc/override", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            return Json(Safe(() => Dlc.SetOverride(_cfg, body)));
        });

        // 强制重新扫描（重试 Steam；读不到自动降级为本地文件检测）
        app.MapPost("/api/dlc/recheck", () => Json(Safe(() => Dlc.Recheck(_cfg))));

        // 兼容补丁目录：只负责创建并打开，启动器不提供 / 不下载 / 不注入任何补丁文件
        app.MapPost("/api/patches/open", () => Json(Safe(Paths.OpenPatchesDir)));

        app.MapPost("/api/dlcs/set", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            return Json(Safe(() => Dlc.Set(_cfg, body, GetMods())));
        });

        app.MapPost("/api/dlcs/verify", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            return Json(Safe(() => Dlc.Verify(_cfg, ConfigStore.Str(body, "playsetId"))));
        });

        app.MapPost("/api/dlcs/open-dir", () => Json(Safe(() => Dlc.OpenDir(_cfg))));

        app.MapGet("/api/saves", () => Json(Safe(() => Saves.List(_cfg))));

        app.MapPost("/api/saves/delete", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var paths = (body["paths"] as JsonArray)?
                .Select(x => x?.ToString() ?? "")
                .Where(x => x.Length > 0)
                .ToArray() ?? Array.Empty<string>();
            return Json(Safe(() => Saves.Delete(_cfg, paths)));
        });

        app.MapPost("/api/backup", () => Json(Safe(() => Backups.Make(_cfg))));

        app.MapGet("/api/backups", () => Json(Safe(Backups.List)));

        app.MapPost("/api/backups/restore", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            return Json(Safe(() => Backups.Restore(_cfg, ConfigStore.Str(body, "name") ?? "")));
        });

        app.MapPost("/api/backups/clear", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            return Json(Safe(() => Backups.Clear(ConfigStore.Str(body, "tag"))));
        });

        app.MapGet("/api/gamedata", () => Json(Safe(BuildGameData)));

        app.MapPost("/api/add-game", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            return Json(Safe(() => AddGame(body)));
        });

        app.MapPost("/api/pick-dir", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            return Json(PickDir(body));
        });

        // ---------------- 成就 / 百科

        app.MapGet("/api/achievements", (HttpContext ctx) => Json(Safe(() =>
        {
            var game = _cfg.CurrentGame;
            var def = GameDefs.Get(game);
            var force = ctx.Request.Query["force"] == "1";
            return Achievements.Collect(def.AppId, _cfg.GameStr(game, "gameRoot") ?? "", force);
        })));

        app.MapGet("/api/ach-icon", (HttpContext ctx) =>
        {
            // 成就图标：从游戏本体 DDS 转 PNG（转一次缓存），不走 Steam CDN
            var name = ctx.Request.Query["n"].ToString().Trim();
            var gray = ctx.Request.Query["g"] == "1";
            var game = _cfg.CurrentGame;
            var def = GameDefs.Get(game);
            var img = Achievements.LocalIconPng(def.AppId, _cfg.GameStr(game, "gameRoot") ?? "", name, gray);
            return SendPng(ctx, img, 86400);
        });

        app.MapGet("/api/wiki", (HttpContext ctx) => Json(Safe(() =>
            Wiki.Load(_cfg.CurrentGame, _cfg.GameStr(_cfg.CurrentGame, "gameRoot") ?? "",
                _cfg.UiLang, ctx.Request.Query["refresh"] == "1"))));

        app.MapGet("/api/wiki/focus", (HttpContext ctx) => Json(Safe(() =>
            Wiki.FocusTree(_cfg.CurrentGame, _cfg.GameStr(_cfg.CurrentGame, "gameRoot") ?? "",
                ctx.Request.Query["tree"].ToString(), _cfg.UiLang))));

        app.MapGet("/api/wiki/focus-icon", (HttpContext ctx) =>
            SendPng(ctx, Wiki.FocusIcon(_cfg.GameStr(_cfg.CurrentGame, "gameRoot") ?? "",
                ctx.Request.Query["n"].ToString()), 604800));

        app.MapGet("/api/wiki/mission", (HttpContext ctx) => Json(Safe(() =>
            Wiki.MissionTree(_cfg.CurrentGame, _cfg.GameStr(_cfg.CurrentGame, "gameRoot") ?? "",
                ctx.Request.Query["tree"].ToString(), _cfg.UiLang))));

        app.MapGet("/api/wiki/mission-icon", (HttpContext ctx) =>
            SendPng(ctx, Wiki.MissionIcon(_cfg.GameStr(_cfg.CurrentGame, "gameRoot") ?? "",
                ctx.Request.Query["n"].ToString()), 604800));

        app.MapGet("/api/wiki/law-icon", (HttpContext ctx) =>
            SendPng(ctx, Wiki.LawIcon(_cfg.GameStr(_cfg.CurrentGame, "gameRoot") ?? "",
                ctx.Request.Query["n"].ToString(), ctx.Request.Query["k"].ToString()), 604800));

        // 用系统默认浏览器打开外部链接（Steam 商店 / 工坊 / 百科），只放行 http(s)
        app.MapPost("/api/url", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var url = ConfigStore.Str(body, "url") ?? "";
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return Json(new { ok = false, error = "invalid url" });
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return Json(new { ok = true });
            }
            catch (Exception ex) { return Json(new { ok = false, error = ex.Message }); }
        });

        // 内置浏览器面板：左侧 Steam 官网原生工坊页 + 右侧本机已订阅列表
        app.MapPost("/api/browser/open", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var url = ConfigStore.Str(body, "url") ?? "";
            if (!url.StartsWith("https://steamcommunity.com/", StringComparison.OrdinalIgnoreCase))
                return Json(new { ok = false, error = "只允许打开 Steam 社区页面" });
            try
            {
                var win = RenlianLauncher.App.LauncherWindow;
                if (win is null) return Json(new { ok = false, error = "主窗口不可用" });
                // 右侧已订阅列表数据由前端带上（它就是 S.mods 里的 steam 项）
                JsonArray? mods = body["mods"] as JsonArray;
                // OpenWorkshop 内部自己切 UI 线程；这里不要再包一层 Dispatcher
                win.OpenWorkshop(url, mods);
                return Json(new { ok = true });
            }
            catch (Exception ex) { return Json(new { ok = false, error = ex.Message }); }
        });
        app.MapPost("/api/browser/subscribed", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            try
            {
                var win = RenlianLauncher.App.LauncherWindow;
                if (win is null) return Json(new { ok = false, error = "主窗口不可用" });
                win.SetSubscribedMods(body["mods"] as JsonArray);
                return Json(new { ok = true });
            }
            catch (Exception ex) { return Json(new { ok = false, error = ex.Message }); }
        });
        app.MapPost("/api/browser/close", () =>
        {
            try
            {
                var win = RenlianLauncher.App.LauncherWindow;
                win?.CloseWorkshop();
                return Json(new { ok = true });
            }
            catch (Exception ex) { return Json(new { ok = false, error = ex.Message }); }
        });

        // 创意工坊浏览数据（懒狗式：直接在启动器页面铺 MOD 卡片网格）
        app.MapGet("/api/workshop/browse", async (HttpContext ctx) =>
        {
            var sort = ctx.Request.Query["sort"].ToString();
            var category = ctx.Request.Query["category"].ToString();
            var search = ctx.Request.Query["search"].ToString();
            var appid = ctx.Request.Query["appid"].ToString(); // 当前游戏的 Steam AppId（缺省回落 CK3）
            int.TryParse(ctx.Request.Query["page"], out var page);
            var data = await SafeAsync(() => WorkshopBrowse.Browse(sort, category, search, page, appid));
            return Json(data);
        });

        // 单个 MOD 详情（完整介绍 + 全字段，按 ID 缓存 30 分钟）
        app.MapGet("/api/workshop/detail", async (HttpContext ctx) =>
        {
            var id = ctx.Request.Query["id"].ToString();
            var data = await SafeAsync(() => WorkshopBrowse.Detail(id));
            return Json(data);
        });

        // 启动器直接下载 MOD（SteamCMD 匿名下载；失败时返回 client/web 降级模式）
        app.MapPost("/api/workshop/download/start", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var id = ConfigStore.Str(body, "id") ?? "";
            var data = await SafeAsync(() => Task.Run<object>(() => WorkshopDownload.Start(id, _cfg)));
            return Json(data);
        });
        app.MapGet("/api/workshop/download-status", () =>
            Json(Safe(() => WorkshopDownload.Status(_cfg))));
        app.MapGet("/api/workshop/download-check", (HttpContext ctx) =>
        {
            var id = ctx.Request.Query["id"].ToString();
            return Json(Safe(() => WorkshopDownload.Check(id, _cfg)));
        });
        // 在 Steam 客户端内打开 MOD 详情页（steam:// 协议，不是浏览器网页）
        app.MapPost("/api/steam/open-filepage", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var id = ConfigStore.Str(body, "id") ?? "";
            return Json(Safe(() => WorkshopDownload.OpenInClient(id)));
        });

        // ------------------------------------------------------------ 上传 mod 到 Steam 创意工坊
        app.MapGet("/api/workshop/upload-info", () => Json(Safe(() => WorkshopUpload.Info(_cfg))));
        // 预览图：自动读取不到时可手动选一张图片（返回绝对路径，上传时直接用）
        app.MapPost("/api/workshop/pick-preview", () =>
        {
            var picked = UiInvoke(() =>
            {
                var dlg = new OpenFileDialog
                {
                    Title = "选择预览图",
                    Filter = "图片 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|所有文件 (*.*)|*.*",
                    CheckFileExists = true,
                };
                return ShowDialogOwned(dlg) ? dlg.FileName : "";
            });
            return Json(picked.Length == 0
                ? new { ok = false, cancelled = true }
                : new { ok = true, path = picked });
        });
        app.MapPost("/api/workshop/upload-prepare", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            return Json(Safe(() => WorkshopUpload.Prepare(
                _cfg,
                ConfigStore.Str(body, "modKey") ?? "",
                ConfigStore.Str(body, "title") ?? "",
                ConfigStore.Str(body, "description") ?? "",
                ConfigStore.Str(body, "preview") ?? "",
                ConfigStore.Str(body, "visibility") ?? "0",
                ConfigStore.Str(body, "publishedId") ?? "0")));
        });
        app.MapPost("/api/workshop/upload-run", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            return Json(Safe(() => WorkshopUpload.Run(
                ConfigStore.Str(body, "steamcmd") ?? "",
                ConfigStore.Str(body, "user") ?? "",
                ConfigStore.Str(body, "pass") ?? "")));
        });
        app.MapGet("/api/workshop/upload-status", () => Json(Safe(WorkshopUpload.Status)));
        // Steam 直连上传（官方游戏内同款：借用 Steam 客户端登录态，免账号密码）
        app.MapGet("/api/workshop/upload-steam-info", () => Json(Safe(() => SteamWorkshopUpload.Info(_cfg))));
        app.MapPost("/api/workshop/upload-steam-start", async (HttpContext ctx) =>
        {
            try
            {
                var b = await JsonNode.ParseAsync(ctx.Request.Body);
                var r = SteamWorkshopUpload.Start(_cfg,
                    b?["modKey"]?.ToString() ?? "", b?["title"]?.ToString() ?? "", b?["description"]?.ToString() ?? "",
                    b?["preview"]?.ToString() ?? "", b?["visibility"]?.ToString() ?? "0",
                    b?["publishedId"]?.ToString() ?? "0", b?["changenote"]?.ToString() ?? "");
                return Json(r);
            }
            catch (Exception ex) { return Json(new JsonObject { ["ok"] = false, ["error"] = ex.Message }); }
        });
        app.MapGet("/api/workshop/upload-steam-status", () => Json(Safe(SteamWorkshopUpload.Status)));

        // ------------------------------------------------------------ Steam 版本切换
        app.MapGet("/api/steam/branches", () => Json(Safe(() => SteamBeta.BranchesInfo(_cfg))));
        // 兼容旧版前端：以前误用 POST 调这个端点（405），这里补一个别名兜底
        app.MapPost("/api/steam/branches", () => Json(Safe(() => SteamBeta.BranchesInfo(_cfg))));

        // Steam 关联状态与手动关联（顶栏「关联 Steam」按钮）
        app.MapGet("/api/steam/link-info", () => Json(Safe(() => SteamBeta.LinkInfo(_cfg))));

        app.MapPost("/api/steam/link", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var dir = ConfigStore.Str(body, "dir") ?? "";
            return Json(Safe(() => SteamBeta.Link(_cfg, dir)));
        });

        app.MapPost("/api/steam/switch", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var key = ConfigStore.Str(body, "key") ?? "";
            var restart = ConfigStore.Bool(body, "restart", true);
            return Json(Safe(() => SteamBeta.Switch(_cfg, key, restart)));
        });

        app.MapPost("/api/launch-official", () => Json(Safe(() => ShellOps.LaunchOfficial(_cfg))));

        // ------------------------------------------------------------ 更新检查
        app.MapPost("/api/check-update", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            return Json(await SafeAsync(() => ShellOps.CheckUpdateAsync(
                ConfigStore.Str(body, "url") ?? "",
                ConfigStore.Str(body, "current") ?? "")));
        });

        // ------------------------------------------------------------ 启动器配置
        app.MapPost("/api/config", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var result = ConfigOps.Apply(_cfg, body);
            _mods = null;
            return Json(result);
        });

        // ------------------------------------------------------------ 多套版本配置
        app.MapPost("/api/profile/add", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            return Json(Safe(() =>
            {
                var r = Profiles.Add(_cfg, body);
                if (!ConfigStore.Bool(r, "ok", false)) return r;
                _mods = null;
                return new { ok = true, pid = ConfigStore.Str(r, "pid"), state = BuildState() };
            }));
        });

        app.MapPost("/api/profile/update", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var pid = ConfigStore.Str(body, "id") ?? ConfigStore.Str(body, "pid") ?? "";
            return Json(Safe(() =>
            {
                var r = Profiles.Update(_cfg, pid, body);
                if (!ConfigStore.Bool(r, "ok", false)) return r;
                _mods = null;
                return new { ok = true, state = BuildState() };
            }));
        });

        app.MapPost("/api/profile/delete", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var pid = ConfigStore.Str(body, "id") ?? ConfigStore.Str(body, "pid") ?? "";
            return Json(Safe(() =>
            {
                var r = Profiles.Delete(_cfg, pid);
                if (!ConfigStore.Bool(r, "ok", false)) return r;
                _mods = null;
                return new { ok = true, state = BuildState() };
            }));
        });

        app.MapPost("/api/profile/switch", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var pid = ConfigStore.Str(body, "id") ?? ConfigStore.Str(body, "pid") ?? "";
            return Json(Safe(() =>
            {
                var r = Profiles.Switch(_cfg, pid);
                if (!ConfigStore.Bool(r, "ok", false)) return r;
                _mods = null;
                return new { ok = true, state = BuildState() };
            }));
        });

        // ------------------------------------------------------------ 崩溃诊断
        app.MapPost("/api/diag/collect", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            var dxdiag = ConfigStore.Bool(body, "dxdiag", false);
            return Json(await SafeAsync(() => Task.Run<object>(
                () => Diagnostics.Collect(_cfg, dxdiag))));
        });

        app.MapPost("/api/diag/open-dir", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            return Json(Safe(() => Diagnostics.OpenDir(ConfigStore.Str(body, "dir") ?? "")));
        });

        app.MapPost("/api/diag/save-as", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            return Json(Safe(() => Diagnostics.SaveAs(ConfigStore.Str(body, "zip") ?? "")));
        });

        // ------------------------------------------------------------ 内存 / 窗口 / 系统工具
        app.MapGet("/api/mem", () => Json(Safe(ShellOps.MemoryInfo)));
        app.MapPost("/api/mem/optimize", () => Json(Safe(ShellOps.OptimizeMemory)));
        app.MapPost("/api/mem/admin-restart", () => Json(Safe(ShellOps.RestartAsAdmin)));
        app.MapPost("/api/win/perf", () => Json(new { ok = ShellOps.OpenPerfOptions() }));
        app.MapPost("/api/open-tutorial", () => Json(Safe(ShellOps.OpenTutorial)));
        app.MapPost("/api/shortcut", () => Json(Safe(ShellOps.CreateShortcut)));
        app.MapPost("/api/exit", () => Json(new { ok = ShellOps.Exit() }));
        app.MapPost("/api/launcher/minimize", () => Json(new { ok = ShellOps.Minimize() }));

        // ------------------------------------------------------------ EU4 强制排序
        app.MapGet("/api/eu4/force-order", () => Json(Safe(() => ShellOps.Eu4Status(_cfg))));
        app.MapPost("/api/eu4/force-order", async (HttpContext ctx) =>
        {
            var body = await ReadJson(ctx);
            return Json(Safe(() => ShellOps.Eu4Toggle(_cfg, body, GetMods())));
        });

        // 还没迁移完的接口：明确报错，前端只会弹提示，不会白屏
        app.Map("/api/{**rest}", () => Json(new
        {
            ok = false,
            error = "该功能正在迁移到新版内核（v2），请稍后用旧版权限的同一操作",
            notImplemented = true,
        }));

        await app.StartAsync();
        _app = app;
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch { }
        try { await _app.DisposeAsync(); } catch { }
        _app = null;
    }

    // ------------------------------------------------------------ 业务

    private List<JsonObject> GetMods(bool force = false)
    {
        if (force || _mods is null)
        {
            // 重建才丢掉「自动判定值」快照；别名/收藏夹是 sidecar，索引本身不认
            if (force) ModOps.Forget();
            _mods = ModIndex.Build(_cfg, force);
        }
        ModOps.Enrich(_cfg.CurrentGame, _mods);
        return _mods;
    }

    /// <summary>别名/来源/收藏夹变化后刷新索引上的展示字段（不重扫磁盘）。</summary>
    private void RefreshModMeta()
    {
        if (_mods is not null) ModOps.Enrich(_cfg.CurrentGame, _mods);
    }

    private static string PickSaveZip(string defaultName)
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出 Mod 压缩包",
            FileName = defaultName,
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            Filter = "ZIP 压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
        };
        return ShowDialogOwned(dlg) ? dlg.FileName : "";
    }

    private object BuildState()
    {
        var game = _cfg.CurrentGame;
        var def = GameDefs.Get(game);

        var games = new List<object>();
        foreach (var key in GameDefs.Order)
        {
            var g = GameDefs.Get(key);
            var groot = _cfg.GameStr(key, "gameRoot");
            var udir = _cfg.GameStr(key, "userDir");
            games.Add(new
            {
                key,
                name = g.Name,
                en = g.En,
                gameRoot = groot ?? "",
                userDir = udir ?? "",
                installed = !string.IsNullOrWhiteSpace(groot) || !string.IsNullOrWhiteSpace(udir),
                cover = GameDefs.Cover(key),
                coverHeader = GameDefs.Cover(key, true),
            });
        }

        var gameDefs = GameDefs.All.ToDictionary(
            kv => kv.Key,
            kv => new { name = kv.Value.Name, en = kv.Value.En, appid = kv.Value.AppId });

        var merged = _cfg.MergedView(game);
        var userDir = ConfigStore.Str(merged, "userDir") ?? "";
        var gameRoot = ConfigStore.Str(merged, "gameRoot") ?? "";
        var exe = string.IsNullOrWhiteSpace(gameRoot) ? "" : GameDefs.ExePath(def, gameRoot);

        var db = PlaysetDb.ForConfig(_cfg);
        var playsets = db.ListPlaysets().Select(SummaryJson).ToArray();
        var activePlayset = db.ActivePlayset();
        var load = db.ReadLoadCounts();

        return new
        {
            config = merged,
            currentGame = game,
            games,
            gameDefs,
            version = ConfigStore.Str(merged, "gameVersion") ?? def.DefaultVersion,
            versionFull = ConfigStore.Str(merged, "gameVersion") ?? def.DefaultVersion,
            gameRoot,
            userDir,
            workshopDir = ConfigStore.Str(merged, "workshopDir") ?? "",
            exeExists = exe.Length > 0 && File.Exists(exe),
            dbExists = db.Exists,
            dbLocked = db.IsLocked(),
            playsets,
            activePlayset,
            dlcLoad = new
            {
                enabledCount = load.EnabledMods,
                disabledDlcCount = load.DisabledDlcs,
            },
            browserModUrl = $"https://steamcommunity.com/app/{def.AppId}/workshop/",
            browserDlcUrl = $"https://store.steampowered.com/dlc/{def.AppId}/",
            backupDir = Paths.BackupDir,
            cacheDir = Paths.CacheDir,
            cacheDirDefault = Paths.CacheDir,
            pathIssues = _cfg.PathIssues(game),
            dataDir = Paths.DataDir,
            portable = Paths.DataDirIsPortable,
        };
    }

    // ------------------------------------------------------------ 播放集辅助

    private static object Fail(string error) => new { ok = false, error };

    /// <summary>播放集写操作统一兜底：目录校验 + 占用/异常转友好提示。</summary>
    private object Guard(Func<object> fn)
    {
        var ud = _cfg.GameStr(_cfg.CurrentGame, "userDir");
        if (string.IsNullOrWhiteSpace(ud) || !Directory.Exists(ud))
            return Fail("找不到该游戏的用户数据目录，请在「启动器设置」里手动指定");
        try { return fn(); }
        catch (PlaysetLockedException) { return Fail(PlaysetDb.LockedMessage); }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    /// <summary>不依赖 userDir 的只读接口兜底：任何异常都转成 {ok:false,error}。</summary>
    private static object Safe(Func<object> fn)
    {
        try { return fn(); }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    /// <summary>异步接口兜底（DLC 商店详情要发网络请求）。</summary>
    private static async Task<object> SafeAsync(Func<Task<object>> fn)
    {
        try { return await fn(); }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    /// <summary>「游戏文件」页汇总信息（字段与旧版 api_gamedata 一致）。</summary>
    private object BuildGameData()
    {
        var game = _cfg.CurrentGame;
        var def = GameDefs.Get(game);
        var gameRoot = _cfg.GameStr(game, "gameRoot") ?? "";
        var userDir = _cfg.GameStr(game, "userDir") ?? "";
        var workshopDir = _cfg.GameStr(game, "workshopDir") ?? "";
        var exe = gameRoot.Length > 0 ? GameDefs.ExePath(def, gameRoot) : "";

        // 版本：优先游戏自带 launcher/launcher-settings.json
        var settings = new JsonObject();
        if (gameRoot.Length > 0)
        {
            foreach (var rel in new[] { "launcher/launcher-settings.json", "launcher-settings.json" })
            {
                var p = Path.Combine(gameRoot, rel.Replace('/', '\\'));
                if (!File.Exists(p)) continue;
                try
                {
                    settings = JsonNode.Parse(File.ReadAllText(p, Encoding.UTF8)) as JsonObject
                               ?? new JsonObject();
                    break;
                }
                catch { }
            }
        }
        var version = ConfigStore.Str(settings, "rawVersion");
        if (string.IsNullOrWhiteSpace(version)) version = ConfigStore.Str(settings, "version");
        if (string.IsNullOrWhiteSpace(version)) version = _cfg.GameStr(game, "gameVersion");
        if (string.IsNullOrWhiteSpace(version)) version = def.DefaultVersion;
        var versionFull = ConfigStore.Str(settings, "version") ?? "";

        var defaultUserDir = Paths.FindUserDir(def) ?? "";
        var isDefault = false;
        if (defaultUserDir.Length > 0 && userDir.Length > 0)
        {
            try
            {
                isDefault = string.Equals(
                    Path.GetFullPath(userDir).TrimEnd('\\'),
                    Path.GetFullPath(defaultUserDir).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { }
        }

        var mods = GetMods();
        var localMods = mods.Count(m => ConfigStore.Str(m, "source") == "local");
        var steamMods = mods.Count(m => ConfigStore.Str(m, "source") == "steam");

        return new
        {
            ok = true,
            gameRoot,
            exe,
            exeExists = exe.Length > 0 && File.Exists(exe),
            version,
            versionFull,
            userDir,
            defaultUserDir,
            isDefaultUserDir = isDefault,
            workshopDir,
            savesDir = Path.Combine(userDir, "save games"),
            modDir = Path.Combine(userDir, "mod"),
            modCount = mods.Count,
            localModCount = localMods,
            steamModCount = steamMods,
            saveCount = Saves.Count(_cfg),
            useDataPathArg = ConfigStore.Bool(_cfg.Root, "useDataPathArg", false),
        };
    }

    /// <summary>手动添加 / 自定义非 Steam 游戏目录：只写 games[key]，不动其它设置。</summary>
    private object AddGame(JsonObject body)
    {
        var key = ConfigStore.Str(body, "game") ?? "";
        if (!GameDefs.All.ContainsKey(key)) return Fail("未知游戏");
        var gameRoot = (ConfigStore.Str(body, "gameRoot") ?? "").Trim();
        var userDir = (ConfigStore.Str(body, "userDir") ?? "").Trim();
        if (gameRoot.Length > 0) _cfg.SetGameStr(key, "gameRoot", gameRoot);
        if (userDir.Length > 0) _cfg.SetGameStr(key, "userDir", userDir);
        _cfg.Save();
        _mods = null;
        return new { ok = true, state = BuildState() };
    }

    /// <summary>目录选择对话框：必须回到 WPF 界面线程，取消返回 cancelled。</summary>
    private static object PickDir(JsonObject body)
    {
        var initial = ConfigStore.Str(body, "initial") ?? "";
        var picked = UiInvoke(() =>
        {
            var dlg = new OpenFolderDialog { Title = "选择文件夹" };
            if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
                dlg.InitialDirectory = initial;
            return ShowDialogOwned(dlg) ? dlg.FolderName : "";
        });
        if (string.IsNullOrWhiteSpace(picked))
            return new { ok = false, cancelled = true, path = "" };
        return new { ok = true, cancelled = false, path = picked };
    }

    private static object SummaryJson(PlaysetSummary p) => new
    {
        id = p.Id,
        name = p.Name,
        isActive = p.IsActive,
        updatedOn = p.UpdatedOn,
        createdOn = p.CreatedOn,
        total = p.Total,
        enabled = p.Enabled,
    };

    private static Dictionary<string, JsonObject> ByModFile(List<JsonObject> index)
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in index)
        {
            var f = ConfigStore.Str(it, "modFile");
            if (!string.IsNullOrWhiteSpace(f)) map[f!] = it;
        }
        return map;
    }

    /// <summary>旧版 _rec_of：把 modFile 转成注册表记录所需字段。</summary>
    private static JsonObject RecOf(string modFile, JsonObject? meta)
    {
        var name = ConfigStore.Str(meta, "name");
        return new JsonObject
        {
            ["steamId"] = ConfigStore.Str(meta, "steamId"),
            ["name"] = name ?? Path.GetFileNameWithoutExtension(modFile),
            ["displayName"] = name,
            ["version"] = ConfigStore.Str(meta, "version") ?? "",
            ["dirPath"] = ConfigStore.Str(meta, "dirPath") ?? "",
            ["source"] = ConfigStore.Str(meta, "source") ?? "local",
        };
    }

    /// <summary>把前端的 items 转成写入项：已有 modId 直接用，本地 mod 按目录重新绑定身份。</summary>
    private static List<ModWrite> BuildWriteItems(JsonArray? raw, List<JsonObject> index)
    {
        var byFile = ByModFile(index);
        var list = new List<ModWrite>();
        foreach (var node in raw ?? new JsonArray())
        {
            if (node is not JsonObject it) continue;
            var mid = (ConfigStore.Str(it, "modId") ?? "").Trim();
            var mf = (ConfigStore.Str(it, "modFile") ?? "").Trim();
            var enabled = ConfigStore.Bool(it, "enabled", true);
            if (mid.Length == 0 && mf.Length == 0) continue;

            JsonObject? meta = mf.Length > 0 && byFile.TryGetValue(mf, out var m) ? m : null;
            if (mid.Length > 0)
            {
                // 存量修复：本地 mod 被存成工坊记录时，按目录重新绑定，避免身份错位
                if (meta is not null && ConfigStore.Str(meta, "source") == "local")
                {
                    list.Add(new ModWrite(null, RecOf(mf, meta), enabled));
                    continue;
                }
                list.Add(new ModWrite(mid, null, enabled));
                continue;
            }
            list.Add(new ModWrite(null, RecOf(mf, meta), enabled));
        }
        return list;
    }

    private static Dictionary<string, bool>? DlcmapOf(JsonObject body)
    {
        if (body["dlcs"] is not JsonObject dlcs || dlcs.Count == 0) return null;
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var kv in dlcs) map[kv.Key] = ConfigStore.Bool(dlcs, kv.Key, true);
        return map;
    }

    private object BuildPlaysetDetail(PlaysetDb db, string id)
    {
        var ps = db.GetPlayset(id);
        if (ps is null) return Fail("播放集不存在");

        var index = GetMods();
        var byFile = ByModFile(index);
        var id2file = db.ModIdToFileMap(index);
        var items = new List<object>();
        foreach (var m in ps.Mods)
        {
            id2file.TryGetValue(m.ModId, out var file);
            JsonObject? meta = file is not null && byFile.TryGetValue(file, out var mm) ? mm : null;
            items.Add(new
            {
                uid = m.ModId,                       // 稳定身份，前端拖拽与保存都用它
                modId = m.ModId,
                modFile = file,
                enabled = m.Enabled,
                position = m.Position,
                known = meta is not null,
                name = ConfigStore.Str(meta, "name") ?? m.DisplayName ?? m.Name ?? "(未注册的模组)",
                thumb = ConfigStore.Str(meta, "thumb") ?? "",
                steamId = ConfigStore.Str(meta, "steamId") ?? m.SteamId,
                supportedVersion = ConfigStore.Str(meta, "supportedVersion") ?? "",
                compat = ConfigStore.Str(meta, "compat") ?? "warn",
                source = ConfigStore.Str(meta, "source") ?? m.Source ?? "",
            });
        }

        return new
        {
            ok = true,
            id = ps.Id,
            name = ps.Name,
            isActive = ps.IsActive,
            items,
            dlcs = ps.Dlcs,
        };
    }

    private sealed record PlaysetExportFile(JsonObject Data, List<string> SkippedLocal);

    private sealed record SaveFileOutcome(bool Ok, bool Cancelled, string Path, string? Error);

    /// <summary>
    /// 导出为 P 社官方启动器的播放集文件，只写官方 schema 的字段：
    /// { "game": "ck3", "name": "...", "mods": [ { "displayName", "enabled", "position", "steamId" } ] }
    /// 官方格式只能表达 Steam 工坊模组；本地模组不会写入文件，改由返回值回报给界面。
    /// </summary>
    private PlaysetExportFile? BuildExport(PlaysetDb db, string id)
    {
        var ps = db.GetPlayset(id);
        if (ps is null) return null;

        var index = GetMods();
        var byFile = ByModFile(index);
        var id2file = db.ModIdToFileMap(index);
        var mods = new JsonArray();
        var skipped = new List<string>();
        var usedSteamIds = new HashSet<string>(StringComparer.Ordinal);
        var pos = 0;

        foreach (var row in ps.Mods)
        {
            id2file.TryGetValue(row.ModId, out var file);
            JsonObject? meta = file is not null && byFile.TryGetValue(file, out var mm) ? mm : null;

            // 官方导出用的是数据库里的 displayName（本地化显示名），不是描述文件里的 name
            var name = row.DisplayName;
            if (string.IsNullOrWhiteSpace(name)) name = ConfigStore.Str(meta, "name");
            if (string.IsNullOrWhiteSpace(name)) name = row.Name;

            var steamId = (ConfigStore.Str(meta, "steamId") ?? row.SteamId ?? "").Trim();
            if (steamId.Length == 0 || !steamId.All(char.IsDigit))
            {
                skipped.Add(string.IsNullOrWhiteSpace(name) ? (file ?? row.ModId) : name);
                continue;
            }
            // 官方要求同一播放集里 steamId 唯一，重复条目会让整包导入失败
            if (!usedSteamIds.Add(steamId)) continue;

            mods.Add(new JsonObject
            {
                ["displayName"] = string.IsNullOrWhiteSpace(name) ? steamId : name,
                ["enabled"] = row.Enabled,
                ["position"] = pos++,   // 官方按 position 排序，必须连续
                ["steamId"] = steamId,
            });
        }

        var data = new JsonObject
        {
            ["game"] = GameIdOf(_cfg.CurrentGame),
            ["name"] = ps.Name,
            ["mods"] = mods,
        };
        return new PlaysetExportFile(data, skipped);
    }

    /// <summary>官方启动器用的 gameId（launcher-settings.json），读不到就退回游戏 key。</summary>
    private string GameIdOf(string game)
    {
        try
        {
            var root = _cfg.GameStr(game, "gameRoot") ?? "";
            if (root.Length > 0)
            {
                foreach (var rel in new[] { "launcher\\launcher-settings.json", "launcher-settings.json" })
                {
                    var f = Path.Combine(root, rel);
                    if (!File.Exists(f)) continue;
                    if (JsonNode.Parse(File.ReadAllText(f, Encoding.UTF8)) is JsonObject o)
                    {
                        var gid = ConfigStore.Str(o, "gameId");
                        if (!string.IsNullOrWhiteSpace(gid)) return gid!;
                    }
                }
            }
        }
        catch { }
        return game;
    }

    /// <summary>文件对话框必须在界面线程上弹（请求线程是 MTA）。</summary>
    private static T UiInvoke<T>(Func<T> fn)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return fn();
        return app.Dispatcher.Invoke(fn);
    }

    /// <summary>
    /// 文件对话框必须绑定主窗口做 owner：无 owner 时对话框可能弹到主窗口后面或
    /// 只在任务栏闪烁，用户点了「浏览」却看不到，误以为没反应。
    /// 返回 true 表示用户确认。
    /// </summary>
    private static bool ShowDialogOwned(Microsoft.Win32.CommonDialog dlg)
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        return owner is not null
            ? dlg.ShowDialog(owner) == true
            : dlg.ShowDialog() == true;
    }

    private static SaveFileOutcome SaveExportFile(string defaultName, JsonObject data)
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出播放集",
            FileName = defaultName,
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true,
            Filter = "播放集文件 (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (!ShowDialogOwned(dlg)) return new SaveFileOutcome(true, true, "", null);
        try
        {
            // 这里必须显式给 TypeInfoResolver：临时创建的 options 走 JsonNode.ToJsonString 时
            // 会先被标记为只读，缺 resolver 会抛「must specify a TypeInfoResolver」
            var opts = new JsonSerializerOptions
            {
                // 官方导出的就是单行紧凑 JSON（无缩进、无 BOM、无结尾换行）
                WriteIndented = false,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
            };
            File.WriteAllText(dlg.FileName, data.ToJsonString(opts), new UTF8Encoding(false));
            return new SaveFileOutcome(true, false, dlg.FileName, null);
        }
        catch (Exception ex) { return new SaveFileOutcome(false, false, "", "写入失败：" + ex.Message); }
    }

    private static object PickAndReadPlaysetFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "导入播放集",
            Filter = "播放集文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (!ShowDialogOwned(dlg)) return new { ok = true, cancelled = true, path = "" };
        try
        {
            var data = JsonNode.Parse(File.ReadAllText(dlg.FileName, Encoding.UTF8));
            return new { ok = true, path = dlg.FileName, cancelled = false, data };
        }
        catch (Exception ex) { return new { ok = false, error = "文件解析失败：" + ex.Message }; }
    }

    private static long Long(JsonObject obj, string name)
    {
        try
        {
            return obj[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
        }
        catch { return 0; }
    }

    private static bool ModInstalled(JsonObject? meta)
    {
        if (meta is null) return false;
        var dp = (ConfigStore.Str(meta, "dirPath") ?? "").Trim();
        if (dp.Length > 0) return Directory.Exists(dp) || File.Exists(dp);
        return ConfigStore.Bool(meta, "valid", false);
    }

    /// <summary>
    /// 导入播放集：兼容 P 社官方导出的 {game,name,mods[{displayName,steamId,position,enabled}]}
    /// 和旧版人联启动器导出的文件（额外带 modFile / dlcs，官方格式不认这些字段）。
    /// </summary>
    private object ImportPlayset(JsonObject body)
    {
        var db = PlaysetDb.ForConfig(_cfg);
        if (!db.Exists) return Fail("找不到启动器数据库（launcher-v2.sqlite）");

        var data = body["data"] as JsonObject ?? new JsonObject();
        var name = (ConfigStore.Str(body, "name") ?? ConfigStore.Str(data, "name") ?? "").Trim();
        if (name.Length == 0) name = "导入的播放集";

        if (data["mods"] is not JsonArray mods || mods.Count == 0)
            return Fail("播放集文件里没有 mod 条目");

        // 官方文件带 game 字段（ck3 / stellaris / ...），跨游戏导入直接拦下
        var fileGame = (ConfigStore.Str(data, "game") ?? "").Trim();
        if (fileGame.Length > 0 &&
            !fileGame.Equals(GameIdOf(_cfg.CurrentGame), StringComparison.OrdinalIgnoreCase))
            return Fail($"这个播放集属于「{fileGame}」，请先切换到对应游戏再导入");

        // 强制重建索引：用户可能刚删过工坊目录，缓存里的 valid/source 会是旧值
        var index = GetMods(true);
        var byFile = ByModFile(index);
        var bySteam = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var byName = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in index)
        {
            var sid = (ConfigStore.Str(m, "steamId") ?? "").Trim();
            if (sid.Length > 0 && !bySteam.ContainsKey(sid)) bySteam[sid] = m;
            foreach (var nm in new[] { ConfigStore.Str(m, "name"), ConfigStore.Str(m, "displayName") })
                if (!string.IsNullOrWhiteSpace(nm) && !byName.ContainsKey(nm!)) byName[nm!] = m;
        }

        var ordered = new List<(string ModFile, bool Enabled)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var uninstalled = new List<string>();
        var missingSteamIds = new List<string>();
        var duplicates = 0;

        // 官方按 position 排序；没有 position 的旧文件保持自身顺序排到最后
        foreach (var item in mods.OfType<JsonObject>()
                     .OrderBy(x => x["position"] is null ? long.MaxValue : Long(x, "position")))
        {
            var sid = (ConfigStore.Str(item, "steamId") ?? "").Trim();
            var mf = (ConfigStore.Str(item, "modFile") ?? "").Trim();
            var nm = (ConfigStore.Str(item, "displayName") ?? ConfigStore.Str(item, "name") ?? "").Trim();

            JsonObject? hit = null;
            if (mf.Length > 0) byFile.TryGetValue(mf, out hit);                    // 旧版文件：最精确
            if (hit is null && sid.Length > 0) bySteam.TryGetValue(sid, out hit);  // 官方文件：steamId
            if (hit is null && nm.Length > 0) byName.TryGetValue(nm, out hit);     // 名称兜底
            if (hit is null)
            {
                missing.Add(nm.Length > 0 ? nm : (sid.Length > 0 ? sid : "?"));
                if (sid.Length > 0 && sid.All(char.IsDigit)) missingSteamIds.Add(sid);
                continue;
            }

            var file = ConfigStore.Str(hit, "modFile");
            if (string.IsNullOrWhiteSpace(file)) continue;
            if (!seen.Add(file!)) { duplicates++; continue; }

            // 索引里有记录，但实际文件/工坊目录已经不存在 → 也记一笔
            if (!ModInstalled(hit))
            {
                uninstalled.Add(ConfigStore.Str(hit, "name") ?? (nm.Length > 0 ? nm : sid));
                var hid = (ConfigStore.Str(hit, "steamId") ?? sid).Trim();
                if (hid.Length > 0 && hid.All(char.IsDigit)) missingSteamIds.Add(hid);
            }
            ordered.Add((file!, ConfigStore.Bool(item, "enabled", true)));
        }

        if (ordered.Count == 0)
            return Fail("文件里的模组在本机一个都没找到，请先在 Steam 订阅/安装这些模组");

        // 官方格式不含 DLC 选择；缺省时沿用当前激活播放集的 DLC 状态
        var dlcMap = DlcmapOf(data);
        var activeId = db.ActivePlayset();
        if (dlcMap is null && !string.IsNullOrWhiteSpace(activeId) &&
            db.GetPlayset(activeId!) is { } cur && cur.Dlcs.Count > 0)
            dlcMap = new Dictionary<string, bool>(cur.Dlcs, StringComparer.Ordinal);

        var newId = db.CreatePlayset(name, null);
        var writes = ordered
            .Select(o => new ModWrite(null, RecOf(o.ModFile,
                byFile.TryGetValue(o.ModFile, out var meta) ? meta : null), o.Enabled))
            .ToList();
        db.WritePlayset(newId, writes, dlcMap, index);

        var asked = missingSteamIds.Distinct().Count();
        return new
        {
            ok = true,
            id = newId,
            name,
            total = mods.Count,
            imported = ordered.Count,
            duplicates,
            missing,
            uninstalled,
            subscribe = new
            {
                asked,
                subscribed = 0,
                failed = Array.Empty<string>(),
                error = "",
            },
            degraded = true,   // Steam 自动订阅待迁移
        };
    }

    private object LaunchGame(bool debug)
    {
        var game = _cfg.CurrentGame;
        var def = GameDefs.Get(game);
        var root = _cfg.GameStr(game, "gameRoot") ?? "";
        if (string.IsNullOrWhiteSpace(root))
            return new { ok = false, error = "还没找到游戏本体目录，请在「启动器设置」里指定" };

        var exe = GameDefs.ExePath(def, root);
        if (!File.Exists(exe))
            return new { ok = false, error = $"找不到 {Path.GetFileName(exe)}，请重新指定游戏目录" };

        // Steam 就绪保障：Vic3 / EU5 等 Jomini 新引擎的工坊 mod 走 Steam UGC 后端——
        // Steam 客户端没运行时启动游戏，引擎会报 "UGC backend is not running"，
        // 工坊 mod 一个都不加载（实测日志 pdx_ugc.cpp:78），表现为「mod 排序/启用全部不生效」。
        // 所以启动前确保 Steam 在运行：没开就拉起并等它就绪（最多 15 秒），失败不阻塞启动。
        bool steamStarted = false, steamReady = true;
        if (!SteamBeta.SteamRunning())
        {
            steamStarted = TryStartSteam();
            steamReady = steamStarted && WaitSteamReady(TimeSpan.FromSeconds(15));
        }

        var args = new List<string>();
        if (ConfigStore.Bool(_cfg.Root, "useDataPathArg", false) &&
            _cfg.GameStr(game, "userDir") is { Length: > 0 } udir)
            args.Add($"-dataPath=\"{udir.Replace('/', '\\')}\"");
        if (debug) args.Add("-debug_mode");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
            return new { ok = true, args = new[] { exe }.Concat(args).ToArray(), steamStarted, steamReady };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    /// <summary>尝试拉起 Steam 客户端：优先 Steam 库目录里的 steam.exe，兜底 steam:// 协议。</summary>
    private static bool TryStartSteam()
    {
        foreach (var root in Paths.SteamRoots())
        {
            var exe = Path.Combine(root, "steam.exe");
            if (!File.Exists(exe)) continue;
            try
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                return true;
            }
            catch { /* 下一个 */ }
        }
        try
        {
            Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    /// <summary>轮询等 Steam 进程出现；出现后再稍等其初始化（Steamworks 连接要几秒）。</summary>
    private static bool WaitSteamReady(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (SteamBeta.SteamRunning())
            {
                Thread.Sleep(2500); // 给 Steam 完成自初始化的时间
                return true;
            }
            Thread.Sleep(500);
        }
        return false;
    }

    private static object OpenPath(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return new { ok = false, error = "路径为空" };
        try
        {
            // 网址（更新下载页等）交给默认浏览器打开
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                return new { ok = true };
            }
            if (Directory.Exists(target) || File.Exists(target))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
                return new { ok = true };
            }
            return new { ok = false, error = "路径不存在：" + target };
        }
        catch (Exception ex) { return new { ok = false, error = ex.Message }; }
    }

    // ------------------------------------------------------------ 工具

    private static IResult Json(object obj) =>
        Results.Text(JsonSerializer.Serialize(obj, JsonOpts), "application/json; charset=utf-8",
            Encoding.UTF8, StatusCodes.Status200OK);

    private static async Task<JsonObject> ReadJson(HttpContext ctx)
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            return JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) as JsonObject ?? new JsonObject();
        }
        catch { return new JsonObject(); }
    }

    /// <summary>返回 PNG（成就 / 国策 / 任务 / 法律图标）；找不到文件时 404，前端失败静默。</summary>
    private static IResult SendPng(HttpContext ctx, string path, int maxAge)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return Results.Text("", "image/png", Encoding.UTF8, StatusCodes.Status404NotFound);
        ctx.Response.Headers.CacheControl = $"public, max-age={maxAge}";
        return Results.File(path, "image/png");
    }

    private static IResult SendFile(HttpContext ctx, string path)
    {
        if (!File.Exists(path)) return Results.NotFound();
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var mime = ext switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            _ => "application/octet-stream",
        };
        ctx.Response.Headers.CacheControl = "no-cache";
        return Results.File(path, mime, enableRangeProcessing: true);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
