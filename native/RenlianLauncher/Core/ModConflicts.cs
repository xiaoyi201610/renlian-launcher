using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RenlianLauncher.Core;

/// <summary>
/// 冲突排查与「播放集 ↔ 启停文件」一致性校验。
/// 判定口径逐条对齐旧版 ck3core.detect_mod_conflicts / server.api_consistency，
/// 输出字段也保持一致（rel/severity/winner/losers/...），前端无需改动。
/// </summary>
public static class ModConflicts
{
    // 单行走本地化键：key 后必须紧跟数字(如 :0)或引号(如 :")，避免误抓语言头/值内冒号
    private static readonly Regex LocRe = new(@"^([^\s:]+)\s*:\s*(?:\d+|"")", RegexOptions.Compiled);

    // 高严重度路径前缀：整文件覆盖通常意味着某 mod 的功能被悄悄废掉
    private static readonly string[] HighImpact =
    {
        "common/defines", "common/landed_titles", "common/cultures",
        "common/religions", "common/scripted_effects", "common/scripted_triggers",
        "common/on_action", "common/decisions", "common/laws", "common/dynasties",
        "common/bookmarks", "common/province_terrain", "common/buildings",
        "common/casus_belli", "common/council_tasks", "common/governments",
        "common/hook_types", "common/interest_groups", "common/maa_types",
        "common/peace_treaties", "common/personal_deities", "common/religion_families",
        "common/religion_doctrines", "common/succession_laws", "common/title_names",
        "common/trade_routes", "common/vanished_counties", "common/war_tenets",
        "common/artifacts", "common/council_positions", "common/diplo_plans",
        "common/diplomatic_actions", "common/dynasty_houses", "common/great_holy_wars",
        "common/holy_order_names", "common/house_unify", "common/innovations",
        "common/maa_templates", "common/named_colors", "common/naming",
        "common/neutrals", "common/perks", "common/pools", "common/regnal_names",
        "common/scheme_types", "common/secrets", "common/society",
        "common/succession_laws", "common/trade_goods", "common/triggered_modifiers",
        "common/war_reasures", "common/age", "common/council_voting",
        "common/factions", "common/holy_sites", "common/province_wars",
        "common/statutes", "common/trade_policies", "common/war",
    };

    // 版本控制/缓存目录里的文件游戏根本不加载，算冲突纯属误报
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "__pycache__", ".idea", ".vscode", "node_modules",
    };

    // 每个 mod 自带的「身份证」文件：同名但不是真的抢文件
    private static readonly HashSet<string> SkipSelfFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".metadata/metadata.json", "descriptor.mod", "thumbnail.png",
    };

    private static readonly Dictionary<string, (DateTime At, JsonObject Result)> Cache = new();
    private static readonly object CacheLock = new();

    private static JsonObject Fail(string error) => new() { ["ok"] = false, ["error"] = error };

    /// <summary>
    /// 排查播放集里【已启用】mod 之间的冲突。
    /// progress 由调用方提供（流式接口用来逐行推送 scanned/total）。
    /// </summary>
    public static JsonObject Detect(ConfigStore cfg, string? psid, IReadOnlyList<JsonObject> index,
        Action<int, int>? progress)
    {
        var userDir = (cfg.GameStr(cfg.CurrentGame, "userDir") ?? "").Trim();
        if (userDir.Length == 0) return Fail("未配置用户数据目录");

        var db = PlaysetDb.ForConfig(cfg);
        var id = string.IsNullOrWhiteSpace(psid) ? db.ActivePlayset() : psid;
        if (string.IsNullOrWhiteSpace(id)) return Fail("找不到该播放集");
        var ps = db.GetPlayset(id!);
        if (ps is null) return Fail("找不到该播放集");

        var enabled = ps.Mods.Where(m => m.Enabled)
            .OrderBy(m => m.Position ?? 0)
            .ToList();
        var totalMods = enabled.Count;
        progress?.Invoke(0, totalMods);

        // 轻量缓存：启用集（modFile:position:dirPath.mtime）变化时失效，TTL 20s
        var sig = id + "|" + string.Join("|", enabled.Select(m =>
        {
            var dp = (m.DirPath ?? "").Trim();
            long mt = 0;
            try { if (dp.Length > 0 && Directory.Exists(dp)) mt = Directory.GetLastWriteTimeUtc(dp).Ticks; }
            catch { }
            return $"{m.ModId}:{m.Position}:{mt}";
        }));
        lock (CacheLock)
        {
            if (Cache.TryGetValue(sig, out var cached) &&
                (DateTime.UtcNow - cached.At).TotalSeconds < 20)
                return cached.Result;
        }

        var fileMap = new Dictionary<string, List<(string Name, long Pos, string ModId)>>(StringComparer.Ordinal);
        var keyMap = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var totalFiles = 0;

        for (var i = 0; i < enabled.Count; i++)
        {
            var m = enabled[i];
            var dp = (m.DirPath ?? "").Trim();
            if (dp.Length == 0 || !Directory.Exists(dp))
            {
                progress?.Invoke(i + 1, totalMods);
                continue;
            }

            var nm = MName(m);
            var pos = m.Position ?? 0;
            var mid = m.ModId;
            try
            {
                var root = Path.GetFullPath(dp);
                var stack = new Stack<string>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    var dir = stack.Pop();
                    IEnumerable<string> files;
                    try { files = Directory.EnumerateFiles(dir); } catch { continue; }

                    foreach (var full in files)
                    {
                        var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
                        if (SkipSelfFiles.Contains(rel)) continue;

                        if (!fileMap.TryGetValue(rel, out var owners))
                            fileMap[rel] = owners = new List<(string, long, string)>();
                        owners.Add((nm, pos, mid));
                        totalFiles++;

                        var ext = Path.GetExtension(rel);
                        if (ext is ".yml" or ".yaml")
                        {
                            string text;
                            try { text = ReadText(full); } catch { continue; }
                            foreach (var raw in text.Split('\n'))
                            {
                                var hit = LocRe.Match(raw.Trim());
                                if (!hit.Success) continue;
                                var key = hit.Groups[1].Value;
                                if (!keyMap.TryGetValue(key, out var set))
                                    keyMap[key] = set = new HashSet<string>(StringComparer.Ordinal);
                                set.Add(nm);
                            }
                        }
                    }

                    IEnumerable<string> subs;
                    try { subs = Directory.EnumerateDirectories(dir); } catch { continue; }
                    foreach (var sub in subs)
                        if (!SkipDirs.Contains(Path.GetFileName(sub))) stack.Push(sub);
                }
            }
            catch { /* 单个 mod 目录读不了不影响其它 mod */ }

            progress?.Invoke(i + 1, totalMods);
        }

        var fileConflicts = new List<JsonObject>();
        foreach (var (rel, owners) in fileMap)
        {
            if (owners.Count <= 1) continue;
            var sorted = owners.OrderBy(o => o.Pos).ToList();
            var winner = sorted[^1];
            var losers = sorted.Take(sorted.Count - 1).ToList();
            fileConflicts.Add(new JsonObject
            {
                ["rel"] = rel,
                ["severity"] = Severity(rel),
                ["winner"] = winner.Name,
                ["winnerModId"] = winner.ModId,
                ["losers"] = new JsonArray(losers.Select(o => (JsonNode?)JsonValue.Create(o.Name)).ToArray()),
                ["loserModIds"] = new JsonArray(losers.Select(o => (JsonNode?)JsonValue.Create(o.ModId)).ToArray()),
            });
        }

        // 按严重度（高>中>低）再按路径排序，前端直接顺序渲染
        var orderedFiles = fileConflicts
            .OrderBy(o => SeverityRank(ConfigStore.Str(o, "severity") ?? "中"))
            .ThenBy(o => ConfigStore.Str(o, "rel") ?? "", StringComparer.Ordinal)
            .ToList();

        var locConflicts = new List<JsonObject>();
        foreach (var (key, names) in keyMap)
        {
            if (names.Count <= 1) continue;
            locConflicts.Add(new JsonObject
            {
                ["key"] = key,
                ["mods"] = new JsonArray(names.OrderBy(x => x, StringComparer.Ordinal)
                    .Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            });
        }
        var orderedLoc = locConflicts
            .OrderBy(o => ConfigStore.Str(o, "key") ?? "", StringComparer.Ordinal)
            .ToList();

        // 描述符级别的依赖 / replace_path 检查
        var modFileMap = db.ModIdToFileMap(index);
        var missingDeps = new JsonArray();
        var replacePaths = new JsonArray();
        var enabledNames = enabled
            .Select(x => ((x.DisplayName ?? x.Name) ?? "").Trim().ToLowerInvariant())
            .ToHashSet();
        var enabledSteam = enabled
            .Select(x => (x.SteamId ?? "").Trim().ToLowerInvariant())
            .ToHashSet();

        foreach (var m in enabled)
        {
            if (!modFileMap.TryGetValue(m.ModId, out var mf) || string.IsNullOrWhiteSpace(mf)) continue;
            var dpath = Path.Combine(userDir, mf.Replace('/', '\\'));
            if (!File.Exists(dpath)) continue;

            var desc = Pdx.ParseFile(dpath);
            var deps = AsStringList(desc, "dependencies");
            if (deps.Count > 0)
            {
                var missing = deps
                    .Where(d => !enabledNames.Contains(d.Trim().ToLowerInvariant()) &&
                                !enabledSteam.Contains(d.Trim().ToLowerInvariant()))
                    .ToList();
                if (missing.Count > 0)
                {
                    missingDeps.Add(new JsonObject
                    {
                        ["mod"] = MName(m),
                        ["deps"] = new JsonArray(missing.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()),
                    });
                }
            }

            var rp = AsStringList(desc, "replace_path");
            if (rp.Count > 0)
            {
                replacePaths.Add(new JsonObject
                {
                    ["mod"] = MName(m),
                    ["paths"] = new JsonArray(rp.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                });
            }
        }

        var result = new JsonObject
        {
            ["ok"] = true,
            ["playset"] = new JsonObject { ["id"] = ps.Id, ["name"] = ps.Name },
            ["scannedMods"] = enabled.Count,
            ["totalFiles"] = totalFiles,
            ["fileConflicts"] = new JsonArray(orderedFiles.Select(n => (JsonNode?)n).ToArray()),
            ["locConflicts"] = new JsonArray(orderedLoc.Select(n => (JsonNode?)n).ToArray()),
            ["missingDeps"] = missingDeps,
            ["replacePaths"] = replacePaths,
        };

        lock (CacheLock)
        {
            Cache[sig] = (DateTime.UtcNow, result);
            if (Cache.Count > 30) Cache.Clear();   // 别让缓存无限增长
        }
        return result;
    }

    private static string MName(PlaysetModRow m)
    {
        var s = ((m.DisplayName ?? m.Name) ?? "").Trim();
        return s.Length > 0 ? s : (string.IsNullOrWhiteSpace(m.ModId) ? "未命名" : m.ModId);
    }

    private static string Severity(string rel)
    {
        if (HighImpact.Any(p => rel.StartsWith(p, StringComparison.Ordinal))) return "高";
        var ext = Path.GetExtension(rel);
        return ext is ".yml" or ".yaml" or ".png" or ".dds" or ".jpg" or ".jpeg" or ".webp" or ".tga"
            ? "低" : "中";
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "高" => 0,
        "中" => 1,
        _ => 2,
    };

    private static List<string> AsStringList(Dictionary<string, object> desc, string key)
    {
        if (!desc.TryGetValue(key, out var v)) return new List<string>();
        if (v is List<string> l) return l.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (v is string s && s.Trim().Length > 0) return new List<string> { s.Trim() };
        return new List<string>();
    }

    /// <summary>按 UTF-8 读文本，非法字节直接忽略（等价旧版 errors="ignore"）。</summary>
    private static string ReadText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, new UTF8Encoding(false), true, 4096);
        return sr.ReadToEnd();
    }

    // ------------------------------------------------------------ 一致性校验

    /// <summary>对比「当前播放集启用列表」与「游戏实际读取的启停文件」。</summary>
    public static JsonObject Consistency(ConfigStore cfg, IReadOnlyList<JsonObject> index)
    {
        var db = PlaysetDb.ForConfig(cfg);
        var psid = db.ActivePlayset();
        if (string.IsNullOrWhiteSpace(psid))
            return new JsonObject
            {
                ["ok"] = true, ["psid"] = null,
                ["extra"] = new JsonArray(), ["missing"] = new JsonArray(),
                ["orderDiff"] = false,
            };

        var ps = db.GetPlayset(psid!);
        if (ps is null)
            return new JsonObject
            {
                ["ok"] = true, ["psid"] = null,
                ["extra"] = new JsonArray(), ["missing"] = new JsonArray(),
                ["orderDiff"] = false,
            };

        if (GameDefs.ContentLoad.Contains(cfg.CurrentGame))
            return ConsistencyContentLoad(db, ps, psid!);

        // ---- dlc_load.json：存的是 "mod/xxx.mod" 相对路径
        var id2f = db.ModIdToFileMap(index);
        var want = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in ps.Mods)
        {
            if (!m.Enabled) continue;
            if (id2f.TryGetValue(m.ModId, out var f) && !string.IsNullOrWhiteSpace(f) && seen.Add(f))
                want.Add(f);
        }

        var have = new List<string>();
        var dlcJson = ReadJson(Path.Combine(db.UserDir, "dlc_load.json"));
        if (dlcJson?["enabled_mods"] is JsonArray arr)
            foreach (var n in arr)
                if (n?.ToString() is { Length: > 0 } s) have.Add(s);

        var wantSet = new HashSet<string>(want, StringComparer.Ordinal);
        var haveSet = new HashSet<string>(have, StringComparer.Ordinal);
        var byFile = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in index)
        {
            var mf = ConfigStore.Str(it, "modFile");
            if (!string.IsNullOrWhiteSpace(mf)) byFile[mf!] = it;
        }

        JsonObject Label(string f)
        {
            var sid = Path.GetFileName(f).Replace("ugc_", "").Replace(".mod", "");
            byFile.TryGetValue(f ?? "", out var meta);
            return new JsonObject
            {
                ["file"] = f,
                ["name"] = ConfigStore.Str(meta, "name") is { Length: > 0 } nm ? nm : sid,
                ["steamId"] = sid,
            };
        }

        var extra = new JsonArray(have.Where(x => !wantSet.Contains(x))
            .Select(x => (JsonNode?)Label(x)).ToArray());
        var missing = new JsonArray(want.Where(x => !haveSet.Contains(x))
            .Select(x => (JsonNode?)Label(x)).ToArray());
        var orderDiff = extra.Count == 0 && missing.Count == 0 &&
                        !want.SequenceEqual(have, StringComparer.Ordinal);

        return new JsonObject
        {
            ["ok"] = true,
            ["psid"] = psid,
            ["playsetName"] = ps.Name,
            ["extra"] = extra,          // 游戏会加载，但播放集里是禁用的
            ["missing"] = missing,      // 播放集启用了，但游戏不会加载
            ["orderDiff"] = orderDiff,  // 集合一致但顺序不同
            ["wantCount"] = want.Count,
            ["haveCount"] = have.Count,
        };
    }

    private static JsonObject ConsistencyContentLoad(PlaysetDb db, PlaysetDetail ps, string psid)
    {
        // Vic3/EU5 的 content_load.json 存的是 mod 绝对目录，不是 mod/xxx.mod
        var recs = new Dictionary<string, ModRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in db.ModRecords())
        {
            var dp = (r.DirPath ?? "").Trim();
            if (dp.Length == 0) continue;
            try { recs[NormCase(dp)] = r; } catch { }
        }

        var want = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in ps.Mods)
        {
            if (!m.Enabled) continue;
            foreach (var cand in new[] { (m.DirPath ?? "").Trim(), (m.ArchivePath ?? "").Trim() })
            {
                if (cand.Length == 0) continue;
                string p;
                try { p = Path.GetFullPath(cand); } catch { continue; }
                if (!seen.Add(NormCase(p))) break;   // 若已去重则直接进下一个 mod
                want.Add(p);
                break;
            }
        }

        var have = new List<string>();
        var cl = ReadJson(Path.Combine(db.UserDir, "content_load.json"));
        if (cl?["enabledMods"] is JsonArray arr)
            foreach (var n in arr)
                if (n is JsonObject o && ConfigStore.Str(o, "path") is { Length: > 0 } p) have.Add(p);

        var wantNorm = new HashSet<string>(want.Select(NormCase), StringComparer.OrdinalIgnoreCase);
        var haveNorm = new HashSet<string>(have.Select(NormCase), StringComparer.OrdinalIgnoreCase);

        JsonObject VLabel(string p)
        {
            recs.TryGetValue(NormCase(p), out var rec);
            var name = (rec?.DisplayName is { Length: > 0 } dn ? dn : rec?.Name) is { Length: > 0 } nm
                ? nm
                : Path.GetFileName(p.TrimEnd('\\', '/'));
            return new JsonObject
            {
                ["file"] = p,
                ["name"] = name,
                ["steamId"] = (rec?.SteamId ?? "").Trim(),
            };
        }

        var extra = new JsonArray(have.Where(x => !wantNorm.Contains(NormCase(x)))
            .Select(x => (JsonNode?)VLabel(x)).ToArray());
        var missing = new JsonArray(want.Where(x => !haveNorm.Contains(NormCase(x)))
            .Select(x => (JsonNode?)VLabel(x)).ToArray());
        var orderDiff = extra.Count == 0 && missing.Count == 0 &&
                        !want.Select(NormCase).SequenceEqual(have.Select(NormCase),
                            StringComparer.OrdinalIgnoreCase);

        return new JsonObject
        {
            ["ok"] = true,
            ["psid"] = psid,
            ["playsetName"] = ps.Name,
            ["mode"] = "content_load",
            ["extra"] = extra,
            ["missing"] = missing,
            ["orderDiff"] = orderDiff,
            ["wantCount"] = want.Count,
            ["haveCount"] = have.Count,
        };
    }

    private static string NormCase(string p)
    {
        try { return Path.GetFullPath(p).ToLowerInvariant(); }
        catch { return p.ToLowerInvariant(); }
    }

    private static JsonObject? ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
        }
        catch { return null; }
    }
}
