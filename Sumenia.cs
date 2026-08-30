// ===== Sumenia（苏暖泉）遥测服务 =====
// 独立于主程序 sipcore.cs 的遥测子系统；与 sipcore.cs 同为全局命名空间（无 namespace）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

// ══════════ Telemetry 服务（本地事实层）══════════
// 硬约束：默认关闭 / 首次征得同意 / 仅本地 / 绝不自动上传 / 可查看-关闭-删除-导出 /
//         记录事实不造画像 / 遥测损坏绝不影响 rss.db 与阅读（独立库 + 完整性检查 + 降级）
// 性能：事件白名单 + 内存缓冲批量写（50 条或 5 秒）+ 容量上限（10 万条留 8 万）
class TelemetryEvent
{
    public long Id { get; set; }
    public string Timestamp { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Type { get; set; } = "";
    public int? ArticleId { get; set; }
    public int? SourceId { get; set; }
    public int? VersionId { get; set; }
    public string? Surface { get; set; }
    public int? Position { get; set; }
    public string DataJson { get; set; } = "";
}

static class TelemetryService
{
    static string _dir = "";
    static string DbPath => Path.Combine(_dir, "telemetry.db");
    static string ConsentPath => Path.Combine(_dir, "telemetry_consent.json");
    static string CheckpointPath => Path.Combine(_dir, "telemetry_checkpoint.json");

    static string _consent = "unset";      // unset / enabled / disabled
    static bool _failed = false;           // 会话内连续写失败 → 降级停用
    static int _consecFails = 0;
    static string _sessionId = "";
    static readonly object _lock = new();
    static readonly List<TelemetryEvent> _buffer = new();
    static System.Threading.Timer? _flushTimer;

    public static bool IsEnabled => _consent == "enabled" && !_failed;
    public static string Consent => _consent;

    public static void Init(string dataDir)
    {
        _dir = dataDir;
        try { Directory.CreateDirectory(_dir); } catch { }
        LoadConsent();
        CheckIntegrity();
        _sessionId = Guid.NewGuid().ToString("N");
        try { _flushTimer = new System.Threading.Timer(_ => Flush(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)); } catch { }
    }

    public static void Shutdown()
    {
        try { _flushTimer?.Dispose(); } catch { }
        Flush();
        WriteCheckpoint("ok");
    }

    static void LoadConsent()
    {
        try
        {
            if (File.Exists(ConsentPath))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ConsentPath));
                if (d != null && d.TryGetValue("state", out var s) && (s == "enabled" || s == "disabled"))
                    _consent = s;
            }
        }
        catch { _consent = "unset"; }
    }

    public static void SetConsent(string state)
    {
        string oldState = _consent;
        _consent = state == "enabled" ? "enabled" : "disabled";
        try
        {
            File.WriteAllText(ConsentPath, JsonSerializer.Serialize(new
            {
                state = _consent,
                updatedAt = DateTime.Now.ToString("O")
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch { }
        // 同意变更审计：绕过 IsEnabled 门禁（disable/clear 后普通 Record 会丢弃），始终留痕
        if (oldState != _consent)
            AppendAudit("consent_change", new { action = _consent == "enabled" ? "enable" : "disable", from = oldState, to = _consent });
    }

    // 直接落一条审计/事实事件（不经缓冲与 IsEnabled 门禁），供同意变更等低概率重要事件留痕。
    // 独立连接 + busy_timeout，WAL 下与缓冲 Flush 并发安全；失败静默不影响功能。
    static void AppendAudit(string type, object data)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var c = conn.CreateCommand();
            c.CommandText = "PRAGMA busy_timeout = 2000;";
            c.ExecuteNonQuery();
            c.CommandText = @"INSERT INTO telemetry_events (timestamp, session_id, type, article_id, source_id, version_id, surface, position, data_json)
                VALUES (@ts, @sid, @type, NULL, NULL, NULL, NULL, NULL, @dj)";
            c.Parameters.AddWithValue("@ts", DateTime.Now.ToString("O"));
            c.Parameters.AddWithValue("@sid", _sessionId);
            c.Parameters.AddWithValue("@type", type);
            c.Parameters.AddWithValue("@dj", JsonSerializer.Serialize(data));
            c.ExecuteNonQuery();
        }
        catch { }
    }

    // 完整性检查：魔数不符/打开失败/quick_check 非 ok → 改名保留现场 → 重建新库；绝不崩溃、绝不动 rss.db。
    // 并发启动/写入时 quick_check 可能读到瞬时中间状态（误报损坏）：
    //   · busy/locked 类错误与瞬时非 ok 结果 → 重试，不算损坏
    //   · 连续重试仍失败 → 改名失败（文件被其他进程占用）时静默跳过，下次启动再试；不吓人、不动数据
    static void CheckIntegrity()
    {
        try
        {
            if (!File.Exists(DbPath)) { CreateSchema(); WriteCheckpoint("created"); return; }
            bool ok = IsSqliteFile(DbPath);
            if (ok) ok = TryQuickCheckOk();
            if (ok)
            {
                CreateSchema();   // 幂等：确保表结构与 WAL 模式（旧库首次启动即完成 WAL 迁移）
                WriteCheckpoint("ok");
                return;
            }
            string corrupt = DbPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            try
            {
                File.Move(DbPath, corrupt);
                CreateSchema();
                WriteCheckpoint("recreated");
                Console.Error.WriteLine("telemetry.db 完整性检查失败，已保留现场并重建：" + corrupt);
            }
            catch { /* 文件仍被占用（并发写入）：本次跳过，下次启动再试 */ }
        }
        catch { /* 完整性检查失败不阻断启动 */ }
    }

    // quick_check：带 busy_timeout；busy/locked 或瞬时非 ok 结果重试，连续多次失败才算损坏
    static bool TryQuickCheckOk()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={DbPath}");
                conn.Open();
                var c = conn.CreateCommand();
                c.CommandText = "PRAGMA busy_timeout = 2000;";
                c.ExecuteNonQuery();
                c.CommandText = "PRAGMA quick_check";
                if (c.ExecuteScalar()?.ToString() == "ok") return true;
            }
            catch (SqliteException ex) when (IsBusyCode(ex.SqliteErrorCode)) { /* 锁冲突：重试 */ }
            catch { return false; }   // 真损坏/打开失败
        }
        return false;
    }

    // SQLITE_BUSY / SQLITE_LOCKED 系列错误码（含共享缓存/恢复/快照/超时变体）
    internal static bool IsBusyCode(int rc) => rc is 5 or 6 or 261 or 262 or 283 or 284;

    // 校验 SQLite 文件头魔数（"SQLite format 3\0"）
    internal static bool IsSqliteFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            Span<byte> head = stackalloc byte[16];
            return fs.Read(head) >= 16 && head.SequenceEqual("SQLite format 3\0"u8);
        }
        catch { return false; }
    }

    static void CreateSchema()
    {
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        var c = conn.CreateCommand();
        // WAL：并发启动/写入时读者永远看到一致快照，从根上避免瞬时损坏误报
        c.CommandText = "PRAGMA journal_mode = WAL;";
        c.ExecuteNonQuery();
        c.CommandText = @"
            CREATE TABLE IF NOT EXISTS telemetry_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                session_id TEXT NOT NULL,
                type TEXT NOT NULL,
                article_id INTEGER,
                source_id INTEGER,
                version_id INTEGER,
                surface TEXT,
                position INTEGER,
                data_json TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_telemetry_type ON telemetry_events(type);
            CREATE INDEX IF NOT EXISTS idx_telemetry_ts ON telemetry_events(timestamp);
        ";
        c.ExecuteNonQuery();
    }

    static void WriteCheckpoint(string status)
    {
        try
        {
            long size = File.Exists(DbPath) ? new FileInfo(DbPath).Length : 0;
            File.WriteAllText(CheckpointPath, JsonSerializer.Serialize(new
            {
                status,
                dbFile = "telemetry.db",
                size,
                lastOkAt = DateTime.Now.ToString("O")
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // 事件入队（内存缓冲；只接受低频事件，滚动/按键不在此列）
    public static void Record(string type, int? articleId = null, int? sourceId = null, int? versionId = null,
                              string? surface = null, int? position = null, object? data = null)
    {
        if (!IsEnabled) return;
        lock (_lock)
        {
            if (_buffer.Count >= 500) _buffer.Clear();   // 极端积压兜底，防止内存膨胀
            _buffer.Add(new TelemetryEvent
            {
                Timestamp = DateTime.Now.ToString("O"),
                SessionId = _sessionId,
                Type = type,
                ArticleId = articleId,
                SourceId = sourceId,
                VersionId = versionId,
                Surface = surface,
                Position = position,
                DataJson = data == null ? "" : JsonSerializer.Serialize(data)
            });
        }
        if (_buffer.Count >= 50) Flush();
    }

    // 记录 AI 调用；articleId/sourceId 可选，用于把调用归因到具体文章/源（精细到文章与源）
    public static void RecordAiCall(string operation, string provider, string model, bool success, long durationMs,
                                    int? articleId = null, int? sourceId = null)
        => Record("ai_call", articleId: articleId, sourceId: sourceId, data: new { operation, provider, model, success, durationMs });

    // 批量落库（best-effort；连续失败 → 本会话降级停用）
    static void Flush()
    {
        if (!IsEnabled) return;
        List<TelemetryEvent> batch;
        lock (_lock)
        {
            if (_buffer.Count == 0) return;
            batch = _buffer.ToList();
            _buffer.Clear();
        }
        try
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            using var tx = conn.BeginTransaction();
            var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO telemetry_events
                (timestamp, session_id, type, article_id, source_id, version_id, surface, position, data_json)
                VALUES (@ts, @sid, @type, @aid, @fsid, @vid, @sf, @pos, @dj)";
            cmd.Parameters.Add("@ts", SqliteType.Text);
            cmd.Parameters.Add("@sid", SqliteType.Text);
            cmd.Parameters.Add("@type", SqliteType.Text);
            cmd.Parameters.Add("@aid", SqliteType.Integer);
            cmd.Parameters.Add("@fsid", SqliteType.Integer);
            cmd.Parameters.Add("@vid", SqliteType.Integer);
            cmd.Parameters.Add("@sf", SqliteType.Text);
            cmd.Parameters.Add("@pos", SqliteType.Integer);
            cmd.Parameters.Add("@dj", SqliteType.Text);
            foreach (var e in batch)
            {
                cmd.Parameters["@ts"].Value = e.Timestamp;
                cmd.Parameters["@sid"].Value = e.SessionId;
                cmd.Parameters["@type"].Value = e.Type;
                cmd.Parameters["@aid"].Value = (object?)e.ArticleId ?? DBNull.Value;
                cmd.Parameters["@fsid"].Value = (object?)e.SourceId ?? DBNull.Value;
                cmd.Parameters["@vid"].Value = (object?)e.VersionId ?? DBNull.Value;
                cmd.Parameters["@sf"].Value = (object?)e.Surface ?? DBNull.Value;
                cmd.Parameters["@pos"].Value = (object?)e.Position ?? DBNull.Value;
                cmd.Parameters["@dj"].Value = e.DataJson;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            _consecFails = 0;
            CapIfNeeded(conn);
        }
        catch
        {
            _consecFails++;
            if (_consecFails >= 3) _failed = true;   // 连续 3 次失败 → 本会话停用，阅读不受影响
        }
    }

    // 容量上限：超过 10 万条 → 只保留最新 8 万
    static void CapIfNeeded(SqliteConnection conn)
    {
        try
        {
            var c = conn.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM telemetry_events";
            long n = (long)c.ExecuteScalar()!;
            if (n > 100_000)
            {
                c.CommandText = "DELETE FROM telemetry_events WHERE id < (SELECT id FROM telemetry_events ORDER BY id DESC LIMIT 1 OFFSET 80000)";
                c.ExecuteNonQuery();
            }
        }
        catch { }
    }

    public static (long Count, string? First, string? Last) Stats()
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var c = conn.CreateCommand();
            c.CommandText = "SELECT COUNT(*), MIN(timestamp), MAX(timestamp) FROM telemetry_events";
            using var r = c.ExecuteReader();
            if (r.Read()) return (r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
        }
        catch { }
        return (0, null, null);
    }

    public static void Clear()
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var c = conn.CreateCommand();
            c.CommandText = "DELETE FROM telemetry_events";
            c.ExecuteNonQuery();
        }
        catch { }
    }

    public static List<TelemetryEvent> AllEvents(int limit = 100000)
    {
        var list = new List<TelemetryEvent>();
        try
        {
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var c = conn.CreateCommand();
            c.CommandText = "SELECT id, timestamp, session_id, type, article_id, source_id, version_id, surface, position, data_json FROM telemetry_events ORDER BY id DESC LIMIT @n";
            c.Parameters.AddWithValue("@n", limit);
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new TelemetryEvent
                {
                    Id = r.GetInt64(0),
                    Timestamp = r.GetString(1),
                    SessionId = r.GetString(2),
                    Type = r.GetString(3),
                    ArticleId = r.IsDBNull(4) ? null : r.GetInt32(4),
                    SourceId = r.IsDBNull(5) ? null : r.GetInt32(5),
                    VersionId = r.IsDBNull(6) ? null : r.GetInt32(6),
                    Surface = r.IsDBNull(7) ? null : r.GetString(7),
                    Position = r.IsDBNull(8) ? null : r.GetInt32(8),
                    DataJson = r.IsDBNull(9) ? "" : r.GetString(9)
                });
        }
        catch { }
        return list;
    }

    // ── 阅读行为聚合（按源，窗口内）──
    // 返回 FeedId → (opened, completed, skipped)；窗口按事件 timestamp 过滤；损坏/关闭时返回空
    public static Dictionary<int, (long Opened, long Completed, long Skipped)> FeedReadingStats(int windowDays)
    {
        var map = new Dictionary<int, (long, long, long)>();
        try
        {
            if (!IsEnabled) return map;
            string cutoff = DateTime.Now.AddDays(-Math.Max(1, windowDays)).ToString("O");
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var c = conn.CreateCommand();
            c.CommandText = @"
                SELECT source_id,
                       SUM(CASE WHEN type = 'article_open' THEN 1 ELSE 0 END),
                       SUM(CASE WHEN type = 'article_complete' THEN 1 ELSE 0 END),
                       SUM(CASE WHEN type = 'article_skip' THEN 1 ELSE 0 END)
                FROM telemetry_events
                WHERE timestamp >= @cut AND source_id IS NOT NULL
                GROUP BY source_id";
            c.Parameters.AddWithValue("@cut", cutoff);
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                int fid = r.GetInt32(0);
                map[fid] = (r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
            }
        }
        catch { }
        return map;
    }

    // ── AI 调用聚合（按源，窗口内）──
    // 返回 FeedId → { llm (摘要/对话) 次数, embedding 次数, 成功数 }；全局（无源）不计入
    public static Dictionary<int, (long Llm, long Embedding, long Success)> FeedAiCallStats(int windowDays)
    {
        var map = new Dictionary<int, (long, long, long)>();
        try
        {
            if (!IsEnabled) return map;
            string cutoff = DateTime.Now.AddDays(-Math.Max(1, windowDays)).ToString("O");
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var c = conn.CreateCommand();
            c.CommandText = @"
                SELECT source_id,
                       SUM(CASE WHEN type = 'ai_call' AND json_extract(data_json, '$.operation') = 'llm' THEN 1 ELSE 0 END),
                       SUM(CASE WHEN type = 'ai_call' AND json_extract(data_json, '$.operation') = 'embedding' THEN 1 ELSE 0 END),
                       SUM(CASE WHEN type = 'ai_call' AND json_extract(data_json, '$.success') = 1 THEN 1 ELSE 0 END)
                FROM telemetry_events
                WHERE type = 'ai_call' AND timestamp >= @cut AND source_id IS NOT NULL
                GROUP BY source_id";
            c.Parameters.AddWithValue("@cut", cutoff);
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                int fid = r.GetInt32(0);
                map[fid] = (r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
            }
        }
        catch { }
        return map;
    }

    // ── AI 调用全局统计（窗口内，含无源调用）──
    // 返回 (总次数, 成功数, 失败数)；另按 operation 计数（llm/embedding/其他）
    public static (long Total, long Success, long Fail, long Llm, long Embedding) GlobalAiCallStats(int windowDays)
    {
        try
        {
            if (!IsEnabled) return (0, 0, 0, 0, 0);
            string cutoff = DateTime.Now.AddDays(-Math.Max(1, windowDays)).ToString("O");
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var c = conn.CreateCommand();
            c.CommandText = @"
                SELECT COUNT(*),
                       SUM(CASE WHEN json_extract(data_json, '$.success') = 1 THEN 1 ELSE 0 END),
                       SUM(CASE WHEN json_extract(data_json, '$.success') = 0 THEN 1 ELSE 0 END),
                       SUM(CASE WHEN json_extract(data_json, '$.operation') = 'llm' THEN 1 ELSE 0 END),
                       SUM(CASE WHEN json_extract(data_json, '$.operation') = 'embedding' THEN 1 ELSE 0 END)
                FROM telemetry_events
                WHERE type = 'ai_call' AND timestamp >= @cut";
            c.Parameters.AddWithValue("@cut", cutoff);
            using var r = c.ExecuteReader();
            if (r.Read())
                return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4));
        }
        catch { }
        return (0, 0, 0, 0, 0);
    }
}
