using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Steamworks;

namespace RenlianLauncher.Core;

/// <summary>
/// Steam 直连上传（官方游戏内同款机制）：走 Steamworks ISteamUGC 接口，
/// 借用本机正在运行的 Steam 客户端登录态 —— 无需账号密码、无需验证码。
/// 前置条件：Steam 客户端已启动并登录；steam_api64.dll（从游戏目录自动复制）。
/// SteamCMD（WorkshopUpload）保留为降级路径。
/// </summary>
public static class SteamWorkshopUpload
{
    private static readonly object Gate = new();
    private static System.Timers.Timer? _timer;
    private static bool _inited;

    private static string _state = "idle"; // idle | running | done | error
    private static string _phase = "";     // preparing | creating | editing | uploading | processing | committing
    private static string _error = "";
    private static ulong _itemId;          // PublishedFileId
    private static ulong _bytesDone;
    private static ulong _bytesTotal;
    private static readonly List<string> Lines = new();

    private static UGCUpdateHandle_t _handle;
    private static SteamAPICall_t _pendingCall;
    private static CallResult<CreateItemResult_t>? _createCr;
    private static CallResult<SubmitItemUpdateResult_t>? _submitCr;
    private static AppId_t _appId;
    /// <summary>Steamworks 初始化时用的 appid：切换游戏后必须 Shutdown 重启，否则上传会进错游戏的工坊。</summary>
    private static string _initedAppId = "";

    private static void Say(string s)
    {
        lock (Lines)
        {
            Lines.Add($"[{DateTime.Now:HH:mm:ss}] {s}");
            if (Lines.Count > 400) Lines.RemoveRange(0, Lines.Count - 400);
        }
    }

    // ------------------------------------------------------------ 可用性

    /// <summary>steam_api64.dll：优先用已复制的，否则从游戏目录取一份（Valve 官方再分发组件）。</summary>
    private static string? EnsureSteamApiDll(ConfigStore cfg)
    {
        var dst = Path.Combine(AppContext.BaseDirectory, "steam_api64.dll");
        if (File.Exists(dst)) return dst;

        var candidates = new List<string>();
        var groot = cfg.Game(cfg.CurrentGame)?["gameRoot"]?.ToString() ?? "";
        if (!string.IsNullOrWhiteSpace(groot))
        {
            candidates.Add(Path.Combine(groot, "steam_api64.dll"));
            candidates.Add(Path.Combine(groot, "binaries", "steam_api64.dll"));
        }
        foreach (var root in Paths.SteamRoots())
        {
            // 跟随当前游戏目录找 steam_api64.dll，而不是写死 CK3
            candidates.Add(Path.Combine(root, "steamapps", "common", GameDefs.Get(cfg.CurrentGame).Folder, "steam_api64.dll"));
        }
        foreach (var c in candidates)
        {
            try
            {
                if (!File.Exists(c)) continue;
                File.Copy(c, dst, overwrite: true);
                Say("已从游戏目录获取 steam_api64.dll：" + c);
                return dst;
            }
            catch { /* 下一个 */ }
        }
        return null;
    }

    public static JsonObject Info(ConfigStore cfg)
    {
        bool steamRunning = false;
        try { steamRunning = Process.GetProcessesByName("steam").Length > 0; } catch { }
        string? dll = null;
        try { dll = EnsureSteamApiDll(cfg); } catch { }
        var def = GameDefs.Get(cfg.CurrentGame);
        return new JsonObject
        {
            ["ok"] = true,
            ["game"] = cfg.CurrentGame,
            ["appid"] = def.AppId,
            ["steamRunning"] = steamRunning,
            ["dllReady"] = !string.IsNullOrEmpty(dll),
            ["ready"] = steamRunning && !string.IsNullOrEmpty(dll),
        };
    }

    // ------------------------------------------------------------ 启动

    public static JsonObject Start(ConfigStore cfg, string modKey, string title, string desc,
        string preview, string visibility, string publishedId, string changenote)
    {
        lock (Gate)
        {
            if (_state == "running")
                return new JsonObject { ["ok"] = false, ["error"] = "已有上传任务在进行中" };

            _state = "running"; _phase = "preparing"; _error = "";
            _itemId = 0; _bytesDone = 0; _bytesTotal = 0;
            lock (Lines) Lines.Clear();

            try
            {
                var def = GameDefs.Get(cfg.CurrentGame);
                _appId = new AppId_t(uint.Parse(def.AppId));

                // ① steam_api64.dll + steam_appid.txt（Steamworks.NET 识别 appid 的官方方式）
                var dll = EnsureSteamApiDll(cfg);
                if (string.IsNullOrEmpty(dll))
                {
                    Fail($"找不到 steam_api64.dll：请确认游戏路径设置正确（Steam 已安装 {def.Name}）");
                    return new JsonObject { ["ok"] = false, ["error"] = _error };
                }
                var appidTxt = Path.Combine(AppContext.BaseDirectory, "steam_appid.txt");
                // 内容必须与当前游戏一致：切换游戏后覆写，否则 Steamworks 还认上一个 appid
                var curAppidTxt = File.Exists(appidTxt) ? File.ReadAllText(appidTxt).Trim() : "";
                if (curAppidTxt != def.AppId) File.WriteAllText(appidTxt, def.AppId);

                // ② staging：本地 mod → 工坊 content 结构（descriptor.mod + 资源）
                _phase = "preparing";
                var staging = PrepareStaging(cfg, modKey, out var modTags);
                if (staging is null)
                {
                    Fail(_error.Length > 0 ? _error : "准备上传内容失败");
                    return new JsonObject { ["ok"] = false, ["error"] = _error };
                }

                // ③ 初始化 Steamworks（Steam 客户端必须在运行）
                // appid 变了（切换过游戏）要 Shutdown 重启，让 Steam 按新 appid 重新认证
                if (!_inited || _initedAppId != def.AppId)
                {
                    if (_inited)
                    {
                        try { SteamAPI.Shutdown(); } catch { }
                        _inited = false;
                        Say($"检测到游戏切换（{_initedAppId} → {def.AppId}），重新初始化 Steamworks");
                    }
                    _phase = "preparing";
                    bool ok = SteamAPI.Init();
                    if (!ok)
                    {
                        Fail("Steam 初始化失败。请先启动 Steam 客户端并登录，再试一次。");
                        return new JsonObject { ["ok"] = false, ["error"] = _error };
                    }
                    _inited = true;
                    _initedAppId = def.AppId;
                    Say("Steamworks 已连接本机 Steam 客户端（免密码模式）");
                }

                // ④ 回调链：CreateItem（新物品）→ StartItemUpdate → Set* → SubmitItemUpdate → 完成
                // CreateItem 是异步回调，Start 返回后回调才到——参数先挂到 pending 字段
                _pendingStaging = staging; _pendingTitle = title; _pendingDesc = desc;
                _pendingPreview = preview; _pendingVis = visibility; _pendingNote = changenote;
                _pendingTags = modTags;
                _createCr = CallResult<CreateItemResult_t>.Create(OnCreateItem);
                _submitCr = CallResult<SubmitItemUpdateResult_t>.Create(OnSubmitItemUpdate);

                var pid = string.IsNullOrWhiteSpace(publishedId) ? 0UL : ulong.Parse(publishedId.Trim());
                if (pid == 0)
                {
                    _phase = "creating";
                    Say("创建新创意工坊物品…");
                    _pendingCall = SteamUGC.CreateItem(_appId, EWorkshopFileType.k_EWorkshopFileTypeCommunity);
                    _createCr.Set(_pendingCall);
                }
                else
                {
                    BeginUpdate(pid, staging, title, desc, preview, visibility, changenote, modTags);
                }

                StartTimer();
                return new JsonObject { ["ok"] = true, ["state"] = _state };
            }
            catch (DllNotFoundException)
            {
                Fail("找不到 steam_api64.dll，无法直连上传");
                return new JsonObject { ["ok"] = false, ["error"] = _error };
            }
            catch (Exception ex)
            {
                Fail("直连上传启动失败：" + ex.Message);
                return new JsonObject { ["ok"] = false, ["error"] = _error };
            }
        }
    }

    private static void Fail(string msg)
    {
        _state = "error"; _error = msg; Say("✖ " + msg);
        StopTimer();
    }

    // ------------------------------------------------------------ staging

    /// <summary>把本地 mod（X.mod + X/）整理成工坊 content 结构：descriptor.mod + 资源平铺。</summary>
    private static string? PrepareStaging(ConfigStore cfg, string modKey, out List<string> tags)
    {
        tags = new List<string>();
        var mods = ModIndex.Build(cfg);
        var m = mods.FirstOrDefault(x =>
            string.Equals(x["modFile"]?.ToString(), modKey, StringComparison.OrdinalIgnoreCase));
        if (m is null) { _error = "找不到这个 mod"; return null; }
        var dir = m["dirPath"]?.ToString() ?? "";
        var modFile = m["modFile"]?.ToString() ?? "";
        if (!Directory.Exists(dir)) { _error = "mod 目录不存在：" + dir; return null; }
        // modFile 通常相对 userDir\mod（如 mod\xxx.mod），拼成绝对路径
        if (!File.Exists(modFile))
        {
            var udir = cfg.Game(cfg.CurrentGame)?["userDir"]?.ToString() ?? "";
            var abs = Path.Combine(udir, modFile);
            if (File.Exists(abs)) modFile = abs;
        }
        if (!File.Exists(modFile)) { _error = "mod 描述文件不存在：" + modFile; return null; }

        var staging = Path.Combine(WorkshopUpload.WorkDir, "staging-steam", Path.GetFileNameWithoutExtension(modFile));
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            // 资源平铺复制（工坊 content = descriptor + 资源）
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly))
                File.Copy(f, Path.Combine(staging, Path.GetFileName(f)), overwrite: true);
            foreach (var d in Directory.GetDirectories(dir, "*", SearchOption.TopDirectoryOnly))
                CopyDir(d, Path.Combine(staging, Path.GetFileName(d)));

            // 工坊版 descriptor.mod：去掉 path 行（订阅后由 Steam 结构决定），其余原样保留
            var descText = File.ReadAllText(modFile, Encoding.UTF8);
            descText = Regex.Replace(descText, @"(?im)^\s*path\s*=.*$", "");
            File.WriteAllText(Path.Combine(staging, "descriptor.mod"), descText, new UTF8Encoding(false));

            // tags 解析（tags={ "A" "B" }）
            var tm = Regex.Match(descText, @"(?is)tags\s*=\s*\{(.*?)\}");
            if (tm.Success)
                foreach (Match t in Regex.Matches(tm.Groups[1].Value, @"""([^""]+)"""))
                    tags.Add(t.Groups[1].Value);

            Say($"上传内容已就绪：{staging}");
            return staging;
        }
        catch (Exception ex)
        {
            _error = "整理上传内容失败：" + ex.Message;
            return null;
        }
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.TopDirectoryOnly))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src, "*", SearchOption.TopDirectoryOnly))
            CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    // ------------------------------------------------------------ 回调链

    private static void OnCreateItem(CreateItemResult_t r, bool ioFailure)
    {
        lock (Gate)
        {
            if (ioFailure || r.m_eResult != EResult.k_EResultOK)
            {
                Fail($"创建物品失败：{r.m_eResult}（若提示需要同意法律协议，请先在 Steam 工坊网页上手动同意一次）");
                return;
            }
            _itemId = r.m_nPublishedFileId.m_PublishedFileId;
            Say($"新物品已创建：{_itemId}（此时为私密状态，可稍后在工坊页改公开）");
            // 重新走更新链（staging 已备好，存到字段里）
            if (_pendingStaging is null) { Fail("内部错误：staging 丢失"); return; }
            BeginUpdate(_itemId, _pendingStaging, _pendingTitle, _pendingDesc, _pendingPreview,
                _pendingVis, _pendingNote, _pendingTags);
        }
    }

    private static string? _pendingStaging, _pendingTitle, _pendingDesc, _pendingPreview, _pendingVis, _pendingNote;
    private static List<string>? _pendingTags;

    private static void BeginUpdate(ulong pid, string staging, string title, string desc,
        string preview, string visibility, string changenote, List<string> tags)
    {
        _pendingStaging = staging; _pendingTitle = title; _pendingDesc = desc;
        _pendingPreview = preview; _pendingVis = visibility; _pendingNote = changenote; _pendingTags = tags;
        _itemId = pid;

        _handle = SteamUGC.StartItemUpdate(_appId, new PublishedFileId_t(pid));
        Say("开始更新物品 " + pid);
        if (!string.IsNullOrWhiteSpace(title)) SteamUGC.SetItemTitle(_handle, title.Trim());
        if (!string.IsNullOrWhiteSpace(desc)) SteamUGC.SetItemDescription(_handle, desc.Trim());
        if (!string.IsNullOrWhiteSpace(preview) && File.Exists(preview))
            SteamUGC.SetItemPreview(_handle, preview);
        SteamUGC.SetItemUpdateLanguage(_handle, "schinese");
        // 前端语义与 Steam 枚举一致：0=公开 1=好友可见 2=私有
        var vis = int.TryParse(visibility, out var v) ? v : 0;
        SteamUGC.SetItemVisibility(_handle, (ERemoteStoragePublishedFileVisibility)vis);
        if (tags is { Count: > 0 }) SteamUGC.SetItemTags(_handle, tags);
        SteamUGC.SetItemContent(_handle, staging); // 关键：内容文件夹

        _phase = "uploading";
        Say("提交上传" + (string.IsNullOrWhiteSpace(changenote) ? "" : "（变更说明：" + changenote.Trim() + "）"));
        _pendingCall = SteamUGC.SubmitItemUpdate(_handle, string.IsNullOrWhiteSpace(changenote) ? null : changenote.Trim());
        _submitCr!.Set(_pendingCall);
    }

    private static void OnSubmitItemUpdate(SubmitItemUpdateResult_t r, bool ioFailure)
    {
        lock (Gate)
        {
            if (ioFailure || r.m_eResult != EResult.k_EResultOK)
            {
                Fail($"提交失败：{r.m_eResult}" + (r.m_eResult == EResult.k_EResultInsufficientPrivilege
                    ? "（Steam 账号受限：新账号需消费后才能上传工坊）" : ""));
                return;
            }
            _itemId = r.m_nPublishedFileId.m_PublishedFileId;
            _state = "done"; _phase = "";
            Say($"✔ 上传完成！创意工坊物品 ID：{_itemId}");
            Say("新上传的物品默认是私密的，可到工坊页面把它改为公开。");
            StopTimer();
        }
    }

    // ------------------------------------------------------------ 回调泵 + 进度

    private static void StartTimer()
    {
        _timer = new System.Timers.Timer(100) { AutoReset = true };
        _timer.Elapsed += (_, _) =>
        {
            try
            {
                SteamAPI.RunCallbacks();
                if (_state == "running" && _handle.m_UGCUpdateHandle != 0)
                {
                    var p = SteamUGC.GetItemUpdateProgress(_handle, out var done, out var total);
                    _bytesDone = done; _bytesTotal = total;
                    _phase = p switch
                    {
                        EItemUpdateStatus.k_EItemUpdateStatusPreparingContent => "preparing",
                        EItemUpdateStatus.k_EItemUpdateStatusUploadingContent => "uploading",
                        EItemUpdateStatus.k_EItemUpdateStatusUploadingPreviewFile => "uploading",
                        EItemUpdateStatus.k_EItemUpdateStatusCommittingChanges => "committing",
                        _ => _phase,
                    };
                }
            }
            catch { /* Steam 退出等：下轮再试 */ }
        };
        _timer.Start();
    }

    private static void StopTimer()
    {
        try { _timer?.Stop(); _timer?.Dispose(); } catch { }
        _timer = null;
        _handle = default;
    }

    public static JsonObject Status()
    {
        lock (Lines)
        {
            var lines = new JsonArray();
            foreach (var l in Lines.TakeLast(300)) lines.Add(l);
            var pct = _bytesTotal > 0 ? (int)(_bytesDone * 100 / _bytesTotal) : 0;
            return new JsonObject
            {
                ["ok"] = true,
                ["state"] = _state,
                ["phase"] = _phase,
                ["pct"] = pct,
                ["done"] = _bytesDone,
                ["total"] = _bytesTotal,
                ["itemId"] = _itemId,
                ["error"] = _error,
                ["lines"] = lines,
            };
        }
    }
}
