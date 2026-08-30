// ===== 孟思琳(simon)——安全守护与数据加密(从 sipcore.cs 拆出)=====
// 与 sipcore.cs 同属 partial class Program(入口文件顶层语句生成的类),
// 可自由调用 sipcore.cs 的顶层函数与基础设施;入口的 RunCli 经
// SimonCheckBlock/SimonCli 与此文件交互。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

public partial class Program
{

// ══════════ 孟思琳(simon)——安全守护(默认开启,无法关闭,只能调节挡位)══════════
// 挡位:1=基础(完整性自愈+基础防护,现状能力) 2=严格(非交互禁破坏性写)
//       3=极致(非交互禁全部写 + 数据加密[Phase B])
// 原则:挡位只能 1/2/3(无 0=不可关闭);降挡必须真实交互终端(防 AI/脚本把守护调弱)

class SimonEvent
{
    public string Ts { get; set; } = "";
    public string Type { get; set; } = "";     // repair_db / blocked_cmd / level_change
    public int? Level { get; set; }
    public string Detail { get; set; } = "";
}

static string SimonEventsPath() => Path.Combine(dataDir, "simon_events.json");

static void SimonRecord(string type, string detail, int? level = null)
{
    try
    {
        var evs = new List<SimonEvent>();
        if (File.Exists(SimonEventsPath()))
            evs = JsonSerializer.Deserialize<List<SimonEvent>>(File.ReadAllText(SimonEventsPath())) ?? new();
        evs.Add(new SimonEvent { Ts = DateTime.Now.ToString("O"), Type = type, Level = level, Detail = detail });
        if (evs.Count > 200) evs.RemoveRange(0, evs.Count - 200);   // 只留最近 200 条
        File.WriteAllText(SimonEventsPath(), JsonSerializer.Serialize(evs,
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
    catch { }
}

static List<SimonEvent> SimonLoadEvents()
{
    try
    {
        if (File.Exists(SimonEventsPath()))
            return JsonSerializer.Deserialize<List<SimonEvent>>(File.ReadAllText(SimonEventsPath())) ?? new();
    }
    catch { }
    return new();
}

// 凭据作用域:按数据目录哈希隔离——同一用户的多个 sip 副本(不同目录)互不影响;
// 同目录的不同 exe 版本(升级)共享同一作用域。环境变量可覆盖(自动化测试用)。
static string SimonScopeHash()
{
    try
    {
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dataDir)))[..12];
    }
    catch { return "default"; }
}

// 挡位存储:权威值在系统凭据库(文件编辑无法降挡——孟思琳不能被改 JSON 绕过);
// sip_settings.json 仅作缓存/兼容(凭据库缺失时回退)。升降挡只经程序接口。
static string SimonLevelKey()
{
    string? t = Environment.GetEnvironmentVariable("SIP_SIMON_KEY_NAME");
    return string.IsNullOrEmpty(t) ? "simon_level_" + SimonScopeHash() : t;
}

static int SimonLevelGet()
{
    string? k = CredGet(SimonLevelKey() + "_level");
    if (!string.IsNullOrEmpty(k) && int.TryParse(k, out int lvl)) return Math.Clamp(lvl, 1, 3);
    return Math.Clamp(LoadSettings().SimonLevel, 1, 3);   // 兼容旧配置/凭据库缺失
}

static void SimonLevelSet(int lvl)
{
    int v = Math.Clamp(lvl, 1, 3);
    CredSet(SimonLevelKey() + "_level", v.ToString());
    var s = LoadSettings();
    s.SimonLevel = v;   // 文件缓存同步(供凭据库缺失场景兜底显示)
    SaveSettings(s);
}

static int CurrentSimonLevel() => SimonLevelGet();

// 挡位 2 的写命令 = 一切非只读命令(用户语义:2 级起不允许写入数据库)
// 挡位 3 = 所有 CLI 调用(唯一例外 simon status,见 SimonCheckBlock)

// 挡位 3 的读命令白名单(挡位 2 用:非只读即写,一律拦截)
static bool SimonIsReadOnly(string cmd, string sub)
    => cmd is "-l" or "--list" or "--show" or "--content" or "--versions" or "--history"
          or "--diff" or "--grep" or "--search" or "--today" or "--feed-info" or "--export-opml"
          or "--help" or "-h" or "--version" or "--insights" or "--insights-interval" or "simon"
       || (cmd == "telemetry" && sub is "status" or "show")
       || (cmd == "--dedup" && sub is "list" or "scan")
       || (cmd == "--policy" && sub == "list");

// 统一拦截入口:返回被拦截的原因;null=放行。
// 用户语义:挡位 2 = CLI 写操作一律拒绝;挡位 3 = CLI 所有调用一律拒绝。
// CLI 本身(含交互终端)是不可信通道;TUI 命令栏不经此检查,永远是真人通道。
// 唯一例外:simon status(守护状态查询)在任意挡位放行,否则挡位 3 下无法查看守护状态。
static string? SimonCheckBlock(string cmd, string[] args)
{
    int level = CurrentSimonLevel();
    string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
    bool isSimonStatus = cmd == "simon" && (sub is "" or "status" or "show" or "list" or "--json");
    if (level >= 3 && !isSimonStatus)
        return Lang.T("挡位 3(极致):CLI 调用已全部拒绝({0});只允许通过 TUI 使用。", cmd);
    if (level >= 2 && !SimonIsReadOnly(cmd, sub))
        return Lang.T("挡位 {0}(严格):CLI 写操作已拒绝({1});只读命令可用,或到 TUI 操作。", level, cmd);
    return null;
}

// CLI:sip simon status [--json] | level <1|2|3> | export-key <file> | import-key <file>
// fromTui=true 表示从 TUI 命令栏调用:降挡(关闭/调弱安全功能)只允许在 TUI 里进行
static void SimonCli(string[] args, string dbPath, bool fromTui = false)
{
    bool json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
    string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
    if (sub == "level")
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int lvl) || lvl is < 1 or > 3)
        {
            SetExit();
            if (json) JsonOut(new { success = false, error = new { code = "USAGE", message = Lang.T("Usage: sip simon level <1|2|3>  (1=基础 2=严格 3=极致;无法关闭)") } });
            else Console.WriteLine(Lang.T("Usage: sip simon level <1|2|3>  (1=基础 2=严格 3=极致;无法关闭)"));
            return;
        }
        int cur = SimonLevelGet();
        if (lvl < cur && !fromTui)
        {
            // 降挡只能从 TUI 进行(真人坐在键盘前的强交互环境);
            // CLI 一律拒绝——即使交互终端,CLI 也常被脚本/AI 包装调用,不能作为降挡通道
            SetExit();
            if (json) JsonOut(new { success = false, error = new { code = "SIMON_LOCKED", level = cur, message = Lang.T("降挡只能在 TUI 界面里进行(安全考虑)——孟思琳不允许被脚本或 CLI 调弱") } });
            else Console.WriteLine(Lang.T("降挡只能在 TUI 界面里进行(安全考虑)——孟思琳不允许被脚本或 CLI 调弱"));
            return;
        }
        SimonLevelSet(lvl);
        SimonRecord("level_change", $"{cur} → {lvl}", lvl);
        // 升到挡位 3 → 加密 rss.db(明文库迁移为 SQLCipher;密钥存系统凭据库)
        if (lvl >= 3 && cur < 3)
        {
            string? err = SimonEncryptDb(dbPath);
            if (err == null)
            {
                SimonEncryptSensitiveFiles();   // 迁移 fulltext 缓存 + dedup.json 为 AES 加密
                if (json) JsonOut(new { success = true, data = new { level = lvl, encryption = "on", backup = ".plaintext.bak" } });
                else Console.WriteLine(Lang.T("数据文件已加密(SQLCipher),原库备份为 {0}", ".plaintext.bak"));
            }
            else
            {
                SetExit();
                if (json) JsonOut(new { success = false, error = new { code = "ENCRYPT_FAILED", level = lvl, message = err } });
                else Console.WriteLine(Lang.T("加密失败: {0}(数据仍为明文,请重试)", err));
            }
        }
        else
        {
            if (json) JsonOut(new { success = true, data = new { level = lvl } });
            else Console.WriteLine(Lang.T("孟思琳(simon) 守护挡位: {0} → {1}", cur, lvl));
        }
        return;
    }
    if (sub == "export-key")
    {
        // 交互式导出密钥备份(换机迁移用);非交互拒绝——防脚本偷走密钥
        if (Console.IsInputRedirected)
        {
            SetExit(); Console.WriteLine(Lang.T("export-key 需要真实交互终端(安全考虑,不接受管道输入)")); return;
        }
        if (args.Length < 2) { SetExit(); Console.WriteLine(Lang.T("Usage: sip simon export-key <file>")); return; }
        string file = args[1];
        Console.WriteLine(Lang.T("即将把数据库加密密钥写入 {0}。该文件可解密你的全部数据,请妥善保管(如密码管理器)。继续? (y/n) ", file));
        if (Console.ReadLine()?.Trim().ToLower() != "y") { Console.WriteLine(Lang.T("Cancelled")); return; }
        try
        {
            File.WriteAllText(file, "sip-simon-key-v1\n" + SimonDbKey() + "\n");
            Console.WriteLine(Lang.T("密钥已导出到 {0}", file));
        }
        catch (Exception ex) { SetExit(); Console.WriteLine(Lang.T("导出失败: {0}", ex.Message)); }
        return;
    }
    if (sub == "import-key")
    {
        // 交互式导入密钥备份(换机恢复);非交互拒绝
        if (Console.IsInputRedirected)
        {
            SetExit(); Console.WriteLine(Lang.T("import-key 需要真实交互终端(安全考虑,不接受管道输入)")); return;
        }
        if (args.Length < 2) { SetExit(); Console.WriteLine(Lang.T("Usage: sip simon import-key <file>")); return; }
        try
        {
            var lines = File.ReadAllLines(args[1]);
            if (lines.Length < 2 || lines[0].Trim() != "sip-simon-key-v1")
            {
                SetExit(); Console.WriteLine(Lang.T("密钥文件格式不正确")); return;
            }
            string key = lines[1].Trim();
            if (key.Length < 16) { SetExit(); Console.WriteLine(Lang.T("密钥文件格式不正确")); return; }
            Console.WriteLine(Lang.T("将覆盖当前密钥(若库已加密且密钥不同,数据将无法读取)。继续? (y/n) "));
            if (Console.ReadLine()?.Trim().ToLower() != "y") { Console.WriteLine(Lang.T("Cancelled")); return; }
            CredSet("simon_db_key", key);
            SimonRecord("key_import", "密钥已从备份导入");
            Console.WriteLine(Lang.T("密钥已导入"));
        }
        catch (Exception ex) { SetExit(); Console.WriteLine(Lang.T("导入失败: {0}", ex.Message)); }
        return;
    }
    if (sub is "status" or "show" or "list")
    {
        int level = CurrentSimonLevel();
        var evs = SimonLoadEvents();
        var repairs = evs.Where(e => e.Type == "repair_db").ToList();
        var blocks = evs.Where(e => e.Type == "blocked_cmd").ToList();
        bool encrypted = File.Exists(Path.Combine(dataDir, ".db-encrypted"));
        if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
        {
            JsonOut(new
            {
                success = true,
                data = new
                {
                    name = "孟思琳(simon)",
                    level,
                    canDisable = false,
                    encryption = encrypted ? "on" : "off",
                    repairs = repairs.Count,
                    blocked = blocks.Count,
                    recent = evs.TakeLast(10).Select(e => new { ts = e.Ts, type = e.Type, level = e.Level, detail = e.Detail })
                }
            });
            return;
        }
        string levelName = level switch { 2 => Lang.T("严格"), 3 => Lang.T("极致"), _ => Lang.T("基础") };
        Console.WriteLine(Lang.T("孟思琳(simon) 安全守护"));
        Console.WriteLine(Lang.T("挡位: {0}({1})——默认开启,无法关闭,只能调节", level, levelName));
        Console.WriteLine(Lang.T("数据加密: {0}(密钥在系统凭据库,自动生成;开启后不可逆)", encrypted ? Lang.T("已开启") : Lang.T("未开启")));
        Console.WriteLine(Lang.T("永远作为此软件的最后一道安全防线。"));
        Console.WriteLine(Lang.T("数据库修复: {0} 次", repairs.Count));
        foreach (var e in repairs.TakeLast(3))
            Console.WriteLine(Lang.T("  · {0} {1}", TryParseIso(e.Ts) is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm") : e.Ts, e.Detail));
        Console.WriteLine(Lang.T("已拦截非交互调用: {0} 次", blocks.Count));
        foreach (var e in blocks.TakeLast(3))
            Console.WriteLine(Lang.T("  · {0} {1}", TryParseIso(e.Ts) is DateTime dt2 ? dt2.ToString("yyyy-MM-dd HH:mm") : e.Ts, e.Detail));
        return;
    }
    SetExit(); Console.WriteLine(Lang.T("Usage: sip simon status [--json] | level <1|2|3> | export-key <file> | import-key <file>"));
}

// ── 数据加密(挡位 3:SQLCipher 加密 rss.db;敏感 JSON 文件 AES 加密)────────────────
// 密钥只存系统凭据库(与 API Key 同机制),绝不落盘到项目文件

// 密钥名:按数据目录哈希隔离(多副本独立密钥);环境变量可覆盖(自动化测试用)
static string SimonKeyName()
{
    string? t = Environment.GetEnvironmentVariable("SIP_SIMON_KEY_NAME");
    return string.IsNullOrEmpty(t) ? "simon_db_key_" + SimonScopeHash() : t;
}

static string SimonDbKey()
{
    string keyName = SimonKeyName();
    string? k = CredGet(keyName);
    if (!string.IsNullOrEmpty(k)) return k;
    var bytes = new byte[32];
    System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
    k = Convert.ToBase64String(bytes);
    CredSet(keyName, k);
    return k;
}

// 库是否明文(SQLite 魔数头);文件不存在视为明文(将新建)
static bool IsPlaintextDb(string path)
{
    try
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        Span<byte> head = stackalloc byte[16];
        return fs.Read(head) >= 16 && head.SequenceEqual("SQLite format 3\0"u8);
    }
    catch { return true; }
}

// 统一打开数据库:数据目录存在 .db-encrypted 标记(挡位 3 加密过)时执行 PRAGMA key 解锁。
// 不用连接字符串 Password(Microsoft.Data.Sqlite 9.x 会检查原生库是否支持加密,报错 'e_sqlite3');
// PRAGMA key 在打开后、任何查询前执行,SQLCipher 标准用法。
// 与挡位无关——降挡后加密库仍可读;挡位 3 只决定「是否把明文库迁移为加密」
static SqliteConnection OpenDb(string dbPath)
{
    string dir = Path.GetDirectoryName(dbPath) ?? "";
    bool encrypted = File.Exists(Path.Combine(dir, ".db-encrypted"));
    var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    if (encrypted)
    {
        using var c = conn.CreateCommand();
        // PRAGMA 不支持参数绑定;key 为 base64(仅 A-Za-z0-9+/=),无引号,拼接安全
        c.CommandText = "PRAGMA key = '" + SimonDbKey() + "'";
        c.ExecuteNonQuery();
    }
    return conn;
}

// 把明文 rss.db 迁移为 SQLCipher 加密(挡位 3 开启时调用;原文件备份 .plaintext.bak)。
// 做法:带 key 的连接创建加密目标库,在其中 ATTACH 明文源逐表复制;
// ItemsFts 及其 shadow 表(ItemsFts_*)不迁移——启动时 InitDatabase 重建,首次 grep 懒回填 FTS
static string? SimonEncryptDb(string dbPath)
{
    try
    {
        // 幂等:已加密(.db-encrypted 标记)直接跳过——SQLCipher 4 保留标准文件头,
        // 不能用 IsPlaintextDb 判断;否则降挡后再次升挡会误判明文、尝试重复加密
        if (File.Exists(Path.Combine(Path.GetDirectoryName(dbPath) ?? "", ".db-encrypted"))) return null;
        if (File.Exists(dbPath) && IsPlaintextDb(dbPath))
        {
            string key = SimonDbKey();
            string encPath = dbPath + ".enc";
            if (File.Exists(encPath)) File.Delete(encPath);
            using (var dst = new SqliteConnection($"Data Source={encPath}"))
            {
                dst.Open();   // 创建目标库
                var c = dst.CreateCommand();
                c.CommandText = "PRAGMA key = '" + key + "'";   // key 为 base64,无引号
                c.ExecuteNonQuery();   // 之后创建的库即加密
                c.CommandText = $"ATTACH DATABASE '{dbPath.Replace("'", "''")}' AS src KEY ''";
                c.ExecuteNonQuery();
                c.CommandText = "SELECT sql FROM src.sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE 'ItemsFts%' AND sql IS NOT NULL";
                var schemas = new List<string>();
                using (var r = c.ExecuteReader())
                    while (r.Read()) schemas.Add(r.GetString(0));
                foreach (var s in schemas)
                {
                    c.CommandText = s.Replace("CREATE TABLE ", "CREATE TABLE main.");
                    c.ExecuteNonQuery();
                }
                foreach (var t in new[] { "Feeds", "Items", "Models", "Vectors" })
                {
                    c.CommandText = $"INSERT INTO main.\"{t}\" SELECT * FROM src.\"{t}\"";
                    c.ExecuteNonQuery();
                }
                c.CommandText = "INSERT INTO main.sqlite_sequence SELECT * FROM src.sqlite_sequence";
                try { c.ExecuteNonQuery(); } catch { }
                c.CommandText = "DETACH DATABASE src";
                c.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            // 替换阶段:两次原子 Move(原库→备份, 加密库→就位)。
            // 任一时刻崩溃:rss.db 缺失但 .plaintext.bak 与 .enc 均在,数据不丢,可恢复
            string bakPath = dbPath + ".plaintext.bak";
            File.Move(dbPath, bakPath);
            try
            {
                File.Move(encPath, dbPath);
            }
            catch
            {
                // 就位失败 → 回滚,恢复明文库(数据优先于加密)
                try { File.Move(bakPath, dbPath); } catch { }
                throw;
            }
            // 加密标记:OpenDb 据此执行 PRAGMA key(SQLCipher 4 文件头是标准头,无法靠头识别)
            try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(dbPath) ?? "", ".db-encrypted"), DateTime.Now.ToString("O")); } catch { }
            return null;
        }
        return null;
    }
    catch (Exception ex) { return ex.Message; }
}

// ── 敏感文件 AES-GCM 加密(挡位 3;密钥复用 simon_db_key,不额外产生密钥)────────────────
// 文件格式: "SIPC1"(5) + nonce(12) + tag(16) + ciphertext
// 读取兼容旧明文文件;解密失败返回 null(调用方容错)

static byte[] SimonKeyBytes() => Convert.FromBase64String(SimonDbKey());

static bool SimonIsEncrypted(byte[] data) => data.Length > 33 && data.AsSpan(0, 5).SequenceEqual("SIPC1"u8);

static byte[] SimonEncryptBytes(byte[] plain)
{
    using var aes = new System.Security.Cryptography.AesGcm(SimonKeyBytes(), 16);
    var nonce = new byte[12];
    System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
    var ct = new byte[plain.Length];
    var tag = new byte[16];
    aes.Encrypt(nonce, plain, ct, tag);
    var outB = new byte[5 + 12 + 16 + ct.Length];
    "SIPC1"u8.CopyTo(outB);
    nonce.CopyTo(outB, 5);
    tag.CopyTo(outB, 17);
    ct.CopyTo(outB, 33);
    return outB;
}

static byte[]? SimonDecryptBytes(byte[] data)
{
    try
    {
        if (!SimonIsEncrypted(data)) return data;   // 明文(旧数据/未加密态)
        using var aes = new System.Security.Cryptography.AesGcm(SimonKeyBytes(), 16);
        var plain = new byte[data.Length - 33];
        aes.Decrypt(data.AsSpan(5, 12), data.AsSpan(33), data.AsSpan(17, 16), plain);
        return plain;
    }
    catch { return null; }
}

// 挡位 3(存在 .db-encrypted 标记)时加密写文本文件;否则明文写(兼容)
static void SimonWriteText(string path, string content)
{
    bool enc = File.Exists(Path.Combine(Path.GetDirectoryName(path) ?? "", ".db-encrypted"));
    if (enc) File.WriteAllBytes(path, SimonEncryptBytes(System.Text.Encoding.UTF8.GetBytes(content)));
    else File.WriteAllText(path, content);
}

// 读文本文件(兼容明文/密文);不存在或解密失败返回 null
static string? SimonReadText(string path)
{
    try
    {
        if (!File.Exists(path)) return null;
        var plain = SimonDecryptBytes(File.ReadAllBytes(path));
        return plain == null ? null : System.Text.Encoding.UTF8.GetString(plain);
    }
    catch { return null; }
}

// 挡位 3 开启时,把现存明文敏感文件迁移为加密(fulltext 缓存 + dedup.json)
static void SimonEncryptSensitiveFiles()
{
    try
    {
        string dedup = Path.Combine(dataDir, "dedup.json");
        if (File.Exists(dedup))
        {
            var data = File.ReadAllBytes(dedup);
            if (!SimonIsEncrypted(data)) File.WriteAllBytes(dedup, SimonEncryptBytes(data));
        }
        string dir = Path.Combine(dataDir, "fulltext");
        if (Directory.Exists(dir))
        {
            foreach (var f in Directory.GetFiles(dir, "*.md"))
            {
                var data = File.ReadAllBytes(f);
                if (!SimonIsEncrypted(data)) File.WriteAllBytes(f, SimonEncryptBytes(data));
            }
        }
    }
    catch { /* 迁移失败不阻断;后续写入仍走加密 */ }
}

}
