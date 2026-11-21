using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RenlianLauncher.Core;

/// <summary>
/// mod 索引。旧版每次启动都全量扫描（本机 702 个 mod 要 9 秒），
/// 这里改为：
///   · 并行读取 .mod 描述文件
///   · 扫描结果按「目录指纹」落盘缓存，指纹没变就直接秒开
///   · 只看 mod 目录里 .mod 文件的 名字/大小/修改时间，不做全目录 walk
/// </summary>
public static class ModIndex
{
    private sealed class CacheFile
    {
        public string Sig { get; set; } = "";
        public List<JsonObject> Items { get; set; } = new();
    }

    public static List<JsonObject> Build(ConfigStore cfg, bool force = false)
    {
        var game = cfg.CurrentGame;
        var def = GameDefs.Get(game);
        var userDir = cfg.GameStr(game, "userDir") ?? "";
        var workshop = cfg.GameStr(game, "workshopDir") ?? "";
        var gameVersion = cfg.GameStr(game, "gameVersion") is { Length: > 0 } gv ? gv : def.DefaultVersion;

        var modDir = Path.Combine(userDir, "mod");
        var hasModDir = Directory.Exists(modDir);

        var sig = Signature(modDir, workshop, gameVersion);
        var cachePath = Path.Combine(Paths.ModIndexCacheDir, $"mod-index-{game}.json");
        if (!force)
        {
            var cached = LoadCache(cachePath, sig);
            if (cached is not null) return cached;
        }

        var items = new List<JsonObject>();
        if (hasModDir)
        {
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(modDir, "*.mod")
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch { files = new List<string>(); }

            var parsed = new JsonObject[files.Count];
            Parallel.For(0, files.Count, i =>
            {
                parsed[i] = BuildItem(files[i], modDir, workshop, gameVersion, game);
            });
            items.AddRange(parsed.Where(x => x is not null));
        }

        // 工坊里已订阅、但还没生成 .mod 的
        if (!string.IsNullOrWhiteSpace(workshop) && Directory.Exists(workshop))
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                var sid = it["steamId"]?.ToString();
                if (!string.IsNullOrEmpty(sid)) known.Add(sid);
                var osid = it["originSteamId"]?.ToString();
                if (!string.IsNullOrEmpty(osid)) known.Add(osid);
            }
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(workshop))
                {
                    var id = Path.GetFileName(dir);
                    if (known.Contains(id)) continue;
                    items.Add(BuildPendingItem(dir, id));
                }
            }
            catch { }
        }

        SaveCache(cachePath, sig, items);
        return items;
    }

    private static JsonObject? BuildItem(string modPath, string modDir, string workshop,
        string gameVersion, string game)
    {
        try
        {
            var desc = Pdx.ParseFile(modPath);
            var fileName = Path.GetFileName(modPath);
            var name = (Pdx.Str(desc, "name") ?? "").Trim();
            var pathAttr = (Pdx.Str(desc, "path") ?? "").Replace('/', '\\').Trim();
            var remoteId = (Pdx.Str(desc, "remote_file_id") ?? "").Trim();
            var stem = Path.GetFileNameWithoutExtension(fileName);
            if (remoteId.Length == 0 && stem.StartsWith("ugc_", StringComparison.OrdinalIgnoreCase))
                remoteId = stem[4..];

            var contentDir = pathAttr;
            if (contentDir.Length > 0 && !Path.IsPathRooted(contentDir))
                contentDir = Path.Combine(modDir, "..", contentDir);

            var inWorkshop = false;
            if (contentDir.Length > 0 && workshop.Length > 0 && Directory.Exists(contentDir))
            {
                try
                {
                    inWorkshop = Path.GetFullPath(contentDir)
                        .StartsWith(Path.GetFullPath(workshop), StringComparison.OrdinalIgnoreCase);
                }
                catch { }
            }

            var exists = contentDir.Length > 0 && Directory.Exists(contentDir);
            var source = inWorkshop ? "steam" : exists ? "local" : (remoteId.Length > 0 ? "steam" : "local");

            var thumb = "";
            if (contentDir.Length > 0)
            {
                foreach (var fn in new[] { "thumbnail.png", "thumbnail.jpg", "picture.png" })
                {
                    var p = Path.Combine(contentDir, fn);
                    if (File.Exists(p)) { thumb = p; break; }
                }
            }

            var supported = Pdx.Str(desc, "supported_version") ?? "";
            var tags = new JsonArray(Pdx.List(desc, "tags")
                .Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());

            var item = new JsonObject
            {
                ["key"] = ("mod/" + fileName).ToLowerInvariant(),
                ["modFile"] = "mod/" + fileName,
                ["modId"] = null,
                ["steamId"] = inWorkshop && remoteId.Length > 0 ? remoteId : null,
                ["originSteamId"] = !inWorkshop && remoteId.Length > 0 ? remoteId : null,
                ["name"] = name.Length > 0 ? name : stem,
                ["originalName"] = name.Length > 0 ? name : stem,
                ["aliased"] = false,
                ["version"] = Pdx.Str(desc, "version") ?? "",
                ["supportedVersion"] = supported,
                ["tags"] = tags,
                ["source"] = source,
                ["sourceOverridden"] = false,
                ["collections"] = new JsonArray(),
                ["dirPath"] = exists ? Path.GetFullPath(contentDir) : "",
                ["thumb"] = thumb,
                ["valid"] = exists,
                ["compat"] = exists ? Compat(supported, gameVersion) : "bad",
                ["size"] = 0,
                ["descriptor"] = fileName,
            };
            return item;
        }
        catch { return null; }
    }

    private static JsonObject BuildPendingItem(string dir, string id)
    {
        string nm = id, supported = "", version = "";
        var descPath = Path.Combine(dir, "descriptor.mod");
        if (File.Exists(descPath))
        {
            var d = Pdx.ParseFile(descPath);
            nm = (Pdx.Str(d, "name") ?? id).Trim();
            supported = Pdx.Str(d, "supported_version") ?? "";
            version = Pdx.Str(d, "version") ?? "";
        }
        else
        {
            var mj = Path.Combine(dir, ".metadata", "metadata.json");
            if (File.Exists(mj))
            {
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(mj)) as JsonObject;
                    nm = ConfigStore.Str(node, "name") ?? id;
                    supported = ConfigStore.Str(node, "supported_game_version") ?? "";
                    version = ConfigStore.Str(node, "version") ?? "";
                }
                catch { }
            }
        }

        var thumb = "";
        foreach (var fn in new[] { "thumbnail.png", "thumbnail.jpg" })
        {
            var p = Path.Combine(dir, fn);
            if (File.Exists(p)) { thumb = p; break; }
        }

        return new JsonObject
        {
            ["key"] = ("mod/ugc_" + id + ".mod").ToLowerInvariant(),
            ["modFile"] = "mod/ugc_" + id + ".mod",
            ["modId"] = null,
            ["steamId"] = id,
            ["name"] = nm,
            ["version"] = version,
            ["supportedVersion"] = supported,
            ["tags"] = new JsonArray(),
            ["source"] = "steam",
            ["dirPath"] = dir,
            ["thumb"] = thumb,
            ["valid"] = false,
            ["pending"] = true,
            ["compat"] = "warn",
            ["size"] = 0,
            ["descriptor"] = "",
        };
    }

    // ------------------------------------------------------------ 版本兼容

    private static int[] VKey(string v) =>
        Regex.Matches(v ?? "", @"\d+").Select(m => int.TryParse(m.Value, out var n) ? n : 0)
            .Take(4).DefaultIfEmpty(0).ToArray();

    public static string Compat(string supported, string gameVersion)
    {
        if (string.IsNullOrWhiteSpace(supported) || supported.Trim() is "" or "*") return "ok";
        var sup = supported.Replace("x", "*").Trim();
        var gv = string.IsNullOrWhiteSpace(gameVersion) ? "1.20" : gameVersion.Trim();
        if (sup.EndsWith(".*"))
        {
            var baseV = VKey(sup[..^2]);
            var gvv = VKey(gv);
            for (var i = 0; i < baseV.Length; i++)
                if (i >= gvv.Length || gvv[i] != baseV[i]) return "warn";
            return "ok";
        }
        var sv = VKey(sup);
        var gv2 = VKey(gv);
        if (sv.SequenceEqual(gv2)) return "ok";
        if (sv.Length >= 2 && gv2.Length >= 2 && sv[0] == gv2[0] && sv[1] == gv2[1]) return "ok";
        return "warn";
    }

    // ------------------------------------------------------------ 指纹与缓存

    private static string Signature(string modDir, string workshop, string gameVersion)
    {
        var sb = new StringBuilder();
        sb.Append(gameVersion).Append('|').Append(workshop).Append('|');
        try
        {
            if (Directory.Exists(modDir))
            {
                foreach (var f in Directory.EnumerateFiles(modDir, "*.mod")
                             .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var fi = new FileInfo(f);
                    sb.Append(fi.Name).Append(',').Append(fi.Length).Append(',')
                      .Append(fi.LastWriteTimeUtc.Ticks).Append(';');
                }
            }
            if (Directory.Exists(workshop))
            {
                foreach (var d in Directory.EnumerateDirectories(workshop)
                             .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    sb.Append(Path.GetFileName(d)).Append(';');
            }
        }
        catch { }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }

    private static List<JsonObject>? LoadCache(string path, string sig)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (node is null) return null;
            if (ConfigStore.Str(node, "sig") != sig) return null;
            if (node["items"] is not JsonArray arr) return null;
            return arr.OfType<JsonObject>().ToList();
        }
        catch { return null; }
    }

    private static void SaveCache(string path, string sig, List<JsonObject> items)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var obj = new JsonObject
            {
                ["sig"] = sig,
                ["savedAt"] = DateTime.Now.ToString("s"),
                ["items"] = new JsonArray(items.Select(i => (JsonNode)i.DeepClone()).ToArray()),
            };
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, obj.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            File.Move(tmp, path, true);
        }
        catch { }
    }
}
