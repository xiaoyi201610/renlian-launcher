using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace RenlianLauncher.Core;

/// <summary>
/// 游戏百科（旧版 wiki.py 的原生移植）：从游戏本体脚本 + 官方本地化抽取结构化资料。
///
/// 设计要点：
///   · 完全离线：只读 gameRoot 下的脚本与本地化，不联网、不抓 wiki 正文
///   · 随版本自动更新：目录 mtime 变化 → 缓存自动失效重建
///   · CK3 / 群星 / 维3 / 钢4 / 欧4 各有完整解析模块
/// </summary>
public static class Wiki
{
    private const int WikiVer = 38;

    public static readonly string[] SupportedGames = { "ck3", "stellaris", "hoi4", "vic3", "eu4" };

    private static readonly object BuildLock = new();
    private static readonly ConcurrentDictionary<string, (string Sig, JsonObject Data)> Mem = new();

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    // JsonNode 里可能有 CLR 数值（int 等），写盘时需要带 TypeInfoResolver 的选项
    private static readonly JsonSerializerOptions CacheWriteOpts = new(JsonOpts)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly string D_MAA = Path.Combine("common", "men_at_arms_types");
    private static readonly string D_CULT = Path.Combine("common", "culture", "cultures");
    private static readonly string D_TRAD = Path.Combine("common", "culture", "traditions");
    private static readonly string D_SV = Path.Combine("common", "script_values");
    private static readonly string D_RELIGION = Path.Combine("common", "religion", "religion_types");
    private static readonly string D_TENET = Path.Combine("common", "religion", "tenet_types");
    private static readonly string D_DOCTRINE = Path.Combine("common", "religion", "doctrine_types");
    private static readonly string D_INNOV = Path.Combine("common", "culture", "innovations");
    private static readonly string D_LOC = "localization";

    // 给 Ck3Builder 用的公开别名
    internal static readonly string D_MAA_PUBLIC = D_MAA;
    internal static readonly string D_CULT_PUBLIC = D_CULT;
    internal static readonly string D_TRAD_PUBLIC = D_TRAD;
    internal static readonly string D_RELIGION_PUBLIC = D_RELIGION;
    internal static readonly string D_TENET_PUBLIC = D_TENET;
    internal static readonly string D_DOCTRINE_PUBLIC = D_DOCTRINE;
    internal static readonly string D_INNOV_PUBLIC = D_INNOV;

    // 界面语言 → 游戏本地化目录
    private static readonly Dictionary<string, string> LangMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "simp_chinese",
        ["en"] = "english",
        ["fr"] = "french",
        ["de"] = "german",
        ["sv"] = "english",
    };

    private static readonly string[] Ck3TabIcons = { "maa", "cultures", "traditions", "religions", "tenets", "doctrines", "innovations" };
    private static readonly string[] Ck3TabLayout = { "maa", "cultures", "traditions", "generic", "generic", "generic", "generic" };

    // ------------------------------------------------------------ 对外入口

    public static JsonObject Load(string game, string gameRoot, string uiLang, bool force)
    {
        game = string.IsNullOrWhiteSpace(game) ? "ck3" : game.Trim().ToLowerInvariant();
        uiLang = string.IsNullOrWhiteSpace(uiLang) ? "zh" : uiLang;

        // 群星 / 维3 / 钢4 / 欧4 各有独立解析模块
        if (game == "stellaris") return WikiStellaris.Load(gameRoot, uiLang, force);
        if (game == "vic3") return WikiVic3.Load(gameRoot, uiLang, force);
        if (game == "hoi4") return WikiHoi4.Load(gameRoot, uiLang, force);
        if (game == "eu4") return WikiEu4.Load(gameRoot, uiLang, force);
        if (!SupportedGames.Contains(game)) return Unsupported(game);

        var root = ScriptsRoot(gameRoot);
        if (root.Length == 0 || !Directory.Exists(Path.Combine(root, D_MAA)))
            return new JsonObject
            {
                ["ok"] = false,
                ["supported"] = true,
                ["game"] = game,
                ["error"] = "没找到游戏本体目录（common/men_at_arms_types），请在设置里确认游戏根目录",
            };

        var locLang = LangMap.TryGetValue(uiLang, out var ll) ? ll : "simp_chinese";
        var dirs = new[]
        {
            Path.Combine(root, D_MAA), Path.Combine(root, D_CULT), Path.Combine(root, D_TRAD),
            Path.Combine(root, D_SV), Path.Combine(root, D_RELIGION), Path.Combine(root, D_TENET),
            Path.Combine(root, D_DOCTRINE), Path.Combine(root, D_INNOV),
            Path.Combine(root, D_LOC, locLang),
        };
        var sig = Sig(dirs);
        var memKey = $"{game}|{locLang}|{root.ToLowerInvariant()}";
        var cachePath = Path.Combine(Paths.WikiCacheDir, $"wiki_cache_{game}_{locLang}.json");

        lock (BuildLock)
        {
            if (!force && Mem.TryGetValue(memKey, out var hit) && hit.Sig == sig)
            {
                hit.Data["cached"] = true;
                return hit.Data;
            }

            if (!force)
            {
                try
                {
                    var blob = JsonNode.Parse(File.ReadAllText(cachePath, Encoding.UTF8)) as JsonObject;
                    if (Numbers.ToLong(blob?["wiki_ver"]) == WikiVer &&
                        (blob?["sig"]?.ToString() ?? "") == sig &&
                        (blob?["root"]?.ToString() ?? "").Equals(root, StringComparison.OrdinalIgnoreCase) &&
                        blob?["data"] is JsonObject cachedData)
                    {
                        Mem[memKey] = (sig, cachedData);
                        cachedData["cached"] = true;
                        return cachedData;
                    }
                }
                catch { }
            }

            var t0 = Environment.TickCount64;
            var data = BuildCk3(root, locLang, uiLang, (int)(Environment.TickCount64 - t0));
            data["buildMs"] = (int)(Environment.TickCount64 - t0);
            Mem[memKey] = (sig, data);

            try
            {
                Directory.CreateDirectory(Paths.WikiCacheDir);
                var payload = new JsonObject
                {
                    ["wiki_ver"] = WikiVer,
                    ["sig"] = sig,
                    ["root"] = root,
                    ["data"] = JsonNode.Parse(data.ToJsonString(JsonOpts)),
                };
                var tmp = cachePath + ".tmp";
                File.WriteAllText(tmp, payload.ToJsonString(JsonOpts), new UTF8Encoding(false));
                File.Move(tmp, cachePath, true);
            }
            catch { /* 写不进去只影响下次重建 */ }

            data["cached"] = false;
            return data;
        }
    }

    /// <summary>
    /// 独立游戏模块（群星 / 维3）共用的缓存层：与 CK3 同一策略——内存按 sig 命中，
    /// 页面缓存按「版本 + sig + 根目录」校验，目录 mtime 变化才重建，不会每次请求全量重扫。
    /// </summary>
    internal static JsonObject Cached(string game, string locLang, string root, string sig, bool force,
        Func<JsonObject> build, out bool cached)
    {
        var memKey = $"{game}|{locLang}|{root.ToLowerInvariant()}";
        var cachePath = Path.Combine(Paths.WikiCacheDir, $"wiki_cache_{game}_{locLang}.json");

        lock (BuildLock)
        {
            if (!force && Mem.TryGetValue(memKey, out var hit) && hit.Sig == sig)
            {
                hit.Data["cached"] = true;
                cached = true;
                return hit.Data;
            }

            if (!force)
            {
                try
                {
                    var blob = JsonNode.Parse(File.ReadAllText(cachePath, Encoding.UTF8)) as JsonObject;
                    if (Numbers.ToLong(blob?["wiki_ver"]) == WikiVer &&
                        (blob?["sig"]?.ToString() ?? "") == sig &&
                        (blob?["root"]?.ToString() ?? "").Equals(root, StringComparison.OrdinalIgnoreCase) &&
                        blob?["data"] is JsonObject cachedData)
                    {
                        Mem[memKey] = (sig, cachedData);
                        cachedData["cached"] = true;
                        cached = true;
                        return cachedData;
                    }
                }
                catch { }
            }

            var data = build();
            Mem[memKey] = (sig, data);

            try
            {
                Directory.CreateDirectory(Paths.WikiCacheDir);
                var payload = new JsonObject
                {
                    ["wiki_ver"] = WikiVer,
                    ["sig"] = sig,
                    ["root"] = root,
                    ["data"] = JsonNode.Parse(data.ToJsonString(CacheWriteOpts)),
                };
                var tmp = cachePath + ".tmp";
                File.WriteAllText(tmp, payload.ToJsonString(CacheWriteOpts), new UTF8Encoding(false));
                File.Move(tmp, cachePath, true);
            }
            catch { /* 写不进去只影响下次重建 */ }

            data["cached"] = false;
            cached = false;
            return data;
        }
    }

    /// <summary>单棵 HOI4 国策树的节点（非钢4 与旧版一致返回 unsupported）。</summary>
    public static JsonObject FocusTree(string game, string gameRoot, string tid, string uiLang)
    {
        game = (game ?? "").Trim().ToLowerInvariant();
        if (game != "hoi4") return new JsonObject { ["ok"] = false, ["error"] = "unsupported" };
        return WikiHoi4.FocusTree(gameRoot, tid, uiLang);
    }

    /// <summary>单棵 EU4 任务树。</summary>
    public static JsonObject MissionTree(string game, string gameRoot, string tid, string uiLang)
    {
        game = (game ?? "").Trim().ToLowerInvariant();
        if (game != "eu4") return new JsonObject { ["ok"] = false, ["error"] = "unsupported" };
        return WikiEu4.MissionTree(gameRoot, tid, uiLang);
    }

    // ------------------------------------------------------------ 图标（DDS → PNG）

    /// <summary>国策图标 PNG（HOI4 的 gfx 索引规则）。</summary>
    public static string FocusIcon(string gameRoot, string gfxName)
    {
        gfxName = (gfxName ?? "").Trim();
        if (gfxName.Length == 0) return "";
        var root = FindHoi4Root(gameRoot);
        if (root.Length == 0) return "";

        var safe = Regex.Replace(gfxName, @"[^0-9A-Za-z_]", "_");
        var dir = Path.Combine(Paths.IconCacheDir, "hoi4_focus");
        var outPath = Path.Combine(dir, safe + ".png");
        if (File.Exists(outPath) && new FileInfo(outPath).Length > 0) return outPath;

        var rel = GfxIndex(root).TryGetValue(gfxName, out var r) ? r : "";
        var src = rel.Length > 0 ? ResolveDds(root, rel) : "";
        if (src.Length == 0)
        {
            // 索引里没有就按命名规律猜一次（GFX_goal_x → goal_x.dds）
            var guess = gfxName.StartsWith("GFX_", StringComparison.Ordinal) ? gfxName[4..] : gfxName;
            src = ResolveDds(root, "gfx/interface/goals/" + guess + ".dds");
        }
        if (src.Length == 0) return "";

        lock (BuildLock)
        {
            if (File.Exists(outPath) && new FileInfo(outPath).Length > 0) return outPath;
            try
            {
                Directory.CreateDirectory(dir);
                return ImageConvert.DdsToPng(src, outPath, 96) ? outPath : "";
            }
            catch { return ""; }
        }
    }

    /// <summary>EU4 任务图标 PNG。</summary>
    public static string MissionIcon(string gameRoot, string gfxName)
    {
        if (string.IsNullOrWhiteSpace(gfxName)) return "";
        var root = FindEu4Root(gameRoot);
        if (root.Length == 0) return "";

        var safe = Regex.Replace(gfxName, @"[^A-Za-z0-9_.-]", "_");
        if (safe.Length > 120) safe = safe[..120];
        var dir = Path.Combine(Paths.IconCacheDir, "eu4_mission");
        var outPath = Path.Combine(dir, safe + ".png");
        if (File.Exists(outPath)) return outPath;

        var rel = GfxMap(root).TryGetValue(gfxName, out var r) && r.Length > 0
            ? r : "gfx/interface/missions/" + gfxName + ".dds";
        var dds = Path.Combine(root, rel.Replace('/', '\\'));
        if (!File.Exists(dds)) return "";

        lock (BuildLock)
        {
            if (File.Exists(outPath)) return outPath;
            try
            {
                Directory.CreateDirectory(dir);
                return ImageConvert.DdsToPng(dds, outPath, 4096) ? outPath : "";
            }
            catch { return ""; }
        }
    }

    /// <summary>维多利亚 3 的图标 PNG（法律 / 利益集团 / 集团特质）。</summary>
    public static string LawIcon(string gameRoot, string name, string kind)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return "";
        var root = FindVic3Root(gameRoot);
        if (root.Length == 0) return "";

        var sub = (kind ?? "").Trim().ToLowerInvariant() switch
        {
            "ig" => "ig_icons",
            "igtrait" => "ig_trait_icons",
            _ => "law_icons",
        };
        var safe = Regex.Replace(name, @"[^0-9A-Za-z_]", "_");
        var prefix = sub == "law_icons" ? "" : sub.Replace("law_icons", "law") + "_";
        var dir = Path.Combine(Paths.IconCacheDir, "vic3_law");
        var outPath = Path.Combine(dir, prefix + safe + ".png");
        if (File.Exists(outPath) && new FileInfo(outPath).Length > 0) return outPath;

        var gameDir = Path.Combine(root, "game");
        if (!Directory.Exists(Path.Combine(gameDir, "gfx"))) gameDir = root;
        var src = Path.Combine(gameDir, "gfx", "interface", "icons", sub, name + ".dds");
        if (!File.Exists(src))
        {
            var alt = Path.Combine(gameDir, "gfx", "interface", "icons", name + ".dds");
            src = File.Exists(alt) ? alt : src;
        }
        if (!File.Exists(src)) return "";

        lock (BuildLock)
        {
            if (File.Exists(outPath) && new FileInfo(outPath).Length > 0) return outPath;
            try
            {
                Directory.CreateDirectory(dir);
                return ImageConvert.DdsToPng(src, outPath, 128, cropSquare: true) ? outPath : "";
            }
            catch { return ""; }
        }
    }

    // ------------------------------------------------------------ 空结构 / 降级

    private static JsonObject Unsupported(string game) => new()
    {
        ["ok"] = true,
        ["supported"] = false,
        ["game"] = game,
        ["maa"] = new JsonArray(),
        ["cultures"] = new JsonArray(),
        ["traditions"] = new JsonArray(),
        ["religions"] = new JsonArray(),
        ["tenets"] = new JsonArray(),
        ["doctrines"] = new JsonArray(),
        ["innovations"] = new JsonArray(),
        ["counterCycle"] = new JsonArray(),
        ["typesInfo"] = new JsonArray(),
        ["counts"] = new JsonObject
        {
            ["maa"] = 0, ["cultures"] = 0, ["traditions"] = 0, ["religions"] = 0,
            ["tenets"] = 0, ["doctrines"] = 0, ["innovations"] = 0,
        },
    };

    /// <summary>已支持但本批未迁移的游戏：结构与旧版同形，只把内容留空并标记 degraded。</summary>
    private static JsonObject Degraded(string game, string uiLang)
    {
        var o = Unsupported(game);
        o["supported"] = false;
        o["degraded"] = true;
        o["uiLang"] = uiLang;
        o["note"] = "该游戏百科解析尚未迁移到新版内核（v2）";
        o["tabs"] = new JsonArray();
        return o;
    }

    private static string ScriptsRoot(string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot)) return "";
        var r = gameRoot.Trim().TrimEnd('\\', '/');
        var inner = Path.Combine(r, "game");
        return Directory.Exists(Path.Combine(inner, D_MAA)) ? inner : r;
    }

    private static string Sig(IEnumerable<string> paths)
    {
        var parts = new List<string>();
        foreach (var p in paths)
        {
            try
            {
                var mtime = new DateTimeOffset(Directory.Exists(p)
                    ? Directory.GetLastWriteTimeUtc(p) : File.GetLastWriteTimeUtc(p)).ToUnixTimeSeconds();
                var size = Directory.Exists(p)
                    ? Directory.EnumerateFileSystemEntries(p).Count() : new FileInfo(p).Length;
                parts.Add($"{Path.GetFileName(p.TrimEnd('\\', '/'))}:{mtime}:{size}");
            }
            catch { parts.Add("-"); }
        }
        return string.Join("|", parts);
    }

    // ------------------------------------------------------------ CK3 构建

    private static JsonObject BuildCk3(string root, string locLang, string uiLang, int buildMs)
    {
        var zh = locLang == "simp_chinese";
        var b = new Ck3Builder(root, locLang);

        // 1) 先扫脚本，收集需要用到的本地化键（避免把 30 万条全塞内存）
        var needed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Glob(root, D_MAA))
        {
            foreach (var (key, body) in TopBlocks(ReadScript(f)))
            {
                needed.Add(key);
                needed.Add(key + "_flavor");
                var t = Val(body, "type") ?? "";
                if (t.Length > 0)
                {
                    if (Ck3Builder.TypeLocCandidates.TryGetValue(t, out var cands))
                        foreach (var c in cands) needed.Add(c);
                    needed.Add(t + "_attribute");
                    needed.Add("accolade_" + t);
                    needed.Add(t);
                }
                var tb = Sub(body, "terrain_bonus");
                if (!string.IsNullOrEmpty(tb))
                    foreach (var (k, _) in TopBlocks(tb)) needed.Add(k);
                var wb = Sub(body, "winter_bonus");
                if (!string.IsNullOrEmpty(wb))
                    foreach (var (k, _) in TopBlocks(wb)) needed.Add(k);
                var cb = Sub(body, "counters");
                if (!string.IsNullOrEmpty(cb))
                    foreach (var k in Pairs(cb).Keys) needed.Add(k);
                // 兵种招募条件引用的本地化键（家族传承 / 革新 / 政体 / 文化参数）
                var crBody = Sub(body, "can_recruit");
                if (!string.IsNullOrEmpty(crBody))
                {
                    foreach (var fld in new[] { "has_dynasty_perk", "has_innovation", "has_government",
                                                "has_cultural_parameter", "PARAMETER" })
                    {
                        foreach (Match m in Regex.Matches(crBody, @"\b" + fld + @"\s*=\s*([A-Za-z_0-9]+)"))
                        {
                            var v = m.Groups[1].Value;
                            needed.Add(v);
                            needed.Add(v + "_name");
                            needed.Add("culture_parameter_" + v);
                            needed.Add("game_concept_" + v);
                        }
                    }
                }
            }
        }

        var rawTrads = new List<string>();
        foreach (var f in Glob(root, D_TRAD))
        {
            foreach (var (key, body) in TopBlocks(ReadScript(f)))
            {
                rawTrads.Add(key);
                var pb = Sub(body, "parameters");
                if (!string.IsNullOrEmpty(pb))
                    foreach (var p in Pairs(pb).Keys) needed.Add("culture_parameter_" + p);
                foreach (var block in new[] { "character_modifier", "culture_modifier" })
                {
                    var mb = Sub(body, block);
                    if (!string.IsNullOrEmpty(mb))
                        foreach (var k in Pairs(mb).Keys) needed.Add(k);
                }
            }
        }

        foreach (var f in Glob(root, D_CULT))
        {
            foreach (var (key, body) in TopBlocks(ReadScript(f)))
            {
                needed.Add(key);
                foreach (var pk in new[] { "ethos", "heritage", "language", "martial_custom", "head_determination" })
                {
                    var v = Val(body, pk);
                    if (string.IsNullOrEmpty(v)) continue;
                    needed.Add(v);
                    needed.Add(v + "_name");
                    needed.Add(v + "_desc");
                }
                foreach (var p in ListIds(Sub(body, "parents"))) needed.Add(p);
                foreach (var dbody in SubAll(body, "dlc_tradition"))
                {
                    var tk = Val(dbody, "trait");
                    if (string.IsNullOrEmpty(tk)) continue;
                    needed.Add(tk);
                    needed.Add(tk + "_name");
                    needed.Add(tk + "_desc");
                }
            }
        }
        foreach (var key in rawTrads)
        {
            needed.Add(key);
            needed.Add(key + "_name");
            needed.Add(key + "_desc");
        }

        // 扩展百科：宗教 / 信条 / 教义 / 革新
        foreach (var (dir, suffixes) in new (string, string[])[]
                 {
                     (D_RELIGION, new[] { "", "_desc" }),
                     (D_TENET, new[] { "_name", "_desc" }),
                     (D_DOCTRINE, new[] { "_name", "_desc" }),
                     (D_INNOV, new[] { "", "_desc" }),
                 })
        {
            foreach (var fp in Glob(root, dir))
            {
                foreach (var (k, body) in TopBlocks(ReadScript(fp)))
                {
                    foreach (var suf in suffixes) needed.Add(k + suf);
                    foreach (var fld in new[] { "doctrine_group_type", "culture_era", "group", "skill" })
                    {
                        var v = Val(body, fld);
                        if (string.IsNullOrEmpty(v)) continue;
                        needed.Add(v);
                        needed.Add(v + "_name");
                        needed.Add("game_concept_" + v);
                    }
                    var rd = Sub(body, "religion_details");
                    if (!string.IsNullOrEmpty(rd))
                    {
                        var fam = Val(rd, "family");
                        if (!string.IsNullOrEmpty(fam))
                        {
                            needed.Add(fam);
                            needed.Add(fam + "_name");
                        }
                    }
                    foreach (var blk in new[] { "parameters", "special_parameters", "character_modifier",
                                                "personal_tenet_modifier", "personal_tenet_parameters",
                                                "culture_modifier" })
                    {
                        var inner = Sub(body, blk);
                        if (string.IsNullOrEmpty(inner)) continue;
                        var ps = inner.Contains('=') ? Pairs(inner).Keys.ToList() : ListIds(inner);
                        foreach (var p in ps)
                        {
                            needed.Add(p);
                            needed.Add("culture_parameter_" + p);
                            needed.Add("doctrine_parameter_" + p);
                            needed.Add("tenet_parameter_" + p);
                        }
                    }
                    foreach (var fld in new[] { "unlock_law", "custom", "unlock_maa", "unlock_building",
                                                "unlock_casus_belli", "maa_upgrade", "has_innovation",
                                                "has_government", "has_dynasty_perk", "has_cultural_parameter" })
                    {
                        foreach (Match m in Regex.Matches(body, "^\t" + fld + @"\s*=\s*([a-z_0-9]+)",
                                     RegexOptions.Multiline))
                        {
                            var v = m.Groups[1].Value;
                            needed.Add(v);
                            needed.Add(v + "_name");
                            needed.Add("culture_parameter_" + v);
                            needed.Add("building_" + v);
                            needed.Add("building_type_" + v);
                            needed.Add("game_concept_" + v);
                        }
                    }
                    var cr = Sub(body, "can_recruit");
                    if (!string.IsNullOrEmpty(cr))
                    {
                        foreach (var fld in new[] { "PARAMETER", "has_innovation", "has_government",
                                                    "has_cultural_parameter", "has_dynasty_perk" })
                        {
                            foreach (Match m in Regex.Matches(cr, @"\b" + fld + @"\s*=\s*([A-Za-z_0-9]+)"))
                            {
                                var v = m.Groups[1].Value;
                                needed.Add(v);
                                needed.Add(v + "_name");
                                needed.Add("culture_parameter_" + v);
                                needed.Add("game_concept_" + v);
                            }
                        }
                    }
                    foreach (Match m in Regex.Matches(body, "^\tdoctrine\\s*=\\s*([a-z_0-9]+)",
                                 RegexOptions.Multiline))
                    {
                        var dk = m.Groups[1].Value;
                        needed.Add(dk);
                        needed.Add(dk + "_name");
                    }
                }
            }
        }

        var locDir = Path.Combine(root, D_LOC, locLang);
        var raw = ScanLoc(locDir, needed, Ck3Builder.WidePrefixes);

        // 勋号兵种属性名默认不在 needed 里，显式补扫
        foreach (var kv in ScanLoc(locDir,
                     new HashSet<string>(Ck3Builder.AccoladeAttrKeys, StringComparer.Ordinal), Array.Empty<string>()))
            raw[kv.Key] = kv.Value;

        b.TraitAliases(raw);
        b.TypeAliases(raw, zh);

        // 引用展开：译名/效果里会引用 `$键$`、`[概念|E]`、`[GetMaA('x')]`，补扫被引用的键（两轮）
        for (var round = 0; round < 2; round++)
        {
            var refs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in raw.Values) refs.UnionWith(RefKeys(v));
            refs.ExceptWith(raw.Keys);
            if (refs.Count == 0) break;
            foreach (var kv in ScanLoc(locDir, refs, Ck3Builder.WidePrefixes))
                raw.TryAdd(kv.Key, kv.Value);
        }
        // 引擎动态文本：loc 里的值是脚本调用，直接覆盖成可读名字
        foreach (var kv in Ck3Builder.DynamicNames)
            raw[kv.Key] = zh ? kv.Value.Zh : kv.Value.En;

        var sv = CollectScriptValues(root);
        b.Loc = Finalize(raw, sv, zh);

        // 2) 正式抽取（脚本值表要先算，效果里会引用）
        var costs = LoadCostValues(root);
        b.SvNum = costs;
        b.LoadUnlockMaa();
        var (traditions, tradMap) = b.CollectTraditions();
        var maa = b.CollectMaa(costs);
        var cultures = b.CollectCultures(tradMap);
        var doctrines = b.CollectDoctrines();
        var docNames = doctrines.ToDictionary(
            d => (string)d["key"]!, d => d["name"]?.ToString() ?? "", StringComparer.Ordinal);
        var religions = b.CollectReligions(docNames);
        var tenets = b.CollectTenets();
        var innovations = b.CollectInnovations();

        // 3) 克制关系：闭环 + 每个兵种的「被谁克制」
        var tname = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in maa)
        {
            var t = m["type"]?.ToString() ?? "";
            if (!tname.ContainsKey(t)) tname[t] = m["typeName"]?.ToString() ?? t;
        }
        string Tn(string k) => tname.TryGetValue(k, out var v) ? v : k;

        var cycle = new JsonArray();
        foreach (var (a, c) in Ck3Builder.CounterCycle)
            cycle.Add(new JsonObject
            {
                ["from"] = new JsonObject { ["key"] = a, ["name"] = Tn(a) },
                ["to"] = new JsonObject { ["key"] = c, ["name"] = Tn(c) },
            });
        var countered = new Dictionary<string, List<object?>>(StringComparer.Ordinal);
        foreach (var (a, c) in Ck3Builder.CounterCycle)
        {
            if (!countered.TryGetValue(c, out var list)) countered[c] = list = new List<object?>();
            list.Add(new JsonObject { ["key"] = a, ["name"] = Tn(a) });
        }
        foreach (var m in maa)
        {
            var t = m["type"]?.ToString() ?? "";
            m["counteredBy"] = countered.TryGetValue(t, out var l) ? l : new List<object?>();
        }

        // 展示用的 5 节点闭环：轻骑兵 + 重骑兵合并成「轻/重骑兵」一组
        var cav = zh ? "轻/重骑兵" : "Light/Heavy Cavalry";
        var chain = new JsonArray
        {
            new JsonObject { ["fromName"] = Tn("skirmishers"), ["toName"] = Tn("heavy_infantry") },
            new JsonObject { ["fromName"] = Tn("heavy_infantry"), ["toName"] = Tn("pikemen") },
            new JsonObject { ["fromName"] = Tn("pikemen"), ["toName"] = cav },
            new JsonObject { ["fromName"] = cav, ["toName"] = Tn("archers") },
            new JsonObject { ["fromName"] = Tn("archers"), ["toName"] = Tn("skirmishers") },
        };

        var cnt = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in maa)
        {
            var t = m["type"]?.ToString() ?? "";
            cnt[t] = cnt.TryGetValue(t, out var n) ? n + 1 : 1;
        }
        var typesInfo = new JsonArray();
        foreach (var t in cnt.Keys.OrderBy(x => OrderIndex(Ck3Builder.MaaTypeOrder, x)).ThenBy(x => x, StringComparer.Ordinal))
            typesInfo.Add(new JsonObject { ["key"] = t, ["name"] = Tn(t), ["count"] = cnt[t] });

        var counts = new JsonObject
        {
            ["maa"] = maa.Count,
            ["cultures"] = cultures.Count,
            ["traditions"] = traditions.Count,
            ["religions"] = religions.Count,
            ["tenets"] = tenets.Count,
            ["doctrines"] = doctrines.Count,
            ["innovations"] = innovations.Count,
        };

        var tabs = new JsonArray();
        for (var i = 0; i < Ck3TabIcons.Length; i++)
        {
            var id = Ck3TabIcons[i];
            tabs.Add(new JsonObject
            {
                ["id"] = id,
                ["icon"] = id == "maa" ? "⚔" : id == "cultures" ? "🏛"
                    : id == "traditions" ? "✦" : id == "religions" ? "☪"
                    : id == "tenets" ? "✧" : id == "doctrines" ? "⚖" : "⚙",
                ["layout"] = Ck3TabLayout[i],
                ["count"] = Numbers.ToLong(counts[id]) ?? 0,
            });
        }

        var data = new JsonObject
        {
            ["ok"] = true,
            ["supported"] = true,
            ["game"] = "ck3",
            ["lang"] = locLang,
            ["uiLang"] = uiLang,
            ["maa"] = ToNode(maa),
            ["cultures"] = ToNode(cultures),
            ["traditions"] = ToNode(traditions),
            ["religions"] = ToNode(religions),
            ["tenets"] = ToNode(tenets),
            ["doctrines"] = ToNode(doctrines),
            ["innovations"] = ToNode(innovations),
            ["counts"] = counts,
            ["buildMs"] = buildMs,
            ["types"] = ToNode(maa.Select(m => m["type"]?.ToString() ?? "")
                .Where(s => s.Length > 0).Distinct().OrderBy(s => s, StringComparer.Ordinal)
                .Cast<object?>().ToList()),
            ["typesInfo"] = typesInfo,
            ["catOrders"] = new JsonObject
            {
                ["innovations"] = new JsonArray(Ck3Builder.EraOrder.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["maa"] = new JsonArray(Ck3Builder.MaaTypeOrder.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            },
            ["counterCycle"] = cycle,
            ["counterChain"] = chain,
            ["tradCats"] = ToNode(traditions.Select(t => t["cat"]?.ToString() ?? "")
                .Distinct().OrderBy(s => s, StringComparer.Ordinal).Cast<object?>().ToList()),
            ["tabs"] = tabs,
        };
        return data;
    }

    // ------------------------------------------------------------ 通用脚本工具

    internal static List<string> Glob(string root, string rel)
    {
        try
        {
            var dir = Path.Combine(root, rel);
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.GetFiles(dir, "*.txt").OrderBy(p => p, StringComparer.Ordinal).ToList();
        }
        catch { return new List<string>(); }
    }

    internal static string ReadScript(string path)
    {
        try
        {
            var t = File.ReadAllText(path, Encoding.UTF8);
            if (t.Length > 0 && t[0] == '\uFEFF') t = t[1..];
            return Regex.Replace(t, "#[^\n]*", "");
        }
        catch { return ""; }
    }

    internal static List<(string Key, string Body)> TopBlocks(string text)
    {
        var res = new List<(string, string)>();
        var n = text.Length;
        var i = 0;
        var scan = 0;
        var depth = 0;
        while (i < n)
        {
            var m = TopBlockRe.Match(text, i);
            if (!m.Success) break;
            for (; scan < m.Index; scan++)
            {
                if (text[scan] == '{') depth++;
                else if (text[scan] == '}') depth--;
            }
            if (depth != 0) { i = m.Index + 1; continue; }

            var start = m.Index + m.Length;
            var d = 1;
            var j = start;
            while (j < n && d > 0)
            {
                if (text[j] == '{') d++;
                else if (text[j] == '}') d--;
                j++;
            }
            res.Add((m.Groups[1].Value, text.Substring(start, Math.Max(0, j - 1 - start))));
            i = j;
            scan = j;
        }
        return res;
    }

    private static readonly Regex TopBlockRe =
        new(@"([A-Za-z_][A-Za-z0-9_]*)\s*=\s*\{", RegexOptions.Compiled);

    private static readonly ConcurrentDictionary<string, Regex> SubCache = new();

    internal static string? Sub(string body, string name)
    {
        var rx = SubCache.GetOrAdd(name, n => new Regex(
            "(?:^|[\\s{])" + Regex.Escape(n) + "\\s*=\\s*\\{", RegexOptions.Compiled));
        var m = rx.Match(body);
        if (!m.Success) return null;
        var start = m.Index + m.Length;
        var d = 1;
        var j = start;
        while (j < body.Length && d > 0)
        {
            if (body[j] == '{') d++;
            else if (body[j] == '}') d--;
            j++;
        }
        return body.Substring(start, Math.Max(0, j - 1 - start));
    }

    internal static List<string> SubAll(string body, string name)
    {
        var outList = new List<string>();
        var rx = SubCache.GetOrAdd(name, n => new Regex(
            "(?:^|[\\s{])" + Regex.Escape(n) + "\\s*=\\s*\\{", RegexOptions.Compiled));
        var i = 0;
        while (true)
        {
            var m = rx.Match(body, i);
            if (!m.Success) break;
            var start = m.Index + m.Length;
            var d = 1;
            var j = start;
            while (j < body.Length && d > 0)
            {
                if (body[j] == '{') d++;
                else if (body[j] == '}') d--;
                j++;
            }
            outList.Add(body.Substring(start, Math.Max(0, j - 1 - start)));
            i = j;
        }
        return outList;
    }

    internal static string? Val(string body, string name)
    {
        // 注意：GetOrAdd 的工厂参数是「缓存键」，不能直接拿来拼正则
        var rx = SubCache.GetOrAdd("val:" + name, _ => new Regex(
            "(?:^|[\\s{])" + Regex.Escape(name) + "\\s*=\\s*([^\\s{}]+)", RegexOptions.Compiled));
        var m = rx.Match(body);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    internal static long? ToInt(string? v)
    {
        if (string.IsNullOrEmpty(v)) return null;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? (long)d : null;
    }

    internal static Dictionary<string, string> Pairs(string? block)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(block)) return o;
        foreach (Match m in PairRe.Matches(block)) o[m.Groups[1].Value] = m.Groups[2].Value;
        return o;
    }

    private static readonly Regex PairRe =
        new(@"([A-Za-z_][A-Za-z0-9_]*)\s*=\s*([^\s{}]+)", RegexOptions.Compiled);

    internal static List<string> ListIds(string? block)
    {
        if (string.IsNullOrEmpty(block)) return new List<string>();
        return IdRe.Matches(block).Select(m => m.Value).ToList();
    }

    private static readonly Regex IdRe =
        new(@"([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    internal static Dictionary<string, string> ReadDefines(string path)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                var s = line.TrimStart('\uFEFF');
                var m = DefineRe.Match(s);
                if (m.Success) o[m.Groups[1].Value] = m.Groups[2].Value;
            }
        }
        catch { }
        return o;
    }

    private static readonly Regex DefineRe =
        new(@"^\s*@([A-Za-z0-9_]+)\s*=\s*(\d+(?:\.\d+)?)\s*$", RegexOptions.Compiled);

    // ------------------------------------------------------------ 本地化

    private static readonly Regex LocPat =
        new("^\\s*([A-Za-z0-9_.\\-]+):\\d*\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    internal static readonly Regex RefPat =
        new(@"\$([A-Za-z0-9_.\-]+)\$", RegexOptions.Compiled);
    internal static readonly Regex TokPat =
        new(@"\[([A-Za-z0-9_.\-]+)\|E\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    internal static readonly Regex FuncPat =
        new(@"\[[^\[\]]*\([^\[\]]*\)[^\[\]]*\]", RegexOptions.Compiled);
    internal static readonly Regex TraitPat =
        new(@"GetTrait\(\s*'([A-Za-z0-9_\-]+)'\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    internal static readonly Regex IconPat =
        new(@"\[[a-z0-9_]+_i\]", RegexOptions.Compiled);
    internal static readonly Regex MarkPat =
        new(@"#[A-Za-z0-9_!]*", RegexOptions.Compiled);
    internal static readonly Regex SvPat =
        new(@"\[EmptyScope\.ScriptValue\(\s*'([A-Za-z0-9_]+)'\s*\)\s*\|0\]", RegexOptions.Compiled);
    internal static readonly Regex GetterPat =
        new(@"Get[A-Za-z]+\(\s*['""]?([A-Za-z0-9_\-]+)['""]?\s*\)", RegexOptions.Compiled);
    internal static readonly Regex BldPat = new(
        @"Get(Domicile)?Building\(\s*'([A-Za-z0-9_]+)'\s*\)(?:\.(GetTypeName|GetNameNoTooltip|GetName))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    internal static readonly Regex AddLocIfPat = new(
        @"\[AddLocalizationIf\(\s*[^,\[\]]+,\s*'([A-Za-z0-9_.\-]+)'\s*\)\]", RegexOptions.Compiled);
    internal static readonly Regex ValueRefPat =
        new(@"\$([A-Za-z0-9_]+)\|[^$]*\$", RegexOptions.Compiled);

    /// <summary>扫一遍本地化，只保留 needed 里的键（或匹配前缀的键）。返回原始文本。</summary>
    internal static Dictionary<string, string> ScanLoc(string locDir, HashSet<string> needed, string[] prefixes)
    {
        var outMap = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(locDir)) return outMap;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(locDir, "*.yml", SearchOption.AllDirectories); }
        catch { return outMap; }
        foreach (var fp in files)
        {
            if (!Path.GetExtension(fp).Equals(".yml", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                foreach (var line in File.ReadLines(fp, Encoding.UTF8))
                {
                    var s = line.Length > 0 && line[0] == '\uFEFF' ? line[1..] : line;
                    var m = LocPat.Match(s);
                    if (!m.Success) continue;
                    var k = m.Groups[1].Value;
                    if (outMap.ContainsKey(k)) continue;
                    if (needed.Contains(k) || (prefixes.Length > 0 && prefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal))))
                        outMap[k] = m.Groups[2].Value;
                }
            }
            catch { }
        }
        return outMap;
    }

    internal static string Title(string? key)
    {
        return string.Join(" ", (key ?? "").Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
    }

    internal static string PyStr(double v)
    {
        if (v == Math.Floor(v) && Math.Abs(v) < 1e15)
            return v.ToString("0.0", CultureInfo.InvariantCulture);
        return v.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string Pick(Dictionary<string, string> loc, IEnumerable<string> keys)
    {
        foreach (var k in keys)
            if (loc.TryGetValue(k, out var v) && v.Length > 0) return v;
        return "";
    }

    internal static string Concept(string key, Dictionary<string, string> raw, bool zh)
    {
        if (key == "scheme") return zh ? "计谋" : "Scheme";
        foreach (var k in new[] { "game_concept_" + key, key })
            if (raw.TryGetValue(k, out var v) && v.Length > 0) return v;
        return key;
    }

    internal static string TraitName(string key, Dictionary<string, string> raw)
    {
        foreach (var k in new[] { key, "trait_" + key, "game_concept_" + key })
            if (raw.TryGetValue(k, out var v) && v.Length > 0) return v;
        return Title(key);
    }

    internal static string GetterName(string key, Dictionary<string, string> raw)
    {
        foreach (var cand in new[] { key, key + "_name", "trait_" + key, "game_concept_" + key })
        {
            if (raw.TryGetValue(cand, out var v) && v.Length > 0 && !v.TrimStart().StartsWith('['))
                return v;
        }
        return Title(key);
    }

    internal static string BuildingName(string key, string getter, Dictionary<string, string> raw)
    {
        var typeK = "building_type_" + key;
        var instK = "building_" + key;
        var domK = key + "_domicile_building";
        var order = (getter ?? "").ToLowerInvariant() == "gettypename"
            ? new[] { typeK, instK, domK } : new[] { instK, typeK, domK };
        foreach (var k in order)
        {
            if (raw.TryGetValue(k, out var v) && v.Length > 0 && !v.TrimStart().StartsWith('['))
                return v;
        }
        return Title(key);
    }

    /// <summary>展开 $变量$、[概念|E]、[GetTrait('x')...] 与脚本值，丢掉算不出来的函数。</summary>
    internal static string ResolveText(string text, Dictionary<string, string> raw,
        Dictionary<string, string> sv, int depth, bool zh)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (text.Contains('$'))
        {
            text = RefPat.Replace(text, m =>
            {
                var k = m.Groups[1].Value;
                if (raw.TryGetValue(k, out var v) && depth > 0) return ResolveText(v, raw, sv, depth - 1, zh);
                return k;
            });
        }
        text = SvPat.Replace(text, m =>
            sv.TryGetValue(m.Groups[1].Value, out var v) ? v : "?");
        if (text.Contains("AddLocalizationIf"))
        {
            text = AddLocIfPat.Replace(text, m =>
            {
                var k = m.Groups[1].Value;
                if (raw.TryGetValue(k, out var v) && v.Length > 0 && depth > 0)
                    return ResolveText(v, raw, sv, depth - 1, zh);
                return "";
            });
        }
        if (text.Contains('['))
        {
            text = FuncPat.Replace(text, m =>
            {
                var s = m.Value;
                if (s.Contains("GetTextIcon") || s.Contains("GetIcon")) return "";
                var bm = BldPat.Match(s);
                if (bm.Success) return BuildingName(bm.Groups[2].Value, bm.Groups[3].Value, raw);
                var tm = TraitPat.Match(s);
                if (tm.Success) return TraitName(tm.Groups[1].Value, raw);
                var gm = GetterPat.Match(s);
                if (gm.Success) return GetterName(gm.Groups[1].Value, raw);
                return "";
            });
            text = TokPat.Replace(text, m => Concept(m.Groups[1].Value, raw, zh));
        }
        return text;
    }

    /// <summary>一段文本里所有需要回查本地化的键（用于二次扫描）。</summary>
    internal static HashSet<string> RefKeys(string text)
    {
        var ks = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in RefPat.Matches(text)) ks.Add(m.Groups[1].Value);
        foreach (Match m in TokPat.Matches(text)) ks.Add(m.Groups[1].Value);
        foreach (Match m in TraitPat.Matches(text))
        {
            var t = m.Groups[1].Value;
            ks.Add(t); ks.Add("trait_" + t); ks.Add("game_concept_" + t);
        }
        foreach (Match m in GetterPat.Matches(text))
        {
            var g = m.Groups[1].Value;
            ks.Add(g); ks.Add(g + "_name"); ks.Add("trait_" + g); ks.Add("game_concept_" + g);
            ks.Add("building_" + g); ks.Add("building_type_" + g); ks.Add(g + "_domicile_building");
        }
        foreach (Match m in AddLocIfPat.Matches(text)) ks.Add(m.Groups[1].Value);
        foreach (var k in ks.ToList()) ks.Add("game_concept_" + k);
        return ks;
    }

    internal static string Clean(string s, bool zh)
    {
        s = s.Replace("\\n", " ");
        s = MarkPat.Replace(s, "");
        s = IconPat.Replace(s, "");
        s = Regex.Replace(s, "@[A-Za-z0-9_]+!?", "");
        s = Regex.Replace(s, @"\[[^\[\]]{1,80}\]", "");
        s = ValueRefPat.Replace(s, m =>
        {
            var k = m.Groups[1].Value;
            if (Ck3Builder.ValueNames.TryGetValue(k, out var pair)) return zh ? pair.Zh : pair.En;
            return Title(k);
        });
        s = Regex.Replace(s, "[ \t]{2,}", " ");
        return s.Trim();
    }

    internal static Dictionary<string, string> Finalize(Dictionary<string, string> raw,
        Dictionary<string, string> sv, bool zh)
    {
        var outMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in raw) outMap[kv.Key] = Clean(ResolveText(kv.Value, raw, sv, 3, zh), zh);
        // 兜底：还有漏网的 $key$，绝不能把美元符号带到界面上
        foreach (var kv in outMap.ToList())
        {
            if (!kv.Value.Contains('$')) continue;
            outMap[kv.Key] = Clean(RefPat.Replace(kv.Value, m =>
            {
                var k = m.Groups[1].Value;
                if (outMap.TryGetValue(k, out var v) && v.Length > 0) return v;
                if (raw.TryGetValue(k, out var rv) && rv.Length > 0) return rv;
                return Title(k);
            }), zh);
        }
        return outMap;
    }

    // ------------------------------------------------------------ 脚本值

    /// <summary>读 common/script_values 里的招募花费与维护费（@常量 / 别名）。</summary>
    private static Dictionary<string, double> LoadCostValues(string root)
    {
        var at = new Dictionary<string, string>(StringComparer.Ordinal);
        var plain = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Wiki.Glob(root, D_SV))
        {
            var text = ReadScript(f);
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Split('#')[0].TrimEnd();
                var m = Regex.Match(line, @"^\s*(@?)([A-Za-z0-9_]+)\s*=\s*(.+?)\s*$");
                if (!m.Success) continue;
                var k = m.Groups[2].Value;
                var v = m.Groups[3].Value.Trim();
                if (!Regex.IsMatch(v, @"^-?[\d.]+|@\[[^\]]*\]$")) continue;
                if (m.Groups[1].Value == "@") at[k] = v; else plain[k] = v;
            }
        }

        var outMap = new Dictionary<string, double>(StringComparer.Ordinal);
        var busy = new HashSet<string>(StringComparer.Ordinal);

        double? Eval(string name, int depth = 0)
        {
            if (outMap.TryGetValue(name, out var known)) return known;
            if (depth > 4 || busy.Contains(name)) return null;
            var expr = at.TryGetValue(name, out var e1) ? e1 : plain.TryGetValue(name, out var e2) ? e2 : null;
            if (expr is null) return null;
            busy.Add(name);
            var e = expr.StartsWith("@[") && expr.EndsWith(']') ? expr[2..^1] : expr;
            e = Regex.Replace(e, @"@?([A-Za-z_][A-Za-z0-9_]*)",
                mm => (Eval(mm.Groups[1].Value, depth + 1) ?? 0).ToString("0.######", CultureInfo.InvariantCulture));
            double? v = null;
            if (Regex.IsMatch(e, @"^[\d.\s+\-*/()]+$")) v = EvalExpr(e);
            else if (double.TryParse(expr, NumberStyles.Float, CultureInfo.InvariantCulture, out var d2)) v = d2;
            busy.Remove(name);
            if (v is not null) outMap[name] = v.Value;
            return v;
        }

        foreach (var k in at.Keys.Concat(plain.Keys).ToList()) Eval(k);
        return outMap;
    }

    /// <summary>极简四则运算求值（脚本值里的 @[base * mult] 这类表达式）。</summary>
    private static double? EvalExpr(string s)
    {
        var i = 0;

        double? ParseNumber()
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i < s.Length && s[i] == '(')
            {
                i++;
                var v = ParseSum();
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i < s.Length && s[i] == ')') i++;
                return v;
            }
            var start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            if (start == i) return null;
            return double.TryParse(s[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        }

        double? ParseFactor()
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            double sign = 1;
            while (i < s.Length && (s[i] == '+' || s[i] == '-'))
            {
                if (s[i] == '-') sign = -sign;
                i++;
            }
            var v = ParseNumber();
            return v is null ? null : v * sign;
        }

        double? ParseProduct()
        {
            var v = ParseFactor();
            if (v is null) return null;
            while (true)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i >= s.Length || (s[i] != '*' && s[i] != '/')) return v;
                var op = s[i++];
                var r = ParseFactor();
                if (r is null) return null;
                v = op == '*' ? v * r : (r == 0 ? null : v / r);
                if (v is null) return null;
            }
        }

        double? ParseSum()
        {
            var v = ParseProduct();
            if (v is null) return null;
            while (true)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i >= s.Length || (s[i] != '+' && s[i] != '-')) return v;
                var op = s[i++];
                var r = ParseProduct();
                if (r is null) return null;
                v = op == '+' ? v + r : v - r;
            }
        }

        var result = ParseSum();
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i == s.Length ? result : null;
    }

    /// <summary>读 common/script_values/*.txt 的顶层常量，用来还原效果里的 ScriptValue('x')。</summary>
    private static Dictionary<string, string> CollectScriptValues(string root)
    {
        var outMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Wiki.Glob(root, D_SV))
        {
            var text = ReadScript(f);
            foreach (Match m in Regex.Matches(text, @"^([A-Za-z0-9_]+)\s*=\s*(-?\d+(?:\.\d+)?)\s*$",
                         RegexOptions.Multiline))
                outMap[m.Groups[1].Value] = m.Groups[2].Value;
            // 顶层别名：`xxx_gain = minor_development_progress_gain`
            foreach (Match m in Regex.Matches(text, @"^([A-Za-z0-9_]+)\s*=\s*([A-Za-z_][A-Za-z0-9_]*)\s*$",
                         RegexOptions.Multiline))
            {
                var v = m.Groups[2].Value;
                if (v is not ("yes" or "no" or "true" or "false")) refs[m.Groups[1].Value] = v;
            }
            foreach (var (k, body) in TopBlocks(text))
            {
                var v = Val(body, "value");
                if (string.IsNullOrEmpty(v)) continue;
                if (Regex.IsMatch(v, @"^-?\d+(?:\.\d+)?$")) outMap.TryAdd(k, v);
                else if (Regex.IsMatch(v, @"^[A-Za-z0-9_]+$")) refs[k] = v;
            }
        }
        // 别名链解析（最多 3 层），让 `_tt` 这类包装键也能取到数字
        for (var round = 0; round < 3; round++)
        {
            foreach (var (k, v) in refs.ToList())
            {
                if (!outMap.ContainsKey(v)) continue;
                outMap.TryAdd(k, outMap[v]);
                refs.Remove(k);
            }
        }
        return outMap;
    }

    internal static int OrderIndex(string[] seq, string key)
    {
        var i = Array.IndexOf(seq, key);
        return i < 0 ? 999 : i;
    }

    // ------------------------------------------------------------ JSON 辅助

    private static JsonNode? ToNode(object? o) => JsonSerializer.SerializeToNode(o, JsonOpts);

    private static JsonArray ToNode(List<Dictionary<string, object?>> list)
    {
        var arr = new JsonArray();
        foreach (var d in list) arr.Add(JsonSerializer.SerializeToNode(d, JsonOpts));
        return arr;
    }

    // ------------------------------------------------------------ 图标索引（HOI4 / EU4）

    private static string FindHoi4Root(string gameRoot)
    {
        var hint = (gameRoot ?? "").Trim().TrimEnd('\\', '/');
        foreach (var cand in new[] { hint, hint.Length > 0 ? Path.Combine(hint, "Hearts of Iron IV") : "" })
            if (cand.Length > 0 && Directory.Exists(Path.Combine(cand, "common", "ideas"))) return cand;
        return "";
    }

    private static string FindEu4Root(string gameRoot)
    {
        var hint = (gameRoot ?? "").Trim().Trim('"').TrimEnd('\\', '/');
        foreach (var cand in new[] { hint, hint.Length > 0 ? Path.Combine(hint, "Europa Universalis IV") : "" })
            if (cand.Length > 0 && Directory.Exists(Path.Combine(cand, "common", "ideas"))) return cand;
        return "";
    }

    private static string FindVic3Root(string gameRoot)
    {
        var hint = (gameRoot ?? "").Trim().TrimEnd('\\', '/');
        foreach (var cand in new[] { hint, hint.Length > 0 ? Path.Combine(hint, "Victoria 3") : "" })
            if (cand.Length > 0 && Directory.Exists(Path.Combine(cand, "game", "common", "laws"))) return cand;
        return "";
    }

    private static readonly object GfxLock = new();
    private static readonly Dictionary<string, Dictionary<string, string>> GfxCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>解析 interface/*.gfx，建立 `GFX_名字 → dds 相对路径` 索引（HOI4）。</summary>
    private static Dictionary<string, string> GfxIndex(string root)
    {
        lock (GfxLock)
        {
            if (GfxCache.TryGetValue(root, out var cached)) return cached;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var dir = Path.Combine(root, "interface");
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, "*.gfx"))
                    {
                        string txt;
                        try { txt = File.ReadAllText(f, Encoding.UTF8); } catch { continue; }
                        foreach (Match mm in Regex.Matches(txt, @"spriteType\s*=\s*\{(.*?)\}",
                                     RegexOptions.Singleline | RegexOptions.IgnoreCase))
                        {
                            var body = mm.Groups[1].Value;
                            var n = Regex.Match(body, "name\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                            var t = Regex.Match(body, "texturefile\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                            if (n.Success && t.Success) map.TryAdd(n.Groups[1].Value, t.Groups[1].Value);
                        }
                    }
                }
            }
            catch { }
            GfxCache[root] = map;
            return map;
        }
    }

    private static string ResolveDds(string root, string rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return "";
        var p = Path.Combine(root, rel.Replace('\\', '/').Replace('/', '\\'));
        if (File.Exists(p)) return p;
        // 大小写不一致时在目录里找一次同名文件
        try
        {
            var dir = Path.GetDirectoryName(p);
            var name = Path.GetFileName(p);
            if (dir is not null && Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "*.dds"))
                    if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) return f;
        }
        catch { }
        return "";
    }

    private static Dictionary<string, string> GfxMap(string root)
    {
        lock (GfxLock)
        {
            if (GfxCache.TryGetValue("eu4:" + root, out var cached)) return cached;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var dir = Path.Combine(root, "interface");
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, "*.gfx", SearchOption.AllDirectories))
                    {
                        string txt;
                        try { txt = File.ReadAllText(f, Encoding.UTF8); } catch { continue; }
                        foreach (Match bm in Regex.Matches(txt, @"spriteType\s*=\s*\{(.*?)\}",
                                     RegexOptions.Singleline | RegexOptions.IgnoreCase))
                        {
                            var body = bm.Groups[1].Value;
                            var n = Regex.Match(body, "name\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                            var t = Regex.Match(body, "texturefile\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                            if (n.Success && t.Success)
                                map[n.Groups[1].Value] = t.Groups[1].Value
                                    .Replace("\\\\", "/").Replace("//", "/").Replace('\\', '/');
                        }
                    }
                }
            }
            catch { }
            GfxCache["eu4:" + root] = map;
            return map;
        }
    }
}

/// <summary>CK3 百科构建器（对应旧版 wiki.py 里的全局状态 + 各 _collect_*）。</summary>
internal sealed class Ck3Builder
{
    public readonly string Root;
    public readonly string LocLang;
    public readonly bool Zh;

    /// <summary>清洗后的译名表（可以直接显示）。</summary>
    public Dictionary<string, string> Loc = new(StringComparer.Ordinal);
    /// <summary>招募/维护花费的真实数字（效果里的修正值也查这张表）。</summary>
    public Dictionary<string, double> SvNum = new(StringComparer.Ordinal);

    private readonly Dictionary<string, List<(string Kind, string Name)>> _unlockMaa = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UnlockInfo> _maaUnlock = new(StringComparer.Ordinal);

    private sealed class UnlockInfo
    {
        public List<(string Kind, string Name)> Grantors = new();
        public string? Region;
    }

    public Ck3Builder(string root, string locLang)
    {
        Root = root;
        LocLang = locLang;
        Zh = locLang == "simp_chinese";
    }

    // ------------------------------------------------------------ 内置表

    public static readonly string[] WidePrefixes =
    {
        "game_concept_", "task_", "knight_culture_", "MAPMODE_", "trait_",
        "culture_parameter_", "doctrine_parameter_", "tenet_parameter_",
    };

    public static readonly string[] AccoladeAttrKeys =
    {
        "archer_attribute", "archer_cavalry", "archers", "camel_cavalry", "camelry_attribute",
        "crossbowmen_attribute", "elephant_cavalry", "elephantry_attribute", "gunpowder",
        "gunpowder_attribute", "heavy_cavalry", "heavy_infantry", "horse_archer_attribute",
        "lancer_attribute", "light_cavalry", "outrider_attribute", "pike_attribute", "pikemen",
        "skirmisher_attribute", "skirmishers", "vanguard_attribute",
    };

    public static readonly Dictionary<string, (string Zh, string En)> TypeNames = new(StringComparer.Ordinal)
    {
        ["skirmishers"] = ("散兵", "Skirmishers"),
        ["heavy_infantry"] = ("重步兵", "Heavy Infantry"),
        ["archers"] = ("弓箭手", "Archers"),
        ["pikemen"] = ("长枪兵", "Pikemen"),
        ["light_cavalry"] = ("轻骑兵", "Light Cavalry"),
        ["heavy_cavalry"] = ("重骑兵", "Heavy Cavalry"),
        ["archer_cavalry"] = ("弓骑兵", "Horse Archers"),
        ["camel_cavalry"] = ("骆驼骑兵", "Camel Cavalry"),
        ["elephant_cavalry"] = ("战象", "War Elephants"),
        ["siege_weapon"] = ("攻城器械", "Siege Weapons"),
        ["gunpowder"] = ("铳手", "Gunpowder Units"),
        ["peasant_militia"] = ("农民民兵", "Peasant Militia"),
        ["nomadic_horde"] = ("游牧部众", "Nomadic Horde"),
    };

    public static readonly Dictionary<string, string[]> TypeLocCandidates = new(StringComparer.Ordinal)
    {
        ["skirmishers"] = new[] { "accolade_skirmishers", "accolade_skirmisher", "skirmisher_attribute" },
        ["heavy_infantry"] = new[] { "accolade_heavy_infantry", "heavy_infantry_attribute" },
        ["archers"] = new[] { "accolade_archers", "archers_attribute" },
        ["pikemen"] = new[] { "accolade_pikes", "pike_attribute" },
        ["light_cavalry"] = new[] { "accolade_light_cavalry", "light_cavalry_attribute" },
        ["heavy_cavalry"] = new[] { "accolade_heavy_cavalry", "heavy_cavalry_attribute" },
        ["archer_cavalry"] = new[] { "accolade_horse_archer", "horse_archer_attribute" },
        ["gunpowder"] = new[] { "gunpowder_attribute", "accolade_gunpowder" },
    };

    public static readonly Dictionary<string, (string Zh, string En)> WinterNames = new(StringComparer.Ordinal)
    {
        ["mild_winter"] = ("温和冬季", "Mild Winter"),
        ["normal_winter"] = ("普通冬季", "Normal Winter"),
        ["harsh_winter"] = ("严寒冬季", "Harsh Winter"),
    };

    public static readonly (string From, string To)[] CounterCycle =
    {
        ("skirmishers", "heavy_infantry"),
        ("heavy_infantry", "pikemen"),
        ("pikemen", "light_cavalry"),
        ("pikemen", "heavy_cavalry"),
        ("light_cavalry", "archers"),
        ("heavy_cavalry", "archers"),
        ("archers", "skirmishers"),
    };

    public static readonly string[] ModKeys = { "damage", "toughness", "pursuit", "screen" };

    public static readonly Dictionary<string, (string Zh, string En)> ModifierNames = new(StringComparer.Ordinal)
    {
        ["accolade_glory_gain_mult"] = ("荣耀获取", "Glory gain"),
        ["knight_limit"] = ("骑士数量上限", "Knight limit"),
        ["character_travel_speed_mult"] = ("旅行速度", "Travel speed"),
        ["movement_speed"] = ("行军速度", "Movement speed"),
        ["movement_speed_land_raiding"] = ("陆上劫掠行军速度", "Land raiding movement speed"),
        ["men_at_arms_recruitment_cost"] = ("兵士招募花费", "Men-at-arms recruitment cost"),
        ["knight_effectiveness_mult"] = ("骑士战斗力", "Knight effectiveness"),
        ["character_travel_safety_mult"] = ("旅行安全", "Travel safety"),
        ["hard_casualty_modifier"] = ("伤亡人数", "Casualties"),
        ["cultural_acceptance_gain_mult"] = ("文化接受度增长", "Cultural acceptance gain"),
        ["diplomatic_range_mult"] = ("外交范围", "Diplomatic range"),
        ["maa_toughness_mult"] = ("兵士坚韧", "Men-at-arms toughness"),
        ["maa_damage_mult"] = ("兵士伤害", "Men-at-arms damage"),
        ["prowess_per_piety_level"] = ("每级虔诚提供勇武", "Prowess per piety level"),
        ["faith_conversion_piety_cost_mult"] = ("改信花费", "Faith conversion cost"),
        ["development_growth"] = ("发展度成长", "Development growth"),
        ["monthly_income_mult"] = ("每月收入", "Monthly income"),
        ["dynasty_house_opinion"] = ("家族好感", "Dynasty house opinion"),
        ["development_growth_factor"] = ("发展度成长", "Development growth"),
        ["men_at_arms_cap"] = ("兵士上限", "Men-at-arms cap"),
        ["men_at_arms_limit"] = ("兵士数量上限", "Men-at-arms limit"),
        ["men_at_arms_title_limit"] = ("按头衔的兵士上限", "Men-at-arms limit per title"),
        ["men_at_arms_maintenance"] = ("兵士维护费", "Men-at-arms maintenance"),
        ["monthly_income_from_herd_mult"] = ("畜群月收入", "Monthly income from herds"),
        ["naval_movement_speed_mult"] = ("海军航行速度", "Naval movement speed"),
        ["domain_tax_mult"] = ("领地税收", "Domain tax"),
        ["different_culture_opinion"] = ("异文化好感", "Different culture opinion"),
        ["same_faith_opinion"] = ("同信仰好感", "Same faith opinion"),
        ["siege_phase_time"] = ("围城阶段耗时", "Siege phase time"),
        ["vassal_limit"] = ("封臣上限", "Vassal limit"),
        ["same_culture_mercenary_hire_cost_mult"] = ("同文化雇佣兵花费", "Same culture mercenary cost"),
        ["mercenary_count_mult"] = ("雇佣兵数量", "Mercenary count"),
        ["building_slot_add"] = ("建筑槽位", "Building slots"),
        ["epidemic_resistance"] = ("瘟疫抗性", "Epidemic resistance"),
        // ---- 个人信条（personal_tenet_modifier）用到的修饰器 ----
        ["advantage"] = ("优势", "Advantage"),
        ["artifact_decay_reduction_mult"] = ("宝物腐坏减缓", "Artifact decay reduction"),
        ["build_speed"] = ("建造速度", "Build speed"),
        ["character_spiritual_fulfillment_gain_mult"] = ("灵性满足获取", "Spiritual fulfillment gain"),
        ["character_travel_safety"] = ("旅行安全", "Travel safety"),
        ["character_travel_speed"] = ("旅行速度", "Travel speed"),
        ["controlled_province_advantage"] = ("控制领地优势", "Controlled province advantage"),
        ["county_opinion_add"] = ("伯爵领好感", "County opinion"),
        ["courtier_and_guest_opinion"] = ("廷臣与宾客好感", "Courtier and guest opinion"),
        ["desert_mountains_construction_gold_cost"] = ("沙漠山地建造花费", "Desert mountains construction cost"),
        ["desert_mountains_travel_danger"] = ("沙漠山地危险", "Desert mountains danger"),
        ["different_faith_opinion"] = ("异信仰好感", "Different faith opinion"),
        ["diplomacy"] = ("外交", "Diplomacy"),
        ["diplomacy_per_piety_level"] = ("每级虔诚外交", "Diplomacy per piety level"),
        ["domain_limit"] = ("直辖领上限", "Domain limit"),
        ["dread_decay_mult"] = ("恐惧衰减", "Dread decay"),
        ["dread_gain_mult"] = ("恐惧获取", "Dread gain"),
        ["dynasty_opinion"] = ("王朝好感", "Dynasty opinion"),
        ["enemy_hostile_scheme_success_chance_add"] = ("敌方敌对计谋成功率", "Enemy hostile scheme success chance"),
        ["enemy_hostile_scheme_success_chance_max_add"] = ("敌方敌对计谋最大成功率", "Enemy hostile scheme max success chance"),
        ["enemy_scheme_secrecy_add"] = ("敌方计谋隐秘", "Enemy scheme secrecy"),
        ["fertility"] = ("生育力", "Fertility"),
        ["health"] = ("健康", "Health"),
        ["hills_construction_gold_cost"] = ("丘陵建造花费", "Hills construction cost"),
        ["holy_order_hire_cost_mult"] = ("圣战骑士团雇佣花费", "Holy order hire cost"),
        ["intrigue"] = ("谋略", "Intrigue"),
        ["knight_effectiveness_per_dread"] = ("每点恐惧骑士战斗力", "Knight effectiveness per dread"),
        ["learn_language_scheme_phase_duration_add"] = ("学习语言计谋阶段时长", "Learn language scheme phase duration"),
        ["levy_reinforcement_rate"] = ("征召兵补充率", "Levy reinforcement rate"),
        ["levy_reinforcement_rate_same_faith"] = ("同信仰征召兵补充率", "Same faith levy reinforcement rate"),
        ["lifestyle_hunter_xp_gain_mult"] = ("猎人生活方式经验", "Hunter lifestyle experience"),
        ["lifestyle_mystic_xp_gain_mult"] = ("神秘主义生活方式经验", "Mystic lifestyle experience"),
        ["lifestyle_reveler_xp_gain_mult"] = ("狂欢生活方式经验", "Reveler lifestyle experience"),
        ["lifestyle_scholar_xp_gain_mult"] = ("学者生活方式经验", "Scholar lifestyle experience"),
        ["maa_siege_value_mult"] = ("兵士攻城值", "Men-at-arms siege value"),
        ["martial"] = ("军事", "Martial"),
        ["martial_per_piety_level"] = ("每级虔诚军事", "Martial per piety level"),
        ["max_learn_language_schemes_add"] = ("学习语言计谋上限", "Max learn language schemes"),
        ["minority_opinion"] = ("少数族群好感", "Minority opinion"),
        ["minority_tax_contribution_mult"] = ("少数族群赋税贡献", "Minority tax contribution"),
        ["monthly_diplomacy_lifestyle_xp_gain_mult"] = ("每月外交生活方式经验", "Monthly diplomacy lifestyle experience"),
        ["monthly_dynasty_prestige"] = ("每月王朝威望", "Monthly dynasty prestige"),
        ["monthly_intrigue_lifestyle_xp_gain_mult"] = ("每月谋略生活方式经验", "Monthly intrigue lifestyle experience"),
        ["monthly_learning_lifestyle_xp_gain_mult"] = ("每月学识生活方式经验", "Monthly learning lifestyle experience"),
        ["monthly_legitimacy_add"] = ("每月合法性", "Monthly legitimacy"),
        ["monthly_lifestyle_xp_gain_mult"] = ("每月生活方式经验", "Monthly lifestyle experience"),
        ["monthly_piety_from_buildings_mult"] = ("来自建筑的虔诚", "Piety from buildings"),
        ["monthly_piety_gain_mult"] = ("每月虔诚获取", "Monthly piety gain"),
        ["monthly_piety_gain_per_court_position_mult"] = ("每个宫廷职位每月虔诚", "Monthly piety per court position"),
        ["monthly_piety_gain_per_knight_mult"] = ("每名骑士每月虔诚", "Monthly piety per knight"),
        ["monthly_prestige_gain_mult"] = ("每月威望获取", "Monthly prestige gain"),
        ["monthly_prestige_gain_per_court_position_mult"] = ("每个宫廷职位每月威望", "Monthly prestige per court position"),
        ["monthly_stewardship_lifestyle_xp_gain_mult"] = ("每月管理生活方式经验", "Monthly stewardship lifestyle experience"),
        ["mountains_construction_gold_cost"] = ("山地建造花费", "Mountains construction cost"),
        ["mountains_travel_danger"] = ("山地危险", "Mountains danger"),
        ["negate_fertility_penalty_add"] = ("抵消生育力惩罚", "Negate fertility penalty"),
        ["negate_health_penalty_add"] = ("抵消健康惩罚", "Negate health penalty"),
        ["owned_legend_spread_mult"] = ("己方传说传播", "Owned legend spread"),
        ["owned_scheme_secrecy_add"] = ("己方计谋隐秘", "Owned scheme secrecy"),
        ["piety_level_impact_mult"] = ("虔诚等级影响", "Piety level impact"),
        ["prowess"] = ("勇武", "Prowess"),
        ["stewardship"] = ("管理", "Stewardship"),
        ["stress_gain_mult"] = ("压力获取", "Stress gain"),
        ["stress_loss_mult"] = ("压力损失", "Stress loss"),
        ["study_faith_scheme_phase_duration_add"] = ("研习信仰计谋阶段时长", "Study faith scheme phase duration"),
        ["tax_mult"] = ("赋税", "Tax"),
        ["terraced_hills_construction_gold_cost"] = ("梯田丘陵建造花费", "Terraced hills construction cost"),
        ["tyranny_gain_mult"] = ("暴政获取", "Tyranny gain"),
    };

    public static readonly Dictionary<string, (string Zh, string En)> StatNames = new(StringComparer.Ordinal)
    {
        ["damage"] = ("伤害", "Damage"), ["toughness"] = ("坚韧", "Toughness"),
        ["pursuit"] = ("追击", "Pursuit"), ["screen"] = ("掩护", "Screen"),
        ["siege_value"] = ("攻城", "Siege"), ["stack"] = ("规模", "Stack"),
    };

    public static readonly Dictionary<string, (string Zh, string En)> ValueNames = new(StringComparer.Ordinal)
    {
        ["OPINION"] = ("好感度", "Opinion"),
        ["VALUE"] = ("数值", "Value"),
        ["MOD_COMBAT_COUNTER_RESISTANCE"] = ("克制抗性", "Counter Resistance"),
    };

    public static readonly Dictionary<string, (string Zh, string En)> DynamicNames = new(StringComparer.Ordinal)
    {
        ["knight_culture_player_plural_no_tooltip_lowercase"] = ("骑士", "Knights"),
        ["knight_culture_player_no_tooltip_lowercase"] = ("骑士", "Knight"),
    };

    public static readonly Dictionary<string, (string Zh, string En)> ParameterNames = new(StringComparer.Ordinal)
    {
        ["faith_coherence_high"] = ("信仰凝聚力较高", "High faith coherence"),
        ["faith_coherence_normal"] = ("信仰凝聚力中等", "Normal faith coherence"),
        ["faith_coherence_low"] = ("信仰凝聚力较低", "Low faith coherence"),
        ["can_only_be_learned_in_person"] = ("只能当面学习（口述传承）", "Can only be learned in person"),
    };

    public static readonly Dictionary<string, (string Zh, string En)> RegionNames = new(StringComparer.Ordinal)
    {
        ["world_europe_west_iberia"] = ("伊比利亚地区", "Iberia"),
        ["ghw_region_germany"] = ("德意志地区", "Germany"),
        ["ghw_region_britannia"] = ("不列颠地区", "Britannia"),
        ["world_india"] = ("印度地区", "India"),
        ["custom_sahel_proper"] = ("萨赫勒地区", "The Sahel"),
        ["world_innovation_camels"] = ("沙漠/骆驼地区", "Camel regions"),
        ["world_asia_north_east"] = ("东北亚地区", "Northeast Asia"),
        ["custom_eastern_roman_empire"] = ("东罗马帝国地区", "Eastern Roman Empire"),
        ["world_himalaya"] = ("喜马拉雅地区", "Himalaya"),
        ["world_steppe"] = ("草原地区", "Steppe"),
        ["world_transoxiana"] = ("河中地区", "Transoxiana"),
    };

    public static readonly Dictionary<string, (string Zh, string En)> PartnerNames = new(StringComparer.Ordinal)
    {
        ["number_of_spouses"] = ("配偶数量上限", "Max spouses"),
        ["number_of_consorts"] = ("伴侣数量上限", "Max consorts"),
        ["number_of_consorts_by_tier"] = ("按头衔等级的伴侣上限", "Consort limit by tier"),
    };

    public static readonly Dictionary<string, string> TradCats = new(StringComparer.Ordinal)
    {
        ["combat"] = "战斗",
        ["realm"] = "领地",
        ["societal"] = "社会",
        ["maa"] = "军事",
        ["regional"] = "地域",
        ["ritual"] = "仪式",
    };

    public static readonly string[] MaaTypeOrder =
    {
        "skirmishers", "heavy_infantry", "archers", "gunpowder", "pikemen",
        "light_cavalry", "heavy_cavalry", "camel_cavalry", "archer_cavalry",
        "elephant_cavalry", "siege_weapon", "nomadic_horde", "peasant_militia",
    };

    public static readonly string[] EraOrder =
    {
        "culture_era_tribal", "culture_era_early_medieval",
        "culture_era_high_medieval", "culture_era_late_medieval",
    };

    private static readonly HashSet<string> AbsModifiers = new(StringComparer.Ordinal)
        { "monthly_dynasty_prestige", "monthly_legitimacy_add", "health" };

    private static readonly Regex DangerKey = new(@"^[a-z_]+_travel_danger$", RegexOptions.Compiled);

    // ------------------------------------------------------------ 小工具

    private static string Mn(Dictionary<string, (string Zh, string En)> map, string key, bool zh)
        => map.TryGetValue(key, out var p) ? (zh ? p.Zh : p.En) : "";

    private string Pick(IEnumerable<string> keys)
    {
        foreach (var k in keys)
            if (Loc.TryGetValue(k, out var v) && v.Length > 0) return v;
        return "";
    }

    private static bool HasCjk(string s) => (s ?? "").Any(c => c >= '\u4e00' && c <= '\u9fff');

    private static bool LooksRaw(string v, string typ)
    {
        var s = (v ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0 || s.StartsWith('[')) return true;
        var norm = s.Replace(" ", "_");
        return norm == typ || norm == typ + "s" || norm == typ.TrimEnd('s');
    }

    private string TypeName(string typ)
    {
        if (string.IsNullOrEmpty(typ)) return "";
        var cands = TypeLocCandidates.TryGetValue(typ, out var c) ? c.ToList() : new List<string>();
        cands.Add(typ + "_attribute");
        cands.Add("accolade_" + typ);
        cands.Add(typ);
        var v = Pick(cands);
        if (v.Length > 0 && !LooksRaw(v, typ) && !(Zh && !HasCjk(v))) return v;
        if (TypeNames.TryGetValue(typ, out var pair)) return Zh ? pair.Zh : pair.En;
        return typ;
    }

    private string AccoladeAttrName(string t)
    {
        if (Loc.TryGetValue(t, out var v) && v.Length > 0 && !LooksRaw(v, t)) return v;
        var tn = TypeName(t);
        return tn.Length > 0 && tn != t ? tn : t;
    }

    private string RegionName(string tok)
    {
        if (RegionNames.TryGetValue(tok, out var pair)) return Zh ? pair.Zh : pair.En;
        var s = tok;
        foreach (var p in new[] { "world_", "ghw_region_", "custom_", "_proper" }) s = s.Replace(p, "");
        s = s.Replace("_", " ").Trim();
        return string.Join(" ", s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    private string ResolveName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        for (var i = 0; i < 3; i++)
        {
            var m = Regex.Match(name, @"\$([A-Za-z_][A-Za-z0-9_]*)\$");
            if (!m.Success) break;
            var inner = Loc.TryGetValue(m.Groups[1].Value, out var a) && a.Length > 0
                ? a
                : Loc.TryGetValue(m.Groups[1].Value + "_name", out var b) ? b : "";
            if (inner.Length == 0) break;
            name = name[..m.Index] + inner + name[(m.Index + m.Length)..];
        }
        return name;
    }

    private static string UnlockPrefix(string kind, bool zh) => kind switch
    {
        "tradition" => zh ? "需要传统：" : "Requires tradition: ",
        "innovation" => zh ? "需要革新：" : "Requires innovation: ",
        "dynasty_legacy" => zh ? "需要家族传承：" : "Requires dynasty legacy: ",
        _ => "",
    };

    private static string FmtNum(string v, bool pct = true)
    {
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
            return "";
        if (pct && Math.Abs(f) > 0 && Math.Abs(f) < 1)
            return (f * 100).ToString("+0;-0;0", CultureInfo.InvariantCulture) + "%";
        if (f == Math.Truncate(f))
            return ((long)f).ToString("+0;-0;0", CultureInfo.InvariantCulture);
        return f.ToString("+0.#####;-0.#####;0", CultureInfo.InvariantCulture);
    }

    private double? ModNumber(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim();
        if (Regex.IsMatch(v, @"^-?\d+(?:\.\d+)?$"))
            return double.Parse(v, CultureInfo.InvariantCulture);
        return SvNum.TryGetValue(v, out var x) ? x : null;
    }

    private string ModLine(string k, string v, string nm)
    {
        var val = ModNumber(v);
        if (val is null) return nm.Trim();
        if (DangerKey.IsMatch(k))
        {
            var terrain = Zh
                ? nm.Replace("危险", "").Trim()
                : Regex.Replace(nm, @"\s*Danger\s*$", "", RegexOptions.IgnoreCase).Trim();
            var amount = (int)Math.Round(Math.Abs(val.Value));
            var down = val.Value < 0;
            return Zh
                ? $"{(down ? "降低" : "提高")}{terrain}地形危险 {amount} 点"
                : $"{(down ? "Reduce" : "Increase")} {terrain} travel danger by {amount}";
        }
        return nm + " " + FmtNum(val.Value.ToString(CultureInfo.InvariantCulture), !AbsModifiers.Contains(k));
    }

    private List<object?> ModList(string inner)
    {
        var outList = new List<object?>();
        foreach (var (bk, bv) in Wiki.TopBlocks(inner))
        {
            var mods = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var kv in Wiki.Pairs(bv))
            {
                if (!ModKeys.Contains(kv.Key)) continue;
                var iv = Wiki.ToInt(kv.Value);
                if (iv is not null) mods[kv.Key] = iv.Value;
            }
            outList.Add(new Dictionary<string, object?>
            {
                ["key"] = bk,
                ["name"] = Loc.TryGetValue(bk, out var nm) && nm.Length > 0 ? nm : bk,
                ["mods"] = mods,
            });
        }
        return outList;
    }

    private static Dictionary<string, object?> Entry(string key, string name, string desc,
        string catKey, string catLabel, List<object?>? attrs = null, List<object?>? effects = null,
        List<object?>? links = null)
    {
        return new Dictionary<string, object?>
        {
            ["key"] = key,
            ["name"] = name,
            ["desc"] = desc,
            ["cat"] = catLabel ?? "",
            ["catKey"] = catKey ?? "",
            ["attrs"] = attrs ?? new List<object?>(),
            ["effects"] = effects ?? new List<object?>(),
            ["links"] = links ?? new List<object?>(),
        };
    }

    private Dictionary<string, object?> Pillar(string pkey)
    {
        if (string.IsNullOrEmpty(pkey)) return new Dictionary<string, object?>();
        return new Dictionary<string, object?>
        {
            ["key"] = pkey,
            ["name"] = Loc.TryGetValue(pkey + "_name", out var n) && n.Length > 0 ? n
                : Loc.TryGetValue(pkey, out var n2) && n2.Length > 0 ? n2 : pkey,
            ["desc"] = Loc.TryGetValue(pkey + "_desc", out var d) ? d : "",
        };
    }

    private string LocAny(string key, IEnumerable<string>? first = null)
    {
        var cands = new List<string>();
        if (first is not null) cands.AddRange(first);
        cands.AddRange(new[]
        {
            key + "_name", key, "culture_parameter_" + key, "doctrine_parameter_" + key,
            "tenet_parameter_" + key, "game_concept_" + key, "building_" + key,
        });
        foreach (var c in cands)
            if (Loc.TryGetValue(c, out var v) && v.Length > 0) return v;
        return "";
    }

    private string CustomName(string key)
    {
        var m = Regex.Match(key, @"^unlock_siege_maa_(first|second|third|fourth)_level$");
        if (m.Success)
        {
            var tier = m.Groups[1].Value switch
            {
                "first" => (Zh: "一", En: 1),
                "second" => (Zh: "二", En: 2),
                "third" => (Zh: "三", En: 3),
                _ => (Zh: "四", En: 4),
            };
            return Zh ? $"解锁第{tier.Zh}级地域攻城器械兵士"
                      : $"Unlocks tier {tier.En} regional siege men-at-arms";
        }
        if (key.StartsWith("eligible_accolade_", StringComparison.Ordinal) && key.EndsWith("_attribute", StringComparison.Ordinal))
        {
            var attr = key["eligible_accolade_".Length..];
            var nm = "";
            if (Loc.TryGetValue(attr, out var a) && a.Length > 0) nm = a;
            else if (Loc.TryGetValue(attr + "_name", out var b) && b.Length > 0) nm = b;
            else if (Loc.TryGetValue(attr + "_attribute", out var c) && c.Length > 0) nm = c;
            if (nm.Length > 0) return Zh ? $"可授予{nm}勋号" : $"Grants access to {nm} accolades";
            return Zh ? "可授予对应勋号" : "Grants access to an accolade";
        }
        return "";
    }

    // ------------------------------------------------------------ 解锁映射

    public void LoadUnlockMaa()
    {
        var tokenMap = _unlockMaa;

        void AddToken(string tok, string kind, string name)
        {
            if (!tokenMap.TryGetValue(tok, out var list)) tokenMap[tok] = list = new List<(string, string)>();
            if (!list.Contains((kind, name))) list.Add((kind, name));
        }

        void AddKey(string mk, string kind, string name, string? region)
        {
            if (!_maaUnlock.TryGetValue(mk, out var e)) _maaUnlock[mk] = e = new UnlockInfo();
            if (!e.Grantors.Contains((kind, name))) e.Grantors.Add((kind, name));
            if (!string.IsNullOrEmpty(region) && string.IsNullOrEmpty(e.Region)) e.Region = region;
        }

        void Scan(string folder, string kind)
        {
            foreach (var f in Wiki.Glob(Root, folder))
            {
                var text = Wiki.ReadScript(f);
                foreach (var (key, body) in Wiki.TopBlocks(text))
                {
                    if (!body.Contains("unlock_")) continue;
                    var name = "";
                    if (Loc.TryGetValue(key, out var n1) && n1.Length > 0) name = n1;
                    else if (Loc.TryGetValue(key + "_name", out var n2) && n2.Length > 0) name = n2;
                    else if (Loc.TryGetValue(key.Replace("innovation_", ""), out var n3) && n3.Length > 0) name = n3;
                    else if (Loc.TryGetValue(key.Replace("innovation_", "") + "_name", out var n4) && n4.Length > 0) name = n4;
                    else name = Wiki.Title(key);
                    name = ResolveName(name);

                    var rm = Regex.Match(body, @"region\s*=\s*([a-z_0-9]+)");
                    var region = rm.Success ? rm.Groups[1].Value : null;

                    // 参数写法 unlock_maa_<x> = yes
                    foreach (Match m in Regex.Matches(body, @"unlock_maa_([A-Za-z0-9_]+)"))
                    {
                        var tok = m.Groups[1].Value;
                        if (Regex.IsMatch(body, @"\bunlock_maa_" + Regex.Escape(tok) + @"\s*=\s*yes\b"))
                        {
                            AddToken("unlock_maa_" + tok, kind, name);
                            AddKey(tok, kind, name, region);
                        }
                    }
                    // 等号写法 unlock_maa = <x>
                    foreach (Match m in Regex.Matches(body, @"unlock_maa\s*=\s*([a-z_0-9]+)"))
                    {
                        var mk = m.Groups[1].Value;
                        AddToken("unlock_maa_" + mk, kind, name);
                        AddKey(mk, kind, name, region);
                    }
                    // 其余 unlock_<x> = yes
                    foreach (var tok in Regex.Matches(body, @"unlock_[A-Za-z0-9_]+")
                                 .Select(m => m.Value).Distinct())
                    {
                        if (Regex.IsMatch(body, @"\b" + Regex.Escape(tok) + @"\s*=\s*yes\b"))
                            AddToken(tok, kind, name);
                    }
                }
            }
        }

        Scan(Wiki.D_TRAD_PUBLIC, "tradition");
        Scan(Wiki.D_INNOV_PUBLIC, "innovation");
    }

    private List<object?> MaaUnlockExtra(string key, string maaName)
    {
        var outList = new List<object?>();
        if (!_maaUnlock.TryGetValue(key, out var e)) return outList;
        foreach (var (kind, name) in e.Grantors)
        {
            if (kind != "dynasty_legacy" && name == maaName) continue;
            outList.Add(UnlockPrefix(kind, Zh) + name);
        }
        if (!string.IsNullOrEmpty(e.Region))
            outList.Add((Zh ? "限定地区：" : "Restricted region: ") + RegionName(e.Region!));
        return outList;
    }

    private static List<object?> MergeRecruit(List<object?> a, List<object?> b)
    {
        var outList = new List<object?>();
        foreach (var t in a.Concat(b))
        {
            var s = t?.ToString() ?? "";
            if (s.Length == 0) continue;
            if (outList.Any(x => (x?.ToString() ?? "") == s)) continue;
            outList.Add(s);
        }
        return outList.Take(6).ToList();
    }

    private static readonly Dictionary<string, (string Zh, string En)> SpecialTriggers = new(StringComparer.Ordinal)
    {
        ["culture_uses_eastern_siege_weapons_trigger"] = ("仅限东方攻城器械文化", "Eastern siege weapons culture only"),
        ["is_landless_adventurer"] = ("仅限无地冒险者", "Landless adventurer only"),
        ["mpo_can_recruit_nomad_maa_trigger"] = ("需游牧制（斡儿朵）", "Requires nomadic government"),
    };

    private List<object?> RecruitReqs(string body)
    {
        var cr = Wiki.Sub(body, "can_recruit") ?? "";
        if (cr.Length == 0) return new List<object?>();
        var outList = new List<object?>();

        void Add(string? t)
        {
            if (string.IsNullOrEmpty(t)) return;
            if (outList.Any(x => (x?.ToString() ?? "") == t)) return;
            outList.Add(t);
        }

        // 文化/特殊兵种：valid_for_maa_trigger = { PARAMETER = unlock_<x> }
        foreach (Match m in Regex.Matches(cr, @"PARAMETER\s*=\s*(unlock_[A-Za-z0-9_]+)"))
        {
            var p = m.Groups[1].Value;
            if (_unlockMaa.TryGetValue(p, out var got))
            {
                foreach (var (kind, name) in got) Add(UnlockPrefix(kind, Zh) + name);
            }
            else
            {
                var t = Loc.TryGetValue("culture_parameter_" + p, out var a) && a.Length > 0 ? a
                    : Loc.TryGetValue(p + "_name", out var b) && b.Length > 0 ? b
                    : Loc.TryGetValue(p, out var c) ? c : "";
                if (t.Length > 0 && !t.Contains("可以招募")) Add(t);
            }
        }
        // 其他文化参数（非 unlock_，通常是文化特性开关）
        foreach (Match m in Regex.Matches(cr, @"PARAMETER\s*=\s*([A-Za-z0-9_]+)"))
        {
            var p = m.Groups[1].Value;
            if (p.StartsWith("unlock_", StringComparison.Ordinal)) continue;
            var t = Loc.TryGetValue("culture_parameter_" + p, out var a) && a.Length > 0 ? a
                : Loc.TryGetValue(p + "_name", out var b) && b.Length > 0 ? b
                : Loc.TryGetValue(p, out var c) ? c : "";
            if (t.Length > 0 && !t.Contains("可以招募")) Add(t);
        }
        // 勋号兵种
        var accoladeHit = false;
        foreach (var al in Wiki.SubAll(cr, "accolade_attribute_level"))
        {
            var t = Wiki.Val(al, "type");
            if (string.IsNullOrEmpty(t)) continue;
            var nm = AccoladeAttrName(t);
            int? lvl = null;
            var lm = Regex.Match(al, @"level\s*>=\s*(\d+)");
            if (!lm.Success) lm = Regex.Match(al, @"level\s*=\s*(\d+)");
            if (lm.Success) lvl = int.Parse(lm.Groups[1].Value);
            var s = (Zh ? "需要勋号：" : "Requires accolade: ") + nm;
            if (lvl is not null)
                s += Zh ? $"（属性等级 ≥ {lvl}）" : $" (attribute level ≥ {lvl})";
            Add(s);
            accoladeHit = true;
        }
        // 革新
        foreach (Match m in Regex.Matches(cr, @"has_innovation\s*=\s*([a-z_0-9]+)"))
        {
            var i = m.Groups[1].Value;
            var t = Loc.TryGetValue(i + "_name", out var a) && a.Length > 0 ? a
                : Loc.TryGetValue(i, out var b) ? b : "";
            if (t.Length > 0) Add((Zh ? "需要革新：" : "Requires innovation: ") + t);
        }
        // NOT 块整体剔除：那是文化层面的禁用规则，不是招募条件
        var crPos = cr;
        foreach (var nb in Wiki.SubAll(cr, "NOT")) crPos = crPos.Replace(nb, "");
        foreach (Match m in Regex.Matches(crPos, @"has_cultural_parameter\s*=\s*([a-z_0-9]+)"))
        {
            var p = m.Groups[1].Value;
            if (p.StartsWith("unlock_", StringComparison.Ordinal) && _unlockMaa.TryGetValue(p, out var got))
            {
                foreach (var (kind, name) in got) Add(UnlockPrefix(kind, Zh) + name);
                continue;
            }
            var t = Loc.TryGetValue("culture_parameter_" + p, out var a) && a.Length > 0 ? a
                : Loc.TryGetValue(p + "_name", out var b) ? b : "";
            if (t.Length > 0 && !t.Contains("可以招募"))
                Add((Zh ? "需文化特性：" : "Requires cultural pillar: ") + t);
        }
        // 政体
        foreach (Match m in Regex.Matches(cr, @"has_government\s*=\s*([a-z_0-9]+)"))
        {
            var g = m.Groups[1].Value;
            var t = Loc.TryGetValue(g, out var a) && a.Length > 0 ? a
                : Loc.TryGetValue(g + "_name", out var b) ? b : "";
            if (t.Length > 0) Add((Zh ? "限定政体：" : "Government: ") + t);
        }
        // 特殊触发器（脚本名，游戏里没有本地化键）
        foreach (var kv in SpecialTriggers)
            if (cr.Contains(kv.Key)) Add(Zh ? kv.Value.Zh : kv.Value.En);
        // 兜底：any_accolade 但没解析出具体属性
        if (cr.Contains("any_accolade") && !accoladeHit)
            Add(Zh ? "需要对应勋号/勋章" : "Requires an appropriate accolade");
        // 家族传承（dynasty perk）
        foreach (Match m in Regex.Matches(cr, @"has_dynasty_perk\s*=\s*([a-z_0-9]+)"))
        {
            var p = m.Groups[1].Value;
            var t = Loc.TryGetValue(p, out var a) && a.Length > 0 ? a
                : Loc.TryGetValue(p + "_name", out var b) ? b : "";
            if (t.Length > 0) Add((Zh ? "需要家族传承：" : "Requires dynasty legacy: ") + t);
        }
        return outList.Take(8).ToList();
    }

    // ------------------------------------------------------------ 别名

    public void TraitAliases(Dictionary<string, string> raw)
    {
        foreach (var f in Wiki.Glob(Root, Path.Combine("common", "traits")))
        {
            foreach (var (k, b) in Wiki.TopBlocks(Wiki.ReadScript(f)))
            {
                var nb = Wiki.Sub(b, "name");
                if (string.IsNullOrEmpty(nb)) continue;
                var m = Regex.Match(nb, @"\bdesc\s*=\s*([A-Za-z0-9_]+)");
                if (m.Success && raw.ContainsKey(m.Groups[1].Value) && !raw.ContainsKey(k))
                    raw[k] = raw[m.Groups[1].Value];
            }
        }
    }

    public void TypeAliases(Dictionary<string, string> raw, bool zh)
    {
        foreach (var kv in TypeNames)
        {
            var v = raw.TryGetValue(kv.Key, out var s) ? s : "";
            if (v.Length == 0 || v.Trim() == $"[{kv.Key}|E]" || v.Trim() == $"[{kv.Key.ToLowerInvariant()}|E]")
                raw[kv.Key] = zh ? kv.Value.Zh : kv.Value.En;
        }
    }

    // ------------------------------------------------------------ 兵种

    public List<Dictionary<string, object?>> CollectMaa(Dictionary<string, double> costs)
    {
        var outList = new List<Dictionary<string, object?>>();
        foreach (var f in Wiki.Glob(Root, Wiki.D_MAA_PUBLIC))
        {
            var defines = Wiki.ReadDefines(f);
            var text = Wiki.ReadScript(f);
            foreach (var (key, body) in Wiki.TopBlocks(text))
            {
                var typ = Wiki.Val(body, "type") ?? "";

                long? Num(string name)
                {
                    var v = Wiki.Val(body, name);
                    if (v is null) return null;
                    if (v.StartsWith('@')) v = defines.TryGetValue(v[1..], out var d) ? d : "";
                    return Wiki.ToInt(v);
                }

                object? CostOf(string field)
                {
                    var blk = Wiki.Sub(body, field);
                    if (string.IsNullOrEmpty(blk)) return null;
                    var g = (Wiki.Val(blk, "gold") ?? "").TrimStart('@');
                    if (g.Length == 0) return null;
                    double v;
                    if (!costs.TryGetValue(g, out v))
                    {
                        if (Wiki.ToInt(g) is not { } iv) return null;
                        v = iv;
                    }
                    return v % 1 == 0 ? (long)v : Math.Round(v, 2);
                }

                var counters = new List<object?>();
                var cb = Wiki.Sub(body, "counters");
                if (!string.IsNullOrEmpty(cb))
                {
                    foreach (var kv in Wiki.Pairs(cb))
                        counters.Add(new Dictionary<string, object?>
                        {
                            ["key"] = kv.Key,
                            ["name"] = TypeName(kv.Key),
                            ["mult"] = Wiki.ToInt(kv.Value),
                        });
                }

                var winter = new List<object?>();
                var wb = Wiki.Sub(body, "winter_bonus");
                if (!string.IsNullOrEmpty(wb))
                {
                    foreach (var (wk, wv) in Wiki.TopBlocks(wb))
                    {
                        var wname = Loc.TryGetValue(wk, out var lv) && lv.Length > 0 ? lv
                            : WinterNames.TryGetValue(wk, out var pair)
                                ? (Zh ? pair.Zh : pair.En) : Wiki.Title(wk);
                        var mods = new Dictionary<string, object?>(StringComparer.Ordinal);
                        foreach (var kv in Wiki.Pairs(wv))
                        {
                            if (!ModKeys.Contains(kv.Key)) continue;
                            var iv = Wiki.ToInt(kv.Value);
                            if (iv is not null) mods[kv.Key] = iv.Value;
                        }
                        winter.Add(new Dictionary<string, object?>
                        {
                            ["key"] = wk, ["name"] = wname, ["mods"] = mods,
                        });
                    }
                }

                var flags = new List<object?>();
                var sro = Wiki.Val(body, "special_recruit_only");
                if (sro == "yes" || sro == "yes_no") flags.Add("special");
                if (Wiki.Val(body, "allowed_in_hired_troops") == "yes") flags.Add("hired");
                var mr = Num("max_regiments");
                if (mr is > 0) flags.Add("max:" + mr);
                if (!string.IsNullOrEmpty(Wiki.Val(body, "siege_tier"))) flags.Add("siege");

                var name = Loc.TryGetValue(key, out var nm) && nm.Length > 0 ? nm : key;
                outList.Add(new Dictionary<string, object?>
                {
                    ["key"] = key,
                    ["name"] = name,
                    ["nameEn"] = Wiki.Title(key),
                    ["flavor"] = Loc.TryGetValue(key + "_flavor", out var fl) ? fl : "",
                    ["type"] = typ,
                    ["typeName"] = TypeName(typ),
                    ["damage"] = Num("damage"),
                    ["toughness"] = Num("toughness"),
                    ["pursuit"] = Num("pursuit"),
                    ["screen"] = Num("screen"),
                    ["stack"] = Num("stack"),
                    ["siege"] = Num("siege_value"),
                    ["provision"] = Num("provision_cost"),
                    ["cost"] = CostOf("buy_cost"),
                    ["maintLow"] = CostOf("low_maintenance_cost"),
                    ["maintHigh"] = CostOf("high_maintenance_cost"),
                    ["recruit"] = MergeRecruit(RecruitReqs(body), MaaUnlockExtra(key, name)),
                    ["terrain"] = ModList(Wiki.Sub(body, "terrain_bonus") ?? ""),
                    ["counters"] = counters,
                    ["winter"] = winter,
                    ["flags"] = flags,
                    ["src"] = Path.GetFileName(f),
                });
            }
        }
        return outList
            .OrderBy(x => Wiki.OrderIndex(MaaTypeOrder, x["type"]?.ToString() ?? ""))
            .ThenBy(x => x["name"]?.ToString() ?? "", StringComparer.Ordinal)
            .ToList();
    }

    // ------------------------------------------------------------ 文化 / 传统

    public (List<Dictionary<string, object?>> Items, Dictionary<string, Dictionary<string, object?>> Map)
        CollectTraditions()
    {
        var items = new List<Dictionary<string, object?>>();
        var map = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var f in Wiki.Glob(Root, Wiki.D_TRAD_PUBLIC))
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            var catKey = Regex.Replace(stem, @"^[0-9]+_", "").Replace("_traditions", "");
            var cat = TradCats.TryGetValue(catKey, out var c) ? c
                : catKey != "debug" ? "扩展内容" : "调试";
            foreach (var (key, body) in Wiki.TopBlocks(Wiki.ReadScript(f)))
            {
                if (cat == "调试") continue;
                var it = new Dictionary<string, object?>
                {
                    ["key"] = key,
                    ["name"] = Loc.TryGetValue(key + "_name", out var n) && n.Length > 0 ? n
                        : Loc.TryGetValue(key, out var n2) && n2.Length > 0 ? n2 : key,
                    ["desc"] = Loc.TryGetValue(key + "_desc", out var d) ? d : "",
                    ["effects"] = TradEffects(body),
                    ["cat"] = cat,
                };
                items.Add(it);
                map[key] = new Dictionary<string, object?>
                {
                    ["key"] = key, ["name"] = it["name"], ["desc"] = it["desc"], ["effects"] = it["effects"],
                };
            }
        }
        return (items.OrderBy(x => x["cat"]?.ToString() ?? "", StringComparer.Ordinal)
                .ThenBy(x => x["name"]?.ToString() ?? "", StringComparer.Ordinal).ToList(), map);
    }

    private List<object?> TradEffects(string body)
    {
        var eff = new List<object?>();
        var pb = Wiki.Sub(body, "parameters");
        if (!string.IsNullOrEmpty(pb))
        {
            foreach (var p in Wiki.Pairs(pb).Keys)
            {
                var t = Loc.TryGetValue("culture_parameter_" + p, out var v) ? v : "";
                if (t.Length > 0) eff.Add(t);
            }
        }
        // 婚姻类传统（多配偶制 / 纳妾制）没有 parameters，效果写在 partners 里
        var partners = Wiki.Sub(body, "partners");
        if (!string.IsNullOrEmpty(partners))
        {
            foreach (var kv in Wiki.Pairs(partners))
            {
                var nm = Mn(PartnerNames, kv.Key, Zh);
                if (nm.Length == 0 && Loc.TryGetValue(kv.Key, out var lv)) nm = lv;
                if (nm.Length > 0 && Regex.IsMatch(kv.Value, @"^-?\d+(?:\.\d+)?$"))
                    eff.Add(nm + " " + kv.Value);
            }
        }
        foreach (var block in new[] { "character_modifier", "culture_modifier" })
        {
            var mb = Wiki.Sub(body, block);
            if (string.IsNullOrEmpty(mb)) continue;
            foreach (var kv in Wiki.Pairs(mb))
            {
                if (kv.Key.StartsWith("ai_", StringComparison.Ordinal)) continue;
                var nm = Loc.TryGetValue(kv.Key, out var lv) && lv.Length > 0 ? lv : Mn(ModifierNames, kv.Key, Zh);
                if (nm.Length > 0) eff.Add(ModLine(kv.Key, kv.Value, nm));
            }
        }
        return Dedup(eff);
    }

    private static List<object?> Dedup(List<object?> eff)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var outList = new List<object?>();
        foreach (var e in eff)
        {
            var s = (e?.ToString() ?? "").Trim();
            if (s.Length > 0 && seen.Add(s)) outList.Add(s);
        }
        return outList;
    }

    public List<Dictionary<string, object?>> CollectCultures(
        Dictionary<string, Dictionary<string, object?>> tradMap)
    {
        var outList = new List<Dictionary<string, object?>>();
        foreach (var f in Wiki.Glob(Root, Wiki.D_CULT_PUBLIC))
        {
            foreach (var (key, body) in Wiki.TopBlocks(Wiki.ReadScript(f)))
            {
                var tradEntries = new List<object?>();
                foreach (var k in Wiki.ListIds(Wiki.Sub(body, "traditions")))
                    tradEntries.Add(TradEntry(k, tradMap, false, ""));
                foreach (var dbody in Wiki.SubAll(body, "dlc_tradition"))
                {
                    var tk = Wiki.Val(dbody, "trait");
                    if (!string.IsNullOrEmpty(tk))
                        tradEntries.Add(TradEntry(tk, tradMap, true, Wiki.Val(dbody, "requires_dlc_flag") ?? ""));
                }

                var parents = new List<object?>();
                foreach (var p in Wiki.ListIds(Wiki.Sub(body, "parents")))
                    parents.Add(new Dictionary<string, object?>
                    {
                        ["key"] = p,
                        ["name"] = Loc.TryGetValue(p, out var pn) && pn.Length > 0 ? pn : p,
                    });

                outList.Add(new Dictionary<string, object?>
                {
                    ["key"] = key,
                    ["name"] = Loc.TryGetValue(key, out var nm) && nm.Length > 0 ? nm : key,
                    ["ethos"] = Pillar(Wiki.Val(body, "ethos") ?? ""),
                    ["heritage"] = Pillar(Wiki.Val(body, "heritage") ?? ""),
                    ["language"] = Pillar(Wiki.Val(body, "language") ?? ""),
                    ["martial"] = Pillar(Wiki.Val(body, "martial_custom") ?? ""),
                    ["head"] = Pillar(Wiki.Val(body, "head_determination") ?? ""),
                    ["traditions"] = tradEntries,
                    ["parents"] = parents,
                    ["created"] = Wiki.Val(body, "created") ?? "",
                });
            }
        }
        return outList.OrderBy(x => x["name"]?.ToString() ?? "", StringComparer.Ordinal).ToList();
    }

    private Dictionary<string, object?> TradEntry(string key,
        Dictionary<string, Dictionary<string, object?>> tradMap, bool dlc, string requires)
    {
        tradMap.TryGetValue(key, out var baseEntry);
        var name = baseEntry?["name"]?.ToString() ?? "";
        var desc = baseEntry?["desc"]?.ToString() ?? "";
        if (name.Length == 0)
            name = Loc.TryGetValue(key + "_name", out var n) && n.Length > 0 ? n
                : Loc.TryGetValue(key, out var n2) && n2.Length > 0 ? n2 : key;
        if (desc.Length == 0) desc = Loc.TryGetValue(key + "_desc", out var d) ? d : "";
        var effects = baseEntry?["effects"] as List<object?> ?? new List<object?>();
        return new Dictionary<string, object?>
        {
            ["key"] = key, ["name"] = name, ["desc"] = desc,
            ["effects"] = effects, ["dlc"] = dlc, ["requires"] = requires,
        };
    }

    // ------------------------------------------------------------ 宗教 / 教义 / 革新

    public List<Dictionary<string, object?>> CollectDoctrines()
    {
        var outList = new List<Dictionary<string, object?>>();
        foreach (var f in Wiki.Glob(Root, Wiki.D_DOCTRINE_PUBLIC))
        {
            foreach (var (key, body) in Wiki.TopBlocks(Wiki.ReadScript(f)))
            {
                var g = Wiki.Val(body, "doctrine_group_type") ?? "";
                var gname = Pick(new[] { g + "_name", g });
                if (gname.Length == 0) gname = g.Length > 0 ? g : "其他";
                outList.Add(Entry(key,
                    Loc.TryGetValue(key + "_name", out var n) && n.Length > 0 ? n
                        : Loc.TryGetValue(key, out var n2) && n2.Length > 0 ? n2 : Wiki.Title(key),
                    Loc.TryGetValue(key + "_desc", out var d) ? d : "",
                    g, gname,
                    effects: GenericEffects(body, new[] { "doctrine_parameter_", "culture_parameter_" })));
            }
        }
        return outList.OrderBy(x => x["cat"]?.ToString() ?? "", StringComparer.Ordinal)
            .ThenBy(x => x["name"]?.ToString() ?? "", StringComparer.Ordinal).ToList();
    }

    public List<Dictionary<string, object?>> CollectReligions(Dictionary<string, string> docNames)
    {
        var outList = new List<Dictionary<string, object?>>();
        foreach (var f in Wiki.Glob(Root, Wiki.D_RELIGION_PUBLIC))
        {
            foreach (var (key, body) in Wiki.TopBlocks(Wiki.ReadScript(f)))
            {
                var fam = "";
                var rd = Wiki.Sub(body, "religion_details");
                if (!string.IsNullOrEmpty(rd)) fam = Wiki.Val(rd, "family") ?? "";
                var docs = Regex.Matches(body, "^\tdoctrine\\s*=\\s*([a-z_0-9]+)", RegexOptions.Multiline)
                    .Select(m => m.Groups[1].Value).Distinct().ToList();
                var links = docs.Select(d => docNames.TryGetValue(d, out var dn) && dn.Length > 0 ? dn : d)
                    .Cast<object?>().ToList();
                outList.Add(Entry(key,
                    Loc.TryGetValue(key, out var n) && n.Length > 0 ? n : key,
                    Loc.TryGetValue(key + "_desc", out var d) ? d : "",
                    fam,
                    Pick(new[] { fam }) is { Length: > 0 } fname ? fname : fam.Length > 0 ? fam : "其他",
                    attrs: new List<object?>
                    {
                        new Dictionary<string, object?> { ["label"] = "教义", ["value"] = $"{docs.Count} 项" },
                    },
                    links: links));
            }
        }
        return outList.OrderBy(x => x["name"]?.ToString() ?? "", StringComparer.Ordinal).ToList();
    }

    private List<object?> TenetPersonal(string body)
    {
        var outList = new List<object?>();
        var pb = Wiki.Sub(body, "personal_tenet_parameters");
        if (!string.IsNullOrEmpty(pb))
        {
            foreach (var p in Wiki.ListIds(pb))
            {
                var t = Loc.TryGetValue("doctrine_parameter_" + p, out var a) && a.Length > 0 ? a
                    : Loc.TryGetValue(p, out var b) && b.Length > 0 ? b
                    : Loc.TryGetValue("tenet_parameter_" + p, out var c) ? c : "";
                if (t.Length > 0) outList.Add(t);
            }
        }
        var mb = Wiki.Sub(body, "personal_tenet_modifier");
        if (!string.IsNullOrEmpty(mb))
        {
            foreach (var kv in Wiki.Pairs(mb))
            {
                var nm = Loc.TryGetValue(kv.Key, out var lv) && lv.Length > 0 ? lv : Mn(ModifierNames, kv.Key, Zh);
                if (nm.Length > 0) outList.Add(ModLine(kv.Key, kv.Value, nm));
            }
        }
        return Dedup(outList);
    }

    public List<Dictionary<string, object?>> CollectTenets()
    {
        var outList = new List<Dictionary<string, object?>>();
        foreach (var f in Wiki.Glob(Root, Wiki.D_TENET_PUBLIC))
        {
            foreach (var (key, body) in Wiki.TopBlocks(Wiki.ReadScript(f)))
            {
                var dlc = Wiki.Val(body, "requires_dlc_flag") ?? "";
                var cat = dlc.Length > 0 ? "需 DLC" : "通用";
                var attrs = new List<object?>();
                // 虔诚花费：piety_cost 按宗教分支给不同档位，单档写数字，多档写「最低~最高」
                var pc = Wiki.Sub(body, "piety_cost");
                if (!string.IsNullOrEmpty(pc))
                {
                    var nums = new List<int>();
                    foreach (Match m in Regex.Matches(pc, @"\bvalue\s*=\s*([A-Za-z_0-9]+)"))
                        if (SvNum.TryGetValue(m.Groups[1].Value, out var pn))
                            nums.Add((int)pn);
                    if (nums.Count > 0)
                    {
                        var lo = nums.Min();
                        var hi = nums.Max();
                        attrs.Add(new Dictionary<string, object?>
                        {
                            ["label"] = Zh ? "虔诚花费" : "Piety cost",
                            ["value"] = lo == hi ? lo.ToString() : $"{lo}~{hi}",
                        });
                    }
                }
                // 分歧度：divergence_multiplier 是倍率，1 表示不变
                var dm = Wiki.Val(body, "divergence_multiplier");
                if (!string.IsNullOrEmpty(dm) &&
                    double.TryParse(dm, NumberStyles.Float, CultureInfo.InvariantCulture, out var dmv))
                {
                    var pct = (dmv - 1) * 100;
                    if (pct != 0)
                        attrs.Add(new Dictionary<string, object?>
                        {
                            ["label"] = Zh ? "分歧度" : "Divergence",
                            ["value"] = pct.ToString("+0.#####;-0.#####;0", CultureInfo.InvariantCulture) + "%",
                        });
                }
                if (dlc.Length > 0)
                    attrs.Add(new Dictionary<string, object?>
                    {
                        ["label"] = Zh ? "需要 DLC" : "DLC",
                        ["value"] = dlc == "by_god_alone" ? "By God Alone" : Wiki.Title(dlc),
                    });

                var core = GenericEffects(body,
                    new[] { "doctrine_parameter_", "culture_parameter_", "tenet_parameter_" },
                    excludeBlocks: new[] { "personal_tenet_modifier" });
                var e = Entry(key,
                    Loc.TryGetValue(key + "_name", out var n) && n.Length > 0 ? n
                        : Loc.TryGetValue(key, out var n2) && n2.Length > 0 ? n2 : Wiki.Title(key),
                    Loc.TryGetValue(key + "_desc", out var d) ? d : "",
                    dlc.Length > 0 ? "dlc" : "base", cat, attrs: attrs, effects: core);
                var rawPersonal = TenetPersonal(body);
                var seenCore = new HashSet<string>(core.Select(x => x?.ToString() ?? ""), StringComparer.Ordinal);
                var personal = rawPersonal.Where(p => !seenCore.Contains(p?.ToString() ?? "")).ToList();
                e["personal"] = personal;
                if (rawPersonal.Count > 0 && personal.Count == 0) e["personalSameAsCore"] = true;
                outList.Add(e);
            }
        }
        return outList.OrderBy(x => x["name"]?.ToString() ?? "", StringComparer.Ordinal).ToList();
    }

    public List<Dictionary<string, object?>> CollectInnovations()
    {
        var outList = new List<Dictionary<string, object?>>();
        foreach (var f in Wiki.Glob(Root, Wiki.D_INNOV_PUBLIC))
        {
            foreach (var (key, body) in Wiki.TopBlocks(Wiki.ReadScript(f)))
            {
                var era = Wiki.Val(body, "culture_era") ?? "";
                var grp = Wiki.Val(body, "group") ?? "";
                var skill = Wiki.Val(body, "skill") ?? "";
                var attrs = new List<object?>();
                foreach (var (label, v) in new[] { ("类别", grp), ("相关能力", skill) })
                {
                    var nm = Loc.TryGetValue(v, out var lv) && lv.Length > 0 ? lv : v;
                    if (nm.Length > 0) attrs.Add(new Dictionary<string, object?> { ["label"] = label, ["value"] = nm });
                }
                outList.Add(Entry(key,
                    Loc.TryGetValue(key, out var n) && n.Length > 0 ? n : key,
                    Loc.TryGetValue(key + "_desc", out var d) ? d : "",
                    era, Pick(new[] { era }) is { Length: > 0 } en ? en : era.Length > 0 ? era : "其他",
                    attrs: attrs,
                    effects: GenericEffects(body, new[] { "culture_parameter_" })));
            }
        }
        return outList.OrderBy(x => x["cat"]?.ToString() ?? "", StringComparer.Ordinal)
            .ThenBy(x => x["name"]?.ToString() ?? "", StringComparer.Ordinal).ToList();
    }

    // ------------------------------------------------------------ 通用效果

    private List<object?> GenericEffects(string body, string[] prefixes,
        string[]? excludeBlocks = null)
    {
        var eff = new List<object?>();
        foreach (var blk in new[] { "parameters", "special_parameters" })
        {
            var pb = Wiki.Sub(body, blk);
            if (string.IsNullOrEmpty(pb)) continue;
            Dictionary<string, string> pairs;
            List<string> keys;
            if (pb.Contains('=')) { pairs = Wiki.Pairs(pb); keys = pairs.Keys.ToList(); }
            else { pairs = new Dictionary<string, string>(StringComparer.Ordinal); keys = Wiki.ListIds(pb); }
            foreach (var p in keys)
            {
                var txt = "";
                foreach (var pre in prefixes)
                {
                    if (Loc.TryGetValue(pre + p, out var v) && v.Length > 0) { txt = v; break; }
                }
                if (txt.Length > 0) eff.Add(txt);
                else if (ParameterNames.TryGetValue(p, out var pn)) eff.Add(Zh ? pn.Zh : pn.En);
                else if (pairs.TryGetValue(p, out var pv) && Regex.IsMatch(pv, @"^-?\d+(?:\.\d+)?$"))
                {
                    var nm = Mn(PartnerNames, p, Zh);
                    if (nm.Length == 0) nm = Mn(ModifierNames, p, Zh);
                    if (nm.Length == 0) nm = Loc.TryGetValue(p, out var lv) ? lv : "";
                    if (nm.Length > 0) eff.Add(nm + " " + pv);
                }
            }
        }
        // 革新解锁的法律 / 自定义效果
        foreach (Match m in Regex.Matches(body, "^\tunlock_law\\s*=\\s*([a-z_0-9]+)", RegexOptions.Multiline))
        {
            var nm = LocAny(m.Groups[1].Value);
            if (nm.Length > 0) eff.Add((Zh ? "解锁：" : "Unlocks: ") + nm);
        }
        foreach (Match m in Regex.Matches(body, "^\tcustom\\s*=\\s*([a-z_0-9]+)", RegexOptions.Multiline))
        {
            var c = m.Groups[1].Value;
            var t = LocAny(c);
            if (t.Length == 0) t = CustomName(c);
            if (t.Length > 0) eff.Add(t);
        }
        // maa_upgrade = { type = heavy_infantry  toughness = 10 } 这种块
        foreach (var ub in Wiki.SubAll(body, "maa_upgrade"))
        {
            var t = Wiki.Val(ub, "type");
            if (string.IsNullOrEmpty(t)) continue;
            var tn = Loc.TryGetValue(t, out var tv) && tv.Length > 0 ? tv
                : Loc.TryGetValue(t + "_attribute", out var ta) && ta.Length > 0 ? ta : t;
            var bits = new List<string>();
            foreach (var kv in Wiki.Pairs(ub))
            {
                if (kv.Key == "type") continue;
                var nm = StatNames.TryGetValue(kv.Key, out var sp) ? (Zh ? sp.Zh : sp.En)
                    : Loc.TryGetValue(kv.Key, out var lv) && lv.Length > 0 ? lv
                    : Mn(ModifierNames, kv.Key, Zh);
                if (nm.Length > 0) bits.Add(nm + " " + FmtNum(kv.Value));
            }
            if (bits.Count > 0)
                eff.Add(tn + (Zh ? "：" : ": ") + string.Join(Zh ? "、" : ", ", bits));
        }
        // 革新的其它解锁字段
        foreach (var (fld, pair) in new (string, (string Zh, string En))[]
                 {
                     ("unlock_maa", ("解锁兵士：", "Unlocks men-at-arms: ")),
                     ("unlock_building", ("解锁建筑：", "Unlocks building: ")),
                     ("unlock_casus_belli", ("解锁宣战理由：", "Unlocks casus belli: ")),
                     ("maa_upgrade", ("兵士升级：", "Men-at-arms upgrade: ")),
                 })
        {
            foreach (Match m in Regex.Matches(body, "^\t" + fld + "\\s*=\\s*([a-z_0-9]+)", RegexOptions.Multiline))
            {
                var v = m.Groups[1].Value;
                // 建筑解锁：优先「线名」（camel_farms_01 → 骆驼养殖场）
                var t = fld == "unlock_building"
                    ? (Loc.TryGetValue("building_type_" + v, out var bt) && bt.Length > 0 ? bt : LocAny(v))
                    : LocAny(v);
                if (t.Length > 0) eff.Add((Zh ? pair.Zh : pair.En) + t);
            }
        }
        foreach (var blk in new[]
                 {
                     "character_modifier", "personal_tenet_modifier", "culture_modifier",
                     "county_modifier", "province_modifier", "clergy_modifier",
                 })
        {
            if (excludeBlocks is not null && excludeBlocks.Contains(blk)) continue;
            var mb = Wiki.Sub(body, blk);
            if (string.IsNullOrEmpty(mb)) continue;
            foreach (var kv in Wiki.Pairs(mb))
            {
                var nm = Loc.TryGetValue(kv.Key, out var lv) && lv.Length > 0 ? lv : Mn(ModifierNames, kv.Key, Zh);
                if (nm.Length > 0) eff.Add(ModLine(kv.Key, kv.Value, nm));
            }
        }
        return Dedup(eff);
    }
}
