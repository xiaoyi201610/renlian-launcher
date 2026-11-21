using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RenlianLauncher.Core;

/// <summary>
/// mod 文件操作层：显示名别名、来源覆盖、收藏夹、删除、工坊描述文件注册、
/// 缩略图缓存、压缩包导入与导出打包。
///
/// 所有改动玩家文件的入口都遵循同一条规矩：
///   · 先备份到配置目录的 backups/，备份失败就放弃操作并返回 {ok:false,error}
///   · 任何异常都转成可读的 error 字符串，绝不抛出到 API 层
/// 三个 sidecar JSON（别名 / 来源 / 收藏夹）沿用旧版文件格式，老用户数据可无缝接续。
/// </summary>
public static class ModOps
{
    public static string BackupDir => Paths.BackupDir;
    private static string AliasPath => Path.Combine(Paths.MetaDir, "mod-aliases.json");
    private static string SourceOverridePath => Path.Combine(Paths.MetaDir, "mod-source-overrides.json");
    private static string CollectionsPath => Path.Combine(Paths.MetaDir, "mod-collections.json");

    private const int MaxBackups = 12;   // 每个 tag 最多保留几份

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static JsonObject Fail(string error) => new() { ["ok"] = false, ["error"] = error };

    // ------------------------------------------------------------ sidecar JSON 读写

    private static JsonObject LoadJson(string path)
    {
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) is JsonObject o)
                return o;
        }
        catch { /* 文件坏了就当空的，别让整个页面打不开 */ }
        return new JsonObject();
    }

    private static void SaveJson(string path, JsonObject data)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, data.ToJsonString(WriteOpts), new UTF8Encoding(false));
            File.Move(tmp, path, true);
        }
        catch { /* 存不下也不能崩 */ }
    }

    /// <summary>取（或补建）游戏对应的子表。</summary>
    private static JsonObject Section(JsonObject root, string game)
    {
        if (root[game] is JsonObject o) return o;
        o = new JsonObject();
        root[game] = o;
        return o;
    }

    // ------------------------------------------------------------ 别名（只改启动器里的显示名）

    private static Dictionary<string, string> AliasesOf(string game)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Section(LoadJson(AliasPath), game))
        {
            if (kv.Value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                map[kv.Key] = s;
        }
        return map;
    }

    public static string GetAlias(string game, string modFile) =>
        AliasesOf(game).TryGetValue((modFile ?? "").ToLowerInvariant(), out var a) ? a : "";

    /// <summary>alias 为空 = 恢复原名（删除记录）。</summary>
    public static void SetAlias(string game, string modFile, string alias)
    {
        var root = LoadJson(AliasPath);
        var g = Section(root, game);
        var key = (modFile ?? "").ToLowerInvariant();
        alias = (alias ?? "").Trim();
        if (alias.Length > 0) g[key] = alias;
        else g.Remove(key);
        SaveJson(AliasPath, root);
    }

    // ------------------------------------------------------------ 来源手动覆盖

    private static Dictionary<string, string> SourceOverridesOf(string game)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Section(LoadJson(SourceOverridePath), game))
        {
            if (kv.Value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                map[kv.Key] = s;
        }
        return map;
    }

    /// <summary>source 为空 = 清除覆盖（回到自动判定）。</summary>
    public static void SetSourceOverride(string game, string modFile, string source)
    {
        var root = LoadJson(SourceOverridePath);
        var g = Section(root, game);
        var key = (modFile ?? "").ToLowerInvariant();
        source = (source ?? "").Trim().ToLowerInvariant();
        if (source is "local" or "steam") g[key] = source;
        else g.Remove(key);
        SaveJson(SourceOverridePath, root);
    }

    // ------------------------------------------------------------ 收藏夹

    private static JsonObject CollectionsRoot() => LoadJson(CollectionsPath);

    public static List<JsonObject> ListCollections(string game)
    {
        var list = new List<JsonObject>();
        foreach (var kv in Section(CollectionsRoot(), game))
        {
            list.Add(new JsonObject
            {
                ["name"] = kv.Key,
                ["count"] = kv.Value is JsonArray a ? a.Count : 0,
            });
        }
        return list;
    }

    public static bool CreateCollection(string game, string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return false;
        var root = CollectionsRoot();
        var g = Section(root, game);
        if (g[name] is not null) return false;
        g[name] = new JsonArray();
        SaveJson(CollectionsPath, root);
        return true;
    }

    public static bool RenameCollection(string game, string oldName, string newName)
    {
        oldName = (oldName ?? "").Trim();
        newName = (newName ?? "").Trim();
        if (oldName.Length == 0 || newName.Length == 0) return false;
        var root = CollectionsRoot();
        var g = Section(root, game);
        if (g[oldName] is not JsonArray oldList || g[newName] is not null) return false;
        g.Remove(oldName);
        g[newName] = oldList;
        SaveJson(CollectionsPath, root);
        return true;
    }

    public static bool DeleteCollection(string game, string name)
    {
        var root = CollectionsRoot();
        var g = Section(root, game);
        if (g[name] is null) return false;
        g.Remove(name);
        if (g.Count == 0) root.Remove(game);
        SaveJson(CollectionsPath, root);
        return true;
    }

    /// <summary>收藏/取消收藏，返回是否已收藏。</summary>
    public static bool ToggleInCollection(string game, string name, string modFile)
    {
        name = (name ?? "").Trim();
        var key = (modFile ?? "").ToLowerInvariant();
        if (name.Length == 0 || key.Length == 0) return false;

        var root = CollectionsRoot();
        var g = Section(root, game);
        var list = g[name] as JsonArray ?? new JsonArray();
        var idx = -1;
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i]?.ToString(), key, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
        }

        bool added;
        if (idx >= 0)
        {
            list.RemoveAt(idx);
            added = false;
        }
        else
        {
            list.Add(key);
            added = true;
        }
        g[name] = list;
        SaveJson(CollectionsPath, root);
        return added;
    }

    // ------------------------------------------------------------ 索引补丁（别名 / 来源 / 收藏夹）
    //
    // ModIndex 只认磁盘上的 .mod 文件，别名与收藏夹是启动器自己的元数据，
    // 这里在索引对象上就地补齐字段，和旧版 build_mod_index 的字段口径一致。
    // 自动判定的原值单独记着，用户清掉覆盖时才能还原。

    private static readonly ConcurrentDictionary<string, string> AutoSource = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> AutoName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object EnrichLock = new();

    /// <summary>索引整体重建时调用，丢掉上一次的「自动判定值」快照。</summary>
    public static void Forget()
    {
        AutoSource.Clear();
        AutoName.Clear();
    }

    public static void Enrich(string game, List<JsonObject> items)
    {
        var aliases = AliasesOf(game);
        var sources = SourceOverridesOf(game);

        var colMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Section(CollectionsRoot(), game))
        {
            if (kv.Value is not JsonArray arr) continue;
            foreach (var node in arr)
            {
                var mf = (node?.ToString() ?? "").ToLowerInvariant();
                if (mf.Length == 0) continue;
                if (!colMap.TryGetValue(mf, out var names)) colMap[mf] = names = new List<string>();
                if (!names.Contains(kv.Key)) names.Add(kv.Key);
            }
        }

        lock (EnrichLock)
        {
            foreach (var it in items)
            {
                var file = ConfigStore.Str(it, "modFile");
                if (string.IsNullOrWhiteSpace(file)) continue;
                var key = file!.ToLowerInvariant();

                AutoSource.TryAdd(key, ConfigStore.Str(it, "source") ?? "");
                AutoName.TryAdd(key, ConfigStore.Str(it, "originalName") ?? ConfigStore.Str(it, "name") ?? "");

                if (aliases.TryGetValue(key, out var alias))
                {
                    it["name"] = alias;
                    it["aliased"] = true;
                }
                else
                {
                    it["name"] = AutoName[key];
                    it["aliased"] = false;
                }

                if (sources.TryGetValue(key, out var src))
                {
                    it["source"] = src;
                    it["sourceOverridden"] = true;
                }
                else
                {
                    it["source"] = AutoSource[key];
                    it["sourceOverridden"] = false;
                }

                var cols = new JsonArray();
                if (colMap.TryGetValue(key, out var names))
                    foreach (var n in names) cols.Add(n);
                it["collections"] = cols;
            }
        }
    }

    // ------------------------------------------------------------ 删除 mod

    /// <summary>
    /// 删除一个 mod：先备份 .mod 描述文件到 backups/，再把描述文件与内容目录移进回收站。
    /// modFile 形如 "mod/ugc_123.mod"；只允许操作 userDir/mod 下的文件，防止误删。
    /// </summary>
    public static JsonObject DeleteMod(ConfigStore cfg, string modFile)
    {
        var userDir = (cfg.GameStr(cfg.CurrentGame, "userDir") ?? "").Trim();
        if (userDir.Length == 0) return Fail("未设置用户数据目录");

        var modDir = Path.Combine(userDir, "mod");
        string full;
        try { full = Path.GetFullPath(Path.Combine(userDir, modFile.Replace('/', '\\'))); }
        catch { return Fail("非法路径"); }

        var rootCmp = Path.GetFullPath(modDir).TrimEnd('\\') + "\\";
        if (!full.StartsWith(rootCmp, StringComparison.OrdinalIgnoreCase)) return Fail("非法路径");
        if (!File.Exists(full)) return Fail("描述文件不存在");

        // 先备份描述文件，备份不了就不动玩家的东西
        var backup = BackupDescriptor(full, modFile);
        if (backup is null) return Fail("备份描述文件失败，已取消删除");

        var desc = Pdx.ParseFile(full);
        var pathAttr = (Pdx.Str(desc, "path") ?? "").Trim().Replace('/', '\\');
        var contentDir = "";
        try
        {
            if (pathAttr.Length > 0)
                contentDir = Path.GetFullPath(Path.IsPathRooted(pathAttr)
                    ? pathAttr : Path.Combine(userDir, pathAttr));
        }
        catch { contentDir = ""; }

        var removed = new List<string>();
        if (Recycle(full)) removed.Add(full);
        if (contentDir.Length > 0 && Directory.Exists(contentDir) &&
            !string.Equals(contentDir.TrimEnd('\\'), Path.GetFullPath(modDir).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            // 内容目录体积可能很大，走回收站（可撤销）而不是再复制一份到 backups
            if (Recycle(contentDir)) removed.Add(contentDir);
        }

        return new JsonObject
        {
            ["ok"] = removed.Count > 0,
            ["error"] = removed.Count > 0 ? "" : "删除失败（可能被占用）",
            ["removed"] = new JsonArray(removed.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["backup"] = backup,
        };
    }

    private static string? BackupDescriptor(string full, string modFile)
    {
        try
        {
            Directory.CreateDirectory(BackupDir);
            var tag = "mod-" + SafeDirName(Path.GetFileNameWithoutExtension(modFile), "mod");
            var stem = Path.Combine(BackupDir, tag + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            var dst = stem + ".mod";
            for (var n = 2; File.Exists(dst); n++) dst = $"{stem}-{n}.mod";
            File.Copy(full, dst, true);
            PruneBackups(tag);
            return dst;
        }
        catch { return null; }
    }

    private static void PruneBackups(string tag)
    {
        try
        {
            foreach (var f in Directory.GetFiles(BackupDir, tag + "-*")
                         .Select(f => new FileInfo(f))
                         .OrderByDescending(f => f.LastWriteTime)
                         .Skip(MaxBackups))
            {
                try { f.Delete(); } catch { }
            }
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>用 Windows Shell API 移到回收站（可撤销）。</summary>
    private static bool Recycle(string path)
    {
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = 3,                              // FO_DELETE
                pFrom = Path.GetFullPath(path) + "\0\0", // 双 NUL 结尾的多字符串
                pTo = null,
                fFlags = 0x0040 | 0x0010 | 0x0004,      // ALLOWUNDO | NOCONFIRMATION | SILENT
            };
            var res = SHFileOperation(ref op);
            return res == 0 && !op.fAnyOperationsAborted;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------ 工坊描述文件注册

    /// <summary>把工坊 mod 写成 mod/ugc_&lt;id&gt;.mod（官方启动器同款命名）。</summary>
    public static JsonObject RegisterDescriptor(ConfigStore cfg, string steamId)
    {
        steamId = (steamId ?? "").Trim();
        var userDir = (cfg.GameStr(cfg.CurrentGame, "userDir") ?? "").Trim();
        if (userDir.Length == 0) return Fail("未设置游戏用户目录，无法生成 .mod 描述文件");

        var workshop = cfg.GameStr(cfg.CurrentGame, "workshopDir") ?? "";
        var src = Path.Combine(workshop, steamId, "descriptor.mod");
        if (!File.Exists(src)) return Fail("工坊目录里找不到 descriptor.mod");

        var desc = Pdx.ParseFile(src);
        var wsDir = Path.Combine(workshop, steamId);
        var sb = new StringBuilder();
        sb.Append("version=\"").Append(Pdx.Str(desc, "version") ?? "").Append("\"\n");
        sb.Append("name=\"").Append(Pdx.Str(desc, "name") ?? "").Append("\"\n");
        sb.Append("supported_version=\"").Append(Pdx.Str(desc, "supported_version") ?? "").Append("\"\n");
        sb.Append("remote_file_id=\"").Append(steamId).Append("\"\n");
        sb.Append("path=\"").Append(wsDir.Replace('\\', '/')).Append("\"\n");
        var tags = Pdx.List(desc, "tags");
        if (tags.Count > 0)
        {
            sb.Append("tags={\n");
            foreach (var t in tags) sb.Append('\t').Append('"').Append(t).Append("\"\n");
            sb.Append("}\n");
        }

        var modDir = Path.Combine(userDir, "mod");
        var dst = Path.GetFullPath(Path.Combine(modDir, $"ugc_{steamId}.mod"));
        // 保护：同名 .mod 可能属于本地 mod，直接覆盖会把它的 path 洗成工坊目录
        if (File.Exists(dst))
        {
            try
            {
                var oldPath = (Pdx.Str(Pdx.ParseFile(dst), "path") ?? "").Trim();
                if (oldPath.Length > 0)
                {
                    var oldFull = Path.GetFullPath(Path.IsPathRooted(oldPath)
                        ? oldPath : Path.Combine(userDir, oldPath));
                    if (!oldFull.StartsWith(Path.GetFullPath(workshop), StringComparison.OrdinalIgnoreCase))
                        return Fail($"ugc_{steamId}.mod 已被本地模组占用，未覆盖");
                }
            }
            catch { }
        }

        try
        {
            Directory.CreateDirectory(modDir);   // 新装游戏时 mod/ 可能还不存在
            File.WriteAllText(dst, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex) { return Fail("写入描述文件失败：" + ex.Message); }

        return new JsonObject { ["ok"] = true, ["path"] = dst };
    }

    // ------------------------------------------------------------ 缩略图缓存

    /// <summary>
    /// 列表里只有 40px 的头像，原图动辄 1MB：压成 160px 的 JPEG 落盘缓存，
    /// 转不了的格式（DDS 等）直接回原文件，保持旧版行为。
    /// </summary>
    public static string ThumbFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            var key = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(
                $"{path.ToLowerInvariant()}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}"))).ToLowerInvariant();
            var dir = Paths.ThumbCacheDir;
            var outPath = Path.Combine(dir, key + ".jpg");
            if (File.Exists(outPath)) return outPath;

            Directory.CreateDirectory(dir);
            using (var fs = File.OpenRead(path))
            {
                var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                BitmapSource src = dec.Frames[0];
                var scale = Math.Min(160.0 / src.PixelWidth, 160.0 / src.PixelHeight);
                if (scale < 1.0)
                {
                    var scaled = new TransformedBitmap(src, new ScaleTransform(scale, scale));
                    scaled.Freeze();
                    src = scaled;
                }
                // JPEG 不支持透明通道，先转成 24 位 BGR
                var converted = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
                var enc = new JpegBitmapEncoder { QualityLevel = 82 };
                enc.Frames.Add(BitmapFrame.Create(converted));
                var tmp = outPath + ".tmp";
                using (var os = File.Create(tmp)) enc.Save(os);
                File.Move(tmp, outPath, true);
            }
            return outPath;
        }
        catch { return path; }
    }

    /// <summary>限制可读取的绝对路径范围，避免任意文件读取。</summary>
    public static bool IsPathAllowed(ConfigStore cfg, string path)
    {
        var game = cfg.CurrentGame;
        var roots = new[]
        {
            cfg.GameStr(game, "gameRoot") ?? "",
            cfg.GameStr(game, "userDir") ?? "",
            cfg.GameStr(game, "workshopDir") ?? "",
            BackupDir,
        };
        string full;
        try { full = Path.GetFullPath(path); } catch { return false; }
        foreach (var r in roots)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            string root;
            try { root = Path.GetFullPath(r); } catch { continue; }
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------ 压缩包导入

    /// <summary>
    /// 把拖进来的 mod 压缩包安装成本地 mod：
    /// 解压到临时目录 → 定位 descriptor.mod → 搬到 userDir/mod/&lt;安全目录名&gt;/ → 生成同名 .mod。
    /// 目标重名时自动加 _2/_3，绝不覆盖已有 mod。
    /// </summary>
    public static JsonObject InstallArchive(ConfigStore cfg, string archive, string origName)
    {
        var userDir = (cfg.GameStr(cfg.CurrentGame, "userDir") ?? "").Trim();
        if (userDir.Length == 0 || !Directory.Exists(userDir))
            return Fail("还没找到用户数据目录，先在「启动器设置」里指定后再拖入");

        var modDir = Path.Combine(userDir, "mod");
        try { Directory.CreateDirectory(modDir); }
        catch (Exception ex) { return Fail("创建 mod 目录失败：" + ex.Message); }

        var stem = Path.GetFileNameWithoutExtension(
            string.IsNullOrWhiteSpace(origName) ? archive : origName);
        var work = Path.Combine(Paths.TmpDir, "x_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(work);
            var (ok, err) = ExtractArchive(archive, work);
            if (!ok) return Fail(err);

            var descPath = FindDescriptor(work);
            string srcRoot;
            Dictionary<string, object> desc;
            if (descPath.Length > 0)
            {
                srcRoot = Path.GetDirectoryName(descPath)!;
                desc = Pdx.ParseFile(descPath);
            }
            else
            {
                // 兜底：包里只放了 .mod 描述文件（作者打包成「文件夹 + 描述文件」）
                var hit = Directory.EnumerateFiles(work, "*.mod", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (hit is null)
                {
                    var listing = Directory.EnumerateFileSystemEntries(work)
                        .Select(Path.GetFileName)
                        .Where(x => x is not null && !x.StartsWith('.'))
                        .Take(6)
                        .ToArray();
                    if (listing.Length == 0)
                        return Fail("解压出来是空的——这个压缩包可能已损坏、加密，或者是分卷包的一部分");
                    return Fail("这个包里找不到 descriptor.mod，不像一个 mod（顶层内容：" +
                                string.Join("、", listing) + "）");
                }
                srcRoot = Path.GetDirectoryName(hit)!;
                desc = Pdx.ParseFile(hit);
            }

            // 目录名：优先用包里的顶层文件夹名，其次用 mod 名 / 压缩包名
            var rel = Path.GetRelativePath(work, srcRoot);
            var baseName = rel is "." or "" ? (Pdx.Str(desc, "name") ?? stem) : rel.Split('\\', '/')[0];
            var dirname = SafeDirName(baseName, "local_" + DateTime.Now.ToString("yyyyMMddHHmmss"));

            var target = Path.Combine(modDir, dirname);
            for (var n = 2; Directory.Exists(target) || File.Exists(target); n++)
                target = Path.Combine(modDir, $"{dirname}_{n}");
            dirname = Path.GetFileName(target);

            try
            {
                if (string.Equals(Path.GetFullPath(srcRoot).TrimEnd('\\'),
                        Path.GetFullPath(work).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    CopyDirectory(work, target);
                else
                    Directory.Move(srcRoot, target);
            }
            catch (Exception ex) { return Fail("复制到 mod 目录失败：" + ex.Message); }

            // 生成描述文件 mod/<名字>.mod
            var gver = cfg.GameStr(cfg.CurrentGame, "gameVersion") is { Length: > 0 } gv ? gv : "1.20";
            var lines = new List<string> { $"name=\"{Pdx.Str(desc, "name") ?? (stem.Length > 0 ? stem : dirname)}\"" };
            if (Pdx.Str(desc, "version") is { Length: > 0 } ver) lines.Add($"version=\"{ver}\"");
            if (Pdx.Str(desc, "supported_version") is { Length: > 0 } sup) lines.Add($"supported_version=\"{sup}\"");
            else lines.Add($"supported_version=\"{gver.Split('.')[0]}.*\"");
            foreach (var fn in new[] { "thumbnail.png", "thumbnail.jpg", "picture.png" })
            {
                if (File.Exists(Path.Combine(target, fn))) { lines.Add($"picture=\"{fn}\""); break; }
            }
            if (Pdx.Str(desc, "short_description") is { Length: > 0 } sdesc)
                lines.Add($"short_description=\"{sdesc.Replace('"', '\'')}\"");
            var deps = AsList(desc, "dependencies");
            if (deps.Count > 0)
                lines.Add("dependencies={\n" + string.Concat(deps.Select(d => $"\t\"{d}\"\n")) + "}");
            var tagList = AsList(desc, "tags");
            if (tagList.Count > 0)
                lines.Add("tags={\n" + string.Concat(tagList.Select(t => $"\t\"{t}\"\n")) + "}");
            lines.Add($"path=\"{Path.GetFullPath(target).Replace('\\', '/')}\"");

            var modFile = Path.Combine(modDir, dirname + ".mod");
            File.WriteAllText(modFile, string.Join("\n", lines) + "\n", new UTF8Encoding(false));

            return new JsonObject
            {
                ["ok"] = true,
                ["name"] = Pdx.Str(desc, "name") ?? dirname,
                ["dir"] = target,
                ["modFile"] = Path.GetFileName(modFile),
                ["renamed"] = !string.Equals(dirname, SafeDirName(baseName, ""), StringComparison.Ordinal),
            };
        }
        catch (Exception ex) { return Fail("安装失败：" + ex.Message); }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    private static List<string> AsList(Dictionary<string, object> d, string key)
    {
        if (!d.TryGetValue(key, out var v)) return new List<string>();
        if (v is List<string> l) return l;
        if (v is string s && s.Length > 0) return new List<string> { s };
        return new List<string>();
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.EnumerateDirectories(src))
            CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    /// <summary>把名字清成 ASCII 安全的目录名（CK3 对非 ASCII 路径兼容性差）。</summary>
    private static string SafeDirName(string name, string fallback)
    {
        var s = Regex.Replace((name ?? "").Trim(), @"[^0-9A-Za-z_\-]+", "_").Trim('_');
        s = Regex.Replace(s, "_+", "_");
        if (s.Length > 64) s = s[..64].TrimEnd('_');
        return s.Length > 0 ? s : fallback;
    }

    private static string ArchiveKind(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[8];
            var n = fs.Read(head, 0, head.Length);
            if (n >= 4 && head[0] == 'P' && head[1] == 'K' &&
                (head[2] == 3 || head[2] == 5 || head[2] == 7)) return "zip";
            if (n >= 6 && head[0] == 0x37 && head[1] == 0x7A && head[2] == 0xBC &&
                head[3] == 0xAF && head[4] == 0x27 && head[5] == 0x1C) return "7z";
            if (n >= 6 && head[0] == 'R' && head[1] == 'a' && head[2] == 'r' &&
                head[3] == '!' && head[4] == 0x1A && head[5] == 0x07) return "rar";
        }
        catch { }
        return "";
    }

    private static (bool Ok, string Error) ExtractArchive(string archive, string dest)
    {
        Directory.CreateDirectory(dest);
        var kind = ArchiveKind(archive);
        if (kind.Length == 0)
            return (false, "这不是有效的压缩包（zip / 7z / rar），可能已损坏或扩展名被改过");

        if (kind == "zip")
        {
            try
            {
                var root = Path.GetFullPath(dest).TrimEnd('\\') + "\\";
                using var z = ZipFile.OpenRead(archive);
                foreach (var entry in z.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries)
                        .Where(p => p is not ("." or "..")).ToArray();
                    if (parts.Length == 0) continue;
                    var target = Path.Combine(new[] { root }.Concat(parts).ToArray());
                    // 防 zip slip：解出来的路径必须落在 dest 里
                    if (!Path.GetFullPath(target).StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.EndsWith('/'))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var src = entry.Open();
                    using var outFs = File.Create(target);
                    src.CopyTo(outFs);
                }
                return (true, "");
            }
            catch (InvalidDataException ex)
            {
                return (false, "解压失败：" + ex.Message);
            }
            catch (Exception ex)
            {
                return (false, "解压失败：" + ex.Message);
            }
        }

        var (tool, tkind) = PickExtractor();
        if (tool.Length == 0)
            return (false, $"这台机器没有可用的解压工具，解不了 .{kind}（可以先手动解压成文件夹）");

        var args = tkind switch
        {
            "unrar" => new[] { "x", "-y", "-p-", archive, dest + "\\" },
            "bsdtar" => new[] { "-xf", archive, "-C", dest },
            _ => new[] { "x", "-y", "-p", "-o" + dest, archive },
        };
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = tool,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            try { p.StandardInput.Close(); } catch { }   // 绝不让它等键盘输入
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(300_000))
            {
                try { p.Kill(true); } catch { }
                return (false, "解压超时（超过 5 分钟），这个包可能太大、加密或已损坏");
            }
            if (p.ExitCode is not (0 or 1))
            {
                var msg = (stdout + stderr).Trim();
                var low = msg.ToLowerInvariant();
                if (low.Contains("password") || low.Contains("encrypted") || msg.Contains("加密"))
                    return (false, "这个压缩包设了密码，解不开（请提供未加密的包）");
                return (false, "解压失败：" + (msg.Length > 0 ? msg[^Math.Min(msg.Length, 300)..] : $"返回码 {p.ExitCode}"));
            }
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, "解压失败：" + ex.Message);
        }
    }

    private static (string Tool, string Kind) PickExtractor()
    {
        var cands = new[]
        {
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe",
            @"C:\Program Files\WinRAR\UnRAR.exe",
            @"C:\Program Files (x86)\WinRAR\UnRAR.exe",
        };
        foreach (var c in cands)
        {
            if (!File.Exists(c)) continue;
            return (c, c.EndsWith("unrar.exe", StringComparison.OrdinalIgnoreCase) ? "unrar" : "7z");
        }

        foreach (var exe in new[] { "7z.exe", "7za.exe", "unrar.exe" })
        {
            var p = Which(exe);
            if (p.Length > 0)
                return (p, exe.StartsWith("unrar", StringComparison.OrdinalIgnoreCase) ? "unrar" : "7z");
        }

        // Windows 10/11 自带的 tar.exe 是 bsdtar，原生支持解 zip/7z/rar
        var sysRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var tar = Path.Combine(sysRoot, "System32", "tar.exe");
        if (File.Exists(tar)) return (tar, "bsdtar");
        var whichTar = Which("bsdtar");
        return (whichTar, whichTar.Length > 0 ? "bsdtar" : "");
    }

    private static string Which(string exe)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var cand = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(cand)) return cand;
            }
        }
        catch { }
        return "";
    }

    // ------------------------------------------------------------ 打包导出

    /// <summary>打包前的统计结果（不写文件，所以很快）。</summary>
    public sealed record PackInfo(bool Ok, string Error, string Name, string Folder,
        int FileCount, long SizeBytes, string ContentDir, string ModFile);

    private static readonly string[] PackSkipDirs = { ".git", "__pycache__", ".idea" };
    private static readonly string[] PackSkipFiles = { ".DS_Store", "Thumbs.db" };

    /// <summary>旧版 pack_mod_archive 的等价物：统计体量 + 内容目录。</summary>
    public static PackInfo PreparePack(ConfigStore cfg, string modFile)
    {
        var userDir = (cfg.GameStr(cfg.CurrentGame, "userDir") ?? "").Trim();
        if (userDir.Length == 0)
            return new PackInfo(false, "未设置用户数据目录", "", "", 0, 0, "", "");

        var modDir = Path.Combine(userDir, "mod");
        string full;
        try { full = Path.GetFullPath(Path.Combine(userDir, modFile.Replace('/', '\\'))); }
        catch { return new PackInfo(false, "非法路径", "", "", 0, 0, "", ""); }
        if (!full.StartsWith(Path.GetFullPath(modDir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            return new PackInfo(false, "非法路径", "", "", 0, 0, "", "");
        if (!File.Exists(full))
            return new PackInfo(false, "描述文件不存在", "", "", 0, 0, "", "");

        var desc = Pdx.ParseFile(full);
        var pathAttr = (Pdx.Str(desc, "path") ?? "").Trim();
        if (pathAttr.Length == 0)
            return new PackInfo(false, "这个 .mod 里没有 path，找不到内容目录", "", "", 0, 0, "", "");

        string contentDir;
        try
        {
            contentDir = Path.GetFullPath(Path.IsPathRooted(pathAttr)
                ? pathAttr : Path.Combine(userDir, pathAttr.Replace('/', '\\')));
        }
        catch { return new PackInfo(false, "内容目录路径非法", "", "", 0, 0, "", ""); }
        if (!Directory.Exists(contentDir))
            return new PackInfo(false, "内容目录不存在：" + contentDir, "", "", 0, 0, "", "");

        var folder = Path.GetFileName(contentDir.TrimEnd('\\', '/'));
        var safeFolder = SafeDirName(folder, "mod_" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        var modName = Pdx.Str(desc, "name") is { Length: > 0 } nm ? nm : safeFolder;

        var count = 0;
        long total = 0;
        foreach (var (path, _) in EnumerateContent(contentDir))
        {
            count++;
            try { total += new FileInfo(path).Length; } catch { }
        }
        if (count == 0)
            return new PackInfo(false, "内容目录是空的，没什么可打包的", "", "", 0, 0, "", "");

        return new PackInfo(true, "", modName, safeFolder, count, total, contentDir, full);
    }

    private static IEnumerable<(string Full, string Rel)> EnumerateContent(string contentDir)
    {
        var stack = new Stack<string>();
        stack.Push(contentDir);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); } catch { continue; }
            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                if (PackSkipFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                yield return (f, Path.GetRelativePath(contentDir, f).Replace('\\', '/'));
            }
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir); } catch { continue; }
            foreach (var sub in subs)
            {
                if (PackSkipDirs.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) continue;
                stack.Push(sub);
            }
        }
    }

    /// <summary>
    /// 按「拖入安装」兼容的结构写 zip：
    ///   &lt;安全目录名&gt;/ ...全部内容... + &lt;安全目录名&gt;.mod（path 写成相对的 mod/xxx，便携）
    /// </summary>
    public static JsonObject WritePack(ConfigStore cfg, string modFile, string destZip,
        Action<int, int, string>? progress)
    {
        if (string.IsNullOrWhiteSpace(destZip)) return Fail("缺少保存路径");

        var info = PreparePack(cfg, modFile);
        if (!info.Ok) return Fail(info.Error);

        var desc = Pdx.ParseFile(info.ModFile);
        var lines = new List<string> { $"name=\"{Pdx.Str(desc, "name") ?? info.Folder}\"" };
        if (Pdx.Str(desc, "version") is { Length: > 0 } ver) lines.Add($"version=\"{ver}\"");
        if (Pdx.Str(desc, "supported_version") is { Length: > 0 } sup) lines.Add($"supported_version=\"{sup}\"");
        foreach (var fn in new[] { "thumbnail.png", "thumbnail.jpg", "picture.png" })
        {
            if (File.Exists(Path.Combine(info.ContentDir, fn))) { lines.Add($"picture=\"{fn}\""); break; }
        }
        if (Pdx.Str(desc, "short_description") is { Length: > 0 } sdesc)
            lines.Add($"short_description=\"{sdesc.Replace('"', '\'')}\"");
        var tags = Pdx.List(desc, "tags");
        if (tags.Count > 0)
            lines.Add("tags={\n" + string.Concat(tags.Select(t => $"\t\"{t}\"\n")) + "}");
        lines.Add($"path=\"mod/{info.Folder}\"");
        var modText = string.Join("\n", lines) + "\n";

        var totalFiles = info.FileCount + 1;   // +1：顶层 .mod
        var written = 0;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destZip))!);
            using (var zip = ZipFile.Open(destZip, ZipArchiveMode.Create))
            {
                foreach (var (path, rel) in EnumerateContent(info.ContentDir))
                {
                    var entry = zip.CreateEntry($"{info.Folder}/{rel}", CompressionLevel.Optimal);
                    using var src = File.OpenRead(path);
                    using var dst = entry.Open();
                    src.CopyTo(dst);
                    written++;
                    // 每 6 个文件报一次，既让进度条动起来又不至于刷爆
                    if (progress is not null && written % 6 == 0) progress(written, totalFiles, rel);
                }

                var modEntry = zip.CreateEntry($"{info.Folder}.mod", CompressionLevel.Optimal);
                using (var w = new StreamWriter(modEntry.Open(), new UTF8Encoding(false)))
                    w.Write(modText);
                written++;
                progress?.Invoke(totalFiles, totalFiles, $"{info.Folder}.mod");
            }
        }
        catch (Exception ex)
        {
            try { File.Delete(destZip); } catch { }
            return Fail("打包失败：" + ex.Message);
        }

        long size = 0;
        try { size = new FileInfo(destZip).Length; } catch { }
        return new JsonObject
        {
            ["ok"] = true,
            ["path"] = destZip,
            ["name"] = info.Name,
            ["fileCount"] = written,
            ["sizeBytes"] = size,
        };
    }

    /// <summary>在解压目录里找最浅的 descriptor.mod（mod 内容根）。</summary>
    private static string FindDescriptor(string root, int maxDepth = 4)
    {
        var best = "";
        var bestDepth = int.MaxValue;

        void Walk(string dir, int depth)
        {
            if (depth >= maxDepth) return;
            try
            {
                if (depth < bestDepth &&
                    Directory.EnumerateFiles(dir, "descriptor.mod").Any())
                {
                    best = Path.Combine(dir, "descriptor.mod");
                    bestDepth = depth;
                }
                foreach (var sub in Directory.EnumerateDirectories(dir)) Walk(sub, depth + 1);
            }
            catch { }
        }

        try { Walk(root, 0); } catch { }
        return best;
    }
}

/// <summary>打包进度（前端每 200ms 轮询 /api/mod/pack/progress）。</summary>
public static class ModPack
{
    private static readonly object Lock = new();
    private static string _phase = "idle";
    private static int _done, _total;
    private static string _cur = "", _msg = "";
    private static bool? _ok;

    public static void Reset()
    {
        lock (Lock) { _phase = "idle"; _done = 0; _total = 0; _cur = ""; _ok = null; _msg = ""; }
    }

    public static void MarkChoosing(string msg = "正在选择保存位置")
    {
        lock (Lock) { _phase = "choose"; _done = 0; _cur = ""; _ok = null; _msg = msg; }
    }

    public static void MarkReady(int total)
    {
        lock (Lock) { _phase = "ready"; _done = 0; _total = total; _cur = ""; _ok = null; _msg = ""; }
    }

    public static void MarkPacking(int total, string msg = "正在压缩")
    {
        lock (Lock) { _phase = "packing"; _done = 0; _total = total; _cur = ""; _ok = null; _msg = msg; }
    }

    public static void Progress(int done, int total, string cur)
    {
        lock (Lock) { _done = done; _total = total; _cur = cur; }
    }

    public static void MarkDone(int count)
    {
        lock (Lock) { _phase = "done"; _done = count; _total = count; _cur = ""; _ok = true; _msg = "打包完成"; }
    }

    public static void MarkError(string msg)
    {
        lock (Lock) { _phase = "error"; _ok = false; _msg = msg; }
    }

    public static JsonObject Snapshot()
    {
        lock (Lock)
        {
            var obj = new JsonObject
            {
                ["phase"] = _phase,
                ["done"] = _done,
                ["total"] = _total,
                ["cur"] = _cur,
                ["msg"] = _msg,
            };
            obj["ok"] = _ok is null ? null : JsonValue.Create(_ok.Value);
            return obj;
        }
    }
}
