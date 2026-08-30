// ===== 引用外部包 =====
// using 相当于导入工具包，每个包提供不同的工具
// System.* 是 C# 自带的（网络、文件、文字处理）
// CodeHollow.FeedReader 是第三方包，专门解析 RSS/Atom
// Microsoft.Data.Sqlite 是微软提供的轻量数据库
using System;
using System.IO;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Runtime.Serialization;
using CodeHollow.FeedReader;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.Data.Sqlite;
using ktsu.CredentialCache;
using ktsu.CredentialCache.Storage;
using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Input;
using Terminal.Gui.Text;

// 统一 UTF-8 输入/输出：避免中文在终端 / AI 调用（PowerShell 默认 GBK 代码页）时乱码，
// 也保证管道输入的同意短语等中文内容按 UTF-8 解码
try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch { /* 某些重定向场景可能不支持，忽略 */ }
try { Console.InputEncoding = new System.Text.UTF8Encoding(false); } catch { /* 同上，忽略 */ }

// 孟思琳(simon):启用 SQLCipher provider。
// 注意顺序:2.1.x 的 SQLitePCL.Batteries_V2.Init(由 Microsoft.Data.Sqlite 首次连接时触发)
// 会无条件覆盖 provider——必须先完成默认初始化,再 SetProvider(sqlcipher) 切换
using (var _initConn = new SqliteConnection("Data Source=:memory:")) { _initConn.Open(); }
try { SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher()); }
catch (Exception ex) { Console.Error.WriteLine("[diag] SetProvider failed: " + ex.GetType().Name + ": " + ex.Message); }

// 数据目录 = exe 同级下的 readwithhotsoup 文件夹（首次启动自动创建）
// 数据库、AI 配置、语言文件等所有配置文件都放在这里，方便整体备份/迁移
string baseDir = AppDomain.CurrentDomain.BaseDirectory;
string dataDir = Path.Combine(baseDir, "readwithhotsoup");
Directory.CreateDirectory(dataDir);

// 首次启动把默认语言文件复制到数据目录（已存在则跳过，保留用户编辑）
EnsureDefaultLanguages(baseDir, dataDir);

string dbPath = Path.Combine(dataDir, "rss.db");
InitDatabase(dbPath);

// 全局选项解析（任意位置均可，解析后从参数中剔除）
// --ignoresafeannouncement：跳过安全横幅等多余输出（供脚本/Agent 使用）
// --lang <代码>：指定语言文件（如 zh-CN / en-US）
string? langCode = null;
if (args.Any(a => a.Equals("--ignoresafeannouncement", StringComparison.OrdinalIgnoreCase)))
{
    AiState.IgnoreAnnouncement = true;
    args = args.Where(a => !a.Equals("--ignoresafeannouncement", StringComparison.OrdinalIgnoreCase)).ToArray();
}
for (int gi = 0; gi < args.Length - 1; gi++)
{
    if (args[gi].Equals("--lang", StringComparison.OrdinalIgnoreCase))
    {
        langCode = args[gi + 1];
        args = args.Where((a, i) => i != gi && i != gi + 1).ToArray();
        break;
    }
}
Lang.Init(dataDir, langCode);
TelemetryService.Init(dataDir);   // 遥测：默认关闭，仅本地，独立 telemetry.db

// ══════════ CLI 模式 ══════════
if (args.Length > 0)
{
    await RunCli(args, dbPath);
    RemindDueFeeds(args, dbPath);
    RemindDueInsights(args, dbPath);
    TelemetryService.Shutdown();   // 冲刷缓冲 + 检查点
    MarkCleanExit(dataDir);
    return AiState.ExitCode;
}

// ══════════ TUI 模式（无参数时进入）══════════
var tuiExit = await RunTui(dbPath);
TelemetryService.Shutdown();   // 冲刷缓冲 + 检查点
MarkCleanExit(dataDir);
return tuiExit;

public partial class Program
{
    // 数据目录 = exe 同级 readwithhotsoup(入口 Main 的局部变量仅入口区可见,
    // 函数区(本 partial 类)统一引用此字段 —— 与入口区初始化值一致)
    static readonly string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "readwithhotsoup");

// ═══════════════════════════════════════════════════════════
// 以下是所有方法，按调用顺序排列
// ═══════════════════════════════════════════════════════════

// 把默认语言文件复制到 readwithhotsoup/languages/，确保 zh-CN / en-US 等官方翻译始终可用。
// 优先级：① exe 旁 languages/ 文件夹（发布外置，可编辑）> ② 内嵌程序集资源（单文件自带）。
// 只写入「缺失」或「旧格式（键为中文）」的文件，用户已编辑过的语言文件不会被覆盖。
// 返回本次是否恢复过文件（用于提示用户优先使用 languages/ 文件夹）。
static bool EnsureDefaultLanguages(string baseDir, string dataDir)
{
    bool restored = false;
    try
    {
        string dst = Path.Combine(dataDir, "languages");
        Directory.CreateDirectory(dst);

        // ① exe 旁外置语言文件夹（发布/开发时复制出来的那份，用户可直接编辑）
        string src = Path.Combine(baseDir, "languages");
        if (Directory.Exists(src))
        {
            foreach (var f in Directory.GetFiles(src, "*.json"))
            {
                string target = Path.Combine(dst, Path.GetFileName(f));
                if (!File.Exists(target)) { File.Copy(f, target); restored = true; }
                else if (IsLegacyLangFile(target))
                {
                    // 旧格式（键为中文原文）→ 用新版英文键格式覆盖，避免界面回退英文
                    try { File.Copy(f, target, overwrite: true); restored = true; } catch { }
                }
                else
                {
                    // 新版文件：合并「内置有、本地缺」的 key（补上新翻译，不覆盖用户已改的 key）
                    MergeLangMissingKeys(f, target);
                }
            }
        }

        // ② 内嵌资源兜底：单文件包里自带官方翻译，外置文件夹缺失时也能恢复
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames())
        {
            const string prefix = "sip-lang.";
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string target = Path.Combine(dst, name.Substring(prefix.Length));
            if (!File.Exists(target))
            {
                using var rs = asm.GetManifestResourceStream(name);
                if (rs != null)
                {
                    using var ws = new FileStream(target, FileMode.Create, FileAccess.Write);
                    rs.CopyTo(ws);
                    restored = true;
                }
            }
        }
    }
    catch { /* 恢复失败不影响主流程 */ }

    // 仅在实际恢复过语言文件时提示（避免每次启动刷屏、污染 --json 输出）。
    // 提示走 stderr，告诉用户自定义翻译优先用 languages/ 文件夹。
    if (restored)
    {
        Console.Error.WriteLine("已恢复默认语言文件 → " + Path.Combine(dataDir, "languages"));
        Console.Error.WriteLine("自定义翻译请优先编辑该 languages/ 文件夹里的文件（内置副本仅作兜底，不会覆盖你的修改）");
    }
    return restored;
}

// 判断语言文件是否为旧格式：键**全是中文**（旧格式键=中文原文；新版键以英文为主，
// 允许个别中文源文本 key，如 slogan/同意短语，不算旧格式）
static bool IsLegacyLangFile(string path)
{
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        bool hasAscii = false, hasCjk = false;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name.Any(c => c < 128)) hasAscii = true;
            if (prop.Name.Any(c => c >= 0x4E00 && c <= 0x9FFF)) hasCjk = true;
        }
        return hasCjk && !hasAscii;
    }
    catch { return false; }
}

// 把内置语言文件里「本地缺失」的 key 补进本地文件（不覆盖用户已改的 key）
static void MergeLangMissingKeys(string builtinPath, string targetPath)
{
    try
    {
        var builtin = ParseLangObject(builtinPath);
        var local = ParseLangObject(targetPath);
        if (builtin == null || local == null) return;
        var localKeys = new HashSet<string>();
        CollectLangKeys(local, localKeys);
        var missing = new List<KeyValuePair<string, string>>();
        CollectLangLeaves(builtin, missing, localKeys);
        if (missing.Count == 0) return;
        foreach (var kv in missing) local[kv.Key] = kv.Value;
        // 用不转义非 ASCII 的编码器，保留中文可读性（用户后续可直接编辑）
        File.WriteAllText(targetPath, local.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
    }
    catch { /* 合并失败不影响启动 */ }
}

// 解析语言文件为 JsonObject。历史文件可能含重复顶层 key(JsonNode 遇到会抛
// ArgumentException,导致整个合并失效):回退用 JsonDocument(允许重复)手动重建,
// 重复 key 保留最后一个值。
static System.Text.Json.Nodes.JsonObject? ParseLangObject(string path)
{
    try { return System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) as System.Text.Json.Nodes.JsonObject; }
    catch
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var obj = new System.Text.Json.Nodes.JsonObject();
            foreach (var p in doc.RootElement.EnumerateObject())
                obj[p.Name] = System.Text.Json.Nodes.JsonNode.Parse(p.Value.GetRawText());
            return obj;
        }
        catch { return null; }
    }
}

static void CollectLangKeys(System.Text.Json.Nodes.JsonObject obj, HashSet<string> keys)
{
    foreach (var p in obj)
    {
        if (p.Value is System.Text.Json.Nodes.JsonObject nested) CollectLangKeys(nested, keys);
        else if (p.Value is System.Text.Json.Nodes.JsonValue) keys.Add(p.Key);
    }
}

static void CollectLangLeaves(System.Text.Json.Nodes.JsonObject obj, List<KeyValuePair<string, string>> missing, HashSet<string> localKeys)
{
    foreach (var p in obj)
    {
        if (p.Value is System.Text.Json.Nodes.JsonObject nested) CollectLangLeaves(nested, missing, localKeys);
        else if (p.Value is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) && !localKeys.Contains(p.Key))
            missing.Add(new KeyValuePair<string, string>(p.Key, s));
    }
}

// CLI 命令结束后提示「有到期的订阅源」（不自动更新，只提醒用户手动 sip --sync）
// 纯读库判断，零网络、不阻塞、不改变退出码；--json 模式自动抑制（避免污染结构化输出）
static void RemindDueFeeds(string[] args, string dbPath)
{
    try
    {
        if (args.Contains("--json", StringComparer.OrdinalIgnoreCase)) return;  // 不能污染 | jq 的 JSON
        string cmd = args[0].ToLowerInvariant();
        if (cmd is "-h" or "--help") return;                          // 帮助页不带噪声
        if (cmd is "-u" or "--update" or "-d" or "--download" or "--sync" or "--update-all"
            or "--schedule" or "--sched") return;                     // 本身就是更新类命令
        var due = GetDueFeeds(dbPath);
        if (due.Count == 0) return;                                   // 没有到期的就不打扰
        Console.WriteLine(Lang.T("{0} feeds are due, run sip --sync to update", due.Count));
    }
    catch { /* 提示失败不影响主命令与退出码 */ }
}

// ══════════ 全文抓取（fetch）：文件缓存，零改表 ══════════
// 全文文本存 dataDir/fulltext/<itemId>.md；sidecar 向量存 fulltext/vecs.json；
// 同意标记存 fulltext_consent.txt。fetch 不改 Items.Content、不产生新版本、
// 不参与 diff/更新，仅是一次性"补充阅读"副作用。

static string FulltextDir() { string d = Path.Combine(dataDir, "fulltext"); Directory.CreateDirectory(d); return d; }
static string FulltextPath(long itemId) => Path.Combine(FulltextDir(), itemId + ".md");
static string FulltextVecsPath() => Path.Combine(FulltextDir(), "vecs.json");
static string FulltextConsentPath() => Path.Combine(dataDir, "fulltext_consent.txt");

static bool HasFulltextConsent() => File.Exists(FulltextConsentPath());
static void WriteFulltextConsent() => File.WriteAllText(FulltextConsentPath(), DateTime.Now.ToString("O"));

// 内容是否过短（Content 或 Description 字符数 < 100 → 触发全文抓取）
static bool ContentTooShort(string content, string desc)
{
    string c = string.IsNullOrWhiteSpace(content) ? desc : content;
    return c.Trim().Length < 100;
}

// 某文章内容是否过短（TUI 判断是否需二次确认用）
static bool ArticleContentShort(string dbPath, int itemId)
{
    try
    {
        using var conn = OpenDb(dbPath);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Content, Description FROM Items WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return true;
        return ContentTooShort(r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1));
    }
    catch { return true; }
}

// 读取某文全文缓存；未缓存返回 null(兼容明文/加密)
static string? ReadFulltextCache(long itemId)
{
    return SimonReadText(FulltextPath(itemId));
}

// —— SSRF 防护：地址分类 0=允许 1=硬拦截（回环/链路本地） 2=私网段（默认拦截，AllowPrivateNet=true 放行）——
static int AddressCategory(System.Net.IPAddress ip)
{
    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
    {
        byte[] b = ip.GetAddressBytes();
        bool loopback = b[0] == 127;
        bool linkLocal = b[0] == 169 && b[1] == 254;            // 含云元数据 169.254.169.254
        bool privateRange = b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);      // CGNAT 100.64/10
        if (loopback || linkLocal || b[0] == 0) return 1;
        if (privateRange) return 2;
        return 0;
    }
    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
    {
        if (System.Net.IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.Equals(System.Net.IPAddress.IPv6None)) return 1;
        if (ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal) return 2;
        return 0;
    }
    return 2;  // 未知地址族保守拦截
}

// 抓取 URL 安全校验（SSRF 防护）：仅 http/https；回环/链路本地一律拒绝；
// 私网段按配置决定。返回错误信息；null = 允许
static string? ValidateFetchUrl(string url, bool allowPrivateNet)
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host))
        return Lang.T("Invalid URL: {0}", url);
    if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)
        return Lang.T("Only http/https URLs can be fetched: {0}", url);
    if (u.IsLoopback)
        return Lang.T("Loopback URLs are not allowed: {0}", url);

    var hosts = new List<System.Net.IPAddress>();
    if (System.Net.IPAddress.TryParse(u.Host.Trim('[', ']'), out var ipLiteral))
    {
        hosts.Add(ipLiteral);
    }
    else
    {
        try { hosts.AddRange(System.Net.Dns.GetHostAddresses(u.Host)); }
        catch { return Lang.T("Cannot resolve host: {0}", u.Host); }
    }
    foreach (var ip in hosts)
    {
        int cat = AddressCategory(ip);
        if (cat == 1)
            return Lang.T("Loopback/link-local addresses are not allowed: {0}", u.Host);
        if (cat == 2 && !allowPrivateNet)
            return Lang.T("Private network address is blocked (set allowPrivateNet=true in ai_config.json to allow): {0}", u.Host);
    }
    return null;
}

// 下载链接页并抽取"可读正文"（简单 readability：去 script/style/nav/footer 等，取可见文本）
static string? FetchAndExtract(string url)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
        var html = client.GetStringAsync(url).GetAwaiter().GetResult();
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);
        foreach (var node in doc.DocumentNode.SelectNodes("//script | //style | //nav | //footer | //header | //aside | //form | //noscript") ?? Enumerable.Empty<HtmlAgilityPack.HtmlNode>())
            node.Remove();
        var text = doc.DocumentNode.InnerText;
        text = Regex.Replace(text, @"[ \t\r]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }
    catch { return null; }
}

// 抓取核心（调用方已完成同意与二次确认）。
// 返回 (全文, 退出码, 错误信息)；0=成功；错误时不再打 Console，由调用方展示
static (string? Text, int ExitCode, string? Error) DoFetchCore(string dbPath, int itemId)
{
    using var conn = OpenDb(dbPath);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT i.Link, i.Content, i.Description, i.FeedId FROM Items i WHERE i.Id = @id";
    cmd.Parameters.AddWithValue("@id", itemId);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) return (null, 3, Lang.T("Article {0} not found", itemId));
    string link = r.IsDBNull(0) ? "" : r.GetString(0);
    string content = r.IsDBNull(1) ? "" : r.GetString(1);
    string desc = r.IsDBNull(2) ? "" : r.GetString(2);
    int feedId = r.GetInt32(3);

    string? cached = ReadFulltextCache(itemId);
    if (cached != null)
    {
        // 缓存命中但 sidecar 缺失（先抓全文后索引）时补齐，避免永远追不上
        EnsureFulltextSidecar(dbPath, itemId, feedId, cached);
        return (cached, 0, null);
    }
    if (string.IsNullOrWhiteSpace(link)) return (null, 1, Lang.T("No link, cannot fetch"));
    string? urlErr = ValidateFetchUrl(link, LoadConfig(dbPath).AllowPrivateNet);
    if (urlErr != null) return (null, 2, urlErr);
    string text = FetchAndExtract(link) ?? "";
    if (string.IsNullOrWhiteSpace(text)) return (null, 2, Lang.T("Fetch failed"));
    SimonWriteText(FulltextPath(itemId), text);
    TrimFulltextCache();
    // 该源若已索引 → 用全文做 sidecar 向量（存 fulltext/vecs.json，不污染主 Vectors 表）
    EmbedFulltextSidecar(dbPath, itemId, feedId, text);
    return (text, 0, null);
}

// 抓取全文主入口（CLI）。返回 (全文, 退出码, 错误信息)。
// yes=true（--yes）：跳过同意与二次确认（AI/脚本）。已缓存则直接返回缓存。
static (string? Text, int ExitCode, string? Error) FetchFulltext(string dbPath, int itemId, bool yes, bool force = false)
{
    // 先读元信息判断是否过短（用于二次确认）；不存在直接返回错误
    string? content = null, desc = null, title = null;
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Content, Description, Title FROM Items WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (null, 3, Lang.T("Article {0} not found", itemId));
        content = r.IsDBNull(0) ? "" : r.GetString(0);
        desc = r.IsDBNull(1) ? "" : r.GetString(1);
        title = r.GetString(2);
    }

    // 同意流程（一次性）：短语按当前语言（en-US 用英文短语）
    string agreePhrase = Lang.T("是的，我愿意与作者达成合理使用约定");
    if (!HasFulltextConsent())
    {
        if (yes) WriteFulltextConsent();
        else
        {
            Console.WriteLine(Lang.T("sip is a reading aid; article fetching is for personal reading/study only. You agree to respect the source's intellectual property and copyright. You alone bear any loss from malicious use."));
            Console.Write(Lang.T("Type exactly to agree: {0}: ", agreePhrase));
            string input = Console.ReadLine()?.Trim() ?? "";
            if (input != agreePhrase)
            {
                Console.WriteLine(Lang.T("Not agreed, cancelled"));
                return (null, 1, null);
            }
            WriteFulltextConsent();
        }
    }

    // 二次确认：仅对"非过短"文章（原文已够长，多半是误触，明确告知）
    if (!force && !ContentTooShort(content!, desc!) && !yes)
    {
        Console.Write(Lang.T("The original text is already long. Did you mean to fetch? Fetch anyway? (y/n) "));
        if (!"y".Equals(Console.ReadLine()?.Trim().ToLower()))
        {
            Console.WriteLine(Lang.T("Cancelled"));
            return (null, 1, null);
        }
    }

    return DoFetchCore(dbPath, itemId);
}

// CLI：sip --fulltext <id> [--yes] [--json]
static void FulltextCli(string[] args, string dbPath)
{
    if (!int.TryParse(args[0], out int itemId)) { SetExit(); Console.WriteLine(Lang.T("The number must be numeric")); return; }
    bool yes = args.Any(a => a.Equals("--yes", StringComparison.OrdinalIgnoreCase));
    bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    var (text, code, err) = FetchFulltext(dbPath, itemId, yes);
    if (text == null)
    {
        SetExit(code);
        if (json && err != null)
            JsonOut(new { success = false, error = new { code = code == 2 ? "FETCH_FAILED" : (code == 3 ? "ITEM_NOT_FOUND" : "CANCELLED"), message = err } });
        else if (err != null) Console.WriteLine(err);
        return;
    }
    if (json) JsonOut(new { success = true, itemId, cached = true, content = text });
    else Console.WriteLine(StripControlChars(text));
}

// —— sidecar 向量（方案甲）——

static List<(int ItemId, int FeedId, int ModelId, float[] Vector)> LoadFulltextVecs()
{
    string p = FulltextVecsPath();
    if (!File.Exists(p)) return new();
    try
    {
        var arr = JsonSerializer.Deserialize<List<FulltextVecEntry>>(File.ReadAllText(p));
        return arr?.Select(e => (e.ItemId, e.FeedId, e.ModelId, e.Vector)).ToList() ?? new();
    }
    catch { return new(); }
}

static void SaveFulltextVecs(List<(int ItemId, int FeedId, int ModelId, float[] Vector)> list)
    => File.WriteAllText(FulltextVecsPath(), JsonSerializer.Serialize(list.Select(e => new FulltextVecEntry { ItemId = e.ItemId, FeedId = e.FeedId, ModelId = e.ModelId, Vector = e.Vector }).ToList()));

// 该源是否已索引（Vectors 表里该 FeedId 是否有向量）
static bool FeedHasVectors(string dbPath, int feedId)
{
    try
    {
        using var conn = OpenDb(dbPath);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Vectors WHERE FeedId = @f";
        cmd.Parameters.AddWithValue("@f", feedId);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
    catch { return false; }
}

// 当前 embedding 模型 Id；无则返回 0
static int CurrentEmbeddingModelId(string dbPath)
{
    try
    {
        using var conn = OpenDb(dbPath);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id FROM Models WHERE IsCurrent = 1 AND ModelType = 'embedding'";
        var o = cmd.ExecuteScalar();
        return o == null ? 0 : Convert.ToInt32(o);
    }
    catch { return 0; }
}

// fetch 后：若该源已索引，用全文算向量存 sidecar；失败静默
static void EmbedFulltextSidecar(string dbPath, int itemId, int feedId, string text)
{
    try
    {
        if (!FeedHasVectors(dbPath, feedId)) return;
        var cfg = LoadConfig(dbPath);
        var vec = SafeEmbed(text, cfg, json: false, articleId: itemId, sourceId: feedId).GetAwaiter().GetResult();
        if (vec == null) return;
        int modelId = CurrentEmbeddingModelId(dbPath);
        if (modelId <= 0) return;
        var list = LoadFulltextVecs();
        list.RemoveAll(e => e.ItemId == itemId);
        list.Add((itemId, feedId, modelId, vec));
        SaveFulltextVecs(list);
    }
    catch { /* 嵌入失败不影响抓取 */ }
}

// 补齐单篇 sidecar：全文缓存命中但尚无对应向量时生成（修复「先抓全文后索引」时序缺陷）。
// 幂等：已有当前模型向量的不重复嵌入
static void EnsureFulltextSidecar(string dbPath, int itemId, int feedId, string text)
{
    try
    {
        if (!FeedHasVectors(dbPath, feedId)) return;
        int modelId = CurrentEmbeddingModelId(dbPath);
        if (modelId <= 0) return;
        var list = LoadFulltextVecs();
        if (list.Any(e => e.ItemId == itemId && e.ModelId == modelId)) return;
        var cfg = LoadConfig(dbPath);
        var vec = SafeEmbed(text, cfg, json: false, articleId: itemId, sourceId: feedId).GetAwaiter().GetResult();
        if (vec == null) return;
        list.RemoveAll(e => e.ItemId == itemId);
        list.Add((itemId, feedId, modelId, vec));
        SaveFulltextVecs(list);
    }
    catch { /* 嵌入失败不影响抓取 */ }
}

// 批量回补：给已有全文缓存的文章补 sidecar 向量（--index / --reindex 后调用）；返回成功数
static int BackfillFulltextSidecars(string dbPath, List<(int Id, int FeedId)> items)
{
    int modelId = CurrentEmbeddingModelId(dbPath);
    if (modelId <= 0) return 0;
    var cfg = LoadConfig(dbPath);
    var list = LoadFulltextVecs();
    var toAdd = new List<(int ItemId, int FeedId, int ModelId, float[] Vector)>();
    foreach (var (id, feedId) in items)
    {
        if (list.Any(e => e.ItemId == id && e.ModelId == modelId) || toAdd.Any(e => e.ItemId == id)) continue;
        string? ft = ReadFulltextCache(id);
        if (ft == null) continue;
        var vec = SafeEmbed(ft, cfg, json: false, articleId: id, sourceId: feedId).GetAwaiter().GetResult();
        if (vec == null) continue;
        toAdd.Add((id, feedId, modelId, vec));
    }
    if (toAdd.Count == 0) return 0;
    list.RemoveAll(e => toAdd.Any(a => a.ItemId == e.ItemId));
    list.AddRange(toAdd);
    SaveFulltextVecs(list);
    return toAdd.Count;
}

// CLI：sip --fulltext <id> [--yes] [--json]
// CLI：sip --purge-fulltext [id]（删全部或单篇缓存）
static void PurgeFulltextCli(string arg, string dbPath)
{
    if (string.IsNullOrWhiteSpace(arg))
    {
        Directory.Delete(FulltextDir(), recursive: true);
        Directory.CreateDirectory(FulltextDir());
        Console.WriteLine(Lang.T("All fulltext cache cleared"));
        return;
    }
    if (!int.TryParse(arg, out int itemId)) { SetExit(); Console.WriteLine(Lang.T("The number must be numeric")); return; }
    string p = FulltextPath(itemId);
    if (File.Exists(p)) File.Delete(p);
    var list = LoadFulltextVecs();
    if (list.RemoveAll(e => e.ItemId == itemId) > 0) SaveFulltextVecs(list);
    Console.WriteLine(Lang.T("Cleared cache for article {0}", itemId));
}

// ══════════ 本地文件导入（--import）：支持 txt/md/pdf/epub/docx ══════════
// 导入的文件存 dataDir/imported/；数据库中用特殊 feed "本地导入" 归类
static string ImportedDir() { string d = Path.Combine(dataDir, "imported"); Directory.CreateDirectory(d); return d; }

static long GetOrCreateImportFeed(string dbPath)
{
    using var conn = OpenDb(dbPath);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id FROM Feeds WHERE FeedUrl = 'local://import'";
    var r = cmd.ExecuteScalar();
    if (r != null) return (long)r;

    cmd.CommandText = @"
        INSERT INTO Feeds (Title, FeedUrl, Link, Description, LastFetched, RawXml)
        VALUES ('本地导入', 'local://import', '', '本地导入的文件（PDF/EPUB/DOCX/TXT/MD）', @now, '')
    ";
    cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
    cmd.ExecuteNonQuery();
    cmd.CommandText = "SELECT last_insert_rowid()";
    return (long)cmd.ExecuteScalar()!;
}

// 纯文本读取（txt/md）
static string ReadTextFile(string path)
{
    return File.ReadAllText(path);
}

// 简易 PDF 文本提取（基于文本流解析，非完整 PDF 解析器）
static string ReadPdfFile(string path)
{
    var bytes = File.ReadAllBytes(path);
    var text = new System.Text.StringBuilder();
    // 尝试提取 BT/ET 文本块（PDF 内嵌文本）
    var content = System.Text.Encoding.Latin1.GetString(bytes);
    int idx = 0;
    while (idx < content.Length)
    {
        int btStart = content.IndexOf("BT", idx);
        if (btStart < 0) break;
        int etEnd = content.IndexOf("ET", btStart + 2);
        if (etEnd < 0) break;
        var block = content[(btStart + 2)..etEnd];
        // 提取 Tj/TJ 操作符中的文本
        int pos = 0;
        while (pos < block.Length)
        {
            int tj = block.IndexOf("Tj", pos);
            int tjArr = block.IndexOf("TJ", pos);
            int next = -1;
            string? val = null;
            if (tj >= 0 && (tjArr < 0 || tj < tjArr))
            {
                // (string)Tj
                int parenStart = block.LastIndexOf('(', tj);
                int parenEnd = block.IndexOf(')', tj);
                if (parenStart >= 0 && parenEnd > parenStart)
                    val = block[(parenStart + 1)..parenEnd];
                next = tj + 2;
            }
            else if (tjArr >= 0)
            {
                // [array]TJ
                int bracketStart = block.LastIndexOf('[', tjArr);
                int bracketEnd = block.IndexOf(']', tjArr);
                if (bracketStart >= 0 && bracketEnd > bracketStart)
                {
                    var arr = block[(bracketStart + 1)..bracketEnd];
                    // 提取每个 (string) 元素
                    var strParts = new List<string>();
                    int si = 0;
                    while (si < arr.Length)
                    {
                        int ps = arr.IndexOf('(', si);
                        if (ps < 0) break;
                        int pe = arr.IndexOf(')', ps);
                        if (pe < 0) break;
                        strParts.Add(arr[(ps + 1)..pe]);
                        si = pe + 1;
                    }
                    val = string.Join("", strParts);
                }
                next = tjArr + 2;
            }
            if (val != null)
            {
                // 解码 PDF 转义
                val = val.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t")
                         .Replace("\\(", "(").Replace("\\)", ")").Replace("\\\\", "\\");
                text.Append(val);
            }
            pos = next > pos ? next : pos + 1;
        }
        idx = etEnd + 2;
    }
    if (text.Length > 0) return text.ToString();
    // 回退：提取可读文本（连续 printable 字符 > 10）
    var readable = new System.Text.StringBuilder();
    var cur = new System.Text.StringBuilder();
    for (int i = 0; i < bytes.Length; i++)
    {
        byte b = bytes[i];
        if (b >= 32 && b < 127 || b == 10 || b == 13 || b >= 192)
        {
            cur.Append((char)b);
        }
        else
        {
            if (cur.Length > 10) readable.Append(cur);
            cur.Clear();
        }
    }
    if (cur.Length > 10) readable.Append(cur);
    return readable.ToString();
}

// 简易 EPUB 文本提取（ZIP → XHTML → 去标签）
static string ReadEpubFile(string path)
{
    using var zip = System.IO.Compression.ZipFile.OpenRead(path);
    var text = new System.Text.StringBuilder();
    foreach (var entry in zip.Entries)
    {
        if (entry.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) ||
            entry.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            entry.FullName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            var html = reader.ReadToEnd();
            // 简单去标签
            var clean = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
            clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();
            if (clean.Length > 0)
            {
                text.AppendLine(clean);
                text.AppendLine();
            }
        }
    }
    return text.ToString();
}

// 简易 DOCX 文本提取（ZIP → word/document.xml → 去标签）
static string ReadDocxFile(string path)
{
    using var zip = System.IO.Compression.ZipFile.OpenRead(path);
    var docEntry = zip.GetEntry("word/document.xml");
    if (docEntry == null) return "";
    using var stream = docEntry.Open();
    using var reader = new StreamReader(stream);
    var xml = reader.ReadToEnd();
    var clean = System.Text.RegularExpressions.Regex.Replace(xml, "<[^>]+>", " ");
    clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();
    return clean;
}

static string ReadImportedFile(string path)
{
    var ext = Path.GetExtension(path).ToLowerInvariant();
    return ext switch
    {
        ".txt" or ".md" or ".markdown" => ReadTextFile(path),
        ".pdf" => ReadPdfFile(path),
        ".epub" => ReadEpubFile(path),
        ".docx" => ReadDocxFile(path),
        _ => throw new NotSupportedException($"Unsupported file type: {ext}")
    };
}

// CLI: sip --import <file> [--title <name>] [--json]
static void ImportCli(string[] args, string dbPath)
{
    bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    string? filePath = null;
    string? title = null;
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--title" && i + 1 < args.Length) title = args[++i];
        else if (!args[i].StartsWith("-")) filePath = args[i];
    }

    if (string.IsNullOrEmpty(filePath))
    {
        SetExit(); Console.WriteLine(Lang.T("Usage: sip --import <file> [--title <name>] [--json]"));
        return;
    }

    if (!File.Exists(filePath))
    {
        ReportError("FILE_NOT_FOUND", Lang.T("File not found: {0}", filePath), json: json);
        return;
    }

    var ext = Path.GetExtension(filePath).ToLowerInvariant();
    if (ext is not (".txt" or ".md" or ".markdown" or ".pdf" or ".epub" or ".docx"))
    {
        ReportError("UNSUPPORTED_FORMAT", Lang.T("Unsupported file type: {0}. Supported: txt, md, pdf, epub, docx", ext), json: json);
        return;
    }

    try
    {
        string content = ReadImportedFile(filePath);
        if (string.IsNullOrWhiteSpace(content))
        {
            ReportError("EMPTY_FILE", Lang.T("File is empty or text could not be extracted: {0}", filePath), json: json);
            return;
        }

        string fileName = Path.GetFileNameWithoutExtension(filePath);
        string importedTitle = title ?? fileName;
        string guid = $"import:{Guid.NewGuid():N}";

        // 复制文件到 imported/
        string importDir = ImportedDir();
        string destName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(filePath)}";
        string destPath = Path.Combine(importDir, destName);
        File.Copy(filePath, destPath, true);

        long feedId = GetOrCreateImportFeed(dbPath);

        using var conn = OpenDb(dbPath);
        conn.Open();
        using var tx = conn.BeginTransaction();

        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Items (FeedId, Title, Link, Description, Author, PublishDate, Content, Guid, Status, Version)
            VALUES (@feedId, @title, @link, @desc, @author, @pubdate, @content, @guid, 'active', 1)
        ";
        cmd.Parameters.AddWithValue("@feedId", feedId);
        cmd.Parameters.AddWithValue("@title", importedTitle);
        cmd.Parameters.AddWithValue("@link", destPath);
        cmd.Parameters.AddWithValue("@desc", $"本地导入 · {ext[1..].ToUpper()} · {content.Length} 字");
        cmd.Parameters.AddWithValue("@author", "本地导入");
        cmd.Parameters.AddWithValue("@pubdate", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@guid", guid);
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT last_insert_rowid()";
        long itemId = (long)cmd.ExecuteScalar()!;

        tx.Commit();

        if (json)
            JsonOut(new { success = true, id = itemId, title = importedTitle, file = destPath, feed = "本地导入" });
        else
            Console.WriteLine(Lang.T("Imported: {0} → #{1}", importedTitle, itemId));
    }
    catch (NotSupportedException ex)
    {
        ReportError("UNSUPPORTED_FORMAT", ex.Message, json: json);
    }
    catch (Exception ex)
    {
        ReportError("IMPORT_ERROR", ex.Message, json: json);
    }
}

// CLI: sip --import-rm <id> [--yes] [--json]
static void ImportRmCli(string[] args, string dbPath)
{
    bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    bool yes = args.Any(a => a.Equals("--yes", StringComparison.OrdinalIgnoreCase) || a.Equals("-y", StringComparison.OrdinalIgnoreCase));
    string? idStr = args.FirstOrDefault(a => !a.StartsWith("-") && !a.Equals("rm", StringComparison.OrdinalIgnoreCase));

    if (string.IsNullOrEmpty(idStr) || !int.TryParse(idStr, out int displayId))
    {
        SetExit(); Console.WriteLine(Lang.T("Usage: sip --import-rm <id> [--yes] [--json]"));
        return;
    }

    // 将显示编号转为真实 ItemId（仅在"本地导入"源内查找）
    long realId = 0;
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Id FROM (
                SELECT i.Id,
                       ROW_NUMBER() OVER (ORDER BY i.Id) AS DisplayNum
                FROM Items i
                JOIN Feeds f ON i.FeedId = f.Id
                WHERE f.FeedUrl = 'local://import'
            ) WHERE DisplayNum = @n
        ";
        cmd.Parameters.AddWithValue("@n", displayId);
        var r = cmd.ExecuteScalar();
        if (r != null) realId = (long)r;
    }

    if (realId == 0) { ReportError("ITEM_NOT_FOUND", Lang.T("Article {0} not found", displayId), json: json); return; }

    // 检查是否属于"本地导入"
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT i.Id FROM Items i
            JOIN Feeds f ON i.FeedId = f.Id
            WHERE i.Id = @id AND f.FeedUrl = 'local://import'
        ";
        cmd.Parameters.AddWithValue("@id", realId);
        if (cmd.ExecuteScalar() == null)
        {
            ReportError("NOT_IMPORTED", Lang.T("Article {0} is not an imported file", displayId), json: json);
            return;
        }
    }

    if (!yes)
    {
        Console.Write(Lang.T("Delete imported article {0}? [y/N] ", displayId));
        var key = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (key != "y" && key != "yes") { Console.WriteLine(Lang.T("Cancelled")); return; }
    }

    // 获取文件路径并删除
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Link FROM Items WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", realId);
        var link = cmd.ExecuteScalar()?.ToString();
        if (!string.IsNullOrEmpty(link) && File.Exists(link))
        {
            try { File.Delete(link); } catch { }
        }
    }

    // 删除数据库记录
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Items WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", realId);
        cmd.ExecuteNonQuery();
    }

    if (json)
        JsonOut(new { success = true, deleted = displayId });
    else
        Console.WriteLine(Lang.T("Deleted imported article {0}", displayId));
}

// ══════════ 阅读进度记忆（按文章记录滚动位置，文件存储，零改表）══════════
static string ReadingProgressPath() => Path.Combine(dataDir, "reading_progress.json");

static Dictionary<long, int> LoadReadingProgress()
{
    try
    {
        var d = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(ReadingProgressPath()));
        return d?.ToDictionary(kv => long.Parse(kv.Key), kv => kv.Value) ?? new Dictionary<long, int>();
    }
    catch { return new Dictionary<long, int>(); }
}

static void SaveReadingProgress(Dictionary<long, int> map)
{
    try
    {
        File.WriteAllText(ReadingProgressPath(), JsonSerializer.Serialize(
            map.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            new JsonSerializerOptions { WriteIndented = true }));
    }
    catch { /* 保存失败不影响使用 */ }
}

// ══════════ 来源健康状态（sidecar 文件，零改表）══════════
static string FeedHealthPath() => Path.Combine(dataDir, "feed_health.json");

static Dictionary<int, (int FailCount, string LastError, string LastOkAt)> LoadFeedHealth()
{
    try
    {
        var d = JsonSerializer.Deserialize<Dictionary<string, FeedHealthEntry>>(File.ReadAllText(FeedHealthPath()));
        return d?.ToDictionary(kv => int.Parse(kv.Key), kv => (kv.Value.FailCount, kv.Value.LastError, kv.Value.LastOkAt)) ?? new();
    }
    catch { return new(); }
}

static void SaveFeedHealth(Dictionary<int, (int FailCount, string LastError, string LastOkAt)> map)
{
    try
    {
        File.WriteAllText(FeedHealthPath(), JsonSerializer.Serialize(
            map.ToDictionary(kv => kv.Key.ToString(), kv => new FeedHealthEntry { FailCount = kv.Value.FailCount, LastError = kv.Value.LastError, LastOkAt = kv.Value.LastOkAt }),
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
    catch { }
}

static void RecordFeedFailure(int feedId, string error)
{
    var map = LoadFeedHealth();
    map.TryGetValue(feedId, out var e);
    map[feedId] = (e.FailCount + 1, error, e.LastOkAt);
    SaveFeedHealth(map);
}

static void RecordFeedSuccess(int feedId)
{
    var map = LoadFeedHealth();
    map[feedId] = (0, "", DateTime.Now.ToString("O"));
    SaveFeedHealth(map);
}

// 长期未更新判定：距上次拉取超过「计划间隔 × 3」；无计划/手动按 30 天
static bool IsFeedStale(string schedule, DateTime lastChecked, DateTime now)
{
    var s = TryParseSchedule(schedule);
    if (s == null || s.IsManual) return (now - lastChecked).TotalDays > 30;
    if (s.Interval is TimeSpan iv) return now - lastChecked > iv * 3;
    if (s.IsDaily) return (now - lastChecked).TotalHours > 72;
    if (s.IsWeekly) return (now - lastChecked).TotalDays > 21;
    return (now - lastChecked).TotalDays > 30;
}

// 来源健康状态：正常 / ⚠ 长期未更新 / ✗ 失败 N 次
static string FeedHealthText(int feedId, string schedule, DateTime? lastChecked, DateTime now)
{
    var map = LoadFeedHealth();
    map.TryGetValue(feedId, out var e);
    if (e.FailCount > 0) return Lang.T("✗ 失败 {0} 次", e.FailCount);
    if (lastChecked is DateTime lc && IsFeedStale(schedule, lc, now)) return Lang.T("⚠ 长期未更新");
    return Lang.T("正常");
}

// 从 Feeds.RawXml 解析来源类型与作者（Atom author / RSS managingEditor / dc:creator）
static (string Type, string Author) ParseFeedMeta(string rawXml)
{
    string type = "RSS", author = "";
    try
    {
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(rawXml);
        var nsm = new System.Xml.XmlNamespaceManager(doc.NameTable);
        nsm.AddNamespace("atom", "http://www.w3.org/2005/Atom");
        nsm.AddNamespace("dc", "http://purl.org/dc/elements/1.1/");
        var root = doc.DocumentElement;
        if (root != null && root.LocalName == "feed")
        {
            type = "Atom";
            author = root.SelectSingleNode("atom:author/atom:name", nsm)?.InnerText?.Trim() ?? "";
        }
        else
        {
            author = root?.SelectSingleNode("channel/managingEditor")?.InnerText?.Trim() ?? "";
            if (string.IsNullOrEmpty(author))
                author = root?.SelectSingleNode("channel/dc:creator", nsm)?.InnerText?.Trim() ?? "";
        }
    }
    catch { }
    return (type, author);
}

// CLI：sip --feed-info <编号> [--json] —— 来源身份与健康状态
static void FeedInfoCli(string[] args, string dbPath)
{
    if (args.Length < 1 || !int.TryParse(args[0], out int dn))
    {
        SetExit(); Console.WriteLine(Lang.T("Usage: sip --feed-info <feed-number> [--json]")); return;
    }
    bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    int realId = GetRealId(dn, dbPath);
    if (realId == 0) { ReportError("FEED_NOT_FOUND", Lang.T("Feed number {0} not found", dn), json: json); return; }

    string title = "", link = "", url = "", rawXml = "", schedule = "", lastChecked = "", lastArticle = "";
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Title, Link, FeedUrl, RawXml, Schedule, LastCheckedAt FROM Feeds WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", realId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) { ReportError("FEED_NOT_FOUND", Lang.T("Feed number {0} not found", dn), json: json); return; }
        title = r.GetString(0);
        link = r.IsDBNull(1) ? "" : r.GetString(1);
        url = r.IsDBNull(2) ? "" : r.GetString(2);
        rawXml = r.IsDBNull(3) ? "" : r.GetString(3);
        schedule = r.IsDBNull(4) ? "" : r.GetString(4);
        lastChecked = r.IsDBNull(5) ? "" : r.GetString(5);
    }
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(PublishDate) FROM Items WHERE FeedId = @id AND Status = 'active'";
        cmd.Parameters.AddWithValue("@id", realId);
        var o = cmd.ExecuteScalar();
        lastArticle = o == null || o == DBNull.Value ? "" : o.ToString()!;
    }

    DateTime? lc = lastChecked.Length > 0 ? TryParseIso(lastChecked) : null;
    var (type, author) = ParseFeedMeta(rawXml);
    string status = FeedHealthText(realId, schedule, lc, DateTime.Now);
    var health = LoadFeedHealth();
    health.TryGetValue(realId, out var h);

    if (json)
    {
        JsonOut(new
        {
            success = true,
            data = new
            {
                id = realId,
                title,
                type,
                author,
                link,
                feedUrl = url,
                schedule,
                lastChecked = lc,
                lastArticle,
                status,
                failCount = h.FailCount,
                lastError = h.LastError
            }
        });
        return;
    }

    Console.WriteLine(Lang.T("来源 [编号 {0}] {1}", dn, CjkSpace(StripControlChars(title))));
    Console.WriteLine("─────────────────────");
    Console.WriteLine(Lang.T("  名称 name       : {0}", CjkSpace(StripControlChars(title))));
    Console.WriteLine(Lang.T("  类型 type       : {0}", StripControlChars(type)));
    if (author.Length > 0) Console.WriteLine(Lang.T("  作者 author     : {0}", CjkSpace(StripControlChars(author))));
    if (link.Length > 0) Console.WriteLine(Lang.T("  官网 site       : {0}", StripControlChars(link)));
    Console.WriteLine(Lang.T("  Feed url        : {0}", StripControlChars(url)));
    Console.WriteLine(Lang.T("  上次更新 updated : {0}", lc is DateTime d ? d.ToString("yyyy-MM-dd HH:mm") : Lang.T("从未")));
    if (lastArticle.Length > 0) Console.WriteLine(Lang.T("  最近文章 latest : {0}", StripControlChars(lastArticle)));
    Console.WriteLine(Lang.T("  状态 status     : {0}", status));
}

// ══════════ OPML 导入导出（RSS 标准，零改表）══════════
static string XmlEscape(string s) => s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

// CLI：sip --export-opml [feeds.opml]
static void ExportOpmlCli(string arg, string dbPath)
{
    string file = string.IsNullOrWhiteSpace(arg) ? "feeds.opml" : arg;
    var feeds = new List<(string Title, string Url)>();
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Title, FeedUrl FROM Feeds ORDER BY Id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            feeds.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)));
    }

    var sb = new StringBuilder();
    sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
    sb.AppendLine("<opml version=\"2.0\">");
    sb.AppendLine("  <head><title>sip feeds</title></head>");
    sb.AppendLine("  <body>");
    foreach (var (t, u) in feeds)
        if (!string.IsNullOrWhiteSpace(u))
            sb.AppendLine($"    <outline type=\"rss\" text=\"{XmlEscape(t)}\" title=\"{XmlEscape(t)}\" xmlUrl=\"{XmlEscape(u)}\"/>");
    sb.AppendLine("  </body>");
    sb.AppendLine("</opml>");
    try
    {
        File.WriteAllText(file, sb.ToString());
    }
    catch (Exception ex)
    {
        SetExit();
        Console.WriteLine(Lang.T("Export OPML failed: {0}", ex.Message));
        return;
    }
    Console.WriteLine(Lang.T("Exported {0} feeds to {1}", feeds.Count(f => f.Url.Length > 0), file));
}

static bool FeedUrlExists(string dbPath, string url)
{
    try
    {
        using var conn = OpenDb(dbPath);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Feeds WHERE FeedUrl = @u";
        cmd.Parameters.AddWithValue("@u", url);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
    catch { return false; }
}

// CLI：sip --import-opml <file.opml>（逐条下载添加，已存在的跳过）
static void ImportOpmlCli(string file, string dbPath)
{
    if (!File.Exists(file)) { SetExit(); Console.WriteLine(Lang.T("File not found: {0}", file)); return; }
    var urls = new List<string>();
    try
    {
        var doc = new System.Xml.XmlDocument();
        doc.Load(file);
        var nodes = doc.SelectNodes("//outline[@xmlUrl]");
        if (nodes != null)
            foreach (System.Xml.XmlNode n in nodes)
            {
                string u = n.Attributes?["xmlUrl"]?.Value ?? "";
                if (!string.IsNullOrWhiteSpace(u)) urls.Add(u.Trim());
            }
    }
    catch (Exception ex) { SetExit(); Console.WriteLine(Lang.T("Parse OPML failed: {0}", ex.Message)); return; }

    if (urls.Count == 0) { Console.WriteLine(Lang.T("No feeds found in the OPML file")); return; }
    int ok = 0, skip = 0, fail = 0;
    foreach (var u in urls)
    {
        if (FeedUrlExists(dbPath, u)) { skip++; continue; }
        try { DownloadAndSaveToDb(u, dbPath, interactive: false).Wait(); ok++; }
        catch { fail++; }
    }
    Console.WriteLine(Lang.T("Import done: {0} added, {1} skipped (already exist), {2} failed", ok, skip, fail));
}

// ══════════ 文章标记信号（article_signals.json，零改表）══════════
// 与 telemetry 分离：signals = 结论/标记层，telemetry 只记 article_like 事实
static string SignalsPath() => Path.Combine(dataDir, "article_signals.json");

static Dictionary<string, SignalEntry> LoadSignals()
{
    try
    {
        var d = JsonSerializer.Deserialize<Dictionary<string, SignalEntry>>(File.ReadAllText(SignalsPath()));
        return d ?? new Dictionary<string, SignalEntry>();
    }
    catch { return new Dictionary<string, SignalEntry>(); }
}

static void SaveSignals(Dictionary<string, SignalEntry> map)
{
    try
    {
        File.WriteAllText(SignalsPath(), JsonSerializer.Serialize(map,
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
    catch { }
}

static SignalEntry? GetSignal(int itemId)
{
    var map = LoadSignals();
    return map.TryGetValue(itemId.ToString(), out var e) ? e : null;
}

// 切换用户/AI 点赞（再执行 = 取消）；返回切换后是否已标记
static bool ToggleSignal(int itemId, bool ai, string? reason, string dbPath)
{
    var map = LoadSignals();
    var key = itemId.ToString();
    map.TryGetValue(key, out var e);
    e ??= new SignalEntry();
    bool liked;
    if (ai)
    {
        e.AiLike = !e.AiLike;
        if (e.AiLike) e.AiReason = string.IsNullOrWhiteSpace(reason) ? "" : reason;
        liked = e.AiLike;
    }
    else
    {
        e.UserLike = !e.UserLike;
        liked = e.UserLike;
    }
    e.UpdatedAt = DateTime.Now.ToString("O");
    if (!e.UserLike && !e.AiLike) map.Remove(key); else map[key] = e;
    SaveSignals(map);
    TelemetryService.Record("article_like", articleId: itemId, sourceId: GetArticleFeedId(itemId, dbPath), data: new { actor = ai ? "ai" : "user", liked });
    return liked;
}

// 文章归属的源 Id（供遥测把点赞归因到源；找不到返回 null）
static int? GetArticleFeedId(int itemId, string dbPath)
{
    try
    {
        using var conn = OpenDb(dbPath);
        conn.Open();
        var c = conn.CreateCommand();
        c.CommandText = "SELECT FeedId FROM Items WHERE Id = @id";
        c.Parameters.AddWithValue("@id", itemId);
        var o = c.ExecuteScalar();
        return o == null ? null : Convert.ToInt32(o);
    }
    catch { return null; }
}

// CLI：sip --like <id> [--ai [reason]]（切换）
static void LikeCli(string[] args, string dbPath)
{
    if (args.Length < 1 || !int.TryParse(args[0], out int itemId))
    {
        SetExit(); Console.WriteLine(Lang.T("Usage: sip --like <article-id> [--ai [reason]]")); return;
    }
    if (!ArticleExists(itemId, dbPath)) { ReportError("ITEM_NOT_FOUND", Lang.T("Article {0} not found", itemId)); return; }
    bool ai = args.Any(a => a.Equals("--ai", StringComparison.OrdinalIgnoreCase));
    string? reason = null;
    int idx = Array.FindIndex(args, a => a.Equals("--ai", StringComparison.OrdinalIgnoreCase));
    if (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--"))
        reason = string.Join(" ", args.Skip(idx + 1));
    bool liked = ToggleSignal(itemId, ai, reason, dbPath);
    Console.WriteLine(ai
        ? (liked ? Lang.T("AI 标记了文章 {0}（用户可能喜欢）", itemId) : Lang.T("已取消 AI 标记 {0}", itemId))
        : (liked ? Lang.T("已收藏文章 {0} ♥", itemId) : Lang.T("已取消收藏 {0}", itemId)));
}

// CLI：sip --likes [--json] —— 列出所有标记文章
static void LikesCli(string[] args, string dbPath)
{
    bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    var map = LoadSignals();
    var ids = map.Keys.Where(k => int.TryParse(k, out _)).Select(int.Parse).ToList();
    if (ids.Count == 0)
    {
        if (json) JsonOut(new { success = true, data = new { signals = Array.Empty<object>() } });
        else Console.WriteLine(Lang.T("No liked articles yet"));
        return;
    }
    // 查标题
    var titles = new Dictionary<int, string>();
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Title FROM Items WHERE Id IN (" + string.Join(",", ids) + ")";
        using var r = cmd.ExecuteReader();
        while (r.Read()) titles[r.GetInt32(0)] = r.GetString(1);
    }
    if (json)
    {
        JsonOut(new
        {
            success = true,
            data = new
            {
                signals = ids.OrderBy(id => id).Select(id =>
                {
                    map.TryGetValue(id.ToString(), out var e);
                    return new
                    {
                        itemId = id,
                        title = titles.TryGetValue(id, out var t) ? t : "",
                        userLike = e?.UserLike ?? false,
                        aiLike = e?.AiLike ?? false,
                        aiReason = e?.AiReason ?? "",
                        updatedAt = e?.UpdatedAt ?? ""
                    };
                })
            }
        });
        return;
    }
    foreach (var id in ids.OrderBy(id => id))
    {
        map.TryGetValue(id.ToString(), out var e);
        string marks = (e?.UserLike == true ? "♥" : "") + (e?.AiLike == true ? "🤖" : "");
        Console.WriteLine($"[{id}] {marks} {StripControlChars(titles.TryGetValue(id, out var t) ? t : "")}" + (string.IsNullOrEmpty(e?.AiReason) ? "" : $"  ({StripControlChars(e.AiReason)})"));
    }
}

// CLI：sip telemetry status|show|enable|disable|clear|export
static void TelemetryCli(string[] args, string dbPath)
{
    string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
    switch (sub)
    {
        case "status":
        {
            var (count, first, last) = TelemetryService.Stats();
            Console.WriteLine(Lang.T("苏暖泉: {0}", TelemetryService.Consent == "enabled" ? Lang.T("开启") : TelemetryService.Consent == "disabled" ? Lang.T("未开启（你拒绝了）") : Lang.T("未开启（还没选择）")));
            Console.WriteLine(Lang.T("  事件数 events   : {0}", count));
            Console.WriteLine(Lang.T("  首次记录 first   : {0}", first ?? Lang.T("—")));
            Console.WriteLine(Lang.T("  最后记录 last    : {0}", last ?? Lang.T("—")));
            if (TelemetryService.Consent == "unset")
                Console.WriteLine(Lang.T("  提示：苏暖泉默认不在；如需开启运行 sip telemetry enable"));
            return;
        }
        case "show":
        {
            int limit = 20;
            for (int i = 1; i < args.Length - 1; i++)
                if (args[i] == "--limit" && int.TryParse(args[i + 1], out int n)) limit = Math.Clamp(n, 1, 1000);
            var events = TelemetryService.AllEvents(limit);
            foreach (var e in events)
            {
                string ts = e.Timestamp.Length >= 19 ? e.Timestamp[..19].Replace("T", " ") : e.Timestamp;
                string extra = e.ArticleId.HasValue ? " article=" + e.ArticleId : "";
                if (e.Surface != null) extra += " surface=" + e.Surface;
                Console.WriteLine($"{ts} {e.Type}{extra}" + (string.IsNullOrEmpty(e.DataJson) ? "" : $"  {e.DataJson}"));
            }
            if (events.Count == 0) Console.WriteLine(Lang.T("苏暖泉还没有记录到什么"));
            return;
        }
        case "enable":
        {
            if (TelemetryService.Consent == "enabled")
            {
                Console.WriteLine(Lang.T("苏暖泉已开启（仅本地记录，不会上传）"));
                return;
            }
            bool yes = args.Any(a => a.Equals("--yes", StringComparison.OrdinalIgnoreCase) || a.Equals("-y", StringComparison.OrdinalIgnoreCase));
            if (!yes)
            {
                Console.WriteLine(Lang.T("苏暖泉将开始记录：哪些文章被打开/读完/跳过、以及 AI 调用与搜索情况。数据仅保存在本机，sip 绝不会自动上传；你随时可用 telemetry disable 关闭、export 导出。"));
                Console.Write(Lang.T("开启吗？(y/n) "));
                if (Console.ReadLine()?.Trim().ToLower() != "y")
                {
                    Console.WriteLine(Lang.T("已取消，苏暖泉仍未工作"));
                    return;
                }
            }
            TelemetryService.SetConsent("enabled");
            Console.WriteLine(Lang.T("苏暖泉来啦（仅本地记录，不会上传）"));
            return;
        }
        case "disable":
            TelemetryService.SetConsent("disabled");
            Console.WriteLine(Lang.T("苏暖泉已离开（历史数据保留，不再记录新事件）"));
            return;
        case "clear":
            TelemetryService.Clear();
            Console.WriteLine(Lang.T("苏暖泉已清空工作记录（保留你的开关选择）"));
            return;
        case "export":
        {
            string file = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "telemetry.json";
            var events = TelemetryService.AllEvents();
            var arr = events.Select(e => new
            {
                id = e.Id,
                timestamp = e.Timestamp,
                sessionId = e.SessionId,
                type = e.Type,
                articleId = e.ArticleId,
                sourceId = e.SourceId,
                versionId = e.VersionId,
                surface = e.Surface,
                position = e.Position,
                data = string.IsNullOrEmpty(e.DataJson) ? null : System.Text.Json.Nodes.JsonNode.Parse(e.DataJson)
            });
            File.WriteAllText(file, JsonSerializer.Serialize(new { exportedAt = DateTime.Now.ToString("O"), events = arr },
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Console.WriteLine(Lang.T("Exported {0} events to {1}", events.Count, file));
            Console.WriteLine(Lang.T("这些数据仅由你决定是否分享给开发者，sip 不会自动上传"));
            return;
        }
        default:
            SetExit();
            Console.WriteLine(Lang.T("Usage: sip telemetry status | show [--limit N] | enable | disable | clear | export [file]"));
            return;
    }
}

// ══════════ Sip Today v1（规则式每日清单，先引导习惯，不做个性化）══════════
// 选文规则（可解释、无黑盒）：近 48h 新增 / 近期被作者更新 / 全文质量 / ♥🤖 标记加权。
// 等 telemetry 积累足够行为数据后，再演进为个性化排序。
//
// 「一天一碗」：当日清单缓存到 sip_today_cache.json，一天只生成一次（仪式感、防反复刷）；
// --refresh 可显式重新生成（新订阅/新标记当天可见）；进度(done/target)保持实时不进缓存。
static string TodayCachePath() => Path.Combine(dataDir, "sip_today_cache.json");

// 返回(缓存日期, 生成时间, 条目, 批次, 已读 itemId)；缓存缺失/损坏返回空
static (string Date, string GeneratedAt, List<TodayItem> Items, int Batch, List<int> Read) LoadTodayCache()
{
    try
    {
        if (!File.Exists(TodayCachePath())) return ("", "", new List<TodayItem>(), 0, new List<int>());
        var doc = JsonDocument.Parse(File.ReadAllText(TodayCachePath()));
        var root = doc.RootElement;
        string date = root.TryGetProperty("date", out var d) ? d.GetString() ?? "" : "";
        string genAt = root.TryGetProperty("generatedAt", out var g) ? g.GetString() ?? "" : "";
        int batch = root.TryGetProperty("batch", out var b) && b.TryGetInt32(out var bi) ? bi : 0;
        var read = new List<int>();
        if (root.TryGetProperty("read", out var ra) && ra.ValueKind == JsonValueKind.Array)
            foreach (var x in ra.EnumerateArray()) if (x.TryGetInt32(out var xi)) read.Add(xi);
        var items = new List<TodayItem>();
        if (root.TryGetProperty("items", out var arr))
            foreach (var it in arr.EnumerateArray())
            {
                items.Add(new TodayItem
                {
                    ItemId = it.TryGetProperty("itemId", out var ii) ? ii.GetInt32() : 0,
                    Title = it.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    Source = it.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "",
                    Reason = it.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "",
                    Minutes = it.TryGetProperty("minutes", out var m) ? m.GetDouble() : 0,
                    Score = it.TryGetProperty("score", out var sc) ? sc.GetInt32() : 0
                });
            }
        return (date, genAt, items, batch, read);
    }
    catch { return ("", "", new List<TodayItem>(), 0, new List<int>()); }   // 缓存损坏 → 当无缓存
}

static void SaveTodayCache(string date, List<TodayItem> items, int batch, List<int> read)
{
    try
    {
        File.WriteAllText(TodayCachePath(), JsonSerializer.Serialize(new
        {
            date,
            generatedAt = DateTime.Now.ToString("O"),
            batch,
            read,
            items = items.Select(i => new
            {
                itemId = i.ItemId, title = i.Title, source = i.Source,
                reason = i.Reason, minutes = i.Minutes, score = i.Score
            })
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
    catch { }
}

// 今日清单：refresh=false 时当天固定（读缓存）；跨天/无缓存/损坏/refresh=true 时重算并落盘
static List<TodayItem> GetTodayList(string dbPath, int limit, bool refresh, out string generatedAt)
{
    string today = DateTime.Now.ToString("yyyy-MM-dd");
    var (cacheDate, cacheAt, cacheItems, cacheBatch, cacheRead) = LoadTodayCache();
    if (!refresh && cacheDate == today && cacheItems.Count > 0)
    {
        // 缓存里的生成时间是 ISO，格式化到 HH:mm 便于展示
        generatedAt = TryParseIso(cacheAt) is DateTime g ? g.ToString("HH:mm") : cacheAt;
        return cacheItems.Take(limit).ToList();
    }
    var items = BuildTodayList(dbPath, limit);
    // 同一天重算 → 批次 +1（新一天从第 1 批开始）；已读沿用当天
    int batch = (cacheDate == today ? cacheBatch : 0) + 1;
    var read = cacheDate == today ? cacheRead : new List<int>();
    SaveTodayCache(today, items, batch, read);
    generatedAt = DateTime.Now.ToString("HH:mm");
    return items;
}

static List<TodayItem> BuildTodayList(string dbPath, int limit = 10)
{
    var items = new List<TodayItem>();
    var signals = LoadSignals();
    var now = DateTime.Now;
    var freshCutoff = now.AddHours(-48);

    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var cmd = conn.CreateCommand();
        // 只考虑近 30 天文章:评分里 7 天外全是负分,旧文不可能进今日哈汤;
        // 且不读 Content/Description 全文本(只取长度)——百万库全表+全文读取曾是启动页卡死的元凶
        cmd.CommandText = @"
            SELECT Id, Title, PublishDate, Version, LENGTH(Content), LENGTH(Description), FeedTitle
            FROM (
                SELECT i.Id, i.Title, i.PublishDate, i.Version, i.Content, i.Description,
                       i.Guid, f.Title AS FeedTitle,
                       ROW_NUMBER() OVER (PARTITION BY i.Guid ORDER BY i.Version DESC) AS rn
                FROM Items i JOIN Feeds f ON i.FeedId = f.Id
                WHERE i.Status = 'active' AND i.Guid IS NOT NULL AND i.PublishDate >= @cut
            )
            WHERE Guid = '' OR rn = 1
            ORDER BY Id";
        cmd.Parameters.AddWithValue("@cut", now.AddDays(-30).ToString("O"));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            int itemId = r.GetInt32(0);
            string title = r.GetString(1);
            DateTime? pub = r.IsDBNull(2) ? null : TryParseIso(r.GetString(2));
            int version = r.GetInt32(3);
            int contentLen = r.IsDBNull(4) ? 0 : r.GetInt32(4);
            int descLen = r.IsDBNull(5) ? 0 : r.GetInt32(5);
            string source = r.GetString(6);

            int score = 0;
            var reasons = new List<string>();
            if (pub is DateTime p && p >= freshCutoff) { score += 3; reasons.Add(Lang.T("新增")); }
            else if (pub is DateTime p2 && p2 >= now.AddDays(-7)) { score += 1; }
            else score -= 1;
            if (version > 1) { score += 2; reasons.Add(Lang.T("有更新")); }
            string q = ContentQualityByLen(contentLen, descLen);
            if (q == "full") score += 1;
            else if (q == "empty") score -= 1;
            if (q == "short") reasons.Add(Lang.T("仅摘要"));

            if (signals.TryGetValue(itemId.ToString(), out var sig))
            {
                if (sig.AiLike) { score += 3; reasons.Add(Lang.T("AI 关注")); }
                if (sig.UserLike) { score += 2; reasons.Add(Lang.T("你收藏过")); }
            }

            int chars = Math.Max(contentLen, descLen);
            double minutes = Math.Max(0.5, Math.Round(chars / (5.0 * 60.0), 1));   // ≈300 字/分
            // 没有任何具体理由时给个兜底（近期可读全文）
            if (reasons.Count == 0) reasons.Add(q == "short" ? Lang.T("仅摘要") : Lang.T("近期"));
            items.Add(new TodayItem
            {
                ItemId = itemId, Title = title, Source = source,
                Reason = string.Join(" · ", reasons), Minutes = minutes, Score = score
            });
        }
    }

    return items.Where(i => i.Score >= 2)          // 有明确正面信号才进今日
                .OrderByDescending(i => i.Score)
                .ThenByDescending(i => i.ItemId)
                .Take(limit)
                .ToList();
}

// 今日阅读进度：目标固定 5 篇（v1；数据积累后做成配置/自适应）；
// 完成数来自 telemetry 的 article_complete（当天）；遥测关闭时 tracking=false
static (int Done, int Target, bool Tracking) TodayProgress(string dbPath)
{
    const int target = 5;
    if (!TelemetryService.IsEnabled || TelemetryService.Consent != "enabled")
        return (0, target, false);
    int done = 0;
    try
    {
        using var conn = new SqliteConnection($"Data Source={Path.Combine(dataDir, "telemetry.db")}");
        conn.Open();
        var c = conn.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM telemetry_events WHERE type = 'article_complete' AND timestamp LIKE @d";
        c.Parameters.AddWithValue("@d", DateTime.Now.ToString("yyyy-MM-dd") + "%");
        done = Convert.ToInt32(c.ExecuteScalar());
    }
    catch { }
    return (done, target, true);
}

// CLI：sip --today [--json] [--refresh] [--quick N]
// 改动概览：对某篇(Guid)的最近两版跑 diff，输出标题是否变 + 增删行数 + 约±字数（纯事实，零 LLM）。
// 少于两版返回 null。
static TodayModified? ChangeOverview(string guid, string dbPath)
{
    try
    {
        using var conn = OpenDb(dbPath);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Version, Title, Content, Description FROM Items WHERE Guid = @g ORDER BY Version";
        cmd.Parameters.AddWithValue("@g", guid);
        var rows = new List<(int V, string T, string Body)>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                rows.Add((r.GetInt32(0), r.GetString(1),
                    string.IsNullOrWhiteSpace(r.IsDBNull(2) ? "" : r.GetString(2))
                        ? (r.IsDBNull(3) ? "" : r.GetString(3)) : r.GetString(2)));
        }
        if (rows.Count < 2) return null;

        var oldV = rows[rows.Count - 2];
        var newV = rows[rows.Count - 1];
        var diff = new InlineDiffBuilder(new Differ()).BuildDiffModel(oldV.Body, newV.Body);
        int added = 0, removed = 0;
        foreach (var l in diff.Lines)
        {
            if (l.Type == ChangeType.Inserted) added++;
            else if (l.Type == ChangeType.Deleted) removed++;
        }
        int OldChars(string s) { int n = 0; foreach (char c in s) if (!char.IsWhiteSpace(c)) n++; return n; }
        return new TodayModified
        {
            ItemId = LatestIdForGuid(guid, dbPath),
            Title = newV.T,
            TitleChanged = oldV.T != newV.T,
            AddedLines = added,
            RemovedLines = removed,
            WordDelta = OldChars(newV.Body) - OldChars(oldV.Body)
        };
    }
    catch { return null; }
}

// 某 Guid 的最新 active 版本 Id（供 --diff/--versions 定位）
static int LatestIdForGuid(string guid, string dbPath)
{
    try
    {
        using var conn = OpenDb(dbPath);
        conn.Open();
        var c = conn.CreateCommand();
        c.CommandText = "SELECT Id FROM Items WHERE Guid = @g AND Status = 'active' ORDER BY Version DESC LIMIT 1";
        c.Parameters.AddWithValue("@g", guid);
        var o = c.ExecuteScalar();
        return o == null ? 0 : Convert.ToInt32(o);
    }
    catch { return 0; }
}

// 构建今日变化摘要：新增按源计数 + 高频源标记 + 被作者改过（改动概览）
static TodayDigest BuildTodayDigest(string dbPath, int windowHours)
{
    var d = new TodayDigest();
    var now = DateTime.Now;
    string cutoff = now.AddHours(-windowHours).ToString("O");
    var settings = LoadSettings();

    // ① 新增按源（Version=1：真正新发布，避免与「被改过」重复计数）
    var newBySource = new List<SourceCount>();
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var c = conn.CreateCommand();
        c.CommandText = @"
            SELECT f.Title, COUNT(*) FROM Items i JOIN Feeds f ON i.FeedId = f.Id
            WHERE i.Status = 'active' AND i.Version = 1 AND i.PublishDate >= @cut
            GROUP BY f.Id ORDER BY COUNT(*) DESC";
        c.Parameters.AddWithValue("@cut", cutoff);
        using var r = c.ExecuteReader();
        while (r.Read())
            newBySource.Add(new SourceCount { Source = r.GetString(0), Count = r.GetInt32(1) });
    }

    // ② 高频/腹泻式判定：单日新增 > 绝对阈值(20) 或 远超中位数×5（避免少源时中位数被高频源撑高）
    double hours = Math.Max(1, windowHours);
    var perDay = newBySource.Select(s => s.Count / (hours / 24.0)).ToList();
    double median = perDay.Count > 0 ? perDay.OrderBy(x => x).ElementAt(perDay.Count / 2) : 0;
    foreach (var s in newBySource)
    {
        double pd = s.Count / (hours / 24.0);
        bool flood;
        if (settings.FloodThresholdPerDay is int ft)
            flood = pd > ft;
        else
            flood = pd > 20 || pd > median * 5;
        s.Flood = flood;
        d.NewTotal += s.Count;
    }
    d.NewBySource = newBySource;
    d.SourceCount = newBySource.Count;

    // ③ 被作者改过：Version>1 且近期被归档（旧版 ArchivedAt >= cutoff）
    var modGuids = new List<(string Guid, string Title, string Feed)>();
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var c = conn.CreateCommand();
        c.CommandText = @"
            SELECT i.Guid, i.Title, f.Title
            FROM Items i JOIN Feeds f ON i.FeedId = f.Id
            JOIN Items a ON a.Guid = i.Guid AND a.Status = 'archived'
            WHERE i.Status = 'active' AND i.Version > 1
            GROUP BY i.Guid HAVING MAX(a.ArchivedAt) >= @cut";
        c.Parameters.AddWithValue("@cut", cutoff);
        using var r = c.ExecuteReader();
        while (r.Read())
            modGuids.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
    }
    foreach (var m in modGuids)
    {
        var ov = ChangeOverview(m.Guid, dbPath);
        if (ov == null) continue;
        ov.Source = m.Feed;
        if (ov.ItemId == 0) ov.ItemId = LatestIdForGuid(m.Guid, dbPath);
        d.Modified.Add(ov);
    }

    // ④ 可能同文（重复簇）——窗口与 digest 一致
    d.Dedups = FindDuplicateClusters(dbPath, windowHours);
    return d;
}

// 打印今日变化摘要（文本）
static void PrintTodayDigest(TodayDigest d)
{
    if (d.NewTotal == 0 && d.Modified.Count == 0 && d.Dedups.Count == 0) return;   // 无变化不打扰
    Console.WriteLine("─────────────────────");
    Console.WriteLine(Lang.T("今日变化 · 新增 {0} 篇 · 来自 {1} 个源 · 被改过 {2} 篇 · 可能同文 {3} 组", d.NewTotal, d.SourceCount, d.Modified.Count, d.Dedups.Count));

    var normal = d.NewBySource.Where(s => !s.Flood).ToList();
    var flood = d.NewBySource.Where(s => s.Flood).ToList();
    if (normal.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine(Lang.T("正常源"));
        foreach (var s in normal.Take(8))
            Console.WriteLine($"├ {CjkSpace(s.Source)}   +{s.Count}");
        if (normal.Count > 8)
            Console.WriteLine(Lang.T("└ 其他 {0} 源  +{1}", normal.Count - 8, normal.Skip(8).Sum(x => x.Count)));
    }
    if (flood.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine(Lang.T("⚠ 高频源（大量更新）"));
        foreach (var s in flood)
            Console.WriteLine($"├ {CjkSpace(s.Source)}   +{s.Count}");
    }
    if (d.Modified.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine(Lang.T("被作者改过（48h 内）"));
        foreach (var m in d.Modified)
        {
            var bits = new List<string>();
            if (m.TitleChanged) bits.Add(Lang.T("标题改了"));
            if (m.AddedLines > 0 || m.RemovedLines > 0) bits.Add(Lang.T("正文 +{0}/-{1} 行", m.AddedLines, m.RemovedLines));
            if (m.WordDelta != 0) bits.Add(Lang.T("约 {0:+0;-0;0} 字", m.WordDelta));
            string desc = bits.Count > 0 ? string.Join(" · ", bits) : Lang.T("内容有变");
            Console.WriteLine($"├ {StripControlChars(m.Title)}  ✎ {desc}  →  sip --diff {m.ItemId}");
        }
    }
    if (d.Dedups.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine(Lang.T("⚠ 可能同文（重复簇）· 显示前 {0} 组", Math.Min(10, d.Dedups.Count)));
        foreach (var c in d.Dedups.Take(10))
        {
            Console.WriteLine($"簇 {c.Size} 篇 · 重合度 ≥ {c.MinOverlap}% · 代表 [{c.RepresentativeId}] {StripControlChars(c.Title)} ({c.Source})");
            Console.WriteLine($"│   隐藏其余: sip --dedup hide-cluster {c.RepresentativeId}");
        }
    }
}

// ══════════ 跨源去重（dedup.json）：用户确认后隐藏重复，导入时跳过 ══════════
static string DedupPath() => Path.Combine(dataDir, "dedup.json");

static Dictionary<string, DedupRule> LoadDedup()
{
    try
    {
        string? text = SimonReadText(DedupPath());
        if (text != null)
            return JsonSerializer.Deserialize<Dictionary<string, DedupRule>>(text) ?? new();
    }
    catch { }
    return new Dictionary<string, DedupRule>();
}

static void SaveDedup(Dictionary<string, DedupRule> map)
{
    try
    {
        SimonWriteText(DedupPath(), JsonSerializer.Serialize(map,
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
    catch { }
}

// 正文 → 归一化段落列表（去 HTML、按段切、去空白空段）
static List<string> NormalizeParagraphs(string text)
{
    var list = new List<string>();
    if (string.IsNullOrWhiteSpace(text)) return list;
    string plain = StripHtml(text);
    foreach (var raw in plain.Split('\n'))
    {
        string p = raw.Trim();
        if (p.Length > 0) list.Add(p);
    }
    return list;
}

// 段落重合度 = 较小段落集在较大段落集中的匹配数 / 较大段落数（0~1）
static double ParagraphOverlap(List<string> a, List<string> b)
{
    if (a.Count == 0 || b.Count == 0) return 0;
    var bigger = a.Count >= b.Count ? a : b;
    var smaller = a.Count >= b.Count ? b : a;
    var set = new HashSet<string>(bigger);
    int matched = smaller.Count(set.Contains);
    return (double)matched / bigger.Count;
}

// 找跨源近重复候选（窗口内、不同 FeedId、段落重合度 ≥ 阈值）
static List<DedupCandidate> FindNearDuplicates(string dbPath, int windowHours)
{
    var res = new List<DedupCandidate>();
    var settings = LoadSettings();
    double thr = settings.DedupThreshold;
    var now = DateTime.Now;
    string cutoff = now.AddHours(-windowHours).ToString("O");

    var arts = new List<(int Id, int FeedId, string Title, string Feed, string Link, string Body)>();
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var c = conn.CreateCommand();
        // 窗口文章数上限:极端场景(48h 内数万篇)不整窗载入,只取最近 N 篇做去重
        c.CommandText = @"
            SELECT i.Id, i.FeedId, i.Title, f.Title, i.Link,
                   COALESCE(NULLIF(i.Content,''), i.Description,'')
            FROM Items i JOIN Feeds f ON i.FeedId = f.Id
            WHERE i.Status = 'active' AND i.PublishDate >= @cut
            ORDER BY i.PublishDate DESC
            LIMIT 20000";
        c.Parameters.AddWithValue("@cut", cutoff);
        using var r = c.ExecuteReader();
        while (r.Read())
            arts.Add((r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? "" : r.GetString(4), r.IsDBNull(5) ? "" : r.GetString(5)));
    }

    // 预计算段落 + 段落倒排索引（para -> 文章下标），只比较共享段落的候选，避免无关文章 O(n²)
    var paras = arts.Select(a => (a, NormalizeParagraphs(a.Body))).ToList();
    var paraIndex = new Dictionary<string, List<int>>();
    for (int k = 0; k < paras.Count; k++)
        foreach (var p in paras[k].Item2)
        {
            if (!paraIndex.TryGetValue(p, out var lst)) { lst = new List<int>(); paraIndex[p] = lst; }
            lst.Add(k);
        }

    var seen = new HashSet<(int, int)>();
    const int MaxCandidates = 2000;   // 限制候选量，避免真重复大簇时输出爆炸淹没调用方
    for (int i = 0; i < paras.Count && res.Count < MaxCandidates; i++)
    {
        var (a, pa) = paras[i];
        if (pa.Count == 0) continue;
        // 与该篇共享至少一个段落的其他文章（跨源、去重）
        var candidates = new HashSet<int>();
        foreach (var p in pa)
            if (paraIndex.TryGetValue(p, out var lst))
                foreach (var k in lst)
                    if (k != i && arts[k].FeedId != a.FeedId)
                        candidates.Add(k);
        foreach (var j in candidates)
        {
            if (seen.Contains((a.Id, paras[j].a.Id))) continue;
            seen.Add((a.Id, paras[j].a.Id));
            double ov = ParagraphOverlap(pa, paras[j].Item2);
            if (ov >= thr)
            {
                var b = paras[j].a;
                res.Add(new DedupCandidate
                {
                    ItemIdA = a.Id, TitleA = a.Title, SourceA = a.Feed,
                    ItemIdB = b.Id, TitleB = b.Title, SourceB = b.Feed,
                    Overlap = Math.Round(ov * 100, 0),
                    DiffCmd = $"sip --diff {a.Id} {b.Id}"
                });
            }
        }
    }
    return res;
}

// 跨源重复簇：用并查集把互相重复的文章聚成簇（解决 pair 输出爆炸/截断）。
// 输出量 = 簇数量（几行），不截断、不淹没调用方。
static List<DedupCluster> FindDuplicateClusters(string dbPath, int windowHours)
{
    var res = new List<DedupCluster>();
    var settings = LoadSettings();
    double thr = settings.DedupThreshold;
    string cutoff = DateTime.Now.AddHours(-windowHours).ToString("O");

    var arts = new List<(int Id, int FeedId, string Title, string Feed, string Body)>();
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var c = conn.CreateCommand();
        // 窗口文章数上限:极端场景(48h 内数万篇)不整窗载入,只取最近 N 篇做去重
        c.CommandText = @"
            SELECT i.Id, i.FeedId, i.Title, f.Title, COALESCE(NULLIF(i.Content,''), i.Description,'')
            FROM Items i JOIN Feeds f ON i.FeedId = f.Id
            WHERE i.Status = 'active' AND i.PublishDate >= @cut
            ORDER BY i.PublishDate DESC
            LIMIT 20000";
        c.Parameters.AddWithValue("@cut", cutoff);
        using var r = c.ExecuteReader();
        while (r.Read())
            arts.Add((r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4)));
    }
    int n = arts.Count;
    var paras = arts.Select(a => NormalizeParagraphs(a.Body)).ToList();
    var paraIndex = new Dictionary<string, List<int>>();
    for (int k = 0; k < n; k++)
        foreach (var p in paras[k])
        {
            if (!paraIndex.TryGetValue(p, out var lst)) { lst = new List<int>(); paraIndex[p] = lst; }
            lst.Add(k);
        }

    // 并查集
    int[] parent = Enumerable.Range(0, n).ToArray();
    int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
    void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

    var seen = new HashSet<(int, int)>();
    var minOv = new Dictionary<(int, int), double>();
    for (int i = 0; i < n; i++)
    {
        if (paras[i].Count == 0) continue;
        var candidates = new HashSet<int>();
        foreach (var p in paras[i])
            if (paraIndex.TryGetValue(p, out var lst))
                foreach (var k in lst)
                    if (k != i && arts[k].FeedId != arts[i].FeedId)
                        candidates.Add(k);
        foreach (var j in candidates)
        {
            var key = (arts[i].Id, arts[j].Id);
            if (!seen.Add(key)) continue;
            double ov = ParagraphOverlap(paras[i], paras[j]);
            if (ov >= thr)
            {
                Union(i, j);
                minOv[key] = ov;
            }
        }
    }

    // 按根分组
    var groups = new Dictionary<int, List<int>>();
    for (int k = 0; k < n; k++) { int r = Find(k); if (!groups.TryGetValue(r, out var l)) { l = new List<int>(); groups[r] = l; } l.Add(k); }
    foreach (var g in groups.Values.Where(x => x.Count >= 2))
    {
        var members = g.OrderBy(x => arts[x].Id).ToList();
        var rep = members[0];
        double min = 1.0;
        for (int a = 0; a < members.Count; a++)
            for (int b = a + 1; b < members.Count; b++)
            {
                var key = (arts[members[a]].Id, arts[members[b]].Id);
                if (minOv.TryGetValue(key, out var o)) min = Math.Min(min, o);
                else if (minOv.TryGetValue((key.Item2, key.Item1), out var o2)) min = Math.Min(min, o2);
            }
        res.Add(new DedupCluster
        {
            RepresentativeId = arts[rep].Id,
            Title = arts[rep].Title,
            Source = arts[rep].Feed,
            Members = members.Select(x => arts[x].Id).ToList(),
            MinOverlap = Math.Round(min * 100, 0)
        });
    }
    return res;
}

// 隐藏某篇（Status='dedup'）+ 记规则；返回 null=成功，否则错误消息
static string? HideAsDedup(string dbPath, int hiddenId, int canonicalId)
{
    try
    {
        if (hiddenId == canonicalId) return Lang.T("不能隐藏自己（保留与隐藏需为两篇不同文章）");
        if (!ArticleExists(hiddenId, dbPath)) return Lang.T("要隐藏的文章不存在");
        if (!ArticleExists(canonicalId, dbPath)) return Lang.T("保留的文章不存在");
        (string Status, int Feed, string Url) Get(int id)
        {
            using var c = OpenDb(dbPath);
            c.Open();
            var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Status, FeedId, Link FROM Items WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return ("", 0, "");
            return (r.IsDBNull(0) ? "" : r.GetString(0), r.GetInt32(1), r.IsDBNull(2) ? "" : r.GetString(2));
        }
        var h = Get(hiddenId);
        var c = Get(canonicalId);
        if (h.Status == "dedup") return Lang.T("这篇已经是隐藏状态，无需重复隐藏");
        if (c.Status == "dedup") return Lang.T("保留的那篇已被隐藏，不能作为保留对象");
        if (h.Feed == 0 || string.IsNullOrEmpty(h.Url)) return Lang.T("无法定位要隐藏的文章");

        // 校验两篇是否真相似（dedup 语义：隐藏只针对重复内容）
        double ov = ParagraphOverlap(NormalizeParagraphs(ArticleBodyById(hiddenId, dbPath)),
                                     NormalizeParagraphs(ArticleBodyById(canonicalId, dbPath)));
        if (ov < LoadSettings().DedupThreshold)
            return Lang.T("两篇正文不相似（段落重合度仅 {0:P0}），可能不是重复，不予隐藏", ov);

        using (var conn = OpenDb(dbPath))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Items SET Status = 'dedup' WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", hiddenId);
            cmd.ExecuteNonQuery();
        }
        var map = LoadDedup();
        map[$"{h.Feed}:{h.Url}"] = new DedupRule
        {
            HiddenFeedId = h.Feed, HiddenUrl = h.Url,
            CanonicalFeedId = c.Feed, CanonicalUrl = c.Url,
            At = DateTime.Now.ToString("O")
        };
        SaveDedup(map);
        TelemetryService.Record("dedup", sourceId: h.Feed, data: new { action = "hide", hiddenId, canonicalId });
        return null;
    }
    catch (Exception ex) { return Lang.T("隐藏失败：{0}", ex.Message); }
}

// 撤销某条规则 + 恢复被隐藏的文章为 active
static bool UndoDedup(string dbPath, string key)
{
    var map = LoadDedup();
    if (!map.TryGetValue(key, out var rule)) return false;
    map.Remove(key);
    SaveDedup(map);
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Items SET Status = 'active' WHERE FeedId = @f AND Link = @u AND Status = 'dedup'";
        cmd.Parameters.AddWithValue("@f", rule.HiddenFeedId);
        cmd.Parameters.AddWithValue("@u", rule.HiddenUrl);
        cmd.ExecuteNonQuery();
    }
    TelemetryService.Record("dedup", sourceId: rule.HiddenFeedId, data: new { action = "undo", key });
    return true;
}

// 列出已隐藏（dedup'd）的文章（供 list / manage 复查）
static List<(int Id, string Title, string Source, string Key)> ListHiddenDedup(string dbPath)
{
    var map = LoadDedup();
    var res = new List<(int, string, string, string)>();
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var c = conn.CreateCommand();
        c.CommandText = @"
            SELECT i.Id, i.Title, f.Title, i.Link
            FROM Items i JOIN Feeds f ON i.FeedId = f.Id
            WHERE i.Status = 'dedup'";
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            int id = r.GetInt32(0);
            string link = r.IsDBNull(3) ? "" : r.GetString(3);
            int fid = 0;
            using (var f2 = OpenDb(dbPath))
            {
                f2.Open();
                var q = f2.CreateCommand();
                q.CommandText = "SELECT FeedId FROM Items WHERE Id = @id";
                q.Parameters.AddWithValue("@id", id);
                var o = q.ExecuteScalar();
                if (o != null) fid = Convert.ToInt32(o);
            }
            string key = $"{fid}:{link}";
            res.Add((id, r.GetString(1), r.GetString(2), key));
        }
    }
    return res;
}

#pragma warning disable CS0618
// TUI：查看/撤销已隐藏（dedup'd）的文章（manage 界面按 i 进入）
static void ShowHiddenDedupDialog(string dbPath)
{
    var top = new Window { Title = " " + Lang.T("已隐藏的文章") + " ", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
    var list = new FeedManagerList { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1), CanFocus = true };
    var hint = new Label
    {
        Text = Lang.T("  j/k 移动 · r/Enter 撤销忽略 · x 删除 · Esc 返回  "),
        X = 0, Y = Pos.AnchorEnd(0), Width = Dim.Fill(), Height = 1
    };
    top.Add(list, hint);

    void Rebuild()
    {
        var hidden = ListHiddenDedup(dbPath);
        list.SetRows(hidden.Select(h => (h.Id, $"[{h.Id}] {CjkSpace(h.Title)}  ({h.Source})")).ToList());
    }

    try
    {
        Rebuild();
        top.Initialized += (s, e) => list.SetFocus();
        top.KeyDown += (s, e) =>
        {
            if (e.KeyCode == KeyCode.Esc) { top.RequestStop(); e.Handled = true; }
            else if (e.KeyCode == KeyCode.R || e.KeyCode == KeyCode.Enter)
            {
                int id = list.SelectedId;
                if (id != 0)
                {
                    var hidden = ListHiddenDedup(dbPath);
                    var hit = hidden.FirstOrDefault(x => x.Id == id);
                    if (hit.Key.Length > 0) { UndoDedup(dbPath, hit.Key); Rebuild(); }
                }
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.X)
            {
                int id = list.SelectedId;
                if (id != 0)
                {
                    var hidden = ListHiddenDedup(dbPath);
                    var hit = hidden.FirstOrDefault(x => x.Id == id);
                    if (MessageBox.Query(Application.Instance, Lang.T("Notice"), Lang.T("永久删除这篇被隐藏的文章（不可恢复）？"), Lang.T("OK"), Lang.T("Cancel")) == 0)
                    {
                        if (hit.Key.Length > 0) UndoDedup(dbPath, hit.Key);   // 先移除规则
                        string guid = ArticleGuidById(id, dbPath);
                        if (guid.Length > 0) DeleteArticleByGuid(guid, dbPath);   // 再硬删
                        Rebuild();
                    }
                }
                e.Handled = true;
            }
        };
        Application.Run(top);
    }
    catch (Exception ex)
    {
        MessageBox.Query(Application.Instance, Lang.T("Notice"), Lang.T("隐藏列表出错: {0}", ex.Message), Lang.T("OK"));
    }
}
#pragma warning restore CS0618

// CLI：sip --dedup scan | hide <hiddenId> <canonicalId> | list | undo <key> [--json]
// 某文章 Guid（供删除定位）
static string ArticleGuidById(int id, string dbPath)
{
    try
    {
        using var conn = OpenDb(dbPath);
        conn.Open();
        var c = conn.CreateCommand();
        c.CommandText = "SELECT Guid FROM Items WHERE Id = @id";
        c.Parameters.AddWithValue("@id", id);
        return c.ExecuteScalar()?.ToString() ?? "";
    }
    catch { return ""; }
}


// 文章正文（去 HTML）
static string ArticleBodyById(int id, string dbPath)
{
    using var conn = OpenDb(dbPath);
    conn.Open();
    var c = conn.CreateCommand();
    c.CommandText = "SELECT COALESCE(NULLIF(Content,''), Description,'') FROM Items WHERE Id = @id";
    c.Parameters.AddWithValue("@id", id);
    var o = c.ExecuteScalar();
    return StripHtml(o?.ToString() ?? "");
}

static string ArticleTitleById(int id, string dbPath)
{
    try
    {
        using var conn = OpenDb(dbPath);
        conn.Open();
        var c = conn.CreateCommand();
        c.CommandText = "SELECT Title FROM Items WHERE Id = @id";
        c.Parameters.AddWithValue("@id", id);
        return c.ExecuteScalar()?.ToString() ?? "";
    }
    catch { return ""; }
}

// 列宽感知的字符宽度（东亚全角=2，其余=1）
static int CharWidth(char c)
{
    return (c >= 0x1100 && (c <= 0x115F || c == 0x2329 || c == 0x232A
        || (c >= 0x2E80 && c <= 0xA4CF && c != 0x303F)
        || (c >= 0xAC00 && c <= 0xD7A3) || (c >= 0xF900 && c <= 0xFAFF)
        || (c >= 0xFE10 && c <= 0xFE19) || (c >= 0xFE30 && c <= 0xFE6F)
        || (c >= 0xFF00 && c <= 0xFF60) || (c >= 0xFFE0 && c <= 0xFFE6))) ? 2 : 1;
}

// 按可见列宽截断（CJK 感知）
static string TruncateCols(string s, int maxCols)
{
    if (maxCols <= 0 || string.IsNullOrEmpty(s)) return "";
    int used = 0; var sb = new StringBuilder();
    foreach (var ch in s)
    {
        int w = CharWidth(ch);
        if (used + w > maxCols) break;
        sb.Append(ch); used += w;
    }
    return sb.ToString();
}

// 补齐到指定列宽
static string PadCols(string s, int width)
{
    int c = s.GetColumns();
    return c >= width ? s : s + new string(' ', width - c);
}

// GitHub 式左右分栏 diff：左边旧版、右边新版，改动行用 - / + 标出，逐行对齐
static string SideBySideDiff(string a, string b, int width)
{
    var diff = new InlineDiffBuilder(new Differ()).BuildDiffModel(a, b);
    var rows = new List<(string L, string R, bool Chg)>();
    var ls = diff.Lines;
    for (int i = 0; i < ls.Count; i++)
    {
        var l = ls[i];
        if (l.Type == ChangeType.Unchanged) { rows.Add((l.Text, l.Text, false)); }
        else if (l.Type == ChangeType.Deleted)
        {
            if (i + 1 < ls.Count && ls[i + 1].Type == ChangeType.Inserted) { rows.Add((l.Text, ls[i + 1].Text, true)); i++; }
            else rows.Add((l.Text, "", true));
        }
        else if (l.Type == ChangeType.Inserted) { rows.Add(("", l.Text, true)); }
        else rows.Add((l.Text, l.Text, false));
    }
    if (rows.Count == 0) return Lang.T("(两篇正文相同)");

    int half = Math.Max(10, (width - 3) / 2);
    var sb = new StringBuilder();
    sb.AppendLine(PadCols(Lang.T("旧版本"), half - 1) + "│" + PadCols(Lang.T("新版本"), half - 1));
    foreach (var r in rows)
    {
        string L = (r.Chg ? "- " : "  ") + TruncateCols(r.L, half - 2);
        string R = (r.Chg ? "+ " : "  ") + TruncateCols(r.R, half - 2);
        sb.AppendLine(PadCols(L, half) + "│" + R);
    }
    return sb.ToString();
}

#pragma warning disable CS0618
// 单个候选对：看 diff → 选择隐藏哪篇
static void ShowDedupPairDialog(int idA, int idB, string dbPath)
{
    string ta = ArticleTitleById(idA, dbPath);
    string tb = ArticleTitleById(idB, dbPath);
    var dlg = new Window { Title = " 可能同文：选择保留 ", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
    var head = new Label { Text = $"  [{idA}] {CjkSpace(ta)}   vs   [{idB}] {CjkSpace(tb)}", X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
    // diff 仅供查看，不抢焦点（避免方向键去滚正文而非切换按钮）
    var tv = new TextView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(6), ReadOnly = true, WordWrap = false, CanFocus = false };
    string bodyA = ArticleBodyById(idA, dbPath);
    string bodyB = ArticleBodyById(idB, dbPath);
    var hideB = new Button { Text = Lang.T("隐藏 B · 保留 A"), X = 0, Y = Pos.Bottom(tv) + 1 };
    var hideA = new Button { Text = Lang.T("隐藏 A · 保留 B"), X = Pos.Right(hideB) + 1, Y = Pos.Bottom(tv) + 1 };
    var giveup = new Button { Text = Lang.T("放弃"), X = Pos.Right(hideA) + 1, Y = Pos.Bottom(tv) + 1 };
    var hint = new Label { Text = Lang.T("  ←/→ 选按钮 · Enter 执行 · 放弃=暂不决定（保留两篇） · Esc 返回  "), X = 0, Y = Pos.Bottom(hideB) + 1, Width = Dim.Fill(), Height = 1 };
    dlg.Add(head, tv, hideB, hideA, giveup, hint);
    hideB.Accepted += (s, e) => { string? err = HideAsDedup(dbPath, idB, idA); if (err != null) MessageBox.Query(Application.Instance, Lang.T("Notice"), err, Lang.T("OK")); dlg.RequestStop(); };
    hideA.Accepted += (s, e) => { string? err = HideAsDedup(dbPath, idA, idB); if (err != null) MessageBox.Query(Application.Instance, Lang.T("Notice"), err, Lang.T("OK")); dlg.RequestStop(); };
    giveup.Accepted += (s, e) => dlg.RequestStop();
    dlg.Initialized += (s, e) => { giveup.SetFocus(); tv.Text = SideBySideDiff(bodyA, bodyB, Math.Max(20, tv.Viewport.Width)); };
    dlg.KeyDown += (s, e) => { if (e.KeyCode == KeyCode.Esc) { dlg.RequestStop(); e.Handled = true; } };
    Application.Run(dlg);
}

// 跨源去重候选列表（TUI）：Enter 查看 diff 并选择保留/隐藏
static void ShowDedupCandidatesDialog(string dbPath)
{
    var top = new Window { Title = " 跨源重复（可能同文） ", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
    var list = new FeedManagerList { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1), CanFocus = true };
    var hint = new Label { Text = Lang.T("  j/k 移动 · Enter 查看diff并选择 · Esc 返回  "), X = 0, Y = Pos.AnchorEnd(0), Width = Dim.Fill(), Height = 1 };
    top.Add(list, hint);

    void Rebuild()
    {
        var cands = FindNearDuplicates(dbPath, 48);
        list.SetRows(cands.Select(c => (c.ItemIdA,
            $"重合{c.Overlap}%  [{c.ItemIdA}]{CjkSpace(c.TitleA)}({c.SourceA})  ≈  [{c.ItemIdB}]{CjkSpace(c.TitleB)}({c.SourceB})")).ToList());
    }
    Rebuild();
    top.Initialized += (s, e) => list.SetFocus();
    top.KeyDown += (s, e) =>
    {
        if (e.KeyCode == KeyCode.Esc) { top.RequestStop(); e.Handled = true; }
        else if (e.KeyCode == KeyCode.Enter)
        {
            var cand = FindNearDuplicates(dbPath, 48).FirstOrDefault(c => c.ItemIdA == list.SelectedId);
            if (cand != null && cand.ItemIdA != 0) { ShowDedupPairDialog(cand.ItemIdA, cand.ItemIdB, dbPath); Rebuild(); }
            e.Handled = true;
        }
    };
    Application.Run(top);
}
#pragma warning restore CS0618

static void DedupCli(string[] args, string dbPath)
{
    bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    var pos = args.Where(a => !a.StartsWith("--")).ToArray();
    string sub = pos.Length > 0 ? pos[0].ToLowerInvariant() : "scan";

    switch (sub)
    {
        case "scan":
        {
            var clusters = FindDuplicateClusters(dbPath, 48);
            if (json)
            {
                JsonOut(new
                {
                    success = true,
                    data = new { clusters = clusters.Select(c => new
                    {
                        size = c.Size,
                        representativeId = c.RepresentativeId,
                        title = c.Title,
                        source = c.Source,
                        minOverlap = c.MinOverlap,
                        members = c.Members
                    }) }
                });
                return;
            }
            if (clusters.Count == 0) { Console.WriteLine(Lang.T("未发现跨源重复")); return; }
            Console.WriteLine(Lang.T("发现 {0} 组重复簇（段落重合度 ≥ {1:P0}）：", clusters.Count, LoadSettings().DedupThreshold));
            foreach (var c in clusters)
            {
                string others = c.Members.Count > 1 ? string.Join(", ", c.Members.Skip(1).Take(20)) + (c.Members.Count > 21 ? " …" : "") : "";
                Console.WriteLine($"簇 {c.Size} 篇 · 重合度 ≥ {c.MinOverlap}% · 代表 [{c.RepresentativeId}] {StripControlChars(c.Title)} ({c.Source})");
                if (others.Length > 0) Console.WriteLine($"     成员：{others}");
                Console.WriteLine(Lang.T("     保留代表隐藏其余：sip --dedup hide-cluster {0}", c.RepresentativeId));
            }
            return;
        }
        case "hide-cluster":
        {
            if (pos.Length < 2 || !int.TryParse(pos[1], out int repId))
            {
                SetExit(); Console.WriteLine(Lang.T("用法: sip --dedup hide-cluster <代表Id>（隐藏该簇其余成员）")); return;
            }
            var clusters = FindDuplicateClusters(dbPath, 48);
            var cl = clusters.FirstOrDefault(x => x.RepresentativeId == repId || x.Members.Contains(repId));
            if (cl == null || cl.Size < 2) { SetExit(); Console.WriteLine(Lang.T("未找到该簇")); return; }
            int rep = cl.Members.Contains(repId) ? repId : cl.RepresentativeId;
            int ok = 0; var fails = new List<string>();
            foreach (var m in cl.Members)
                if (m != rep)
                {
                    string? err = HideAsDedup(dbPath, m, rep);
                    if (err == null) ok++; else fails.Add(err);
                }
            if (json) JsonOut(new { success = true, data = new { representative = rep, hidden = ok, fails } });
            else Console.WriteLine(Lang.T("已隐藏 {0} 篇（保留 {1}），失败 {2}", ok, rep, fails.Count));
            return;
        }
        case "hide":
        {
            if (pos.Length < 3 || !int.TryParse(pos[1], out int hid) || !int.TryParse(pos[2], out int cid))
            {
                SetExit(); Console.WriteLine(Lang.T("用法: sip --dedup hide <hiddenId> <canonicalId>")); return;
            }
            string? err = HideAsDedup(dbPath, hid, cid);
            bool ok = err == null;
            if (json) JsonOut(ok
                ? new { success = true, data = new { hiddenId = hid, canonicalId = cid, ok = true } }
                : new { success = false, error = new { code = "DEDUP_INVALID", message = err } });
            else Console.WriteLine(ok ? Lang.T("已隐藏 {0}（保留 {1}）", hid, cid) : err);
            if (!ok) SetExit();
            return;
        }
        case "list":
        {
            var hidden = ListHiddenDedup(dbPath);
            if (json)
            {
                JsonOut(new { success = true, data = new { hidden = hidden.Select(h => new { id = h.Id, title = h.Title, source = h.Source, key = h.Key }) } });
                return;
            }
            if (hidden.Count == 0) { Console.WriteLine(Lang.T("暂无已隐藏的重复文章")); return; }
            Console.WriteLine(Lang.T("已隐藏（dedup'd）的文章："));
            foreach (var h in hidden)
                Console.WriteLine($"[{h.Id}] {StripControlChars(h.Title)}  ({h.Source})  →  sip --dedup undo {h.Key}");
            return;
        }
        case "undo":
        {
            if (pos.Length < 2)
            {
                SetExit(); Console.WriteLine(Lang.T("用法: sip --dedup undo <key（见 list 输出）>")); return;
            }
            string key = pos[1];
            bool ok = UndoDedup(dbPath, key);
            if (json) JsonOut(new { success = ok, data = new { key, ok } });
            else Console.WriteLine(ok ? Lang.T("已撤销忽略，文章恢复显示") : Lang.T("未找到该规则"));
            if (!ok) SetExit();
            return;
        }
        default:
            SetExit(); Console.WriteLine(Lang.T("用法: sip --dedup scan | hide <hiddenId> <canonicalId> | list | undo <key> [--json]"));
            return;
    }
}

// 导入时检查跨源去重规则：命中 (feedId,url) → 无条件阻止导入（防卷土重来）。
// 不比对内容、不改写 dedup.json（避免内容为空/瞬时读取失败导致误清空或放行）。
static bool DedupImportBlocked(SqliteConnection conn, long feedId, FeedItem item)
{
    try
    {
        var map = LoadDedup();
        return map.ContainsKey($"{feedId}:{item.Link}");
    }
    catch { return false; }
}

// ══════════ Source Policy（用户确认的规则，source_policy.json）══════════
static string SourcePolicyPath() => Path.Combine(dataDir, "source_policy.json");

static Dictionary<int, SourcePolicyRule> LoadSourcePolicy()
{
    try
    {
        if (File.Exists(SourcePolicyPath()))
            return JsonSerializer.Deserialize<Dictionary<int, SourcePolicyRule>>(File.ReadAllText(SourcePolicyPath())) ?? new();
    }
    catch { }
    return new Dictionary<int, SourcePolicyRule>();
}

static void SaveSourcePolicy(Dictionary<int, SourcePolicyRule> map)
{
    try
    {
        File.WriteAllText(SourcePolicyPath(), JsonSerializer.Serialize(map,
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
    catch { }
}

// 单源规则标记文本（-l / 报告里显示）：#tag · [动作]
static string PolicyMarker(SourcePolicyRule? p)
{
    if (p == null) return "";
    var bits = new List<string>();
    if (!string.IsNullOrWhiteSpace(p.Tag)) bits.Add("#" + p.Tag);
    if (p.Action == "lower_frequency") bits.Add(Lang.T("降频 {0}", p.Schedule));
    else if (p.Action == "archive") bits.Add(Lang.T("已归档"));
    else if (p.Action == "keep") bits.Add(Lang.T("保留"));
    else if (p.Action == "unsubscribe") bits.Add(Lang.T("退订候选"));
    return bits.Count > 0 ? "  [" + string.Join(" · ", bits) + "]" : "";
}

// CLI：sip --policy list | set <feedId> <action> [args] | remove <feedId> [--json]
static void PolicyCli(string[] args, string dbPath)
{
    bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    var pos = args.Where(a => !a.StartsWith("--")).ToArray();
    string sub = pos.Length > 0 ? pos[0].ToLowerInvariant() : "list";

    switch (sub)
    {
        case "list":
        {
            var map = LoadSourcePolicy();
            if (json)
            {
                JsonOut(new { success = true, data = new { policies = map.Select(kv => new { feedId = kv.Key, action = kv.Value.Action, schedule = kv.Value.Schedule, tag = kv.Value.Tag, note = kv.Value.Note, createdBy = kv.Value.CreatedBy, updatedAt = kv.Value.UpdatedAt }) } });
                return;
            }
            if (map.Count == 0) { Console.WriteLine(Lang.T("暂无 Source Policy（用 sip --policy set <feedId> <action> 创建）")); return; }
            foreach (var kv in map.OrderBy(x => x.Key))
                Console.WriteLine($"[{kv.Key}] {Lang.T(kv.Value.Action)}" + (kv.Value.Tag.Length > 0 ? $"  #{kv.Value.Tag}" : "") + (kv.Value.Schedule.Length > 0 ? $"  → {kv.Value.Schedule}" : "") + (kv.Value.Note.Length > 0 ? $"  ({kv.Value.Note})" : ""));
            return;
        }
        case "set":
        {
            if (pos.Length < 3 || !int.TryParse(pos[1], out int feedId))
            {
                SetExit(); Console.WriteLine(Lang.T("用法: sip --policy set <feedId> <archive|keep|lower_frequency|tag|unsubscribe> [args]")); return;
            }
            if (GetRealId(feedId, dbPath) == 0) { ReportError("FEED_NOT_FOUND", Lang.T("Feed number not found"), json: json); return; }
            string action = pos[2].ToLowerInvariant();
            string rest = pos.Length > 3 ? string.Join(" ", pos.Skip(3)) : "";
            var map = LoadSourcePolicy();
            map.TryGetValue(feedId, out var rule);
            rule ??= new SourcePolicyRule();
            rule.Action = action;
            rule.UpdatedAt = DateTime.Now.ToString("O");
            rule.CreatedBy = "user";

            switch (action)
            {
                case "lower_frequency":
                    if (string.IsNullOrWhiteSpace(rest)) { SetExit(); Console.WriteLine(Lang.T("用法: --policy set <id> lower_frequency <30m|1h|7d|daily@08:00|...>")); return; }
                    if (TryParseSchedule(rest) == null)   // 非法计划：不写 policy
                    {
                        SetExit(); Console.WriteLine(Lang.T("无效的计划表达式：{0}", rest)); return;
                    }
                    SetFeedSchedule(GetDisplayNum(feedId, dbPath).ToString(), rest, dbPath);
                    rule.Schedule = rest.Trim().ToLowerInvariant();
                    break;
                case "archive":
                    AddTimestampForRealId(feedId, dbPath);
                    break;
                case "keep":
                    rule.Note = rest;
                    break;
                case "tag":
                    rule.Tag = rest.Split(' ', 2)[0].TrimStart('#');
                    if (rest.Contains(' ')) rule.Note = rest.Substring(rest.IndexOf(' ')).Trim();
                    break;
                case "unsubscribe":
                    rule.Note = rest;
                    break;
                default:
                    SetExit(); Console.WriteLine(Lang.T("未知动作: {0}", action)); return;
            }
            map[feedId] = rule;
            SaveSourcePolicy(map);
            if (json) JsonOut(new { success = true, data = new { feedId, action, tag = rule.Tag, schedule = rule.Schedule, note = rule.Note } });
            else Console.WriteLine(Lang.T("已应用规则：{0}", Lang.T(action)) + PolicyMarker(rule));
            return;
        }
        case "remove":
        {
            if (pos.Length < 2 || !int.TryParse(pos[1], out int feedId))
            {
                SetExit(); Console.WriteLine(Lang.T("用法: sip --policy remove <feedId>")); return;
            }
            var map = LoadSourcePolicy();
            bool ok = map.Remove(feedId);
            SaveSourcePolicy(map);
            if (json) JsonOut(new { success = ok, data = new { feedId, ok } });
            else Console.WriteLine(ok ? Lang.T("已移除规则") : Lang.T("该源无规则"));
            return;
        }
        default:
            SetExit(); Console.WriteLine(Lang.T("用法: sip --policy list | set <feedId> <action> [args] | remove <feedId> [--json]"));
            return;
    }
}

// ══════════ Onboarding：推荐源模板（templates.json，用户可编辑）══════════
static string TemplatesPath() => Path.Combine(dataDir, "templates.json");

static Dictionary<string, List<SourceTemplate>> DefaultTemplates() => new()
{
    ["AI"] = new()
    {
        new SourceTemplate { Name = "Hugging Face Blog", Url = "https://huggingface.co/blog/feed.xml" },
        new SourceTemplate { Name = "GitHub Blog", Url = "https://github.blog/feed/" },
        new SourceTemplate { Name = "OpenAI", Url = "https://openai.com/news/rss.xml" }
    },
    ["开发"] = new()
    {
        new SourceTemplate { Name = "Hacker News", Url = "https://news.ycombinator.com/rss" },
        new SourceTemplate { Name = "GitHub Blog", Url = "https://github.blog/feed/" }
    },
    ["科技公司"] = new()
    {
        new SourceTemplate { Name = "BBC News", Url = "http://feeds.bbci.co.uk/news/rss.xml" }
    }
};

static Dictionary<string, List<SourceTemplate>> LoadTemplates()
{
    try
    {
        if (File.Exists(TemplatesPath()))
            return JsonSerializer.Deserialize<Dictionary<string, List<SourceTemplate>>>(File.ReadAllText(TemplatesPath())) ?? DefaultTemplates();
    }
    catch { }
    return DefaultTemplates();
}

// CLI：sip --onboarding [list] | <category> | add <category> <index|all>
static void OnboardingCli(string[] args, string dbPath)
{
    var templates = LoadTemplates();
    var pos = args.Where(a => !a.StartsWith("--")).ToArray();
    string sub = pos.Length > 0 ? pos[0].ToLowerInvariant() : "list";

    if (sub == "add")
    {
        if (pos.Length < 3)
        {
            SetExit(); Console.WriteLine(Lang.T("用法: sip --onboarding add <category> <index|all>")); return;
        }
        string cat = pos[1];
        if (!templates.TryGetValue(cat, out var list))
        {
            SetExit(); Console.WriteLine(Lang.T("未找到分类 {0}（可用: {1}）", cat, string.Join(", ", templates.Keys))); return;
        }
        string which = pos[2];
        int ok = 0, fail = 0;
        if (which.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var t in list)
            {
                try { DownloadAndSaveToDb(t.Url, dbPath, interactive: false).Wait(); ok++; }
                catch { fail++; Console.WriteLine(Lang.T("添加失败: {0} ({1})", t.Name, t.Url)); }
            }
        }
        else if (int.TryParse(which, out int idx) && idx >= 1 && idx <= list.Count)
        {
            var t = list[idx - 1];
            try { DownloadAndSaveToDb(t.Url, dbPath, interactive: false).Wait(); ok++; }
            catch { fail++; Console.WriteLine(Lang.T("添加失败: {0} ({1})", t.Name, t.Url)); }
        }
        else { SetExit(); Console.WriteLine(Lang.T("索引无效（1~{0} 或 all）", list.Count)); return; }
        Console.WriteLine(Lang.T("完成：成功 {0}，失败 {1}", ok, fail));
        return;
    }

    // list / 指定分类
    Console.WriteLine(Lang.T("开始使用 sip —— 选择你的领域，一键添加订阅源（templates.json 可编辑）："));
    if (sub == "list" || sub == "")
    {
        foreach (var kv in templates)
        {
            Console.WriteLine();
            Console.WriteLine(Lang.T("◆ {0}", kv.Key));
            for (int i = 0; i < kv.Value.Count; i++)
                Console.WriteLine($"  {i + 1}. {kv.Value[i].Name}  {kv.Value[i].Url}");
            Console.WriteLine(Lang.T("   → sip --onboarding add {0} <索引|all>", kv.Key));
        }
        return;
    }
    if (templates.TryGetValue(sub, out var sel))
    {
        Console.WriteLine(Lang.T("◆ {0}", sub));
        for (int i = 0; i < sel.Count; i++)
            Console.WriteLine($"  {i + 1}. {sel[i].Name}  {sel[i].Url}");
        Console.WriteLine(Lang.T("   → sip --onboarding add {0} <索引|all>", sub));
        return;
    }
    SetExit(); Console.WriteLine(Lang.T("未找到分类 {0}（可用: {1}）", sub, string.Join(", ", templates.Keys)));
}


static void TodayCli(string[] args, string dbPath)
{
    bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    bool refresh = args.Any(a => a.Equals("--refresh", StringComparison.OrdinalIgnoreCase));
    int quick = 5;
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i].Equals("--quick", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int q))
            quick = Math.Clamp(q, 1, 5);   // 时间不够就只喝一小口：--quick N
    var (done, target, tracking) = TodayProgress(dbPath);
    var list = GetTodayList(dbPath, quick, refresh, out string generatedAt);   // 一天一碗；--refresh 重生成
    var digest = BuildTodayDigest(dbPath, 48);   // 今日变化摘要（48h 窗口）

    if (json)
    {
        JsonOut(new
        {
            success = true,
            data = new
            {
                date = DateTime.Now.ToString("yyyy-MM-dd"),
                generatedAt,
                refreshed = refresh,
                target,
                done,
                tracking,
                digest = new
                {
                    newTotal = digest.NewTotal,
                    sourceCount = digest.SourceCount,
                    newBySource = digest.NewBySource.Select(s => new { source = s.Source, count = s.Count, flood = s.Flood }),
                    modified = digest.Modified.Select(m => new
                    {
                        itemId = m.ItemId,
                        title = m.Title,
                        source = m.Source,
                        titleChanged = m.TitleChanged,
                        addedLines = m.AddedLines,
                        removedLines = m.RemovedLines,
                        wordDelta = m.WordDelta
                    }),
                    dedups = digest.Dedups.Select(c => new
                    {
                        size = c.Size,
                        representativeId = c.RepresentativeId,
                        title = c.Title,
                        source = c.Source,
                        minOverlap = c.MinOverlap,
                        members = c.Members
                    })
                },
                items = list.Select(i => new
                {
                    itemId = i.ItemId,
                    title = i.Title,
                    source = i.Source,
                    reason = i.Reason,
                    minutes = i.Minutes,
                    score = i.Score
                })
            }
        });
        return;
    }

    Console.WriteLine(Lang.T("今日哈汤 · {0}", DateTime.Now.ToString("yyyy-MM-dd")));
    if (refresh)
        Console.WriteLine(Lang.T("（已重新生成今日哈汤）"));

    // 今日变化摘要
    PrintTodayDigest(digest);

    Console.WriteLine("─────────────────────");
    if (list.Count == 0)
    {
        Console.WriteLine(Lang.T("（今天还没有值得读的——去添加或更新一些订阅源吧）"));
    }
    for (int i = 0; i < list.Count; i++)
    {
        var it = list[i];
        Console.WriteLine($" {i + 1}. {CjkSpace(StripControlChars(it.Title))}");
        Console.WriteLine(Lang.T("    [{0} · ~{1} 分钟 · {2}]", it.Source, it.Minutes, it.Reason));
    }
    Console.WriteLine("─────────────────────");
    double total = list.Sum(i => i.Minutes);
    if (tracking)
        Console.WriteLine(Lang.T("共约 {0} 分钟 · 今日目标 {1} 篇 · 已完成 {2} 篇{3}", total, target, done, done >= target ? Lang.T(" 🎉 今天结束") : ""));
    else
        Console.WriteLine(Lang.T("共约 {0} 分钟 · 今日目标 {1} 篇（与苏暖泉共同阅读可跟踪完成进度）", total, target));
    if (!refresh)
        Console.WriteLine(Lang.T("（今日哈汤已生成于 {0} · --refresh 可重新来一碗 · 新文章随时可从侧栏/--search/--grep 看）", generatedAt));
}

// ══════════ CLI 参数处理 ══════════
static async Task RunCli(string[] args, string dbPath)
{
    var cmd = args[0].ToLower();

    // 进 TUI 并**开启文章图片显示**。
    //
    // 图片默认关闭,只有这个子命令才开。原因是终端 sixel 能力探测不可靠:
    // Terminal.Gui 会发查询序列问终端"支持吗",但 sixel 是 DCS 序列,中间必须过
    // ConPTY(真实的 conhost 实例,不是透传管道),不认识的 DCS 会被吞掉。
    // WezTerm 自带的 ConPTY 太老时(实测 2024-02-03),探测得到"支持"、实际一张
    // 图都出不来,画面还会错乱 —— 所以不做自动判断,交给用户显式决定。
    //
    // **放在孟思琳检查之前**:pic 等价于无参数启动(`sip`),只是多开一个图片
    // 开关,不做任何写操作。而挡位 3 的语义是"只允许通过 TUI 使用" —— pic 本身
    // 就是 TUI 入口,不该被自己的规则挡在门外。
    if (cmd == "pic")
    {
        ImagesEnabled = true;
        var picExit = await RunTui(dbPath);
        SetExit(picExit);
        return;
    }

    // 孟思琳(simon)安全守护:非交互调用按挡位拦截(挡位 1 不拦,行为不变)
    string? simonBlock = SimonCheckBlock(cmd, args);
    if (simonBlock != null)
    {
        SimonRecord("blocked_cmd", $"{cmd} {string.Join(' ', args.Skip(1))}", CurrentSimonLevel());
        bool json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        if (json) JsonOut(new { success = false, error = new { code = "SIMON_BLOCKED", level = CurrentSimonLevel(), message = simonBlock } });
        else Console.WriteLine(Lang.T("🔒 孟思琳(simon) 拦截: {0}", simonBlock));
        SetExit(3);   // 3=策略拒绝
        return;
    }

    // 孟思琳(simon)守护配置(读命令,永不拦截)
    if (cmd == "simon")
    {
        SimonCli(args.Skip(1).ToArray(), dbPath);
        return;
    }


    // 原文阅读：sip --show <文章编号>
    //   默认 → 全屏阅读界面（无侧栏，W 进入完整 TUI，Esc/Q 退出），给人读文章
    //   --json → 原文 JSON 打到标准输出（未渲染），供 AI / 脚本读取
    if (cmd is "--show" or "--content")
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int sNum))
        {
            SetExit(); Console.WriteLine(Lang.T("Usage: sip --show <article-id>"));
            return;
        }
        bool json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        if (json) ShowArticleJson(sNum, dbPath);
        else
        {
            if (!ArticleExists(sNum, dbPath)) { SetExit(); Console.WriteLine(Lang.T("Article {0} not found", sNum)); return; }
            await RunFullscreenReader(sNum, dbPath);
        }
        return;
    }

    if (cmd is "-h" or "--help")
    {
        PrintHelp();
        return;
    }

    if (cmd is "--version")
    {
        string ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";
        string build = "";
        try { build = new FileInfo(Environment.ProcessPath ?? "").LastWriteTime.ToString("yyyy-MM-dd HH:mm"); } catch { }
        Console.WriteLine($"sip v{ver}" + (build.Length > 0 ? $"  (built {build})" : ""));
        return;
    }

    if (cmd is "-l" or "--list")
    {
        bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
        // -l <N> --limit M：限制输出的文章条数（大源省 token 用；0=不限制）
        int listLimit = 0;
        for (int i = 1; i < args.Length - 1; i++)
            if (args[i].Equals("--limit", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int lm))
                listLimit = Math.Clamp(lm, 1, 5000);
        // 找第一个非 flag 的参数作为编号（-l --json 或 -l 1 --json 都能用）
        var numArg = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
        if (numArg != null)
        {
            // -l 后面带编号 → 列出该源的文章
            if (!int.TryParse(numArg, out int lNum)) { SetExit(); Console.WriteLine(Lang.T("The number must be numeric")); return; }
            int feedRealId = GetRealId(lNum, dbPath);
            if (feedRealId == 0)
            {
                if (json) ReportError("FEED_NOT_FOUND", Lang.T("Feed number not found"), json: true);
                else { SetExit(); Console.WriteLine(Lang.T("Feed number not found")); }
                return;
            }
            ListArticlesFromDb(feedRealId, lNum, dbPath, json, listLimit);
        }
        else
        {
            ListFeedsFromDb(dbPath, json);
        }
        return;
    }

    // ══════════ 更新调度命令 ═══════════
    if (cmd is "--schedule" or "--sched")
    {
        if (args.Length < 3)
        {
            SetExit(); Console.WriteLine(Lang.T("Usage: sip --schedule <id> <expr>; expr like 30m / 1h / daily@10:00 / weekly@Mon 08:00 / manual"));
            return;
        }
        SetFeedSchedule(args[1], args[2], dbPath);
        return;
    }
    if (cmd is "--sync")
    {
        await SyncCli(args.Skip(1).ToArray(), dbPath);
        return;
    }
    if (cmd is "--update-all")
    {
        await UpdateAllCli(dbPath);
        return;
    }

    // ══════════ AI 无参数命令（注意不能用 args.Length >= 2 判断）═══════════
    switch (cmd)
    {
        case "--init":
            // 安全边界：--init 涉及录入 API Key，仅在真实交互式终端运行；
            // 拒绝 AI/脚本经管道喂输入（stdin 被重定向时视为非交互）
            if (Console.IsInputRedirected)
            {
                SetExit();
                Console.WriteLine(Lang.T("--init 需在真实交互式终端中手动运行（安全考虑，不接受管道输入）"));
                return;
            }
            InitAiConfigInteractive(dbPath);
            return;
        case "--config":
            ShowConfig(dbPath);
            return;
        case "--index":
            await IndexArticlesCli(new string[] { }, dbPath);
            return;
        case "--reindex":
            await ReindexCli(dbPath);
            return;
        case "--summary-all":
            await SummaryAllCli(dbPath);
            return;
        case "--purge-fulltext":
            PurgeFulltextCli(args.Length > 1 ? args[1] : "", dbPath);
            return;
        case "--export-opml":
            ExportOpmlCli(args.Length > 1 ? args[1] : "", dbPath);
            return;
        case "--likes":
            LikesCli(args.Skip(1).ToArray(), dbPath);
            return;
        case "--today":
            TodayCli(args.Skip(1).ToArray(), dbPath);
            return;
        case "--insights":
            InsightsCli(args.Skip(1).ToArray(), dbPath);
            return;
        case "--insights-interval":
            if (args.Length < 2) { SetExit(); Console.WriteLine(Lang.T("Usage: sip --insights-interval <7d|30d|off>")); return; }
            InsightsIntervalCli(args[1], dbPath);
            return;
        case "--dedup":
            DedupCli(args.Skip(1).ToArray(), dbPath);
            return;
        case "--policy":
            PolicyCli(args.Skip(1).ToArray(), dbPath);
            return;
        case "--onboarding":
            OnboardingCli(args.Skip(1).ToArray(), dbPath);
            return;
    }

    // 已知但需要参数的命令；不在此列的一律当作"已知命令"但少参数，否则是未知命令
    bool needsArg = cmd is "-u" or "--update" or "-d" or "--download" or "-a" or "--archive"
                    or "-una" or "--unarchive" or "-r" or "--remove" or "--search" or "--summary" or "--grep"
                    or "--versions" or "--history" or "--fulltext" or "--diff" or "--export"
                    or "--feed-info" or "--import-opml" or "--like" or "telemetry";
    if (args.Length < 2)
    {
        if (!needsArg) { SetExit(); Console.WriteLine(Lang.T("Unknown command: {0}", cmd)); PrintHelp(); return; }
        SetExit(); Console.WriteLine(Lang.T("Missing argument. Usage: sip {0} <arg>", cmd));
        return;
    }

    switch (cmd)
    {
        case "-u" or "--update":
            if (!int.TryParse(args[1], out int aNum)) { SetExit(); Console.WriteLine(Lang.T("The number must be numeric")); return; }
            UpdateFeed(aNum, dbPath).Wait();
            break;
        case "-d" or "--download":
            DownloadCli(args[1], dbPath);
            break;
        case "-a" or "--archive":
            if (!int.TryParse(args[1], out int tNum)) { SetExit(); Console.WriteLine(Lang.T("The number must be numeric")); return; }
            AddTimestamp(tNum, dbPath);
            break;
        case "-una" or "--unarchive":
            if (!int.TryParse(args[1], out int uNum)) { SetExit(); Console.WriteLine(Lang.T("The number must be numeric")); return; }
            RemoveTimestamp(uNum, dbPath);
            break;
        case "-r" or "--remove":
            if (!int.TryParse(args[1], out int dNum)) { SetExit(); Console.WriteLine(Lang.T("The number must be numeric")); return; }
            DeleteFeed(dNum, dbPath, args.Contains("--yes", StringComparer.OrdinalIgnoreCase) || args.Contains("-y", StringComparer.OrdinalIgnoreCase));
            break;
        case "--search":
            if (args.Length < 2) { SetExit(); Console.WriteLine(Lang.T("Usage: sip --search <query> [--feed number] [--threshold 0.7] [--json]")); return; }
            SearchCli(args.Skip(1).ToArray(), dbPath);
            break;
        case "--grep":
            GrepCli(args.Skip(1).ToArray(), dbPath);
            break;
        case "--fulltext":
            if (args.Length < 2) { SetExit(); Console.WriteLine(Lang.T("Usage: sip --fulltext <article-id> [--yes] [--json]")); return; }
            FulltextCli(args.Skip(1).ToArray(), dbPath);
            break;
        case "--versions" or "--history":
            ListVersionsCli(args[1], dbPath, args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase)));
            break;
        case "--diff":
            if (args.Length < 2) { SetExit(); Console.WriteLine(Lang.T("Usage: sip --diff <article-id> [vA vB] [--json]")); return; }
            DiffCli(args.Skip(1).ToArray(), dbPath);
            break;
        case "--export":
            if (args.Length < 2) { SetExit(); Console.WriteLine(Lang.T("Usage: sip --export <article-id | feed:number | all> [out.md | dir] [--yes]")); return; }
            ExportCli(args.Skip(1).ToArray(), dbPath);
            break;
        case "--feed-info":
            if (args.Length < 2) { SetExit(); Console.WriteLine(Lang.T("Usage: sip --feed-info <feed-number> [--json]")); return; }
            FeedInfoCli(args.Skip(1).ToArray(), dbPath);
            break;
        case "--import-opml":
            if (args.Length < 2) { SetExit(); Console.WriteLine(Lang.T("Usage: sip --import-opml <file.opml>")); return; }
            ImportOpmlCli(args[1], dbPath);
            break;
        case "--like":
            if (args.Length < 2) { SetExit(); Console.WriteLine(Lang.T("Usage: sip --like <article-id> [--ai [reason]]")); return; }
            LikeCli(args.Skip(1).ToArray(), dbPath);
            break;
        case "telemetry":
            TelemetryCli(args.Skip(1).ToArray(), dbPath);
            break;
        case "--summary":
            SummaryCli(args[1], dbPath, args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase))).Wait();
            break;
        case "--import":
            if (args.Length < 2) { SetExit(); Console.WriteLine(Lang.T("Usage: sip --import <file> [--title <name>] [--json]")); return; }
            ImportCli(args.Skip(1).ToArray(), dbPath);
            break;
        case "--import-rm":
            if (args.Length < 2) { SetExit(); Console.WriteLine(Lang.T("Usage: sip --import-rm <id> [--yes] [--json]")); return; }
            ImportRmCli(args.Skip(1).ToArray(), dbPath);
            break;
        default:
            SetExit(); Console.WriteLine(Lang.T("Unknown command: {0}", cmd));
            PrintHelp();
            break;
    }
}

static void PrintHelp()
{
    Console.WriteLine(Lang.T("Usage: sip <command> [args]"));
    Console.WriteLine();
    Console.WriteLine(Lang.T("Commands:"));
    Console.WriteLine(Lang.T("  -l, --list       list all feeds (-l <n> [--limit M] article list, capped)"));
    Console.WriteLine(Lang.T("  -u, --update     update a feed (number)"));
    Console.WriteLine(Lang.T("  -d, --download   download a new RSS feed (URL)"));
    Console.WriteLine(Lang.T("  -a, --archive    archive a feed (add timestamp)"));
    Console.WriteLine(Lang.T("  -una, --unarchive unarchive a feed"));
    Console.WriteLine(Lang.T("  -r, --remove     delete a feed (add --yes to skip confirmation)"));
    Console.WriteLine(Lang.T("  pic              TUI with article images on (sixel); images are OFF by default — terminal sixel detection is unreliable"));
    Console.WriteLine(Lang.T("  --show <id>      fullscreen reading (no sidebar; W = full TUI, Esc = exit); add --json to output raw content for AI/scripts"));
    Console.WriteLine(Lang.T("  --versions <id>  list all versions of an article (use --show <id> to view one)"));
    Console.WriteLine(Lang.T("  --diff <id> [vA vB]  diff two versions of an article (default: last two); --json for structured output"));
    Console.WriteLine(Lang.T("  --export <id | feed:N | all> [out.md|dir]  export article(s) as Markdown (--yes to skip confirm)"));
    Console.WriteLine(Lang.T("  --fulltext <id>  fetch the article's full text to a local cache (--yes skip consent/confirm; --json)"));
    Console.WriteLine(Lang.T("  --purge-fulltext [id]  clear the full-text cache"));
    Console.WriteLine(Lang.T("  --feed-info <n>  source identity & health (type/author/site/updated/status; --json)"));
    Console.WriteLine(Lang.T("  --export-opml [file]  export feeds as OPML; --import-opml <file>  import feeds"));
    Console.WriteLine(Lang.T("  --import <file> [--title <name>]  import local file (txt/md/pdf/epub/docx)"));
    Console.WriteLine(Lang.T("  --import-rm <id> [--yes]  remove an imported file"));
    Console.WriteLine(Lang.T("  --like <id> [--ai [reason]]  mark an article (♥ user / 🤖 AI); --likes lists marks"));
    Console.WriteLine(Lang.T("  --today [--json]  today's curated reading list (rule-based; guides daily reading habit)"));
    Console.WriteLine(Lang.T("  telemetry status|show|enable|disable|clear|export  local reading telemetry · Sumenia (default OFF)"));
    Console.WriteLine(Lang.T("  simon status|level <1|2|3>  security guardian (default ON, cannot disable; 2=block destructive CLI, 3=block all writes) · 孟思琳"));
    Console.WriteLine(Lang.T("  --insights [--window N d] [--json]  reading report (facts per feed; needs telemetry ON; decisions are yours)"));
    Console.WriteLine(Lang.T("  --insights-interval <7d|30d|off>  schedule the report reminder (due popup in TUI)"));
    Console.WriteLine(Lang.T("  --dedup scan|hide <hiddenId> <canonicalId>|list|undo <key>  cross-source duplicate detection & hide (--json)"));
    Console.WriteLine(Lang.T("  --policy list|set <feedId> <action> [args]|remove <feedId>  source rules you confirm (tag / lower frequency / archive / keep / unsubscribe)"));
    Console.WriteLine(Lang.T("  --onboarding [list|<category>]|add <category> <index|all>  recommended source templates (edit templates.json)"));
    Console.WriteLine(Lang.T("  -h, --help       show this help"));
    Console.WriteLine(Lang.T("  --version        show version"));
    Console.WriteLine();
    Console.WriteLine(Lang.T("Update scheduling:"));
    Console.WriteLine(Lang.T("  --sync [--feed N] [--json]  update only 'due' feeds (lists last/next times)"));
    Console.WriteLine(Lang.T("  --update-all            force update all feeds (same as TUI F6)"));
    Console.WriteLine(Lang.T("  --schedule <id> <expr>  set a feed's update schedule, e.g. 30m / 1h / 7d / daily@10:00 / weekly@Mon 08:00 / manual"));
    Console.WriteLine(Lang.T("  -l shows each feed's 'schedule · last · next'"));
    Console.WriteLine();
    Console.WriteLine(Lang.T("AI commands:"));
    Console.WriteLine(Lang.T("  --init           configure AI for the first time (model + API key)"));
    Console.WriteLine(Lang.T("  --config         view/edit AI config"));
    Console.WriteLine(Lang.T("  --index          embed articles (interactive selection)"));
    Console.WriteLine(Lang.T("  --reindex        re-embed after changing the embedding model"));
    Console.WriteLine(Lang.T("  --search <query> [--feed number] [--threshold 0.7] [--json] semantic search (all feeds without --feed)"));
    Console.WriteLine(Lang.T("  --grep <keyword>   full-text search (title/content/summary, no AI needed); outputs id+title+count+line and ±N-char snippets (--context N / --feed N / --limit N / --max-snippets N / --json / --full)"));
    Console.WriteLine(Lang.T("  --summary <id>   summarize one article; use feed:<number> for all articles of a feed (--json)"));
    Console.WriteLine(Lang.T("  --summary-all    summarize all articles without a summary"));
    Console.WriteLine();
    Console.WriteLine(Lang.T("Examples:"));
    Console.WriteLine(Lang.T("  sip -l"));
    Console.WriteLine(Lang.T("  sip -d https://example.com/rss"));
    Console.WriteLine(Lang.T("  sip -u 1"));
    Console.WriteLine(Lang.T("  sip -a 1"));
    Console.WriteLine(Lang.T("  sip --search \"LLM Agent\" --feed 1 --json"));
    Console.WriteLine(Lang.T("  sip --summary 12"));
    Console.WriteLine(Lang.T("  sip --summary feed:3"));
    Console.WriteLine();
    Console.WriteLine(Lang.T("Global options:"));
    Console.WriteLine(Lang.T("  --ignoresafeannouncement   skip safety banner, data only (for scripts / AI agents)"));
    Console.WriteLine(Lang.T("  --lang <code>              language file (e.g. zh-CN / en-US, default zh-CN)"));
    Console.WriteLine();
    Console.WriteLine(Lang.T("Security notes:"));
    Console.WriteLine(Lang.T("  API keys are stored in the OS-native credential store (Windows Credential Manager / macOS Keychain / Linux Secret Service),"));
    Console.WriteLine(Lang.T("  never written to any file. Do not leak it. You will be prompted on first AI use."));
}

// ══════════ TUI（Terminal.Gui 文件夹视图）═══════════
// 布局：左侧订阅源+文章树（源为父节点，展开即见文章）/ 右侧正文预览 / 底部状态栏
// 操作：↑↓ 选择，Enter 折叠/展开源或打开文章，←→ 切换树/正文，PageUp/PageDown 翻页，
//       U 更新当前源，F6 全部更新，A 归档，R 去归档，X 删除，D 加源，S 搜索，Y 摘要，H 帮助，Q 退出

static int GetDisplayNum(int realId, string dbPath)
{
    try
    {
        using var conn = OpenDb(dbPath);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, ROW_NUMBER() OVER (ORDER BY Id) AS dn FROM Feeds";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (r.GetInt32(0) == realId) return r.GetInt32(1);
    }
    catch { }
    return realId;
}

static void TrimFulltextCache(int maxFiles = 200, long maxBytes = 200L * 1024 * 1024)
{
    try
    {
        var dir = FulltextDir();
        var files = new DirectoryInfo(dir).GetFiles("*.md").ToList();
        long total = files.Sum(f => f.Length);
        if (files.Count <= maxFiles && total <= maxBytes) return;
        foreach (var f in files.OrderBy(f => f.LastWriteTime).Take(files.Count - maxFiles))
        {
            try { f.Delete(); } catch { }
        }
    }
    catch { /* 清理失败不影响主流程 */ }
}

// 获取文章的 Link URL（供 TUI 图片相对路径解析）
static string GetArticleLink(long itemId, string dbPath)
{
    try
    {
        using var conn = OpenDb(dbPath);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Link FROM Items WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        var r = cmd.ExecuteReader();
        if (r.Read() && !r.IsDBNull(0)) return r.GetString(0);
    }
    catch { }
    return "";
}

// 起始页「今日哈汤」区块：规则清单 + 目标进度（引导习惯，不堆量）
static string BuildArticleMarkdown(long itemId, bool contentMode, string dbPath, int wrapWidth, bool showFetchHint = false)
{
    using var conn = OpenDb(dbPath);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT i.Title, i.Content, i.Description, i.Link, i.PublishDate, i.Summary, i.Author, f.Title
        FROM Items i LEFT JOIN Feeds f ON i.FeedId = f.Id
        WHERE i.Id = @id";
    cmd.Parameters.AddWithValue("@id", itemId);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) return Lang.T("(Article not found)");
    string title = r.GetString(0);
    string content = r.IsDBNull(1) ? "" : r.GetString(1);
    string desc = r.IsDBNull(2) ? "" : r.GetString(2);
    string link = r.IsDBNull(3) ? "" : r.GetString(3);
    string pub = r.IsDBNull(4) ? "" : r.GetString(4);
    string aiSummary = r.IsDBNull(5) ? "" : r.GetString(5);
    string author = r.IsDBNull(6) ? "" : r.GetString(6);
    string feedTitle = r.IsDBNull(7) ? "" : r.GetString(7);

    var md = new StringBuilder();
    // 标题独立成段，像文章标题（不再加粗，H1 已足够醒目）
    md.AppendLine($"# {EscapeMd(CjkSpace(title))}");
    md.AppendLine();
    // 元信息独立成块：作者 / 日期 / 来源，与正文分离，避免混在一起
    var meta = new List<string>();
    if (!string.IsNullOrWhiteSpace(author)) meta.Add($"**{Lang.T("Author")}**: {EscapeMd(CjkSpace(author))}");
    if (!string.IsNullOrWhiteSpace(pub)) meta.Add($"**{Lang.T("Date")}**: {EscapeMd(CjkSpace(pub))}");
    if (meta.Count > 0) md.AppendLine(string.Join("  ·  ", meta));
    if (!string.IsNullOrWhiteSpace(link))
        md.AppendLine($"**{Lang.T("Source")}**: [{(string.IsNullOrWhiteSpace(feedTitle) ? link : EscapeMd(CjkSpace(feedTitle)))}]({EscapeMdUrl(link)})");
    else if (!string.IsNullOrWhiteSpace(feedTitle))
        md.AppendLine($"**{Lang.T("Source")}**: {EscapeMd(CjkSpace(feedTitle))}");
    md.AppendLine();
    md.AppendLine("---");
    md.AppendLine();

    if (contentMode)
    {
        // 完整正文模式：Content（原文）在上，抓取全文在下（若有缓存），中间分界
        string body = string.IsNullOrWhiteSpace(content) ? desc : content;
        md.Append(HtmlToMarkdown(body, wrapWidth, link));
        string? fulltext = ReadFulltextCache(itemId);
        if (!string.IsNullOrWhiteSpace(fulltext))
        {
            md.AppendLine();
            md.AppendLine("---");
            md.AppendLine();
            md.AppendLine("## " + Lang.T("Fetched full text"));
            md.AppendLine();
            md.AppendLine(EscapeMd(fulltext.Trim()));
        }
        else if (showFetchHint && ContentTooShort(content, desc))
        {
            // 摘要过短且未抓取 → 提示（仅 TUI，CLI 全屏没有命令行）
            md.AppendLine();
            md.AppendLine($"> {Lang.T("The summary is too short. Type fetch to get the full text.")}");
        }
    }
    else
    {
        // 概要模式：AI 摘要 + RSS 摘要
        if (!string.IsNullOrWhiteSpace(aiSummary))
        {
            md.AppendLine("## " + Lang.T("AI Summary"));
            md.AppendLine();
            md.AppendLine(EscapeMd(aiSummary));
            md.AppendLine();
            md.AppendLine("---");
            md.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(desc))
            md.Append(HtmlToMarkdown(desc, wrapWidth, link));
        else
            md.Append(Lang.T("(No summary, press G for full content)"));
    }
    // 返回前统一过滤控制字符:标题/元信息/正文/fulltext/AI 摘要都可能来自
    // 不可信 RSS 源,含 ANSI 转义序列会注入终端(CLI 导出与 TUI 渲染共用此函数)。
    // StripControlChars 保留 \n \t \r,不影响 Markdown 语法字符。
    return StripControlChars(md.ToString());
}

// 中文排版：在汉字与相邻的英文/数字之间插入空格，让混排更清爽
// 同时过滤控制字符（ANSI 转义等）：本函数是 TUI 标题/列表/对话框标题的公共渲染入口，
// 恶意源可在标题里塞 ESC 序列注入终端——CLI 路径已有单独过滤，这里兜底 TUI 全部标题路径
static string CjkSpace(string s)
{
    if (string.IsNullOrEmpty(s)) return s;
    s = StripControlChars(s);
    return Regex.Replace(s,
        @"(?<=[\u4E00-\u9FFF])(?=[A-Za-z0-9@#%])|(?<=[A-Za-z0-9@#%])(?=[\u4E00-\u9FFF])",
        " ");
}


// 从数据库加载某源的文章节点（TUI 树的叶子）
// 加载某源的文章节点（TUI 侧栏叶子）
// 每个 Guid（同一篇文章）只显示最新一版，不再堆「[现] v1」；若该文有被作者改过的旧版本，
// 标题右侧加 ✎ 标记，选中后按 V 可查看全部版本 / 变更历史
// 注意：Guid 为空串时（既无 Id 也无 Link 的文章）不做分组，避免把无关文章挤成一行
static string HtmlToMarkdown(string html, int imageWidth = 80, string? baseUrl = null)
{
    TuiMdState.Links.Clear();
    TuiMdState.ImageWidth = imageWidth;
    if (string.IsNullOrWhiteSpace(html)) return "";
    try
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);
        var sb = new StringBuilder();
        WalkHtml(doc.DocumentNode, sb, 0, baseUrl);
        var text = sb.ToString();
        text = Regex.Replace(text, @"[ \t]{2,}", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        // 只去掉首尾换行（不 Trim 空白，保留原文空格）
        return System.Net.WebUtility.HtmlDecode(text).Trim('\n', '\r');
    }
    catch
    {
        return StripHtml(html);
    }
}

static void WalkHtml(HtmlAgilityPack.HtmlNode node, StringBuilder sb, int listDepth, string? baseUrl = null)
{
    if (node.NodeType == HtmlAgilityPack.HtmlNodeType.Text)
    {
        sb.Append(node.InnerText);
        return;
    }
    string name = node.Name;
    switch (name)
    {
        case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
            int level = name[1] - '0';
            sb.Append('\n').Append(new string('#', level)).Append(' ');
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth, baseUrl);
            sb.Append('\n');
            return;
        case "p":
            // 段落不做首行缩进（博客原文通常没有缩进，避免格式打架）
            sb.Append('\n');
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth, baseUrl);
            sb.Append("\n\n");
            return;
        case "div": case "section": case "article":
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth, baseUrl);
            sb.Append("\n\n");
            return;
        case "blockquote":
            // 引用块也不加缩进（与段落一致）
            sb.Append('\n');
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth, baseUrl);
            sb.Append("\n\n");
            return;
        case "br":
            // 保持单换行：依赖 UseSoftlineBreakAsHardlineBreak 管道渲染为硬换行，
            // 避免 \n\n 切断跨 <br> 的删除线/加粗标记、丢失段内缩进
            sb.Append('\n');
            return;
        case "hr":
            sb.Append("\n---\n");
            return;
        case "strong": case "b":
            sb.Append("**");
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth, baseUrl);
            sb.Append("**");
            return;
        case "em": case "i":
            sb.Append('*');
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth, baseUrl);
            sb.Append('*');
            return;
        case "del": case "s": case "strike":
            sb.Append("~~");
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth, baseUrl);
            sb.Append("~~");
            return;
        case "u":
            sb.Append("__");
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth, baseUrl);
            sb.Append("__");
            return;
        case "code":
            sb.Append('`').Append(node.InnerText).Append('`');
            return;
        case "pre":
            sb.Append("\n```\n").Append(node.InnerText).Append("\n```\n");
            return;
        case "a":
            string href = node.GetAttributeValue("href", "");
            var linkText = new StringBuilder();
            foreach (var c in node.ChildNodes) WalkHtml(c, linkText, listDepth, baseUrl);
            string ltxt = System.Net.WebUtility.HtmlDecode(linkText.ToString().Trim());
            if (!string.IsNullOrWhiteSpace(ltxt) && !string.IsNullOrWhiteSpace(href))
                TuiMdState.Links.Add((ltxt, href));
            sb.Append('[').Append(linkText).Append(']').Append('(').Append(EscapeMdUrl(href)).Append(')');
            return;
        case "img":
            // alt 不再读取 —— 占位文本统一用 ZWSP(见下方 label 的注释)
            string src2 = node.GetAttributeValue("src", "");
            if (!string.IsNullOrWhiteSpace(src2))
            {
                // 解析相对路径图片 URL
                string imgUrl = src2;
                if (!string.IsNullOrWhiteSpace(baseUrl) && !Uri.TryCreate(src2, UriKind.Absolute, out _))
                {
                    try { imgUrl = new Uri(new Uri(baseUrl), src2).AbsoluteUri; }
                    catch { /* 保持原值 */ }
                }
                // 保留 Markdown 图片语法，由 TUI Markdown 渲染器处理（Sixel/Kitty/链接回退）
                //
                // alt 恒定为 ZWSP(U+200B),不管 TUI 还是 CLI 导出:
                //   * Terminal.Gui 的 GetFallbackText 是私有的,没法让它返回空串。
                //     给**空** alt 时它会兜底成 "[image]"(6 列可见) —— 反而更糟。
                //   * ZWSP 不是空白字符(IsNullOrWhiteSpace 为 false),所以会走真实
                //     分支返回 "[\u200B]" —— "[" + ZWSP + "]",其中 ZWSP 渲染宽度为 0,
                //     "[" 和 "]" 各 1 列,**共 2 列可见** —— 这是能做到的最小占位。
                //   * 原 HTML 的 alt 通常取自 src 文件名(实测见过占 32 列的
                //     "[Image_1771682342796_915.webp]"),在这层直接丢弃。
                //
                // 取舍:TUI 里 sixel 会覆盖图片那一行,2 列占位基本看不见;CLI 导出
                // 到不支持图片的阅读器时,那张图就只剩 2 列 "[]"。hotsoup 明确接受
                // 这个后果("用户用老阅读器就当不知道图片行了")。
                const string label = "\u200b";
                sb.Append("\n\n![");
                sb.Append(label);
                sb.Append("](");
                sb.Append(EscapeMdUrl(imgUrl));
                sb.Append(")\n\n");

                // 给图片在文档流里预留垂直空间。
                //
                // 原因:sixel 是以光标位置为原点向右下铺像素的,而它在 Markdown 文档流
                // 里只占 1 行(占位文本 "[alt]" 那一行)。图片实际有十几到二十个单元高,
                // 不预留的话,后面那几段正文会被图片整个压在底下看不见。
                //
                // 直接堆空行是没用的 —— CommonMark 会把连续空行折叠成一个段落间隔
                // (实测 "AAA\n\n\n\n\n\n\nBBB" 和 "AAA\n\nBBB" 渲染结果一模一样)。
                // 零宽空格不是空白字符,所以不会被当空行折叠,但渲染宽度为 0,看起来
                // 还是空行。实测每个零宽空格段落产出 2 行(自身 + 段落间隔)。
                //
                // 只在 TUI 里加:这个函数同时供 CLI 导出用,命令行输出里塞零宽空格是垃圾。
                if (TuiMdState.ReserveImageSpace)
                {
                    for (int i = 0; i < TuiMdState.ImageReservedBlankParagraphs; i++)
                        sb.Append(TuiMdState.BlankLineFiller).Append("\n\n");
                }
            }
            return;
        case "ul": case "ol":
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth + 1, baseUrl);
            sb.Append('\n');
            return;
        case "li":
            sb.Append('\n').Append(new string(' ', listDepth * 2)).Append("- ");
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth, baseUrl);
            return;
        case "tr":
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth, baseUrl);
            sb.Append('\n');
            return;
        case "td": case "th":
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth, baseUrl);
            sb.Append(" | ");
            return;
        case "table":
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth, baseUrl);
            sb.Append('\n');
            return;
        case "script": case "style": case "head": case "nav": case "footer": case "aside":
            return;  // 丢弃
        default:
            foreach (var c in node.ChildNodes) WalkHtml(c, sb, listDepth, baseUrl);
            return;
    }
}

// Markdown 转义标题/链接文本里的特殊字符
static string EscapeMd(string s) => StripControlChars(s).Replace("\\", "\\\\").Replace("*", "\\*").Replace("#", "\\#").Replace("[", "\\[").Replace("]", "\\]").Replace("|", "\\|");

// 剥除终端控制字符（ESC 序列 / BEL / 其他 C0-C1 控制符），防止恶意内容注入终端。
// 保留 \n \t \r 等正常空白；JSON 路径不受影响（序列化器自行转义）
static string StripControlChars(string s)
{
    if (string.IsNullOrEmpty(s)) return s;
    var sb = new StringBuilder(s.Length);
    foreach (char c in s)
    {
        if (c == '\n' || c == '\t' || c == '\r') { sb.Append(c); continue; }
        if (c < 0x20 || c == 0x7f || (c >= 0x80 && c <= 0x9f)) continue;  // C0（除空白）+ DEL + C1
        sb.Append(c);
    }
    return sb.ToString();
}

static string EscapeMdUrl(string s) => s.Replace(" ", "%20").Replace("(", "%28").Replace(")", "%29");

// HTML 正文转纯文本（去标签、解实体，保留段落/换行）
static string StripHtml(string html)
{
    if (string.IsNullOrWhiteSpace(html)) return "";
    try
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);
        // 块级元素与换行标签后补一个换行，避免整篇被压成一坨
        foreach (var node in doc.DocumentNode.SelectNodes("//text()[normalize-space()]") ?? Enumerable.Empty<HtmlAgilityPack.HtmlNode>())
        {
            var parent = node.ParentNode;
            if (parent == null) continue;
            string name = parent.Name;
            if (name is "p" or "div" or "br" or "li" or "tr" or "section" or "article" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "pre" or "ul" or "ol")
                node.InnerHtml = node.InnerHtml.TrimEnd() + "\n";
        }
        var text = doc.DocumentNode.InnerText;
        // 把连续的多个空行压成一个空行
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }
    catch
    {
        return html;
    }
}

// 按真实 Id 归档（不查显示编号）
static void AddTimestampForRealId(int realId, string dbPath)
{
    using var conn = OpenDb(dbPath);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Title FROM Feeds WHERE Id = @id";
    cmd.Parameters.AddWithValue("@id", realId);
    string oldTitle = cmd.ExecuteScalar()!.ToString()!;
    if (IsArchived(oldTitle)) return;
    string newTitle = oldTitle + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
    cmd.CommandText = "UPDATE Feeds SET Title = @newTitle WHERE Id = @id";
    cmd.Parameters.AddWithValue("@newTitle", newTitle);
    cmd.ExecuteNonQuery();
    TelemetryService.Record("feed_change", sourceId: realId, data: new { action = "archive", title = oldTitle });
}

// 按真实 Id 去归档
static void RemoveTimestampForRealId(int realId, string dbPath)
{
    using var conn = OpenDb(dbPath);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Title FROM Feeds WHERE Id = @id";
    cmd.Parameters.AddWithValue("@id", realId);
    string title = cmd.ExecuteScalar()!.ToString()!;
    string plainTitle = Regex.Replace(title, @"_\d{8}_\d{6}$", "");
    if (plainTitle == title) return;
    cmd.CommandText = "SELECT COUNT(*) FROM Feeds WHERE Title = @title AND Id != @id";
    cmd.Parameters.AddWithValue("@title", plainTitle);
    long conflict = (long)cmd.ExecuteScalar()!;
    if (conflict > 0) return;
    cmd.CommandText = "UPDATE Feeds SET Title = @newTitle WHERE Id = @id";
    cmd.Parameters.AddWithValue("@newTitle", plainTitle);
    cmd.ExecuteNonQuery();
    TelemetryService.Record("feed_change", sourceId: realId, data: new { action = "unarchive", title = plainTitle });
}

// 按真实 Id 删除源（含文章与向量）
static void DeleteFeedByRealId(int realId, string dbPath)
{
    // 先取该源全部文章 Id（用于清理全文缓存与 sidecar 向量）
    var ids = new List<int>();
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var c = conn.CreateCommand();
        c.CommandText = "SELECT Id FROM Items WHERE FeedId = @id";
        c.Parameters.AddWithValue("@id", realId);
        using var r = c.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt32(0));
    }
    foreach (var id in ids)
    {
        string p = FulltextPath(id);
        if (File.Exists(p)) { try { File.Delete(p); } catch { } }
    }
    RemoveFulltextVecs(ids);

    using var db = OpenDb(dbPath);
    db.Open();
    var cmd = db.CreateCommand();
    cmd.CommandText = "DELETE FROM Vectors WHERE FeedId = @id";
    cmd.Parameters.AddWithValue("@id", realId);
    cmd.ExecuteNonQuery();
    cmd.CommandText = "DELETE FROM ItemsFts WHERE rowid IN (SELECT Id FROM Items WHERE FeedId = @id)";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "DELETE FROM Items WHERE FeedId = @id";
    cmd.ExecuteNonQuery();
    // 删除前记录标题（用于遥测 feed 生命周期）
    string delTitle = "";
    var tCmd = db.CreateCommand();
    tCmd.CommandText = "SELECT Title FROM Feeds WHERE Id = @id";
    tCmd.Parameters.AddWithValue("@id", realId);
    var to = tCmd.ExecuteScalar();
    if (to != null) delTitle = to.ToString() ?? "";
    cmd.CommandText = "DELETE FROM Feeds WHERE Id = @id";
    cmd.ExecuteNonQuery();
    TelemetryService.Record("feed_change", sourceId: realId, data: new { action = "delete", title = delTitle });

    // 清理孤儿 dedup 规则（删除源的规则不再有意义）
    CleanOrphanDedup(realId);
}

// 删除源后清理孤儿 dedup 规则（该源作为被隐藏或保留方都不再有意义）
static void CleanOrphanDedup(int feedId)
{
    var dmap = LoadDedup();
    int before = dmap.Count;
    foreach (var kv in dmap.ToList())
        if (kv.Value.HiddenFeedId == feedId || kv.Value.CanonicalFeedId == feedId)
            dmap.Remove(kv.Key);
    if (dmap.Count != before) SaveDedup(dmap);
}

// 从 sidecar vecs.json 移除指定 itemId（删文章/删源时清理孤儿向量）
static void RemoveFulltextVecs(List<int> itemIds)
{
    if (itemIds.Count == 0) return;
    var list = LoadFulltextVecs();
    int before = list.Count;
    list.RemoveAll(e => itemIds.Contains(e.ItemId));
    if (list.Count != before) SaveFulltextVecs(list);
}

// ══════════ 更新指定订阅源（A 菜单和 CLI 共用）═══════════
static async Task UpdateFeed(int displayNum, string dbPath)
{
    int realId = GetRealId(displayNum, dbPath);
    if (realId == 0) { SetExit(); Console.WriteLine(Lang.T("Feed number not found")); return; }

    using var conn = OpenDb(dbPath);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Title, FeedUrl FROM Feeds WHERE Id = @id";
    cmd.Parameters.AddWithValue("@id", realId);
    using var r = cmd.ExecuteReader();
    r.Read();
    string title = r.GetString(0);
    string url = r.GetString(1);
    r.Close();

    if (IsArchived(title)) { SetExit(); Console.WriteLine(Lang.T("{0} is archived and cannot be updated", title)); return; }

    try
    {
        await DownloadAndSaveToDb(url, dbPath);
        RecordFeedSuccess(realId);
        Console.WriteLine(Lang.T("Update complete"));
    }
    catch (TaskCanceledException) { RecordFeedFailure(realId, "timeout"); ReportError("NETWORK_ERROR", Lang.T("Download timed out; check your network or the URL"), Lang.T("Check your network connection or the URL")); }
    catch (HttpRequestException ex) { RecordFeedFailure(realId, ex.Message); ReportError("NETWORK_ERROR", Lang.T("Network request failed, the URL may be dead"), Lang.T("Check the URL or your network connection"), ex.Message); }
    catch (SqliteException ex) { SetExit(); Console.WriteLine(Lang.T("Database error: {0}", ex.Message)); }
    catch (Exception ex) { SetExit(); Console.WriteLine(Lang.T("Unknown error: {0}", ex.Message)); }
}

// CLI 模式下载（同步等待异步方法）
static void DownloadCli(string url, string dbPath)
{
    Exception? err = null;
    try { DownloadAndSaveToDb(url, dbPath).Wait(); Console.WriteLine(Lang.T("Download complete")); return; }
    catch (AggregateException ae) { err = ae.GetBaseException(); }   // .Wait() 会把异常包成 AggregateException
    catch (Exception ex) { err = ex; }

    switch (err)
    {
        case TaskCanceledException:
            ReportError("NETWORK_ERROR", Lang.T("Download timed out; check your network or the URL"), Lang.T("Check your network connection or the URL"));
            break;
        case HttpRequestException http:
            ReportError("NETWORK_ERROR", Lang.T("Network request failed, the URL may be dead"), Lang.T("Check the URL or your network connection"), http.Message);
            break;
        default:
            SetExit();
            Console.WriteLine(Lang.T("Error: {0}", err?.Message ?? ""));
            break;
    }
}

// ══════════ 更新计划（CLI）═══════════
// 设置某源的更新计划表达式；无效表达式会给出提示
static void SetFeedSchedule(string displayNum, string expr, string dbPath)
{
    if (!int.TryParse(displayNum, out int dn)) { SetExit(); Console.WriteLine(Lang.T("The number must be numeric")); return; }
    int realId = GetRealId(dn, dbPath);
    if (realId == 0) { SetExit(); Console.WriteLine(Lang.T("Feed number not found")); return; }

    string raw = (expr ?? "").Trim();
    if (raw.Length == 0 || raw.Equals("manual", StringComparison.OrdinalIgnoreCase))
    {
        // 清空计划 = 手动，不自动更新
        using var conn = OpenDb(dbPath);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Feeds SET Schedule = '' WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", realId);
        cmd.ExecuteNonQuery();
        TelemetryService.Record("feed_change", sourceId: realId, data: new { action = "schedule", schedule = "manual" });
        Console.WriteLine(Lang.T("Feed {0} set to manual updates (not automatic)", dn));
        return;
    }

    var s = TryParseSchedule(raw);
    if (s == null)
    {
        SetExit();
        Console.WriteLine(Lang.T("Invalid schedule expression: {0}; e.g. 30m / 1h / daily@10:00 / weekly@Mon 08:00 / manual", raw));
        return;
    }

    using var conn2 = OpenDb(dbPath);
    conn2.Open();
    var cmd2 = conn2.CreateCommand();
    cmd2.CommandText = "UPDATE Feeds SET Schedule = @s WHERE Id = @id";
    cmd2.Parameters.AddWithValue("@s", raw.ToLowerInvariant());
    cmd2.Parameters.AddWithValue("@id", realId);
    cmd2.ExecuteNonQuery();
    TelemetryService.Record("feed_change", sourceId: realId, data: new { action = "schedule", schedule = raw.ToLowerInvariant() });

    string hint = TryParseSchedule(raw.ToLowerInvariant()) is FeedSchedule ps && !ps.IsManual && ps.Raw.Length > 0
        ? HumanSchedule(ps) : raw;
    Console.WriteLine(Lang.T("Feed {0} update schedule set: {1}", dn, hint));
}

// --sync：只更新到期的订阅源（可 --feed N 限定单个源）；输出每个源的 上次/下次；--json 结构化
static async Task SyncCli(string[] extra, string dbPath)
{
    bool json = extra.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    int? feedReal = null;
    for (int i = 0; i < extra.Length; i++)
        if (extra[i] == "--feed" && i + 1 < extra.Length && int.TryParse(extra[++i], out int f))
            feedReal = GetRealId(f, dbPath);

    if (feedReal.HasValue && feedReal.Value == 0) { ReportError("FEED_NOT_FOUND", Lang.T("Feed number not found"), json: json); return; }

    var now = DateTime.Now;
    var due = feedReal.HasValue
        ? GetDueFeeds(dbPath).Where(d => d.Id == feedReal.Value).ToList()
        : GetDueFeeds(dbPath);

    if (feedReal.HasValue && due.Count == 0)
    {
        // 限定单个源但未到期 → 提示还差多久
        using var conn = OpenDb(dbPath);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Title, LastCheckedAt, Schedule FROM Feeds WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", feedReal.Value);
        using var r = cmd.ExecuteReader();
        if (r.Read())
        {
            string title = r.GetString(0);
            DateTime? lc = r.IsDBNull(1) ? null : TryParseIso(r.GetString(1));
            string schedule = r.IsDBNull(2) ? "" : r.GetString(2);
            var next = FeedNextDue(schedule, lc, now);
            if (json)
                JsonOut(new { success = true, data = new { feedId = feedReal.Value, title, due = false, nextUpdate = next }});
            else if (next != null)
                Console.WriteLine(Lang.T("{0} is not due yet; next update in {1}", title, UntilText(next.Value, now)));
            else
                Console.WriteLine(Lang.T("{0} has no auto-update schedule (set one with --schedule)", title));
        }
        return;
    }

    if (due.Count == 0)
    {
        if (json) JsonOut(new { success = true, data = new { feeds = Array.Empty<object>(), ok = 0, fail = 0 } });
        else Console.WriteLine(Lang.T("No feeds are due"));
        return;
    }

    if (!json) Console.WriteLine(Lang.T("{0} feeds are due, syncing...", due.Count));
    int ok = 0, fail = 0;
    var results = new List<object>();
    foreach (var f in due)
    {
        if (!json) Console.WriteLine(Lang.T("  · {0} (last {1})", f.Title,
            f.LastChecked is DateTime lc ? AgoText(lc, now) : Lang.T("never")));
        try
        {
            await DownloadAndSaveToDb(f.Url, dbPath, interactive: false);
            RecordFeedSuccess(f.Id);
            ok++;
            if (json) results.Add(new { feedId = f.Id, title = f.Title, ok = true });
        }
        catch (Exception ex)
        {
            RecordFeedFailure(f.Id, ex.Message);
            fail++;
            if (json) results.Add(new { feedId = f.Id, title = f.Title, ok = false, error = ex.Message });
            else Console.WriteLine(Lang.T("    ✗ {0}", ex.Message));
        }
    }
    if (json)
    {
        JsonOut(new { success = true, data = new { feeds = results, ok, fail } });
        if (fail > 0) SetExit();
    }
    else
    {
        Console.WriteLine(Lang.T("Sync done: {0} ok, {1} failed", ok, fail));
        if (fail > 0) SetExit();
    }
}

// --update-all：强制更新所有订阅源（等价 TUI F6）
static async Task UpdateAllCli(string dbPath)
{
    using var conn = OpenDb(dbPath);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id, Title, FeedUrl FROM Feeds ORDER BY Id";
    using var r = cmd.ExecuteReader();
    var feeds = new List<(int Id, string Title, string Url)>();
    while (r.Read())
        feeds.Add((r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2)));
    r.Close();

    if (feeds.Count == 0) { Console.WriteLine(Lang.T("No feeds in the database yet")); return; }

    Console.WriteLine(Lang.T("Updating all feeds ({0} total)...", feeds.Count));
    int ok = 0, fail = 0;
    foreach (var f in feeds)
    {
        if (IsArchived(f.Title)) { Console.WriteLine(Lang.T("  · skipping archived feed: {0}", f.Title)); continue; }
        if (string.IsNullOrWhiteSpace(f.Url)) { fail++; continue; }
        try
        {
            await DownloadAndSaveToDb(f.Url, dbPath, interactive: false);
            RecordFeedSuccess(f.Id);
            ok++;
        }
        catch (Exception ex)
        {
            RecordFeedFailure(f.Id, ex.Message);
            fail++;
            Console.WriteLine(Lang.T("    ✗ {0}", ex.Message));
        }
    }
    Console.WriteLine(Lang.T("Update done: {0} ok, {1} failed", ok, fail));
    if (fail > 0) SetExit();
}

// ══════════ 建表方法 ══════════
// 只在程序启动时调用一次。IF NOT EXISTS 保证不会覆盖已有数据库
// 两张表的关系：Feeds 是"班级"，Items 是"学生"，FeedId 就是学生属于哪个班级
static void InitDatabase(string dbPath)
{
    // 主库完整性检查：损坏时改名保留现场并重建，绝不崩溃（与 telemetry 自愈同策略）
    CheckMainDbIntegrity(dbPath);

    // $ 开头是"字符串插值"：把 {dbPath} 替换成实际路径
    // using 保证连接用完会自动关闭，不占资源
    using var conn = OpenDb(dbPath);
    conn.Open();  // 打开连接

    var cmd = conn.CreateCommand();  // 创建一个命令对象
    // 先开外键约束 + WAL 模式（允许多进程并发读，写只阻塞写），再建表
    cmd.CommandText = "PRAGMA foreign_keys = ON;";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "PRAGMA journal_mode = WAL;";
    cmd.ExecuteNonQuery();

    cmd.CommandText = @"
        CREATE TABLE IF NOT EXISTS Feeds ( --管理rss链接
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            Title       TEXT    NOT NULL,    -- 订阅源标题
            FeedUrl     TEXT,               -- 下载链接（唯一标识，用来去重）
            Link        TEXT,               -- 博客首页网址
            Description TEXT,               -- 一句话简介
            LastFetched TEXT,               -- 上次抓取时间
            RawXml      TEXT                -- 原始XML，留着以后做diff
        );

        CREATE TABLE IF NOT EXISTS Items ( --管理rss文章
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            FeedId      INTEGER NOT NULL,   -- 外键：指向 Feeds 表的 Id
            Title       TEXT,               -- 文章标题
            Link        TEXT,               -- 文章链接
            Description TEXT,               -- 文章摘要
            Author      TEXT,               -- 作者
            PublishDate TEXT,               -- 发布时间
            Content     TEXT,               -- 正文
            Guid        TEXT,               -- 文章唯一标识（同Guid可有多版本）
            Status      TEXT    DEFAULT 'active',  -- active/archived/deleted
            Version     INTEGER DEFAULT 1,         -- 同一Guid的第几版
            ArchivedAt  TEXT,                      -- 归档时间戳
            FOREIGN KEY (FeedId) REFERENCES Feeds(Id)  -- 需配合 PRAGMA
        );

        CREATE TABLE IF NOT EXISTS Models ( --记录每个 Embedding 模型的元数据
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            ModelType   TEXT    NOT NULL,   -- 'embedding' / 'llm'
            Provider    TEXT    NOT NULL,   -- 'ollama' / 'openai' / 'deepseek'
            ModelName   TEXT    NOT NULL,   -- 模型名
            Dimensions  INTEGER,            -- 向量维度（仅 embedding 用）
            IsCurrent   INTEGER DEFAULT 0,  -- 是否为当前使用的 embedding 模型
            CreatedAt   TEXT
        );

        CREATE TABLE IF NOT EXISTS Vectors ( --文章向量索引
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            FeedId      INTEGER NOT NULL,   -- 所属源 Id（删除源时整组清除）
            ItemId      INTEGER NOT NULL,   -- 关联文章 Id
            ModelId     INTEGER NOT NULL,   -- 关联模型 Id
            Vector      BLOB    NOT NULL,   -- 向量二进制（float[] 序列化）
            CreatedAt   TEXT,
            FOREIGN KEY (FeedId) REFERENCES Feeds(Id),
            FOREIGN KEY (ItemId) REFERENCES Items(Id),
            FOREIGN KEY (ModelId) REFERENCES Models(Id)
        );

        CREATE UNIQUE INDEX IF NOT EXISTS UQ_Vectors_ItemModel ON Vectors (ItemId, ModelId);
        CREATE INDEX IF NOT EXISTS idx_items_guid ON Items (Guid);
        CREATE INDEX IF NOT EXISTS idx_items_feedid ON Items (FeedId);
        CREATE INDEX IF NOT EXISTS idx_items_status ON Items (Status);
        -- TUI 侧栏计数与按源查询的复合索引(RebuildTree 一次 GROUP BY 扫全表受益)
        CREATE INDEX IF NOT EXISTS idx_items_feed_status ON Items (FeedId, Status);
        -- 窗口类命令(今日哈汤/去重簇)按发布时间过滤,百万级必须走索引
        CREATE INDEX IF NOT EXISTS idx_items_pubdate ON Items (PublishDate);
        -- 被改过文章查询(Status='active' AND Version>1)
        CREATE INDEX IF NOT EXISTS idx_items_status_version ON Items (Status, Version);

        -- 全文检索索引(FTS5 + trigram,中文子串可搜):
        -- 数据在 Items,此表只存索引,rowid = Items.Id,由代码增量维护(见 SyncFtsInsert / 删除路径)
        CREATE VIRTUAL TABLE IF NOT EXISTS ItemsFts USING fts5(Title, Content, Description, Summary, tokenize='trigram');
    ";
    cmd.ExecuteNonQuery();

    // 旧库迁移：给已存在的 Items 表补 Summary / SummaryAt 字段（若缺就加）
    try
    {
        cmd.CommandText = "ALTER TABLE Items ADD COLUMN Summary TEXT";
        cmd.ExecuteNonQuery();
    }
    catch (SqliteException) { /* 字段已存在则忽略 */ }
    try
    {
        cmd.CommandText = "ALTER TABLE Items ADD COLUMN SummaryAt TEXT";
        cmd.ExecuteNonQuery();
    }
    catch (SqliteException) { /* 字段已存在则忽略 */ }
    // 旧库迁移：给 Vectors 加 FeedId 列并回填（按 Items 的归属源补上）
    try
    {
        cmd.CommandText = "ALTER TABLE Vectors ADD COLUMN FeedId INTEGER";
        cmd.ExecuteNonQuery();
        cmd.CommandText = @"
            UPDATE Vectors SET FeedId = (
                SELECT Items.FeedId FROM Items WHERE Items.Id = Vectors.ItemId
            )
        ";
        cmd.ExecuteNonQuery();
    }
    catch (SqliteException) { /* 列已存在则忽略 */ }
    // 旧库迁移：给 Feeds 补更新计划相关字段（Schedule=计划表达式，LastCheckedAt=上次拉取时间）
    try { cmd.CommandText = "ALTER TABLE Feeds ADD COLUMN Schedule TEXT"; cmd.ExecuteNonQuery(); }
    catch (SqliteException) { /* 列已存在则忽略 */ }
    try { cmd.CommandText = "ALTER TABLE Feeds ADD COLUMN LastCheckedAt TEXT"; cmd.ExecuteNonQuery(); }
    catch (SqliteException) { /* 列已存在则忽略 */ }
}

// 正常退出标记:下次启动跳过全库 quick_check(大库省 30s+);
// 进程异常退出/被杀时不写标记,下次启动仍会全检,完整性自愈能力不变
static void MarkCleanExit(string dataDir)
{
    try { File.WriteAllText(Path.Combine(dataDir, ".clean-exit"), DateTime.Now.ToString("O")); } catch { }
}

// ══════════ FTS5 全文索引维护（百万级 grep 的关键；trigram 中文子串可搜）══════════
// ItemsFts 只存索引,rowid = Items.Id;数据在 Items,由代码增量维护。
// 老库/新库首次搜索时懒回填(一次性),之后增量同步。

// 懒回填:DoGrep 前检查 FTS 是否已覆盖 Items,未覆盖则重建。
// 注意:fts5 的 COUNT(*) 需扫全部索引(百万级 30s+),改用 MAX(rowid) 对比,
// 回填按 Items.Id 顺序插入,MAX(rowid) == MAX(Id) 即视为已覆盖。
static void EnsureFtsIndexed(string dbPath)
{
    try
    {
        using var conn = OpenDb(dbPath);
        conn.Open();
        long itemsMax, ftsMax;
        var c = conn.CreateCommand();
        c.CommandText = "SELECT MAX(Id) FROM Items";
        var io = c.ExecuteScalar();
        itemsMax = io == null || io is DBNull ? 0 : Convert.ToInt64(io);
        c.CommandText = "SELECT MAX(rowid) FROM ItemsFts";
        var fo = c.ExecuteScalar();
        ftsMax = fo == null || fo is DBNull ? 0 : Convert.ToInt64(fo);   // 空表 MAX 返回 DBNull,必须处理
        if (ftsMax >= itemsMax) return;
        if (itemsMax > 1000) Console.Error.WriteLine(Lang.T("Building full-text index (one-time, first search)..."));
        c.CommandText = "DELETE FROM ItemsFts"; c.ExecuteNonQuery();
        c.CommandText = "INSERT INTO ItemsFts(rowid, Title, Content, Description, Summary) SELECT Id, Title, Content, Description, Summary FROM Items";
        c.ExecuteNonQuery();
    }
    catch { /* FTS 不可用(旧 SQLite 无 trigram)时搜索自动回退 LIKE */ }
}

// 新文章插入后同步 FTS 索引(在调用方事务内;失败静默,下次懒回填会补齐)
static void SyncFtsInsert(SqliteConnection conn, long itemId, string title, string content, string desc, string summary)
{
    try
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO ItemsFts(rowid, Title, Content, Description, Summary) VALUES (@id, @t, @c, @d, @s)";
        cmd.Parameters.AddWithValue("@id", itemId);
        cmd.Parameters.AddWithValue("@t", title ?? "");
        cmd.Parameters.AddWithValue("@c", content ?? "");
        cmd.Parameters.AddWithValue("@d", desc ?? "");
        cmd.Parameters.AddWithValue("@s", summary ?? "");
        cmd.ExecuteNonQuery();
    }
    catch { }
}

// 删除文章(含全部版本)时同步删除 FTS 行
static void SyncFtsDelete(SqliteConnection conn, long itemId)
{
    try
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ItemsFts WHERE rowid = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        cmd.ExecuteNonQuery();
    }
    catch { }
}

// 主库完整性检查：魔数不符/打开失败/quick_check 非 ok → 改名保留现场 → 重建新库；绝不崩溃
// 性能：.clean-exit 标记 = 「最近一次完整性检查通过」。有标记 → 跳过全库 quick_check
// (百万级 + FTS 索引的库可达 2GB+,全检 30s+);无标记才全检,检查通过后立即写标记——
// 之后即使进程被强杀/断电,下次启动也无需再全检(SQLite WAL 崩溃自恢复,打开时自带校验)
static void CheckMainDbIntegrity(string dbPath)
{
    try
    {
        // 加密迁移中断恢复:rss.db 缺失但明文备份存在 → 恢复明文(数据优先;重新 simon level 3 即可再加密)
        string plainBak = dbPath + ".plaintext.bak";
        if (!File.Exists(dbPath) && File.Exists(plainBak))
        {
            try
            {
                File.Move(plainBak, dbPath);
                Console.Error.WriteLine(Lang.T("检测到加密迁移未完成,已从备份恢复数据;可重新执行 simon level 3 加密"));
                try { File.Delete(Path.Combine(Path.GetDirectoryName(dbPath) ?? "", ".db-encrypted")); } catch { }
                string enc = dbPath + ".enc";
                if (File.Exists(enc)) { try { File.Delete(enc); } catch { } }
            }
            catch { /* 恢复失败不阻断(备份仍在,可手动处理) */ }
        }
        if (!File.Exists(dbPath)) return;  // 新建库走正常建表流程
        string cleanMarker = Path.Combine(Path.GetDirectoryName(dbPath) ?? "", ".clean-exit");
        if (File.Exists(cleanMarker))
        {
            try { File.Delete(cleanMarker); } catch { }
            return;
        }
        Console.Error.WriteLine(Lang.T("🔒 孟思琳(simon): 数据库完整性检查中(上次未正常退出;大库约需 30 秒,仅此一次)…"));
        bool ok = TelemetryService.IsSqliteFile(dbPath);
        if (ok)
        {
            // 独立方法 + 显式 Dispose：确保句柄释放后文件才能改名
            ok = QuickCheckOk(dbPath);
        }
        if (ok)
        {
            // 检查通过 → 立即写标记,避免「每次强杀后下次启动都全检」的恶性循环
            try { File.WriteAllText(cleanMarker, DateTime.Now.ToString("O")); } catch { }
            return;
        }
        string corrupt = dbPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        SimonRecord("repair_db", Lang.T("检测到数据库损坏,已保留现场并重建: {0}", Path.GetFileName(corrupt)));
        try
        {
            SqliteConnection.ClearAllPools();
            File.Move(dbPath, corrupt);
        }
        catch (Exception ex)
        {
            // 文件被其他进程占用（并发启动）：不是真损坏现场，本次跳过自愈，下次启动再试
            try { File.Delete(dbPath); } catch { }
            Console.Error.WriteLine("rss.db 完整性检查异常（文件可能被占用，本次跳过自愈）：" + ex.Message);
            return;
        }
        Console.Error.WriteLine(Lang.T("哎呀，麻烦我修复下数据库——检测到损坏，已保留现场并重建：{0}", corrupt));
    }
    catch { /* 完整性检查失败不阻断启动 */ }
}

// 打开主库执行 quick_check；返回是否完好。连接在本方法内用完即关，避免文件句柄占用。
// 带 busy_timeout；busy/locked 与瞬时非 ok 结果重试，连续多次失败才算损坏（与 telemetry 同策略）
static bool QuickCheckOk(string dbPath)
{
    for (int attempt = 0; attempt < 3; attempt++)
    {
        try
        {
            using var conn = OpenDb(dbPath);
            conn.Open();
            var c = conn.CreateCommand();
            c.CommandText = "PRAGMA busy_timeout = 2000;";
            c.ExecuteNonQuery();
            c.CommandText = "PRAGMA quick_check";
            if (c.ExecuteScalar()?.ToString() == "ok") return true;
        }
        catch (SqliteException ex) when (TelemetryService.IsBusyCode(ex.SqliteErrorCode)) { /* 锁冲突：重试 */ }
        catch { return false; }   // 真损坏/打开失败
    }
    return false;
}

// ══════════ 列出指定源的所有文章（用 ROW_NUMBER 显示编号）═══════════
static void ListArticlesFromDb(int feedRealId, int feedDisplayNum, string dbPath, bool json = false, int limit = 0)
{
    using var conn = OpenDb(dbPath);
    conn.Open();

    // 查 Feed 标题
    var titleCmd = conn.CreateCommand();
    titleCmd.CommandText = "SELECT Title FROM Feeds WHERE Id = @id";
    titleCmd.Parameters.AddWithValue("@id", feedRealId);
    string feedTitle = titleCmd.ExecuteScalar()!.ToString()!;

    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT Id, Title, Version,
               ROW_NUMBER() OVER (ORDER BY Id) AS DisplayNum,
               VersionCount, ArchivedCount,
               LENGTH(Content) AS ContentLen, LENGTH(Description) AS DescLen
        FROM (
            SELECT i.Id, i.Title, i.Version, i.Guid, i.Content, i.Description,
                   CASE WHEN i.Guid = '' THEN 1
                        ELSE COUNT(*) OVER (PARTITION BY i.Guid) END AS VersionCount,
                   CASE WHEN i.Guid = '' THEN 0
                        ELSE COUNT(*) FILTER (WHERE i.Status = 'archived') OVER (PARTITION BY i.Guid) END AS ArchivedCount,
                   ROW_NUMBER() OVER (PARTITION BY i.Guid ORDER BY i.Version DESC) AS rn
            FROM Items i
            WHERE i.FeedId = @fid AND i.Guid IS NOT NULL AND i.Status != 'dedup'
        )
        WHERE Guid = '' OR rn = 1
        ORDER BY Id
        " + (limit > 0 ? "LIMIT @limit" : "") + @"
    ";
    cmd.Parameters.AddWithValue("@fid", feedRealId);
    if (limit > 0) cmd.Parameters.AddWithValue("@limit", limit);
    using var reader = cmd.ExecuteReader();
    if (!reader.HasRows)
    {
        if (json) JsonOut(new { success = true, data = new { feedId = feedRealId, feedTitle, articles = Array.Empty<object>() } });
        else Console.WriteLine(Lang.T("  This feed has no articles yet"));
        return;
    }

    var items = new List<(int RealId, int DisplayNum, string Title, bool HasHistory, string Quality)>();
    while (reader.Read())
    {
        int realId = reader.GetInt32(0);
        string title = reader.GetString(1);
        int displayNum = reader.GetInt32(3);
        int archived = reader.GetInt32(5);
        int contentLen = reader.IsDBNull(6) ? 0 : reader.GetInt32(6);
        int descLen = reader.IsDBNull(7) ? 0 : reader.GetInt32(7);
        items.Add((realId, displayNum, title, archived > 0, ContentQualityByLen(contentLen, descLen)));
    }

    // 信号（点赞/AI 标记）只读一次，避免每篇文章重复读磁盘文件
    var signals = LoadSignals();
    SignalEntry? SignalOf(int realId) => signals.TryGetValue(realId.ToString(), out var e) ? e : null;

    if (json)
    {
        JsonOut(new
        {
            success = true,
            data = new
            {
                feedId = feedRealId,
                feedTitle,
                articles = items.Select(a =>
                {
                    var sig = SignalOf(a.RealId);
                    return new
                    {
                        itemId = a.RealId,
                        displayNum = a.DisplayNum,
                        title = a.Title,
                        hasHistory = a.HasHistory,
                        quality = a.Quality,
                        liked = sig?.UserLike ?? false,
                        aiLiked = sig?.AiLike ?? false
                    };
                })
            }
        });
        return;
    }

    Console.WriteLine(Lang.T("── [{0}] {1} article list──", feedDisplayNum, feedTitle));
    Console.WriteLine(Lang.T("  [seq/real] left = list sequence, right = global article ID (use the right one with --show/--versions/--summary)"));
    foreach (var a in items)
    {
        var sig = SignalOf(a.RealId);
        string marks = (sig?.UserLike == true ? "♥" : "") + (sig?.AiLike == true ? "🤖" : "");
        string marker = (marks.Length > 0 ? " " + marks : "") + (a.HasHistory ? " ✎" : "") + (a.Quality == "short" ? Lang.T(" [摘要]") : a.Quality == "empty" ? Lang.T(" [无正文]") : "");
        Console.WriteLine($"  [{a.DisplayNum}/{a.RealId}] {CjkSpace(StripControlChars(a.Title))}{marker}");
    }
}

// 内容质量三级：full（正文完整）/ short（过短，仅摘要）/ empty（无正文）
static string ContentQuality(string content, string desc)
{
    string c = string.IsNullOrWhiteSpace(content) ? desc : content;
    if (string.IsNullOrWhiteSpace(c)) return "empty";
    return c.Trim().Length < 100 ? "short" : "full";
}

// 按长度判质量(列表/搜索只关心长度时,避免把全文文本列读出来——百万级列表的关键优化)
static string ContentQualityByLen(int contentLen, int descLen)
{
    if (contentLen <= 0 && descLen <= 0) return "empty";
    return Math.Max(contentLen, descLen) < 100 ? "short" : "full";
}

// 文章是否存在（--show 全屏模式启动前检查，避免进空界面）
static bool ArticleExists(int itemId, string dbPath)
{
    try
    {
        using var conn = OpenDb(dbPath);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Items WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
    catch { return false; }
}

// ══════════ 原文 JSON 直出（sip --show <文章编号> --json）：供 AI / 脚本读取 ═══════════
// 不做任何渲染，标题/来源/链接/作者等元信息 + 原始正文（Content 原文，空则 Description）原样输出
static void ShowArticleJson(int itemId, string dbPath)
{
    using var conn = OpenDb(dbPath);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT i.Title, i.Content, i.Description, i.Link, i.PublishDate, i.Author, f.Title
        FROM Items i LEFT JOIN Feeds f ON i.FeedId = f.Id
        WHERE i.Id = @id";
    cmd.Parameters.AddWithValue("@id", itemId);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) { ReportError("ITEM_NOT_FOUND", Lang.T("Article {0} not found", itemId), json: true); return; }

    string title = r.GetString(0);
    string content = r.IsDBNull(1) ? "" : r.GetString(1);
    string desc = r.IsDBNull(2) ? "" : r.GetString(2);
    string link = r.IsDBNull(3) ? "" : r.GetString(3);
    string pub = r.IsDBNull(4) ? "" : r.GetString(4);
    string author = r.IsDBNull(5) ? "" : r.GetString(5);
    string feed = r.IsDBNull(6) ? "" : r.GetString(6);

    var sig = GetSignal(itemId);
    // 有全文缓存时合并输出（AI 读全文主路径；无缓存则不出现该字段，避免误导）
    string? fulltext = ReadFulltextCache(itemId);
    JsonOut(new
    {
        success = true,
        data = new
        {
            itemId,
            title,
            feed,
            link,
            published = pub,
            author,
            quality = ContentQuality(content, desc),
            liked = sig?.UserLike ?? false,
            aiLiked = sig?.AiLike ?? false,
            content = string.IsNullOrWhiteSpace(content) ? desc : content,
            fulltext = fulltext
        }
    });
}

// 查看文章版本历史 CLI：--versions <文章Id> [--json]
// 列出同一 Guid 的所有版本；想看某版原文，用 sip --show <该版本的 Id>
static void ListVersionsCli(string arg, string dbPath, bool json = false)
{
    if (!int.TryParse(arg, out int itemId)) { SetExit(); Console.WriteLine(Lang.T("The number must be numeric")); return; }

    using var conn = OpenDb(dbPath);
    conn.Open();
    var gCmd = conn.CreateCommand();
    gCmd.CommandText = "SELECT Guid, Title FROM Items WHERE Id = @id";
    gCmd.Parameters.AddWithValue("@id", itemId);
    using var gr = gCmd.ExecuteReader();
    if (!gr.Read()) { ReportError("ITEM_NOT_FOUND", Lang.T("Article {0} not found", itemId), json: json); return; }
    string guid = gr.IsDBNull(0) ? "" : gr.GetString(0);
    string title = gr.GetString(1);
    gr.Close();

    if (string.IsNullOrEmpty(guid))
    {
        if (json) JsonOut(new { success = true, data = new { itemId, title, versions = Array.Empty<object>() } });
        else Console.WriteLine(Lang.T("This article has no version history (no Guid)"));
        return;
    }

    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id, Version, Status, ArchivedAt, Title FROM Items WHERE Guid = @g ORDER BY Version DESC";
    cmd.Parameters.AddWithValue("@g", guid);
    using var r = cmd.ExecuteReader();
    var list = new List<(long Id, int Version, string Status, string At, string T)>();
    while (r.Read())
        list.Add((r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3), r.GetString(4)));

    if (list.Count <= 1)
    {
        if (json) JsonOut(new { success = true, data = new { itemId, title, versions = Array.Empty<object>() } });
        else Console.WriteLine(Lang.T("This article has only one version, no change history"));
        return;
    }

    if (json)
    {
        JsonOut(new
        {
            success = true,
            data = new
            {
                itemId,
                title,
                versions = list.Select(v => new
                {
                    id = v.Id,
                    version = v.Version,
                    status = v.Status,
                    archivedAt = v.At,
                    title = v.T,
                    current = v.Id == itemId
                })
            }
        });
        return;
    }

    Console.WriteLine(Lang.T("Version history of: {0}", title));
    foreach (var (id, ver, status, at, t) in list)
    {
        string tag = status switch
        {
            "active" => Lang.T("current"),
            "archived" => Lang.T("archived"),
            "deleted" => Lang.T("deleted"),
            _ => ""
        };
        string when = at.Length > 0 && TryParseIso(at) is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm") : "";
        string sep = when.Length > 0 ? " · " : "";
        string mark = id == itemId ? " ←" : "";
        Console.WriteLine($"  [{id}] v{ver}  {tag}{sep}{when}  {StripControlChars(t)}{mark}");
    }
    Console.WriteLine();
    Console.WriteLine(Lang.T("View a version's full text with sip --show <article-id>"));
}

// ══════════ Diff（sip --diff <id> [vA vB] [--json]）═══════════
// 对比同一文章两个版本的正文（Content/Description）；不涉及抓取全文
static void DiffCli(string[] args, string dbPath)
{
    if (args.Length < 1 || !int.TryParse(args[0], out int itemId))
    {
        SetExit(); Console.WriteLine(Lang.T("Usage: sip --diff <article-id> [vA vB] [--json]")); return;
    }
    bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    var vers = args.Where(a => a.StartsWith("v", StringComparison.OrdinalIgnoreCase) && a.Length > 1 && int.TryParse(a[1..], out _))
                   .Select(a => int.Parse(a[1..])).ToList();

    // 查文章 + 所有版本正文
    string guid;
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var gCmd = conn.CreateCommand();
        gCmd.CommandText = "SELECT Guid FROM Items WHERE Id = @id";
        gCmd.Parameters.AddWithValue("@id", itemId);
        var o = gCmd.ExecuteScalar();
        if (o == null) { ReportError("ITEM_NOT_FOUND", Lang.T("Article {0} not found", itemId), json: json); return; }
        guid = o.ToString() ?? "";
    }
    if (string.IsNullOrEmpty(guid))
    {
        if (json)
            JsonOut(new { success = true, article = itemId, from = 0, to = 0, changes = Array.Empty<object>() });
        else
            Console.WriteLine(Lang.T("This article has no version history (no Guid)"));
        return;
    }

    var rows = new List<(int Version, string Text)>();
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Version, Content, Description FROM Items WHERE Guid = @g ORDER BY Version";
        cmd.Parameters.AddWithValue("@g", guid);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add((r.GetInt32(0), string.IsNullOrWhiteSpace(r.IsDBNull(1) ? "" : r.GetString(1))
                ? (r.IsDBNull(2) ? "" : r.GetString(2)) : r.GetString(1)));
    }
    if (rows.Count < 2)
    {
        if (json)
            JsonOut(new { success = true, article = itemId, from = 0, to = 0, changes = Array.Empty<object>() });
        else
            Console.WriteLine(Lang.T("This article has only one version, no change history"));
        return;
    }

    // 选定两个版本：指定 vA vB，或默认最后两个
    (int va, int vb) = SelectDiffVersions(rows, vers);
    var rowA = rows.FirstOrDefault(x => x.Version == va);
    var rowB = rows.FirstOrDefault(x => x.Version == vb);
    if (rowA.Text == null || rowB.Text == null)
    {
        ReportError("VERSION_NOT_FOUND", Lang.T("Version {0} or {1} not found", va, vb), json: json);
        return;
    }

    var diff = new InlineDiffBuilder(new Differ()).BuildDiffModel(rowA.Text, rowB.Text);

    if (json)
    {
        // 把 DiffPlex 的 Deleted/Inserted 相邻配对成 replace；其余为 insert/delete
        var lines = diff.Lines;
        var changes = new List<object>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Type == ChangeType.Unchanged) continue;
            if (lines[i].Type == ChangeType.Deleted && i + 1 < lines.Count && lines[i + 1].Type == ChangeType.Inserted)
            {
                changes.Add(new { type = "replace", before = lines[i].Text, after = lines[i + 1].Text });
                i++;
            }
            else if (lines[i].Type == ChangeType.Deleted)
                changes.Add(new { type = "delete", before = lines[i].Text, after = "" });
            else if (lines[i].Type == ChangeType.Inserted)
                changes.Add(new { type = "insert", before = "", after = lines[i].Text });
        }
        JsonOut(new { article = itemId, from = va, to = vb, changes });
        return;
    }

    Console.WriteLine(Lang.T("v{0} → v{1}", va, vb));
    foreach (var line in diff.Lines)
    {
        switch (line.Type)
        {
            case ChangeType.Inserted: Console.WriteLine($"+ {StripControlChars(line.Text)}"); break;
            case ChangeType.Deleted: Console.WriteLine($"- {StripControlChars(line.Text)}"); break;
            case ChangeType.Modified: Console.WriteLine($"~ {StripControlChars(line.Text)}"); break;
        }
    }
}

// 从请求的 v 后缀选两个版本；不足则默认取最后两个（按版本号）
static (int, int) SelectDiffVersions(List<(int Version, string Text)> rows, List<int> vers)
{
    if (vers.Count >= 2) return (vers[0], vers[1]);
    var byVer = rows.OrderBy(x => x.Version).ToList();
    if (byVer.Count >= 2) return (byVer[^2].Version, byVer[^1].Version);
    return (byVer[0].Version, byVer[0].Version);
}

// ══════════ Markdown 导出（sip --export <id | feed:N | all> [out.md|dir] [--yes]）═══════════
// 导出 = 屏幕所见（BuildArticleMarkdown：原文 + 分界 + 抓取全文，若有缓存）
static void ExportCli(string[] args, string dbPath)
{
    bool yes = args.Any(a => a.Equals("--yes", StringComparison.OrdinalIgnoreCase));
    var pos = args.Where(a => !a.StartsWith("--")).ToList();
    if (pos.Count == 0)
    {
        SetExit(); Console.WriteLine(Lang.T("Usage: sip --export <article-id | feed:number | all> [out.md | dir] [--yes]")); return;
    }
    string target = pos[0];
    string outPath = pos.Count > 1 ? pos[1] : "";

    if (target.Equals("all", StringComparison.OrdinalIgnoreCase))
    {
        if (!yes)
        {
            Console.Write(Lang.T("You sure? This will produce a lot of files. Export all? (y/n) "));
            if (!"y".Equals(Console.ReadLine()?.Trim().ToLower())) { Console.WriteLine(Lang.T("Cancelled")); return; }
        }
        ExportArticlesToDir(GetActiveItemIds(dbPath, null), outPath, dbPath);
        return;
    }
    if (target.StartsWith("feed:", StringComparison.OrdinalIgnoreCase))
    {
        if (!int.TryParse(target["feed:".Length..].Trim(), out int fd)) { SetExit(); Console.WriteLine(Lang.T("Bad format. Correct: {0}", "--export feed:3")); return; }
        int feedReal = GetRealId(fd, dbPath);
        if (feedReal == 0) { SetExit(); Console.WriteLine(Lang.T("Feed number {0} not found", fd)); return; }
        ExportArticlesToDir(GetActiveItemIds(dbPath, feedReal), outPath, dbPath);
        return;
    }
    if (!int.TryParse(target, out int itemId)) { SetExit(); Console.WriteLine(Lang.T("The number must be numeric")); return; }
    if (!ArticleExists(itemId, dbPath)) { SetExit(); Console.WriteLine(Lang.T("Article {0} not found", itemId)); return; }
    string md = BuildArticleMarkdown(itemId, true, dbPath, 90);
    if (string.IsNullOrWhiteSpace(outPath)) outPath = itemId + ".md";
    File.WriteAllText(outPath, md);
    Console.WriteLine(Lang.T("Exported to {0}", outPath));
}

// 取 active 文章 Id；feedReal=null 表示全部
static List<int> GetActiveItemIds(string dbPath, int? feedReal)
{
    var list = new List<int>();
    using var conn = OpenDb(dbPath);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id FROM Items WHERE Status = 'active'" + (feedReal.HasValue ? " AND FeedId = @fid" : "") + " ORDER BY Id";
    if (feedReal.HasValue) cmd.Parameters.AddWithValue("@fid", feedReal.Value);
    using var r = cmd.ExecuteReader();
    while (r.Read()) list.Add(r.GetInt32(0));
    return list;
}

static void ExportArticlesToDir(List<int> itemIds, string dir, string dbPath)
{
    if (string.IsNullOrWhiteSpace(dir)) dir = "sip-export";
    Directory.CreateDirectory(dir);
    int ok = 0;
    foreach (var id in itemIds)
    {
        string md = BuildArticleMarkdown(id, true, dbPath, 90);
        File.WriteAllText(Path.Combine(dir, id + ".md"), md);
        ok++;
    }
    Console.WriteLine(Lang.T("Exported {0} articles to {1}", ok, dir));
}

// ══════════ 列表方法：显示数据库中所有订阅源 ══════════
// ROW_NUMBER() 保证显示出来永远是 1, 2, 3 连续编号（不管中间有没有删过源）
// 但操作（更新/时间戳/删除）仍然用真实 Id，因为 Items 表靠它关联
static void ListFeedsFromDb(string dbPath, bool json = false)
{
    var rows = new List<(int RealId, int DisplayNum, string Title, int Active, int Archived, int Deleted, DateTime? LastChecked, string Schedule)>();
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, Title,
                   (SELECT COUNT(*) FROM Items WHERE FeedId = Feeds.Id AND Status = 'active')   AS ActiveCount,
                   (SELECT COUNT(*) FROM Items WHERE FeedId = Feeds.Id AND Status = 'archived') AS ArchiveCount,
                   (SELECT COUNT(*) FROM Items WHERE FeedId = Feeds.Id AND Status = 'deleted')  AS DeleteCount,
                   ROW_NUMBER() OVER (ORDER BY Id) AS DisplayNum,
                   LastCheckedAt, Schedule
            FROM Feeds";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(5), reader.GetString(1),
                reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
                reader.IsDBNull(6) ? null : TryParseIso(reader.GetString(6)),
                reader.IsDBNull(7) ? "" : reader.GetString(7)));
    }

    if (rows.Count == 0)
    {
        if (json) JsonOut(new { success = true, data = new { feeds = Array.Empty<object>() } });
        else Console.WriteLine(Lang.T("No feeds in the database yet"));
        return;
    }

    if (json)
    {
        var health = LoadFeedHealth();
        JsonOut(new
        {
            success = true,
            data = new
            {
                feeds = rows.Select(r =>
                {
                    health.TryGetValue(r.RealId, out var h);
                    return new
                    {
                        id = r.RealId,
                        displayNum = r.DisplayNum,
                        title = r.Title,
                        active = r.Active,
                        archived = r.Archived,
                        deleted = r.Deleted,
                        schedule = r.Schedule,
                        lastChecked = r.LastChecked,
                        health = h.FailCount > 0 ? "failed" : (r.LastChecked is DateTime lc && IsFeedStale(r.Schedule, lc, DateTime.Now) ? "stale" : "ok"),
                        failCount = h.FailCount
                    };
                })
            }
        });
        return;
    }

    var now = DateTime.Now;
    var policy = LoadSourcePolicy();
    foreach (var r in rows)
    {
        var parts = new List<string>();
        if (r.Active > 0) parts.Add(Lang.T("{0} current", r.Active + r.Deleted));
        if (r.Archived > 0) parts.Add(Lang.T("{0} changed", r.Archived));
        if (r.Deleted > 0) parts.Add(Lang.T("{0} deleted by author, but archived for you", r.Deleted));
        string stats = string.Join(", ", parts);
        string status = FormatFeedStatus(r.Schedule, r.LastChecked, now);
        string healthText = FeedHealthText(r.RealId, r.Schedule, r.LastChecked, now);
        // 健康标记只显示异常（正常不显示）
        string marker = healthText == Lang.T("正常") ? "" : "  " + healthText;
        policy.TryGetValue(r.RealId, out var pl);
        Console.WriteLine($"[{r.DisplayNum}] {StripControlChars(r.Title)} {stats}{status}{marker}{PolicyMarker(pl)}");
    }
}

// ══════════ 更新计划（调度）═══════════
// 每个订阅源可设一条「更新计划」，到期才自动拉取，避免浪费资源：
//   间隔：     5m / 30m / 1h / 6h / 1d / 7d / 30d
//   固定时刻： daily@HH:mm  /  weekly@Ddd HH:mm（Ddd = Mon..Sun）
//   manual 或空：不自动更新
// 到期判断：now >= 上次拉取时间 + 计划到期点；LastCheckedAt 为空视为首次，到期更新一次。
// 每次成功拉取（手动 U / F6 / --sync / 启动同步）都会重写 LastCheckedAt，计时从最新拉取重算。

static FeedSchedule? TryParseSchedule(string raw)
{
    string r = (raw ?? "").Trim();
    if (r.Length == 0 || r.Equals("manual", StringComparison.OrdinalIgnoreCase))
        return new FeedSchedule { Raw = r, IsManual = true };
    string lower = r.ToLowerInvariant();

    var mInterval = Regex.Match(lower, @"^(\d+)([mhd])$");
    if (mInterval.Success)
    {
        double n = int.Parse(mInterval.Groups[1].Value);
        double minutes = mInterval.Groups[2].Value switch { "m" => n, "h" => n * 60, _ => n * 1440 };
        return new FeedSchedule { Raw = r, Interval = TimeSpan.FromMinutes(minutes) };
    }

    if (lower.StartsWith("daily@"))
    {
        if (TryParseHhmm(lower["daily@".Length..], out int h, out int m))
            return new FeedSchedule { Raw = r, IsDaily = true, DailyHour = h, DailyMinute = m };
        return null;   // 无效 → 返回 null 表示表达式有误
    }

    if (lower.StartsWith("weekly@"))
    {
        var parts = lower["weekly@".Length..].Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && TryParseWeekday(parts[0], out int dow) && TryParseHhmm(parts[1], out int h, out int m))
            return new FeedSchedule { Raw = r, IsWeekly = true, WeeklyDay = dow, WeeklyHour = h, WeeklyMinute = m };
        return null;
    }

    return null;   // 无效表达式
}

static bool TryParseHhmm(string s, out int hour, out int minute)
{
    hour = minute = 0;
    var p = s.Split(':');
    if (p.Length != 2) return false;
    if (!int.TryParse(p[0], out hour) || !int.TryParse(p[1], out minute)) return false;
    return hour is >= 0 and <= 23 && minute is >= 0 and <= 59;
}

static bool TryParseWeekday(string s, out int dow)
{
    dow = s switch
    {
        "sun" or "sunday" => 0,
        "mon" or "monday" => 1,
        "tue" or "tuesday" => 2,
        "wed" or "wednesday" => 3,
        "thu" or "thursday" => 4,
        "fri" or "friday" => 5,
        "sat" or "saturday" => 6,
        _ => -1
    };
    return dow >= 0;
}

static string WeekdayName(int dow) => new[] { Lang.T("Sun"), Lang.T("Mon"), Lang.T("Tue"), Lang.T("Wed"), Lang.T("Thu"), Lang.T("Fri"), Lang.T("Sat") }[dow];

// 计算某源的下一次到期时间；手动/无效计划返回 null
static DateTime? ComputeNextDue(FeedSchedule s, DateTime lastChecked, DateTime now)
{
    if (s.IsManual) return null;
    if (s.Interval.HasValue)
        return lastChecked.Add(s.Interval.Value);
    if (s.IsDaily)
    {
        var cand = new DateTime(now.Year, now.Month, now.Day, s.DailyHour, s.DailyMinute, 0);
        if (cand <= lastChecked) cand = cand.AddDays(1);
        return cand;
    }
    if (s.IsWeekly)
    {
        var cand = new DateTime(now.Year, now.Month, now.Day, s.WeeklyHour, s.WeeklyMinute, 0);
        int diff = (s.WeeklyDay - (int)cand.DayOfWeek + 7) % 7;
        cand = cand.AddDays(diff);
        if (cand <= lastChecked) cand = cand.AddDays(7);
        return cand;
    }
    return null;
}

static bool IsFeedDue(string schedule, DateTime? lastChecked, DateTime now)
{
    var s = TryParseSchedule(schedule);
    if (s == null || s.IsManual) return false;
    if (lastChecked == null) return true;   // 首次：到期
    var due = ComputeNextDue(s, lastChecked.Value, now);
    return due != null && now >= due.Value;
}

// 列出当前到期的订阅源（归档源跳过）
static List<DueFeed> GetDueFeeds(string dbPath)
{
    var now = DateTime.Now;
    var list = new List<DueFeed>();
    using var conn = OpenDb(dbPath);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id, Title, FeedUrl, LastCheckedAt, Schedule FROM Feeds ORDER BY Id";
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        int id = r.GetInt32(0);
        string title = r.GetString(1);
        string url = r.IsDBNull(2) ? "" : r.GetString(2);
        DateTime? lc = r.IsDBNull(3) ? null : TryParseIso(r.GetString(3));
        string schedule = r.IsDBNull(4) ? "" : r.GetString(4);
        if (IsArchived(title) || string.IsNullOrWhiteSpace(url)) continue;
        if (IsFeedDue(schedule, lc, now))
            list.Add(new DueFeed { Id = id, Title = title, Url = url, LastChecked = lc, Schedule = schedule });
    }
    return list;
}

static DateTime? TryParseIso(string s)
{
    return DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
}

// 返回某源的下一次到期时间（用于「距离下次还需多久」提示）；手动/未设置/从未检查返回 null
static DateTime? FeedNextDue(string schedule, DateTime? lastChecked, DateTime now)
{
    var s = TryParseSchedule(schedule);
    if (s == null || s.IsManual || lastChecked == null) return null;
    return ComputeNextDue(s, lastChecked.Value, now);
}

static string HumanSchedule(FeedSchedule s)
{
    if (s.IsManual) return Lang.T("manual");
    if (s.Interval is TimeSpan iv)
    {
        double minutes = iv.TotalMinutes;
        if (minutes < 60) return Lang.T("{0} min", (int)minutes);
        if (minutes < 1440) return Lang.T("{0} hr", (int)(minutes / 60));
        return Lang.T("{0} days", (int)(minutes / 1440));
    }
    if (s.IsDaily) return Lang.T("daily at {0:00}:{1:00}", s.DailyHour, s.DailyMinute);
    if (s.IsWeekly) return Lang.T("weekly {0} {1:00}:{2:00}", WeekdayName(s.WeeklyDay), s.WeeklyHour, s.WeeklyMinute);
    return "";
}

static string AgoText(DateTime t, DateTime now)
{
    var span = now - t;
    if (span.TotalSeconds < 60) return Lang.T("just now");
    if (span.TotalMinutes < 60) return Lang.T("{0} min ago", (int)span.TotalMinutes);
    if (span.TotalHours < 24) return Lang.T("{0} hr ago", (int)span.TotalHours);
    return Lang.T("{0} days ago", (int)span.TotalDays);
}

static string UntilText(DateTime t, DateTime now)
{
    var span = t - now;
    if (span.TotalSeconds < 60) return Lang.T("soon");
    if (span.TotalMinutes < 60) return Lang.T("in {0} min", (int)span.TotalMinutes);
    if (span.TotalHours < 24) return Lang.T("in {0} hr", (int)span.TotalHours);
    return Lang.T("in {0} days", (int)span.TotalDays);
}

// -l 里追加的「频率 / 上次 / 下次」状态；手动或未设置时返回空串
static string FormatFeedStatus(string schedule, DateTime? lastChecked, DateTime now)
{
    var s = TryParseSchedule(schedule);
    if (s == null || s.IsManual) return "";
    string sched = HumanSchedule(s);
    string last = lastChecked is DateTime lc ? AgoText(lc, now) : Lang.T("never");
    string next;
    if (lastChecked is DateTime lc2)
    {
        var due = ComputeNextDue(s, lc2, now);
        next = due is DateTime d ? Lang.T("in ") + UntilText(d, now) : "—";
    }
    else
    {
        next = Lang.T("first update pending");
    }
    return Lang.T("({0} · last {1} · next {2})", sched, last, next);
}


// ══════════ 核心方法：下载 RSS → 解析 → 去重 → 写入数据库 ══════════
static async Task DownloadAndSaveToDb(string url, string dbPath, bool interactive = true)
{
    // 用户可能忘记 https:// 或 http:// 前缀，自动补全；
    // 若补全的 https 连不上（站点只提供 http），再回退 http 重试一次
    string raw = url.Trim();
    bool wasAutoPrefixed = !(raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                             raw.StartsWith("//", StringComparison.OrdinalIgnoreCase));
    url = EnsureUrlScheme(raw);

    // --- 第 1 步：下载 RSS 原始 XML ---
    // 不加 User-Agent 有些服务器会返回 403 拒绝
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
    Console.WriteLine(Lang.T("Downloading..."));

    string rawXml;
    try
    {
        rawXml = await client.GetStringAsync(url);
    }
    catch (HttpRequestException) when (wasAutoPrefixed && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        // https 失败 → 站点可能只支持 http，重试一次
        string httpUrl = "http://" + url["https://".Length..];
        Console.WriteLine(Lang.T("https:// failed, retrying with http://{0}...", httpUrl["http://".Length..]));
        rawXml = await client.GetStringAsync(httpUrl);
        url = httpUrl;  // 后续用有效的 http 地址写入 / 更新
    }

    // --- 第 2 步：解析 ---
    var feed = FeedReader.ReadFromString(rawXml);

    // --- 第 3 步：打开数据库 ---
    using var conn = OpenDb(dbPath);
    conn.Open();

    // 第 4~5 步(源更新 + 文章比对/插入)包进一个事务:大源导入从逐条 fsync 变为一次提交,
    // 中途失败整体回滚,不会留下半截数据
    using var tx = conn.BeginTransaction();

    // --- 第 4 步：检查是否已存在同名且未归档的订阅源 ---
    // 已归档的（标题带时间戳）不参与比对，直接当新源处理
    string? oldXml = GetActiveRawXml(feed.Title, conn);
    long feedId;

    bool isNewFeed;  // 新源还是更新已有源

    if (oldXml != null)
    {
        // 同名未归档源存在！先用文本 diff 比对 Feed 级别变化
        isNewFeed = false;
        Console.WriteLine(Lang.T("Feed {0} already exists, comparing...", feed.Title));
        bool hasChanges = ShowFeedXmlDiff(oldXml, rawXml);

        if (hasChanges)
        {
            var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = @"
                UPDATE Feeds SET RawXml = @rawXml, LastFetched = @fetched
                WHERE Title = @title
            ";
            updateCmd.Parameters.AddWithValue("@rawXml", rawXml);
            updateCmd.Parameters.AddWithValue("@fetched", DateTime.Now.ToString("O"));
            updateCmd.Parameters.AddWithValue("@title", feed.Title);
            updateCmd.ExecuteNonQuery();
            Console.WriteLine(Lang.T("Content changed, feed updated"));
        }
        else
        {
            Console.WriteLine(Lang.T("No content change, skipped update"));
        }

        var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT Id FROM Feeds WHERE Title = @title";
        idCmd.Parameters.AddWithValue("@title", feed.Title);
        feedId = (long)idCmd.ExecuteScalar()!;
    }
    else
    {
        // 新订阅源 → 插入（不含归档源的冲突）
        isNewFeed = true;
        var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = @"
            INSERT INTO Feeds (Title, FeedUrl, Link, Description, LastFetched, RawXml, LastCheckedAt)
            VALUES (@title, @url, @link, @desc, @fetched, @rawXml, @checked)
        ";
        insertCmd.Parameters.AddWithValue("@title", feed.Title);
        insertCmd.Parameters.AddWithValue("@url", url);
        insertCmd.Parameters.AddWithValue("@link", feed.Link ?? "");
        insertCmd.Parameters.AddWithValue("@desc", feed.Description ?? "");
        insertCmd.Parameters.AddWithValue("@fetched", DateTime.Now.ToString("O"));
        insertCmd.Parameters.AddWithValue("@rawXml", rawXml);
        insertCmd.Parameters.AddWithValue("@checked", DateTime.Now.ToString("O"));
        insertCmd.ExecuteNonQuery();

        insertCmd.CommandText = "SELECT last_insert_rowid()";
        feedId = (long)insertCmd.ExecuteScalar()!;
    }

    // 遥测：新增订阅源（feed 生命周期，仅新源）
    if (isNewFeed)
        TelemetryService.Record("feed_change", sourceId: (int)feedId, data: new { action = "add", title = feed.Title });

    // 无论内容是否有变化，都记录「上次拉取时间」——调度只关心上次何时真正查过
    var touchCmd = conn.CreateCommand();
    touchCmd.CommandText = "UPDATE Feeds SET LastCheckedAt = @checked WHERE Id = @id";
    touchCmd.Parameters.AddWithValue("@checked", DateTime.Now.ToString("O"));
    touchCmd.Parameters.AddWithValue("@id", feedId);
    touchCmd.ExecuteNonQuery();

    // --- 第 5 步：ShowDiff 负责检测文章变化 + 输出 + 执行归档/插入/标记删除 ---
    // 新源 → 全量插入不过滤；旧源 → 逐篇比对
    ShowDiff(feed, feedId, conn, isNewFeed);
    tx.Commit();   // 源更新 + 文章变更一次性落盘

    Console.WriteLine(Lang.T("{0} saved", feed.Title));

    // --- 第 6 步：若已初始化 AI，询问是否把该源未向量化的文章加入 embedding ---
    await MaybeIndexNewArticles(feedId, dbPath, interactive);
}

// ══════════ 辅助方法：下载/更新后询问是否对新文章做向量化 ══════════
// 仅当已执行过 --init（存在 ai_config.json）时才会询问，避免打扰未配置 AI 的用户
// ask=false（自动同步/后台检查）时跳过询问，默认不向量化，避免卡在读输入
static async Task MaybeIndexNewArticles(long feedId, string dbPath, bool ask = true)
{
    if (!File.Exists(ConfigPath(dbPath))) return;

    var cfg = LoadConfig(dbPath);
    using var conn = OpenDb(dbPath);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT COUNT(*) FROM Items i
        WHERE i.FeedId = @fid AND i.Status = 'active'
        AND NOT EXISTS (SELECT 1 FROM Vectors v WHERE v.ItemId = i.Id)
    ";
    cmd.Parameters.AddWithValue("@fid", feedId);
    long pending = (long)cmd.ExecuteScalar()!;
    if (pending == 0) return;

    // 自动同步 / 后台检查时跳过 y/n 询问，不打扰、不卡输入
    if (!ask) { Console.WriteLine(Lang.T("{0} new articles not embedded (auto-sync, skipped; use sip --index when needed)", pending)); return; }

    Console.WriteLine(Lang.T("This feed has {0} new articles not yet embedded. Add to semantic search ({1})? (y/n)", pending, cfg.Embedding.Model));
    if (Console.ReadLine()?.Trim().ToLower() != "y") { Console.WriteLine(Lang.T("Skipped, run sip --index later if needed")); return; }

    cmd.CommandText = "SELECT Id, Title FROM Items WHERE FeedId = @fid AND Status = 'active' AND NOT EXISTS (SELECT 1 FROM Vectors v WHERE v.ItemId = Items.Id)";
    using var r = cmd.ExecuteReader();
    var articles = new List<(int Id, string Title)>();
    while (r.Read()) articles.Add((r.GetInt32(0), r.GetString(1)));
    r.Close();

    int modelId = EnsureModel(dbPath, cfg.Embedding);
    int ok = 0, fail = 0;
    foreach (var a in articles)
    {
        var vec = await SafeEmbed(a.Title, cfg, articleId: a.Id, sourceId: (int)feedId);
        if (vec == null) { fail++; continue; }
        if (vec.Length != cfg.Embedding.Dimensions)
        {
            cfg.Embedding.Dimensions = vec.Length;
            SaveConfig(dbPath, cfg);
        }
        SaveVector(dbPath, (int)feedId, a.Id, modelId, vec);
        ok++;
    }
    Console.WriteLine(Lang.T("Embedding done: {0} OK, {1} failed", ok, fail));
}

// ══════════ 辅助方法：补全 URL 协议前缀 ══════════
// 用户可能直接输入 "example.com/rss" 而忘记 https:// 或 http://
// 无协议时默认补 https://（GET 失败会由调用方捕获提示）
static string EnsureUrlScheme(string url)
{
    string trimmed = url.Trim();
    if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        return trimmed;
    if (trimmed.StartsWith("//", StringComparison.OrdinalIgnoreCase))
        return "https:" + trimmed;
    Console.WriteLine(Lang.T("URL missing a scheme, auto-prefixed to https://{0}", trimmed));
    return "https://" + trimmed;
}

// ══════════ 辅助方法：规范化 OpenAI 兼容端点 ══════════
// 用户常只填 "http://host:11434"，这里补上 "/v1"（OpenAI 兼容路径）；
// 缺协议头（如 "open.cherryin.net/v1"）时静默补 https://，避免请求崩溃
static string EnsureV1Endpoint(string ep)
{
    string e = ep.Trim().TrimEnd('/');
    if (e.Length == 0) return e;
    if (!e.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !e.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        e = "https://" + e;
    if (e.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        return e;
    return e + "/v1";
}

// ══════════ 辅助方法：按标题查未归档源的 RawXml ══════════
// 只匹配无时间戳后缀的源，已归档的（带 _yyyymmdd_hhmmss）不参与比对
// 返回 null = 没找到或全是归档源 → 当作新源处理
static string? GetActiveRawXml(string title, SqliteConnection conn)
{
    // 先查出所有同名源的 RawXml 和 Title，用 C# IsArchived 过滤
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Title, RawXml FROM Feeds WHERE Title = @title OR Title LIKE @title || '\\_%' ESCAPE '\\'";
    cmd.Parameters.AddWithValue("@title", title);

    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        string t = reader.GetString(0);
        if (!IsArchived(t))  // 只返回未归档的
            return reader.GetString(1);
    }
    return null;  // 没找到或全是归档源
}

// ══════════ 判断标题是否有时间戳后缀（即是否已被归档）═══════════
static bool IsArchived(string title)
{
    return Regex.IsMatch(title, @"_\d{8}_\d{6}$");
}


// ══════════ 显示编号 → 真实 Id ══════════
// 列表显示用了 ROW_NUMBER()，用户输入的是显示编号（1,2,3...）
// 这个方法把显示编号转换成数据库里真实的 Id（可能是 1,3,5...有断档）
// 返回 0 表示找不到
static int GetRealId(int displayNum, string dbPath)
{
    using var conn = OpenDb(dbPath);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT Id FROM (
            SELECT Id, ROW_NUMBER() OVER (ORDER BY Id) AS DisplayNum
            FROM Feeds
        ) WHERE DisplayNum = @n
    ";
    cmd.Parameters.AddWithValue("@n", displayNum);
    object? result = cmd.ExecuteScalar();
    return result is null ? 0 : Convert.ToInt32(result);
}

// ══════════ 删除订阅源 + 它的所有文章 ══════════
// 删除订阅源；yes=true（--yes/-y）跳过确认，供脚本/AI 非交互使用
static void DeleteFeed(int displayNum, string dbPath, bool yes = false)
{
    int realId = GetRealId(displayNum, dbPath);
    if (realId == 0) { SetExit(); Console.WriteLine(Lang.T("Feed number not found")); return; }

    using var conn = OpenDb(dbPath);
    conn.Open();

    // 1. 查标题和文章数，用于确认提示
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT Title, (SELECT COUNT(*) FROM Items WHERE FeedId = @id)
        FROM Feeds WHERE Id = @id
    ";
    cmd.Parameters.AddWithValue("@id", realId);
    using var reader = cmd.ExecuteReader();
    reader.Read();
    string title = reader.GetString(0);
    int itemCount = reader.GetInt32(1);
    reader.Close();

    if (!yes)
    {
        Console.Write(Lang.T("Delete {0} and its {1} articles? (y/n)", title, itemCount));
        if (!"y".Equals(Console.ReadLine()?.Trim().ToLower()))
        {
            Console.WriteLine(Lang.T("Cancelled"));
            return;
        }
    }

    // 2. 先删该源的向量和文章
    cmd.CommandText = "DELETE FROM Vectors WHERE FeedId = @id";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "DELETE FROM Items WHERE FeedId = @id";
    cmd.ExecuteNonQuery();

    // 3. 再删订阅源
    cmd.CommandText = "DELETE FROM Feeds WHERE Id = @id";
    cmd.ExecuteNonQuery();

    CleanOrphanDedup(realId);
    Console.WriteLine(Lang.T("{0} deleted", title));
}

// ══════════ 加时间戳：标题 + _20260712_143000 ══════════
// 加完后标题变了，下次下载同名源时 GetOldRawXml 找不到，
// 就会被当作新订阅源处理，不会触发去重
static void AddTimestamp(int displayNum, string dbPath)
{
    int realId = GetRealId(displayNum, dbPath);
    if (realId == 0) { SetExit(); Console.WriteLine(Lang.T("Feed number not found")); return; }

    using var conn = OpenDb(dbPath);
    conn.Open();

    // 1. 查当前标题
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Title FROM Feeds WHERE Id = @id";
    cmd.Parameters.AddWithValue("@id", realId);
    string oldTitle = cmd.ExecuteScalar()!.ToString()!;

    // 2. 已经归档的不能再归档
    if (IsArchived(oldTitle))
    {
        Console.WriteLine(Lang.T("{0} is already archived", oldTitle));
        return;
    }

    // 3. 追加时间戳
    string newTitle = oldTitle + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

    // 4. 更新
    cmd.CommandText = "UPDATE Feeds SET Title = @newTitle WHERE Id = @id";
    cmd.Parameters.AddWithValue("@newTitle", newTitle);
    cmd.ExecuteNonQuery();

    Console.WriteLine(Lang.T("Title changed: {0} → {1} ", oldTitle, newTitle));
}

// ══════════ 去时间戳：去掉 _yyyymmdd_hhmmss 后缀 ══════════
// 去掉之前检查原始标题是否已存在，防止冲突
static void RemoveTimestamp(int displayNum, string dbPath)
{
    int realId = GetRealId(displayNum, dbPath);
    if (realId == 0) { SetExit(); Console.WriteLine(Lang.T("Feed number not found")); return; }

    using var conn = OpenDb(dbPath);
    conn.Open();

    // 1. 查当前标题
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Title FROM Feeds WHERE Id = @id";
    cmd.Parameters.AddWithValue("@id", realId);
    string title = cmd.ExecuteScalar()!.ToString()!;

    // 2. 用正则去掉末尾 _8位数字_6位数字 的时间戳
    string plainTitle = Regex.Replace(title, @"_\d{8}_\d{6}$", "");

    if (plainTitle == title)
    {
        Console.WriteLine(Lang.T("{0} was not archived", title));
        return;
    }

    // 3. 检查 plainTitle 是否已被其他源占用（排除自己）
    cmd.CommandText = "SELECT COUNT(*) FROM Feeds WHERE Title = @title AND Id != @id";
    cmd.Parameters.AddWithValue("@title", plainTitle);
    long conflict = (long)cmd.ExecuteScalar()!;
    if (conflict > 0)
    {
        Console.WriteLine(Lang.T("Conflict! Another feed named {0} exists, cannot remove the timestamp", plainTitle));
        return;
    }

    // 4. 安全 → 更新
    cmd.CommandText = "UPDATE Feeds SET Title = @newTitle WHERE Id = @id";
    cmd.Parameters.AddWithValue("@newTitle", plainTitle);
    cmd.ExecuteNonQuery();

    Console.WriteLine(Lang.T("Timestamp removed: {0} → {1} ", title, plainTitle));
}

// ════════════════════════════════════════════════════════
// 下面是 ShowDiff 的两个版本
// ════════════════════════════════════════════════════════

// ══════════ 辅助方法：插入一篇新文章到 Items 表 ══════════
// 统一管理 INSERT SQL，避免三处重复写同样的代码
static void InsertNewItem(SqliteConnection conn, long feedId, FeedItem item, string guid, int version)
{
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        INSERT INTO Items (FeedId, Title, Link, Description, Author, PublishDate, Content, Guid, Status, Version)
        VALUES (@fid, @title, @link, @desc, @author, @pub, @content, @guid, 'active', @ver)
    ";
    cmd.Parameters.AddWithValue("@fid", feedId);
    cmd.Parameters.AddWithValue("@title", item.Title ?? "");
    cmd.Parameters.AddWithValue("@link", item.Link ?? "");
    cmd.Parameters.AddWithValue("@desc", item.Description ?? "");
    cmd.Parameters.AddWithValue("@author", item.Author ?? "");
    cmd.Parameters.AddWithValue("@pub", item.PublishingDate?.ToString("O") ?? "");
    cmd.Parameters.AddWithValue("@content", item.Content ?? "");
    cmd.Parameters.AddWithValue("@guid", guid);
    cmd.Parameters.AddWithValue("@ver", version);
    cmd.ExecuteNonQuery();

    // 全文索引增量同步(与插入同事务;失败静默,懒回填会兜底)
    cmd.CommandText = "SELECT last_insert_rowid()";
    long newId = (long)cmd.ExecuteScalar()!;
    SyncFtsInsert(conn, newId, item.Title ?? "", item.Content ?? "", item.Description ?? "", "");
}

// ══════════ ShowDiff（文章级别）：检测新增/修改/删除 + 输出 + 执行 ══════════
// isNewFeed=true  → 新订阅源，全量插入 + 跳过删除检测
// isNewFeed=false → 已有源，逐篇比对：新增/修改/删除
static void ShowDiff(Feed newFeed, long feedId, SqliteConnection conn, bool isNewFeed = false)
{
    int newCount = 0;
    int modifyCount = 0;

    foreach (var item in newFeed.Items)
    {
        string guid = item.Id ?? item.Link ?? "";

        if (isNewFeed)
        {
            // 新源模式：不查重，直接插入
            InsertNewItem(conn, feedId, item, guid, version: 1);
            newCount++;
            continue;
        }

        // --- 更新模式：查是否已有 active 状态的同 Guid 文章 ---
        var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = @"
            SELECT Id, Version, Title, Content
            FROM Items WHERE Guid = @guid AND Status = 'active'
        ";
        checkCmd.Parameters.AddWithValue("@guid", guid);

        using var reader = checkCmd.ExecuteReader();

        if (reader.Read())
        {
            // --- 已有 → 检查内容是否变化 ---
            long existingId = reader.GetInt64(0);
            int oldVersion = reader.GetInt32(1);
            string oldContent = reader.IsDBNull(3) ? "" : reader.GetString(3);
            reader.Close();

            if (oldContent == (item.Content ?? ""))
                continue;  // 内容相同 → 跳过

            // 内容不同 → 强制归档该 Guid 下所有 active 的旧版（防止残留多版本）
            var archiveCmd = conn.CreateCommand();
            archiveCmd.CommandText = @"
                UPDATE Items SET Status = 'archived', ArchivedAt = @now
                WHERE Guid = @guid AND Status = 'active'
            ";
            archiveCmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
            archiveCmd.Parameters.AddWithValue("@guid", guid);
            archiveCmd.ExecuteNonQuery();

            // 插入新版本
            InsertNewItem(conn, feedId, item, guid, version: oldVersion + 1);

            Console.WriteLine(Lang.T("  [archived] {0} author changed content, old version kept", item.Title));
            modifyCount++;
        }
        else
        {
            reader.Close();
            // 新文章 → 直接插入（先查跨源去重规则，命中则跳过，避免卷土重来）
            if (DedupImportBlocked(conn, feedId, item))
                continue;
            InsertNewItem(conn, feedId, item, guid, version: 1);
            newCount++;
        }
    }

    // 新源跳过修改检测（没有旧数据可比）
    if (isNewFeed)
    {
        Console.WriteLine(Lang.T("  {0} new", newCount));
        return;
    }

    // 不检测删除：很多站点 RSS 只推最近 N 篇，老文章不在列表里不代表被删，
    // 因此只跟踪新增与修改，避免把正常下架的文章误标为 deleted
    Console.WriteLine(Lang.T("  {0} new, {1} modified", newCount, modifyCount));
}

// ══════════ ShowDiff（Feed 级别）：纯文本比对，看旧 XML 和新 XML 有无差异 ══════════
// 只负责输出和返回 bool，不做任何数据库操作
static bool ShowFeedXmlDiff(string oldRaw, string newRaw)
{
    try
    {
        var oldFeed = FeedReader.ReadFromString(oldRaw);  // 把旧 XML 解析成 Feed 对象
        var newFeed = FeedReader.ReadFromString(newRaw);  // 把新 XML 解析成 Feed 对象

        // 把每条文章压成一行摘要（方便做 diff），然后用换行拼成一个大字符串
        string oldSummary = string.Join(Environment.NewLine, oldFeed.Items.Select(GetItemSummary));
        string newSummary = string.Join(Environment.NewLine, newFeed.Items.Select(GetItemSummary));

        // DiffPlex 是做文本比较的库，比较两个字符串哪行多了、少了、改了
        var diffResult = new InlineDiffBuilder(new Differ()).BuildDiffModel(oldSummary, newSummary);

        bool hasChanges = false;
        foreach (var line in diffResult.Lines)  // 逐行看差异
        {
            switch (line.Type)
            {
                case ChangeType.Inserted:   // 新增文章（新 RSS 有、旧 RSS 没有）
                    Console.WriteLine($"+ {StripControlChars(line.Text)}");
                    hasChanges = true;
                    break;
                case ChangeType.Deleted:    // 被删掉的文章（旧 RSS 有、新 RSS 没有）
                    Console.WriteLine($"- {StripControlChars(line.Text)}");
                    hasChanges = true;
                    break;
                case ChangeType.Modified:   // 内容被修改的文章
                    Console.WriteLine($"~ {StripControlChars(line.Text)}");
                    hasChanges = true;
                    break;
            }
        }

        if (!hasChanges)  // 一个变化都没有
            Console.WriteLine(Lang.T("New and old RSS are identical, no changes"));

        return hasChanges;  // 把结果返回给调用方，让它决定是否更新
    }
    catch (Exception ex)
    {
        Console.WriteLine(Lang.T("Error comparing item diffs: {0}", ex.Message));
        return false;  // 出错了保守处理：不用旧数据覆盖，当作没变化
    }
}

// ══════════ GetItemSummary：生成文章摘要行，供文本 diff 显示用 ══════════
static string GetItemSummary(FeedItem item)
{
    string id = !string.IsNullOrEmpty(item.Id) ? item.Id : item.Link ?? item.Title ?? Lang.T("unknown");
    return $"[{id}] {item.Title}";
}

// ══════════════════════════════════════════════════════════
// AI 相关功能：配置、凭据、Embedding、向量、搜索、摘要
// ══════════════════════════════════════════════════════════
// （配置类 AiConfig / EmbeddingCfg / LlmCfg / SearchHit / AiException 见文件末尾类型区）

static string ConfigPath(string dbPath) => Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", "ai_config.json");

static AiConfig LoadConfig(string dbPath)
{
    string path = ConfigPath(dbPath);
    AiConfig? cfg = null;
    if (File.Exists(path))
    {
        try
        {
            // 大小写不敏感绑定：兼容手写/旧版小驼峰配置（apiEndpoint/searchThreshold 等）
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            cfg = JsonSerializer.Deserialize<AiConfig>(File.ReadAllText(path), opts);
        }
        catch { /* 配置损坏时用默认值 */ }
    }
    cfg ??= new AiConfig();
    // 容错：手写/旧配置缺协议头时补全（静默，不打扰用户）
    cfg.Embedding.ApiEndpoint = NormalizeEndpoint(cfg.Embedding.ApiEndpoint);
    cfg.Llm.ApiEndpoint = NormalizeEndpoint(cfg.Llm.ApiEndpoint);
    return cfg;
}

// 端点缺协议头时静默补 https://
static string NormalizeEndpoint(string ep)
{
    string e = ep.Trim();
    if (e.Length == 0) return e;
    if (e.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || e.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        return e;
    return "https://" + e;
}

static void SaveConfig(string dbPath, AiConfig cfg)
{
    var opts = new JsonSerializerOptions { WriteIndented = true };
    File.WriteAllText(ConfigPath(dbPath), JsonSerializer.Serialize(cfg, opts));
}

// ══════════ 凭据存储（系统原生凭据管理器）═══════════
// 服务标识：固定字符串，用于在系统凭据库中区分本应用的条目
static void CredSet(string key, string value)
{
    var store = CredentialStoreFactory.CreateDefault("hotsoupreader");
    var cache = new ktsu.CredentialCache.CredentialCache(store);
    cache.AddOrReplace(new PersonaGUID { WeakString = key }, new CredentialWithToken { Token = new CredentialToken { WeakString = value } });
}

static string? CredGet(string key)
{
    try
    {
        var store = CredentialStoreFactory.CreateDefault("hotsoupreader");
        var cache = new ktsu.CredentialCache.CredentialCache(store);
        if (cache.TryGet(new PersonaGUID { WeakString = key }, out var cred) && cred is CredentialWithToken ct)
            return ct.Token.WeakString;
    }
    catch { /* 凭据库不可用时返回 null */ }
    return null;
}

static bool CredHas(string key) => CredGet(key) != null;

// ══════════ 安全提醒（首次调用 AI 功能时输出）═══════════
// 传了 --ignoresafeannouncement 则不输出（供脚本/AI Agent 使用，避免多余内容）
static void EnsureAiPrompted()
{
    if (AiState.Warned) return;
    AiState.Warned = true;
    if (AiState.IgnoreAnnouncement) return;
    Console.WriteLine(Lang.T("════════════════════════════════════════════════════"));
    Console.WriteLine(Lang.T("🔐 Security notice"));
    Console.WriteLine(Lang.T("Your API key is stored in the OS-native credential store"));
    Console.WriteLine(Lang.T("(Windows Credential Manager / macOS Keychain / Linux Secret Service)"));
    Console.WriteLine(Lang.T("It is never written to any project file. Please keep it secret:"));
    Console.WriteLine(Lang.T("1. Never share/send your API key to anyone"));
    Console.WriteLine(Lang.T("2. Never screenshot or upload screens with the key"));
    Console.WriteLine(Lang.T("3. Rotate your key immediately if you suspect a leak"));
    Console.WriteLine(Lang.T("════════════════════════════════════════════════════"));
}

// ══════════ JSON 输出辅助 ══════════
static void JsonOut(object obj) => Console.WriteLine(JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));

// 退出码分类（脚本/AI 用 exit code 判断成败）：
//   0=成功  1=通用错误（参数/用法/数据库）  2=网络/服务不可达  3=资源未就绪（AI 未配置/密钥缺失/无索引/找不到）
static int ExitCodeFor(string code) => code switch
{
    "NETWORK_ERROR" or "MODEL_UNAVAILABLE" => 2,
    "API_KEY_MISSING" or "API_KEY_INVALID" or "NO_INDEX"
        or "FEED_NOT_FOUND" or "ITEM_NOT_FOUND" or "EMPTY_QUERY"
        or "EVIDENCE_NOT_FOUND" or "EMPTY_STDIN" => 3,
    _ => 1,
};

// 设置退出码（取最严重的：同一次调用里若有多次失败不会被较低严重度的覆盖）
static void SetExit(int code = 1) => AiState.ExitCode = Math.Max(AiState.ExitCode, code);

// 自然语言报错 + JSON 双格式
static void ReportError(string code, string message, string? suggestion = null, string? details = null, bool json = false)
{
    SetExit(ExitCodeFor(code));
    if (json)
    {
        JsonOut(new { success = false, error = new { code, message, suggestion, details } });
    }
    else
    {
        Console.WriteLine(Lang.T("Error [{0}] {1}", code, message));
        if (suggestion != null) Console.WriteLine(Lang.T("Suggestion: {0}", suggestion));
        if (details != null) Console.WriteLine(Lang.T("Details: {0}", details));
    }
}

// ══════════ Embedding 服务（OpenAI 兼容格式，端点可自定义）═══════════
// 统一走 POST {endpoint}/embeddings：Ollama(/v1)、DeepSeek、OpenAI 及任何
// 兼容服务均可；API Key 可选（本地 Ollama 不需要，填了才带 Bearer 头）
static async Task<float[]?> GetEmbeddingAsync(string text, AiConfig cfg, int? articleId = null, int? sourceId = null)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    bool ok = false;
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        string? key = CredGet("embedding_api_key");
        if (!string.IsNullOrEmpty(key))
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        var body = new { model = cfg.Embedding.Model, input = text };
        var resp = await client.PostAsync($"{cfg.Embedding.ApiEndpoint}/embeddings",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        if (!resp.IsSuccessStatusCode)
            throw new AiException("MODEL_UNAVAILABLE", Lang.T("Embedding request failed (HTTP {0})", (int)resp.StatusCode),
                Lang.T("Verify the endpoint/port/model name; with Ollama run ollama list / ollama pull first"), await resp.Content.ReadAsStringAsync());
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var data = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
            ok = true;
            return data.EnumerateArray().Select(x => x.GetSingle()).ToArray();
        }
        catch (JsonException)
        {
            // 返回的不是 JSON（比如端点缺少 /v1 返回的 HTML），给出友好提示而非崩溃
            throw new AiException("INVALID_RESPONSE", Lang.T("Embedding service did not return JSON"),
                Lang.T("Check whether the endpoint is missing /v1 (correct form http://host:port/v1)"));
        }
    }
    finally
    {
        // 遥测：记录 ai_call（不记 prompt/响应内容）；带文章/源归属
        TelemetryService.RecordAiCall("embedding", cfg.Embedding.Provider, cfg.Embedding.Model, ok, sw.ElapsedMilliseconds, articleId, sourceId);
    }
}

// 模型调用失败时：捕获并自然语言报错，停止使用该模型
static async Task<float[]?> SafeEmbed(string text, AiConfig cfg, bool json = false, int? articleId = null, int? sourceId = null)
{
    try
    {
        EnsureAiPrompted();
        return await GetEmbeddingAsync(text, cfg, articleId, sourceId);
    }
    catch (HttpRequestException ex)
    {
        ReportError("NETWORK_ERROR", Lang.T("Network error, cannot reach the Embedding service"),
            Lang.T("Check your network connection or the API endpoint"), ex.Message, json);
        return null;
    }
    catch (AiException ex)
    {
        ReportError(ex.Code, ex.Message, ex.Suggestion, ex.Details, json);
        return null;
    }
}

// ══════════ 向量存储与相似度 ══════════
static byte[] VectorToBytes(float[] v)
{
    var bytes = new byte[v.Length * sizeof(float)];
    Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
    return bytes;
}

static float[] BytesToVector(byte[] bytes)
{
    var v = new float[bytes.Length / sizeof(float)];
    Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
    return v;
}

static float CosineSimilarity(float[] a, float[] b)
{
    if (a.Length != b.Length) return 0f;
    float dot = 0, na = 0, nb = 0;
    for (int i = 0; i < a.Length; i++)
    {
        dot += a[i] * b[i];
        na += a[i] * a[i];
        nb += b[i] * b[i];
    }
    return dot / (MathF.Sqrt(na) * MathF.Sqrt(nb) + 1e-12f);
}

// 注册/获取当前 embedding 模型，返回 Models.Id；维度变化时更新 IsCurrent
static int EnsureModel(string dbPath, EmbeddingCfg emb)
{
    using var conn = OpenDb(dbPath);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id FROM Models WHERE Provider = @p AND ModelName = @m AND ModelType = 'embedding'";
    cmd.Parameters.AddWithValue("@p", emb.Provider);
    cmd.Parameters.AddWithValue("@m", emb.Model);
    var id = cmd.ExecuteScalar();
    if (id != null)
    {
        int modelId = Convert.ToInt32(id);
        // 确保是当前模型
        cmd.CommandText = "UPDATE Models SET IsCurrent = CASE WHEN Id = @id THEN 1 ELSE 0 END WHERE ModelType = 'embedding'";
        cmd.Parameters.AddWithValue("@id", modelId);
        cmd.ExecuteNonQuery();
        return modelId;
    }
    // 新模型：把旧模型取消 IsCurrent
    cmd.CommandText = "UPDATE Models SET IsCurrent = 0 WHERE ModelType = 'embedding'";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "INSERT INTO Models (ModelType, Provider, ModelName, Dimensions, IsCurrent, CreatedAt) VALUES ('embedding', @p, @m, @d, 1, @now)";
    cmd.Parameters.AddWithValue("@d", emb.Dimensions);
    cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
    cmd.ExecuteNonQuery();
    cmd.CommandText = "SELECT last_insert_rowid()";
    return Convert.ToInt32(cmd.ExecuteScalar());
}

// 检查是否需要重新索引（模型维度变化时提醒）
static string? CheckDimensionMismatch(string dbPath, EmbeddingCfg emb)
{
    using var conn = OpenDb(dbPath);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT ModelName, Dimensions FROM Models WHERE IsCurrent = 1 AND ModelType = 'embedding'";
    using var r = cmd.ExecuteReader();
    if (r.Read())
    {
        string oldName = r.GetString(0);
        int oldDim = r.IsDBNull(1) ? 0 : r.GetInt32(1);
        if (oldName != emb.Model && oldDim != emb.Dimensions)
            return Lang.T("Embedding model dimensions changed (old {0} {1}D → new {2} {3}D), old vectors are unusable, run --reindex to re-embed",
                oldName, oldDim, emb.Model, emb.Dimensions);
    }
    return null;
}

// 保存向量（幂等：同文章 + 同模型只留一条）
static void SaveVector(string dbPath, int feedId, int itemId, int modelId, float[] vector)
{
    using var conn = OpenDb(dbPath);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        INSERT INTO Vectors (FeedId, ItemId, ModelId, Vector, CreatedAt)
        VALUES (@f, @i, @m, @v, @now)
        ON CONFLICT(ItemId, ModelId) DO UPDATE SET FeedId = excluded.FeedId, Vector = excluded.Vector, CreatedAt = excluded.CreatedAt
    ";
    cmd.Parameters.AddWithValue("@f", feedId);
    cmd.Parameters.AddWithValue("@i", itemId);
    cmd.Parameters.AddWithValue("@m", modelId);
    cmd.Parameters.AddWithValue("@v", VectorToBytes(vector));
    cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
    cmd.ExecuteNonQuery();
}

// ══════════ 交互式选择文章进行向量化 ══════════
static async Task IndexArticlesCli(string[] extraArgs, string dbPath)
{
    EnsureAiPrompted();
    var cfg = LoadConfig(dbPath);

    // 默认全选模式；也可支持 --all
    ListFeedsFromDb(dbPath);
    Console.WriteLine();
    Console.Write(Lang.T("Enter feed numbers to embed (comma-separated, \"all\" for all): "));
    string input = Console.ReadLine()?.Trim() ?? "";

    using var conn = OpenDb(dbPath);
    conn.Open();
    var feedIds = new List<int>();
    if (input.Equals("all", StringComparison.OrdinalIgnoreCase))
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id FROM Feeds";
        using var r = cmd.ExecuteReader();
        while (r.Read()) feedIds.Add(r.GetInt32(0));
    }
    else
    {
        foreach (var part in input.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out int disp))
            {
                int real = GetRealId(disp, dbPath);
                if (real != 0) feedIds.Add(real);
            }
        }
    }

    if (feedIds.Count == 0) { Console.WriteLine(Lang.T("No feed selected, cancelled")); return; }

    // 收集未向量化的 active 文章
    var articles = new List<(int Id, int FeedId, string Title)>();
    var cmd2 = conn.CreateCommand();
    cmd2.CommandText = @"
        SELECT i.Id, i.FeedId, i.Title FROM Items i
        WHERE i.Status = 'active' AND i.FeedId IN (" + string.Join(",", feedIds) + @")
        AND NOT EXISTS (SELECT 1 FROM Vectors v WHERE v.ItemId = i.Id)
    ";
    using var r2 = cmd2.ExecuteReader();
    while (r2.Read()) articles.Add((r2.GetInt32(0), r2.GetInt32(1), r2.GetString(2)));

    if (articles.Count == 0) { Console.WriteLine(Lang.T("All articles of the selected feeds are already embedded")); return; }

    Console.WriteLine(Lang.T("Will embed {0} articles, confirm? (y/n)", articles.Count));
    if (Console.ReadLine()?.ToLower() != "y") { Console.WriteLine(Lang.T("Cancelled")); return; }

    int modelId = EnsureModel(dbPath, cfg.Embedding);
    int ok = 0, fail = 0;
    for (int i = 0; i < articles.Count; i++)
    {
        var a = articles[i];
        var vec = await SafeEmbed(a.Title, cfg, articleId: a.Id, sourceId: a.FeedId);
        if (vec == null) { fail++; Console.WriteLine(Lang.T("  [{0}/{1}] failed: {2}", i + 1, articles.Count, a.Title)); continue; }
        if (vec.Length != cfg.Embedding.Dimensions)
        {
            // 自动校正维度（以实际为准）
            cfg.Embedding.Dimensions = vec.Length;
            SaveConfig(dbPath, cfg);
        }
        SaveVector(dbPath, a.FeedId, a.Id, modelId, vec);
        ok++;
        if (ok % 10 == 0) Console.WriteLine(Lang.T("  processed {0}/{1}", ok + fail, articles.Count));
    }
    Console.WriteLine(Lang.T("Done: {0} OK, {1} failed", ok, fail));

    // 回补全文 sidecar：已抓全文但当时未索引的文章补向量（修复时序缺陷）
    int backfilled = BackfillFulltextSidecars(dbPath, articles.Select(a => (a.Id, a.FeedId)).ToList());
    if (backfilled > 0) Console.WriteLine(Lang.T("Fulltext sidecar vectors backfilled: {0}", backfilled));
}

// 重新向量化（更换模型后）：清空旧向量并重来
static async Task ReindexCli(string dbPath)
{
    EnsureAiPrompted();
    var cfg = LoadConfig(dbPath);
    using var conn = OpenDb(dbPath);
    conn.Open();

    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM Items WHERE Status = 'active'";
    long total = (long)cmd.ExecuteScalar()!;

    Console.Write(Lang.T("Will delete existing vectors and re-embed all {0} active articles, confirm? (y/n)", total));
    if (Console.ReadLine()?.ToLower() != "y") { Console.WriteLine(Lang.T("Cancelled")); return; }

    cmd.CommandText = "DELETE FROM Vectors";
    cmd.ExecuteNonQuery();
    // 换模型后旧 sidecar 向量（抓取全文的）同样失效，一并清空
    if (File.Exists(FulltextVecsPath())) { try { File.Delete(FulltextVecsPath()); } catch { } }

    int modelId = EnsureModel(dbPath, cfg.Embedding);
    cmd.CommandText = "SELECT Id, FeedId, Title FROM Items WHERE Status = 'active'";
    using var r = cmd.ExecuteReader();
    var items = new List<(int Id, int FeedId, string Title)>();
    while (r.Read()) items.Add((r.GetInt32(0), r.GetInt32(1), r.GetString(2)));
    r.Close();

    int ok = 0, fail = 0;
    foreach (var item in items)
    {
        var vec = await SafeEmbed(item.Title, cfg, articleId: item.Id, sourceId: item.FeedId);
        if (vec == null) { fail++; continue; }
        SaveVector(dbPath, item.FeedId, item.Id, modelId, vec);
        ok++;
        if ((ok + fail) % 10 == 0) Console.WriteLine(Lang.T("  processed {0}/{1}", ok + fail, items.Count));
    }
    Console.WriteLine(Lang.T("Re-indexing done: {0} OK, {1} failed", ok, fail));

    // 换模型后旧全文 sidecar 已清空，给有全文缓存的文章重算
    int backfilled = BackfillFulltextSidecars(dbPath, items.Select(a => (a.Id, a.FeedId)).ToList());
    if (backfilled > 0) Console.WriteLine(Lang.T("Fulltext sidecar vectors backfilled: {0}", backfilled));
}

// ══════════ 语义搜索 ══════════
static void SearchCli(string[] args, string dbPath)
{
    EnsureAiPrompted();
    var cfg = LoadConfig(dbPath);

    int? feedDisplay = null;
    int? feedReal = null;
    float threshold = cfg.Embedding.SearchThreshold;
    bool json = false;
    var queryParts = new List<string>();

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--feed":
                if (i + 1 < args.Length && int.TryParse(args[++i], out int f))
                {
                    feedDisplay = f;
                    feedReal = GetRealId(f, dbPath);
                    if (feedReal == 0) { ReportError("FEED_NOT_FOUND", Lang.T("Feed number {0} not found", f), json: json); return; }
                }
                break;
            case "--threshold":
                if (i + 1 < args.Length && float.TryParse(args[++i], out float t))
                    threshold = t;
                break;
            case "--json":
                json = true;
                break;
            default:
                queryParts.Add(args[i]);
                break;
        }
    }

    string query = string.Join(" ", queryParts);
    if (string.IsNullOrWhiteSpace(query)) { ReportError("EMPTY_QUERY", Lang.T("Please enter a search query"), json: json); return; }

    var results = DoSearch(query, dbPath, feedReal, threshold, json);
    if (results == null) return;
    TelemetryService.Record("search", sourceId: feedReal, data: new { mode = "semantic", query, hits = results.Count, threshold });

    if (json)
    {
        JsonOut(new
        {
            success = true,
            data = new
            {
                query,
                threshold,
                feedId = feedReal,
                results = results.Select(h =>
                {
                    var sig = GetSignal(h.ItemId);
                    return new
                    {
                        itemId = h.ItemId,
                        title = h.Title,
                        description = h.Description,
                        link = h.Link,
                        feedId = h.FeedId,
                        feedTitle = h.FeedTitle,
                        score = Math.Round(h.Score, 4),
                        quality = ContentQuality(h.Content, h.Description),
                        liked = sig?.UserLike ?? false,
                        aiLiked = sig?.AiLike ?? false
                    };
                }),
                total = results.Count
            }
        });
    }
    else
    {
        Console.WriteLine(Lang.T("Search results (query: {0}, threshold: {1}, total {2})", query, threshold, results.Count));
        foreach (var h in results)
        {
            Console.WriteLine($"  [{h.ItemId}] {StripControlChars(h.Title)}");
            Console.WriteLine(Lang.T("      source: {0} | {1}", StripControlChars(h.FeedTitle), h.Link));
            if (!string.IsNullOrEmpty(h.Description) && h.Description.Length > 80)
                Console.WriteLine(Lang.T("      summary: {0}...", StripControlChars(h.Description[..80])));
        }
    }
}

// 全文搜索 CLI：在标题/正文/摘要里做关键字匹配（类似 VS Code 全文搜索，不依赖 AI）
// 默认「片段模式」：每篇只出 编号 + 标题 + 出现次数 + 上下 50 字符的片段，输出有上限、不会爆上下文；
// --full 恢复旧模式（整篇摘要），--json 结构化输出
// --context <N>：自定义上下文字符数（默认 50）
static void GrepCli(string[] args, string dbPath)
{
    var flags = args.Skip(1).ToArray();
    bool json = flags.Contains("--json", StringComparer.OrdinalIgnoreCase);
    bool full = flags.Contains("--full", StringComparer.OrdinalIgnoreCase);
    int limit = 20, maxSnippets = 10, context = 50;
    int? feedReal = null;
    for (int i = 0; i < flags.Length; i++)
    {
        if (flags[i].Equals("--limit", StringComparison.OrdinalIgnoreCase) && i + 1 < flags.Length && int.TryParse(flags[i + 1], out int l))
            limit = Math.Max(1, l);
        if (flags[i].Equals("--max-snippets", StringComparison.OrdinalIgnoreCase) && i + 1 < flags.Length && int.TryParse(flags[i + 1], out int ms))
            maxSnippets = Math.Max(1, ms);
        if (flags[i].Equals("--context", StringComparison.OrdinalIgnoreCase) && i + 1 < flags.Length && int.TryParse(flags[i + 1], out int ctx))
            context = Math.Max(0, ctx);
        // --feed N：限定单个源内搜索（与 --search --feed 同规则，N 为显示序号）
        if (flags[i].Equals("--feed", StringComparison.OrdinalIgnoreCase) && i + 1 < flags.Length && int.TryParse(flags[i + 1], out int fn))
        {
            feedReal = GetRealId(fn, dbPath);
            if (feedReal == 0) { ReportError("FEED_NOT_FOUND", Lang.T("Feed number not found"), json: json); return; }
        }
    }
    string keyword = args[0];

    var hits = DoGrep(keyword, dbPath, limit, feedReal);
    if (hits == null) return;
    TelemetryService.Record("search", data: new { mode = "grep", query = keyword, hits = hits.Count });

    // --full：整篇摘要直出（旧行为，显式 opt-in）
    if (full)
    {
        if (json)
        {
            JsonOut(new
            {
                success = true,
                data = new
                {
                    query = keyword,
                    results = hits.Select(h => new
                    {
                        itemId = h.ItemId,
                        title = h.Title,
                        description = h.Description,
                        link = h.Link,
                        feedTitle = h.FeedTitle
                    }),
                    total = hits.Count
                }
            });
        }
        else
        {
            Console.WriteLine(Lang.T("Full-text search \"{0}\": {1} hits", keyword, hits.Count));
            foreach (var h in hits)
            {
                Console.WriteLine($"  [{h.ItemId}] {StripControlChars(h.Title)}");
                Console.WriteLine(Lang.T("      source: {0} | {1}", StripControlChars(h.FeedTitle), h.Link));
                if (!string.IsNullOrEmpty(h.Description))
                    Console.WriteLine(Lang.T("      summary: {0}", StripControlChars(h.Description)));
            }
        }
        return;
    }

    // 片段模式：每篇统计出现次数 + 取前 maxSnippets 个 ±context 字符片段
    var items = new List<GrepSnippetResult>();
    foreach (var h in hits)
    {
        string haystack = h.Title + "\n" + StripHtml(string.IsNullOrWhiteSpace(h.Content) ? h.Description : h.Content)
                          + (string.IsNullOrWhiteSpace(h.Summary) ? "" : "\n" + h.Summary);
        var (snippets, total, lineNumbers) = ExtractGrepSnippets(haystack, keyword, radius: context, max: maxSnippets);
        items.Add(new GrepSnippetResult
        {
            ItemId = h.ItemId, Title = h.Title, Link = h.Link, FeedTitle = h.FeedTitle,
            Count = total, Snippets = snippets, LineNumbers = lineNumbers, TotalSnippets = total,
            Quality = ContentQuality(h.Content, h.Description)
        });
    }

    if (json)
    {
        JsonOut(new
        {
            success = true,
            data = new
            {
                query = keyword,
                results = items.Select(r => new
                {
                    itemId = r.ItemId,
                    title = r.Title,
                    count = r.Count,
                    totalSnippets = r.TotalSnippets,
                    snippets = r.Snippets.Select((s, i) => new
                    {
                        text = s,
                        line = i < r.LineNumbers.Count ? r.LineNumbers[i] : 0
                    }),
                    link = r.Link,
                    feedTitle = r.FeedTitle,
                    quality = r.Quality
                }),
                total = items.Count
            }
        });
        return;
    }

    Console.WriteLine(Lang.T("Full-text search \"{0}\": {1} hits", keyword, items.Count));
    foreach (var r in items)
    {
        string note = r.Count == 0 ? "  " + Lang.T("(仅命中链接/属性，未计入可见文本)") : "";
        Console.WriteLine($"  [{r.ItemId}] {StripControlChars(r.Title)} ({Lang.T("{0} occurrences", r.Count)}){note}");
        for (int i = 0; i < r.Snippets.Count; i++)
        {
            int lineNum = i < r.LineNumbers.Count ? r.LineNumbers[i] : 0;
            Console.WriteLine($"    {i + 1}. L{lineNum}: {StripControlChars(r.Snippets[i])}");
        }
        if (r.TotalSnippets > r.Snippets.Count)
            Console.WriteLine(Lang.T("    …({0} more, view full text with sip --show {1})", r.TotalSnippets - r.Snippets.Count, r.ItemId));
    }
}

// 在纯文本 haystack 里大小写不敏感地找出 keyword 的所有出现位置，
// 每个位置取 [i-radius, i+radius+len] 的窗口；相邻窗口重叠时合并；
// 只保留前 max 段（超出返回 total 让调用方知道还有多少）
// 返回 (snippets, total, lineNumbers)：lineNumbers[i] = snippets[i] 所在行号
static (List<string> Snippets, int Total, List<int> LineNumbers) ExtractGrepSnippets(string haystack, string keyword, int radius, int max)
{
    if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(keyword)) return (new List<string>(), 0, new List<int>());

    int kwLen = keyword.Length;
    var ranges = new List<(int Start, int End)>();
    var matchPositions = new List<int>(); // 每个匹配的起始位置
    int from = 0, total = 0;
    while (true)
    {
        int idx = haystack.IndexOf(keyword, from, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) break;
        total++;
        matchPositions.Add(idx);

        int start = Math.Max(0, idx - radius);
        int end = Math.Min(haystack.Length, idx + kwLen + radius);
        // 与上一个窗口重叠 → 扩展合并，避免重复文本
        if (ranges.Count > 0 && start <= ranges[^1].End)
        {
            var last = ranges[^1];
            ranges[^1] = (last.Start, Math.Max(last.End, end));
        }
        else if (ranges.Count < max)
        {
            ranges.Add((start, end));
        }
        // 超过 max 后不再新增窗口，但继续统计总出现次数（total 是真实的全部次数）
        from = idx + kwLen;
    }

    // 计算每个片段所在行号（以第一个匹配位置为准）
    var lineNumbers = new List<int>();
    foreach (var r in ranges)
    {
        // 找到这个范围内第一个匹配位置
        int firstMatch = matchPositions.FirstOrDefault(p => p >= r.Start && p <= r.End, r.Start);
        int lineNum = CountLines(haystack, firstMatch) + 1;
        lineNumbers.Add(lineNum);
    }

    var snippets = ranges.Select(r => haystack[r.Start..r.End]).ToList();
    return (snippets, total, lineNumbers);
}

// 计算文本中某个位置之前有多少换行符（即行号，0-based）
static int CountLines(string text, int position)
{
    int lines = 0;
    for (int i = 0; i < position && i < text.Length; i++)
        if (text[i] == '\n') lines++;
    return lines;
}

// 全文搜索核心逻辑（CLI 与 TUI 共用）：SQL LIKE 匹配标题/正文/摘要
// limit：命中数上限（TUI 传默认 200；CLI 默认 --limit 20）
static List<GrepHit>? DoGrep(string keyword, string dbPath, int limit = 200, int? feedReal = null)
{
    if (string.IsNullOrWhiteSpace(keyword)) { SetExit(); Console.WriteLine(Lang.T("Enter a search keyword")); return null; }
    using var conn = OpenDb(dbPath);
    conn.Open();

    // FTS5 优先(百万级从秒级降到毫秒级);索引缺失/查询失败/短词(trigram 需 ≥3 字符)时回退 LIKE
    EnsureFtsIndexed(dbPath);
    var hits = FtsSuitable(keyword)
        ? (TryGrepFts(conn, keyword, limit, feedReal) ?? TryGrepLike(conn, keyword, limit, feedReal))
        : TryGrepLike(conn, keyword, limit, feedReal);
    if (hits == null) { SetExit(); Console.WriteLine(Lang.T("Enter a search keyword")); return null; }

    // Description 可能是 HTML，转纯文本便于阅读
    for (int i = 0; i < hits.Count; i++)
        hits[i] = new GrepHit { ItemId = hits[i].ItemId, Title = hits[i].Title, Description = StripHtml(hits[i].Description), Content = hits[i].Content, Summary = hits[i].Summary, Link = hits[i].Link, FeedTitle = hits[i].FeedTitle };
    return hits;
}

// trigram tokenizer 只支持 ≥3 字符的查询;按最长连续字母数字段判断是否可用 FTS。
// (短词如"熊猫"走 LIKE 全表扫,百万级约 2s;可接受,后续可加 2-gram 辅助列消除)
static bool FtsSuitable(string keyword)
{
    int best = 0, cur = 0;
    foreach (char c in keyword)
    {
        if (char.IsLetterOrDigit(c)) cur++;
        else { if (cur > best) best = cur; cur = 0; }
    }
    if (cur > best) best = cur;
    return best >= 3;
}

// FTS5 检索:关键词整体作为短语查询(引号包裹,内部引号翻倍),
// 语义与 LIKE '%kw%' 一致(任意位置子串);失败返回 null 由调用方回退 LIKE
static List<GrepHit>? TryGrepFts(SqliteConnection conn, string keyword, int limit, int? feedReal)
{
    try
    {
        string phrase = "\"" + keyword.Replace("\"", "\"\"") + "\"";
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT i.Id, i.Title, i.Description, i.Content, i.Summary, i.Link, f.Title AS FeedTitle
            FROM ItemsFts fts
            JOIN Items i ON i.Id = fts.rowid
            JOIN Feeds f ON i.FeedId = f.Id
            WHERE ItemsFts MATCH @m
              AND i.Status = 'active'
              " + (feedReal.HasValue ? "AND i.FeedId = @fid" : "") + @"
            LIMIT @limit
        ";
        // 注意:不能加 ORDER BY i.Id —— fts5 默认按 rowid(=Items.Id)升序返回,
        // 加 ORDER BY 会破坏 LIMIT 的流式提前终止,导致全量命中 JOIN(百万级冷缓存 90s+)。
        cmd.Parameters.AddWithValue("@m", phrase);
        cmd.Parameters.AddWithValue("@limit", Math.Max(1, limit));
        if (feedReal.HasValue) cmd.Parameters.AddWithValue("@fid", feedReal.Value);
        var hits = new List<GrepHit>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                hits.Add(new GrepHit
                {
                    ItemId = r.GetInt32(0),
                    Title = r.GetString(1),
                    Description = r.IsDBNull(2) ? "" : r.GetString(2),
                    Content = r.IsDBNull(3) ? "" : r.GetString(3),
                    Summary = r.IsDBNull(4) ? "" : r.GetString(4),
                    Link = r.IsDBNull(5) ? "" : r.GetString(5),
                    FeedTitle = r.GetString(6)
                });
            }
        }
        return hits;
    }
    catch { return null; }   // MATCH 语法异常/旧 SQLite 无 trigram → 回退 LIKE
}

// LIKE 全表扫描回退(与旧行为完全一致;短词 <3 字符时 trigram 无法用索引,也走这里)
static List<GrepHit>? TryGrepLike(SqliteConnection conn, string keyword, int limit, int? feedReal)
{
    try
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT i.Id, i.Title, i.Description, i.Content, i.Summary, i.Link, f.Title AS FeedTitle
            FROM Items i
            JOIN Feeds f ON i.FeedId = f.Id
            WHERE i.Status = 'active'
              " + (feedReal.HasValue ? "AND i.FeedId = @fid" : "") + @"
              AND (i.Title LIKE @kw ESCAPE '\' OR i.Content LIKE @kw ESCAPE '\' OR i.Description LIKE @kw ESCAPE '\' OR i.Summary LIKE @kw ESCAPE '\')
            ORDER BY i.Id
            LIMIT @limit
        ";
        // 转义 LIKE 通配符（% _ \），让关键词按字面匹配而非被当作通配符
        string escaped = keyword.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        cmd.Parameters.AddWithValue("@kw", "%" + escaped + "%");
        cmd.Parameters.AddWithValue("@limit", Math.Max(1, limit));
        if (feedReal.HasValue) cmd.Parameters.AddWithValue("@fid", feedReal.Value);
        var hits = new List<GrepHit>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                hits.Add(new GrepHit
                {
                    ItemId = r.GetInt32(0),
                    Title = r.GetString(1),
                    Description = r.IsDBNull(2) ? "" : r.GetString(2),
                    Content = r.IsDBNull(3) ? "" : r.GetString(3),
                    Summary = r.IsDBNull(4) ? "" : r.GetString(4),
                    Link = r.IsDBNull(5) ? "" : r.GetString(5),
                    FeedTitle = r.GetString(6)
                });
            }
        }
        return hits;
    }
    catch { return null; }
}

// 语义搜索核心逻辑（CLI 与 TUI 共用）；失败返回 null
static List<SearchHit>? DoSearch(string query, string dbPath, int? feedReal = null, float? threshold = null, bool json = false)
{
    var cfg = LoadConfig(dbPath);
    float thr = threshold ?? cfg.Embedding.SearchThreshold;

    var vec = SafeEmbed(query, cfg, json, sourceId: feedReal).GetAwaiter().GetResult();
    if (vec == null) return null;

    using var conn = OpenDb(dbPath);
    conn.Open();
    var modelCmd = conn.CreateCommand();
    modelCmd.CommandText = "SELECT Id FROM Models WHERE IsCurrent = 1 AND ModelType = 'embedding'";
    var modelObj = modelCmd.ExecuteScalar();
    if (modelObj == null) { ReportError("NO_INDEX", Lang.T("No vector index yet, run sip --index first"), json: json); return null; }
    int modelId = Convert.ToInt32(modelObj);

    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM Vectors WHERE ModelId = @m";
    cmd.Parameters.AddWithValue("@m", modelId);
    long count = (long)cmd.ExecuteScalar()!;
    if (count == 0) { ReportError("NO_INDEX", Lang.T("The current model has no vectors yet, run sip --index first"), json: json); return null; }

    cmd.Parameters.Clear();
    cmd.CommandText = @"
        SELECT v.ItemId, v.Vector, i.Title, i.Description, i.Content, i.Link,
               f.Title AS FeedTitle, f.Id AS FeedId
        FROM Vectors v
        JOIN Items i ON v.ItemId = i.Id
        JOIN Feeds f ON i.FeedId = f.Id
        WHERE v.ModelId = @m AND i.Status = 'active'
        " + (feedReal.HasValue ? "AND i.FeedId = @fid" : "") + @"
        ORDER BY i.Id
    ";
    cmd.Parameters.AddWithValue("@m", modelId);
    if (feedReal.HasValue) cmd.Parameters.AddWithValue("@fid", feedReal.Value);

    var results = new List<SearchHit>();
    using (var r = cmd.ExecuteReader())
    {
        while (r.Read())
        {
            float[] stored = BytesToVector(r.GetFieldValue<byte[]>(1));
            float score = CosineSimilarity(vec, stored);
            if (score < thr) continue;
            results.Add(new SearchHit
            {
                ItemId = r.GetInt32(0),
                Title = r.GetString(2),
                Description = r.IsDBNull(3) ? "" : r.GetString(3),
                Content = r.IsDBNull(4) ? "" : r.GetString(4),
                Link = r.IsDBNull(5) ? "" : r.GetString(5),
                FeedTitle = r.GetString(6),
                FeedId = r.GetInt32(7),
                Score = score
            });
        }
    }

    // 合并 sidecar（抓取全文向量）：只补主表没有的 itemId
    var seen = new HashSet<int>(results.Select(h => h.ItemId));
    foreach (var (sid, sfeed, smodel, svec) in LoadFulltextVecs())
    {
        if (smodel != modelId || seen.Contains(sid)) continue;
        if (feedReal.HasValue && sfeed != feedReal.Value) continue;
        float score = CosineSimilarity(vec, svec);
        if (score < thr) continue;
        var hit = GetSearchHitForItem(dbPath, sid, score);
        if (hit != null) { results.Add(hit); seen.Add(sid); }
    }

    return results.OrderByDescending(h => h.Score).Take(20).ToList();
}

// 按 itemId 取搜索结果条目（sidecar 向量命中用）
static SearchHit? GetSearchHitForItem(string dbPath, int itemId, float score)
{
    try
    {
        using var conn = OpenDb(dbPath);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT i.Title, i.Description, i.Content, i.Link, i.FeedId, f.Title FROM Items i LEFT JOIN Feeds f ON i.FeedId = f.Id WHERE i.Id = @id AND i.Status = 'active'";
        cmd.Parameters.AddWithValue("@id", itemId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new SearchHit
        {
            ItemId = itemId,
            Title = r.GetString(0),
            Description = r.IsDBNull(1) ? "" : r.GetString(1),
            Content = r.IsDBNull(2) ? "" : r.GetString(2),
            Link = r.IsDBNull(3) ? "" : r.GetString(3),
            FeedId = r.GetInt32(4),
            FeedTitle = r.IsDBNull(5) ? "" : r.GetString(5),
            Score = score
        };
    }
    catch { return null; }
}

// 按真实 Id 更新单个源（TUI 用）
static void RefreshOneFeed(int realId, string dbPath)
{
    using var conn = OpenDb(dbPath);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT FeedUrl FROM Feeds WHERE Id = @id";
    cmd.Parameters.AddWithValue("@id", realId);
    string? url = cmd.ExecuteScalar()?.ToString();
    if (string.IsNullOrWhiteSpace(url)) return;
    try { DownloadAndSaveToDb(url, dbPath).Wait(); }
    catch { }
}

// 按 Guid 删除整篇文章（含全部历史版本与向量）
static void DeleteArticleByGuid(string guid, string dbPath)
{
    // 先取该 Guid 全部 Id（清理全文缓存与 sidecar 向量）
    var ids = new List<int>();
    using (var q = OpenDb(dbPath))
    {
        q.Open();
        var c = q.CreateCommand();
        c.CommandText = "SELECT Id FROM Items WHERE Guid = @g";
        c.Parameters.AddWithValue("@g", guid);
        using var r = c.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt32(0));
    }
    foreach (var id in ids)
    {
        string p = FulltextPath(id);
        if (File.Exists(p)) { try { File.Delete(p); } catch { } }
    }
    RemoveFulltextVecs(ids);

    using var conn = OpenDb(dbPath);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM Vectors WHERE ItemId IN (SELECT Id FROM Items WHERE Guid = @g)";
    cmd.Parameters.AddWithValue("@g", guid);
    cmd.ExecuteNonQuery();
    cmd.CommandText = "DELETE FROM ItemsFts WHERE rowid IN (SELECT Id FROM Items WHERE Guid = @g)";
    cmd.ExecuteNonQuery();
    cmd.CommandText = "DELETE FROM Items WHERE Guid = @g";
    cmd.Parameters.AddWithValue("@g", guid);
    cmd.ExecuteNonQuery();
}

// （SearchHit 类见文件末尾类型区）
// ══════════ LLM 摘要服务（OpenAI 兼容，端点可自定义）═══════════
static async Task<string?> CallLlmAsync(string prompt, AiConfig cfg, int? articleId = null, int? sourceId = null)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    bool ok = false;
    try
    {
        string? key = CredGet("llm_api_key");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        if (!string.IsNullOrEmpty(key))
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        var body = new
        {
            model = cfg.Llm.Model,
            messages = new[] { new { role = "user", content = prompt } },
            temperature = 0.3
        };
        var resp = await client.PostAsync($"{cfg.Llm.ApiEndpoint}/chat/completions",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        if (!resp.IsSuccessStatusCode)
            throw new AiException("API_KEY_INVALID", Lang.T("LLM request failed (HTTP {0})", (int)resp.StatusCode),
                Lang.T("Check the API key / model name / endpoint config"), await resp.Content.ReadAsStringAsync());
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            ok = true;
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        }
        catch (JsonException)
        {
            throw new AiException("INVALID_JSON", Lang.T("LLM service did not return JSON"),
                Lang.T("Check whether the endpoint is missing /v1 (e.g. https://api.deepseek.com/v1)"));
        }
    }
    finally
    {
        // 遥测：记录 ai_call（不记 prompt/响应内容）；带文章/源归属（供报告按源统计）
        TelemetryService.RecordAiCall("llm", cfg.Llm.Provider, cfg.Llm.Model, ok, sw.ElapsedMilliseconds, articleId, sourceId);
    }
}

// 生成单篇文章摘要并保存到 rss.db（与文章同在库中）
static async Task<(bool Ok, string? Summary)> SummarizeItem(string dbPath, int itemId, bool json = false, bool quiet = false)
{
    var cfg = LoadConfig(dbPath);
    using var conn = OpenDb(dbPath);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Title, Content, Description, Summary, FeedId FROM Items WHERE Id = @id AND Status = 'active'";
    cmd.Parameters.AddWithValue("@id", itemId);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) { ReportError("ITEM_NOT_FOUND", Lang.T("Article {0} not found", itemId), json: json); return (false, null); }
    string title = r.GetString(0);
    string content = r.IsDBNull(1) ? "" : r.GetString(1);
    string desc = r.IsDBNull(2) ? "" : r.GetString(2);
    string existing = r.IsDBNull(3) ? "" : r.GetString(3);
    int feedId = r.GetInt32(4);
    r.Close();

    if (!string.IsNullOrEmpty(existing))
    {
        if (!quiet) Console.WriteLine(Lang.T("Article [{0}] {1} already has a summary, skipped (delete it first to regenerate)", itemId, title));
        if (json) JsonOut(new { success = true, itemId, title, summary = existing, cached = true });
        return (true, existing);
    }

    string text = string.IsNullOrEmpty(content) ? desc : content;
    if (text.Length > 6000) text = text[..6000];
    var prompt = $"请用 150 字以内概括以下文章的核心内容（用中文回答，直接输出摘要正文，不要额外解释）：\n\n标题：{title}\n\n正文：{text}";

    try
    {
        EnsureAiPrompted();
        var summary = await CallLlmAsync(prompt, cfg, articleId: itemId, sourceId: feedId);
        if (summary == null) throw new AiException("EMPTY_RESPONSE", Lang.T("LLM returned empty"), Lang.T("Retry or check the model config"));

        var upd = conn.CreateCommand();
        upd.CommandText = "UPDATE Items SET Summary = @s, SummaryAt = @now WHERE Id = @id";
        upd.Parameters.AddWithValue("@s", summary.Trim());
        upd.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
        upd.Parameters.AddWithValue("@id", itemId);
        upd.ExecuteNonQuery();
        if (!quiet) Console.WriteLine(Lang.T("Summary generated: [{0}] {1}", itemId, title));
        if (json) JsonOut(new { success = true, itemId, title, summary = summary.Trim() });
        return (true, summary.Trim());
    }
    catch (HttpRequestException ex)
    {
        ReportError("NETWORK_ERROR", Lang.T("Network error, cannot reach the LLM service"), Lang.T("Check your network connection"), ex.Message, json);
        return (false, null);
    }
    catch (AiException ex)
    {
        ReportError(ex.Code, ex.Message, ex.Suggestion, ex.Details, json);
        return (false, null);
    }
}

// 单篇/整源摘要 CLI；支持 '12' 和 'feed:3'；--json 结构化输出（feed: 模式跳过 y/n 确认）
static async Task SummaryCli(string arg, string dbPath, bool json = false)
{
    EnsureAiPrompted();

    // feed:N → 为该订阅源全部未摘要的 active 文章逐个生成
    if (arg.StartsWith("feed:", StringComparison.OrdinalIgnoreCase))
    {
        if (!int.TryParse(arg["feed:".Length..].Trim(), out int feedDisplay))
        {
            SetExit();
            Console.WriteLine(Lang.T("Bad format. Correct: {0}", "--summary feed:3"));
            return;
        }
        int feedReal = GetRealId(feedDisplay, dbPath);
        if (feedReal == 0) { SetExit(); Console.WriteLine(Lang.T("Feed number {0} not found", feedDisplay)); return; }

        using var conn = OpenDb(dbPath);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Title FROM Items WHERE Status = 'active' AND FeedId = @fid AND (Summary IS NULL OR Summary = '')";
        cmd.Parameters.AddWithValue("@fid", feedReal);
        using var r = cmd.ExecuteReader();
        var items = new List<(int Id, string Title)>();
        while (r.Read()) items.Add((r.GetInt32(0), r.GetString(1)));
        r.Close();

        if (items.Count == 0)
        {
            if (json) JsonOut(new { success = true, data = new { feed = feedDisplay, results = Array.Empty<object>(), ok = 0, fail = 0 } });
            else Console.WriteLine(Lang.T("All active articles of feed {0} already have summaries", feedDisplay));
            return;
        }
        if (!json)
        {
            Console.WriteLine(Lang.T("Will summarize {1} articles of feed {0}, confirm? (y/n)", feedDisplay, items.Count));
            if (Console.ReadLine()?.ToLower() != "y") { Console.WriteLine(Lang.T("Cancelled")); return; }
        }

        int ok = 0, fail = 0;
        var results = new List<object>();
        foreach (var it in items)
        {
            var (o, s) = await SummarizeItem(dbPath, it.Id, json: false, quiet: json);
            if (json) results.Add(new { itemId = it.Id, title = it.Title, ok = o, summary = s });
            if (o) ok++; else fail++;
            if (!json) Console.WriteLine(Lang.T("  progress: {0}/{1}", ok + fail, items.Count));
        }
        if (json)
        {
            JsonOut(new { success = true, data = new { feed = feedDisplay, results, ok, fail } });
            if (fail > 0) SetExit();
        }
        else Console.WriteLine(Lang.T("Done: {0} OK, {1} failed", ok, fail));
        return;
    }

    // 单篇文章
    if (!int.TryParse(arg, out int sumId)) { SetExit(); Console.WriteLine(Lang.T("Usage: sip --summary <article-number | feed:number>")); return; }
    await SummarizeItem(dbPath, sumId, json: json, quiet: json);
}

// 全部摘要
static async Task SummaryAllCli(string dbPath)
{
    EnsureAiPrompted();
    using var conn = OpenDb(dbPath);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id, Title FROM Items WHERE Status = 'active' AND (Summary IS NULL OR Summary = '')";
    using var r = cmd.ExecuteReader();
    var items = new List<(int Id, string Title)>();
    while (r.Read()) items.Add((r.GetInt32(0), r.GetString(1)));
    r.Close();

    if (items.Count == 0) { Console.WriteLine(Lang.T("All active articles already have summaries")); return; }
    Console.WriteLine(Lang.T("Will summarize {0} articles, confirm? (y/n)", items.Count));
    if (Console.ReadLine()?.ToLower() != "y") { Console.WriteLine(Lang.T("Cancelled")); return; }

    int ok = 0, fail = 0;
    foreach (var it in items)
    {
        if ((await SummarizeItem(dbPath, it.Id)).Ok) ok++; else fail++;
        Console.WriteLine(Lang.T("  progress: {0}/{1}", ok + fail, items.Count));
    }
    Console.WriteLine(Lang.T("Done: {0} OK, {1} failed", ok, fail));
}

// ══════════ 交互式配置向导 ══════════
static void InitAiConfigInteractive(string dbPath)
{
    EnsureAiPrompted();
    Console.WriteLine(Lang.T("===== RSS Reader AI Setup Wizard ====="));
    Console.WriteLine(Lang.T("All services use the OpenAI-compatible format (Ollama / DeepSeek / OpenAI / any compatible service), endpoints and ports can be freely specified."));
    var cfg = LoadConfig(dbPath);

// --- Embedding ---
    Console.WriteLine(Lang.T("\n[1/3] Embedding service (for semantic search, OpenAI-compatible format):"));
    Console.Write(Lang.T("  endpoint (just http://host:port or https://domain, /v1 auto-appended) [current: {0}]: ", cfg.Embedding.ApiEndpoint));
    string embEndpoint = Console.ReadLine()?.Trim() ?? "";
    if (!string.IsNullOrEmpty(embEndpoint))
    {
        cfg.Embedding.ApiEndpoint = EnsureV1Endpoint(embEndpoint);
        Console.WriteLine(Lang.T("  → final endpoint: {0}", cfg.Embedding.ApiEndpoint));
    }

    Console.Write(Lang.T("  model (e.g. nomic-embed-text / bge-m3 / text-embedding-3-small) [current: {0}]: ", cfg.Embedding.Model));
    string embModel = Console.ReadLine()?.Trim() ?? "";
    if (!string.IsNullOrEmpty(embModel)) cfg.Embedding.Model = embModel;

    Console.Write(Lang.T("  vector dimensions (e.g. 768/1024/1536, leave empty to auto-detect) [current: {0}]: ", cfg.Embedding.Dimensions));
    if (int.TryParse(Console.ReadLine()?.Trim(), out int embDim) && embDim > 0)
        cfg.Embedding.Dimensions = embDim;

    Console.Write(Lang.T("  Embedding API key (skip with Enter for local Ollama; hidden input, stored in OS credentials) [current: {0}]: ",
        CredHas("embedding_api_key") ? Lang.T("set") : Lang.T("not set")));
    var embKey = ReadSecret();
    if (!string.IsNullOrEmpty(embKey)) CredSet("embedding_api_key", embKey);

    // --- LLM ---
    Console.WriteLine(Lang.T("\n[2/3] LLM service (for summaries, OpenAI-compatible format):"));
    Console.Write(Lang.T("  endpoint (just https://host[:port] or http://host:port, /v1 auto-appended) [current: {0}]: ", cfg.Llm.ApiEndpoint));
    string llmEndpoint = Console.ReadLine()?.Trim() ?? "";
    if (!string.IsNullOrEmpty(llmEndpoint))
    {
        cfg.Llm.ApiEndpoint = EnsureV1Endpoint(llmEndpoint);
        Console.WriteLine(Lang.T("  → final endpoint: {0}", cfg.Llm.ApiEndpoint));
    }

    Console.Write(Lang.T("  model (e.g. deepseek-chat / gpt-4o-mini / qwen2.5) [current: {0}]: ", cfg.Llm.Model));
    string llmModel = Console.ReadLine()?.Trim() ?? "";
    if (!string.IsNullOrEmpty(llmModel)) cfg.Llm.Model = llmModel;

    Console.Write(Lang.T("  LLM API key (hidden input, stored in OS credentials, Enter to skip): "));
    var llmKey = ReadSecret();
    if (!string.IsNullOrEmpty(llmKey)) CredSet("llm_api_key", llmKey);

    // --- 通用 ---
    Console.Write(Lang.T("\n[3/3] Default search similarity threshold (0-1, suggest 0.7; 0.5 for local bge-m3) [current: {0}]: ", cfg.Embedding.SearchThreshold));
    if (float.TryParse(Console.ReadLine()?.Trim(), out float thr)) cfg.Embedding.SearchThreshold = thr;

    SaveConfig(dbPath, cfg);
    Console.WriteLine(Lang.T("\nConfig saved. You can tweak ai_config.json for the model; API keys live in the OS credential store."));
    Console.WriteLine(Lang.T("Note: after changing the Embedding model, run sip --reindex to re-embed."));
}

// 读取密码（不回显）——跨平台简易实现。
// 密文输入：仅支持真实终端 ReadKey；非交互（stdin 重定向）下拒绝（不降级 ReadLine，防止 AI/脚本驱动）
static string ReadSecret()
{
    var sb = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
        {
            sb.Length--;
            continue;
        }
        sb.Append(key.KeyChar);
    }
    Console.WriteLine();
    return sb.ToString();
}

// 查看配置
static void ShowConfig(string dbPath)
{
    var cfg = LoadConfig(dbPath);
    Console.WriteLine(Lang.T("===== AI Config ====="));
    Console.WriteLine(Lang.T("Embedding: {0} / {1} ({2} dims)", cfg.Embedding.Provider, cfg.Embedding.Model, cfg.Embedding.Dimensions));
    Console.WriteLine(Lang.T("  endpoint: {0}", cfg.Embedding.ApiEndpoint));
    Console.WriteLine(Lang.T("  default search threshold: {0}", cfg.Embedding.SearchThreshold));
    Console.WriteLine(Lang.T("  API Key: {0}", CredHas("embedding_api_key") ? Lang.T("set") : Lang.T("not set")));
    Console.WriteLine(Lang.T("LLM: {0} / {1}", cfg.Llm.Provider, cfg.Llm.Model));
    Console.WriteLine(Lang.T("  endpoint: {0}", cfg.Llm.ApiEndpoint));
    Console.WriteLine(Lang.T("  API Key: {0}", CredHas("llm_api_key") ? Lang.T("set") : Lang.T("not set")));
    Console.WriteLine(Lang.T("Config file: {0}", ConfigPath(dbPath)));

    var warn = CheckDimensionMismatch(dbPath, cfg.Embedding);
    if (warn != null) Console.WriteLine($"\n{warn}");
}

// ══════════════════════════════════════════════════════════
// 以下为类型定义（必须位于所有顶级语句/局部函数之后）
// ══════════════════════════════════════════════════════════

// 进程内 AI 状态
// ══════════ 阅读情况报告（Insights）：遥测事实汇总，决定在用户 ══════════
static string SettingsPath() => Path.Combine(dataDir, "sip_settings.json");

static SipSettings LoadSettings()
{
    try
    {
        if (File.Exists(SettingsPath()))
            return JsonSerializer.Deserialize<SipSettings>(File.ReadAllText(SettingsPath())) ?? new SipSettings();
    }
    catch { }
    return new SipSettings();
}

static void SaveSettings(SipSettings s)
{
    try
    {
        File.WriteAllText(SettingsPath(), JsonSerializer.Serialize(s,
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
    catch { }
}

// 解析报告间隔：off / 0 → null；Nd → N 天；否则 null（无效）
static int? TryParseInsightsInterval(string raw)
{
    string r = (raw ?? "").Trim();
    if (r.Length == 0 || r.Equals("off", StringComparison.OrdinalIgnoreCase) || r == "0") return null;
    var m = Regex.Match(r, @"^(\d+)\s*d$", RegexOptions.IgnoreCase);
    if (m.Success)
    {
        int d = int.Parse(m.Groups[1].Value);
        return d >= 1 ? d : null;
    }
    return null;
}

// 报告是否「到期」（上次生成时间 + 间隔 <= now）。未生成过或间隔 off → false
static bool IsInsightsDue(DateTime now)
{
    var s = LoadSettings();
    int? days = TryParseInsightsInterval(s.InsightsInterval);
    if (days == null) return false;
    if (string.IsNullOrEmpty(s.LastInsightsAt)) return false;
    if (TryParseIso(s.LastInsightsAt) is not DateTime last) return false;
    return now - last >= TimeSpan.FromDays(days.Value);
}

// CLI 末尾报告到期提醒（仿 RemindDueFeeds：不污染 --json）
static void RemindDueInsights(string[] args, string dbPath)
{
    try
    {
        if (args.Contains("--json", StringComparer.OrdinalIgnoreCase)) return;
        if (!TelemetryService.IsEnabled) return;
        if (!IsInsightsDue(DateTime.Now)) return;
        Console.WriteLine(Lang.T("你的阅读情况报告已就绪，运行 sip --insights 查看"));
    }
    catch { }
}

// 按源统计点赞（来自 signals，不受遥测限制）：FeedId → (userLikes, aiLikes)
static Dictionary<int, (int User, int Ai)> BuildFeedLikeStats(string dbPath)
{
    var result = new Dictionary<int, (int, int)>();
    try
    {
        var signals = LoadSignals();
        var ids = signals.Keys.Where(k => int.TryParse(k, out _)).Select(int.Parse).Distinct().ToList();
        if (ids.Count == 0) return result;
        var feedOf = new Dictionary<int, int>();
        using (var conn = OpenDb(dbPath))
        {
            conn.Open();
            var c = conn.CreateCommand();
            c.CommandText = "SELECT Id, FeedId FROM Items WHERE Id IN (" + string.Join(",", ids) + ")";
            using var r = c.ExecuteReader();
            while (r.Read()) feedOf[r.GetInt32(0)] = r.GetInt32(1);
        }
        foreach (var kv in signals)
        {
            if (!int.TryParse(kv.Key, out int itemId)) continue;
            if (!feedOf.TryGetValue(itemId, out int fid)) continue;
            result.TryGetValue(fid, out var cur);
            if (kv.Value.UserLike) cur.Item1++;
            if (kv.Value.AiLike) cur.Item2++;
            result[fid] = cur;
        }
    }
    catch { }
    return result;
}

// 单源「事实原因」列表（只陈述事实与数据，不替用户下价值结论）
static List<string> BuildReasons(int feedId, string schedule, DateTime? lastChecked, long active, long opened, long completed, long skipped, Dictionary<int, (int FailCount, string LastError, string LastOkAt)> health, DateTime now)
{
    var r = new List<string>();
    health.TryGetValue(feedId, out var h);
    if (h.FailCount > 0)
        r.Add(Lang.T("近期拉取失败 {0} 次", h.FailCount));
    else if (lastChecked is DateTime lc && IsFeedStale(schedule, lc, now))
        r.Add(Lang.T("长期未更新"));
    if (opened == 0)
        r.Add(Lang.T("窗口内未打开任何一篇（订阅 {0} 篇）", active));
    else
    {
        r.Add(Lang.T("窗口内打开 {0} 篇、读完 {1} 篇、跳过 {2} 篇", opened, completed, skipped));
        double rate = completed / (double)Math.Max(1, opened);
        if (rate < 0.4)
            r.Add(Lang.T("完读率仅 {0:P0}", rate));
    }
    return r;
}

// 来源「状态」：仅技术故障（拉取失败/长期未更新）；阅读行为属活跃度，不计入
static string FeedStatusText(int feedId, string schedule, DateTime? lastChecked, DateTime now)
{
    var map = LoadFeedHealth();
    map.TryGetValue(feedId, out var e);
    if (e.FailCount > 0) return Lang.T("✗ 失败 {0} 次", e.FailCount);
    if (lastChecked is DateTime lc && IsFeedStale(schedule, lc, now)) return Lang.T("⚠ 长期未更新");
    return Lang.T("正常");
}

// 构建报告数据
static List<InsightsFeed> BuildInsights(string dbPath, int windowDays)
{
    var feeds = new List<(int Id, string Title, string Schedule, DateTime? LastChecked, long Active)>();
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var c = conn.CreateCommand();
        c.CommandText = @"
            SELECT f.Id, f.Title, f.Schedule, f.LastCheckedAt,
                   (SELECT COUNT(*) FROM Items WHERE FeedId = f.Id AND Status = 'active')
            FROM Feeds f ORDER BY f.Id";
        using var r = c.ExecuteReader();
        while (r.Read())
            feeds.Add((r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2),
                r.IsDBNull(3) ? null : TryParseIso(r.GetString(3)), r.GetInt64(4)));
    }

    var reading = TelemetryService.FeedReadingStats(windowDays);
    var aiCalls = TelemetryService.FeedAiCallStats(windowDays);
    var likes = BuildFeedLikeStats(dbPath);
    var health = LoadFeedHealth();
    var now = DateTime.Now;

    var list = new List<InsightsFeed>();
    foreach (var f in feeds)
    {
        reading.TryGetValue(f.Id, out var rd);
        aiCalls.TryGetValue(f.Id, out var ac);
        likes.TryGetValue(f.Id, out var lk);
        long backlog = Math.Max(0, f.Active - rd.Opened);
        list.Add(new InsightsFeed
        {
            FeedId = f.Id,
            Title = f.Title,
            Schedule = f.Schedule,
            Active = f.Active,
            Backlog = backlog,
            Opened = rd.Opened,
            Completed = rd.Completed,
            Skipped = rd.Skipped,
            UserLikes = lk.User,
            AiLikes = lk.Ai,
            LlmCalls = ac.Llm,
            EmbeddingCalls = ac.Embedding,
            Status = FeedStatusText(f.Id, f.Schedule, f.LastChecked, now),
            Reasons = BuildReasons(f.Id, f.Schedule, f.LastChecked, f.Active, rd.Opened, rd.Completed, rd.Skipped, health, now)
        });
    }
    return list;
}

// 全局 AI 调用概况文本
static string AiCallSummary(int windowDays)
{
    var (total, ok, fail, llm, emb) = TelemetryService.GlobalAiCallStats(windowDays);
    if (total == 0) return Lang.T("窗口内暂无 AI 调用");
    return Lang.T("AI 调用 {0} 次 · 摘要/对话 {1} · 嵌入 {2} · 成功 {3} · 失败 {4}", total, llm, emb, ok, fail);
}

// CLI：sip --insights [--window N d] [--json]
static void InsightsCli(string[] args, string dbPath)
{
    bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
    int window = 30;
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i].Equals("--window", StringComparison.OrdinalIgnoreCase) && TryParseInsightsInterval(args[i + 1]) is int w)
            window = w;

    if (!TelemetryService.IsEnabled)
    {
        ReportError("TELEMETRY_OFF", Lang.T("阅读情况报告需要先开启遥测"), Lang.T("运行 sip telemetry enable 后重试"), json: json);
        return;
    }

    var list = BuildInsights(dbPath, window);
    var (total, ok, fail, llm, emb) = TelemetryService.GlobalAiCallStats(window);

    // 记录本次报告时间（用于下次到期判定）
    var s = LoadSettings();
    s.LastInsightsAt = DateTime.Now.ToString("O");
    SaveSettings(s);

    if (json)
    {
        JsonOut(new
        {
            success = true,
            data = new
            {
                windowDays = window,
                generatedAt = DateTime.Now.ToString("O"),
                aiCalls = new { total, success = ok, fail, llm, embedding = emb },
                feeds = list.Select(x => new
                {
                    id = x.FeedId, title = x.Title, schedule = x.Schedule,
                    active = x.Active, backlog = x.Backlog,
                    opened = x.Opened, completed = x.Completed, skipped = x.Skipped,
                    completionRate = x.Opened > 0 ? Math.Round(100.0 * x.Completed / x.Opened, 0) : 0,
                    userLikes = x.UserLikes, aiLikes = x.AiLikes,
                    llmCalls = x.LlmCalls, embeddingCalls = x.EmbeddingCalls,
                    status = x.Status, reasons = x.Reasons
                })
            }
        });
        return;
    }

    Console.WriteLine("──────────────────────────────────────────");
    Console.WriteLine(Lang.T("阅读情况报告（窗口 {0} 天）· {1}", window, DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
    Console.WriteLine(AiCallSummary(window));
    Console.WriteLine(Lang.T("事实仅供参考，决定在你 —— 可邀请 Agent 协助，或按 a/x 当场调整。"));
    Console.WriteLine("──────────────────────────────────────────");
    if (list.Count == 0) { Console.WriteLine(Lang.T("尚无订阅源")); return; }
    foreach (var x in list)
    {
        string rate = x.Opened > 0 ? Math.Round(100.0 * x.Completed / x.Opened, 0) + "%" : "—";
        string status = x.Status == Lang.T("正常") ? "" : "  " + x.Status;
        Console.WriteLine($"[{x.FeedId}] {CjkSpace(StripControlChars(x.Title))}{status}");
        Console.WriteLine($"   订阅 {x.Active} 篇 · 未读积压 {x.Backlog}");
        Console.WriteLine($"   打开 {x.Opened} · 读完 {x.Completed} · 完成率 {rate} · 跳过 {x.Skipped}");
        Console.WriteLine($"   ♥ 你点赞 {x.UserLikes} · 🤖 AI 点赞 {x.AiLikes}" + (x.LlmCalls > 0 || x.EmbeddingCalls > 0 ? $" · AI 摘要 {x.LlmCalls} 次" : ""));
        foreach (var reason in x.Reasons)
            Console.WriteLine($"   · {reason}");
    }
}

// CLI：sip --insights-interval <7d|30d|off>
static void InsightsIntervalCli(string arg, string dbPath)
{
    if (TryParseInsightsInterval(arg) == null && !arg.Equals("off", StringComparison.OrdinalIgnoreCase))
    {
        SetExit();
        Console.WriteLine(Lang.T("无效间隔，应为 7d / 30d / off 之一"));
        return;
    }
    var s = LoadSettings();
    s.InsightsInterval = arg.Trim().ToLowerInvariant();
    SaveSettings(s);
    int? days = TryParseInsightsInterval(arg);
    Console.WriteLine(arg.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)
        ? Lang.T("已关闭报告定时提醒")
        : Lang.T("报告定时提醒已设为每 {0} 天一次（遥测开启时到期会提醒）", days ?? 0));
}


}

static class AiState
{
    public static bool Warned = false;
    public static bool IgnoreAnnouncement = false;  // --ignoresafeannouncement：跳过安全横幅等多余输出
    public static int ExitCode = 0;  // CLI 退出码（脚本/AI 用 exit code 判断成败；0=成功，非零=失败）
}

// TUI 图片缓存（URL → sixel 字节 + 该图占多少终端单元宽）
//
// 连宽度一起存的原因:Markdown 视图**每帧绘制**都会对每个可见图片调一次
// ImageLoader。命中缓存时如果每次都把几百 KB 的 sixel 解成字符串再跑正则去
// 取宽度,滚动会直接卡死。宽度在首次编码时算一次就够。
static class TuiImageCache
{
    // 后台预取线程会写、渲染线程会读 —— 必须线程安全,所以是 Concurrent 的。
    public static readonly ConcurrentDictionary<string, (byte[] Sixel, int WidthCells)> Map = new();

    // 正在下载/编码中的 URL。避免多个预取任务重复下载同一张图
    // (切文章很快时,旧文章的预取任务可能还在跑)。
    // value 无意义,用 byte 占位。
    public static readonly ConcurrentDictionary<string, byte> InFlight = new();
}

// TUI Markdown 渲染过程状态（链接收集、图片宽度）
static class TuiMdState
{
    public static List<(string Text, string Url)> Links = new();
    public static int ImageWidth = 80;

    // —— 图片垂直空间预留(TUI 专用)——
    //
    // 是否给图片在文档流里预留垂直空间。只有 TUI 的 Markdown 视图需要 ——
    // BuildArticleMarkdown 同时供 CLI 导出使用,命令行输出里塞零宽空格是垃圾。
    public static bool ReserveImageSpace = false;

    // 零宽空格(U+200B)。它不是空白字符,所以 CommonMark 不会把它所在的行当成
    // 空行折叠掉(char.IsWhiteSpace('\u200b') == false),但渲染宽度为 0,
    // 屏幕上看起来就是空行。用它来"伪造"不会被折叠的连续空行。
    public const string BlankLineFiller = "\u200b";

    // 图片后面补多少个"零宽空格段落"。实测每个产出 2 行(自身 + 段落间隔),
    // 所以 10 个 = 20 行。图片最高 18 单元(见 ImageSixel.cs 的 MaxHeightCells),
    // 预留 20 行 -> 图片底部到正文之间约 2 行空隙;图片偏矮时空隙会更大些。
    public const int ImageReservedBlankParagraphs = 10;
}

// ══════════ 语言 / 本地化支持 ══════════
// 用法示例：T("Hello") / T("Total {0} articles", n)（源码原文 = 英文）
// 查找顺序：readwithhotsoup/languages/<代码>.json（首次启动已自动复制默认翻译，可直接编辑）
//        → 英文原文（原样返回）
// 语言文件格式（JSON 字典，键为英文原文，值为译文）：
//   例如 { "Hello": "你好", "Total {0} articles": "共有 {0} 篇" }
// 内置语言：zh-CN.json（英→中，默认界面）、en-US.json（英→英，即英文原文）
static class Lang
{
    public static string Code { get; private set; } = "zh-CN";

    private static readonly Dictionary<string, string> _custom = new();
    private static bool _loaded;

    public static void Init(string dataDir, string? requested)
    {
        string code = requested ?? "";
        if (string.IsNullOrEmpty(code))
            code = Environment.GetEnvironmentVariable("LANG") ?? "zh-CN";
        Code = code;

        // 语言文件只从数据目录 readwithhotsoup/languages/ 读取（首次启动已复制默认翻译）
        string path = Path.Combine(dataDir, "languages", code + ".json");
        if (!File.Exists(path)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            _custom.Clear();
            Flatten(doc.RootElement, _custom);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"加载语言文件失败：{ex.Message}");
        }
        _loaded = true;
    }

    // 递归展平：兼容旧扁平文件（值全为字符串）与嵌套分组文件
    // 分组名（如 ui/start/help）只是组织分类，叶子用「英文源文本 → 译文」。
    static void Flatten(JsonElement el, Dictionary<string, string> target)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                break; // 由上层赋值，这里不处理
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        target[prop.Name] = prop.Value.GetString() ?? prop.Name;
                    else if (prop.Value.ValueKind == JsonValueKind.Object)
                        Flatten(prop.Value, target);
                }
                break;
        }
    }

    public static string T(string key)
    {
        if (_loaded && _custom.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
            return v;
        return key;
    }

    public static string T(string key, params object[] args)
    {
        string s = T(key);
        try { return string.Format(s, args); }
        catch (FormatException) { return s; }
    }
}


// ══════════ AI 配置模型（ai_config.json，非敏感信息）═══════════
class AiConfig
{
    public EmbeddingCfg Embedding { get; set; } = new();
    public LlmCfg Llm { get; set; } = new();
    // 全文抓取安全策略：默认拦截私网地址（SSRF 防护），内网源可设 true 放行
    public bool AllowPrivateNet { get; set; } = false;
}

class EmbeddingCfg
{
    public string Provider { get; set; } = "openai-compatible";  // 备注字段（兼容服务名）
    public string Model { get; set; } = "nomic-embed-text";
    public int Dimensions { get; set; } = 768;          // 向量维度
    public string ApiEndpoint { get; set; } = "http://localhost:11434/v1";  // 兼容服务端点
    public float SearchThreshold { get; set; } = 0.7f;  // 默认相似度阈值
}

class LlmCfg
{
    public string Provider { get; set; } = "openai-compatible";  // 备注字段（兼容服务名）
    public string Model { get; set; } = "deepseek-chat";
    public string ApiEndpoint { get; set; } = "https://api.deepseek.com/v1";
}

// 解析后的更新计划（见 TryParseSchedule）
class FeedSchedule
{
    public string Raw { get; set; } = "";       // 原始表达式
    public bool IsManual { get; set; }          // manual / 空：不自动更新
    public TimeSpan? Interval { get; set; }      // 间隔型：5m / 1h / 7d ...
    public bool IsDaily { get; set; }            // daily@HH:mm
    public int DailyHour { get; set; }
    public int DailyMinute { get; set; }
    public bool IsWeekly { get; set; }           // weekly@Ddd HH:mm
    public int WeeklyDay { get; set; }           // 0=周日 ... 6=周六
    public int WeeklyHour { get; set; }
    public int WeeklyMinute { get; set; }
}

// 一个到期的订阅源（GetDueFeeds 的结果）
class DueFeed
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime? LastChecked { get; set; }
    public string Schedule { get; set; } = "";
}


// ══════════ 自绘侧栏（订阅源 + 文章，标题自动换行）═══════════
// Terminal.Gui 的 TreeView 每行只能画一行，长标题会被截断；
// 这里自绘一个轻量侧栏：来源可展开/折叠，标题按列宽换行（CJK 宽度感知），
// 提供与旧 TreeView 用法一致的接口（SelectedObject/SetFeeds/Toggle/...），
// 便于正文区、状态栏等其余代码无感替换。

// ══════════ 自绘侧栏（订阅源 + 文章，标题自动换行）═══════════
// Terminal.Gui 的 TreeView 每行只能画一行，长标题会被截断；
// 这里自绘一个轻量侧栏：来源可展开/折叠，标题按列宽换行（CJK 宽度感知），
// 提供与旧 TreeView 用法一致的接口（SelectedObject/SetFeeds/Toggle/...），
// 便于正文区、状态栏等其余代码无感替换。


// 开始界面的自绘视图：整块居中排版


// 报告数据行（卡片）
class InsightsFeed
{
    public int FeedId { get; set; }
    public string Title { get; set; } = "";
    public string Schedule { get; set; } = "";
    public long Active { get; set; }
    public long Backlog { get; set; }
    public long Opened { get; set; }
    public long Completed { get; set; }
    public long Skipped { get; set; }
    public int UserLikes { get; set; }
    public int AiLikes { get; set; }
    public long LlmCalls { get; set; }
    public long EmbeddingCalls { get; set; }
    public string Status { get; set; } = "";        // 仅技术故障：正常/⚠长期未更新/✗失败 N 次
    public List<string> Reasons { get; set; } = new();  // 事实原因（活跃度观察，无价值结论）
}

// 应用设置
class SipSettings
{
    public string InsightsInterval { get; set; } = "off";
    public string LastInsightsAt { get; set; } = "";
    public int? FloodThresholdPerDay { get; set; }   // 今日高频源阈值（单日新增）；null=自动 max(20,中位数×5)
    public double DedupThreshold { get; set; } = 0.8; // 跨源去重段落重合度阈值
    // 改动分级阈值(你定;语义距离 = 1 − cos(旧版,新版)):
    //   距离 < ChangeGradePolish → ⚪润色(重写但意思没变)  < ChangeGradeReverse → 🟡调整  ≥ → 🔴反转
    public double ChangeGradePolish { get; set; } = 0.08;
    public double ChangeGradeReverse { get; set; } = 0.45;
    // 语义去重阈值(cos;存前检测,命中自动跳过/询问,不替你删)
    public double DedupSemanticThreshold { get; set; } = 0.92;
    // 主题归组阈值(cos;只归组不建组,主题由你定义)
    public double GroupMatchThreshold { get; set; } = 0.75;
    // 孟思琳(simon)安全守护挡位:1=基础 2=严格 3=极致。
    // 默认开启、无法关闭(无 0),只能调节挡位——降挡必须交互终端
    public int SimonLevel { get; set; } = 1;
}

// Source Policy：用户确认的「处理规则」（source_policy.json）。createdBy 永远 user，AI 永不自动写。
class SourcePolicyRule
{
    public string Action { get; set; } = "";   // archive / keep / lower_frequency / tag / unsubscribe
    public string Schedule { get; set; } = "";  // lower_frequency 的目标频率
    public string Tag { get; set; } = "";       // tag 标签名
    public string Note { get; set; } = "";      // 用户备注/理由
    public string CreatedBy { get; set; } = "user";
    public string UpdatedAt { get; set; } = "";
}

// Onboarding 推荐源模板条目（templates.json）
class SourceTemplate
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

// ══════════ 报告卡片视图（每源一张卡片，j/k 移动）═══════════

// 全文搜索结果条目
class GrepSnippetResult
{
    public int ItemId { get; set; }
    public string Title { get; set; } = "";
    public string Link { get; set; } = "";
    public string FeedTitle { get; set; } = "";
    public int Count { get; set; }
    public List<string> Snippets { get; set; } = new();
    public List<int> LineNumbers { get; set; } = new();
    public int TotalSnippets { get; set; }
    public string Quality { get; set; } = "";
}

// 全文搜索结果条目
class GrepHit
{
    public int ItemId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";   // 已转纯文本的摘要（TUI 渲染用）
    public string Content { get; set; } = "";        // 原始正文（HTML，CLI 片段模式用）
    public string Summary { get; set; } = "";        // AI 摘要（CLI 片段模式拼进 haystack）
    public string Link { get; set; } = "";
    public string FeedTitle { get; set; } = "";
}

// ══════════ 自定义异常 ══════════
#pragma warning disable SYSLIB0051
[Serializable]
public class AiException : Exception
{
    public string Code { get; private set; } = string.Empty;
    public string? Suggestion { get; private set; }
    public string? Details { get; private set; }

    public AiException() : base() { }

    public AiException(string message) : base(message) { }

    public AiException(string code, string message, string? suggestion = null, string? details = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Suggestion = suggestion;
        Details = details;
    }

    protected AiException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
        Code = info.GetString(nameof(Code)) ?? string.Empty;
        Suggestion = info.GetString(nameof(Suggestion));
        Details = info.GetString(nameof(Details));
    }

    [Obsolete]
    public override void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        base.GetObjectData(info, context);
        info.AddValue(nameof(Code), Code);
        info.AddValue(nameof(Suggestion), Suggestion);
        info.AddValue(nameof(Details), Details);
    }

    public override string ToString()
    {
        return $"错误码：{Code}，消息：{Message}，建议：{Suggestion}，详情：{Details}\n{base.ToString()}";
    }
}
#pragma warning restore SYSLIB0051



// ===== 领域模型(原错放在 Tui.cs,归位至核心类型区)=====
// 搜索结果条目
class SearchHit
{
    public int ItemId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Content { get; set; } = "";
    public string Link { get; set; } = "";
    public string FeedTitle { get; set; } = "";
    public int FeedId { get; set; }
    public float Score { get; set; }
}

// 全文搜索结果条目
class FulltextVecEntry { public int ItemId { get; set; } public int FeedId { get; set; } public int ModelId { get; set; } public float[] Vector { get; set; } = Array.Empty<float>(); }

// 来源健康记录（feed_health.json）
class FeedHealthEntry { public int FailCount { get; set; } public string LastError { get; set; } = ""; public string LastOkAt { get; set; } = ""; }

// 文章标记信号（article_signals.json）
class SignalEntry
{
    public bool UserLike { get; set; }
    public bool AiLike { get; set; }
    public string AiReason { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

// Sip Today 条目
class TodayItem
{
    public int ItemId { get; set; }
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public string Reason { get; set; } = "";   // 为什么出现在今日（新增/更新/AI关注/你收藏过…）
    public double Minutes { get; set; }        // 预估阅读时长
    public int Score { get; set; }
}

// 今日变化摘要：按源新增计数 + 高频源 + 被作者改过 + 可能同文（纯事实，零 LLM）
class TodayDigest
{
    public int NewTotal { get; set; }
    public int SourceCount { get; set; }
    public List<SourceCount> NewBySource { get; set; } = new();   // 每个源新增数（含高频标记）
    public List<TodayModified> Modified { get; set; } = new();    // 被作者改过（改动概览）
    public List<DedupCluster> Dedups { get; set; } = new();     // 可能同文（重复簇）
}

class SourceCount
{
    public string Source { get; set; } = "";
    public int Count { get; set; }
    public bool Flood { get; set; }          // 腹泻式/高频源
}

class TodayModified
{
    public int ItemId { get; set; }          // 最新版本 Id → sip --diff <ItemId>
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public bool TitleChanged { get; set; }   // 标题是否改过
    public int AddedLines { get; set; }      // 正文新增行数
    public int RemovedLines { get; set; }    // 正文删除行数
    public int WordDelta { get; set; }       // 约 ±字数
}

// 跨源去重规则（dedup.json）：键 = "feedId:url"（被隐藏那篇）；值为 canonical 信息
class DedupRule
{
    public int HiddenFeedId { get; set; }
    public string HiddenUrl { get; set; } = "";
    public int CanonicalFeedId { get; set; }
    public string CanonicalUrl { get; set; } = "";
    public string At { get; set; } = "";
}

// 一个「可能同文」候选对（检测结果，未处理）
class DedupCandidate
{
    public int ItemIdA { get; set; }
    public string TitleA { get; set; } = "";
    public string SourceA { get; set; } = "";
    public int ItemIdB { get; set; }
    public string TitleB { get; set; } = "";
    public string SourceB { get; set; } = "";
    public double Overlap { get; set; }      // 段落重合度
    public string DiffCmd { get; set; } = ""; // sip --diff A B
}

// 一个「重复簇」：互相重复的文章集合（代表篇 + 成员）。输出按簇，避免 16 万对的 pair 爆炸
class DedupCluster
{
    public int RepresentativeId { get; set; }   // 代表篇（成员之一）
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public List<int> Members { get; set; } = new();   // 全部成员（含代表）
    public int Size => Members.Count;
    public double MinOverlap { get; set; }            // 簇内最小重合度
}

