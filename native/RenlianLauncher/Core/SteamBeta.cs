using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RenlianLauncher.Core;

/// <summary>
/// Steam 版本分支（beta）读写。
///
/// P 社游戏的「版本切换」本质就是 Steam 的 beta 分支：
///   · 分支列表：&lt;Steam&gt;\appcache\appinfo.vdf（v41 二进制 VDF，字符串表在文件尾部）
///   · 当前选择：&lt;库&gt;\steamapps\appmanifest_&lt;appid&gt;.acf 的 UserConfig.BetaKey
///
/// 与旧版一致的原则：
///   · appinfo.vdf 解析失败时退回内置常见分支表，绝不抛异常；
///   · 写 acf 前必须确认 Steam 已完全退出（否则改动会被 Steam 覆盖）；
///   · 写前备份 &lt;acf&gt;.bak，只改 BetaKey 与 StateFlags，保留其它字段与缩进风格。
/// </summary>
public static class SteamBeta
{
    // ------------------------------------------------------------ 路径

    /// <summary>在所有 Steam 库里找 appmanifest_&lt;appid&gt;.acf；找不到返回空串。</summary>
    public static string ManifestPath(string appid, string? gameRoot = null)
    {
        var libs = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameRoot))
        {
            try
            {
                var full = Path.GetFullPath(gameRoot);
                var lib = Path.GetFullPath(Path.Combine(full, "..", ".."));
                if (Directory.Exists(lib)) libs.Add(lib);
            }
            catch { }
        }
        foreach (var r in Paths.SteamRoots())
            if (!libs.Contains(r, StringComparer.OrdinalIgnoreCase)) libs.Add(r);

        var name = $"appmanifest_{appid}.acf";
        foreach (var lib in libs)
        {
            var p = Path.Combine(lib, "steamapps", name);
            if (File.Exists(p)) return p;
        }
        return "";
    }

    public static string SteamExePath()
    {
        foreach (var root in Paths.SteamRoots())
        {
            var p = Path.Combine(root, "steam.exe");
            if (File.Exists(p)) return p;
        }
        return "";
    }

    private static string AppInfoPath()
    {
        foreach (var root in Paths.SteamRoots())
        {
            var p = Path.Combine(root, "appcache", "appinfo.vdf");
            if (File.Exists(p)) return p;
        }
        return "";
    }

    // ------------------------------------------------------------ 进程状态

    /// <summary>Steam 是否在运行（按进程名判断，不依赖 tasklist 的中文编码）。</summary>
    public static bool SteamRunning()
    {
        try { return Process.GetProcessesByName("steam").Length > 0; }
        catch { return false; }
    }

    /// <summary>某个 exe 是否在运行（用于拦截「游戏没退干净就切版本」）。</summary>
    public static bool ProcessRunning(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return false;
        try { return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName)).Length > 0; }
        catch { return false; }
    }

    // ------------------------------------------------------------ acf 读取

    public sealed record BranchInfo(string Key, string Desc, long BuildId);

    public sealed record ManifestInfo(
        string Path, string InstallDir, long StateFlags, string BetaKey,
        string BuildId, List<BranchInfo> Branches);

    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    }

    private static string ReStr(string text, string pattern, string fallback = "")
    {
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : fallback;
    }

    /// <summary>解析 acf 的 VDF 文本结构（顶层键值 + 可选 branches 段）。</summary>
    public static ManifestInfo? ReadManifest(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var text = ReadText(path);
            var installDir = ReStr(text, "\"installdir\"\\s+\"([^\"]*)\"");
            var flagsRaw = ReStr(text, "\"StateFlags\"\\s+\"([^\"]*)\"", "0");
            long.TryParse(flagsRaw, out var flags);
            var beta = ReStr(text, "\"BetaKey\"\\s+\"([^\"]*)\"", "public");
            var buildId = ReStr(text, "\"buildid\"\\s+\"([^\"]*)\"");
            var branches = ParseAcfBranches(text);
            return new ManifestInfo(path, installDir, flags, beta, buildId, branches);
        }
        catch { return null; }
    }

    /// <summary>解析 acf 里的 "branches" 段（标准 appmanifest 通常没有，有则一并读）。</summary>
    private static List<BranchInfo> ParseAcfBranches(string text)
    {
        var list = new List<BranchInfo>();
        var m = Regex.Match(text, "\"branches\"\\s*\\{", RegexOptions.IgnoreCase);
        if (!m.Success) return list;

        // 花括号配对：跳过引号内的字符，避免目录名里的括号干扰
        int start = m.Index + m.Length;
        int depth = 1, i = start;
        bool inQuote = false;
        while (i < text.Length && depth > 0)
        {
            var c = text[i];
            if (inQuote)
            {
                if (c == '\\') i++;
                else if (c == '"') inQuote = false;
            }
            else if (c == '"') inQuote = true;
            else if (c == '{') depth++;
            else if (c == '}') depth--;
            i++;
        }
        var block = text[start..Math.Max(start, i - 1)];

        foreach (Match bm in Regex.Matches(block, "\"([^\"]+)\"\\s*\\{([\\s\\S]*?)\\}", RegexOptions.IgnoreCase))
        {
            var key = bm.Groups[1].Value;
            var inner = bm.Groups[2].Value;
            var desc = ReStr(inner, "\"description\"\\s+\"([^\"]*)\"");
            var bidRaw = ReStr(inner, "\"buildid\"\\s+\"([^\"]*)\"");
            long.TryParse(bidRaw, out var bid);
            if (!list.Any(x => x.Key == key))
                list.Add(new BranchInfo(key, desc, bid));
        }
        return list;
    }

    // ------------------------------------------------------------ appinfo.vdf 解析

    private static readonly byte[] PatBuildId = { 0x02, 0xE9, 0x01, 0x00, 0x00 };
    private static readonly byte[] PatDesc = { 0x01, 0xEC, 0x01, 0x00, 0x00 };

    private static int IndexOf(byte[] hay, byte[] needle, int start)
    {
        if (needle.Length == 0 || hay.Length < needle.Length) return -1;
        int last = hay.Length - needle.Length;
        for (int i = Math.Max(0, start); i <= last; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    /// <summary>v41 尾部字符串表：头部 offset8 存 uint64 偏移，偏移处前 4 字节是条数。</summary>
    private static List<string>? ReadStringTable(byte[] data)
    {
        try
        {
            if (data.Length < 16) return null;
            if (BitConverter.ToUInt32(data, 0) != 0x07564429u) return null;
            var tbl = BitConverter.ToUInt64(data, 8);
            if (tbl == 0 || tbl >= (ulong)data.Length - 4) return null;

            var count = BitConverter.ToUInt32(data, (int)tbl);
            if (count == 0 || count > 5_000_000) return null;

            var list = new List<string>((int)count);
            int off = (int)tbl + 4;
            for (int i = 0; i < count; i++)
            {
                int end = Array.IndexOf(data, (byte)0, off);
                if (end < 0) return null;
                list.Add(Encoding.Latin1.GetString(data, off, end - off));
                off = end + 1;
            }
            return list;
        }
        catch { return null; }
    }

    private static List<BranchInfo> ParseAppInfoBlock(byte[] block, List<string> strings)
    {
        var list = new List<BranchInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int k = 0;
        while (true)
        {
            int m = IndexOf(block, PatBuildId, k);
            if (m < 0) break;
            k = m + 1;
            if (m < 6 || block[m - 6] != 0x08 || block[m - 5] != 0x00) continue;

            int keyIdx = BitConverter.ToInt32(block, m - 4);
            if (keyIdx < 0 || keyIdx >= strings.Count) continue;
            var key = strings[keyIdx];
            if (string.IsNullOrEmpty(key) ||
                key.Equals("buildid", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("description", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("timeupdated", StringComparison.OrdinalIgnoreCase))
                continue;

            long bid = m + 9 <= block.Length ? BitConverter.ToUInt32(block, m + 5) : 0;

            var desc = "";
            int dp = IndexOf(block, PatDesc, m + 9);
            if (dp >= 0)
            {
                int ds = dp + 5;
                int de = Array.IndexOf(block, (byte)0, ds);
                if (de > ds) desc = Encoding.Latin1.GetString(block, ds, de - ds).Trim();
            }

            var sig = key + "\u0001" + desc;
            if (!seen.Add(sig)) continue;
            list.Add(new BranchInfo(key, desc, bid));
        }
        return list;
    }

    /// <summary>读 appinfo.vdf 里某 appid 的所有分支（取命中分支最多的数据块）。</summary>
    public static List<BranchInfo> ParseAppInfo(string appid)
    {
        var result = new List<BranchInfo>();
        var path = AppInfoPath();
        if (path.Length == 0 || !int.TryParse(appid, out var id) || id <= 0) return result;

        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch { return result; }

        var strings = ReadStringTable(data);
        if (strings is null || strings.Count == 0) return result;

        var pat = BitConverter.GetBytes(id);
        int pos = 0;
        while (true)
        {
            int j = IndexOf(data, pat, pos);
            if (j < 0) break;
            pos = j + 1;
            if (j + 8 > data.Length) continue;

            var size = BitConverter.ToUInt32(data, j + 4);
            if (size == 0 || size >= 8_000_000 || (long)j + 8 + size > data.Length) continue;

            var block = new byte[size];
            Array.Copy(data, j + 8, block, 0, (int)size);
            var branches = ParseAppInfoBlock(block, strings);
            if (branches.Count > result.Count)
            {
                result = branches;
                if (result.Count >= 20) break;   // 已足够像真实数据块
            }
        }
        return result;
    }

    // 内置兜底：appinfo 解析失败时仍可切换（CK3 常见分支）
    private static readonly (string Key, string Desc)[] Ck3Fallback =
    {
        ("public", "默认公开版"),
        ("open_beta", "Open beta for update 1.19.0"),
        ("1.20.0", "Old Version 1.20.0.4 (Crozier)"),
        ("1.19.0", "Old Version 1.19.0.6 (Scribe)"),
        ("1.18.4", "Old Version 1.18.4 (Crane)"),
        ("1.17.1", "Old version 1.17.1 (Ascendant)"),
        ("1.16.2", "Old version 1.16.2 (Chamfron)"),
        ("1.15.0", "Old version 1.15.0 (Crown)"),
    };

    /// <summary>分支列表（appinfo → acf → 内置兜底），public 永远置顶。</summary>
    public static List<BranchInfo> Branches(string appid, ManifestInfo? manifest)
    {
        var list = ParseAppInfo(appid);
        if (list.Count == 0 && manifest is not null && manifest.Branches.Count > 0)
            list = manifest.Branches;
        if (list.Count == 0 && appid == "1158310")
            list = Ck3Fallback.Select(x => new BranchInfo(x.Key, x.Desc, 0)).ToList();

        if (!list.Any(b => b.Key == "public"))
            list.Insert(0, new BranchInfo("public", "默认公开版本（游戏最常见版本）", 0));
        else
        {
            var pub = list.First(b => b.Key == "public");
            list.Remove(pub);
            list.Insert(0, pub with { Desc = string.IsNullOrWhiteSpace(pub.Desc) ? "默认公开版本（游戏最常见版本）" : pub.Desc });
        }
        return list;
    }

    // ------------------------------------------------------------ 接口数据

    /// <summary>GET /api/steam/branches 的数据（字段与旧版一致）。</summary>
    public static JsonObject BranchesInfo(ConfigStore cfg)
    {
        var game = cfg.CurrentGame;
        var def = GameDefs.Get(game);
        var gameRoot = cfg.GameStr(game, "gameRoot");
        var manifestPath = ManifestPath(def.AppId, gameRoot);
        var manifest = manifestPath.Length > 0 ? ReadManifest(manifestPath) : null;
        var current = manifest?.BetaKey is { Length: > 0 } k ? k : "public";

        var arr = new JsonArray();
        foreach (var b in Branches(def.AppId, manifest))
        {
            arr.Add(new JsonObject
            {
                ["key"] = b.Key,
                ["desc"] = b.Desc,
                ["buildid"] = b.BuildId,
            });
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["appid"] = def.AppId,
            ["current"] = current,
            ["manifest"] = manifestPath,
            ["steamRunning"] = SteamRunning(),
            ["branches"] = arr,
        };
    }

    /// <summary>
    /// 顶栏「关联 Steam」用：把 Steam 读取状态一次讲清楚——探测到哪些库、游戏清单
    /// 和工坊目录在不在、能读到几个模组。用户点一下就知道启动器有没有权限/能不能访问 Steam。
    /// </summary>
    public static JsonObject LinkInfo(ConfigStore cfg)
    {
        var game = cfg.CurrentGame;
        var def = GameDefs.Get(game);
        var roots = Paths.SteamRoots();
        var arr = new JsonArray();
        var manifest = "";
        var workshop = cfg.GameStr(game, "workshopDir") ?? "";
        var workshopOk = workshop.Length > 0 && Directory.Exists(workshop);
        int modCount = 0;

        foreach (var r in roots)
        {
            var steamapps = Path.Combine(r, "steamapps");
            var acf = Path.Combine(steamapps, $"appmanifest_{def.AppId}.acf");
            var manifestOk = File.Exists(acf);
            if (manifestOk && manifest.Length == 0) manifest = acf;
            var ws = Path.Combine(steamapps, "workshop", "content", def.AppId);
            if (Directory.Exists(ws))
            {
                try { modCount += Directory.EnumerateDirectories(ws).Count(); } catch { }
            }
            arr.Add(new JsonObject
            {
                ["path"] = r,
                ["steamapps"] = Directory.Exists(steamapps),
                ["manifest"] = manifestOk,
                ["workshop"] = Directory.Exists(ws),
            });
        }

        var linked = roots.Count > 0;
        var msg = !linked
            ? "没有找到 Steam 安装目录：请确认 Steam 已安装，或在下面手动关联 Steam 目录"
            : manifest.Length > 0
                ? "已读取到 Steam 和本游戏的清单，启动器可以正常访问 Steam"
                : "已读取到 Steam，但没找到本游戏的 appmanifest（游戏可能未安装或装在别的库）";

        return new JsonObject
        {
            ["ok"] = true,
            ["linked"] = linked,
            ["manualDir"] = Paths.ManualSteamRoot,
            ["roots"] = arr,
            ["manifest"] = manifest,
            ["manifestOk"] = manifest.Length > 0,
            ["workshopDir"] = workshop,
            ["workshopOk"] = workshopOk,
            ["modCount"] = modCount,
            ["steamRunning"] = SteamRunning(),
            ["appid"] = def.AppId,
            ["msg"] = msg,
        };
    }

    /// <summary>手动关联 Steam 目录；传空串表示清除关联、恢复自动探测。</summary>
    public static JsonObject Link(ConfigStore cfg, string dir)
    {
        dir = (dir ?? "").Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
        if (dir.Length == 0)
        {
            cfg.SteamDir = "";
            cfg.Save();
            Paths.SetManualSteamRoot(null);
            return LinkInfo(cfg);
        }
        if (!Directory.Exists(dir))
            return new JsonObject { ["ok"] = false, ["error"] = "目录不存在：" + dir };

        // 允许用户填 Steam 安装根、库根，或直接填 steamapps 这一层
        var check = Path.GetFileName(dir).Equals("steamapps", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(Path.Combine(dir, "..")) : dir;
        var looksSteam = Directory.Exists(Path.Combine(check, "steamapps"))
                         || Directory.Exists(Path.Combine(check, "workshop", "content"));
        if (!looksSteam)
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = "这个目录不像 Steam 安装目录或库目录（下面找不到 steamapps）",
            };

        cfg.SteamDir = check;
        cfg.Save();
        Paths.SetManualSteamRoot(check);
        return LinkInfo(cfg);
    }

    // ------------------------------------------------------------ 写入 BetaKey

    /// <summary>
    /// 把 appmanifest 的 BetaKey 改成 newKey，并把 StateFlags 置为「需要更新」。
    /// 写前必须 Steam 完全退出；自动备份 &lt;acf&gt;.bak。
    /// </summary>
    public static JsonObject SetBeta(string appid, string newKey, string? gameRoot = null)
    {
        var key = (newKey ?? "").Trim();
        if (key.Length == 0)
            return Fail("分支键为空");

        var path = ManifestPath(appid, gameRoot);
        if (path.Length == 0)
            return Fail("找不到该游戏的 Steam 清单（可能不是 Steam 安装）");

        if (SteamRunning())
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = "请先完全退出 Steam",
                ["need_close"] = true,
            };

        try
        {
            var text = ReadText(path);

            var backup = path + ".bak";
            var backupOk = true;
            try { File.Copy(path, backup, overwrite: true); }
            catch { backupOk = false; }

            string text2;
            var m = Regex.Match(text, "(\"BetaKey\"\\s*\")([^\"]*)(\")", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                // 只替换值，保留原有缩进与制表符风格
                text2 = text[..m.Groups[2].Index] + key + text[(m.Groups[2].Index + m.Groups[2].Length)..];
            }
            else
            {
                var mu = Regex.Match(text, "(\"UserConfig\"\\s*\\{)", RegexOptions.IgnoreCase);
                if (!mu.Success)
                    return Fail("清单里没有 UserConfig 段，无法写入");
                var insertAt = mu.Index + mu.Length;
                text2 = text[..insertAt] + "\n\t\t\"BetaKey\"\t\t\"" + key + "\"" + text[insertAt..];
            }

            // 置 StateFlags |= 2（Update Required），Steam 启动后才会真正下载所选分支
            var mf = Regex.Match(text2, "(\"StateFlags\"\\s*\")(\\d+)(\")", RegexOptions.IgnoreCase);
            if (mf.Success)
            {
                var nv = long.TryParse(mf.Groups[2].Value, out var old) ? (old | 2) : 6;
                text2 = text2[..mf.Groups[2].Index] + nv + text2[(mf.Groups[2].Index + mf.Groups[2].Length)..];
            }

            File.WriteAllText(path, text2, new UTF8Encoding(false));

            return new JsonObject
            {
                ["ok"] = true,
                ["key"] = key,
                ["manifest"] = path,
                ["backup"] = backupOk ? backup : "",
                ["forceUpdate"] = true,
            };
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    /// <summary>
    /// POST /api/steam/switch：可选自动关 Steam → 写清单 → 重新拉起 Steam。
    /// restart=false 且 Steam 在运行时直接拒绝（与旧版一致）。
    /// </summary>
    public static JsonObject Switch(ConfigStore cfg, string key, bool restart)
    {
        var game = cfg.CurrentGame;
        var def = GameDefs.Get(game);

        var exeName = def.Exe.Length > 0 ? def.Exe[^1] : "";
        if (ProcessRunning(exeName))
            return Fail("检测到游戏进程仍在（可能是上次残留的僵尸进程）。请先完全退出游戏；" +
                        "若已关闭，请重启电脑清掉残留进程后再切换（Steam 无法更新被占用的文件）");

        var steamWasRunning = SteamRunning();
        if (restart && steamWasRunning)
        {
            if (!QuitSteam(35))
                return Fail("Steam 未能自动关闭，请手动完全退出 Steam 后再试");
        }

        var r = SetBeta(def.AppId, key, cfg.GameStr(game, "gameRoot"));
        if (ConfigStore.Bool(r, "ok", false) && restart)
        {
            r["steamRestarted"] = LaunchSteam();
        }
        return r;
    }

    // ------------------------------------------------------------ Steam 启停

    /// <summary>退出 Steam：先 steam://exit，等 10 秒；没退就 taskkill；再等到超时。</summary>
    public static bool QuitSteam(int waitSeconds = 15)
    {
        try
        {
            Process.Start(new ProcessStartInfo("steam://exit") { UseShellExecute = true });
        }
        catch { }

        var t0 = DateTime.UtcNow;
        while ((DateTime.UtcNow - t0).TotalSeconds < 10 && SteamRunning())
            Thread.Sleep(1000);

        if (SteamRunning())
        {
            foreach (var name in new[] { "steam", "steamwebhelper" })
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try { p.Kill(); } catch { }
                    }
                }
                catch { }
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds > 0 ? waitSeconds : 15);
        while (DateTime.UtcNow < deadline && SteamRunning())
            Thread.Sleep(1000);
        return !SteamRunning();
    }

    public static bool LaunchSteam()
    {
        try
        {
            var exe = SteamExePath();
            Process.Start(new ProcessStartInfo(exe.Length > 0 ? exe : "steam://open/main")
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch { return false; }
    }

    private static JsonObject Fail(string error) => new() { ["ok"] = false, ["error"] = error };
}
