using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RenlianLauncher.Core;

/// <summary>
/// Steam 成就解析（旧版 achievements.py 的原生移植）。
///
/// 数据来源（除全球完成率外全本地）：
///   1. &lt;Steam&gt;/appcache/stats/UserGameStatsSchema_&lt;appid&gt;.bin
///      —— 成就定义（中英文名、描述、图标 hash、所属 DLC 分组）
///   2. &lt;Steam&gt;/appcache/stats/UserGameStats_&lt;steamid&gt;_&lt;appid&gt;.bin
///      —— 本地解锁记录（AchievementTimes：block + index → Unix 时间戳）
///   3. Steam 公开接口 GetGlobalAchievementPercentagesForApp —— 全球完成率
/// 两个 .bin 是 Steam 二进制 KeyValues（VDF），下面自带最小解析器。
/// </summary>
public static class Achievements
{
    private const double RarePercent = 5.0;
    private const int CacheVersion = 2;

    private static readonly object Lock = new();
    private static readonly Dictionary<string, JsonObject> GlobalMem = new(StringComparer.Ordinal);

    // ------------------------------------------------------------ 二进制 VDF

    /// <summary>解析 Steam 二进制 KeyValues；解不出来的部分安静跳过。</summary>
    private static Dictionary<string, object> ParseVdfBin(byte[] data)
    {
        var pos = 0;
        var n = data.Length;

        string ReadStr()
        {
            var end = Array.IndexOf(data, (byte)0, pos);
            if (end < 0) throw new InvalidDataException("字符串未闭合");
            var s = Encoding.UTF8.GetString(data, pos, end - pos);
            pos = end + 1;
            return s;
        }

        Dictionary<string, object> ReadObj()
        {
            var res = new Dictionary<string, object>(StringComparer.Ordinal);
            while (pos < n)
            {
                var t = data[pos];
                pos++;
                if (t == 0x08) return res;                       // 块结束
                if (t == 0x00)                                   // 子块
                {
                    var k = ReadStr();
                    res[k] = ReadObj();
                }
                else if (t == 0x01)                              // 字符串
                {
                    var k = ReadStr();
                    res[k] = ReadStr();
                }
                else if (t == 0x02)                              // int32
                {
                    var k = ReadStr();
                    res[k] = BitConverter.ToInt32(data, pos);
                    pos += 4;
                }
                else if (t == 0x07)                              // uint64
                {
                    var k = ReadStr();
                    res[k] = BitConverter.ToUInt64(data, pos);
                    pos += 8;
                }
                else return res;                                 // 未知类型：返回已解出的部分
            }
            return res;
        }

        try { return ReadObj(); }
        catch { return new Dictionary<string, object>(StringComparer.Ordinal); }
    }

    private static Dictionary<string, object> Dict(object? o) =>
        o as Dictionary<string, object> ?? new Dictionary<string, object>(StringComparer.Ordinal);

    private static string StrOf(object? o) => o as string ?? "";

    // ------------------------------------------------------------ 路径定位

    /// <summary>所有找到的 Steam &lt;root&gt;/appcache/stats 目录（大小写不同的同一路径只留一个）。</summary>
    private static List<string> StatsDirs()
    {
        var outList = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in Paths.SteamRoots())
        {
            var p = Path.Combine(root, "appcache", "stats");
            try
            {
                var key = Path.GetFullPath(p).ToLowerInvariant();
                if (Directory.Exists(p) && seen.Add(key)) outList.Add(p);
            }
            catch { }
        }
        return outList;
    }

    private static string SchemaPath(string appId)
    {
        foreach (var d in StatsDirs())
        {
            var p = Path.Combine(d, "UserGameStatsSchema_" + appId + ".bin");
            if (File.Exists(p)) return p;
        }
        return "";
    }

    /// <summary>所有账号的 UserGameStats_&lt;steamid&gt;_&lt;appid&gt;.bin（按体积从大到小）。</summary>
    private static List<string> UnlockPaths(string appId)
    {
        var found = new List<(long Size, string Path)>();
        var suffix = "_" + appId + ".bin";
        foreach (var d in StatsDirs())
        {
            try
            {
                foreach (var p in Directory.EnumerateFiles(d, "UserGameStats_*.bin"))
                {
                    var fn = Path.GetFileName(p);
                    if (!fn.StartsWith("UserGameStats_", StringComparison.Ordinal) ||
                        !fn.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                    found.Add((new FileInfo(p).Length, p));
                }
            }
            catch { }
        }
        return found.OrderByDescending(x => x.Size).Select(x => x.Path).ToList();
    }

    // ------------------------------------------------------------ 成就定义

    private static string Loc(object? m, string lang = "schinese")
    {
        var d = m as Dictionary<string, object>;
        if (d is null) return "";
        var v = d.TryGetValue(lang, out var a) ? StrOf(a) : "";
        if (v.Length == 0) v = d.TryGetValue("english", out var b) ? StrOf(b) : "";
        return v.Trim();
    }

    private sealed record AchievementDef(
        string Block, string Idx, string ApiName, string Name, string NameEn,
        string Desc, string DescEn, bool Hidden, string Icon, string IconGray, string Group);

    /// <summary>返回 (成就定义列表, 分组 {groupid: 名称})。</summary>
    private static (List<AchievementDef> Defs, Dictionary<string, string> Groups) LoadDefinitions(string appId)
    {
        var defs = new List<AchievementDef>();
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = SchemaPath(appId);
        if (path.Length == 0) return (defs, groups);

        Dictionary<string, object> tree;
        try { tree = ParseVdfBin(File.ReadAllBytes(path)); }
        catch { return (defs, groups); }

        var app = tree.TryGetValue(appId, out var a) ? Dict(a)
            : new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var kv in Dict(app.TryGetValue("groups", out var g) ? g : null))
        {
            if (kv.Value is not Dictionary<string, object> gd) continue;
            gd.TryGetValue("name", out var nm);
            groups[kv.Key] = nm is Dictionary<string, object> ? Loc(nm) : StrOf(nm);
        }

        foreach (var blockKv in Dict(app.TryGetValue("stats", out var st) ? st : null))
        {
            if (blockKv.Value is not Dictionary<string, object> blockData) continue;
            blockData.TryGetValue("type", out var typ);
            if (!StrOf(typ).ToUpperInvariant().Equals("ACHIEVEMENTS", StringComparison.Ordinal)) continue;

            foreach (var bitKv in Dict(blockData.TryGetValue("bits", out var bits) ? bits : null))
            {
                if (bitKv.Value is not Dictionary<string, object> item) continue;
                var disp = Dict(item.TryGetValue("display", out var dv) ? dv : null);
                var nameMap = disp.TryGetValue("name", out var nmObj) ? nmObj : null;
                var descMap = disp.TryGetValue("desc", out var dmObj) ? dmObj : null;
                var hasGroup = item.TryGetValue("groupid", out var gidObj);
                var gid = StrOf(gidObj);
                var hidden = Numbers.ToLong(disp.TryGetValue("hidden", out var hv) ? hv : null) is { } h && h != 0;
                var icon = StrOf(disp.TryGetValue("icon", out var iv) ? iv : null);
                var iconGray = StrOf(disp.TryGetValue("icon_gray", out var igv) ? igv : null);
                var apiName = StrOf(item.TryGetValue("name", out var av) ? av : null);

                var name = Loc(nameMap);
                if (name.Length == 0) name = apiName;
                var desc = Loc(descMap);
                if (desc.Length == 0) desc = Loc(descMap, "english");

                defs.Add(new AchievementDef(
                    blockKv.Key, bitKv.Key, apiName, name, Loc(nameMap, "english"), desc,
                    Loc(descMap, "english"), hidden, icon, iconGray,
                    hasGroup && groups.TryGetValue(gid, out var gn) ? gn : ""));
            }
        }
        return (defs, groups);
    }

    /// <summary>返回 ({(block, idx): unix_ts}, 命中文件路径)；多账号取解锁条数最多的一份。</summary>
    private static (Dictionary<(string, string), long> Unlocks, string Path) LoadUnlocks(string appId)
    {
        var best = new Dictionary<(string, string), long>();
        var bestPath = "";
        foreach (var p in UnlockPaths(appId))
        {
            Dictionary<string, object> tree;
            try { tree = ParseVdfBin(File.ReadAllBytes(p)); }
            catch { continue; }

            var got = new Dictionary<(string, string), long>();
            foreach (var blockKv in Dict(tree.TryGetValue("cache", out var c) ? c : null))
            {
                if (blockKv.Value is not Dictionary<string, object> bd) continue;
                foreach (var idxKv in Dict(bd.TryGetValue("AchievementTimes", out var at) ? at : null))
                {
                    var v = Numbers.ToLong(idxKv.Value);
                    if (v is > 0) got[(blockKv.Key, idxKv.Key)] = v.Value;
                }
            }
            if (got.Count > best.Count) { best = got; bestPath = p; }
        }
        return (best, bestPath);
    }

    // ------------------------------------------------------------ 全球完成率

    private static string GlobalCachePath(string appId) =>
        Path.Combine(Paths.AchievementCacheDir, "achievement_global_" + appId + ".json");

    private static Dictionary<string, double> LoadGlobalPercent(string appId, int maxAge = 86400)
    {
        if (GlobalMem.TryGetValue(appId, out var hit))
            return hit.ToDictionary(kv => kv.Key, kv => Numbers.ToDouble(kv.Value) ?? 0, StringComparer.Ordinal);

        var disk = new Dictionary<string, double>(StringComparer.Ordinal);
        var cacheTime = 0.0;
        try
        {
            var c = JsonNode.Parse(File.ReadAllText(GlobalCachePath(appId), Encoding.UTF8)) as JsonObject;
            if (c?["data"] is JsonObject data)
                foreach (var kv in data)
                    disk[kv.Key] = Numbers.ToDouble(kv.Value) ?? 0;
            cacheTime = Numbers.ToDouble(c?["time"]) ?? 0;
        }
        catch { }

        if (disk.Count > 0 && (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - cacheTime) < maxAge)
        {
            GlobalMem[appId] = ToNode(disk);
            return disk;
        }

        var url = "https://api.steampowered.com/ISteamUserStats/" +
                  "GetGlobalAchievementPercentagesForApp/v2/?gameid=" + appId;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            var raw = JsonNode.Parse(http.GetStringAsync(url).GetAwaiter().GetResult()) as JsonObject;
            var online = new Dictionary<string, double>(StringComparer.Ordinal);
            if (raw?["achievementpercentages"]?["achievements"] is JsonArray items)
            {
                foreach (var it in items)
                {
                    var nm = it?["name"]?.ToString();
                    if (string.IsNullOrEmpty(nm)) continue;
                    online[nm!] = Numbers.ToDouble(it?["percent"]) ?? 0;
                }
            }
            if (online.Count > 0)
            {
                GlobalMem[appId] = ToNode(online);
                try
                {
                    var payload = new JsonObject
                    {
                        ["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        ["data"] = ToNode(online),
                    };
                    Directory.CreateDirectory(Paths.AchievementCacheDir);
                    File.WriteAllText(GlobalCachePath(appId), payload.ToJsonString(JsonOpts), new UTF8Encoding(false));
                }
                catch { }
                return online;
            }
        }
        catch { }

        GlobalMem[appId] = ToNode(disk);
        return disk;
    }

    private static JsonObject ToNode(Dictionary<string, double> m)
    {
        var o = new JsonObject();
        foreach (var kv in m) o[kv.Key] = kv.Value;
        return o;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    // ------------------------------------------------------------ 缓存

    private static string DataCachePath(string appId) =>
        Path.Combine(Paths.AchievementCacheDir, "achievement_data_" + appId + ".json");

    private static JsonObject? ReadDataCache(string appId)
    {
        try
        {
            var d = JsonNode.Parse(File.ReadAllText(DataCachePath(appId), Encoding.UTF8)) as JsonObject;
            if (d is null) return null;
            var ok = d["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            var hasItems = d["items"] is JsonArray arr && arr.Count > 0;
            var ver = Numbers.ToLong(d["v"]) ?? -1;
            if (ok && hasItems && ver == CacheVersion) return d;
        }
        catch { }
        return null;
    }

    private static string IconUrl(string appId, string h) =>
        string.IsNullOrEmpty(h) ? ""
            : $"https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/{appId}/{h}.jpg";

    private static string IconHttpUrl(string apiName, bool gray) =>
        string.IsNullOrEmpty(apiName) ? "" : $"/api/ach-icon?n={Uri.EscapeDataString(apiName)}&g={(gray ? 1 : 0)}";

    // ------------------------------------------------------------ 汇总

    /// <summary>组装成前端要的结构；默认优先本地缓存，force=true 重读 Steam 缓存。</summary>
    public static object Collect(string appId, string gameRoot, bool force = false)
    {
        lock (Lock)
        {
            if (!force)
            {
                var cached = ReadDataCache(appId);
                if (cached is not null)
                {
                    cached["fromCache"] = true;
                    return cached;
                }
            }
            else
            {
                GlobalMem.Remove(appId);
                try { File.Delete(GlobalCachePath(appId)); } catch { }
            }

            var (defs, _) = LoadDefinitions(appId);
            if (defs.Count == 0)
            {
                return new
                {
                    ok = false,
                    error = "没找到 Steam 成就数据。请先在 Steam 里运行过一次游戏，" +
                            "且 Steam 安装目录下存在 appcache/stats 缓存。",
                };
            }

            var (unlocks, unlockSrc) = LoadUnlocks(appId);
            var percents = LoadGlobalPercent(appId);

            var items = new List<(JsonObject J, bool Unlocked, long Time, double? Percent)>();
            int unlockedCount = 0, rareTotal = 0, rareUnlocked = 0;

            foreach (var d in defs)
            {
                unlocks.TryGetValue((d.Block, d.Idx), out var ts);
                double? pct = null;
                if (d.ApiName.Length > 0 && percents.TryGetValue(d.ApiName, out var pv)) pct = pv;
                var isRare = pct is not null && pct < RarePercent;
                if (ts > 0)
                {
                    unlockedCount++;
                    if (isRare) rareUnlocked++;
                }
                if (isRare) rareTotal++;

                var o = new JsonObject
                {
                    ["id"] = d.Block + "_" + d.Idx,
                    ["apiName"] = d.ApiName,
                    ["name"] = d.Name.Length > 0 ? d.Name : d.NameEn,
                    ["nameEn"] = d.NameEn,
                    ["desc"] = d.Desc.Length > 0 ? d.Desc : d.DescEn,
                    ["hidden"] = d.Hidden,
                    ["group"] = d.Group,
                    ["icon"] = IconUrl(appId, d.Icon),
                    ["iconGray"] = IconUrl(appId, d.IconGray.Length > 0 ? d.IconGray : d.Icon),
                    ["iconLocal"] = IconHttpUrl(d.ApiName, false),
                    ["iconGrayLocal"] = IconHttpUrl(d.ApiName, true),
                    ["unlocked"] = ts > 0,
                    ["time"] = ts,
                    ["timeText"] = ts > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().ToString("yyyy-MM-dd")
                        : "",
                    ["percent"] = pct is null ? null : Math.Round(pct.Value, 1),
                    ["rare"] = isRare,
                };
                if (o["name"]?.ToString() == "") o["name"] = d.ApiName;
                items.Add((o, ts > 0, ts, pct));
            }

            // 解锁的排前面、时间新的靠上；未解锁的按全球完成率从高到低
            var sorted = items
                .OrderBy(x => x.Unlocked ? 0 : 1)
                .ThenBy(x => x.Unlocked ? -(double)x.Time : (x.Percent ?? 0))
                .Select(x => x.J)
                .ToArray();

            var groupsUsed = defs.Select(d => d.Group).Where(g => g.Length > 0)
                .Distinct().OrderBy(g => g, StringComparer.Ordinal).ToArray();

            var itemsArr = new JsonArray();
            foreach (var it in sorted) itemsArr.Add(it);

            var result = new JsonObject
            {
                ["ok"] = true,
                ["v"] = CacheVersion,
                ["appid"] = appId,
                ["total"] = sorted.Length,
                ["unlocked"] = unlockedCount,
                ["rareTotal"] = rareTotal,
                ["rareUnlocked"] = rareUnlocked,
                ["hasGlobal"] = percents.Count > 0,
                ["groups"] = new JsonArray(groupsUsed.Select(g => (JsonNode?)JsonValue.Create(g)).ToArray()),
                ["items"] = itemsArr,
                ["unlockSource"] = unlockSrc.Length > 0 ? Path.GetFileName(unlockSrc) : "",
                ["cachedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };

            try
            {
                Directory.CreateDirectory(Paths.AchievementCacheDir);
                File.WriteAllText(DataCachePath(appId), result.ToJsonString(JsonOpts), new UTF8Encoding(false));
            }
            catch { }

            result["fromCache"] = false;
            return result;
        }
    }

    // ------------------------------------------------------------ 本地成就图标

    // 游戏本体自带全套成就图标（DDS 纹理）：
    //   <gameRoot>/game/gfx/interface/icons/achievements/<成就key>.dds            已解锁
    //   <gameRoot>/game/gfx/interface/icons/achievements/<成就key>_notachieved.dds 未解锁
    private static readonly string[][] IconRelDirs =
    {
        new[] { "game", "gfx", "interface", "icons", "achievements" },   // CK3 / Vic3 / Imperator
        new[] { "gfx", "interface", "icons", "achievements" },           // Stellaris
        new[] { "gfx", "achievements" },                                 // HOI4
        new[] { "gfx", "interface", "achievements" },                    // EU4
    };

    // 未解锁图标的命名后缀（各游戏不同）
    private static readonly string[] GraySuffixes =
        { "_notachieved", "_locked", "_grey", "_gray", "_not_eligible" };

    // 个别游戏缺单独图标时，用游戏自带的成就奖杯图作占位
    private static readonly Dictionary<string, string[]> GenericIcons = new()
    {
        ["281990"] = new[] { "gfx", "interface", "buttons", "main_menu_achievements.dds" },  // Stellaris
    };

    private static List<string> DdsDirs(string gameRoot)
    {
        var outList = new List<string>();
        if (string.IsNullOrWhiteSpace(gameRoot)) return outList;
        foreach (var rel in IconRelDirs)
        {
            var p = Path.Combine(new[] { gameRoot }.Concat(rel).ToArray());
            if (Directory.Exists(p)) outList.Add(p);
        }
        return outList;
    }

    /// <summary>图标文件名候选：原名、规范化名、去/加前缀名、修正常见拼写错误。</summary>
    private static List<string> IconBases(string apiName)
    {
        var raw = Regex.Replace((apiName ?? "").Trim(), @"[\\/:*?""<>|]", "");
        if (raw.Length == 0) return new List<string>();
        var norm = Regex.Replace(raw, @"[^0-9A-Za-z_]", "_");
        var list = new List<string> { raw };
        if (norm != raw) list.Add(norm);
        // Steam 成就表里有拼写错误（achieviement_），游戏文件用正确拼写
        if (raw.StartsWith("achieviement_", StringComparison.Ordinal))
            list.Add("achievement_" + raw["achieviement_".Length..]);
        foreach (var pre in new[] { "achievement_", "ach_" })
        {
            if (raw.StartsWith(pre, StringComparison.Ordinal) && raw.Length > pre.Length)
            {
                list.Add(raw[pre.Length..]);
                if (norm.StartsWith(pre, StringComparison.Ordinal)) list.Add(norm[pre.Length..]);
            }
            else list.Add(pre + raw);
        }
        return list.Where(s => s.Length > 0).Distinct().ToList();
    }

    /// <summary>在游戏目录里找成就 DDS：依次尝试各目录 / 文件名 / 解锁状态后缀。</summary>
    private static string FindDds(string gameRoot, string apiName, bool gray)
    {
        var bases = IconBases(apiName);
        if (bases.Count == 0) return "";
        var suffixes = gray ? GraySuffixes : new[] { "" };
        foreach (var d in DdsDirs(gameRoot))
        foreach (var b in bases)
        foreach (var suf in suffixes)
        {
            var p = Path.Combine(d, b + suf + ".dds");
            if (File.Exists(p)) return p;
        }
        return "";
    }

    private static string GenericDds(string appId, string gameRoot)
    {
        if (!GenericIcons.TryGetValue(appId, out var rel) || string.IsNullOrWhiteSpace(gameRoot)) return "";
        var p = Path.Combine(new[] { gameRoot }.Concat(rel).ToArray());
        return File.Exists(p) ? p : "";
    }

    private static string IconCacheDir(string appId) =>
        Path.Combine(Paths.IconCacheDir, "achievement_icons", appId);

    /// <summary>返回本地 PNG 图标路径（必要时从游戏 DDS 转一次并存缓存）。找不到返回空串。</summary>
    public static string LocalIconPng(string appId, string gameRoot, string apiName, bool gray)
    {
        var name = Regex.Replace(apiName ?? "", @"[^0-9A-Za-z_]", "_");
        if (name.Length == 0) return "";

        var dir = IconCacheDir(appId);
        var outPath = Path.Combine(dir, name + (gray ? "_gray" : "") + ".png");
        if (File.Exists(outPath) && new FileInfo(outPath).Length > 0) return outPath;

        var src = FindDds(gameRoot, apiName, gray);
        if (src.Length == 0 && gray) src = FindDds(gameRoot, apiName, false);   // 没有灰版就退回彩版
        if (src.Length == 0) src = GenericDds(appId, gameRoot);                 // 再退回通用成就图标
        if (src.Length == 0) return "";

        // 串行转换：DDS 解码是 CPU 密集，几十个请求并发抢 CPU 反而更慢
        lock (Lock)
        {
            if (File.Exists(outPath) && new FileInfo(outPath).Length > 0) return outPath;
            try
            {
                Directory.CreateDirectory(dir);
                return ImageConvert.DdsToPng(src, outPath, 128) ? outPath : "";
            }
            catch { return ""; }
        }
    }
}

/// <summary>JsonNode / 数字对象统一的取值小工具（成就与百科共用）。</summary>
internal static class Numbers
{
    public static long? ToLong(object? o)
    {
        switch (o)
        {
            case null: return null;
            case long l: return l;
            case int i: return i;
            case ulong u: return (long)u;
            case double d: return (long)d;
            case float f: return (long)f;
            case decimal m: return (long)m;
            case string s: return long.TryParse(s, out var n) ? n : null;
            case JsonValue v:
                // 新建的 JsonValue 保存的是 CLR 原生类型，TryGetValue<long> 对 int 会失败
                if (v.TryGetValue<int>(out var ji)) return ji;
                if (v.TryGetValue<short>(out var jsh)) return jsh;
                if (v.TryGetValue<byte>(out var jb)) return jb;
                if (v.TryGetValue<long>(out var jl)) return jl;
                if (v.TryGetValue<double>(out var jd)) return (long)jd;
                if (v.TryGetValue<string>(out var js)) return long.TryParse(js, out var jn) ? jn : null;
                return null;
            default:
                return long.TryParse(o.ToString(), out var p) ? p : null;
        }
    }

    public static double? ToDouble(object? o)
    {
        switch (o)
        {
            case null: return null;
            case double d: return d;
            case int i: return i;
            case long l: return l;
            case float f: return f;
            case decimal m: return (double)m;
            case string s when double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n): return n;
            case JsonValue v:
                if (v.TryGetValue<int>(out var ji)) return ji;
                if (v.TryGetValue<short>(out var jsh)) return jsh;
                if (v.TryGetValue<long>(out var jl)) return jl;
                if (v.TryGetValue<double>(out var jd)) return jd;
                if (v.TryGetValue<string>(out var js) && double.TryParse(js,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var jn)) return jn;
                return null;
            default:
                return double.TryParse(o.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : null;
        }
    }
}

/// <summary>游戏 DDS 纹理 → 浏览器可显示的 PNG（成就 / 国策 / 法律 / 任务图标共用）。</summary>
internal static class ImageConvert
{
    /// <summary>解码 DDS（P 社纹理多为无压缩 32 位或 DXT1/3/5），可选方形裁切，缩到 maxSize 内后存 PNG。</summary>
    public static bool DdsToPng(string src, string outPath, int maxSize, bool cropSquare = false)
    {
        try
        {
            var img = LoadDds(src) ?? LoadDdsWic(src);
            if (img is null) return false;

            var w = img.PixelWidth;
            var h = img.PixelHeight;
            // DXT 纹理常把 1:1 美术塞进 2:1 画布，右侧留白：方形图标顺手裁掉
            if (cropSquare && w > 0 && h > 0 && w != h)
            {
                var side = Math.Min(w, h);
                img = new CroppedBitmap(img, new System.Windows.Int32Rect((w - side) / 2, (h - side) / 2, side, side));
            }

            var scale = Math.Min((double)maxSize / img.PixelWidth, (double)maxSize / img.PixelHeight);
            if (scale < 1.0)
            {
                var scaled = new TransformedBitmap(img, new ScaleTransform(scale, scale));
                scaled.Freeze();
                img = scaled;
            }

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            var tmp = outPath + ".tmp";
            using (var os = File.Create(tmp)) enc.Save(os);
            File.Move(tmp, outPath, true);
            return true;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------ 自带 DDS 解码

    /// <summary>
    /// 直接解析 DDS 头与像素数据。WIC 的 DDS 解码器对 P 社这些「非标准头部」会报损坏，
    /// 所以无压缩（RGB 掩码）和 DXT1/3/5 自己解。
    /// </summary>
    private static BitmapSource? LoadDds(string path)
    {
        byte[] b;
        try { b = File.ReadAllBytes(path); } catch { return null; }
        if (b.Length < 128 || b[0] != (byte)'D' || b[1] != (byte)'D' || b[2] != (byte)'S' || b[3] != (byte)' ')
            return null;

        int height = BitConverter.ToInt32(b, 12);
        int width = BitConverter.ToInt32(b, 16);
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return null;

        var pfFlags = BitConverter.ToInt32(b, 80);
        var fourcc = Encoding.ASCII.GetString(b, 84, 4);
        var rgbBits = BitConverter.ToInt32(b, 88);
        var rmask = BitConverter.ToUInt32(b, 92);
        var gmask = BitConverter.ToUInt32(b, 96);
        var bmask = BitConverter.ToUInt32(b, 100);
        var amask = BitConverter.ToUInt32(b, 104);
        const int dataOffset = 128;

        var bgra = new byte[width * height * 4];

        if ((pfFlags & 0x4) != 0) // DDPF_FOURCC
        {
            var blockBytes = fourcc switch
            {
                "DXT1" => 8, "DXT2" or "DXT3" or "DXT4" or "DXT5" => 16, _ => 0,
            };
            if (blockBytes == 0) return null;
            var bw = (width + 3) / 4;
            var bh = (height + 3) / 4;
            if (b.Length < dataOffset + bw * bh * blockBytes) return null;
            DecodeDxt(b, dataOffset, width, height, fourcc, bgra);
        }
        else if ((pfFlags & 0x40) != 0 && rgbBits >= 16) // DDPF_RGB
        {
            var bpp = rgbBits / 8;
            if (bpp < 2 || bpp > 4) return null;
            var need = (long)width * height * bpp;
            if (b.Length < dataOffset + need) return null;
            for (var i = 0; i < width * height; i++)
            {
                uint px = 0;
                for (var k = 0; k < bpp; k++) px |= (uint)b[dataOffset + i * bpp + k] << (8 * k);
                bgra[i * 4 + 0] = Chan(px, bmask);
                bgra[i * 4 + 1] = Chan(px, gmask);
                bgra[i * 4 + 2] = Chan(px, rmask);
                bgra[i * 4 + 3] = amask == 0 ? (byte)255 : Chan(px, amask);
            }
        }
        else return null;

        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        bmp.Freeze();
        return bmp;
    }

    private static BitmapSource? LoadDdsWic(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return dec.Frames[0];
        }
        catch { return null; }
    }

    /// <summary>按位掩码取一个通道，并拉伸到 0..255。</summary>
    private static byte Chan(uint px, uint mask)
    {
        if (mask == 0) return 0;
        var shift = 0;
        while (((mask >> shift) & 1) == 0) shift++;
        var max = mask >> shift;
        var v = (px & mask) >> shift;
        if (max == 0) return 0;
        return (byte)(v * 255 / max);
    }

    private static void DecodeDxt(byte[] b, int off, int w, int h, string fourcc, byte[] bgra)
    {
        var bw = (w + 3) / 4;
        var bh = (h + 3) / 4;
        var block = fourcc == "DXT1" ? 8 : 16;
        var pos = off;

        for (var by = 0; by < bh; by++)
        for (var bx = 0; bx < bw; bx++)
        {
            var alpha = new byte[16];
            Array.Fill(alpha, (byte)255);

            if (fourcc is "DXT3" or "DXT2")
            {
                for (var i = 0; i < 8; i++)
                {
                    alpha[i * 2] = (byte)((b[pos + i] & 0xF) * 17);
                    alpha[i * 2 + 1] = (byte)(((b[pos + i] >> 4) & 0xF) * 17);
                }
            }
            else if (fourcc is "DXT5" or "DXT4")
            {
                var a0 = b[pos];
                var a1 = b[pos + 1];
                var av = new byte[8];
                av[0] = a0;
                av[1] = a1;
                if (a0 > a1)
                    for (var i = 2; i < 8; i++) av[i] = (byte)(((7 - i) * a0 + (i - 1) * a1) / 6);
                else
                    for (var i = 2; i < 6; i++) av[i] = (byte)(((5 - i) * a0 + (i - 1) * a1) / 4);
                av[6] = 0;
                av[7] = 255;
                ulong bits = 0;
                for (var i = 0; i < 6; i++) bits |= (ulong)b[pos + 2 + i] << (8 * i);
                for (var i = 0; i < 16; i++)
                    alpha[i] = av[(bits >> (3 * i)) & 7];
            }

            var cOff = pos + (fourcc == "DXT1" ? 0 : 8);
            var c0 = (ushort)(b[cOff] | (b[cOff + 1] << 8));
            var c1 = (ushort)(b[cOff + 2] | (b[cOff + 3] << 8));
            uint colorBits = 0;
            for (var i = 0; i < 4; i++) colorBits |= (uint)b[cOff + 4 + i] << (8 * i);

            var pal = new byte[4][];
            pal[0] = Rgb565(c0);
            pal[1] = Rgb565(c1);
            if (c0 > c1 || fourcc != "DXT1")
            {
                pal[2] = Mix(pal[0], pal[1], 1, 2);
                pal[3] = Mix(pal[0], pal[1], 2, 1);
            }
            else
            {
                pal[2] = Mix(pal[0], pal[1], 1, 1);
                pal[3] = new byte[] { 0, 0, 0, 0 };
            }

            for (var py = 0; py < 4; py++)
            for (var px = 0; px < 4; px++)
            {
                var x = bx * 4 + px;
                var y = by * 4 + py;
                if (x >= w || y >= h) continue;
                var idx = (int)((colorBits >> (2 * (py * 4 + px))) & 3);
                var c = pal[idx];
                var o = (y * w + x) * 4;
                bgra[o + 0] = c[2];
                bgra[o + 1] = c[1];
                bgra[o + 2] = c[0];
                bgra[o + 3] = fourcc == "DXT1" && idx == 3 ? (byte)0 : alpha[py * 4 + px];
            }

            pos += block;
        }
    }

    private static byte[] Rgb565(ushort c) => new[]
    {
        (byte)(((c >> 11) & 0x1F) * 255 / 31),
        (byte)(((c >> 5) & 0x3F) * 255 / 63),
        (byte)((c & 0x1F) * 255 / 31),
        (byte)255,
    };

    private static byte[] Mix(byte[] a, byte[] b, int wa, int wb)
    {
        var sum = wa + wb;
        return new[]
        {
            (byte)((a[0] * wa + b[0] * wb) / sum),
            (byte)((a[1] * wa + b[1] * wb) / sum),
            (byte)((a[2] * wa + b[2] * wb) / sum),
            (byte)255,
        };
    }
}
