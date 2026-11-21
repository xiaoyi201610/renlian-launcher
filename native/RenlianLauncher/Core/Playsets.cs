using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace RenlianLauncher.Core;

/// <summary>
/// 播放集数据库层：直接读写 P 社官方启动器的 launcher-v2.sqlite。
///
/// 与旧版 LauncherDB 的差异（都是有意为之）：
///   · 读操作一律用只读连接，绝不触发建表/写事务 —— 游戏开着也能安全浏览播放集
///   · 写操作才建表（IF NOT EXISTS，官方库零影响）、开事务，被游戏占用时抛
///     <see cref="PlaysetLockedException"/>，由 API 层转成友好提示
///   · 库不存在时列播放集返回空列表，不会凭空建库（新建播放集才会建，与旧版一致）
/// </summary>
public sealed class PlaysetDb
{
    public const string LockedMessage = "数据库被游戏占用，请先关闭游戏";

    public string UserDir { get; }
    public string Game { get; }
    public string DbPath { get; }

    // 旧版建表语句原样搬过来：老游戏（如 CK2）官方启动器可能从没建过表
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS playsets(
          id TEXT PRIMARY KEY, name TEXT, isActive INTEGER, loadOrder TEXT,
          createdOn TEXT, updatedOn TEXT, isRemoved INTEGER, state TEXT, owned INTEGER,
          author TEXT, subscribersCount INTEGER, ratingsCount INTEGER, description TEXT,
          offDisk INTEGER);
        CREATE TABLE IF NOT EXISTS playsets_mods(
          playsetId TEXT, modId TEXT, enabled INTEGER, position INTEGER);
        CREATE TABLE IF NOT EXISTS playsets_dlcs(
          playsetId TEXT, dlcId TEXT, enabled INTEGER);
        CREATE TABLE IF NOT EXISTS mods(
          id TEXT PRIMARY KEY, steamId TEXT, name TEXT, displayName TEXT, version TEXT,
          dirPath TEXT, archivePath TEXT, source TEXT, status TEXT, thumbnailPath TEXT,
          timeUpdated INTEGER, tags TEXT, isMetadataApplied INTEGER, metadataStatus TEXT);
        """;

    private static readonly HashSet<string> SchemaReady = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SchemaLock = new();
    private static readonly Dictionary<string, DateTime> BackupStamp = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object BackupLock = new();

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public PlaysetDb(string userDir, string game)
    {
        UserDir = userDir ?? "";
        Game = string.IsNullOrWhiteSpace(game) ? "ck3" : game;
        var primary = Path.Combine(UserDir, "launcher-v2.sqlite");
        var alt = Path.Combine(UserDir, "launcher.sqlite");
        // 个别老安装只有 launcher.sqlite：兼容读取
        DbPath = File.Exists(primary) ? primary : File.Exists(alt) ? alt : primary;
    }

    public static PlaysetDb ForConfig(ConfigStore cfg)
    {
        var game = cfg.CurrentGame;
        return new PlaysetDb(cfg.GameStr(game, "userDir") ?? "", game);
    }

    /// <summary>userDir 为空时路径是相对的，必须当作「没有库」，否则会写错地方。</summary>
    public bool Exists => UserDir.Length > 0 && File.Exists(DbPath);

    // ------------------------------------------------------------ 连接

    private SqliteConnection Open(bool writable, int timeout = 8, bool ensureSchema = true)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = writable ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
            DefaultTimeout = timeout,
        }.ToString();
        var conn = new SqliteConnection(cs);
        conn.Open();
        if (writable && ensureSchema) EnsureSchema(conn);
        return conn;
    }

    private void EnsureSchema(SqliteConnection conn)
    {
        lock (SchemaLock)
        {
            if (SchemaReady.Contains(DbPath)) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = SchemaSql;
            cmd.ExecuteNonQuery();
            SchemaReady.Add(DbPath);
        }
    }

    /// <summary>只读查询统一入口：任何异常都退回兜底值，绝不影响界面。</summary>
    private T Read<T>(Func<SqliteConnection, T> fn, T fallback)
    {
        if (!Exists) return fallback;
        try
        {
            using var conn = Open(false);
            return fn(conn);
        }
        catch { return fallback; }
    }

    private static bool IsBusy(SqliteException ex) => (ex.SqliteErrorCode & 0xFF) is 5 or 6;

    // ------------------------------------------------------------ 播放集读取

    public List<PlaysetSummary> ListPlaysets() => Read(c =>
    {
        var list = new List<PlaysetSummary>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT p.id, p.name, p.isActive, p.updatedOn, p.createdOn,
                   (SELECT COUNT(*) FROM playsets_mods pm WHERE pm.playsetId = p.id) AS total,
                   (SELECT COUNT(*) FROM playsets_mods pm WHERE pm.playsetId = p.id AND pm.enabled = 1) AS enabled
            FROM playsets p WHERE p.isRemoved = 0 ORDER BY p.isActive DESC, p.name
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new PlaysetSummary(
                Text(r, 0) ?? "",
                Text(r, 1) ?? "",
                Num(r, 2),
                Text(r, 3),
                Text(r, 4),
                Num(r, 5) ?? 0,
                Num(r, 6) ?? 0));
        }
        return list;
    }, new List<PlaysetSummary>());

    public string? ActivePlayset() => Read(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM playsets WHERE isActive = 1 AND isRemoved = 0 LIMIT 1";
        using var r = cmd.ExecuteReader();
        return r.Read() ? Text(r, 0) : null;
    }, null);

    public PlaysetDetail? GetPlayset(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return Read(c =>
        {
            string? name; bool isActive;
            using (var head = c.CreateCommand())
            {
                head.CommandText = "SELECT id,name,isActive FROM playsets WHERE id=$id";
                head.Parameters.AddWithValue("$id", id);
                using var r = head.ExecuteReader();
                if (!r.Read()) return null;
                name = Text(r, 1);
                isActive = ToBool(r.GetValue(2));
            }

            var mods = new List<PlaysetModRow>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT pm.modId, pm.enabled, pm.position,
                           m.name, m.displayName, m.steamId, m.dirPath, m.archivePath, m.source, m.status
                    FROM playsets_mods pm JOIN mods m ON m.id = pm.modId
                    WHERE pm.playsetId = $id ORDER BY pm.position
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    mods.Add(new PlaysetModRow(
                        Text(r, 0) ?? "",
                        ToBool(r.GetValue(1), true),
                        Num(r, 2),
                        Text(r, 3),
                        Text(r, 4),
                        Text(r, 5),
                        Text(r, 6),
                        Text(r, 7),
                        Text(r, 8),
                        Text(r, 9)));
                }
            }

            var dlcs = new Dictionary<string, bool>(StringComparer.Ordinal);
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT dlcId, enabled FROM playsets_dlcs WHERE playsetId=$id";
                cmd.Parameters.AddWithValue("$id", id);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var dlcId = Text(r, 0);
                    if (!string.IsNullOrEmpty(dlcId)) dlcs[dlcId] = ToBool(r.GetValue(1));
                }
            }

            return new PlaysetDetail(id, name ?? "", isActive, mods, dlcs);
        }, null);
    }

    /// <summary>官方启动器运行时会长期持锁，尝试拿独占锁判断。</summary>
    public bool IsLocked()
    {
        if (!Exists) return false;
        try
        {
            using var conn = Open(true, 0, ensureSchema: false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "BEGIN EXCLUSIVE";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "ROLLBACK";
            cmd.ExecuteNonQuery();
            return false;
        }
        catch (SqliteException ex) when (IsBusy(ex)) { return true; }
        catch { return false; }
    }

    // ------------------------------------------------------------ mod 注册表

    public List<ModRecord> ModRecords() => Read(c =>
    {
        var list = new List<ModRecord>();
        var hasRegistry = false;
        using (var pragma = c.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(mods)";
            using var pr = pragma.ExecuteReader();
            while (pr.Read())
            {
                if (string.Equals(Text(pr, 1), "gameRegistryId", StringComparison.OrdinalIgnoreCase))
                    hasRegistry = true;
            }
        }

        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT id, steamId, name, displayName, version, dirPath, archivePath, source" +
            (hasRegistry ? ", gameRegistryId" : "") + " FROM mods";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ModRecord(
                Text(r, 0) ?? "",
                Text(r, 1),
                Text(r, 2),
                Text(r, 3),
                Text(r, 4),
                Text(r, 5),
                Text(r, 6),
                Text(r, 7),
                hasRegistry ? Text(r, 8) : null));
        }
        return list;
    }, new List<ModRecord>());

    /// <summary>modId(uuid) → dlc_load.json 里的 mod 相对路径（"mod/xxx.mod"）。</summary>
    public Dictionary<string, string> ModIdToFileMap(IReadOnlyList<JsonObject>? index)
    {
        var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in index ?? Array.Empty<JsonObject>())
        {
            var dir = ConfigStore.Str(it, "dirPath");
            var file = ConfigStore.Str(it, "modFile");
            if (!string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(file))
                byPath[NormKey(dir!)] = file!;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in ModRecords())
        {
            var reg = (r.GameRegistryId ?? "").Replace('\\', '/').Trim();
            if (reg.StartsWith("mod/", StringComparison.OrdinalIgnoreCase) &&
                reg.EndsWith(".mod", StringComparison.OrdinalIgnoreCase))
            {
                map[r.Id] = reg;
                continue;
            }

            var dp = (r.DirPath ?? "").TrimEnd('\\', '/');
            if (dp.Length > 0 && byPath.TryGetValue(NormKey(dp), out var hit))
            {
                map[r.Id] = hit;
                continue;
            }

            var sid = (r.SteamId ?? "").Trim();
            var src = (r.Source ?? "").Trim().ToLowerInvariant();
            var baseName = dp.Length > 0 ? Path.GetFileName(dp) : "";
            if (sid.Length == 0 && baseName.Length > 0 && baseName.All(char.IsDigit))
                sid = baseName;
            if (sid.Length > 0 && src != "local")
            {
                map[r.Id] = $"mod/ugc_{sid}.mod";
                continue;
            }
            if (baseName.Length > 0)
            {
                map[r.Id] = $"mod/{baseName}.mod";
                continue;
            }
            var nm = (r.Name ?? "").Trim();
            if (nm.Length > 0) map[r.Id] = $"mod/{nm}.mod";
        }
        return map;
    }

    // ------------------------------------------------------------ 播放集写入

    public string CreatePlayset(string name, string? cloneFrom)
    {
        var newId = Guid.NewGuid().ToString();
        BackupDb();
        try
        {
            using var conn = Open(true);
            using var tx = conn.BeginTransaction();
            Exec(conn, tx, """
                INSERT INTO playsets (id, name, isActive, loadOrder, createdOn, isRemoved,
                                      state, owned, author, subscribersCount, ratingsCount, description, offDisk)
                VALUES ($id, $name, 0, 'custom', datetime('now'), 0, 'private', 1, '', 0, 0, '', 0)
                """, ("$id", newId), ("$name", name));

            if (!string.IsNullOrWhiteSpace(cloneFrom))
            {
                Exec(conn, tx, """
                    INSERT INTO playsets_mods (playsetId, modId, enabled, position)
                    SELECT $new, modId, enabled, position FROM playsets_mods
                    WHERE playsetId = $src ORDER BY position
                    """, ("$new", newId), ("$src", cloneFrom));
                Exec(conn, tx, """
                    INSERT INTO playsets_dlcs (playsetId, dlcId, enabled)
                    SELECT $new, dlcId, enabled FROM playsets_dlcs WHERE playsetId = $src
                    """, ("$new", newId), ("$src", cloneFrom));
            }
            tx.Commit();
            return newId;
        }
        catch (SqliteException ex) when (IsBusy(ex)) { throw new PlaysetLockedException(); }
    }

    public void RenamePlayset(string id, string name)
    {
        if (!Exists) throw new PlaysetDbException("找不到启动器数据库（launcher-v2.sqlite）");
        BackupDb();
        try
        {
            using var conn = Open(true);
            using var tx = conn.BeginTransaction();
            Exec(conn, tx, "UPDATE playsets SET name=$name WHERE id=$id", ("$name", name), ("$id", id));
            tx.Commit();
        }
        catch (SqliteException ex) when (IsBusy(ex)) { throw new PlaysetLockedException(); }
    }

    public void DeletePlayset(string id)
    {
        if (!Exists) throw new PlaysetDbException("找不到启动器数据库（launcher-v2.sqlite）");
        BackupDb();
        try
        {
            using var conn = Open(true);
            using var tx = conn.BeginTransaction();
            Exec(conn, tx, "DELETE FROM playsets_mods WHERE playsetId=$id", ("$id", id));
            Exec(conn, tx, "DELETE FROM playsets_dlcs WHERE playsetId=$id", ("$id", id));
            Exec(conn, tx, "DELETE FROM playsets WHERE id=$id", ("$id", id));
            tx.Commit();
        }
        catch (SqliteException ex) when (IsBusy(ex)) { throw new PlaysetLockedException(); }
    }

    public void SetActive(string id)
    {
        if (!Exists) throw new PlaysetDbException("找不到启动器数据库（launcher-v2.sqlite）");
        BackupDb();
        try
        {
            using var conn = Open(true);
            using var tx = conn.BeginTransaction();
            Exec(conn, tx, "UPDATE playsets SET isActive = 0 WHERE isActive = 1");
            Exec(conn, tx, "UPDATE playsets SET isActive = 1 WHERE id = $id", ("$id", id));
            tx.Commit();
        }
        catch (SqliteException ex) when (IsBusy(ex)) { throw new PlaysetLockedException(); }
    }

    /// <summary>整表重写播放集内的 mod 顺序/开关，并按需同步游戏启停清单。</summary>
    public WriteResult WritePlayset(string psid, List<ModWrite> items,
        Dictionary<string, bool>? dlcMap, IReadOnlyList<JsonObject>? index)
    {
        if (!Exists) throw new PlaysetDbException("找不到启动器数据库（launcher-v2.sqlite）");
        var bak = BackupDb();
        try
        {
            using (var conn = Open(true))
            using (var tx = conn.BeginTransaction())
            {
                Exec(conn, tx, "DELETE FROM playsets_mods WHERE playsetId=$id", ("$id", psid));
                for (var pos = 0; pos < items.Count; pos++)
                {
                    var it = items[pos];
                    var modId = it.ModId;
                    // 若前端带回的 modId 指向坏记录（invalid_mod/unsubscribed），不要沿用：
                    // 否则官方导入播放集时会因"坏记录 + 重排数组"不匹配而报错。
                    if (!string.IsNullOrWhiteSpace(modId) && !IsGoodMod(conn, tx, modId))
                    {
                        // 优先用坏记录自身的 steamId 反查同 id 的好记录并重绑
                        var rebound = RebindGoodMod(conn, tx, modId);
                        if (rebound is not null) modId = rebound;
                        else if (it.Rec is not null
                            && ((ConfigStore.Str(it.Rec, "steamId") ?? "").Trim().Length > 0
                                || (ConfigStore.Str(it.Rec, "dirPath") ?? "").Trim().Length > 0))
                            modId = null; // 有 Rec 可重建
                        // 都没有则保留原 id（写入后仍是坏引用，但至少不制造空记录）
                    }
                    if (string.IsNullOrWhiteSpace(modId)) modId = EnsureMod(conn, tx, it.Rec);
                    Exec(conn, tx, """
                        INSERT INTO playsets_mods (playsetId, modId, enabled, position)
                        VALUES ($ps, $mod, $en, $pos)
                        """,
                        ("$ps", psid), ("$mod", modId), ("$en", it.Enabled ? 1 : 0), ("$pos", pos));
                }

                if (dlcMap is not null)
                {
                    Exec(conn, tx, "DELETE FROM playsets_dlcs WHERE playsetId=$id", ("$id", psid));
                    foreach (var kv in dlcMap)
                        Exec(conn, tx,
                            "INSERT INTO playsets_dlcs (playsetId, dlcId, enabled) VALUES ($ps, $dlc, $en)",
                            ("$ps", psid), ("$dlc", kv.Key), ("$en", kv.Value ? 1 : 0));
                }

                Exec(conn, tx, "UPDATE playsets SET updatedOn=datetime('now') WHERE id=$id", ("$id", psid));
                tx.Commit();
            }
        }
        catch (SqliteException ex) when (IsBusy(ex)) { throw new PlaysetLockedException(); }

        // 只有当前激活的播放集才写游戏启停清单，避免污染游戏实际读取的列表
        var synced = false;
        if (ActivePlayset() == psid)
            synced = SyncLoadFile(psid, index).Written;
        return new WriteResult(bak, synced);
    }

    /// <summary>确保 mod 在注册表里存在，返回 modId；不存在则新建（同一事务内）。</summary>
    private static string EnsureMod(SqliteConnection conn, SqliteTransaction tx, JsonObject? rec)
    {
        var steamId = (ConfigStore.Str(rec, "steamId") ?? "").Trim();
        var dirPath = (ConfigStore.Str(rec, "dirPath") ?? "").Trim();
        var source = (ConfigStore.Str(rec, "source") ?? "").Trim().ToLowerInvariant();
        var isLocal = source == "local";

        // 同一个 steamId 在官方库里可能存在多条记录：旧的 invalid_mod/unsubscribed 残留 +
        // 新的 ready_to_play。必须优先命中 ready_to_play，否则播放集会引用到坏记录，
        // 官方启动器导入时就会报 "array with new mods order is incompatible ..."。
        // 排序规则：ready_to_play 优先，其次排除明显的坏状态，最后用 rowid 保证稳定。
        const string preferGood =
            " ORDER BY CASE WHEN status='ready_to_play' THEN 0 WHEN status IS NULL THEN 1 ELSE 2 END, rowid LIMIT 1";
        string? found = null;
        if (steamId.Length > 0 && !isLocal)
            found = Scalar(conn, tx, "SELECT id FROM mods WHERE steamId=$v" + preferGood, steamId);
        if (found is null && dirPath.Length > 0)
            found = Scalar(conn, tx, "SELECT id FROM mods WHERE dirPath=$v" + preferGood, dirPath);
        if (found is null && steamId.Length > 0 && isLocal)
            found = Scalar(conn, tx, "SELECT id FROM mods WHERE steamId=$v AND source='local'" + preferGood, steamId);
        if (found is not null) return found;

        var newId = Guid.NewGuid().ToString();
        var name = ConfigStore.Str(rec, "name") ?? "";
        Exec(conn, tx, """
            INSERT INTO mods
              (id, steamId, name, displayName, version, dirPath, source, status,
               timeUpdated, tags, isMetadataApplied, metadataStatus)
            VALUES ($id, $sid, $name, $dn, $ver, $dir, $src, 'ready_to_play',
                    $ts, '[]', 0, 'not_applied')
            """,
            ("$id", newId),
            ("$sid", steamId.Length > 0 && !isLocal ? steamId : null),
            ("$name", name),
            ("$dn", ConfigStore.Str(rec, "displayName") is { Length: > 0 } dn ? dn : name),
            ("$ver", ConfigStore.Str(rec, "version") ?? ""),
            ("$dir", dirPath.Length > 0 ? dirPath : null),
            ("$src", source.Length > 0 ? source : "local"),
            ("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        return newId;
    }

    /// <summary>modId 是否指向一条"好"记录（存在且不是 invalid_mod/unsubscribed 坏状态）。</summary>
    private static bool IsGoodMod(SqliteConnection conn, SqliteTransaction tx, string modId)
    {
        var st = ScalarById(conn, tx, "SELECT status FROM mods WHERE id=$v", modId);
        // 记录不存在(null) 视为坏；ready_to_play 与 status 为空(旧库)视为好
        return st is not null && st is not ("invalid_mod" or "unsubscribed");
    }

    private static string? ScalarById(SqliteConnection conn, SqliteTransaction tx, string sql, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$v", value);
        var o = cmd.ExecuteScalar();
        return o is null or DBNull ? null : o.ToString();
    }

    /// <summary>坏 modId 若其 steamId 存在 ready_to_play 的同伴记录，返回同伴 id，否则 null。</summary>
    private static string? RebindGoodMod(SqliteConnection conn, SqliteTransaction tx, string badModId)
    {
        var sid = ScalarById(conn, tx, "SELECT steamId FROM mods WHERE id=$v", badModId);
        if (string.IsNullOrWhiteSpace(sid)) return null;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id FROM mods WHERE steamId=$v AND id<>$bad AND status='ready_to_play' ORDER BY rowid LIMIT 1";
        cmd.Parameters.AddWithValue("$v", sid);
        cmd.Parameters.AddWithValue("$bad", badModId);
        var o = cmd.ExecuteScalar();
        return o is null or DBNull ? null : o.ToString();
    }

    // ------------------------------------------------------------ 游戏启停清单

    /// <summary>把播放集里启用的 mod 按 position 写入游戏读取的启停清单。</summary>
    public SyncResult SyncLoadFile(string psid, IReadOnlyList<JsonObject>? index)
    {
        if (!Exists || UserDir.Length == 0) return new SyncResult(false, null);
        var ps = GetPlayset(psid);
        if (ps is null) return new SyncResult(false, null);

        if (GameDefs.ContentLoad.Contains(Game)) return SyncContentLoad(ps);

        var id2file = ModIdToFileMap(index);
        var enabled = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in ps.Mods)
        {
            if (!m.Enabled) continue;
            if (!id2file.TryGetValue(m.ModId, out var file) || string.IsNullOrWhiteSpace(file)) continue;
            if (!seen.Add(file)) continue;   // 同一条 .mod 加载两次没有意义
            enabled.Add(file);
        }
        return WriteDlcLoad(enabled);
    }

    private SyncResult WriteDlcLoad(List<string> enabledMods)
    {
        var path = Path.Combine(UserDir, "dlc_load.json");
        var bak = BackupFile(path, "dlc_load");
        var obj = new JsonObject
        {
            ["disabled_dlcs"] = new JsonArray(),
            ["enabled_mods"] = new JsonArray(
                enabledMods.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        };
        WriteJsonAtomic(path, obj);
        return new SyncResult(true, bak);
    }

    /// <summary>Vic3/EU5：按官方 1.1 格式写 content_load.json。</summary>
    private SyncResult SyncContentLoad(PlaysetDetail ps)
    {
        var path = Path.Combine(UserDir, "content_load.json");
        var old = ReadJsonObject(path);

        var enabled = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in ps.Mods)
        {
            if (!m.Enabled) continue;
            var cand = (m.DirPath ?? "").Trim();
            if (cand.Length == 0 || !(Directory.Exists(cand) || File.Exists(cand)))
                cand = (m.ArchivePath ?? "").Trim();
            if (cand.Length == 0 || !(Directory.Exists(cand) || File.Exists(cand))) continue;
            string full;
            try { full = Path.GetFullPath(cand); } catch { continue; }
            if (!seen.Add(full)) continue;
            enabled.Add(new JsonObject { ["path"] = full });
        }

        var disabled = new JsonArray();
        foreach (var kv in ps.Dlcs)
        {
            if (kv.Value) continue;
            disabled.Add(new JsonObject { ["paradoxAppId"] = kv.Key });
        }

        var data = new JsonObject
        {
            ["enabledMods"] = enabled,
            ["disabledDLC"] = disabled,
            ["enabledUGC"] = old?["enabledUGC"]?.DeepClone() ?? new JsonArray(),
        };
        if (old?["formatVersion"] is not null)
            data["formatVersion"] = old["formatVersion"]!.DeepClone();

        var bak = BackupFile(path, "content_load");
        WriteJsonAtomic(path, data);
        return new SyncResult(true, bak);
    }

    public (int EnabledMods, int DisabledDlcs) ReadLoadCounts()
    {
        if (UserDir.Length == 0) return (0, 0);
        if (GameDefs.ContentLoad.Contains(Game))
        {
            var d = ReadJsonObject(Path.Combine(UserDir, "content_load.json"));
            return (ArrayCount(d, "enabledMods"), ArrayCount(d, "disabledDLC"));
        }
        var j = ReadJsonObject(Path.Combine(UserDir, "dlc_load.json"));
        return (ArrayCount(j, "enabled_mods"), ArrayCount(j, "disabled_dlcs"));
    }

    // ------------------------------------------------------------ 备份

    /// <summary>写库前备份（20 秒内只备一次，避免连续保存刷屏）。</summary>
    private string? BackupDb()
    {
        if (!Exists) return null;
        var tag = Path.GetFileNameWithoutExtension(DbPath);
        return BackupFile(DbPath, tag, 20);
    }

    private static string? BackupFile(string src, string tag, int minInterval = 20)
    {
        try
        {
            if (!File.Exists(src)) return null;
            lock (BackupLock)
            {
                var key = tag + "|" + src;
                var now = DateTime.UtcNow;
                if (minInterval > 0 && BackupStamp.TryGetValue(key, out var last) &&
                    (now - last).TotalSeconds < minInterval)
                    return null;

                var dir = Paths.BackupDir;
                Directory.CreateDirectory(dir);
                var ext = Path.GetExtension(src);
                var stem = Path.Combine(dir, $"{tag}-{DateTime.Now:yyyyMMdd-HHmmss}");
                var dst = stem + ext;
                for (var n = 2; File.Exists(dst); n++) dst = $"{stem}-{n}{ext}";
                File.Copy(src, dst, true);
                BackupStamp[key] = now;
                PruneBackups(dir, tag);
                return dst;
            }
        }
        catch { return null; }
    }

    private static void PruneBackups(string dir, string tag)
    {
        try
        {
            var files = Directory.GetFiles(dir, tag + "-*")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Skip(30)
                .ToList();
            foreach (var f in files) { try { f.Delete(); } catch { } }
        }
        catch { }
    }

    // ------------------------------------------------------------ 小工具

    private static void Exec(SqliteConnection conn, SqliteTransaction tx,
        string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static string? Scalar(SqliteConnection conn, SqliteTransaction tx, string sql, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$v", value);
        var o = cmd.ExecuteScalar();
        return o is null or DBNull ? null : o.ToString();
    }

    private static string? Text(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        return r.GetValue(i)?.ToString();
    }

    private static long? Num(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        try { return Convert.ToInt64(r.GetValue(i)); } catch { return null; }
    }

    private static bool ToBool(object? v, bool fallback = false)
    {
        if (v is null or DBNull) return fallback;
        try
        {
            if (v is bool b) return b;
            if (v is string s) return s is "1" or "true" or "True";
            return Convert.ToInt64(v) != 0;
        }
        catch { return fallback; }
    }

    private static string NormKey(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return p.TrimEnd('\\', '/').ToLowerInvariant(); }
    }

    private static JsonObject? ReadJsonObject(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
        }
        catch { return null; }
    }

    private static int ArrayCount(JsonObject? obj, string name) =>
        obj?[name] is JsonArray a ? a.Count : 0;

    private static void WriteJsonAtomic(string path, JsonObject data)
    {
        var text = data.ToJsonString(WriteOpts);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed record PlaysetSummary(string Id, string Name, long? IsActive,
    string? UpdatedOn, string? CreatedOn, long Total, long Enabled);

public sealed record PlaysetModRow(string ModId, bool Enabled, long? Position,
    string? Name, string? DisplayName, string? SteamId,
    string? DirPath, string? ArchivePath, string? Source, string? Status);

public sealed record PlaysetDetail(string Id, string Name, bool IsActive,
    List<PlaysetModRow> Mods, Dictionary<string, bool> Dlcs);

public sealed record ModRecord(string Id, string? SteamId, string? Name,
    string? DisplayName, string? Version, string? DirPath, string? ArchivePath,
    string? Source, string? GameRegistryId);

/// <summary>要写进播放集的一项：已有 modId，或给一条新记录让 DB 层注册。</summary>
public sealed record ModWrite(string? ModId, JsonObject? Rec, bool Enabled);

public sealed record WriteResult(string? BackupPath, bool Synced);

public sealed record SyncResult(bool Written, string? BackupPath);

/// <summary>数据库被游戏/官方启动器占用。</summary>
public sealed class PlaysetLockedException : Exception
{
    public PlaysetLockedException() : base(PlaysetDb.LockedMessage) { }
}

/// <summary>可读的播放集错误（直接展示给玩家）。</summary>
public sealed class PlaysetDbException : Exception
{
    public PlaysetDbException(string message) : base(message) { }
}
