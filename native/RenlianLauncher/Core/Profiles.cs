using System.Text.Json.Nodes;

namespace RenlianLauncher.Core;

/// <summary>
/// 多套「版本配置」（profile）：每个游戏可有多个独立的本体目录 / 工坊目录 / 用户数据目录。
///
/// 存储位置：launcher-config.json → games[key].profiles（数组）+ games[key].activeProfile。
/// 与旧版一致：
///   · 首次新增时把现有扁平配置保留为「默认」版本，不丢数据；
///   · 新增版本若没填数据目录，自动在默认目录旁建一个「xxx - 版本名」目录；
///   · 切换版本会把该配置的三个路径回填到 games[key] 的扁平字段并保存；
///   · 删到只剩一条时拒绝删除。
/// </summary>
public static class Profiles
{
    private static readonly string[] PathFields = { "gameRoot", "userDir", "workshopDir" };

    // ------------------------------------------------------------ 读取

    private static JsonArray ArrayOf(JsonObject g)
    {
        if (g["profiles"] is JsonArray a) return a;
        var na = new JsonArray();
        g["profiles"] = na;
        return na;
    }

    /// <summary>返回 (带 active 标记的配置列表, 激活 id)；无 profiles 时合成一个「默认」项。</summary>
    public static (List<JsonObject> Items, string ActiveId) View(ConfigStore cfg, string code)
    {
        var g = cfg.Game(code);
        var arr = g["profiles"] as JsonArray;
        var active = ConfigStore.Str(g, "activeProfile");

        if (arr is null || arr.Count == 0)
        {
            var def = GameDefs.Get(code);
            var ver = ConfigStore.Str(g, "gameVersion");
            var item = new JsonObject
            {
                ["id"] = "default",
                ["name"] = string.IsNullOrWhiteSpace(ver) ? (def.DefaultVersion.Length > 0 ? def.DefaultVersion : "默认") : ver,
                ["active"] = true,
                ["gameVersion"] = ver ?? "",
                ["gameRoot"] = ConfigStore.Str(g, "gameRoot") ?? "",
                ["userDir"] = ConfigStore.Str(g, "userDir") ?? "",
                ["workshopDir"] = ConfigStore.Str(g, "workshopDir") ?? "",
            };
            return (new List<JsonObject> { item }, "default");
        }

        if (string.IsNullOrWhiteSpace(active)) active = ConfigStore.Str(arr[0] as JsonObject, "id");
        var list = new List<JsonObject>();
        foreach (var node in arr)
        {
            if (node is not JsonObject p) continue;
            var item = new JsonObject
            {
                ["id"] = ConfigStore.Str(p, "id") ?? "",
                ["name"] = ConfigStore.Str(p, "name") ?? "",
                ["active"] = (ConfigStore.Str(p, "id") ?? "") == active,
                ["gameVersion"] = ConfigStore.Str(p, "gameVersion") ?? "",
                ["gameRoot"] = ConfigStore.Str(p, "gameRoot") ?? "",
                ["userDir"] = ConfigStore.Str(p, "userDir") ?? "",
                ["workshopDir"] = ConfigStore.Str(p, "workshopDir") ?? "",
            };
            list.Add(item);
        }
        return (list, active ?? "");
    }

    // ------------------------------------------------------------ 写入

    public static JsonObject Add(ConfigStore cfg, JsonObject body)
    {
        var code = cfg.CurrentGame;
        var g = cfg.Game(code);
        var arr = ArrayOf(g);

        // 旧格式首次建 profile：先把现有扁平配置作为「默认」版本存进去
        if (arr.Count == 0)
        {
            var legacy = PathFields.Select(f => ConfigStore.Str(g, f)).Any(v => !string.IsNullOrWhiteSpace(v))
                         || !string.IsNullOrWhiteSpace(ConfigStore.Str(g, "gameVersion"));
            if (legacy)
            {
                arr.Add(new JsonObject
                {
                    ["id"] = "default",
                    ["name"] = ConfigStore.Str(g, "gameVersion") is { Length: > 0 } lv
                        ? lv : GameDefs.Get(code).DefaultVersion,
                    ["gameVersion"] = ConfigStore.Str(g, "gameVersion") ?? "",
                    ["gameRoot"] = ConfigStore.Str(g, "gameRoot") ?? "",
                    ["userDir"] = ConfigStore.Str(g, "userDir") ?? "",
                    ["workshopDir"] = ConfigStore.Str(g, "workshopDir") ?? "",
                });
            }
        }

        var name = (ConfigStore.Str(body, "name") ?? "").Trim();
        if (name.Length == 0) name = "新版本";
        var pid = NewId(arr);

        var udir = (ConfigStore.Str(body, "userDir") ?? "").Trim();
        if (udir.Length == 0)
        {
            // 新版本默认用独立数据目录：否则空目录会被自动探测回默认目录，
            // 破坏「版本之间文件互相独立」
            var baseDir = (ConfigStore.Str(g, "userDir") ?? "").Trim();
            if (baseDir.Length == 0) baseDir = Paths.FindUserDir(GameDefs.Get(code)) ?? "";
            if (baseDir.Length > 0)
            {
                try
                {
                    var full = baseDir.TrimEnd('\\', '/');
                    var parent = Path.GetDirectoryName(full);
                    var leaf = Path.GetFileName(full);
                    var safe = new string(name.Where(ch => ch is not ('\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')).ToArray()).Trim();
                    if (safe.Length == 0) safe = pid;
                    if (!string.IsNullOrEmpty(parent))
                    {
                        var cand = Path.Combine(parent, $"{leaf} - {safe}");
                        Directory.CreateDirectory(cand);
                        udir = cand;
                    }
                }
                catch { }
            }
        }

        var prof = new JsonObject
        {
            ["id"] = pid,
            ["name"] = name,
            ["gameVersion"] = (ConfigStore.Str(body, "gameVersion") ?? "").Trim(),
            ["gameRoot"] = (ConfigStore.Str(body, "gameRoot") ?? "").Trim(),
            ["userDir"] = udir,
            ["workshopDir"] = (ConfigStore.Str(body, "workshopDir") ?? "").Trim(),
        };
        arr.Add(prof);
        g["activeProfile"] = pid;
        ApplyFlat(g, prof);
        cfg.Save();
        return new JsonObject { ["ok"] = true, ["pid"] = pid };
    }

    public static JsonObject Update(ConfigStore cfg, string pid, JsonObject body)
    {
        var g = cfg.Game(cfg.CurrentGame);
        var arr = g["profiles"] as JsonArray;
        var target = arr?.OfType<JsonObject>().FirstOrDefault(p => ConfigStore.Str(p, "id") == pid);
        if (target is null) return Fail("版本配置不存在");

        foreach (var f in new[] { "name", "gameVersion", "gameRoot", "userDir", "workshopDir" })
        {
            if (!body.ContainsKey(f)) continue;
            var v = (ConfigStore.Str(body, f) ?? "").Trim();
            if (f == "name" && v.Length == 0) v = "未命名";
            target[f] = v;
        }

        if (ConfigStore.Str(g, "activeProfile") == pid) ApplyFlat(g, target);
        cfg.Save();
        return new JsonObject { ["ok"] = true };
    }

    public static JsonObject Delete(ConfigStore cfg, string pid)
    {
        var g = cfg.Game(cfg.CurrentGame);
        var arr = g["profiles"] as JsonArray;
        if (arr is null || !arr.OfType<JsonObject>().Any(p => ConfigStore.Str(p, "id") == pid))
            return Fail("版本配置不存在");
        if (arr.Count <= 1) return Fail("至少保留一个版本配置");

        for (int i = arr.Count - 1; i >= 0; i--)
        {
            if (arr[i] is JsonObject p && ConfigStore.Str(p, "id") == pid) arr.RemoveAt(i);
        }

        if (ConfigStore.Str(g, "activeProfile") == pid &&
            arr[0] is JsonObject first)
        {
            g["activeProfile"] = ConfigStore.Str(first, "id") ?? "";
            ApplyFlat(g, first);
        }
        cfg.Save();
        return new JsonObject { ["ok"] = true };
    }

    public static JsonObject Switch(ConfigStore cfg, string pid)
    {
        var g = cfg.Game(cfg.CurrentGame);
        var arr = g["profiles"] as JsonArray;
        var target = arr?.OfType<JsonObject>().FirstOrDefault(p => ConfigStore.Str(p, "id") == pid);
        if (target is null) return Fail("版本配置不存在");

        g["activeProfile"] = pid;
        ApplyFlat(g, target);

        // 用户显式指定的数据目录还不存在 → 直接建出来，避免 AutoHeal 把它改回默认目录
        var udir = ConfigStore.Str(target, "userDir");
        if (!string.IsNullOrWhiteSpace(udir))
        {
            try { Directory.CreateDirectory(udir!); } catch { }
        }
        cfg.Save();
        return new JsonObject { ["ok"] = true };
    }

    /// <summary>把当前扁平路径同步回「激活」的版本配置（设置页改路径后调用）。</summary>
    public static void SyncActiveFromFlat(ConfigStore cfg)
    {
        var code = cfg.CurrentGame;
        var g = cfg.Game(code);
        var active = ConfigStore.Str(g, "activeProfile");
        if (string.IsNullOrWhiteSpace(active) || g["profiles"] is not JsonArray arr) return;
        var target = arr.OfType<JsonObject>().FirstOrDefault(p => ConfigStore.Str(p, "id") == active);
        if (target is null) return;
        target["gameRoot"] = ConfigStore.Str(g, "gameRoot") ?? "";
        target["userDir"] = ConfigStore.Str(g, "userDir") ?? "";
        target["workshopDir"] = ConfigStore.Str(g, "workshopDir") ?? "";
        target["gameVersion"] = ConfigStore.Str(g, "gameVersion") ?? "";
    }

    private static void ApplyFlat(JsonObject g, JsonObject p)
    {
        g["gameRoot"] = ConfigStore.Str(p, "gameRoot") ?? "";
        g["userDir"] = ConfigStore.Str(p, "userDir") ?? "";
        g["workshopDir"] = ConfigStore.Str(p, "workshopDir") ?? "";
        var ver = ConfigStore.Str(p, "gameVersion");
        if (!string.IsNullOrWhiteSpace(ver)) g["gameVersion"] = ver;
    }

    private static string NewId(JsonArray arr)
    {
        while (true)
        {
            var id = "p" + Guid.NewGuid().ToString("N")[..8];
            if (!arr.OfType<JsonObject>().Any(p => ConfigStore.Str(p, "id") == id)) return id;
        }
    }

    private static JsonObject Fail(string error) => new() { ["ok"] = false, ["error"] = error };
}

/// <summary>
/// /api/config 的写入逻辑（路由层只负责解析 body）。
/// 与旧版一致：路径去引号、换本体目录时跟随重探工坊目录、缓存目录写前验证、
/// 保存后返回 warnings 与 pathIssues。
/// </summary>
public static class ConfigOps
{
    private static readonly string[] BoolKeys =
    {
        "autoBackup", "rememberLastPlayset", "minimizeOnLaunch",
        "closeOnLaunch", "useDataPathArg", "eu4ForceOrder",
    };

    private static string CleanPath(string? v)
    {
        var s = (v ?? "").Trim();
        if (s.Length >= 2 && s[0] == s[^1] && s[0] is '"' or '\'')
            s = s[1..^1].Trim();
        return s;
    }

    public static object Apply(ConfigStore cfg, JsonObject body)
    {
        var game = cfg.CurrentGame;
        var g = cfg.Game(game);
        var oldRoot = (ConfigStore.Str(g, "gameRoot") ?? "").Trim();
        var oldWs = (ConfigStore.Str(g, "workshopDir") ?? "").Trim();

        if (ConfigStore.Bool(body, "resetPaths", false))
        {
            foreach (var f in new[] { "gameRoot", "userDir", "workshopDir" }) g[f] = "";
        }
        else
        {
            foreach (var f in new[] { "gameRoot", "userDir", "workshopDir" })
                if (body.ContainsKey(f)) g[f] = CleanPath(ConfigStore.Str(body, f));
            if (ConfigStore.Str(body, "gameVersion") is { Length: > 0 } gv) g["gameVersion"] = gv;
        }

        // 换了游戏本体目录、且没有单独指定工坊目录 → 让工坊目录重新探测
        var newRoot = (ConfigStore.Str(g, "gameRoot") ?? "").Trim();
        if (newRoot.Length > 0 && oldRoot.Length > 0 &&
            !newRoot.Equals(oldRoot, StringComparison.OrdinalIgnoreCase) &&
            (ConfigStore.Str(g, "workshopDir") ?? "").Trim().Equals(oldWs, StringComparison.OrdinalIgnoreCase))
            g["workshopDir"] = "";

        if (body.ContainsKey("updateUrl"))
            cfg.Root["updateUrl"] = (ConfigStore.Str(body, "updateUrl") ?? "").Trim();

        foreach (var k in BoolKeys)
            if (body.ContainsKey(k)) cfg.Root[k] = ConfigStore.Bool(body, k, false);

        // 右侧栏拖拽排序：只认字符串、去重，空数组表示恢复默认
        if (body["navOrder"] is JsonArray order)
        {
            var clean = new JsonArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in order)
            {
                if (n is not JsonValue nv || !nv.TryGetValue<string>(out var s)) continue;
                s = s.Trim();
                if (s.Length == 0 || !seen.Add(s)) continue;
                clean.Add(s);
            }
            cfg.Root["navOrder"] = clean;
        }

        // 缓存目录：留空=跟随程序目录；填了先验证能否创建/写入
        if (body.ContainsKey("cacheDir"))
        {
            var cd = CleanPath(ConfigStore.Str(body, "cacheDir"));
            if (cd.Length > 0)
            {
                try { Directory.CreateDirectory(cd); }
                catch (Exception ex)
                {
                    return new { ok = false, error = $"缓存目录无法创建：{cd}（{ex.Message}）" };
                }
                try
                {
                    var probe = Path.Combine(cd, ".write-probe-" + Guid.NewGuid().ToString("N")[..8]);
                    using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                }
                catch
                {
                    return new { ok = false, error = $"缓存目录没有写入权限：{cd}" };
                }
            }
            cfg.Root["cacheDir"] = cd;
        }

        if (ConfigStore.Bool(body, "resetPaths", false)) cfg.AutoHealPaths();

        var warnings = new List<string>();
        foreach (var (f, label) in new[]
                 {
                     ("gameRoot", "游戏根目录"),
                     ("userDir", "用户数据目录"),
                     ("workshopDir", "创意工坊目录"),
                 })
        {
            var v = (ConfigStore.Str(g, f) ?? "").Trim();
            if (v.Length > 0 && !Directory.Exists(v)) warnings.Add($"{label}不存在：{v}");
        }

        Profiles.SyncActiveFromFlat(cfg);
        cfg.Save();

        return new
        {
            ok = true,
            config = cfg.MergedView(game),
            warnings,
            pathIssues = cfg.PathIssues(game),
        };
    }
}
