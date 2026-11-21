using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace RenlianLauncher.Core;

/// <summary>
/// Steam 创意工坊浏览数据。
/// 首选路径：Steam 工坊浏览页内嵌 JSON（React Query 序列化，需一层反转义）——
/// 一次请求即可拿到 30 个 MOD 的标题/简介/封面/大小/订阅数/更新时间，速度最快。
/// 兜底路径：正则提 ID + 官方 GetPublishedFileDetails 批量补全。
/// 详情：/detail 用 GetPublishedFileDetails 单查，含完整 BBCode 介绍（清洗后返回），按 ID 缓存 30 分钟。
/// </summary>
public static class WorkshopBrowse
{
    /// <summary>按游戏取 appid：必须是 GameDefs 里登记过的数字才放行，否则回落 CK3。</summary>
    private static string NormAppId(string? appId)
    {
        var a = (appId ?? "").Trim();
        if (a.Length > 0 && a.All(char.IsDigit) && GameDefs.All.Values.Any(g => g.AppId == a))
            return a;
        return "1158310"; // 默认 CK3
    }

    /// <summary>工坊磁盘缓存目录（进程重启后依然秒开，不用等网络）。</summary>
    private static readonly string DiskCacheDir = Path.Combine(Paths.CacheDir, "workshop");

    /// <summary>
    /// Steam 社区必须走代理：跟随系统代理设置（Steam++/Watt Toolkit 的 hosts 反代、系统代理、TUN 均自动生效）。
    /// 不再尝试裸直连——国内直连 Steam 社区不可用，旧的双通道竞速会白等直连通道。
    /// </summary>
    private static readonly HttpClient Http = new(new HttpClientHandler
    { AutomaticDecompression = System.Net.DecompressionMethods.All })
    { Timeout = TimeSpan.FromSeconds(12) };

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, (DateTime At, JsonObject Data)> Cache = new();
    private static readonly ConcurrentDictionary<string, byte> InFlight = new();
    /// <summary>按 ID 的详情缓存（浏览 / 详情共用，命中即免网络请求）。</summary>
    private static readonly ConcurrentDictionary<string, (DateTime At, JsonObject Item)> DetailCache = new();
    /// <summary>filedetails 页连续限流背压：短时间内失败后暂停拉页，避免每次详情都白等超时。</summary>
    private static int _pageFailStreak;
    private static DateTime _pageBackoffUntil = DateTime.MinValue;

    private static void Ua(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/122 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
    }

    /// <summary>单通道 GET；失败/异常返回 null。</summary>
    private static async Task<string?> TryGetAsync(string url, string? cookie, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            Ua(req);
            if (!string.IsNullOrEmpty(cookie)) req.Headers.TryAddWithoutValidation("Cookie", cookie);
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch { return null; }
    }

    /// <summary>单通道 POST；失败/异常返回 null。</summary>
    private static async Task<string?> TryPostAsync(string url, string form, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            Ua(req);
            req.Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch { return null; }
    }

    private static Task<string?> GetAsync(string url) => GetAsync(url, null);

    private static Task<string?> GetAsync(string url, string? cookie)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        return TryGetAsync(url, cookie, cts.Token);
    }

    private static Task<string?> PostAsync(string url, string form)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        return TryPostAsync(url, form, cts.Token);
    }

    /// <summary>解一层 JS/JSON 字符串转义（保护 \\，解 \" 与 \u0026、\/，再还原 \\）。</summary>
    private static string UnescapeOnce(string s) =>
        s.Replace("\\\\", "\u0000").Replace("\\\"", "\"")
         .Replace("\\u0026", "&").Replace("\\/", "/")
         .Replace("\u0000", "\\");

    /// <summary>Browse 参数：sort=trend/mostrecent/mostsubscribed/totaluniquesubscribers；category=分类 tag；search=关键词；page=页码；appId=当前游戏的 Steam AppId。</summary>
    public static async Task<object> Browse(string sort, string category, string search, int page, string? appId = null)
    {
        if (string.IsNullOrWhiteSpace(sort)) sort = "trend";
        if (page < 1) page = 1;
        var aid = NormAppId(appId);

        var cacheKey = $"browse|{aid}|{sort}|{category ?? ""}|{search ?? ""}|{page}";
        var fkey = DiskKey("browse_" + cacheKey);

        // ① 内存缓存 10 分钟内：直接返回（微秒级）
        lock (CacheLock)
        {
            if (Cache.TryGetValue(cacheKey, out var hit) && (DateTime.UtcNow - hit.At).TotalMinutes < 10)
                return hit.Data;
        }
        // ② 磁盘缓存 10 分钟内：载入内存后返回——进程重启后依然秒开，不用等网络
        var fresh = LoadDisk(fkey, TimeSpan.FromMinutes(10));
        if (fresh != null)
        {
            lock (CacheLock) Cache[cacheKey] = (DateTime.UtcNow, fresh);
            return fresh;
        }
        // ③ 有旧数据（24h 内）：先返回旧数据（stale 标记）立即渲染，同时后台刷新——
        //    stale-while-revalidate：用户几乎永远"秒开"，真数据在几秒后补上。
        //    刷新已在跑时（TryAdd 失败）也返回 stale，绝不并行重复抓取。
        var stale = LoadDisk(fkey, TimeSpan.FromHours(24));
        if (stale != null)
        {
            if (InFlight.TryAdd(cacheKey, 0))
            {
                var nSort = sort; var nCat = category; var nSearch = search; var nPage = page; var nAid = aid;
                _ = Task.Run(async () =>
                {
                    try { await FetchBrowse(nSort, nCat, nSearch, nPage, cacheKey, fkey, nAid); }
                    catch { }
                    finally { InFlight.TryRemove(cacheKey, out _); }
                });
            }
            var sd = (JsonObject)stale.DeepClone();
            sd["stale"] = true;
            return sd;
        }
        // ④ 无任何缓存：同步抓取
        return await FetchBrowse(sort, category, search, page, cacheKey, fkey, aid);
    }

    /// <summary>抓取一页工坊数据并写内存 + 磁盘缓存；成功后预取下一页。</summary>
    private static async Task<object> FetchBrowse(string sort, string category, string search, int page, string cacheKey, string fkey, string appId)
    {
        // 1) 拼 Steam 工坊浏览页 URL
        var qs = new List<string>
        {
            "appid=" + appId,
            "browsesort=" + Uri.EscapeDataString(sort),
            "section=readytouseitems",
            "p=" + page,
            "l=schinese",
        };
        // 分类并入搜索词：Steam 新版工坊的 category1 需要数字 tag id，
        // 直接用 tag 名做全文搜索更稳妥，点分类一定有结果
        var finalSearch = string.IsNullOrWhiteSpace(search) ? (category ?? "")
                        : string.IsNullOrWhiteSpace(category) ? search
                        : search + " " + category;
        if (!string.IsNullOrWhiteSpace(finalSearch)) qs.Add("searchtext=" + Uri.EscapeDataString(finalSearch));
        var browseUrl = "https://steamcommunity.com/workshop/browse/?" + string.Join("&", qs);

        var html = await GetAsync(browseUrl);
        if (string.IsNullOrEmpty(html))
            return Err("无法访问 Steam 工坊（网络或代理问题）");

        int totalCount = ParseInt(html, "total_count");
        int totalPages = ParseInt(html, "total_pages");

        // 2) 首选：解一层转义后直接解析内嵌 JSON（一次请求拿全字段，比两步法快一半）
        var details = TryParseEmbedded(html);
        if (details.Count == 0)
        {
            // 兜底：正则提 ID → 批量调 GetPublishedFileDetails
            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(html, @"publishedfileid\\?""\s*:\s*\\?""(\d+)\\?"""))
            {
                var id = m.Groups[1].Value;
                if (seen.Add(id)) ids.Add(id);
            }
            if (ids.Count == 0)
                foreach (Match m in Regex.Matches(html, @"filedetails/\?id=(\d+)"))
                {
                    var id = m.Groups[1].Value;
                    if (seen.Add(id)) ids.Add(id);
                }
            if (ids.Count == 0)
                return Err("Steam 工坊页未解析到 MOD 列表");
            details = await FetchDetails(ids);
        }

        var result = new JsonObject
        {
            ["ok"] = true,
            ["items"] = new JsonArray(details.Select(d => (JsonNode)d).ToArray()),
            ["page"] = page,
            ["totalPages"] = totalPages,
            ["totalCount"] = totalCount,
            ["sort"] = sort,
            ["category"] = category ?? "",
            ["search"] = search ?? "",
        };
        var snapshot = (JsonObject)result.DeepClone();
        lock (CacheLock) Cache[cacheKey] = (DateTime.UtcNow, snapshot);
        SaveDisk(fkey, snapshot);

        // 3) 后台预取下一页（不阻塞当前响应；重复请求被 InFlight 挡住）
        if (page < Math.Min(totalPages, 200))
        {
            var nextKey = $"browse|{appId}|{sort}|{category ?? ""}|{search ?? ""}|{page + 1}";
            bool cached;
            lock (CacheLock) cached = Cache.ContainsKey(nextKey);
            if (!cached && InFlight.TryAdd(nextKey, 0))
            {
                var nSort = sort; var nCat = category; var nSearch = search; var nPage = page + 1; var nAid = appId;
                _ = Task.Run(async () =>
                {
                    try { await Browse(nSort, nCat, nSearch, nPage, nAid); }
                    catch { }
                    finally { InFlight.TryRemove(nextKey, out _); }
                });
            }
        }
        return result;
    }

    /* —— 磁盘缓存：进程重启后 browse/detail 依然秒开（stale-while-revalidate 数据源）—— */

    private static string DiskKey(string key)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(h)[..32].ToLowerInvariant() + ".json";
    }

    private static void SaveDisk(string fkey, JsonObject data)
    {
        try
        {
            Directory.CreateDirectory(DiskCacheDir);
            var doc = new JsonObject
            {
                ["at"] = DateTime.UtcNow.Ticks,
                ["data"] = (JsonNode)data.DeepClone(),
            };
            var tmp = Path.Combine(DiskCacheDir, fkey + ".tmp");
            File.WriteAllText(tmp, doc.ToJsonString());
            File.Move(tmp, Path.Combine(DiskCacheDir, fkey), overwrite: true);
        }
        catch { }
    }

    /// <summary>读磁盘缓存；文件不存在或比 maxAge 旧则返回 null。</summary>
    private static JsonObject? LoadDisk(string fkey, TimeSpan maxAge)
    {
        try
        {
            var path = Path.Combine(DiskCacheDir, fkey);
            if (!File.Exists(path)) return null;
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node?["at"] is null || node["data"] is not JsonObject d) return null;
            var at = new DateTime(node["at"]!.GetValue<long>(), DateTimeKind.Utc);
            if (DateTime.UtcNow - at > maxAge) return null;
            return d;
        }
        catch { return null; }
    }

    /// <summary>解析浏览页内嵌 JSON（React Query 序列化，一层转义）。失败返回空列表（走兜底）。</summary>
    private static List<JsonObject> TryParseEmbedded(string html)
    {
        try
        {
            var u = UnescapeOnce(html);
            var start = u.IndexOf("\"results\":[", StringComparison.Ordinal);
            if (start < 0) return new();
            var i = start + "\"results\":".Length;
            var bytes = Encoding.UTF8.GetBytes(u.Substring(i));
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            { CommentHandling = JsonCommentHandling.Skip });
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return new();

            var list = new List<JsonObject>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var o = NormFromElement(e);
                if (o != null) list.Add(o);
            }
            return list;
        }
        catch { return new(); }
    }

    private static string? Sref(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
    }

    private static long Lref(JsonElement e, string name)
    {
        var s = Sref(e, name);
        return long.TryParse(s, out var v) ? v : 0;
    }

    /// <summary>把浏览页内嵌 JSON 的一个条目压成前端结构。</summary>
    private static JsonObject? NormFromElement(JsonElement e)
    {
        try
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            var id = Sref(e, "publishedfileid");
            if (string.IsNullOrEmpty(id)) return null;

            var tags = new JsonArray();
            if (e.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
                foreach (var tag in t.EnumerateArray())
                {
                    string? name = tag.ValueKind == JsonValueKind.String
                        ? tag.GetString()
                        : (Sref(tag, "display_name") ?? Sref(tag, "tag"));
                    if (!string.IsNullOrEmpty(name)) tags.Add(name);
                }

            return new JsonObject
            {
                ["id"] = id,
                ["title"] = Sref(e, "title") ?? id,
                ["desc"] = Sref(e, "short_description") ?? "",
                ["thumb"] = Sref(e, "preview_url") ?? "",
                ["size"] = Lref(e, "file_size"),
                ["updated"] = Lref(e, "time_updated"),
                ["created"] = Lref(e, "time_created"),
                ["subs"] = Lref(e, "subscriptions"),
                ["fav"] = Lref(e, "favorited"),
                ["views"] = Lref(e, "views"),
                ["comments"] = Lref(e, "num_comments_public"),
                ["rating"] = Lref(e, "star_rating"),
                ["ratingsCount"] = Lref(e, "total_votes"),
                ["tags"] = tags,
            };
        }
        catch { return null; }
    }

    /// <summary>批量调官方 GetPublishedFileDetails；命中 DetailCache 的 ID 直接复用。</summary>
    private static async Task<List<JsonObject>> FetchDetails(List<string> ids)
    {
        var details = new List<JsonObject>();
        var need = new List<string>();
        foreach (var id in ids)
        {
            if (DetailCache.TryGetValue(id, out var hit) && (DateTime.UtcNow - hit.At).TotalMinutes < 30)
                details.Add((JsonObject)hit.Item.DeepClone());
            else need.Add(id);
        }
        for (int i = 0; i < need.Count; i += 50)
        {
            var batch = need.Skip(i).Take(50).ToList();
            var sb = new StringBuilder();
            sb.Append("itemcount=").Append(batch.Count);
            for (int k = 0; k < batch.Count; k++)
                sb.Append("&publishedfileids%5B").Append(k).Append("%5D=").Append(batch[k]);
            var raw = await PostAsync(
                "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/",
                sb.ToString());
            if (string.IsNullOrEmpty(raw)) continue;
            try
            {
                var node = JsonNode.Parse(raw)?["response"]?["publishedfiledetails"]?.AsArray();
                if (node is null) continue;
                foreach (var item in node)
                {
                    if (item is not JsonObject o) continue;
                    var res = o["result"]?.GetValue<int>();
                    if (res != 1) continue; // 跳过失效项
                    var norm = Normalize(o);
                    DetailCache[norm["id"]?.ToString() ?? ""] = (DateTime.UtcNow, (JsonObject)norm.DeepClone());
                    details.Add(norm);
                }
            }
            catch { }
        }
        // 按 browse 返回的 ID 顺序排列
        var order = ids.Select((id, idx) => (id, idx)).ToDictionary(x => x.id, x => x.idx);
        details = details.OrderBy(d => order.TryGetValue(d["id"]?.ToString() ?? "", out var idx) ? idx : 999).ToList();
        return details;
    }

    /// <summary>
    /// 单个 MOD 详情：完整介绍（中文优先）+ 社区评分（星级/评价数）+ 全部字段；按 ID 缓存 30 分钟。
    /// 数据源并行：① GetPublishedFileDetails（结构化字段）；② filedetails?l=schinese 页（星级图片、
    /// 评价数、中文版描述——作者上传了中文介绍时这里返回中文）。
    /// </summary>
    public static async Task<object> Detail(string id)
    {
        id = (id ?? "").Trim();
        if (!Regex.IsMatch(id, @"^\d{1,12}$"))
            return Err("无效的工坊 ID");

        // ① 内存 30 分钟内
        if (DetailCache.TryGetValue(id, out var hit) && (DateTime.UtcNow - hit.At).TotalMinutes < 30)
            return new JsonObject { ["ok"] = true, ["item"] = (JsonObject)hit.Item.DeepClone() };
        // ② 磁盘 30 分钟内（进程重启后依然秒开）
        var fkey = DiskKey("detail_" + id);
        var fresh = LoadDisk(fkey, TimeSpan.FromMinutes(30));
        if (fresh != null)
        {
            DetailCache[id] = (DateTime.UtcNow, fresh);
            return new JsonObject { ["ok"] = true, ["item"] = fresh };
        }
        // ③ 有旧数据（24h 内）：先返回 stale 立即渲染，同时后台刷新；刷新已在跑也返回 stale
        var stale = LoadDisk(fkey, TimeSpan.FromHours(24));
        var inflightKey = "detail:" + id;
        if (stale != null)
        {
            if (InFlight.TryAdd(inflightKey, 0))
            {
                _ = Task.Run(async () =>
                {
                    try { await FetchDetail(id, fkey); }
                    catch { }
                    finally { InFlight.TryRemove(inflightKey, out _); }
                });
            }
            var sd = (JsonObject)stale.DeepClone();
            sd["stale"] = true;
            return new JsonObject { ["ok"] = true, ["item"] = sd };
        }
        // ④ 无缓存：同步抓取
        var item = await FetchDetail(id, fkey);
        if (item is null) return Err("Steam 未返回该 MOD 的数据");
        return new JsonObject { ["ok"] = true, ["item"] = item };
    }

    /// <summary>抓取单个 MOD 完整详情（并行 API + 中文页），写内存 + 磁盘缓存。</summary>
    private static async Task<JsonObject?> FetchDetail(string id, string fkey)
    {
        // 并行：结构化 API + filedetails 中文页（星级/评价数/中文标题/中文描述）
        string? html = null;
        if (DateTime.UtcNow >= _pageBackoffUntil)
        {
            html = await GetAsync(
                "https://steamcommunity.com/sharedfiles/filedetails/?id=" + id + "&l=schinese",
                "birthtime=1000; lastagecheckage=1-January-2000; wants_mature_content=1; Steam_Language=schinese");
            if (!string.IsNullOrEmpty(html) && html.Contains("highlightContent", StringComparison.Ordinal))
                _pageFailStreak = 0;
            else if (++_pageFailStreak >= 2)
                _pageBackoffUntil = DateTime.UtcNow.AddMinutes(5); // 疑似限流：暂停拉页 5 分钟
        }
        var (item, rawDesc) = await FetchApiDetailAsync(id);
        if (item is null) return null;

        // filedetails 页：中文标题 + 评分 + 评价数 + 中文描述（失败自动降级，不影响主数据）
        if (!string.IsNullOrEmpty(html))
        {
            var wt = Regex.Match(html, @"<div class=""workshopItemTitle"">([^<]+)</div>");
            if (wt.Success && !string.IsNullOrWhiteSpace(wt.Groups[1].Value))
                item["title"] = wt.Groups[1].Value.Trim();
            var star = Regex.Match(html, @"(\d)-star_large\.png");
            if (star.Success && int.TryParse(star.Groups[1].Value, out var r5) && r5 >= 1)
                item["rating"] = r5;
            var nr = Regex.Match(html, @"(\d+)\s*(?:个评价|篇评价|ratings|篇用户评价)");
            if (nr.Success && int.TryParse(nr.Groups[1].Value, out var rc))
                item["ratingsCount"] = rc;
            var zh = ExtractHighlightDiv(html);
            if (!string.IsNullOrWhiteSpace(zh))
                item["full"] = HtmlToText(zh);
        }
        // 介绍兜底：中文页没有 → 英文 description（清洗 BBCode）→ 短简介
        if (string.IsNullOrWhiteSpace(item["full"]?.ToString()))
        {
            var apiFull = rawDesc ?? "";
            if (!string.IsNullOrWhiteSpace(apiFull)) item["full"] = CleanDesc(apiFull);
            if (string.IsNullOrWhiteSpace(item["full"]?.ToString()))
                item["full"] = item["desc"]?.ToString() ?? "";
        }
        DetailCache[id] = (DateTime.UtcNow, (JsonObject)item.DeepClone());
        SaveDisk(fkey, (JsonObject)item.DeepClone());
        return item;
    }

    /// <summary>调 GetPublishedFileDetails 单查，返回 (精简结构, 英文原始 description)。</summary>
    private static async Task<(JsonObject? Result, string? RawDesc)> FetchApiDetailAsync(string id)
    {
        var sb = new StringBuilder("itemcount=1&publishedfileids%5B0%5D=").Append(id);
        var raw = await PostAsync(
            "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/",
            sb.ToString());
        if (string.IsNullOrEmpty(raw)) return (null, null);
        try
        {
            var node = JsonNode.Parse(raw)?["response"]?["publishedfiledetails"]?.AsArray();
            if (node is not null)
                foreach (var it in node)
                {
                    if (it is not JsonObject o) continue;
                    if (o["result"]?.GetValue<int>() != 1) return (null, null);
                    return (Normalize(o), o["description"]?.ToString() ?? "");
                }
        }
        catch { }
        return (null, null);
    }

    /// <summary>从 filedetails 页提取描述容器（id="highlightContent"）内 HTML，按 div 平衡截取。</summary>
    private static string ExtractHighlightDiv(string html)
    {
        var key = @"id=""highlightContent"">";
        var start = html.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return "";
        start += key.Length;
        var depth = 1;
        var sb = new StringBuilder();
        for (int i = start; i < html.Length - 5;)
        {
            var open = html.IndexOf("<div", i, StringComparison.Ordinal);
            var close = html.IndexOf("</div>", i, StringComparison.Ordinal);
            if (close < 0) return sb.ToString();
            if (open >= 0 && open < close)
            {
                depth++;
                i = open + 4;
            }
            else
            {
                depth--;
                if (depth == 0) return sb.ToString();
                i = close + 6;
            }
        }
        return sb.ToString();
    }

    /// <summary>描述页 HTML → 纯文本：img 去掉、br/li/p 转换行、剥标签、解实体。</summary>
    private static string HtmlToText(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = Regex.Replace(s, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<img[^>]*>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<li[^>]*>", "\n• ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</(p|div|tr|h\d)>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = s.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<")
             .Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'")
             .Replace("&hellip;", "…").Replace("&mdash;", "—");
        s = Regex.Replace(s, "&#(\\d+);", m =>
        {
            try { return char.ConvertFromUtf32(int.Parse(m.Groups[1].Value)); }
            catch { return ""; }
        });
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    private static int ParseInt(string html, string key)
    {
        var m = Regex.Match(html, key + @"\\?""\s*:\s*\\?""?(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    /// <summary>清洗 BBCode：去掉 [img]/[previewyoutube] 整段与各类标签，保留文本和换行。</summary>
    private static string CleanDesc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = Regex.Replace(s, @"\[img\].*?\[/img\]", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"\[previewyoutube=[^\]]*\]\s*\[/previewyoutube\]", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"\[/?(?:b|i|u|s|h1|h2|h3|h4|h5|h6|list|olist|hr|quote|url|code|noparse|spoiler|map|me|sub|sup|table|tr|td|th|em|strong|p|br)(?:\s*=[^\]]*)?\]", "", RegexOptions.IgnoreCase);
        s = s.Replace("[*]", "• ").Replace("[/*]", "");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    /// <summary>把 Steam 详情字段压成前端需要的精简结构（desc=短简介，full=完整介绍）。</summary>
    private static JsonObject Normalize(JsonObject o)
    {
        string S(string k) => o[k]?.ToString() ?? "";
        var tags = new JsonArray();
        if (o["tags"] is JsonArray t)
            foreach (var tag in t)
                if (tag is JsonObject to)
                {
                    var name = to["display_name"]?.ToString() ?? to["tag"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(name)) tags.Add(name);
                }
        long.TryParse(S("file_size"), out var size);
        long.TryParse(S("time_updated"), out var updated);
        long.TryParse(S("time_created"), out var created);
        long.TryParse(S("subscriptions"), out var subs);
        long.TryParse(S("favorited"), out var fav);
        long.TryParse(S("views"), out var views);
        long.TryParse(S("total_votes"), out var votes);
        int.TryParse(S("num_comments_public"), out var comments);
        double.TryParse(S("star_rating"), out var rating);

        return new JsonObject
        {
            ["id"] = S("publishedfileid"),
            ["title"] = S("title"),
            ["desc"] = S("short_description"),
            ["full"] = CleanDesc(S("description")),
            ["thumb"] = S("preview_url"),
            ["size"] = size,
            ["updated"] = updated,
            ["created"] = created,
            ["subs"] = subs,
            ["fav"] = fav,
            ["views"] = views,
            ["comments"] = comments,
            ["rating"] = rating,
            ["ratingsCount"] = votes,
            ["tags"] = tags,
        };
    }

    private static JsonObject Err(string msg) => new() { ["ok"] = false, ["error"] = msg };
}
