using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RenlianLauncher.Core;

/// <summary>
/// 配置读写。相比旧版：
///   · 配置目录不可写时自动落到 %LOCALAPPDATA%，不会再「设置存不住」
///   · 启动时自动补齐/修复各游戏的 gameRoot / userDir / workshopDir（路径探测层负责）
///   · 原子写入，避免断电/被杀进程时配置写坏
/// </summary>
public sealed class ConfigStore
{
    public string FilePath { get; }
    public JsonObject Root { get; private set; }

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 这个运行时环境下必须显式挂 resolver，否则 ToJsonString 会抛
        // "must specify a TypeInfoResolver..."，导致配置静默写不进去
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    private ConfigStore(string path, JsonObject root)
    {
        FilePath = path;
        Root = root;
    }

    public static ConfigStore Load()
    {
        // 统一放在 RenlianData\config；旧位置的文件已由 Paths.EnsureLayout() 迁移
        var candidates = new List<string> { Paths.ConfigPath };
        var exeCfg = Path.Combine(Paths.ExeDir, "launcher-config.json");
        if (!candidates.Contains(exeCfg, StringComparer.OrdinalIgnoreCase)) candidates.Add(exeCfg);

        foreach (var p in candidates)
        {
            try
            {
                if (!File.Exists(p)) continue;
                var node = JsonNode.Parse(File.ReadAllText(p, Encoding.UTF8));
                if (node is JsonObject obj)
                {
                    var store = new ConfigStore(Paths.ConfigPath, obj);
                    store.Normalize();
                    store.AutoHealPaths();
                    store.Save();
                    return store;
                }
            }
            catch { /* 配置坏了就重建，别让启动器打不开 */ }
        }

        var fresh = new ConfigStore(Paths.ConfigPath, new JsonObject());
        fresh.Normalize();
        fresh.AutoHealPaths();
        fresh.Save();
        return fresh;
    }

    // ------------------------------------------------------------ 基础字段

    public string CurrentGame
    {
        get => Str(Root, "currentGame") is { Length: > 0 } k && GameDefs.All.ContainsKey(k) ? k : "ck3";
        set => Root["currentGame"] = value;
    }

    public string UiLang
    {
        get => Str(Root, "uiLang") is { Length: > 0 } s ? s : "zh";
        set => Root["uiLang"] = value;
    }

    /// <summary>用户手动关联的 Steam 安装/库目录（全局，跨游戏共用）。</summary>
    public string SteamDir
    {
        get => Str(Root, "steamDir") ?? "";
        set => Root["steamDir"] = value;
    }

    public JsonObject Game(string key)
    {
        if (Root["games"] is not JsonObject games)
        {
            games = new JsonObject();
            Root["games"] = games;
        }
        if (games[key] is not JsonObject g)
        {
            g = new JsonObject();
            games[key] = g;
        }
        return g;
    }

    public string? GameStr(string key, string field) => Str(Game(key), field);
    public void SetGameStr(string key, string field, string value) => Game(key)[field] = value;

    // ------------------------------------------------------------ 合并视图（给前端）

    /// <summary>旧版 cfg 的等价物：默认值 + 指定游戏的扁平方段。</summary>
    public JsonObject MergedView(string key)
    {
        var g = Game(key);
        var def = GameDefs.Get(key);
        var view = new JsonObject
        {
            ["currentGame"] = key,
            ["gameRoot"] = Str(g, "gameRoot") ?? "",
            ["userDir"] = Str(g, "userDir") ?? "",
            ["workshopDir"] = Str(g, "workshopDir") ?? "",
            ["gameVersion"] = Str(g, "gameVersion") is { Length: > 0 } v ? v : def.DefaultVersion,
            ["autoBackup"] = Bool(Root, "autoBackup", true),
            ["rememberLastPlayset"] = Bool(Root, "rememberLastPlayset", true),
            ["minimizeOnLaunch"] = Bool(Root, "minimizeOnLaunch", false),
            ["closeOnLaunch"] = Bool(Root, "closeOnLaunch", false),
            ["useDataPathArg"] = Bool(Root, "useDataPathArg", false),
            ["eu4ForceOrder"] = Bool(Root, "eu4ForceOrder", false),
            ["steamDir"] = SteamDir,
            ["cacheDir"] = Str(Root, "cacheDir") ?? "",
            ["uiLang"] = UiLang,
            ["updateUrl"] = Str(Root, "updateUrl") ?? "https://gitee.com/xiaoyi2233/renlian-launcher/raw/master/version.json",
            ["lastPlayset"] = Str(g, "lastPlayset") ?? "",
        };

        // 多套版本配置：前端顶栏与设置页都直接读 config.profiles / activeProfile
        var (profiles, activeProfile) = Profiles.View(this, key);
        var pv = new JsonArray();
        foreach (var p in profiles) pv.Add(p);
        view["profiles"] = pv;
        view["activeProfile"] = activeProfile;

        // 右侧栏自定义排序（用户拖拽后保存的路由顺序）
        var navOrder = new JsonArray();
        if (Root["navOrder"] is JsonArray na)
            foreach (var n in na)
                if (n is JsonValue nv && nv.TryGetValue<string>(out var s) && s.Length > 0)
                    navOrder.Add(s);
        view["navOrder"] = navOrder;

        return view;
    }

    // ------------------------------------------------------------ 自动修补

    /// <summary>
    /// 启动时纠正路径：空的、指向不存在目录的，重新探测；
    /// 用户手填且有效的路径一律保留（不打扰玩家自己的设置）。
    /// </summary>
    public void AutoHealPaths()
    {
        foreach (var key in GameDefs.Order)
        {
            var def = GameDefs.Get(key);
            var g = Game(key);

            var groot = Str(g, "gameRoot");
            if (string.IsNullOrWhiteSpace(groot) || !Directory.Exists(groot))
            {
                var found = Paths.FindGameRoot(def);
                if (found is not null) g["gameRoot"] = found;
            }

            var udir = Str(g, "userDir");
            if (string.IsNullOrWhiteSpace(udir) || !Directory.Exists(udir))
            {
                var found = Paths.FindUserDir(def);
                if (found is not null) g["userDir"] = found;
            }

            var wdir = Str(g, "workshopDir");
            if (string.IsNullOrWhiteSpace(wdir) || !Directory.Exists(wdir))
            {
                var found = Paths.FindWorkshopDir(def, Str(g, "gameRoot"));
                if (found is not null) g["workshopDir"] = found;
            }

            if (Str(g, "gameVersion") is null or { Length: 0 })
                g["gameVersion"] = def.DefaultVersion;
        }
    }

    /// <summary>返回当前游戏缺失的路径项，供界面提示 / 一键修复。</summary>
    public List<object> PathIssues(string key)
    {
        var g = Game(key);
        var issues = new List<object>();
        void Check(string field, string label, string? hint)
        {
            var val = Str(g, field);
            if (!string.IsNullOrWhiteSpace(val) && Directory.Exists(val)) return;
            issues.Add(new
            {
                field,
                label,
                value = val ?? "",
                hint = hint ?? "",
                found = false,
            });
        }
        Check("gameRoot", "游戏本体目录", "可在「启动器设置」里手动选择游戏安装目录");
        Check("userDir", "玩家数据目录", "通常位于「文档\\Paradox Interactive」下，启动游戏一次会自动生成");
        Check("workshopDir", "创意工坊目录", "需要 Steam 已下载该游戏的工坊内容");
        return issues;
    }

    // ------------------------------------------------------------ 保存

    private void Normalize()
    {
        Root["schemaVersion"] ??= 2;
        Root["games"] ??= new JsonObject();
        if (Str(Root, "currentGame") is null) Root["currentGame"] = "ck3";
        if (Str(Root, "uiLang") is null) Root["uiLang"] = "zh";
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.ConfigDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, Root.ToJsonString(WriteOpts), new UTF8Encoding(true));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // 写不进去不能让启动器崩，但要留痕，方便排查「设置存不住」
            try { File.AppendAllText(Paths.LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 配置保存失败：{ex}\r\n"); } catch { }
        }
    }

    // ------------------------------------------------------------ JSON 小工具

    public static string? Str(JsonObject? obj, string name)
    {
        try
        {
            return obj?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        }
        catch { return null; }
    }

    public static bool Bool(JsonObject? obj, string name, bool fallback)
    {
        try
        {
            return obj?[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
        }
        catch { return fallback; }
    }
}
