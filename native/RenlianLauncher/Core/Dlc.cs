using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RenlianLauncher.Core;

/// <summary>
/// DLC 管理：清单 / 拥有判定 / 启停 / 商店详情。
///
/// 旧版通过 steam_api64.dll 的 BIsSubscribedApp 查真实授权；原生版改成
/// 「本地 .dlc 文件 + Steam appmanifest」双证据合并判定：
///   · 本地 dlc 目录扫到该 DLC 的 .dlc 文件 → 视为检测到
///   · Steam 的 appmanifest_&lt;dlcId&gt;.acf 存在（或主清单 InstalledDepots 命中）→ 视为检测到
///   两个来源任一命中即为已拥有；来源归因 steam 优先，其次 local。
/// 实测 Steam 只会给游戏本体写 appmanifest，DLC 的清单几乎不会存在，
/// 因此本地文件才是主证据，Steam 清单只作为附加证据。
/// 第三方解锁补丁只用于顶部提示，绝不参与拥有判定与来源归因。
/// </summary>
public static class Dlc
{
    public const string DisabledSuffix = ".disabled";

    // ------------------------------------------------------------ 静态数据

    private static JsonObject? _data;

    private static JsonObject Data => _data ??= LoadData();

    private static JsonObject LoadData()
    {
        // 数据来自旧版 dlc_meta.py / dlc_cn.py，构建时导出成 json 内嵌进程序
        try
        {
            var asm = typeof(Dlc).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("dlc_data.json", StringComparison.OrdinalIgnoreCase));
            if (name != null)
            {
                using var s = asm.GetManifestResourceStream(name);
                if (s != null)
                {
                    using var sr = new StreamReader(s, Encoding.UTF8);
                    return JsonNode.Parse(sr.ReadToEnd()) as JsonObject ?? new JsonObject();
                }
            }
        }
        catch { /* 回退到外部文件 */ }

        try
        {
            var path = Path.Combine(Paths.ExeDir, "Resources", "dlc_data.json");
            return JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject
                   ?? new JsonObject();
        }
        catch { return new JsonObject(); }
    }

    private static JsonObject Sub(string name) =>
        Data[name] is JsonObject o ? o : new JsonObject();

    // ------------------------------------------------------------ 名称本地化

    private const string PrefixKind =
        @"(?:expansion|immersion pack|content pack|music pack|music|e-?book|dlc|collection|" +
        @"cosmetic pack|flavor pack|country pack|art book|soundtrack)";

    private const string PrefixGame =
        @"(?:crusader kings ii|crusader kings iii|europa universalis iv|europa universalis v|" +
        @"hearts of iron iv|victoria 3|victoria ii|victoria 2|imperator: rome|cities: skylines ii|" +
        @"cities: skylines|stellaris|surviving mars|empire of sin|prison architect)";

    private static readonly Regex RePrefix = new(
        @"^(?:" + PrefixKind + "|" + PrefixGame + @")\s*[-\u2013\u2014:]\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReCreator = new(
        @"^(?:" + PrefixGame + @")\s+creator pack\s*:\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReEbook = new(
        @"^e-?book\s+(?=" + PrefixGame + ")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReSuffix = new(
        @"\s*[-\u2013\u2014]\s*(?:immersion pack|music pack|expansion|art pack|mechanics pack|" +
        @"region pack|bonus pack|content pack|flavor pack|unit pack|sprite pack)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>把商店里的英文 DLC 名压成查表键（与 dlc_cn.normalize 一致）。</summary>
    private static string Normalize(string name)
    {
        var s = (name ?? "").Trim();
        s = ReEbook.Replace(s, "");
        s = ReCreator.Replace(s, "");
        for (var i = 0; i < 4; i++)
        {
            var s2 = RePrefix.Replace(s, "");
            if (s2 == s) break;
            s = s2;
        }
        s = ReCreator.Replace(s, "");
        s = ReSuffix.Replace(s, "");
        s = Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();
        return s;
    }

    private static bool HasCjk(string? text) =>
        !string.IsNullOrEmpty(text) && text.Any(ch => ch >= '\u4e00' && ch <= '\u9fff');

    // 玩家本地覆盖表（dlc-names-cn.json）：按修改时间缓存
    private static readonly object UserLock = new();
    private static DateTime _userMtime = DateTime.MinValue;
    private static Dictionary<string, string> _userMap = new(StringComparer.Ordinal);

    public static string UserNamesPath => Path.Combine(Paths.MetaDir, "dlc-names-cn.json");

    private static Dictionary<string, string> UserOverrides()
    {
        lock (UserLock)
        {
            var p = UserNamesPath;
            if (!File.Exists(p)) return _userMap;
            DateTime mt;
            try { mt = File.GetLastWriteTimeUtc(p); } catch { return _userMap; }
            if (mt == _userMtime) return _userMap;

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(p, Encoding.UTF8)) as JsonObject;
                foreach (var kv in root ?? new JsonObject())
                {
                    var k = kv.Key.Trim();
                    if (k.Length == 0 || k.StartsWith('_') || k.StartsWith('#')) continue;
                    var nk = Normalize(k);
                    var v = kv.Value?.ToString()?.Trim();
                    if (nk.Length > 0 && !string.IsNullOrWhiteSpace(v)) map[nk] = v!;
                }
            }
            catch { /* 表坏了就当没有，不影响 DLC 列表 */ }

            _userMtime = mt;
            _userMap = map;
            return _userMap;
        }
    }

    private static string LookupName(string name)
    {
        var key = Normalize(name);
        if (key.Length == 0) return "";
        // 与旧版一致：再试一次 & / and 互换
        var alt = key.Replace(" and ", " & ").Replace("&", " and ").Replace("  ", " ").Trim();
        var table = Sub("cnByName");
        foreach (var k in new[] { key, alt })
        {
            if (table[k]?.ToString() is { Length: > 0 } hit) return hit;
        }
        return "";
    }

    private static string CnName(string raw, string dirname, string game)
    {
        var key = Normalize(raw);
        if (key.Length > 0)
        {
            var user = UserOverrides();
            var alt = key.Replace(" and ", " & ").Replace("&", " and ").Replace("  ", " ").Trim();
            if (user.TryGetValue(key, out var u1)) return u1;
            if (user.TryGetValue(alt, out var u2)) return u2;
        }
        if (game == "ck3")
        {
            if (Sub("cnByDir")[dirname]?.ToString() is { Length: > 0 } byDir) return byDir;
        }
        var hit = LookupName(raw);
        if (hit.Length > 0) return hit;
        return HasCjk(raw) ? raw : "";
    }

    // ------------------------------------------------------------ 目录

    /// <summary>游戏「内容目录」（放 dlc / dlc_metadata 的根）。</summary>
    public static string ContentDir(string gameRoot, string game)
    {
        if (string.IsNullOrWhiteSpace(gameRoot)) return "";
        foreach (var c in new[]
                 {
                     Path.Combine(gameRoot, "game"),
                     Path.Combine(gameRoot, "Data"),
                     gameRoot,
                 })
        {
            if (Directory.Exists(Path.Combine(c, "dlc"))) return c;
        }
        return Path.Combine(gameRoot, "game");
    }

    public static string DlcRoot(string gameRoot, string game) =>
        string.IsNullOrWhiteSpace(gameRoot) ? "" : Path.Combine(ContentDir(gameRoot, game), "dlc");

    /// <summary>放 steam_api64.dll 的目录（补丁检测用）。</summary>
    private static string BinDir(string gameRoot, string game)
    {
        if (string.IsNullOrWhiteSpace(gameRoot)) return "";
        var def = GameDefs.Get(game);
        return Path.Combine(new[] { gameRoot }.Concat(def.Exe.SkipLast(1)).ToArray());
    }

    // ------------------------------------------------------------ 本地 metadata

    private sealed class LocalMeta
    {
        public string Name = "";
        public string Type = "minor";
        public int Priority;
        public string Category = "";
    }

    private static Dictionary<string, LocalMeta> ReadLocalMeta(string gameRoot, string game)
    {
        var outMap = new Dictionary<string, LocalMeta>(StringComparer.Ordinal);
        var content = ContentDir(gameRoot, game);
        if (content.Length == 0) return outMap;

        var candidates = new[]
        {
            Path.Combine(content, "dlc_metadata", "00_dlc_metadata.txt"),
            Path.Combine(content, "dlc_metadata", "dlc_info.txt"),
            Path.Combine(content, "dlc_metadata", "dlc_info", "00_dlc_info.txt"),
            Path.Combine(content, "dlc_metadata", "00_dlc_info.txt"),
            Path.Combine(content, "dlc_metadata", "dlc_metadata.txt"),
            Path.Combine(content, "dlc_metadata.txt"),
        };
        var file = candidates.FirstOrDefault(File.Exists);
        if (file is null) return outMap;

        string text;
        try { text = File.ReadAllText(file, Encoding.UTF8); }
        catch { return outMap; }

        foreach (Match m in Regex.Matches(text,
                     @"dlc_?\s*\d+\s*=\s*\{([^{}]*(?:\{[^{}]*\}[^{}]*)*)\}"))
        {
            var body = m.Groups[1].Value;
            var sid = Regex.Match(body, @"steam_id\s*=\s*""?(\d+)""?");
            if (!sid.Success) continue;
            var name = Regex.Match(body, @"name\s*=\s*""([^""]+)""");
            var type = Regex.Match(body, @"type\s*=\s*(\w+)");
            var prio = Regex.Match(body, @"priority\s*=\s*(\d+)");
            var cat = Regex.Match(body, @"category\s*=\s*(\w+)");
            outMap[sid.Groups[1].Value] = new LocalMeta
            {
                Name = name.Success ? name.Groups[1].Value : "",
                Type = type.Success ? type.Groups[1].Value : "minor",
                Priority = prio.Success && int.TryParse(prio.Groups[1].Value, out var p) ? p : 0,
                Category = cat.Success ? cat.Groups[1].Value : "",
            };
        }
        return outMap;
    }

    // ------------------------------------------------------------ .dlc 文件扫描

    private sealed class DlcFile
    {
        public string Path = "";
        public string Dir = "";
        public string Name = "";
        public string PopsId = "";
        public string Thumb = "";
        public bool FileDisabled;
        public string Category = "";
    }

    private static Dictionary<string, DlcFile> ScanFiles(string dlcRoot)
    {
        var outMap = new Dictionary<string, DlcFile>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(dlcRoot) || !Directory.Exists(dlcRoot)) return outMap;

        // fallbackKey：.dlc 里解析不到 steam_id 时用目录名/文件名兜底，
        // 这样条目仍然能被列出来，但优先使用 steam_id 与元数据 key 对齐
        void ParseOne(string path, string dirname, string fallbackKey)
        {
            var d = Pdx.ParseFile(path);
            var sid = Pdx.Str(d, "steam_id") ?? "";
            var key = sid.Length > 0 ? sid : fallbackKey;
            if (key.Length == 0) return;

            var thumb = "";
            if (dirname.Length > 0)
            {
                foreach (var fn in new[] { "thumbnail.png", "thumbnail.jpg" })
                {
                    var p = Path.Combine(dlcRoot, dirname, fn);
                    if (File.Exists(p)) { thumb = p; break; }
                }
            }
            var baseName = Path.GetFileName(path).ToLowerInvariant();
            outMap[key] = new DlcFile
            {
                Path = path,
                Dir = dirname.Length > 0
                    ? dirname
                    : Path.GetFileNameWithoutExtension(path),
                Name = Pdx.Str(d, "name") ?? "",
                PopsId = Pdx.Str(d, "pops_id") ?? "",
                Thumb = thumb,
                FileDisabled = baseName.EndsWith(DisabledSuffix, StringComparison.Ordinal),
                Category = Pdx.Str(d, "category") ?? "",
            };
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(dlcRoot))
            {
                // 子目录型（CK3/VIC3/HOI4/EU4）：dlc/dlc001_xxx/dlc001.dlc，
                // 文件可能已被改名为 .dlc.disabled；同一目录里的多个 .dlc 都解析
                var dirName = Path.GetFileName(dir);
                var active = Directory.GetFiles(dir, "*.dlc");
                var disabled = Directory.GetFiles(dir, "*" + DisabledSuffix)
                    .Where(f => f.EndsWith(".dlc" + DisabledSuffix, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                // 先解析启用中的文件，再用禁用文件兜底，避免同一个 DLC 被后者覆盖
                foreach (var f in active.Concat(disabled))
                    ParseOne(f, dirName, dirName);
            }
            foreach (var f in Directory.EnumerateFiles(dlcRoot))
            {
                // 平铺型（CK2）：dlc/dlc001.dlc
                if (f.EndsWith(".dlc", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".dlc" + DisabledSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    var stem = Path.GetFileName(f);
                    if (stem.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
                        stem = stem[..^DisabledSuffix.Length];
                    ParseOne(f, "", Path.GetFileNameWithoutExtension(stem));
                }
            }
        }
        catch { }
        return outMap;
    }

    // ------------------------------------------------------------ 分类

    private static (string Code, string Cn) CategoryOf(
        string game, string dirname, LocalMeta? lm, string fileCat)
    {
        var gameCn = Sub("gameCategoryCn")[game] as JsonObject;
        var allCn = Sub("categoryCn");

        string Cn(string code) =>
            gameCn?[code]?.ToString() is { Length: > 0 } g ? g
            : allCn[code]?.ToString() is { Length: > 0 } c ? c
            : "内容包";

        // ① .dlc 文件自带 category（HOI4/EU4/Imperator 最权威）
        if (fileCat.Length > 0 && fileCat is not ("none" or "other"))
            return (fileCat, Cn(fileCat));

        // ② CK3：目录后缀（ep/ce/fp/bp/sp/cp/mp）最精确
        if (game == "ck3")
        {
            var core = Regex.Replace(dirname, @"^dlc\d+_", "");
            var m = Regex.Match(core, "^([a-z]+)");
            var code = m.Success ? m.Groups[1].Value : "";
            var suffix = Sub("suffixCn");
            if (suffix[code]?.ToString() is { Length: > 0 } cn) return (code, cn);
            if (dirname.Contains("attire", StringComparison.OrdinalIgnoreCase) ||
                code is "afr" or "preorder" or "day1")
                return ("sp", "装饰包");
            return ("other", "其他");
        }

        // ③ 本地 metadata 的 category（CK2 等）
        if (!string.IsNullOrWhiteSpace(lm?.Category) && lm!.Category is not "none")
            return (lm.Category, Cn(lm.Category));

        // ④ metadata 的 type 字段
        var typ = lm?.Type ?? "";
        if (typ is "major" or "medium" or "minor") return (typ, Cn(typ));

        // ⑤ 目录名关键词兜底
        var dl = dirname.ToLowerInvariant();
        if (dl.Contains("music") || dl.Contains("song")) return ("music", Cn("music"));
        if (dl.Contains("unit")) return ("unit_pack", Cn("unit_pack"));
        if (dl.Contains("portrait") || dl.Contains("face")) return ("portrait", Cn("portrait"));
        return ("minor", Cn("minor"));
    }

    private static int CategoryOrder(string game, string code)
    {
        if (Sub("gameCategoryOrder")[game] is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
                if (string.Equals(arr[i]?.ToString(), code, StringComparison.OrdinalIgnoreCase))
                    return i;
            return 99;
        }
        if (Data["suffixOrder"] is JsonArray suffix)
        {
            for (var i = 0; i < suffix.Count; i++)
                if (string.Equals(suffix[i]?.ToString(), code, StringComparison.OrdinalIgnoreCase))
                    return i;
        }
        return 99;
    }

    // ------------------------------------------------------------ 补丁检测

    public static JsonObject DetectPatch(string gameRoot, string game)
    {
        var res = new JsonObject
        {
            ["present"] = false,
            ["type"] = "",
            ["unlockedCount"] = 0,
            ["proxyDll"] = "",
            ["backupDll"] = "",
            ["suspicious"] = false,
            ["detail"] = "",
            ["binDir"] = "",
        };
        var bin = BinDir(gameRoot, game);
        res["binDir"] = bin;
        if (bin.Length == 0 || !Directory.Exists(bin)) return res;

        var creamIni = Path.Combine(bin, "cream_api.ini");
        var dll64 = Path.Combine(bin, "steam_api64.dll");
        var dll64o = Path.Combine(bin, "steam_api64_o.dll");
        var hasCream = File.Exists(creamIni);
        var hasBackup = File.Exists(dll64o);
        if (!hasCream && !hasBackup) return res;

        long dllSize = 0;
        try { if (File.Exists(dll64)) dllSize = new FileInfo(dll64).Length; } catch { }

        if (hasCream)
        {
            res["type"] = "CreamAPI";
            res["proxyDll"] = "steam_api64.dll";
            res["backupDll"] = hasBackup ? "steam_api64_o.dll" : "";
            try
            {
                var txt = File.ReadAllText(creamIni, Encoding.UTF8);
                var seg = Regex.Match(txt, @"\[dlc\]\s*\n(.*?)(?=\n\s*\[|\Z)", RegexOptions.Singleline);
                if (seg.Success)
                    res["unlockedCount"] = Regex.Matches(seg.Groups[1].Value, @"^\s*(\d{6,8})\s*=",
                        RegexOptions.Multiline).Count;
            }
            catch { }
        }
        else if (hasBackup)
        {
            res["type"] = "其他（Goldberg 类）";
            res["proxyDll"] = "steam_api64.dll";
            res["backupDll"] = "steam_api64_o.dll";
            res["unlockedCount"] = 0;
        }

        res["present"] = true;
        if (dllSize is > 0 and < 340000)
        {
            res["suspicious"] = true;
            res["detail"] = "steam_api64.dll 体积疑似被还原为原版，补丁可能已失效，建议重新覆盖补丁文件";
        }
        else
        {
            res["detail"] = "补丁生效中";
        }
        return res;
    }

    // ------------------------------------------------------------ Steam 清单（拥有判定）

    private sealed class SteamInfo
    {
        public bool Ok;
        public string Msg = "";
        public HashSet<string> Installed = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// 读 Steam 的 appmanifest：主游戏清单存在即视为可读；每个 DLC 自己的
    /// appmanifest_&lt;id&gt;.acf 存在即视为「已安装」，主清单 InstalledDepots 里的
    /// depot id 也算（部分游戏把 DLC 内容挂进主包 depot）。
    /// </summary>
    private static SteamInfo ReadSteam(string appId)
    {
        var info = new SteamInfo();
        var libs = Paths.SteamRoots();
        if (libs.Count == 0)
        {
            info.Msg = "没有找到 Steam 安装目录（Steam 未安装或注册表信息缺失）";
            return info;
        }
        foreach (var lib in libs)
        {
            var steamapps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(steamapps)) continue;
            try
            {
                foreach (var acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
                {
                    var id = Path.GetFileNameWithoutExtension(acf)["appmanifest_".Length..];
                    if (id.Length == 0 || !id.All(char.IsDigit)) continue;
                    if (id == appId) info.Ok = true;
                    info.Installed.Add(id);
                }
            }
            catch { }

            var main = Path.Combine(steamapps, $"appmanifest_{appId}.acf");
            if (File.Exists(main))
            {
                info.Ok = true;
                try
                {
                    var txt = File.ReadAllText(main, Encoding.UTF8);
                    var seg = Regex.Match(txt, @"""InstalledDepots""\s*\{(.*?)\n\s*\}", RegexOptions.Singleline);
                    if (seg.Success)
                    {
                        foreach (Match m in Regex.Matches(seg.Groups[1].Value, @"""(\d{4,9})""\s*\{"))
                            info.Installed.Add(m.Groups[1].Value);
                    }
                }
                catch { }
            }
        }
        if (!info.Ok)
            info.Msg = "没有找到游戏的 Steam 清单（appmanifest），无法核对 DLC 拥有情况";
        return info;
    }

    // ------------------------------------------------------------ 用户手动标记（拥有 / 未拥有）

    // 手动标记落盘在 RenlianData\data\dlc-overrides.json，结构 { "appid:dlcKey": true/false }
    private static readonly object OverrideLock = new();
    private static DateTime _overrideMtime = DateTime.MinValue;
    private static Dictionary<string, bool> _overrides = new(StringComparer.OrdinalIgnoreCase);

    public static string OverridesPath => Path.Combine(Paths.MetaDir, "dlc-overrides.json");

    /// <summary>读用户手动标记；按文件修改时间缓存，文件没变就不重复解析。</summary>
    private static Dictionary<string, bool> ReadOverrides()
    {
        lock (OverrideLock)
        {
            var p = OverridesPath;
            if (!File.Exists(p))
            {
                _overrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                _overrideMtime = DateTime.MinValue;
                return _overrides;
            }

            DateTime mt;
            try { mt = File.GetLastWriteTimeUtc(p); }
            catch { return _overrides; }
            if (mt == _overrideMtime) return _overrides;

            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(p, Encoding.UTF8)) as JsonObject;
                foreach (var kv in root ?? new JsonObject())
                {
                    if (kv.Value is JsonValue v && v.TryGetValue<bool>(out var b))
                        map[kv.Key.Trim()] = b;
                }
            }
            catch { /* 文件坏了就当没有手动标记，不影响列表 */ }

            _overrides = map;
            _overrideMtime = mt;
            return _overrides;
        }
    }

    /// <summary>写手动标记：owned 为 null 表示清除；原子写（.tmp + File.Move 覆盖）。</summary>
    private static bool SaveOverride(string key, bool? owned)
    {
        lock (OverrideLock)
        {
            try
            {
                var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var p = OverridesPath;
                if (File.Exists(p))
                {
                    try
                    {
                        var root = JsonNode.Parse(File.ReadAllText(p, Encoding.UTF8)) as JsonObject;
                        foreach (var kv in root ?? new JsonObject())
                        {
                            if (kv.Value is JsonValue v && v.TryGetValue<bool>(out var b))
                                map[kv.Key] = b;
                        }
                    }
                    catch { /* 原文件坏了就重写一份干净的 */ }
                }

                if (owned.HasValue) map[key] = owned.Value;
                else map.Remove(key);

                var obj = new JsonObject();
                foreach (var kv in map.OrderBy(x => x.Key, StringComparer.Ordinal))
                    obj[kv.Key] = kv.Value;

                Directory.CreateDirectory(Paths.MetaDir);
                var tmp = p + ".tmp";
                File.WriteAllText(tmp, obj.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }), new UTF8Encoding(false));
                File.Move(tmp, p, true);   // 覆盖已有文件，避免写一半被读到

                _overrides = map;
                _overrideMtime = DateTime.MinValue;   // 下次读取以磁盘上的新文件为准
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>一次检测的汇总信息（Steam 是否可读、整体来源、补丁状态）。</summary>
    private sealed class DetectInfo
    {
        public bool SteamAvailable;
        public string SteamMsg = "";
        public bool PatchPresent;
        public string Source = "none";
        public JsonObject Patch = new();
    }

    // ------------------------------------------------------------ 清单构建

    /// <summary>兼容旧调用：不需要汇总信息时直接用。</summary>
    private static List<JsonObject> ListItems(ConfigStore cfg) => ListItems(cfg, new DetectInfo());

    /// <summary>
    /// 构建 DLC 静态清单（含拥有判定与用户手动标记，但不含播放集开关状态）。
    /// Steam 读取失败一律降级为「只按本地文件检测」，不向外抛异常。
    /// </summary>
    private static List<JsonObject> ListItems(ConfigStore cfg, DetectInfo info)
    {
        var game = cfg.CurrentGame;
        var gameRoot = cfg.GameStr(game, "gameRoot") ?? "";
        var def = GameDefs.Get(game);

        var localMeta = ReadLocalMeta(gameRoot, game);
        var files = ScanFiles(DlcRoot(gameRoot, game));
        var steamMeta = Sub("meta")[game] as JsonObject ?? new JsonObject();
        var patch = DetectPatch(gameRoot, game);
        var patchPresent = ConfigStore.Bool(patch, "present", false);

        SteamInfo steam;
        try { steam = ReadSteam(def.AppId); }
        catch (Exception ex)
        {
            // 注册表 / 目录 / 文件任何异常都吞掉，改用本地文件检测
            steam = new SteamInfo { Msg = "读取 Steam 库失败：" + ex.Message };
        }

        info.Patch = patch;
        info.PatchPresent = patchPresent;
        info.SteamAvailable = steam.Ok;
        info.SteamMsg = steam.Msg;
        // 整体来源只看 Steam 清单是否可读 + 本地是否扫到文件，补丁不参与归因
        info.Source = steam.Ok ? "steam"
            : files.Count > 0 ? "local"
            : "none";

        var overrides = ReadOverrides();

        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var kv in steamMeta) ids.Add(kv.Key);
        foreach (var k in localMeta.Keys) ids.Add(k);
        foreach (var k in files.Keys) ids.Add(k);

        var list = new List<JsonObject>();
        foreach (var sid in ids)
        {
            var sm = steamMeta[sid] as JsonObject;
            localMeta.TryGetValue(sid, out var lm);
            files.TryGetValue(sid, out var f);
            var dirname = f?.Dir is { Length: > 0 } d ? d : $"dlc_{sid}";

            var rawName = sm?["name"]?.ToString() is { Length: > 0 } sn ? sn
                : lm?.Name is { Length: > 0 } ln ? ln
                : f?.Name is { Length: > 0 } fn ? fn
                : sid;
            var nameCn = CnName(rawName, dirname, game);
            var (catCode, typeCn) = CategoryOf(game, dirname, lm, f?.Category ?? "");

            // 双证据合并判定：本地文件命中 / Steam 清单命中，任一成立即视为检测到。
            // 补丁状态只用于顶部提示，这里一律不参与。
            var onDisk = f is not null;
            var inSteam = steam.Ok && steam.Installed.Contains(sid);
            var detected = onDisk || inSteam;
            var ownedKnown = inSteam;   // 只有 Steam 清单确认才算「已验证」，false 仅表示未验证
            var ownedSource = inSteam ? "steam" : onDisk ? "file" : "unknown";
            var detectSource = inSteam ? "steam" : onDisk ? "local" : "none";
            // 证据：Steam 命中给清单文件名，本地命中给相对游戏目录的 .dlc 路径
            var evidence = inSteam
                ? $"appmanifest_{sid}.acf"
                : onDisk ? RelativeTo(gameRoot, f!.Path) : "";

            // 用户手动标记（appid:dlcKey）优先于检测结果
            var id = $"{def.AppId}:{sid}";
            bool? userOwned = overrides.TryGetValue(id, out var uv) ? uv : null;
            var owned = userOwned ?? detected;
            var conflict = userOwned.HasValue && userOwned.Value != detected;
            JsonNode? userNode = userOwned.HasValue ? JsonValue.Create(userOwned.Value) : null;

            var item = new JsonObject
            {
                ["id"] = id,
                ["popsId"] = f?.PopsId is { Length: > 0 } pops ? pops : $"{game}_{dirname}",
                ["dir"] = dirname,
                ["name"] = rawName,
                ["nameCn"] = nameCn.Length > 0 ? nameCn : rawName,
                ["nameEn"] = rawName,
                ["steamId"] = sid,
                ["affectsChecksum"] = true,
                ["thumb"] = sm?["header"]?.ToString() is { Length: > 0 } h1 ? h1
                    : f?.Thumb ?? "",
                ["header"] = sm?["header"]?.ToString() ?? "",
                ["installed"] = f is not null,
                ["dlcType"] = catCode,
                ["typeCn"] = typeCn,
                ["metaType"] = lm?.Type ?? "minor",
                ["priority"] = lm?.Priority ?? 0,
                ["releaseDate"] = sm?["date"]?.ToString() ?? "",
                ["sizeBytes"] = 0,
                ["fileDisabled"] = f?.FileDisabled ?? false,
                // 判定相关：detected 为检测值，userOwned 为用户选择，owned 为最终生效值
                ["detected"] = detected,
                ["userOwned"] = userNode,
                ["conflict"] = conflict,
                ["detectSource"] = detectSource,
                ["owned"] = owned,
                ["ownedKnown"] = ownedKnown,
                ["ownedSource"] = ownedSource,
                // 证据路径：方便用户核查判定依据（Steam 清单名 / 本地相对路径）
                ["evidence"] = evidence,
            };
            list.Add(item);
        }

        // 同一 DLC 可能因元数据 / 本地文件用不同 key 各出现一次，按名称合并为一条，
        // 避免列表里出现两个同名 DLC；合并时优先保留本地已安装的那一条
        var merged = new List<JsonObject>();
        var byName = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var it in list)
        {
            var nameKey = (it["nameCn"]?.ToString() ?? "").Trim();
            if (nameKey.Length == 0) nameKey = (it["name"]?.ToString() ?? "").Trim();
            nameKey = nameKey.ToLowerInvariant();
            if (nameKey.Length == 0) { merged.Add(it); continue; }

            if (!byName.TryGetValue(nameKey, out var keep))
            {
                byName[nameKey] = it;
                merged.Add(it);
                continue;
            }
            if (!ConfigStore.Bool(keep, "installed", false) && ConfigStore.Bool(it, "installed", false))
            {
                var idx = merged.IndexOf(keep);
                if (idx >= 0) merged[idx] = it;
                byName[nameKey] = it;
            }
        }

        return merged
            .OrderBy(x => CategoryOrder(game, x["dlcType"]?.ToString() ?? ""))
            .ThenByDescending(x => x["priority"]?.GetValue<int>() ?? 0)
            .ThenBy(x => x["dir"]?.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>把绝对路径转成相对游戏目录的路径（用于展示判定证据）。</summary>
    private static string RelativeTo(string root, string full)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(full)) return "";
            if (string.IsNullOrWhiteSpace(root)) return full;
            var rel = Path.GetRelativePath(root, full);
            return rel.StartsWith("..", StringComparison.Ordinal) ? full : rel.Replace('\\', '/');
        }
        catch { return full; }
    }

    /// <summary>读游戏实际生效的禁用 DLC 清单（dlc_load.json / content_load.json）。</summary>
    public static HashSet<string> ReadDisabledDlcs(ConfigStore cfg)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var game = cfg.CurrentGame;
        var userDir = cfg.GameStr(game, "userDir") ?? "";
        if (userDir.Length == 0) return set;

        try
        {
            if (GameDefs.ContentLoad.Contains(game))
            {
                var path = Path.Combine(userDir, "content_load.json");
                if (!File.Exists(path)) return set;
                var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
                if (root?["disabledDLC"] is JsonArray arr)
                {
                    foreach (var node in arr.OfType<JsonObject>())
                    {
                        var id = node["paradoxAppId"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(id)) set.Add(id!);
                    }
                }
            }
            else
            {
                var path = Path.Combine(userDir, "dlc_load.json");
                if (!File.Exists(path)) return set;
                var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
                if (root?["disabled_dlcs"] is JsonArray arr)
                {
                    foreach (var node in arr)
                    {
                        var id = node?.ToString();
                        if (!string.IsNullOrWhiteSpace(id)) set.Add(id!);
                    }
                }
            }
        }
        catch { /* 启停文件坏了就当没有禁用项 */ }
        return set;
    }

    // ------------------------------------------------------------ 对外：列表

    public static object Build(ConfigStore cfg, string? psid, bool force, bool pullSteam)
    {
        var game = cfg.CurrentGame;
        var db = PlaysetDb.ForConfig(cfg);

        var target = psid;
        if (string.IsNullOrWhiteSpace(target)) target = db.ActivePlayset();
        if (string.IsNullOrWhiteSpace(target))
            target = db.ListPlaysets().FirstOrDefault()?.Id;

        var state = string.IsNullOrWhiteSpace(target)
            ? new Dictionary<string, bool>(StringComparer.Ordinal)
            : db.GetPlayset(target!)?.Dlcs ?? new Dictionary<string, bool>(StringComparer.Ordinal);
        var disabled = ReadDisabledDlcs(cfg);

        var detect = new DetectInfo();
        var items = ListItems(cfg, detect);
        foreach (var d in items)
        {
            var pops = d["popsId"]?.ToString() ?? "";
            var enabled = state.Count > 0
                ? state.TryGetValue(pops, out var v) && v
                : !disabled.Contains(pops);

            // 真正生效状态以 .dlc 文件为准（文件被改名禁用 = 未启用）
            if (ConfigStore.Bool(d, "fileDisabled", false)) enabled = false;
            if (!ConfigStore.Bool(d, "owned", false)) enabled = false;
            d["enabled"] = enabled;
        }

        var patch = detect.Patch;
        var steamOk = detect.PatchPresent || detect.SteamAvailable;
        var steamMsg = detect.PatchPresent ? "" : detect.SteamMsg;

        int CountSource(string src) =>
            items.Count(x => string.Equals(x["ownedSource"]?.ToString(), src, StringComparison.Ordinal));

        return new
        {
            ok = true,
            playset = target ?? "",
            items,
            patch,
            steamOk,
            steamMsg,
            // steamAvailable 只表示「Steam 清单是否真的读到」；false 时前端提示已降级
            steamAvailable = detect.SteamAvailable,
            detectSource = detect.Source,
            conflictCount = items.Count(x => ConfigStore.Bool(x, "conflict", false)),
            cachedAt = 0,          // 原生版不落盘缓存：每次实时扫，数量小、耗时可忽略
            fromCache = false,
            ownedCount = items.Count(x => ConfigStore.Bool(x, "owned", false)),
            enabledCount = items.Count(x =>
                ConfigStore.Bool(x, "owned", false) && ConfigStore.Bool(x, "enabled", false)),
            bySource = new
            {
                steam = CountSource("steam"),
                file = CountSource("file"),
                unknown = CountSource("unknown"),
                none = CountSource("none"),
            },
            refresh = (object?)null,
        };
    }

    // ------------------------------------------------------------ 对外：手动标记 / 重新检测

    /// <summary>保存用户对某个 DLC 的手动拥有标记；owned 传 null 表示恢复为跟随检测。</summary>
    public static object SetOverride(ConfigStore cfg, JsonObject? body)
    {
        var id = (ConfigStore.Str(body, "id") ?? "").Trim();
        if (id.Length == 0 || !id.Contains(':'))
            return new { ok = false, error = "缺少有效的 DLC id（格式应为 appid:dlcKey）" };

        bool? owned = null;
        if (body?["owned"] is JsonValue v)
        {
            if (!v.TryGetValue<bool>(out var b))
                return new { ok = false, error = "owned 只能是 true / false / null" };
            owned = b;
        }
        else if (body?["owned"] is not null)
        {
            return new { ok = false, error = "owned 只能是 true / false / null" };
        }

        // 先确认这个 DLC 在当前游戏的清单里，避免写入无意义的垃圾键
        var before = new DetectInfo();
        var exists = ListItems(cfg, before)
            .Any(x => string.Equals(x["id"]?.ToString(), id, StringComparison.OrdinalIgnoreCase));
        if (!exists)
            return new { ok = false, error = "找不到这个 DLC（可能游戏已切换或条目不存在）" };

        if (!SaveOverride(id, owned))
            return new { ok = false, error = "写入 dlc-overrides.json 失败，请检查数据目录是否可写" };

        // 保存后重新检测，把最终状态返回给前端
        var after = new DetectInfo();
        var item = ListItems(cfg, after)
            .FirstOrDefault(x => string.Equals(x["id"]?.ToString(), id, StringComparison.OrdinalIgnoreCase));
        return new
        {
            ok = item is not null,
            item,
            steamAvailable = after.SteamAvailable,
            detectSource = after.Source,
        };
    }

    /// <summary>强制重新扫描：刷新 Steam 路径并重新读取 appmanifest，失败自动降级。</summary>
    public static object Recheck(ConfigStore cfg)
    {
        Paths.RefreshSteamRoots();   // Steam 后启动时也能重新探测到
        var info = new DetectInfo();
        var items = ListItems(cfg, info);
        return new
        {
            ok = true,
            steamAvailable = info.SteamAvailable,
            source = info.Source,
            steamMsg = info.SteamMsg,
            items,
        };
    }

    // ------------------------------------------------------------ 对外：详情

    private static readonly object DetailLock = new();
    private static readonly Dictionary<string, (DateTime At, JsonObject Data)> DetailCache =
        new(StringComparer.Ordinal);
    private static bool _detailLoaded;

    private static string DetailCachePath => Path.Combine(Paths.DlcCacheDir, "dlc_detail_cache.json");

    private static void LoadDetailCache()
    {
        if (_detailLoaded) return;
        _detailLoaded = true;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(DetailCachePath, Encoding.UTF8)) as JsonObject;
            var now = DateTimeOffset.UtcNow;
            foreach (var kv in root ?? new JsonObject())
            {
                if (kv.Value is not JsonObject o) continue;
                var t = o["t"]?.GetValue<double>() ?? 0;
                var at = DateTimeOffset.FromUnixTimeSeconds((long)t).UtcDateTime;
                if ((now - at).TotalDays < 7 && o["d"] is JsonObject d)
                    DetailCache[kv.Key] = (at, d);
            }
        }
        catch { }
    }

    private static void SaveDetailCache()
    {
        try
        {
            var root = new JsonObject();
            foreach (var kv in DetailCache)
            {
                root[kv.Key] = new JsonObject
                {
                    ["t"] = new DateTimeOffset(kv.Value.At).ToUnixTimeSeconds(),
                    ["d"] = kv.Value.Data.DeepClone(),
                };
            }
            Directory.CreateDirectory(Paths.DlcCacheDir);
            File.WriteAllText(DetailCachePath,
                root.ToJsonString(new JsonSerializerOptions
                {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }), new UTF8Encoding(false));
        }
        catch { }
    }

    private static readonly HttpClient NoProxyClient = new(new HttpClientHandler
    {
        UseProxy = false,
    })
    { Timeout = TimeSpan.FromSeconds(12) };

    private static readonly HttpClient ProxyClient = new() { Timeout = TimeSpan.FromSeconds(12) };

    /// <summary>Steam 商店 DLC 详情（中文），内存 + 磁盘缓存 1 小时 / 7 天。</summary>
    public static async Task<object> DetailAsync(string steamId)
    {
        var sid = (steamId ?? "").Trim();
        if (sid.Length == 0 || !sid.All(char.IsDigit))
            return new { ok = false, error = "无效的 DLC ID" };

        lock (DetailLock)
        {
            LoadDetailCache();
            if (DetailCache.TryGetValue(sid, out var c) &&
                (DateTime.UtcNow - c.At).TotalHours < 1)
                return c.Data;
        }

        var url = $"https://store.steampowered.com/api/appdetails?appids={sid}&l=schinese&cc=cn";
        string? raw = null;
        string err = "";
        foreach (var client in new[] { NoProxyClient, ProxyClient })
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
                using var resp = await client.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                raw = await resp.Content.ReadAsStringAsync();
                break;
            }
            catch (Exception ex) { err = ex.Message; }
        }

        if (raw is null)
        {
            lock (DetailLock)
            {
                if (DetailCache.TryGetValue(sid, out var stale))
                {
                    var copy = (JsonObject)stale.Data.DeepClone();
                    copy["stale"] = true;
                    return copy;
                }
            }
            return new { ok = false, error = "拉取失败：" + err };
        }

        JsonObject? result = null;
        try
        {
            var root = JsonNode.Parse(raw) as JsonObject;
            var data = root?[sid]?["data"] as JsonObject;
            if (root?[sid]?["success"]?.GetValue<bool>() != true || data is null)
            {
                result = new JsonObject { ["ok"] = false, ["error"] = "Steam 商店没有这个 DLC 的数据" };
            }
            else
            {
                var shots = new JsonArray();
                if (data["screenshots"] is JsonArray arr)
                {
                    foreach (var s in arr.OfType<JsonObject>())
                    {
                        var u = s["path_thumbnail"]?.ToString() is { Length: > 0 } t
                            ? t : s["path_full"]?.ToString() ?? "";
                        if (u.Length > 0) shots.Add(u);
                    }
                }
                result = new JsonObject
                {
                    ["ok"] = true,
                    ["steamId"] = sid,
                    ["name"] = data["name"]?.ToString() ?? "",
                    ["shortDescription"] = data["short_description"]?.ToString() ?? "",
                    ["descriptionHtml"] = data["detailed_description"]?.ToString()
                        ?? data["about_the_game"]?.ToString() ?? "",
                    ["headerImage"] = data["header_image"]?.ToString() ?? "",
                    ["screenshots"] = shots,
                    ["releaseDate"] = data["release_date"]?["date"]?.ToString() ?? "",
                    ["isFree"] = data["is_free"]?.GetValue<bool>() ?? false,
                };
            }
        }
        catch (Exception ex)
        {
            return new { ok = false, error = "解析失败：" + ex.Message };
        }

        if (ConfigStore.Bool(result, "ok", false))
        {
            lock (DetailLock)
            {
                DetailCache[sid] = (DateTime.UtcNow, result);
                SaveDetailCache();
            }
        }
        return result;
    }

    // ------------------------------------------------------------ 对外：启停

    /// <summary>写播放集 DLC 开关，并把「真正生效」落到 .dlc 文件（content_load 游戏除外）。</summary>
    public static object Set(ConfigStore cfg, JsonObject? body, IReadOnlyList<JsonObject>? index)
    {
        var game = cfg.CurrentGame;
        var db = PlaysetDb.ForConfig(cfg);
        var psid = ConfigStore.Str(body, "playsetId");
        if (string.IsNullOrWhiteSpace(psid)) psid = db.ActivePlayset();
        if (string.IsNullOrWhiteSpace(psid))
            psid = db.ListPlaysets().FirstOrDefault()?.Id;
        if (string.IsNullOrWhiteSpace(psid))
            return new { ok = false, error = "没有激活的播放集" };

        var cur = db.GetPlayset(psid!);
        if (cur is null) return new { ok = false, error = "播放集不存在" };

        if (db.ActivePlayset() is null)
        {
            try { db.SetActive(psid!); } catch { /* 没激活也不该阻止保存 */ }
        }

        var merged = new Dictionary<string, bool>(cur.Dlcs, StringComparer.Ordinal);
        if (body?["dlcs"] is JsonObject mapping)
        {
            foreach (var kv in mapping)
                merged[kv.Key] = ConfigStore.Bool(mapping, kv.Key, true);
        }

        var writes = cur.Mods
            .Select(m => new ModWrite(string.IsNullOrWhiteSpace(m.ModId) ? null : m.ModId,
                null, m.Enabled))
            .ToList();
        var res = db.WritePlayset(psid!, writes, merged, index);

        if (GameDefs.ContentLoad.Contains(game))
        {
            // Vic3/EU5 的 DLC 开关走 content_load.json 的 disabledDLC，不改扩展名
            return new
            {
                ok = true,
                synced = res.Synced,
                dlcs = merged,
                mode = "content_load",
                dlcFilesChanged = 0,
                dlcFilesFailed = 0,
                failed = Array.Empty<object>(),
            };
        }

        // 改游戏目录里对应 .dlc 文件的扩展名（disabled_dlcs 自 1.20 起已失效）
        var gameRoot = cfg.GameStr(game, "gameRoot") ?? "";
        var popsToDir = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in ListItems(cfg))
        {
            if (!ConfigStore.Bool(d, "installed", false)) continue;
            var pops = d["popsId"]?.ToString() ?? "";
            var dir = d["dir"]?.ToString() ?? "";
            if (pops.Length > 0 && dir.Length > 0) popsToDir[pops] = dir;
        }

        var changed = 0;
        var failed = new List<object>();
        foreach (var kv in merged)
        {
            if (!popsToDir.TryGetValue(kv.Key, out var dir)) continue;
            var r = ApplyFileState(gameRoot, dir, kv.Value, game);
            if (ConfigStore.Bool(r, "ok", false))
            {
                if (r["changed"] is JsonArray ch && ch.Count > 0) changed++;
            }
            else
            {
                failed.Add(new
                {
                    popsId = kv.Key,
                    dir,
                    reason = ConfigStore.Str(r, "reason") ?? "未知原因",
                    writable = ConfigStore.Bool(r, "writable", false),
                });
            }
        }

        return new
        {
            ok = true,
            synced = res.Synced,
            dlcs = merged,
            dlcFilesChanged = changed,
            dlcFilesFailed = failed.Count,
            failed,
        };
    }

    // ------------------------------------------------------------ 对外：自检

    public static object Verify(ConfigStore cfg, string? psid)
    {
        var game = cfg.CurrentGame;
        var db = PlaysetDb.ForConfig(cfg);
        var target = psid;
        if (string.IsNullOrWhiteSpace(target)) target = db.ActivePlayset();
        if (string.IsNullOrWhiteSpace(target))
            target = db.ListPlaysets().FirstOrDefault()?.Id;
        if (string.IsNullOrWhiteSpace(target))
            return new { ok = false, error = "没有激活的播放集" };

        var desired = db.GetPlayset(target!)?.Dlcs ?? new Dictionary<string, bool>(StringComparer.Ordinal);
        var loadDisabled = ReadDisabledDlcs(cfg);
        var gameRoot = cfg.GameStr(game, "gameRoot") ?? "";
        var items = new List<object>();
        var mismatches = new List<object>();
        var contentLoad = GameDefs.ContentLoad.Contains(game);

        foreach (var d in ListItems(cfg))
        {
            if (!ConfigStore.Bool(d, "installed", false)) continue;
            var pops = d["popsId"]?.ToString() ?? "";
            var dir = d["dir"]?.ToString() ?? "";
            var name = d["nameCn"]?.ToString() is { Length: > 0 } cn ? cn : d["name"]?.ToString() ?? dir;

            if (contentLoad)
            {
                var want = desired.TryGetValue(pops, out var v) ? v : !loadDisabled.Contains(pops);
                var actualOn = !loadDisabled.Contains(pops);
                string reason = "";
                if (want != actualOn)
                {
                    reason = want
                        ? "期望启用，但 content_load.json 的 disabledDLC 仍包含它（启用没有生效）"
                        : "期望关闭，但 content_load.json 的 disabledDLC 里没有它（关闭没有生效）";
                }
                var rec = new
                {
                    popsId = pops,
                    dir,
                    name,
                    desired = want,
                    actual = actualOn,
                    actualState = actualOn ? "enabled" : "disabled",
                    writable = true,
                    conflict = false,
                    reason,
                };
                items.Add(rec);
                if (want != actualOn) mismatches.Add(rec);
                continue;
            }

            var want2 = desired.TryGetValue(pops, out var v2) ? v2
                : ConfigStore.Bool(d, "fileDisabled", false) ? false
                : !loadDisabled.Contains(pops);
            var st = InspectFile(gameRoot, dir, game);
            var actual = st.Actual;
            var actualEnabled = actual == "enabled";
            var rec2 = new
            {
                popsId = pops,
                dir,
                name,
                desired = want2,
                actual = actualEnabled,
                actualState = actual,
                writable = st.Writable,
                conflict = st.Conflict,
                reason = st.Reason,
            };
            items.Add(rec2);

            if (actual == "missing")
            {
                mismatches.Add(new
                {
                    popsId = rec2.popsId,
                    dir = rec2.dir,
                    name = rec2.name,
                    desired = rec2.desired,
                    actual = rec2.actual,
                    actualState = rec2.actualState,
                    writable = rec2.writable,
                    conflict = rec2.conflict,
                    reason = st.Reason.Length > 0 ? st.Reason : "DLC 目录里没有 .dlc 文件",
                });
            }
            else if (st.Conflict)
            {
                mismatches.Add(new
                {
                    popsId = rec2.popsId,
                    dir = rec2.dir,
                    name = rec2.name,
                    desired = rec2.desired,
                    actual = rec2.actual,
                    actualState = rec2.actualState,
                    writable = rec2.writable,
                    conflict = rec2.conflict,
                    reason = "目录里同时存在 .dlc 和 .dlc.disabled 重复文件",
                });
            }
            else if (want2 != actualEnabled)
            {
                mismatches.Add(new
                {
                    popsId = rec2.popsId,
                    dir = rec2.dir,
                    name = rec2.name,
                    desired = rec2.desired,
                    actual = rec2.actual,
                    actualState = rec2.actualState,
                    writable = rec2.writable,
                    conflict = rec2.conflict,
                    reason = want2
                        ? "期望启用，但文件仍是 .dlc.disabled（启用没有生效）"
                        : "期望关闭，但文件仍是 .dlc（关闭没有生效）",
                });
            }
        }

        return new
        {
            ok = true,
            playset = target,
            mode = contentLoad ? "content_load" : "files",
            @checked = items.Count,
            mismatched = mismatches.Count,
            items,
            mismatches,
        };
    }

    // ------------------------------------------------------------ .dlc 文件状态

    private sealed class FileState
    {
        public bool Exists;
        public string Dir = "";
        public List<string> Active = new();
        public List<string> Disabled = new();
        public bool Writable;
        public string Actual = "missing";
        public bool Conflict;
        public string Reason = "";
    }

    private static bool PathWritable(string p)
    {
        try
        {
            if (Directory.Exists(p))
            {
                var probe = Path.Combine(p, ".probe-" + Guid.NewGuid().ToString("N")[..8]);
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                           FileOptions.DeleteOnClose)) { }
                return true;
            }
            if (!File.Exists(p)) return false;
            var attrs = File.GetAttributes(p);
            return (attrs & FileAttributes.ReadOnly) == 0;
        }
        catch { return false; }
    }

    private static void ClearReadonly(string p)
    {
        try
        {
            var attrs = File.GetAttributes(p);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(p, attrs & ~FileAttributes.ReadOnly);
        }
        catch { }
    }

    private static string UniquePath(string p)
    {
        if (!File.Exists(p) && !Directory.Exists(p)) return p;
        var ext = Path.GetExtension(p);
        var stem = p[..^ext.Length];
        for (var i = 2; ; i++)
        {
            var cand = $"{stem}_{i}{ext}";
            if (!File.Exists(cand) && !Directory.Exists(cand)) return cand;
        }
    }

    private static (List<string> Active, List<string> Disabled) DirFiles(string d)
    {
        var active = new List<string>();
        var disabled = new List<string>();
        try
        {
            foreach (var fn in Directory.EnumerateFiles(d))
            {
                var low = Path.GetFileName(fn).ToLowerInvariant();
                if (low.EndsWith(".dlc" + DisabledSuffix, StringComparison.Ordinal))
                    disabled.Add(Path.GetFileName(fn));
                else if (low.EndsWith(".dlc", StringComparison.Ordinal))
                    active.Add(Path.GetFileName(fn));
            }
        }
        catch { }
        return (active, disabled);
    }

    private static FileState InspectFile(string gameRoot, string dirName, string game)
    {
        var info = new FileState();
        var root = DlcRoot(gameRoot, game);
        if (root.Length == 0 || !Directory.Exists(root))
        {
            info.Reason = "找不到游戏的 dlc 目录，请检查游戏安装路径是否正确";
            return info;
        }

        var sub = dirName.Length > 0 ? Path.Combine(root, dirName) : "";
        if (sub.Length > 0 && Directory.Exists(sub))
        {
            // 子目录型（CK3/VIC3/HOI4 等）：dlc/dlc001_xxx/xxx.dlc
            info.Dir = sub;
            info.Exists = true;
            var (a, d) = DirFiles(sub);
            info.Active = a;
            info.Disabled = d;
            info.Writable = PathWritable(sub) &&
                            (a.Concat(d).All(fn => PathWritable(Path.Combine(sub, fn))));
        }
        else
        {
            // 平铺型（CK2）：dlc/dlc001.dlc
            if (dirName.Length == 0)
            {
                info.Reason = "无法定位这个 DLC 的目录";
                return info;
            }
            info.Dir = root;
            var wantActive = (dirName + ".dlc").ToLowerInvariant();
            var wantDisabled = wantActive + DisabledSuffix;
            try
            {
                foreach (var fn in Directory.EnumerateFiles(root))
                {
                    var low = Path.GetFileName(fn).ToLowerInvariant();
                    if (low == wantActive) info.Active.Add(Path.GetFileName(fn));
                    else if (low == wantDisabled) info.Disabled.Add(Path.GetFileName(fn));
                }
            }
            catch (Exception ex)
            {
                info.Reason = "读取 DLC 目录失败：" + ex.Message;
                return info;
            }
            info.Exists = info.Active.Count > 0 || info.Disabled.Count > 0;
            info.Writable = PathWritable(root) &&
                            info.Active.Concat(info.Disabled)
                                .All(fn => PathWritable(Path.Combine(root, fn)));
            if (!info.Exists) info.Reason = "没有找到这个 DLC 的 .dlc 文件（可能未安装）";
        }

        info.Conflict = info.Active.Count > 0 && info.Disabled.Count > 0;
        info.Actual = info.Active.Count > 0 ? "enabled"
            : info.Disabled.Count > 0 ? "disabled"
            : "missing";
        return info;
    }

    /// <summary>启用/禁用某个 DLC，改名后以真实文件状态作为成功依据。</summary>
    private static JsonObject ApplyFileState(string gameRoot, string dirName, bool enabled, string game)
    {
        var res = new JsonObject
        {
            ["ok"] = false,
            ["dir"] = "",
            ["changed"] = new JsonArray(),
            ["active"] = new JsonArray(),
            ["disabled"] = new JsonArray(),
            ["writable"] = false,
            ["actual"] = "missing",
            ["conflict"] = false,
            ["reason"] = "",
        };
        var st = InspectFile(gameRoot, dirName, game);
        res["dir"] = st.Dir;
        res["writable"] = st.Writable;
        res["actual"] = st.Actual;
        res["conflict"] = st.Conflict;

        if (!st.Exists || (st.Active.Count == 0 && st.Disabled.Count == 0))
        {
            res["reason"] = st.Reason.Length > 0 ? st.Reason
                : $"没有找到这个 DLC 的 .dlc 文件（可能未安装），无法{(enabled ? "启用" : "关闭")}";
            return res;
        }

        var changed = (JsonArray)res["changed"]!;
        var d = st.Dir;
        try
        {
            if (enabled)
            {
                // 启用：去掉 .disabled；若同名 .dlc 已存在则把重复文件挪成 .bak
                foreach (var fn in st.Disabled.ToList())
                {
                    var src = Path.Combine(d, fn);
                    var tgt = src[..^DisabledSuffix.Length];
                    if (File.Exists(tgt))
                    {
                        File.Move(src, UniquePath(src + ".bak"));
                        continue;
                    }
                    ClearReadonly(src);
                    File.Move(src, tgt);
                    changed.Add(tgt);
                }
            }
            else
            {
                // 禁用：必须让所有 .dlc 都消失，否则游戏仍会加载它
                foreach (var fn in st.Active.ToList())
                {
                    var src = Path.Combine(d, fn);
                    var tgt = src + DisabledSuffix;
                    if (File.Exists(tgt)) tgt = UniquePath(tgt + ".bak");
                    ClearReadonly(src);
                    File.Move(src, tgt);
                    changed.Add(tgt);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            res["reason"] = "没有权限修改 DLC 文件：游戏可能正在运行，或文件被设为只读。" +
                            "请完全退出游戏后重试；仍失败就用管理员身份运行启动器。";
            return res;
        }
        catch (IOException ex)
        {
            var code = ex.HResult & 0xFFFF;
            res["reason"] = code switch
            {
                32 => "DLC 文件正被其他程序占用（游戏可能正在运行）。请完全退出游戏和官方启动器后重试。",
                5 => "拒绝访问：没有修改 DLC 文件的权限。请用管理员身份运行启动器，或检查游戏目录是否只读。",
                _ => "修改 DLC 文件失败：" + ex.Message,
            };
            return res;
        }

        var st2 = InspectFile(gameRoot, dirName, game);
        var active = new JsonArray();
        foreach (var fn in st2.Active) active.Add(fn);
        var disabled = new JsonArray();
        foreach (var fn in st2.Disabled) disabled.Add(fn);
        res["active"] = active;
        res["disabled"] = disabled;
        res["actual"] = st2.Actual;
        res["writable"] = st2.Writable;
        res["conflict"] = st2.Conflict;

        var want = enabled ? "enabled" : "disabled";
        if (st2.Actual == want)
        {
            res["ok"] = true;
        }
        else
        {
            res["reason"] = want == "disabled"
                ? "改名后仍检测到有效的 .dlc 文件（同时存在重复文件），关闭没有完全生效；请退出游戏后点「开关自检」重试。"
                : "仍存在 .dlc.disabled 文件，启用没有生效；请退出游戏后点「开关自检」重试。";
        }
        return res;
    }

    // ------------------------------------------------------------ 对外：打开目录 / 名称表

    public static object OpenDir(ConfigStore cfg)
    {
        var root = DlcRoot(cfg.GameStr(cfg.CurrentGame, "gameRoot") ?? "", cfg.CurrentGame);
        if (root.Length == 0 || !Directory.Exists(root))
            return new { ok = false, error = "找不到 DLC 目录，请先在设置里确认游戏安装路径" };
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
            return new { ok = true, path = root };
        }
        catch (Exception ex) { return new { ok = false, path = root, error = "打开目录失败：" + ex.Message }; }
    }

    public static object OpenNamesFile()
    {
        var path = UserNamesPath;
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Paths.MetaDir);
                var template = new JsonObject
                {
                    ["_说明"] = "DLC 中文名的本地覆盖表。格式是 \"英文原名\": \"中文名\"，" +
                               "英文名大小写不限、前面的 Expansion - / 音乐 - 之类的词可以省略。" +
                               "改完保存，回启动器点 DLC 页右上角「重新校验」即可生效。",
                    ["_示例（可以删掉）"] = "",
                    ["the great wave"] = "大浪潮",
                    ["Gotterdammerung"] = "诸神黄昏",
                };
                File.WriteAllText(path, template.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }), new UTF8Encoding(false));
            }
        }
        catch (Exception ex) { return new { ok = false, error = "创建文件失败：" + ex.Message }; }

        var opened = false;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            opened = true;
        }
        catch { }
        return new
        {
            ok = true,
            path,
            opened,
            error = opened ? "" : "文件已生成在程序目录，但系统没有关联的 .json 打开程序，请手动打开",
        };
    }
}
