// ===== TUI 应用层:从 sipcore.cs 拆出的 TUI 专属代码(纯搬移,逻辑未改)=====
// 与 sipcore.cs 同属 partial class Program(顶层语句入口文件生成的类),
// 因此可自由调用 sipcore.cs 中的顶层函数(同类 private static 成员),
// 反之亦然。partial 类无法访问入口文件的 Main 局部变量(dataDir 等),
// 所以这些 TUI 函数一律通过 dbPath 参数拿数据目录 —— 拆出前已是如此。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Input;
using Terminal.Gui.Text;

public partial class Program
{
#pragma warning disable CS0618
// TUI：在对话框里显示多行文本（用于 diff/feed-info/likes 等控制台输出命令）
static void ShowTextDialog(string title, string text)
{
    var dlg = new Dialog { Title = " " + title + " ", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
    var tv = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1), ReadOnly = true, CanFocus = true, WordWrap = false };
    tv.Text = string.IsNullOrWhiteSpace(text) ? Lang.T("(无输出)") : text.TrimEnd();
    var hint = new Label { Text = Lang.T("  j/k 滚动 · q/Esc 关闭  "), X = 0, Y = Pos.AnchorEnd(0), Width = Dim.Fill(), Height = 1 };
    dlg.Add(tv, hint);
    dlg.KeyDown += (s, e) => { if (e.KeyCode == KeyCode.Esc || e.KeyCode == KeyCode.Q) { dlg.RequestStop(); e.Handled = true; } };
    Application.Run(dlg);
}

// TUI：运行一个控制台命令，捕获其 stdout 并在对话框显示（避免污染 TUI 界面）
static void RunCliCommandInTui(Action run)
{
    var sb = new System.Text.StringBuilder();
    var orig = Console.Out;
    try
    {
        using (var sw = new StringWriter(sb)) { Console.SetOut(sw); run(); }
    }
    finally { Console.SetOut(orig); }
    ShowTextDialog(Lang.T("输出"), sb.ToString());
}
#pragma warning restore CS0618

#pragma warning disable CS0618
// 删除线渲染（源被删时标题加删除线）
static string StrikeText(string s) { var sb = new StringBuilder(); foreach (var c in s) { sb.Append(c); sb.Append('\u0336'); } return sb.ToString(); }

// 今日哈汤阅读界面（主 TUI 阅读风格，只放当天这 5 篇）
static void ShowTodayPage(string dbPath)
{
    var (date, genAt, items, batch, read) = LoadTodayCache();
    if (items.Count == 0) { MessageBox.Query(Application.Instance, Lang.T("Notice"), Lang.T("今天还没有值得读的——去添加或更新一些订阅源吧"), Lang.T("OK")); return; }
    var signals = LoadSignals();
    var (done, target, tracking) = TodayProgress(dbPath);
    var readSet = new HashSet<int>(read);

    var top = new Window { Title = " 今日哈汤 ", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
    var header = new Label { Text = $"  {Lang.T("今日哈汤")} · {date} · {Lang.T("第 {0} 批", batch)} · {Lang.T("已完成 {0}/{1}", done, target)}", X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
    var list = new FeedManagerList { X = 0, Y = 1, Width = Dim.Percent(38), Height = Dim.Fill(2), CanFocus = true };
    var content = new TextView { X = Pos.Right(list) + 1, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(2), ReadOnly = true, WordWrap = true, CanFocus = false };
    var hint = new Label { Text = Lang.T("  j/k 移动 · l 点赞 · v 版本 · Esc 返回  "), X = 0, Y = Pos.AnchorEnd(0), Width = Dim.Fill(), Height = 1 };
    top.Add(header, list, content, hint);

    void MarkRead(int idx)
    {
        if (idx < 0 || idx >= items.Count) return;
        int id = items[idx].ItemId;
        if (readSet.Add(id))
        {
            var (d2, g2, i2, b2, r2) = LoadTodayCache();
            if (!r2.Contains(id)) { r2.Add(id); SaveTodayCache(d2, i2, b2, r2); }
        }
    }

    void RefreshList()
    {
        list.SetRows(items.Select((it, i) =>
        {
            string mark = readSet.Contains(it.ItemId) ? "✓ " : "  ";
            bool alive = ArticleExists(it.ItemId, dbPath);
            string t = alive ? CjkSpace(it.Title) : StrikeText(CjkSpace(it.Title));
            signals.TryGetValue(it.ItemId.ToString(), out var sig);
            string like = sig?.UserLike == true ? " ♥" : "";
            return (it.ItemId, $"{mark}{i + 1}. {t}{like}");
        }).ToList());
    }

    void ShowContent(int idx)
    {
        if (idx < 0 || idx >= items.Count) { content.Text = ""; return; }
        var it = items[idx];
        if (!ArticleExists(it.ItemId, dbPath))
        {
            content.Text = StrikeText(it.Title) + "\n\n" + Lang.T("对不起，但是源已经被删除了");
            return;
        }
        string body = "";
        using (var conn = OpenDb(dbPath))
        {
            conn.Open();
            var c = conn.CreateCommand();
            c.CommandText = "SELECT i.Title, COALESCE(NULLIF(i.Content,''), i.Description,''), f.Title FROM Items i LEFT JOIN Feeds f ON i.FeedId=f.Id WHERE i.Id=@id";
            c.Parameters.AddWithValue("@id", it.ItemId);
            using var r = c.ExecuteReader();
            if (r.Read())
            {
                string t = r.IsDBNull(0) ? "" : r.GetString(0);
                string text = r.IsDBNull(1) ? "" : r.GetString(1);
                string ft = r.IsDBNull(2) ? "" : r.GetString(2);
                body = t + "\n" + Lang.T("来源 {0} · {1} · ~{2} 分钟", ft, it.Reason, it.Minutes) + "\n\n" + StripHtml(text);
            }
        }
        content.Text = body;
    }

    void SelectAndShow(int idx)
    {
        if (idx < 0 || idx >= items.Count) return;
        list.MoveTo(idx);          // MoveTo 是相对量；从 0 出发移动 idx
        ShowContent(list.Selected);
        MarkRead(list.Selected);
        RefreshList();
    }

    RefreshList();
    // 接着读：跳第一篇未读
    int first = 0;
    for (int i = 0; i < items.Count; i++) if (!readSet.Contains(items[i].ItemId)) { first = i; break; }
    SelectAndShow(first);

    list.SelectionChanged += (s, e) => { ShowContent(list.Selected); MarkRead(list.Selected); RefreshList(); };
    top.Initialized += (s, e) => list.SetFocus();
    top.KeyDown += (s, e) =>
    {
        if (e.KeyCode == KeyCode.Esc) { top.RequestStop(); e.Handled = true; }   // 直接回归原界面，不调命令行
        else if (e.KeyCode == KeyCode.L)
        {
            int id = items[list.Selected].ItemId;
            ToggleSignal(id, ai: false, null, dbPath);
            signals = LoadSignals();
            ShowContent(list.Selected); RefreshList();
            e.Handled = true;
        }
        else if (e.KeyCode == KeyCode.V)
        {
            int id = items[list.Selected].ItemId;
            RunCliCommandInTui(() => ListVersionsCli(id.ToString(), dbPath));
            e.Handled = true;
        }
    };
    Application.Run(top);
}
#pragma warning restore CS0618

#pragma warning disable CS0618  // 使用尚未迁移的静态 Application API

// TUI 侧栏单源展开的最大加载条数(百万级适配:超大源不全量载入,防卡 UI/爆内存;
// 折叠状态的源计数始终显示真实总数,展开仅显示前 N 条)
const int TuiArticleLoadLimit = 20000;

static async Task<int> RunTui(string dbPath, bool appReady = false, bool showStartScreen = true, long preselectItemId = 0)
{
    if (!appReady) Application.Init();
    try
    {
        // 开始界面：回车进入 / Q 退出
        if (showStartScreen && !ShowStartScreen(dbPath)) return 0;
        EnsureTelemetryConsentTui();   // 首次询问遥测（默认保持关闭）

        // TUI 里给图片预留垂直空间。BuildArticleMarkdown 同时供 CLI 导出使用,
        // 那边不能加零宽空格,所以用开关区分而不是写死在生成逻辑里。
        //
        // 图片默认关闭 —— 只有 `sip pic` 启动时才开(ImagesEnabled)。原因见
        // ImageSixel.cs 头注释:终端 sixel 能力探测不可靠,WezTerm 自带的 ConPTY
        // 太老会吞掉 sixel,探测说"支持"实际出不来图还会让画面错乱。所以交给
        // 用户显式决定。
        TuiMdState.ReserveImageSpace = ImagesEnabled;

        // —— 左侧：订阅源 + 文章 侧栏（文章标题自动换行显示）——
        // 侧栏为自绘 View：来源可展开/折叠，标题过长时自动换行（CJK 宽度感知）
        var tree = new SidebarView(feedId => LoadArticleNodes(feedId, dbPath, TuiArticleLoadLimit))
        {
            X = 0,
            Y = 0,
            Width = Dim.Percent(24),
            Height = Dim.Fill() - 3,
            CanFocus = true,
            BorderStyle = LineStyle.Single,
            Title = " " + Lang.T("Feeds") + " (C " + Lang.T("collapse") + ") "
        };
        tree.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.Black),
            HotNormal = new Terminal.Gui.Drawing.Attribute(StandardColor.BrightYellow, StandardColor.Black),
            // 聚焦时选中行用清晰的亮青反色；正文区聚焦（阅读中）时选中行柔和变暗，不抢注意力
            Active = new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.DarkCyan),
            HotActive = new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.DarkCyan),
            Focus = new Terminal.Gui.Drawing.Attribute(StandardColor.Black, StandardColor.BrightCyan),
            HotFocus = new Terminal.Gui.Drawing.Attribute(StandardColor.Black, StandardColor.BrightCyan),
            Highlight = new Terminal.Gui.Drawing.Attribute(StandardColor.Black, StandardColor.BrightCyan),
            ReadOnly = new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.Black)
        });

        // —— 中间垂直分隔线 ——
        var vDivider = new Line
        {
            Orientation = Orientation.Vertical,
            Style = LineStyle.Single,
            X = Pos.Right(tree) + 1,
            Y = 0,
            Height = Dim.Fill() - 3
        };

        // —— 右侧：正文预览（Markdown 渲染：标题/粗体/斜体/删除线/分隔线/列表/图片）——
        var contentView = CreateMarkdownView();
        contentView.X = Pos.Right(tree) + 2;
        contentView.Y = 0;
        contentView.Width = Dim.Fill();
        contentView.Height = Dim.Fill() - 3;
        contentView.CanFocus = true;
        contentView.BorderStyle = LineStyle.Single;
        contentView.Title = " " + Lang.T("Content") + " ";

        // 侧栏折叠状态：按 C 折叠左侧栏，正文区扩张（再按 C 恢复）
        bool sidebarCollapsed = false;
        void ToggleSidebar()
        {
            sidebarCollapsed = !sidebarCollapsed;
            tree.Visible = !sidebarCollapsed;
            vDivider.Visible = !sidebarCollapsed;
            if (sidebarCollapsed) contentView.X = 0;
            else contentView.X = Pos.Right(tree) + 2;
            UpdateLinkNavTitle();
            contentView.SetFocus();
        }

        // 沉浸阅读状态（ToggleImmersive 定义在 statusBar 之后，因为要用到它）
        bool immersive = false;

        // 底部命令行：平时隐藏，按 Esc 唤出，Enter 执行后隐藏，再按 Esc 隐藏
        var cmdBar = new TextField
        {
            X = 1,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(1),
            Height = 1,
            CanFocus = true,
            Text = "",
            Secret = false,
            Visible = false
        };
        var cmdLabel = new Label
        {
            Text = ":",
            X = 0,
            Y = Pos.AnchorEnd(2),
            CanFocus = false,
            Visible = false
        };

        // 主窗口（先于 UpdateStats 声明，后者会更新窗口标题）
        var top = new Window
        {
            Title = " sip RSS Reader ",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        // 状态行（命令行隐藏时显示）：源数 · 文章位置/总数
        var statsLabel = new Label
        {
            Text = "",
            X = 1,
            Y = Pos.AnchorEnd(2),
            CanFocus = false,
            Visible = true
        };
        // —— 阅读进度记忆（按文章记住滚动位置；文件存储，零改表）——
        // 变量声明必须在 UpdateStats 之前（局部变量不能前向引用）
        var progressMap = LoadReadingProgress();
        long _currentArticleId = 0;
        int _savedScrollY = -1;   // 打开文章时若检测到历史进度，存这里；-1 = 无
        bool _pageFlipMode = false;   // false=滚动模式,true=翻页模式(PDF 强制 true)
        bool _isPdfArticle = false;   // 当前文章是否为 PDF 导入(强制翻页,不可切换)

        // —— Telemetry 阅读状态（仅内存，会话内）——
        double _maxProgress = 0;      // 当前文章最大进度 0-1
        DateTime _lastActivity = default;
        double _activeSeconds = 0;    // 当前文章活跃阅读秒数（空档不计）
        int _estimatedSeconds = 0;    // 预估阅读时长 ERT
        int _lastMilestone = 0;       // 已上报里程碑 0/25/50/75/100
        // 活动事件时累计活跃时间：空档超过 ERT×25%（10~120s）不计入
        void TelemetryActivityTick()
        {
            if (_currentArticleId == 0) return;
            var now = DateTime.Now;
            if (_lastActivity == default) { _lastActivity = now; return; }
            double gap = (now - _lastActivity).TotalSeconds;
            double idleThreshold = Math.Clamp(_estimatedSeconds * 0.25, 10, 120);
            if (gap <= idleThreshold) _activeSeconds += gap;
            _lastActivity = now;
        }
        // 打开文章：记录 open + 按内容长度算 ERT + 初始化计时
        void TelemetryOpenArticle(long itemId, int feedId)
        {
            TelemetryService.Record("article_open", articleId: (int)itemId, sourceId: feedId);
            int chars = 0;
            try
            {
                using var conn = OpenDb(dbPath);
                conn.Open();
                var c = conn.CreateCommand();
                c.CommandText = "SELECT LENGTH(COALESCE(Content,'')), LENGTH(COALESCE(Description,'')) FROM Items WHERE Id = @id";
                c.Parameters.AddWithValue("@id", itemId);
                using var r = c.ExecuteReader();
                if (r.Read()) chars = Math.Max(r.GetInt32(0), r.GetInt32(1));
            }
            catch { }
            _estimatedSeconds = Math.Max(10, chars / 5);
            _lastActivity = DateTime.Now;
            _activeSeconds = 0;
            _maxProgress = 0;
            _lastMilestone = 0;
        }
        // 进度更新：里程碑 25/50/75/100 + 滚到底 = complete（带 active/estimated/time_ratio）
        void TelemetryProgressTick(double ratio)
        {
            if (ratio > _maxProgress) _maxProgress = ratio;
            if (_maxProgress <= 0) return;
            if (ratio >= 1.0 && _lastMilestone < 100)
            {
                _lastMilestone = 100;
                TelemetryService.Record("article_complete", articleId: (int)_currentArticleId,
                    data: new { active_seconds = Math.Round(_activeSeconds, 1), estimated_seconds = _estimatedSeconds,
                               time_ratio = Math.Round(_estimatedSeconds > 0 ? _activeSeconds / _estimatedSeconds : 0, 3),
                               max_progress = Math.Round(_maxProgress, 3) });
                return;
            }
            int ms = (int)(Math.Min(ratio, 0.999) * 100 / 25) * 25;
            if (ms > _lastMilestone)
            {
                _lastMilestone = ms;
                TelemetryService.Record("article_progress", articleId: (int)_currentArticleId,
                    data: new { progress = ms / 100.0, max_progress = Math.Round(_maxProgress, 3) });
            }
        }
        // 离开当前文章：progress < 10% 记 skip（主动离开才触发）；否则补记最终进度
        void TelemetryCloseArticle()
        {
            if (_currentArticleId == 0) return;
            if (_maxProgress < 0.10)
            {
                TelemetryService.Record("article_skip", articleId: (int)_currentArticleId,
                    data: new { progress = Math.Round(_maxProgress, 3) });
            }
            else if (_maxProgress > 0 && _lastMilestone < 100)
            {
                TelemetryService.Record("article_progress", articleId: (int)_currentArticleId,
                    data: new { progress = Math.Round(_maxProgress, 3), max_progress = Math.Round(_maxProgress, 3) });
            }
        }

        // 全库计数缓存:在 RebuildTree 时一次算好。
        // UpdateStats 只读缓存——百万级库的 COUNT(*) 需 200ms+,每次按键都查会让键盘不跟手
        int _statsFeeds = 0, _statsArticles = 0;

        void UpdateStats()
        {
            // 检测到阅读进度时，状态行优先显示跳转提示（标题栏会截断，这里更显眼）
            if (_savedScrollY > 0)
            {
                statsLabel.Text = Lang.T("▷ 按 Space 跳回上次位置");
                return;
            }
            var (cur, tot) = tree.ArticlePosition();
            string modeHint = _isPdfArticle ? "[PDF]" : (_pageFlipMode ? "[Flip]" : "[Scroll]");
            statsLabel.Text = Lang.T("feeds {0} · article {1}/{2} · {3}", _statsFeeds, cur, Math.Max(_statsArticles, tot), modeHint);
            top.Title = $" sip RSS Reader · {Lang.T("feeds {0}", _statsFeeds)} ";
        }

        // 正文/概要模式 + 链接导航状态（供状态栏快捷键引用）
        bool contentMode = true;     // true=完整正文，false=文章概要
        bool linkNavMode = false;
        bool _syncing = false;       // 到期源自动同步进行中（防重入）
        int linkNavIndex = 0;

        // —— 阅读进度：保存 / 跳转 / 退出（函数必须在变量声明之后）——
        void SaveCurrentScroll()
        {
            if (_currentArticleId == 0) return;
            try { progressMap[_currentArticleId] = contentView.Viewport.Y; } catch { }
            // 遥测进度：按滚动位置算 ratio，驱动里程碑/complete
            try
            {
                int h = contentView.GetContentHeight();
                if (h > 0) TelemetryProgressTick(Math.Clamp(contentView.Viewport.Y / (double)h, 0, 1.0));
            }
            catch { }
        }
        // 跳到上次阅读位置（按 Space 触发）：对进度做边界校验，绝不跳到负数或超出正文范围
        void JumpToSaved()
        {
            if (_savedScrollY <= 0) return;
            try
            {
                int maxY = Math.Max(0, contentView.GetContentHeight() - contentView.Viewport.Height);
                int y = Math.Clamp(_savedScrollY, 0, maxY);
                contentView.ScrollVertical(y);
                _savedScrollY = -1;
                SaveCurrentScroll();
                UpdateStats();
                UpdateLinkNavTitle();
            }
            catch { _savedScrollY = -1; }
        }
        // 退出前保存并落盘（必须在 RequestStop 之前调，否则 Viewport 已归 0）
        void QuitApp()
        {
            SaveCurrentScroll();
            TelemetryCloseArticle();   // 主动退出，低进度记 skip
            SaveReadingProgress(progressMap);
            top.RequestStop();
        }

        // 状态栏快捷操作（全键盘，键位对齐外部 CLI）
        var statusBar = new StatusBar(new Shortcut[]
        {
            new Shortcut(Key.H, Lang.T("Help"), () => ShowHelpDialog(), Lang.T("Show all keybindings")),
            new Shortcut(Key.F2, Lang.T("About"), () => ShowAboutDialog(), Lang.T("About sip")),
            new Shortcut(Key.U, Lang.T("Update"), () => RefreshSelectedFeed(), Lang.T("Update selected feed (same as CLI -u)")),
            new Shortcut(Key.F6, Lang.T("Update all"), () => RefreshAllFeeds(), Lang.T("Update all feeds")),
            new Shortcut(Key.A, Lang.T("Archive"), () => ArchiveSelectedFeed(), Lang.T("Add timestamp to feed (same as CLI -a)")),
            new Shortcut(Key.R, Lang.T("Unarchive"), () => UnarchiveSelectedFeed(), Lang.T("Remove timestamp (same as CLI -una)")),
            new Shortcut(Key.X, Lang.T("Delete"), () => DeleteSelected(), Lang.T("Delete selected feed/article (same as CLI -r)")),
            new Shortcut(Key.D, Lang.T("Add"), () => AddFeedDialog(), Lang.T("Add new feed (same as CLI -d)")),
            new Shortcut(Key.S, Lang.T("Search"), () => SearchDialog(), Lang.T("Semantic search (same as CLI --search)")),
            new Shortcut(Key.Y, Lang.T("Summary"), () => SummarizeSelected(), Lang.T("Summarize current article (same as CLI --summary)")),
            new Shortcut(Key.G, Lang.T("Overview"), () => ToggleContentMode(), Lang.T("Toggle content/overview")),
                new Shortcut(Key.P, Lang.T("Report"), () => ShowInsightsPage(dbPath), Lang.T("Reading report (needs telemetry ON)")),
                new Shortcut(Key.Q, Lang.T("Quit"), QuitApp, Lang.T("Exit program"))
        });

        top.Add(tree, vDivider, contentView, cmdLabel, cmdBar, statsLabel, statusBar);

        // 沉浸阅读：隐藏侧栏/分隔线/状态栏/状态行，正文占满全屏；再按 i 恢复
        void ToggleImmersive()
        {
            immersive = !immersive;
            tree.Visible = !immersive && !sidebarCollapsed;
            vDivider.Visible = !immersive;
            statusBar.Visible = !immersive;
            statsLabel.Visible = !immersive && !cmdBar.Visible;
            cmdBar.Visible = false;
            cmdLabel.Visible = false;
            if (immersive) contentView.X = 0;
            else contentView.X = sidebarCollapsed ? 0 : Pos.Right(tree) + 2;
            UpdateLinkNavTitle();
            contentView.SetFocus();
        }

        // —— 侧栏宽度自适应：宽屏固定列宽（正文更宽更好读），窄屏退回比例 ——
        const int WideSidebarWidth = 32;
        const int WideThreshold = 130;   // 终端宽度 ≥ 此列数时用固定列宽
        void ApplyResponsiveSidebar()
        {
            tree.Width = top.Frame.Width >= WideThreshold ? Dim.Absolute(WideSidebarWidth) : Dim.Percent(24);
        }
        top.FrameChanged += (s, e) => ApplyResponsiveSidebar();
        ApplyResponsiveSidebar();

        void RebuildTree()
        {
            var feeds = new List<TuiNode>();
            int totalActive = 0;
            using var conn = OpenDb(dbPath);
            conn.Open();
            var cmd = conn.CreateCommand();
            // 一次 GROUP BY 拿全部源的计数(原每源 3 个相关子查询,百万库 100 源要扫 300 万行)
            cmd.CommandText = @"
                SELECT f.Id, f.Title,
                       COUNT(i.Id) FILTER (WHERE i.Status = 'active')   AS ActiveCount,
                       COUNT(i.Id) FILTER (WHERE i.Status = 'archived') AS ArchiveCount,
                       COUNT(i.Id) FILTER (WHERE i.Status = 'deleted')  AS DeleteCount
                FROM Feeds f
                LEFT JOIN Items i ON i.FeedId = f.Id
                GROUP BY f.Id
                ORDER BY f.Id
            ";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                int id = r.GetInt32(0);
                string title = r.GetString(1);
                int active = r.GetInt32(2);
                int archive = r.GetInt32(3);
                int deleted = r.GetInt32(4);
                totalActive += active;
                var parts = new List<string>();
                if (active > 0) parts.Add(Lang.T("{0} current", active + deleted));
                if (archive > 0) parts.Add(Lang.T("{0} changed", archive));
                if (deleted > 0) parts.Add(Lang.T("{0} deleted by author, but archived for you", deleted));
                string stats = string.Join("，", parts);
                feeds.Add(new TuiNode { IsFeed = true, FeedId = id, Title = $"{CjkSpace(title)} {stats}" });
            }
            _statsFeeds = feeds.Count;
            _statsArticles = totalActive;
            tree.SetFeeds(feeds);   // 默认折叠；用户展开的源在 SetFeeds 里会保留
            UpdateStats();
        }

        void ShowSelectedContent()
        {
            SaveCurrentScroll();                       // 先记住上一篇的位置
            var n = tree.SelectedObject;
            if (n == null || n.IsFeed)
            {
                TelemetryCloseArticle();               // 从文章切到源/空 → 主动离开
                contentView.Text = ""; _currentArticleId = 0; _savedScrollY = -1; UpdateStats();
                return;
            }
            if (n.ItemId != _currentArticleId)
            {
                TelemetryCloseArticle();               // 主动切换 → 低进度记 skip
                _lastBaseUrl = GetArticleLink(n.ItemId, dbPath);
                contentView.Text = BuildArticleMarkdown(n.ItemId, contentMode, dbPath, contentView.GetContentWidth(), showFetchHint: true);
                _currentArticleId = n.ItemId;
                _isPdfArticle = _lastBaseUrl.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
                if (_isPdfArticle) _pageFlipMode = true;   // PDF 强制翻页
                TelemetryOpenArticle(n.ItemId, n.FeedId);   // article_open + 计时初始化
            }
            else
            {
                _lastBaseUrl = GetArticleLink(n.ItemId, dbPath);
                contentView.Text = BuildArticleMarkdown(n.ItemId, contentMode, dbPath, contentView.GetContentWidth(), showFetchHint: true);
            }
            // 文章渲染完立刻把图片丢给后台预取。渲染线程不等网络/解码,
            // 图到货后由 SipMarkdown.OnImageReady 通知重绘。
            PrefetchArticleImages(contentView.Text, _lastBaseUrl);
            // 检测到历史进度 → 提示（不自动跳，等用户按 Space）；非法值直接忽略
            _savedScrollY = progressMap.TryGetValue(n.ItemId, out int y) && y > 0 ? y : -1;
            UpdateStats();                             // 有进度时状态行显示跳转提示
            UpdateLinkNavTitle();
        }

        // 在正文区显示某个历史版本的内容
        void ShowSelectedVersion(long itemId, int version)
        {
            contentMode = true;   // 历史版本固定用完整正文
            contentView.Text = BuildArticleMarkdown(itemId, true, dbPath, contentView.GetContentWidth());
            PrefetchArticleImages(contentView.Text, _lastBaseUrl);
            contentView.Title = " " + Lang.T("Content") + " · v" + version + " ";
            contentView.SetFocus();
        }

        // V：查看当前文章的版本历史 / 变更（列出所有版本，可输入编号查看旧版正文）
        void ShowVersionHistory(TuiNode n)
        {
            if (n == null || n.IsFeed || string.IsNullOrEmpty(n.Guid)) return;

            var versions = new List<(long Id, int Version, string Status, string At)>();
            using (var conn = OpenDb(dbPath))
            {
                conn.Open();
                var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Id, Version, Status, ArchivedAt FROM Items WHERE Guid = @g ORDER BY Version DESC";
                cmd.Parameters.AddWithValue("@g", n.Guid);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    versions.Add((r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3)));
            }

            if (versions.Count <= 1)
            {
                Ask(Lang.T("This article has only one version, no change history"), Lang.T("OK"));
                return;
            }

            var sb = new StringBuilder();
            for (int i = 0; i < versions.Count; i++)
            {
                var (_, ver, status, at) = versions[i];
                string tag = status switch
                {
                    "active" => Lang.T("current"),
                    "archived" => Lang.T("archived"),
                    "deleted" => Lang.T("deleted"),
                    _ => ""
                };
                string when = at.Length > 0 && TryParseIso(at) is DateTime dt ? " · " + dt.ToString("yyyy-MM-dd HH:mm") : "";
                sb.AppendLine($"{i + 1}.  v{ver}  {tag}{when}");
            }
            sb.AppendLine();
            sb.AppendLine(Lang.T("Enter a number to view that version, 0 to cancel"));

            var dlg = new Dialog { Title = " " + Lang.T("Version History") + " ", Width = 60, Height = 14 };
            var txt = new TextView { X = 0, Y = 0, Width = Dim.Fill(2), Height = 9, ReadOnly = true, CanFocus = false };
            txt.Text = sb.ToString();
            var input = new TextField { X = 0, Y = Pos.Bottom(txt), Width = 5, Text = "" };
            var ok = new Button { Text = Lang.T("View"), IsDefault = true, X = 0, Y = Pos.Bottom(input) + 1 };
            var cancel = new Button { Text = Lang.T("Cancel"), X = Pos.Right(ok) + 1, Y = Pos.Bottom(input) + 1 };
            // input 第一个加入 + 列表只读不抢焦点 → 打开对话框时光标就在输入框上，直接敲数字即可
            dlg.Add(input, txt, ok, cancel);
            ok.Accepted += (s, e) => dlg.RequestStop();
            cancel.Accepted += (s, e) => { input.Text = ""; dlg.RequestStop(); };
            input.Initialized += (s, e) => input.SetFocus();
            Application.Run(dlg);

            if (int.TryParse(input.Text.Trim(), out int idx) && idx >= 1 && idx <= versions.Count)
            {
                var (id2, ver2, _, _) = versions[idx - 1];
                ShowSelectedVersion(id2, ver2);
            }
        }

        int GetSelectedFeedId()
        {
            var n = tree.SelectedObject;
            return n?.FeedId ?? 0;
        }

        TuiNode? GetSelected() => tree.SelectedObject;

        void ArchiveSelectedFeed()
        {
            int realId = GetSelectedFeedId();
            if (realId == 0) return;
            AddTimestampForRealId(realId, dbPath);
            RebuildTree();
        }

        void UnarchiveSelectedFeed()
        {
            int realId = GetSelectedFeedId();
            if (realId == 0) return;
            RemoveTimestampForRealId(realId, dbPath);
            RebuildTree();
        }

        void DeleteSelected()
        {
            var n = GetSelected();
            if (n == null) return;
            if (n.IsFeed)
            {
                // 删除源（同 CLI -r）
                int ans = Ask(Lang.T("Delete {0}? This cannot be undone! (y/n)", n.Title),
                    Lang.T("OK"), Lang.T("Cancel"));
                if (ans != 0) return;
                DeleteFeedByRealId(n.FeedId, dbPath);
                RebuildTree();
                contentView.Text = "";
            }
            else
            {
                // 删除整篇文章（该 Guid 的全部版本，含向量）
                int ans = Ask(Lang.T("Delete this article (with all its versions)? This cannot be undone!"), Lang.T("OK"), Lang.T("Cancel"));
                if (ans != 0) return;
                DeleteArticleByGuid(n.Guid, dbPath);
                RebuildTree();
                contentView.Text = "";
            }
        }

        void RefreshSelectedFeed()
        {
            int realId = GetSelectedFeedId();
            if (realId == 0) return;
            RunNetworkOp(() => RefreshOneFeed(realId, dbPath));
        }

        void RefreshAllFeeds()
        {
            using var conn = OpenDb(dbPath);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, FeedUrl FROM Feeds";
            using var r = cmd.ExecuteReader();
            var list = new List<(int Id, string Url)>();
            while (r.Read())
                list.Add((r.GetInt32(0), r.IsDBNull(1) ? "" : r.GetString(1)));
            RunNetworkOp(() =>
            {
                foreach (var f in list)
                    if (!string.IsNullOrWhiteSpace(f.Url))
                        try { DownloadAndSaveToDb(f.Url, dbPath).Wait(); } catch { }
            });
        }

        // 网络/耗时操作：弹出居中进度对话框，把 Console 输出重定向到对话框内实时显示，
        // 完成后自动关闭并重建树（不污染正文区）
        void RunNetworkOp(Action op)
        {
            var sb = new StringBuilder();
            var outTxt = new TextView
            {
                X = 0, Y = 0, Width = Dim.Fill(2), Height = Dim.Fill(2),
                ReadOnly = true, WordWrap = true, ScrollBars = true
            };
            var dlg = new Dialog { Title = " " + Lang.T("Working") + " ", Width = 64, Height = 18 };
            dlg.Add(outTxt);

            TextWriter oldOut = Console.Out;
            var writer = new StringWriter(sb);
            Console.SetOut(writer);
            object lockObj = new();
            bool done = false;

            // 后台线程执行操作，避免卡住 UI 刷新
            Task.Run(() =>
            {
                try { op(); }
                catch (Exception ex) { lock (lockObj) sb.AppendLine(Lang.T("Error: {0}", ex.Message)); }
                finally { lock (lockObj) { done = true; sb.AppendLine(); } }
            });

            // 定时把缓冲内容刷到对话框；完成后自动关闭
            Application.AddTimeout(TimeSpan.FromMilliseconds(120), () =>
            {
                lock (lockObj) outTxt.Text = sb.ToString();
                if (done)
                {
                    Console.SetOut(oldOut);
                    dlg.RequestStop();
                    return false;  // 停止定时器
                }
                return true;
            });

            Application.Run(dlg);  // 等后台完成
            Console.SetOut(oldOut);
            RebuildTree();
        }

        void AddFeedDialog()
        {
            // 输入 URL 添加新源（同 CLI -d <url>）
            var dlg = new Dialog { Title = " " + Lang.T("Add feed") + " " };
            var lbl = new Label { Text = Lang.T("RSS URL: "), X = 0, Y = 0 };
            var input = new TextField { X = 0, Y = 1, Width = Dim.Fill(2), Text = "" };
            var ok = new Button { Text = Lang.T("OK"), IsDefault = true, X = 0, Y = 3 };
            var cancel = new Button { Text = Lang.T("Cancel"), X = Pos.Right(ok) + 1, Y = 3 };
            dlg.Add(lbl, input, ok, cancel);
            dlg.Width = 60;
            dlg.Height = 7;

            ok.Accepted += (s, e) => dlg.RequestStop();
            cancel.Accepted += (s, e) => { input.Text = ""; dlg.RequestStop(); };

            Application.Run(dlg);
            string url = input.Text.Trim();
            if (string.IsNullOrWhiteSpace(url)) return;
            RunNetworkOp(() => { DownloadAndSaveToDb(url, dbPath).Wait(); });
        }

        void SearchDialog()
        {
            if (!File.Exists(ConfigPath(dbPath)))
            {
                Ask(Lang.T("AI not configured. Run 'sip --init' in the terminal first"), Lang.T("OK"));
                return;
            }
            var dlg = new Dialog { Title = " " + Lang.T("Semantic search") + " " };
            var lbl = new Label { Text = Lang.T("Search for: "), X = 0, Y = 0 };
            var input = new TextField { X = 0, Y = 1, Width = Dim.Fill(2), Text = "" };
            var ok = new Button { Text = Lang.T("Search"), IsDefault = true, X = 0, Y = 3 };
            var cancel = new Button { Text = Lang.T("Cancel"), X = Pos.Right(ok) + 1, Y = 3 };
            dlg.Add(lbl, input, ok, cancel);
            dlg.Width = 60;
            dlg.Height = 7;

            ok.Accepted += (s, e) => dlg.RequestStop();
            cancel.Accepted += (s, e) => { input.Text = ""; dlg.RequestStop(); };

            Application.Run(dlg);
            string q = input.Text.Trim();
            if (string.IsNullOrWhiteSpace(q)) return;

            // 复用语义搜索，渲染带链接的结果
            DoTuiSearch(q);
        }

        void SummarizeSelected()
        {
            var n = GetSelected();
            if (n == null || n.IsFeed)
            {
                Ask(Lang.T("Select an article first to summarize it"), Lang.T("OK"));
                return;
            }
            if (!File.Exists(ConfigPath(dbPath)))
            {
                Ask(Lang.T("AI not configured. Run 'sip --init' in the terminal first"), Lang.T("OK"));
                return;
            }
            long itemId = n.ItemId;
            RunNetworkOp(() => SummarizeItem(dbPath, (int)itemId).Wait());
            ShowSelectedContent();
        }

        void ShowHelpDialog()
        {
            // 全屏对话框:内容一屏放下,不需要滚动;初始焦点给 OK 按钮
            // (焦点在按钮上时 ←/→ 切换按钮、Enter 确认——避免焦点落在 TextView 上方向键全被吞)
            var dlg = new Dialog { Title = " " + Lang.T("Keyboard help") + " ", Width = Dim.Fill(), Height = Dim.Fill() };
            var txt = new TextView
            {
                X = 0, Y = 0, Width = Dim.Fill(2), Height = Dim.Fill(2),
                ReadOnly = true, WordWrap = false
            };
            txt.Text = string.Join("\n",
                Lang.T("j/k ↑↓    move up/down"),
                Lang.T("l/Enter   open article / toggle feed"),
                Lang.T("←         back (to sidebar)"),
                Lang.T("Space/b   page down/up  ·  Ctrl+D/U half page"),
                Lang.T("i         immersive reading (hide all UI)"),
                Lang.T("U          update current feed"),
                Lang.T("F6         update all feeds"),
                Lang.T("A          archive current feed"),
                Lang.T("R          unarchive"),
                Lang.T("X          delete selected feed/article"),
                Lang.T("D          add new feed"),
                Lang.T("S          semantic search"),
                Lang.T("Y          summarize article"),
                Lang.T("G          toggle content/overview"),
                Lang.T("V          view article versions/changes (marked ✎)"),
                Lang.T("C          collapse/expand sidebar"),
                Lang.T("Esc        open command line"),
                Lang.T("H          show this help"),
                Lang.T("Q          quit"),
                Lang.T("← / →      switch sidebar/content"),
                Lang.T("PageUp/Dn  page up/down"),
                "",
                Lang.T("Auto-sync: on open + every 15 min, only 'due' feeds (set frequency with schedule)"),
                Lang.T("Commands: init / index / reindex / u / d / a / r / s / g / y / q"),
                Lang.T("           schedule <id> <expr> (e.g. 30m / daily@10:00 / manual)"),
                Lang.T("           sync / all"),
                Lang.T("           lang <code> (switch UI language, e.g. zh-CN / en-US)"),
                Lang.T("           diff <id> / export <id|feed:N|all> / export-opml / import-opml <file>"),
                Lang.T("           feed-info <id> / like <id> / likes / purge-fulltext [id]"),
                Lang.T("           pic (toggle article images; off by default — sixel detection is unreliable)"),
                Lang.T("           dedup（无参=交互选择） / dedup scan|list|undo / insights-interval <7d|30d|off> / telemetry ... / config"));
            var ok = new Button { Text = Lang.T("OK"), IsDefault = true, X = 0, Y = Pos.Bottom(txt) };
            var about = new Button { Text = Lang.T("About"), X = Pos.Right(ok) + 1, Y = Pos.Bottom(txt) };
            dlg.Add(txt, ok, about);
            ok.Accepted += (s, e) => dlg.RequestStop();
            about.Accepted += (s, e) => { dlg.RequestStop(); ShowAboutDialog(); };
            dlg.Initialized += (s, e) => ok.SetFocus();   // 焦点给按钮,方向键/Enter 可用
            Application.Run(dlg);
        }

        void ShowAboutDialog()
        {
            var dlg = new Dialog { Title = " " + Lang.T("About") + " ", Width = Dim.Fill(), Height = Dim.Fill() };
            var txt = new TextView
            {
                X = 0, Y = 0, Width = Dim.Fill(2), Height = Dim.Fill(2),
                ReadOnly = true, WordWrap = true
            };
            var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";
            string build = "";
            try { build = new FileInfo(Environment.ProcessPath ?? "").LastWriteTime.ToString("yyyy-MM-dd HH:mm"); } catch { }
            txt.Text = string.Join("\n",
                Lang.T("🍲 sip"),
                Lang.T("——「品，你细品。」"),
                Lang.T("一个本地优先的透明信息过滤器与阅读辅助器。"),
                "",
                Lang.T("版本 v{0}", ver),
                Lang.T("构建时间：{0}", build),
                "",
                Lang.T("作者：hahahotsoup"),
                Lang.T("博客：https://blog.hotsouprealm.top/"),
                "",
                Lang.T("🔒 孟思琳正保护着你的软件哦"));
            var ok = new Button { Text = Lang.T("OK"), IsDefault = true, X = 0, Y = Pos.Bottom(txt) };
            dlg.Add(txt, ok);
            ok.Accepted += (s, e) => dlg.RequestStop();
            dlg.Initialized += (s, e) => ok.SetFocus();
            Application.Run(dlg);
        }

        // 通用确认/提示对话框，返回按钮索引（0 = 第一个按钮）
        int Ask(string message, params string[] buttons)
        {
            var btns = buttons.Length > 0 ? buttons : new[] { Lang.T("OK") };
            return MessageBox.Query(Application.Instance, Lang.T("Notice"), message, btns) ?? 0;
        }

        // 在浏览器/默认程序中打开链接（仅放行 http/https，防 javascript: 等注入）
        void OpenUrl(string url)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u)
                    || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
                {
                    Ask(Lang.T("Unsupported link scheme, not opened: {0}", url), Lang.T("OK"));
                    return;
                }
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                Ask(Lang.T("Failed to open link: {0}", ex.Message), Lang.T("OK"));
            }
        }

        // 进入/退出链接导航模式
        void ToggleLinkNav()
        {
            if (TuiMdState.Links.Count == 0)
            {
                Ask(Lang.T("This article has no openable links"), Lang.T("OK"));
                return;
            }
            linkNavMode = !linkNavMode;
            linkNavIndex = 0;
            UpdateLinkNavTitle();
            if (linkNavMode) contentView.SetFocus();
        }

        void UpdateLinkNavTitle()
        {
            string extra = linkNavMode && TuiMdState.Links.Count > 0
                ? $"  [ {linkNavIndex + 1}/{TuiMdState.Links.Count} ]  {TuiMdState.Links[linkNavIndex].Text}"
                : "";
            string modeTag = immersive ? Lang.T("Immersive") : (contentMode ? Lang.T("Content") : Lang.T("Overview"));
            if (sidebarCollapsed && !immersive) modeTag = "◀ " + modeTag;
            string focusTag = contentView.HasFocus ? " ◉" : "";
            contentView.Title = " " + modeTag + focusTag + (linkNavMode ? " (链接模式)" : "") + extra + " ";
        }

        void ToggleContentMode()
        {
            contentMode = !contentMode;
            UpdateLinkNavTitle();
            ShowSelectedContent();
            contentView.SetFocus();
        }

        void OpenCurrentLink()
        {
            if (!linkNavMode || TuiMdState.Links.Count == 0) return;
            var (text, url) = TuiMdState.Links[linkNavIndex];
            int ans = Ask(Lang.T("Open link?\n{0}\n{1}", text, url), Lang.T("Open"), Lang.T("Cancel"));
            if (ans == 0) OpenUrl(url);
        }

        // —— 事件绑定 ——
        tree.SelectionChanged += (s, e) => ShowSelectedContent();
        // 焦点变化时刷新正文标题栏的 ◉ 焦点标记（阅读区聚焦不再整块变色，靠它指示）
        contentView.HasFocusChanged += (s, e) => UpdateLinkNavTitle();

        // 鼠标点击正文中的链接直接打开
        contentView.LinkClicked += (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Url)) OpenUrl(e.Url);
            e.Handled = true;
        };

        // 侧栏：j/k 上下移动，l/Enter 展开源或打开文章，Space/b 翻页，C 折叠侧栏，
        //       i 沉浸阅读，V 版本，Esc 命令行
        tree.KeyDown += (s, e) =>
        {
            var n = tree.SelectedObject;
            if (e.KeyCode == KeyCode.Enter || e.KeyCode == KeyCode.L || e.KeyCode == KeyCode.Space)
            {
                if (n != null && n.IsFeed) tree.Toggle(n);
                else contentView.SetFocus();   // Space：直接跳到正文页
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.CursorRight)
            {
                if (n is { IsFeed: false }) contentView.SetFocus();
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.CursorDown || e.KeyCode == KeyCode.J)
            {
                tree.MoveDown();
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.CursorUp || e.KeyCode == KeyCode.K)
            {
                tree.MoveUp();
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.PageUp || e.KeyCode == KeyCode.B)
            {
                tree.MovePageUp();
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.PageDown)
            {
                tree.MovePageDown();
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.C)
            {
                ToggleSidebar();
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.I)
            {
                ToggleImmersive();
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.M)
            {
                ShowFeedManager(dbPath);
                RebuildTree();
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.V)
            {
                if (n is { IsFeed: false } && n.HasHistory) ShowVersionHistory(n);
                else Ask(Lang.T("This article has no change history (only ones marked ✎ have it)"), Lang.T("OK"));
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.Esc)
            {
                ShowCmdBar();
                e.Handled = true;
            }
        };

        // 正文栏：← 返回树；j/k/↑↓ 平滑滚动；Space/b/PageUp/PageDown 翻页；Ctrl+D/Ctrl+U 半页；
        //       l/Enter 打开当前链接；i 沉浸阅读；C 折叠侧栏；V 版本；Esc 命令行
        // 链接导航：Ctrl+O 进入/退出，Tab/Shift+Tab 切换链接，Enter/l 打开当前链接
        // 翻页模式:不再自由滚动,改为按整屏高度吸附 + 翻页整屏清。
        // 整屏清(ESC[2J)能清掉上一页的 sixel 像素(普通 FillRect 清不掉),
        // 这是从根上消除"滚动黑屏/条纹/残影"这类 bug 的关键。
        void ClearScreenForPageFlip()
        {
            try
            {
#pragma warning disable CS0618   // legacy 静态入口,本项目整体仍用
                var drv = Terminal.Gui.App.Application.Driver;
                if (drv != null)
                {
                    // 直接往终端写 ESC[2J + 光标归位,清掉上一页的 sixel 帧缓冲(普通 FillRect 清不掉)。
                    // WriteRaw 是 IDriver 原生方法,能透传原始转义序列。
                    string seq = EscSeqUtils.CSI_ClearScreen(EscSeqUtils.ClearScreenOptions.EntireScreen).ToString()
                               + EscSeqUtils.CSI_SetCursorPosition(1, 1).ToString();
                    drv.WriteRaw(seq);
                    Terminal.Gui.App.Application.LayoutAndDraw(true);
                }
#pragma warning restore CS0618
            }
            catch { /* 清屏失败绝不阻断翻页 */ }
        }

        void PageFlip(Terminal.Gui.ViewBase.View cv, int dir)
        {
            int vh = cv.Viewport.Height;
            if (vh <= 0) return;
            int maxY = Math.Max(0, cv.GetContentHeight() - vh);
            int cur = cv.Viewport.Y;
            int target = dir > 0 ? Math.Min(maxY, cur + vh) : Math.Max(0, cur - vh);
            if (target == cur) return;   // 已在首页/末页
            cv.ScrollVertical(target - cur);
            SaveCurrentScroll();
            ClearScreenForPageFlip();
        }

        contentView.KeyDown += (s, e) =>
        {
            switch (e.KeyCode)
            {
                case KeyCode.CursorLeft:
                    if (linkNavMode) { /* 链接模式下 ← 不抢 */ }
                    else if (immersive) ToggleImmersive();
                    else if (!sidebarCollapsed) tree.SetFocus();
                    e.Handled = true;
                    break;
                case KeyCode.Tab:
                    if (!_isPdfArticle)
                    {
                        _pageFlipMode = !_pageFlipMode;
                        UpdateStats();
                    }
                    e.Handled = true;
                    break;
                case KeyCode.CursorUp:
                case KeyCode.K:
                    if (_savedScrollY > 0) { _savedScrollY = -1; UpdateStats(); }   // 手动滚动 → 撤掉跳转提示
                    if (linkNavMode) { CycleLink(-1); }
                    else if (_pageFlipMode) { TelemetryActivityTick(); PageFlip(contentView, -1); }
                    else { TelemetryActivityTick(); contentView.ScrollVertical(-1); SaveCurrentScroll(); }
                    e.Handled = true;
                    break;
                case KeyCode.CursorDown:
                case KeyCode.J:
                    if (_savedScrollY > 0) { _savedScrollY = -1; UpdateStats(); }
                    if (linkNavMode) { CycleLink(1); }
                    else if (_pageFlipMode) { TelemetryActivityTick(); PageFlip(contentView, +1); }
                    else { TelemetryActivityTick(); contentView.ScrollVertical(1); SaveCurrentScroll(); }
                    e.Handled = true;
                    break;
                case KeyCode.PageUp:
                case KeyCode.B:
                    if (_savedScrollY > 0) { _savedScrollY = -1; UpdateStats(); }
                    TelemetryActivityTick();
                    if (_pageFlipMode) PageFlip(contentView, -1);
                    else { contentView.ScrollVertical(-6); SaveCurrentScroll(); }
                    e.Handled = true;
                    break;
                case KeyCode.PageDown:
                case KeyCode.Space:
                    if (_savedScrollY > 0) { JumpToSaved(); }   // 有历史进度 → Space 跳回
                    else { TelemetryActivityTick(); if (_pageFlipMode) PageFlip(contentView, +1); else { contentView.ScrollVertical(6); SaveCurrentScroll(); } }
                    e.Handled = true;
                    break;
                case KeyCode.Enter:
                case KeyCode.L:
                    if (linkNavMode) OpenCurrentLink();
                    e.Handled = true;
                    break;
                case KeyCode.C:
                    ToggleSidebar();
                    e.Handled = true;
                    break;
                case KeyCode.I:
                    ToggleImmersive();
                    e.Handled = true;
                    break;
                case KeyCode.M:
                    ShowFeedManager(dbPath);
                    RebuildTree();
                    e.Handled = true;
                    break;
                case KeyCode.P:
                    ShowInsightsPage(dbPath);
                    RebuildTree();
                    e.Handled = true;
                    break;
                case KeyCode.V:
                {
                    var nv = tree.SelectedObject;
                    if (nv is { IsFeed: false } && nv.HasHistory) ShowVersionHistory(nv);
                    else Ask(Lang.T("This article has no change history (only ones marked ✎ have it)"), Lang.T("OK"));
                    e.Handled = true;
                    break;
                }
                case KeyCode.Esc:
                    if (linkNavMode) { linkNavMode = false; UpdateLinkNavTitle(); }
                    else ShowCmdBar();
                    e.Handled = true;
                    break;
                default:
                    if (e.IsCtrl && e.KeyCode == (KeyCode.O | KeyCode.CtrlMask))
                    {
                        ToggleLinkNav();
                        e.Handled = true;
                    }
                    else if (e.IsCtrl && e.KeyCode == (KeyCode.Tab | KeyCode.CtrlMask))
                    {
                        if (linkNavMode) CycleLink(1);
                        e.Handled = true;
                    }
                    else if (e.IsCtrl && e.KeyCode == (KeyCode.D | KeyCode.CtrlMask))
                    {
                        // Ctrl+D：半页向下（vim 习惯）
                        if (_savedScrollY > 0) { _savedScrollY = -1; UpdateStats(); }
                        TelemetryActivityTick();
                        contentView.ScrollVertical(3);
                        SaveCurrentScroll();
                        e.Handled = true;
                    }
                    else if (e.IsCtrl && e.KeyCode == (KeyCode.U | KeyCode.CtrlMask))
                    {
                        // Ctrl+U：半页向上（vim 习惯）
                        if (_savedScrollY > 0) { _savedScrollY = -1; UpdateStats(); }
                        TelemetryActivityTick();
                        contentView.ScrollVertical(-3);
                        SaveCurrentScroll();
                        e.Handled = true;
                    }
                    else if (e.KeyCode == KeyCode.G && !e.IsCtrl)
                    {
                        // G：切换「完整正文 / 文章概要」
                        contentMode = !contentMode;
                        ShowSelectedContent();
                        e.Handled = true;
                    }
                    break;
            }
        };

        void CycleLink(int dir)
        {
            if (TuiMdState.Links.Count == 0) return;
            linkNavIndex = (linkNavIndex + dir + TuiMdState.Links.Count) % TuiMdState.Links.Count;
            UpdateLinkNavTitle();
        }

        void ShowCmdBar()
        {
            cmdBar.Visible = true;
            cmdLabel.Visible = true;
            statsLabel.Visible = false;
            cmdBar.Text = "";
            cmdBar.SetFocus();
        }

        void HideCmdBar()
        {
            cmdBar.Visible = false;
            cmdLabel.Visible = false;
            statsLabel.Visible = !immersive;
            cmdBar.Text = "";
            tree.SetFocus();
        }

        // 命令行：Enter 执行，Esc 隐藏
        cmdBar.KeyDown += (s, e) =>
        {
            if (e.KeyCode == KeyCode.Enter)
            {
                string input = cmdBar.Text.Trim();
                cmdBar.Text = "";
                HideCmdBar();
                if (input.Length > 0) RunCommand(input);
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.Esc)
            {
                HideCmdBar();
                e.Handled = true;
            }
        };

        // Telemetry 同意对话框（TUI，仅 unset 时询问一次；默认保持关闭）
        void EnsureTelemetryConsentTui()
        {
            if (TelemetryService.Consent != "unset") return;
            var dlg = new Dialog { Title = " " + Lang.T("苏暖泉") + " ", Width = 78, Height = 20 };
            var txt = new TextView { X = 0, Y = 0, Width = Dim.Fill(2), Height = 13, ReadOnly = true, CanFocus = false, WordWrap = true };
            txt.Text = Lang.T("苏暖泉是一个会主动了解你阅读习惯的软萌妹纸：她会记录哪些文章被打开/读完/跳过、AI 调用与搜索情况，用于未来改进内容筛选。\n\n苏暖泉默认不在。开启后数据仅保存在本机 telemetry.db，sip 绝不会自动上传；你随时可用 telemetry disable 关闭、export 导出。\n\n🔒 孟思琳(simon) 则永远在岗：数据库自愈、SSRF 防护、终端注入拦截、非交互调用管控——默认开启且无法关闭，只能调节挡位。永远作为此软件的最后一道安全防线。");
            var enable = new Button { Text = Lang.T("与苏暖泉共同阅读"), IsDefault = false, X = 0, Y = Pos.Bottom(txt) + 1 };
            var keep = new Button { Text = Lang.T("我暂时不需要"), IsDefault = true, X = Pos.Right(enable) + 2, Y = Pos.Bottom(txt) + 1 };
            dlg.Add(txt, enable, keep);
            bool enabled = false;
            enable.Accepted += (s, e) => { enabled = true; dlg.RequestStop(); };
            keep.Accepted += (s, e) => dlg.RequestStop();
            enable.Initialized += (s, e) => keep.SetFocus();   // 默认焦点在「保持关闭」
            Application.Run(dlg);
            TelemetryService.SetConsent(enabled ? "enabled" : "disabled");
        }

        // 全文抓取同意对话框（TUI）：要求输入指定短语，同意后写标记文件
        bool FulltextConsentDialog()
        {
            if (HasFulltextConsent()) return true;
            string phrase = Lang.T("是的，我愿意与作者达成合理使用约定");
            var dlg = new Dialog { Title = " " + Lang.T("Consent") + " ", Width = 76, Height = 14 };
            var txt = new TextView { X = 0, Y = 0, Width = Dim.Fill(2), Height = 8, ReadOnly = true, CanFocus = false, WordWrap = true };
            txt.Text = Lang.T("sip is a reading aid; article fetching is for personal reading/study only. You agree to respect the source's intellectual property and copyright. You alone bear any loss from malicious use.") + "\n\n" +
                Lang.T("Type exactly to agree: {0}", phrase);
            var input = new TextField { X = 0, Y = Pos.Bottom(txt), Width = Dim.Fill(2), Text = "" };
            var ok = new Button { Text = Lang.T("OK"), IsDefault = true, X = 0, Y = Pos.Bottom(input) + 1 };
            var cancel = new Button { Text = Lang.T("Cancel"), X = Pos.Right(ok) + 1, Y = Pos.Bottom(input) + 1 };
            dlg.Add(input, txt, ok, cancel);
            ok.Accepted += (s, e) => dlg.RequestStop();
            cancel.Accepted += (s, e) => { input.Text = ""; dlg.RequestStop(); };
            input.Initialized += (s, e) => input.SetFocus();
            Application.Run(dlg);
            if (input.Text.Trim() == phrase)
            {
                WriteFulltextConsent();
                return true;
            }
            return false;
        }

        // TUI：抓取当前/指定文章的全文
        void FetchFulltextTui(int itemId)
        {
            if (!FulltextConsentDialog()) { Ask(Lang.T("Not agreed, cancelled"), Lang.T("OK")); return; }
            if (!ArticleContentShort(dbPath, itemId))
            {
                // 原文已够长 → 提示可能是误触
                int ans = Ask(Lang.T("The original text is already long. Did you mean to fetch? Fetch anyway?"), Lang.T("Fetch"), Lang.T("Cancel"));
                if (ans != 0) return;
            }
            var (text, _, err) = DoFetchCore(dbPath, itemId);
            ShowSelectedContent();   // 重新渲染（现在会显示原文 + 分界 + 全文）
            if (text == null) Ask(err ?? Lang.T("Fetch failed"), Lang.T("OK"));
        }

        // 执行命令行输入（复用 CLI 命令语法）
        void RunCommand(string input)
        {
            var parts = input.Split(' ', 2);
            string cmd = parts[0].ToLowerInvariant();
            string arg = parts.Length > 1 ? parts[1].Trim() : "";

            switch (cmd)
            {
                case "q" or "quit" or "exit":
                    QuitApp();
                    return;
                case "h" or "help":
                    ShowHelpDialog();
                    return;
                case "pic" or "--pic":
                {
                    // 开关文章图片显示。**默认关闭** —— 终端 sixel 能力探测不可靠
                    // (见 ImagesEnabled 的注释):WezTerm 自带的 ConPTY 太老时会
                    // 吞掉 sixel,探测说"支持"、实际一张图都出不来还会让画面错乱。
                    // 所以不做自动判断,由用户在确认自己终端 OK 的前提下手动打开。
                    ImagesEnabled = !ImagesEnabled;
                    TuiMdState.ReserveImageSpace = ImagesEnabled;
                    contentView.EnableSixelImages = ImagesEnabled;

                    // 垂直留白是写进 Markdown 文本的(ReserveImageSpace 控制),
                    // 开关变了必须重新生成,否则开着图时没留空间会压住正文、
                    // 关了图却留着 20 行空白。
                    if (_currentArticleId != 0)
                    {
                        contentView.Text = BuildArticleMarkdown(
                            _currentArticleId, contentMode, dbPath,
                            contentView.GetContentWidth(), showFetchHint: true);
                        if (ImagesEnabled) PrefetchArticleImages(contentView.Text, _lastBaseUrl);
                    }
                    contentView.SetNeedsDraw();
                    return;
                }
                case "manage":
                    ShowFeedManager(dbPath);
                    RebuildTree();
                    return;
                case "report" or "insights":
                    ShowInsightsPage(dbPath);
                    return;
                case "today" or "--today":
                    ShowTodayPage(dbPath);
                    return;
                case "u" or "-u" or "--update":
                    if (int.TryParse(arg, out int unum))
                        RunNetworkOp(() => RefreshOneFeed(unum, dbPath));
                    else RefreshSelectedFeed();
                    return;
                case "a" or "-a" or "--archive":
                    if (int.TryParse(arg, out int anum)) { AddTimestampForRealId(anum, dbPath); RebuildTree(); }
                    else ArchiveSelectedFeed();
                    return;
                case "r" or "una" or "-r" or "-una" or "--remove" or "--unarchive":
                    if (int.TryParse(arg, out int rnum)) { RemoveTimestampForRealId(rnum, dbPath); RebuildTree(); }
                    else UnarchiveSelectedFeed();
                    return;
                case "x" or "--delete":
                    DeleteSelected();
                    return;
                case "d" or "-d" or "--download":
                    if (string.IsNullOrWhiteSpace(arg))
                        AddFeedDialog();
                    else
                        RunNetworkOp(() => { try { DownloadAndSaveToDb(arg, dbPath).Wait(); } catch { } });
                    return;
                case "s" or "--search":
                    if (string.IsNullOrWhiteSpace(arg)) { SearchDialog(); return; }
                    DoTuiSearch(arg);
                    return;
                case "g" or "--grep":
                    if (string.IsNullOrWhiteSpace(arg)) { Ask(Lang.T("Usage: grep <keyword>"), Lang.T("OK")); return; }
                    DoTuiGrep(arg);
                    return;
                case "y" or "--summary":
                    SummarizeSelected();
                    return;
                case "fetch" or "--fulltext":
                {
                    long fid = 0;
                    if (!string.IsNullOrWhiteSpace(arg) && int.TryParse(arg, out int fnum)) fid = fnum;
                    else
                    {
                        var sel = tree.SelectedObject;
                        if (sel is { IsFeed: false }) fid = sel.ItemId;
                        else { Ask(Lang.T("Select an article first to fetch"), Lang.T("OK")); return; }
                    }
                    FetchFulltextTui((int)fid);
                    return;
                }
                case "init" or "--init":
                    InitConfigDialog();
                    return;
                case "index" or "--index":
                    IndexSelectedFeed();
                    return;
                case "reindex" or "--reindex":
                    ReindexAll();
                    return;
                case "schedule" or "sched" or "--schedule":
                {
                    var sp = arg.Split(' ', 2);
                    if (sp.Length < 2 || !int.TryParse(sp[0], out int sn))
                    {
                        Ask(Lang.T("Usage: schedule <id> <expr>, e.g. schedule 1 30m / schedule 1 daily@10:00 / schedule 1 manual"), Lang.T("OK"));
                        return;
                    }
                    if (sn <= 0 || GetRealId(sn, dbPath) == 0) { Ask(Lang.T("Feed number not found"), Lang.T("OK")); return; }
                    SetFeedSchedule(sn.ToString(), sp[1], dbPath);
                    RebuildTree();
                    return;
                }
                case "sync" or "--sync":
                    SyncDueFeeds();
                    return;
                case "all" or "--update-all" or "update-all":
                    RefreshAllFeeds();
                    return;
                case "lang" or "--lang":
                    SwitchLanguage(arg);
                    return;
                case "diff" or "--diff":
                    RunCliCommandInTui(() => DiffCli(string.IsNullOrEmpty(arg) ? new string[] { "" } : arg.Split(' '), dbPath));
                    return;
                case "export" or "--export":
                {
                    string ea = string.IsNullOrWhiteSpace(arg) ? "all" : arg;
                    if (!ea.Contains("--yes", StringComparison.OrdinalIgnoreCase)) ea = (ea + " --yes").Trim();
                    var eaArgs = ea.Split(' ').Where(x => x.Length > 0).ToArray();
                    RunCliCommandInTui(() => ExportCli(eaArgs, dbPath));
                    return;
                }
                case "export-opml" or "--export-opml":
                    RunCliCommandInTui(() => ExportOpmlCli(arg, dbPath));
                    return;
                case "import-opml" or "--import-opml":
                    if (string.IsNullOrWhiteSpace(arg)) { Ask(Lang.T("Usage: import-opml <file.opml>"), Lang.T("OK")); return; }
                    RunCliCommandInTui(() => ImportOpmlCli(arg, dbPath));
                    return;
                case "import" or "--import":
                    if (string.IsNullOrWhiteSpace(arg))
                    {
                        // 无参数时弹对话框让用户输入路径
                        ImportFileDialog(dbPath);
                        RebuildTree();
                    }
                    else
                    {
                        RunCliCommandInTui(() => ImportCli(new[] { arg, "--json" }, dbPath));
                        RebuildTree();
                    }
                    return;
                case "import-rm" or "--import-rm":
                    if (string.IsNullOrWhiteSpace(arg)) { Ask(Lang.T("Usage: import-rm <id>"), Lang.T("OK")); return; }
                    RunCliCommandInTui(() => ImportRmCli(new[] { arg, "--yes" }, dbPath));
                    RebuildTree();
                    return;
                case "feed-info" or "--feed-info":
                    RunCliCommandInTui(() => FeedInfoCli(string.IsNullOrEmpty(arg) ? new string[] { "" } : arg.Split(' '), dbPath));
                    return;
                case "like" or "--like":
                    RunCliCommandInTui(() => LikeCli(string.IsNullOrEmpty(arg) ? new string[] { "" } : arg.Split(' '), dbPath));
                    return;
                case "likes" or "--likes":
                    RunCliCommandInTui(() => LikesCli(string.IsNullOrEmpty(arg) ? new string[] { } : arg.Split(' '), dbPath));
                    return;
                case "purge-fulltext" or "--purge-fulltext":
                    RunCliCommandInTui(() => PurgeFulltextCli(arg, dbPath));
                    return;
                case "dedup" or "--dedup":
                    if (string.IsNullOrWhiteSpace(arg)) { ShowDedupCandidatesDialog(dbPath); return; }
                    RunCliCommandInTui(() => DedupCli(arg.Split(' '), dbPath));
                    return;
                case "insights-interval" or "--insights-interval":
                    RunCliCommandInTui(() => InsightsIntervalCli(arg, dbPath));
                    return;
                case "telemetry":
                {
                    var tpos = (arg ?? "").Split(' ').Where(x => x.Length > 0).ToArray();
                    string tsub = tpos.Length > 0 ? tpos[0].ToLowerInvariant() : "";
                    if (tsub == "enable")
                    {
                        // TUI 里不静默开启：先弹确认
                        if (Ask(Lang.T("苏暖泉将开始记录：哪些文章被打开/读完/跳过、以及 AI 调用与搜索情况。数据仅保存在本机，sip 绝不会自动上传。开启吗？\n\n🔒 孟思琳(simon) 守护着这一切——默认开启、无法关闭，永远作为此软件的最后一道安全防线。"), Lang.T("开启"), Lang.T("取消")) == 0)
                            RunCliCommandInTui(() => TelemetryCli(new[] { "enable", "--yes" }, dbPath));
                        return;
                    }
                    RunCliCommandInTui(() => TelemetryCli(tpos, dbPath));
                    return;
                }
                case "config" or "--config":
                    RunCliCommandInTui(() => ShowConfig(dbPath));
                    return;
                case "policy" or "--policy":
                    RunCliCommandInTui(() => PolicyCli(string.IsNullOrEmpty(arg) ? new string[] { "list" } : arg.Split(' '), dbPath));
                    return;
                case "onboarding" or "--onboarding":
                    RunCliCommandInTui(() => OnboardingCli(string.IsNullOrEmpty(arg) ? new string[] { } : arg.Split(' '), dbPath));
                    return;
                case "simon" or "--simon":
                {
                    // 孟思琳(simon)守护:从 TUI 可升降挡(降挡是唯一被允许的通道,需确认)
                    var spos = (arg ?? "").Split(' ').Where(x => x.Length > 0).ToArray();
                    if (spos.Length >= 2 && spos[0].Equals("level", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(spos[1], out int lvl) && lvl < LoadSettings().SimonLevel)
                    {
                        if (Ask(Lang.T("确定要把孟思琳(simon)守护挡位从 {0} 降到 {1} 吗?(降挡后仍无法关闭,只能再升回)", LoadSettings().SimonLevel, lvl), Lang.T("降挡"), Lang.T("取消")) != 0)
                            return;
                    }
                    RunCliCommandInTui(() => SimonCli(spos, dbPath, fromTui: true));
                    return;
                }
                default:
                    Ask(Lang.T("Unknown command: {0}. Press H for help", cmd), Lang.T("OK"));
                    return;
            }
        }

        // 运行时切换界面语言：lang <代码>（如 lang zh-CN / lang en-US）
        void SwitchLanguage(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                Ask(Lang.T("Usage: lang <code>, e.g. lang zh-CN / lang en-US"), Lang.T("OK"));
                return;
            }
            string dataDir = Path.GetDirectoryName(dbPath) ?? ".";
            string file = Path.Combine(dataDir, "languages", code + ".json");
            if (!File.Exists(file))
            {
                Ask(Lang.T("Language file not found: {0}", file), Lang.T("OK"));
                return;
            }
            Lang.Init(dataDir, code);
            // 重绘持久化的静态标签（Terminal.Gui 设置 Title 会自动触发重绘）
            tree.Title = " " + Lang.T("Feeds") + " (C " + Lang.T("collapse") + ") ";
            contentView.Title = " " + Lang.T("Content") + " ";
            RebuildStatusBar();
            Ask(Lang.T("Language switched to {0}", code), Lang.T("OK"));
        }

        // 用当前语言重建状态栏（语言切换后调用）
        void RebuildStatusBar()
        {
            var sb = new StatusBar(new Shortcut[]
            {
                new Shortcut(Key.H, Lang.T("Help"), () => ShowHelpDialog(), Lang.T("Show all keybindings")),
                new Shortcut(Key.F2, Lang.T("About"), () => ShowAboutDialog(), Lang.T("About sip")),
                new Shortcut(Key.U, Lang.T("Update"), () => RefreshSelectedFeed(), Lang.T("Update selected feed (same as CLI -u)")),
                new Shortcut(Key.F6, Lang.T("Update all"), () => RefreshAllFeeds(), Lang.T("Update all feeds")),
                new Shortcut(Key.A, Lang.T("Archive"), () => ArchiveSelectedFeed(), Lang.T("Add timestamp to feed (same as CLI -a)")),
                new Shortcut(Key.R, Lang.T("Unarchive"), () => UnarchiveSelectedFeed(), Lang.T("Remove timestamp (same as CLI -una)")),
                new Shortcut(Key.X, Lang.T("Delete"), () => DeleteSelected(), Lang.T("Delete selected feed/article (same as CLI -r)")),
                new Shortcut(Key.D, Lang.T("Add"), () => AddFeedDialog(), Lang.T("Add new feed (same as CLI -d)")),
                new Shortcut(Key.S, Lang.T("Search"), () => SearchDialog(), Lang.T("Semantic search (same as CLI --search)")),
                new Shortcut(Key.Y, Lang.T("Summary"), () => SummarizeSelected(), Lang.T("Summarize current article (same as CLI --summary)")),
                new Shortcut(Key.G, Lang.T("Overview"), () => ToggleContentMode(), Lang.T("Toggle content/overview")),
            new Shortcut(Key.Q, Lang.T("Quit"), QuitApp, Lang.T("Exit program"))
            });
            top.Remove(statusBar);
            statusBar = sb;
            top.Add(statusBar);
        }

        // TUI 内语义搜索并显示到正文区
        void DoTuiSearch(string query)
        {
            if (!File.Exists(ConfigPath(dbPath)))
            {
                Ask(Lang.T("AI not configured. Run 'sip --init' in the terminal first"), Lang.T("OK"));
                return;
            }
            contentView.Text = Lang.T("Searching, please wait...");
            var results = DoSearch(query, dbPath);
            if (results == null) { contentView.Text = Lang.T("Search failed"); return; }
            // 让 Ctrl+O 链接导航也能遍历搜索结果
            TuiMdState.Links.Clear();
            foreach (var h in results)
                if (!string.IsNullOrWhiteSpace(h.Link))
                    TuiMdState.Links.Add((h.Title, h.Link));
            var sb = new StringBuilder();
            sb.AppendLine(Lang.T("Search results (query: {0}, total {1})", query, results.Count));
            sb.AppendLine(Lang.T("Hint: Enter/Tab or Ctrl+O to open link"));
            sb.AppendLine();
            foreach (var h in results)
            {
                string titleLink = !string.IsNullOrWhiteSpace(h.Link)
                    ? $"[{EscapeMd(h.Title)}]({EscapeMdUrl(h.Link)})"
                    : EscapeMd(h.Title);
                string feedLink = !string.IsNullOrWhiteSpace(h.Link)
                    ? $"[{EscapeMd(h.FeedTitle)}]({EscapeMdUrl(h.Link)})"
                    : EscapeMd(h.FeedTitle);
                sb.AppendLine($"- {titleLink}  （{Lang.T("similarity")} {h.Score:P1}）");
                sb.AppendLine($"  来源：{feedLink}");
                if (!string.IsNullOrWhiteSpace(h.Description))
                    sb.AppendLine($"  摘要：{EscapeMd(h.Description)}");
                sb.AppendLine();
            }
            contentView.Text = sb.ToString();
        }

        // TUI 内全文搜索（等价 CLI --grep，不依赖 AI）
        void DoTuiGrep(string keyword)
        {
            contentView.Text = Lang.T("Searching, please wait...");
            var hits = DoGrep(keyword, dbPath);
            if (hits == null) { contentView.Text = Lang.T("Search failed"); return; }
            // 让 Ctrl+O 链接导航也能遍历搜索结果
            TuiMdState.Links.Clear();
            foreach (var h in hits)
                if (!string.IsNullOrWhiteSpace(h.Link))
                    TuiMdState.Links.Add((h.Title, h.Link));
            var sb = new StringBuilder();
            sb.AppendLine(Lang.T("Full-text search \"{0}\": {1} hits", keyword, hits.Count));
            sb.AppendLine(Lang.T("Hint: Enter/Tab or Ctrl+O to open link"));
            sb.AppendLine();
            foreach (var h in hits)
            {
                string titleLink = !string.IsNullOrWhiteSpace(h.Link)
                    ? $"[{EscapeMd(h.Title)}]({EscapeMdUrl(h.Link)})"
                    : EscapeMd(h.Title);
                sb.AppendLine($"- {titleLink}");
                if (!string.IsNullOrWhiteSpace(h.Description))
                    sb.AppendLine($"  {EscapeMd(h.Description)}");
                sb.AppendLine();
            }
            contentView.Text = sb.ToString();
        }

        // TUI 内 AI 配置向导（对话框版，等价 CLI --init）
        void InitConfigDialog()
        {
            var cfg = LoadConfig(dbPath);
            int y = 0;
            var embEp = new TextField { X = 16, Y = y, Width = Dim.Fill(2), Text = cfg.Embedding.ApiEndpoint };
            var embEpL = new Label { Text = Lang.T("Embedding endpoint: "), X = 1, Y = y };
            y++;
            var embM = new TextField { X = 16, Y = y, Width = Dim.Fill(2), Text = cfg.Embedding.Model };
            var embML = new Label { Text = Lang.T("Embedding model: "), X = 1, Y = y };
            y++;
            var embD = new TextField { X = 16, Y = y, Width = Dim.Fill(2), Text = cfg.Embedding.Dimensions.ToString() };
            var embDL = new Label { Text = Lang.T("Vector dims: "), X = 1, Y = y };
            y++;
            var llmEp = new TextField { X = 16, Y = y, Width = Dim.Fill(2), Text = cfg.Llm.ApiEndpoint };
            var llmEpL = new Label { Text = Lang.T("LLM endpoint: "), X = 1, Y = y };
            y++;
            var llmM = new TextField { X = 16, Y = y, Width = Dim.Fill(2), Text = cfg.Llm.Model };
            var llmML = new Label { Text = Lang.T("LLM model: "), X = 1, Y = y };
            y++;
            var embKey = new TextField { X = 16, Y = y, Width = Dim.Fill(2), Text = "", Secret = true };
            var embKeyL = new Label { Text = Lang.T("Embedding Key: "), X = 1, Y = y };
            y++;
            var llmKey = new TextField { X = 16, Y = y, Width = Dim.Fill(2), Text = "", Secret = true };
            var llmKeyL = new Label { Text = Lang.T("LLM Key: "), X = 1, Y = y };
            y++;
            var thr = new TextField { X = 16, Y = y, Width = Dim.Fill(2), Text = cfg.Embedding.SearchThreshold.ToString() };
            var thrL = new Label { Text = Lang.T("Search threshold: "), X = 1, Y = y };
            y++;
            var ok = new Button { Text = Lang.T("Save"), IsDefault = true, X = 1, Y = y };
            var cancel = new Button { Text = Lang.T("Cancel"), X = Pos.Right(ok) + 1, Y = y };
            var dlg = new Dialog { Title = " " + Lang.T("AI config") + " ", Width = 64, Height = y + 3 };
            dlg.Add(embEpL, embEp, embML, embM, embDL, embD, llmEpL, llmEp, llmML, llmM,
                    embKeyL, embKey, llmKeyL, llmKey, thrL, thr, ok, cancel);
            ok.Accepted += (s, e) => dlg.RequestStop();
            cancel.Accepted += (s, e) => { cfg = null!; dlg.RequestStop(); };

            Application.Run(dlg);
            if (cfg == null) return;  // 用户取消

            // 保存非敏感配置
            if (embEp.Text.Trim().Length > 0) cfg.Embedding.ApiEndpoint = EnsureV1Endpoint(embEp.Text.Trim());
            if (embM.Text.Trim().Length > 0) cfg.Embedding.Model = embM.Text.Trim();
            if (int.TryParse(embD.Text.Trim(), out int dim) && dim > 0) cfg.Embedding.Dimensions = dim;
            if (llmEp.Text.Trim().Length > 0) cfg.Llm.ApiEndpoint = EnsureV1Endpoint(llmEp.Text.Trim());
            if (llmM.Text.Trim().Length > 0) cfg.Llm.Model = llmM.Text.Trim();
            if (float.TryParse(thr.Text.Trim(), out float t)) cfg.Embedding.SearchThreshold = t;
            SaveConfig(dbPath, cfg);

            // Key 存系统凭据库
            if (!string.IsNullOrEmpty(embKey.Text)) CredSet("embedding_api_key", embKey.Text);
            if (!string.IsNullOrEmpty(llmKey.Text)) CredSet("llm_api_key", llmKey.Text);

            Ask(Lang.T("AI config saved. Run reindex after changing the Embedding model."), Lang.T("OK"));
        }

        // TUI 内对当前选中源做向量化（等价 CLI --index，作用于当前源）
        void IndexSelectedFeed()
        {
            int realId = GetSelectedFeedId();
            if (realId == 0) { Ask(Lang.T("Select a feed first"), Lang.T("OK")); return; }
            if (!File.Exists(ConfigPath(dbPath)))
            {
                Ask(Lang.T("AI not configured. Run 'sip --init' in the terminal first"), Lang.T("OK"));
                return;
            }
            var cfg = LoadConfig(dbPath);
            using var conn = OpenDb(dbPath);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT i.Id, i.Title FROM Items i
                WHERE i.FeedId = @fid AND i.Status = 'active'
                AND NOT EXISTS (SELECT 1 FROM Vectors v WHERE v.ItemId = i.Id)
            ";
            cmd.Parameters.AddWithValue("@fid", realId);
            using var r = cmd.ExecuteReader();
            var articles = new List<(int Id, string Title)>();
            while (r.Read()) articles.Add((r.GetInt32(0), r.GetString(1)));

            if (articles.Count == 0) { Ask(Lang.T("All articles of this feed are already embedded"), Lang.T("OK")); return; }

            Console.WriteLine(Lang.T("Embedding {0} articles...", articles.Count));
            RunNetworkOp(() =>
            {
                int modelId = EnsureModel(dbPath, cfg.Embedding);
                int ok = 0, fail = 0;
                foreach (var a in articles)
                {
                    var vec = SafeEmbed(a.Title, cfg, articleId: a.Id, sourceId: realId).GetAwaiter().GetResult();
                    if (vec == null) { fail++; Console.WriteLine(Lang.T("  failed: {0}", a.Title)); continue; }
                    if (vec.Length != cfg.Embedding.Dimensions)
                    {
                        cfg.Embedding.Dimensions = vec.Length;
                        SaveConfig(dbPath, cfg);
                    }
                    SaveVector(dbPath, realId, a.Id, modelId, vec);
                    ok++;
                    if ((ok + fail) % 10 == 0) Console.WriteLine(Lang.T("  processed {0}/{1}", ok + fail, articles.Count));
                }
                Console.WriteLine(Lang.T("Embedding done: {0} OK, {1} failed", ok, fail));
            });
        }

        // TUI 内重新向量化全部（等价 CLI --reindex）：清空所有向量后重建
        void ReindexAll()
        {
            if (!File.Exists(ConfigPath(dbPath)))
            {
                Ask(Lang.T("AI not configured. Run 'sip --init' in the terminal first"), Lang.T("OK"));
                return;
            }
            int ans = Ask(Lang.T("Delete all vectors and re-embed all active articles?"), Lang.T("OK"), Lang.T("Cancel"));
            if (ans != 0) return;

            var cfg = LoadConfig(dbPath);
            using var conn = OpenDb(dbPath);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Vectors";
            cmd.ExecuteNonQuery();
            // 换模型后旧 sidecar 向量（抓取全文的）同样失效，一并清空
            if (File.Exists(FulltextVecsPath())) { try { File.Delete(FulltextVecsPath()); } catch { } }
            cmd.CommandText = "SELECT Id, FeedId, Title FROM Items WHERE Status = 'active'";
            using var r = cmd.ExecuteReader();
            var items = new List<(int Id, int FeedId, string Title)>();
            while (r.Read()) items.Add((r.GetInt32(0), r.GetInt32(1), r.GetString(2)));

            if (items.Count == 0) { Ask(Lang.T("No articles to embed"), Lang.T("OK")); return; }

            Console.WriteLine(Lang.T("Re-embedding {0} articles...", items.Count));
            RunNetworkOp(() =>
            {
                int modelId = EnsureModel(dbPath, cfg.Embedding);
                int ok = 0, fail = 0;
                foreach (var it in items)
                {
                    var vec = SafeEmbed(it.Title, cfg, articleId: it.Id, sourceId: it.FeedId).GetAwaiter().GetResult();
                    if (vec == null) { fail++; continue; }
                    if (vec.Length != cfg.Embedding.Dimensions)
                    {
                        cfg.Embedding.Dimensions = vec.Length;
                        SaveConfig(dbPath, cfg);
                    }
                    SaveVector(dbPath, it.FeedId, it.Id, modelId, vec);
                    ok++;
                    if ((ok + fail) % 10 == 0) Console.WriteLine(Lang.T("  processed {0}/{1}", ok + fail, items.Count));
                }
                Console.WriteLine(Lang.T("Re-indexing done: {0} OK, {1} failed", ok, fail));
            });
        }

        RebuildTree();
        // 默认折叠；从 --show 按 W 进入时才展开并定位到原文章
        if (preselectItemId != 0) { tree.ExpandAll(); tree.SelectItem(preselectItemId); }
        tree.SetFocus();

        // —— 到期源自动同步 ——
        // 启动后稍等片刻，主界面先显示，再非阻塞地同步到期的源；开着期间每 15 分钟后台检查一次
        void SyncDueFeeds()
        {
            if (_syncing) return;
            try
            {
                var due = GetDueFeeds(dbPath);
                if (due.Count == 0) return;
                _syncing = true;
                RunNetworkOp(() =>
                {
                    Console.WriteLine(Lang.T("Syncing {0} due feeds:", due.Count));
                    var now = DateTime.Now;
                    foreach (var f in due)
                    {
                        Console.WriteLine(Lang.T("  · {0} (last {1})", f.Title,
                            f.LastChecked is DateTime lc ? AgoText(lc, now) : Lang.T("never")));
                        try
                        {
                            DownloadAndSaveToDb(f.Url, dbPath, interactive: false).Wait();
                            Console.WriteLine(Lang.T("    ✓ updated"));
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(Lang.T("    ✗ {0}", ex.Message));
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Ask(Lang.T("Error syncing due feeds: {0}", ex.Message), Lang.T("OK"));
            }
            finally
            {
                _syncing = false;
            }
        }

        // 启动同步：一次性的，主界面显示后约 0.4 秒开始
        Application.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            SyncDueFeeds();
            return false;
        });
        // 报告到期且遥测开启 → 启动时自动弹出阅读情况报告页（用户决策，或 Esc 关闭）
        if (TelemetryService.IsEnabled && IsInsightsDue(DateTime.Now))
        {
            Application.AddTimeout(TimeSpan.FromMilliseconds(250), () =>
            {
                ShowInsightsPage(dbPath);
                RebuildTree();
                return false;
            });
        }
        // 后台检查：程序开着期间每 15 分钟查一次到期源（没到期不请求，几乎零开销）
        Application.AddTimeout(TimeSpan.FromMinutes(15), () =>
        {
            SyncDueFeeds();
            return true;
        });

        Application.Run(top);
        // 退出时 progressMap 已由滚动时/QuitApp 实时更新；这里只落盘，不再重读 Viewport（已归 0）
        SaveReadingProgress(progressMap);
        return 0;
    }
    finally
    {
        if (!appReady) Application.Shutdown();
    }
}
#pragma warning restore CS0618


#pragma warning disable CS0618
// ══════════ 外部 CLI 全屏阅读（sip --show <文章编号>）═══════════
// 全屏阅读界面：无侧栏，正文 Markdown 渲染，底部提示「W 进入完整阅读器 · Esc 退出」；
// W → 进入完整 TUI 并定位到当前文章，Esc/Q → 退出
static async Task RunFullscreenReader(int itemId, string dbPath)
{
    Application.Init();
    try
    {
        if (ShowFullscreenReader(itemId, dbPath))
            await RunTui(dbPath, appReady: true, showStartScreen: false, preselectItemId: itemId);
    }
    finally
    {
        Application.Shutdown();
    }
}

static bool ShowFullscreenReader(int itemId, string dbPath)
{
    var md = CreateMarkdownView();
    md.X = 0;
    md.Y = 0;
    md.Width = Dim.Fill();
    md.Height = Dim.Fill() - 1;
    md.CanFocus = true;
    md.Title = " " + Lang.T("Article") + " ";
    md.Text = BuildArticleMarkdown(itemId, contentMode: true, dbPath, 90);

    var hint = new Label
    {
        Text = Lang.T("  Press W to enter the full reader  ·  Esc to exit  "),
        X = 0,
        Y = Pos.AnchorEnd(1),
        Width = Dim.Fill(),
        Height = 1,
        
    };

    var top = new Window
    {
        Title = " sip · " + Lang.T("Article") + " ",
        X = 0,
        Y = 0,
        Width = Dim.Fill(),
        Height = Dim.Fill(),
    };
    top.Add(md, hint);

    bool enterTui = false;
    void OnKey(object? s, Key e)
    {
        if (e.KeyCode == KeyCode.W)
        {
            enterTui = true;
            top.RequestStop();
            e.Handled = true;
        }
        else if (e.KeyCode is KeyCode.Q or KeyCode.Esc)
        {
            top.RequestStop();
            e.Handled = true;
        }
    }
    top.KeyDown += OnKey;
    md.KeyDown += OnKey;

    md.SetFocus();
    Application.Run(top);
    return enterTui;
}

// 开始界面：全屏居中展示 slogan 与功能简介，回车进入 / Q 退出
// Dashboard 统计面板行（初始页数据）
static List<string> DashboardStats(string dbPath)
{
    var lines = new List<string>();
    int feeds = 0, articles = 0, versions = 0, archived = 0, aiIndex = 0;
    long dbSize = 0; string lastSync = "";
    try
    {
        using var conn = OpenDb(dbPath);
        conn.Open();
        var cmd = conn.CreateCommand();
        // 合并为一次 Items 扫描 + 两个小表(原 6 个独立全库 COUNT,百万库要扫 5 遍)
        cmd.CommandText = @"
            SELECT f.feeds, i.total, i.active, i.archived, v.vectors, f.lastSync
            FROM (SELECT COUNT(*) AS feeds, MAX(LastCheckedAt) AS lastSync FROM Feeds) f,
                 (SELECT COUNT(*) AS total,
                         COUNT(*) FILTER (WHERE Status = 'active') AS active,
                         COUNT(*) FILTER (WHERE Status = 'archived') AS archived
                  FROM Items) i,
                 (SELECT COUNT(*) AS vectors FROM Vectors) v
        ";
        using var r = cmd.ExecuteReader();
        if (r.Read())
        {
            feeds = r.GetInt32(0); articles = r.GetInt32(1); versions = r.GetInt32(2);
            archived = r.GetInt32(3); aiIndex = r.GetInt32(4);
            lastSync = r.IsDBNull(5) ? "" : r.GetString(5);
        }
        dbSize = new FileInfo(dbPath).Length;
    }
    catch { }
    lines.Add(Lang.T("──  Dashboard  ──"));
    lines.Add(Lang.T("  订阅源 feeds      : {0}", feeds));
    lines.Add(Lang.T("  文章 articles     : {0}", articles));
    lines.Add(Lang.T("  版本 versions     : {0}", versions));
    lines.Add(Lang.T("  归档 archived     : {0}", archived));
    lines.Add(Lang.T("  AI 索引 index     : {0}", aiIndex));
    lines.Add(Lang.T("  数据库 database   : {0:N1} MB", dbSize / 1048576.0));
    lines.Add(Lang.T("  最近同步 last sync: {0}", lastSync.Length > 0 ? lastSync : Lang.T("never")));
    return lines;
}

// 订阅源管理页（TUI：m 键 / manage 命令）

static void ShowFeedManager(string dbPath)
{
    // Dialog 全屏；列表用自绘 FeedManagerList，方向键/翻页由它自己处理，不会被吞
    var top = new Window
    {
        Title = " " + Lang.T("Manage feeds") + " ",
        X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill()
    };
    var list = new FeedManagerList
    {
        X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2),
        CanFocus = true
    };
    var hint = new Label
    {
        Text = Lang.T("  j/k 移动 · u 更新 · a 归档 · r 去归档 · x 删除 · d 加源 · i 已隐藏 · Esc 返回  "),
        X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1
    };
    top.Add(list, hint);

    void Rebuild()
    {
        var rows = new List<(int Id, string Line)>();
        using (var conn = OpenDb(dbPath))
        {
            conn.Open();
            var c = conn.CreateCommand();
            c.CommandText = @"
                SELECT f.Id, f.Title, f.Schedule, f.LastCheckedAt,
                       (SELECT COUNT(*) FROM Items WHERE FeedId = f.Id AND Status='active'),
                       (SELECT COUNT(*) FROM Items WHERE FeedId = f.Id AND Status='archived')
                FROM Feeds f ORDER BY f.Id";
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                int id = r.GetInt32(0);
                string title = r.GetString(1);
                string sched = r.IsDBNull(2) ? "" : r.GetString(2);
                string last = r.IsDBNull(3) ? "" : r.GetString(3);
                int active = r.GetInt32(4); int arch = r.GetInt32(5);
                string s = (string.IsNullOrWhiteSpace(sched) || sched.Equals("manual", StringComparison.OrdinalIgnoreCase)) ? Lang.T("manual") : sched;
                string healthText = FeedHealthText(id, sched, last.Length > 0 ? TryParseIso(last) : null, DateTime.Now);
                string healthMark = healthText == Lang.T("正常") ? "" : " " + healthText;
                string line = $"[{id}] {CjkSpace(title)}  · {s} · {Lang.T("last")} {last} · {active}/{arch}{healthMark}";
                rows.Add((id, line));
            }
        }
        list.SetRows(rows);
    }

    try
    {
        Rebuild();
        top.Initialized += (s, e) => list.SetFocus();
        top.KeyDown += (s, e) =>
        {
            int id = list.SelectedId;
            // 方向键/翻页已由 list 自行处理，这里只处理动作键
            if (e.KeyCode == KeyCode.Esc) { top.RequestStop(); e.Handled = true; }
            else if (e.KeyCode == KeyCode.U) { if (id != 0) RefreshOneFeed(id, dbPath); Rebuild(); e.Handled = true; }
            else if (e.KeyCode == KeyCode.A) { if (id != 0) { AddTimestampForRealId(id, dbPath); Rebuild(); } e.Handled = true; }
            else if (e.KeyCode == KeyCode.R) { if (id != 0) { RemoveTimestampForRealId(id, dbPath); Rebuild(); } e.Handled = true; }
            else if (e.KeyCode == KeyCode.X)
            {
                if (id != 0)
                {
                    if (MessageBox.Query(Application.Instance, Lang.T("Notice"), Lang.T("Delete feed {0}? This cannot be undone!", id), Lang.T("OK"), Lang.T("Cancel")) == 0)
                    { DeleteFeedByRealId(id, dbPath); Rebuild(); }
                }
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.Enter)
            {
                if (id != 0) { FeedEditDialog(id, dbPath); Rebuild(); }
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.D)
            {
                AddFeedManagerDialog(dbPath);
                Rebuild();
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.I)
            {
                ShowHiddenDedupDialog(dbPath);
                e.Handled = true;
            }
        };
        Application.Run(top);
    }
    catch (Exception ex)
    {
        // 管理页出错不崩溃整个 TUI
        MessageBox.Query(Application.Instance, Lang.T("Notice"), Lang.T("Manage page error: {0}", ex.Message), Lang.T("OK"));
    }
}

// 管理页：设置某源更新计划（对话框）
#pragma warning disable CS0618
// 更新计划预设的显示名

static string ScheduleDisplayName(string p)
{
    return p switch
    {
        "manual" => Lang.T("手动"),
        "30m" => "30m",
        "1h" => "1h",
        "6h" => "6h",
        "12h" => "12h",
        "1d" => "1d",
        "3d" => "3d",
        "7d" => "7d",
        "30d" => "30d",
        "custom" => Lang.T("自定义…"),
        _ when p.StartsWith("daily@") => Lang.T("每天 {0}:00", p[6..].Substring(0, 2)),
        _ when p.StartsWith("weekly@") => Lang.T("每周 {0}", p["weekly@".Length..]),
        _ => p
    };
}

// 左右键调整 daily/weekly 预设的小时（就地修改 presets[idx]）
static void AdjustHour(List<string> presets, int idx, int delta)
{
    string p = presets[idx];
    var m = Regex.Match(p, @"^(daily@|weekly@\w+ )(\d{2}):\d{2}$");
    if (!m.Success) return;
    int h = int.Parse(m.Groups[2].Value);
    h = ((h + delta) % 24 + 24) % 24;
    presets[idx] = m.Groups[1].Value + h.ToString("00") + ":00";
}

// 管理页：更新计划（方向键预设选择器）
static void SchedulePickerDialog(int realId, string dbPath)
{
    string current = "";
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var c = conn.CreateCommand();
        c.CommandText = "SELECT Schedule FROM Feeds WHERE Id = @id";
        c.Parameters.AddWithValue("@id", realId);
        var o = c.ExecuteScalar();
        if (o != null) current = (o.ToString() ?? "").Trim().ToLowerInvariant();
    }
    var presets = new List<string> { "manual", "30m", "1h", "6h", "12h", "1d", "3d", "7d", "30d", "daily@08:00", "daily@12:00", "daily@18:00", "weekly@Mon 08:00", "custom" };
    int sel = presets.FindIndex(p => p == current);
    if (sel < 0) sel = 0;

    var top = new Window { Title = " " + Lang.T("Update schedule") + " ", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
    var list = new FeedManagerList { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1), CanFocus = true };
    var hint = new Label
    {
        Text = Lang.T("  ↑/↓ 选择 · ←/→ 调时间 · Enter 应用 · Esc 取消  "),
        X = 0, Y = Pos.AnchorEnd(0), Width = Dim.Fill(), Height = 1
    };
    top.Add(list, hint);

    void RebuildRows() => list.SetRows(presets.Select((p, i) => (i, "  " + ScheduleDisplayName(p) + (i == sel ? "  ←" : ""))).ToList());
    RebuildRows();
    list.MoveTo(sel);

    top.Initialized += (s, e) => list.SetFocus();
    top.KeyDown += (s, e) =>
    {
        if (e.KeyCode == KeyCode.Esc) { top.RequestStop(); e.Handled = true; }
        else if (e.KeyCode == KeyCode.Enter)
        {
            string p = presets[list.SelectedId];
            if (p == "custom") { ScheduleCustomDialog(realId, dbPath); top.RequestStop(); e.Handled = true; return; }
            SetFeedSchedule(GetDisplayNum(realId, dbPath).ToString(), p, dbPath);
            top.RequestStop(); e.Handled = true;
        }
        else if (e.KeyCode == KeyCode.CursorLeft || e.KeyCode == KeyCode.CursorRight)
        {
            sel = list.SelectedId;
            AdjustHour(presets, sel, e.KeyCode == KeyCode.CursorLeft ? -1 : 1);
            RebuildRows();
            e.Handled = true;
        }
    };
    Application.Run(top);
}

// 更新计划：自定义表达式（TextField）
static void ScheduleCustomDialog(int realId, string dbPath)
{
    var dlg = new Dialog { Title = " " + Lang.T("Update schedule") + " ", Width = 64, Height = 9 };
    var lbl = new Label { Text = Lang.T("Schedule (30m / 1h / daily@10:00 / weekly@Mon 08:00 / manual): "), X = 0, Y = 0 };
    var input = new TextField { X = 0, Y = 1, Width = Dim.Fill(2), Text = "" };
    var ok = new Button { Text = Lang.T("OK"), IsDefault = true, X = 0, Y = 3 };
    var cancel = new Button { Text = Lang.T("Cancel"), X = Pos.Right(ok) + 1, Y = 3 };
    dlg.Add(lbl, input, ok, cancel);
    ok.Accepted += (s, e) => dlg.RequestStop();
    cancel.Accepted += (s, e) => dlg.RequestStop();
    Application.Run(dlg);
    string expr = input.Text.Trim();
    if (string.IsNullOrEmpty(expr)) return;
    SetFeedSchedule(GetDisplayNum(realId, dbPath).ToString(), expr, dbPath);
}

// 管理页：单源编辑面板（Enter 进入；↑/↓ 选字段，Enter/←/→ 执行）
static void FeedEditDialog(int realId, string dbPath)
{
    string title = "", schedule = "";
    using (var conn = OpenDb(dbPath))
    {
        conn.Open();
        var c = conn.CreateCommand();
        c.CommandText = "SELECT Title, COALESCE(Schedule,'') FROM Feeds WHERE Id = @id";
        c.Parameters.AddWithValue("@id", realId);
        using var r = c.ExecuteReader();
        if (r.Read()) { title = r.GetString(0); schedule = r.GetString(1); }
    }
    var actions = new List<(int Id, string Line)>
    {
        (1, Lang.T("更新计划") + "  : " + (string.IsNullOrWhiteSpace(schedule) || schedule == "manual" ? Lang.T("手动") : schedule)),
        (2, Lang.T("归档")),
        (3, Lang.T("去归档")),
        (4, Lang.T("删除"))
    };
    var top = new Window { Title = " " + Lang.T("编辑源") + " · " + CjkSpace(title) + " ", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
    var list = new FeedManagerList { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1), CanFocus = true };
    var hint = new Label { Text = Lang.T("  ↑/↓ 选择 · Enter 执行 · Esc 返回  "), X = 0, Y = Pos.AnchorEnd(0), Width = Dim.Fill(), Height = 1 };
    top.Add(list, hint);
    list.SetRows(actions);

    top.Initialized += (s, e) => list.SetFocus();
    top.KeyDown += (s, e) =>
    {
        if (e.KeyCode == KeyCode.Esc) { top.RequestStop(); e.Handled = true; }
        else if (e.KeyCode == KeyCode.Enter || e.KeyCode == KeyCode.CursorRight)
        {
            switch (list.SelectedId)
            {
                case 1: SchedulePickerDialog(realId, dbPath); break;
                case 2: AddTimestampForRealId(realId, dbPath); break;
                case 3: RemoveTimestampForRealId(realId, dbPath); break;
                case 4:
                    if (MessageBox.Query(Application.Instance, Lang.T("Notice"), Lang.T("Delete feed {0}? This cannot be undone!", realId), Lang.T("OK"), Lang.T("Cancel")) == 0)
                        DeleteFeedByRealId(realId, dbPath);
                    break;
            }
            top.RequestStop(); e.Handled = true;
        }
    };
    Application.Run(top);
}
#pragma warning restore CS0618

// 真实源 Id → 列表显示编号（1,2,3...；找不到原样返回）

#pragma warning disable CS0618
// 管理页：加源对话框（下载放后台，不阻塞 TUI）
static void AddFeedManagerDialog(string dbPath)
{
    var dlg = new Dialog { Title = " " + Lang.T("Add feed") + " " };
    var lbl = new Label { Text = Lang.T("RSS URL: "), X = 0, Y = 0 };
    var input = new TextField { X = 0, Y = 1, Width = Dim.Fill(2), Text = "" };
    var ok = new Button { Text = Lang.T("OK"), IsDefault = true, X = 0, Y = 3 };
    var cancel = new Button { Text = Lang.T("Cancel"), X = Pos.Right(ok) + 1, Y = 3 };
    dlg.Add(lbl, input, ok, cancel);
    dlg.Width = 60; dlg.Height = 7;
    ok.Accepted += (s, e) => dlg.RequestStop();
    cancel.Accepted += (s, e) => { input.Text = ""; dlg.RequestStop(); };
    Application.Run(dlg);
    string url = input.Text.Trim();
    if (string.IsNullOrWhiteSpace(url)) return;
    // 后台下载，避免冻结管理页；完成后由用户按任意键刷新列表
    _ = Task.Run(() =>
    {
        try { DownloadAndSaveToDb(url, dbPath).Wait(); } catch { }
    });
}

// 管理页：导入本地文件对话框
#pragma warning disable CS0618
static void ImportFileDialog(string dbPath)
{
    var dlg = new Dialog { Title = " " + Lang.T("Import file") + " " };
    var lbl = new Label { Text = Lang.T("File path: "), X = 0, Y = 0 };
    var input = new TextField { X = 0, Y = 1, Width = Dim.Fill(2), Text = "" };
    var hint = new Label { Text = Lang.T("Supported: txt, md, pdf, epub, docx"), X = 0, Y = 2, Width = Dim.Fill() };
    var ok = new Button { Text = Lang.T("OK"), IsDefault = true, X = 0, Y = 4 };
    var cancel = new Button { Text = Lang.T("Cancel"), X = Pos.Right(ok) + 1, Y = 4 };
    dlg.Add(lbl, input, hint, ok, cancel);
    dlg.Width = 60; dlg.Height = 8;
    ok.Accepted += (s, e) => dlg.RequestStop();
    cancel.Accepted += (s, e) => { input.Text = ""; dlg.RequestStop(); };
    Application.Run(dlg);
    string path = input.Text.Trim();
    if (string.IsNullOrWhiteSpace(path)) return;
    // 去掉首尾引号（用户可能从资源管理器复制路径带引号）
    path = path.Trim('"', '\'');
    if (!File.Exists(path))
    {
        MessageBox.Query(Application.Instance, Lang.T("Error"), Lang.T("File not found: {0}", path), Lang.T("OK"));
        return;
    }
    try
    {
        // 调用 CLI 导入逻辑
        var args = new[] { "--import", path, "--json" };
        var orig = Console.Out;
        var sb = new StringBuilder();
        using (var sw = new StringWriter(sb)) { Console.SetOut(sw); ImportCli(args, dbPath); }
        Console.SetOut(orig);
        var result = sb.ToString().Trim();
        // 提取标题
        string title = Path.GetFileNameWithoutExtension(path);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(result);
            if (doc.RootElement.TryGetProperty("title", out var t)) title = t.GetString() ?? title;
        }
        catch { }
        MessageBox.Query(Application.Instance, Lang.T("Imported"), Lang.T("Imported: {0}", title), Lang.T("OK"));
    }
    catch (Exception ex)
    {
        MessageBox.Query(Application.Instance, Lang.T("Error"), ex.Message, Lang.T("OK"));
    }
}
#pragma warning restore CS0618

// 全文缓存自动清理：超过阈值时按最旧先删（保留 --purge-fulltext 手动清）

static List<string> TodayStartScreenLines(string dbPath)
{
    var lines = new List<string>();
    try
    {
        lines.Add("");
        lines.Add(Lang.T("──  今日哈汤  ──"));

        // 首次启动（还没有订阅源）：不显示空清单，给引导文案
        int feedCount = 0;
        using (var conn = OpenDb(dbPath))
        {
            conn.Open();
            var c = conn.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM Feeds";
            feedCount = Convert.ToInt32(c.ExecuteScalar());
        }
        if (feedCount == 0)
        {
            lines.Add(Lang.T("  🍵 还没有订阅源——回车先去添加几个，明天起每天给你一小碗"));
            return lines;
        }

        var list = GetTodayList(dbPath, 5, refresh: false, out _);   // 一天一碗,当天固定
        var (done, target, tracking) = TodayProgress(dbPath);
        if (list.Count == 0)
            lines.Add(Lang.T("  今天还没有值得读的，回车后去更新订阅源"));
        for (int i = 0; i < list.Count; i++)
        {
            var it = list[i];
            lines.Add(Lang.T("  {0}. {1}", i + 1, CjkSpace(it.Title)));
            lines.Add(Lang.T("     [{0} · ~{1} 分钟{2}]", it.Source, it.Minutes, it.Reason.Length > 0 ? " · " + it.Reason : ""));
        }
        // 总时长：让时间不够的用户一眼判断「这碗汤要喝多久」
        double total = list.Sum(i => i.Minutes);
        if (tracking)
            lines.Add(done >= target
                ? Lang.T("  共约 {0} 分钟 · 已完成 🎉 今天结束", total)
                : Lang.T("  共约 {0} 分钟 · 目标 {1} 篇 · 已完成 {2} 篇", total, target, done));
        else
            lines.Add(Lang.T("  共约 {0} 分钟 · 目标 {1} 篇（与苏暖泉共同阅读可跟踪进度）", total, target));
    }
    catch { /* 起始页不因异常崩溃 */ }
    return lines;
}

static bool ShowStartScreen(string dbPath)
{
    var top = new Window
    {
        Title = " 🍲 sip RSS Reader ",
        X = 0,
        Y = 0,
        Width = Dim.Fill(),
        Height = Dim.Fill(),
    };
    // slogan / 功能简介 + Dashboard 数据面板（同屏）
    var lines = new List<string>
    {
        Lang.T("🍲 sip"),
        "",
        Lang.T("——「品，你细品。」"),
        Lang.T("一个让你站着把信息喝了的 RSS 阅读器核心"),
        "",
        Lang.T("  订阅管理 · 全文搜索 · 语义搜索 · AI 摘要"),
        Lang.T("  版本追踪 · 快照归档 · 多语言"),
        ""
    };
    lines.AddRange(DashboardStats(dbPath));
    lines.AddRange(TodayStartScreenLines(dbPath));   // 「今日 Sip」：引导每日少量阅读
    lines.Add("");
    lines.Add(Lang.T("  Enter 进入 · 输入 today 今日哈汤 · M 订阅管理 · Q 退出  "));

    var sv = new StartScreenView
    {
        Lines = lines.ToArray()
    };
    sv.X = 0;
    sv.Y = 0;
    sv.Width = Dim.Fill();
    sv.Height = Dim.Fill();
    top.Add(sv);

    bool cont = false;
    top.KeyDown += (s, e) =>
    {
        if (e.KeyCode is KeyCode.Enter or KeyCode.Space)
        {
            cont = true;
            top.RequestStop();
            e.Handled = true;
        }
        else if (e.KeyCode is KeyCode.Q or KeyCode.Esc)
        {
            top.RequestStop();
            e.Handled = true;
        }
        else if (e.KeyCode == KeyCode.M)
        {
            ShowFeedManager(dbPath);
            e.Handled = true;
        }
        else if (e.KeyCode == KeyCode.T)
        {
            ShowTodayPage(dbPath);
            e.Handled = true;
        }
    };
    Application.Run(top);
    return cont;
}
#pragma warning restore CS0618

// 统一配置的 Markdown 阅读视图（配色 + 软换行当硬换行 + 删除线）
//
// 返回 SipMarkdown 而不是 Markdown:子类会在每帧绘制结束后改写图片的 sixel
// 落点,把它们水平居中。基类把图片锚在占位文本 "[alt]" 的首字符位置(即段落
// 左对齐起点),私有方法没法 override,只能这样迂回。详见 SipMarkdown.cs。
static SipMarkdown CreateMarkdownView()
{
    var v = new SipMarkdown
    {
        ShowHeadingPrefix = false,
        UseThemeBackground = true,
        // 能否出图取决于两件事，缺一不可：
        //   1. 终端本身支持 sixel；
        //   2. 终端自带的 ConPTY 组件（conpty.dll + OpenConsole.exe）版本够新。
        // ConPTY 是真实的 conhost 实例而不是透传管道，不认识的 DCS(sixel) 序列会被
        // 直接吞掉。Windows Terminal 自带的 OpenConsole 没问题；WezTerm 需要升级它
        // 自带的那对组件到 1.22+，见 tools/upgrade-wezterm-conpty.ps1。
        // 默认关。只有 `sip pic` 启动才开 —— 探测终端 sixel 能力不可靠,
        // 见 ImagesEnabled 的注释。
        EnableSixelImages = ImagesEnabled,
        ImageLoader = MarkdownImageLoader
    };
    // 阅读配色：正文亮白、代码绿色、强调亮黄、链接亮青
    // 聚焦时正文保持白字黑底（不再整块变深蓝），靠标题栏 ◀/▶ 指示焦点，阅读更干净
    v.SetScheme(new Scheme
    {
        Normal = new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.Black),
        HotNormal = new Terminal.Gui.Drawing.Attribute(StandardColor.BrightCyan, StandardColor.Black),
        Active = new Terminal.Gui.Drawing.Attribute(StandardColor.BrightYellow, StandardColor.Black, TextStyle.Bold),
        HotActive = new Terminal.Gui.Drawing.Attribute(StandardColor.BrightYellow, StandardColor.Black, TextStyle.Bold),
        Focus = new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.Black),
        HotFocus = new Terminal.Gui.Drawing.Attribute(StandardColor.BrightCyan, StandardColor.Black),
        Highlight = new Terminal.Gui.Drawing.Attribute(StandardColor.Black, StandardColor.BrightCyan),
        ReadOnly = new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.Black),
        Code = new Terminal.Gui.Drawing.Attribute(StandardColor.Green, StandardColor.Black),
        CodeString = new Terminal.Gui.Drawing.Attribute(StandardColor.BrightYellow, StandardColor.Black),
        CodeComment = new Terminal.Gui.Drawing.Attribute(StandardColor.Gray, StandardColor.Black)
    });
    // 软换行当硬换行 + 启用删除线（~~ 需要 UseEmphasisExtras）
    var pipeBuilder = new Markdig.MarkdownPipelineBuilder();
    Markdig.MarkdownExtensions.UseSoftlineBreakAsHardlineBreak(pipeBuilder);
    Markdig.MarkdownExtensions.UseEmphasisExtras(pipeBuilder, Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough);
    v.MarkdownPipeline = pipeBuilder.Build();

    // 后台图片到货时通知重绘。ImageSixel 在线程池里下载+编码,完成后通过
    // Application.Invoke 排到主循环 —— 这是后台线程碰 UI 的唯一安全方式。
    SipMarkdown.OnImageReady = () => v.SetNeedsDraw();
    return v;
}

// 从生成的 Markdown 文本里抽出图片 URL,供后台预取。
// sipcore 输出的图片语法固定是 `![\u200B](url)`(alt 恒为 ZWSP,见 sipcore 注释),
// 但这里按通用 Markdown 图片语法匹配,不依赖 alt 的具体内容。
static readonly Regex MdImageUrlRegex = new(@"!\[[^\]]*\]\(([^)\s]+)", RegexOptions.Compiled);

/// <summary>
/// 文章渲染后立刻把里面的图片丢给后台预取,避免首次打开带图文章时渲染线程
/// 被同步下载+解码卡住。MarkdownImageLoader 只在缓存命中时才返回 sixel,
/// 所以预取是"图能显示出来"的前提,不只是加速。
/// </summary>
static void PrefetchArticleImages(string markdownText, string baseUrl)
{
    if (string.IsNullOrEmpty(markdownText)) return;
    var urls = new List<string>();
    foreach (Match m in MdImageUrlRegex.Matches(markdownText))
    {
        var u = m.Groups[1].Value;
        if (!string.IsNullOrWhiteSpace(u)) urls.Add(u);
    }
    if (urls.Count > 0) Program.PrefetchImages(urls, baseUrl);
}

// 把一篇文章渲染成 Markdown 字符串（TUI 正文区与 CLI 预览共用）
// showFetchHint=true（仅 TUI）：正文过短且未抓取全文时，提示输入 fetch

static IEnumerable<TuiNode> LoadArticleNodes(int feedId, string dbPath, int limit = 0)
{
    var nodes = new List<TuiNode>();
    using var conn = OpenDb(dbPath);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT Id, Title, Version, Status, Guid, VersionCount, ArchivedCount
        FROM (
            SELECT i.Id, i.Title, i.Version, i.Status, i.Guid,
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
    cmd.Parameters.AddWithValue("@fid", feedId);
    if (limit > 0) cmd.Parameters.AddWithValue("@limit", limit);
    var signals = LoadSignals();
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        long id = r.GetInt64(0);
        string title = r.GetString(1);
        string status = r.GetString(3);
        string guid = r.IsDBNull(4) ? "" : r.GetString(4);
        int versionCount = r.GetInt32(5);
        int archivedCount = r.GetInt32(6);
        bool hasHistory = archivedCount > 0;
        signals.TryGetValue(id.ToString(), out var sig);
        string marks = (sig?.UserLike == true ? "♥" : "") + (sig?.AiLike == true ? "🤖" : "");
        string display = CjkSpace(title) + (marks.Length > 0 ? " " + marks : "") + (hasHistory ? " ✎" : "");
        nodes.Add(new TuiNode
        {
            IsFeed = false,
            FeedId = feedId,
            ItemId = id,
            Status = status,
            Guid = guid,
            HasHistory = hasHistory,
            VersionCount = versionCount,
            Title = display
        });
    }
    return nodes;
}


// Markdown 图片回调。注意这里必须返回**已编码的 sixel 字节**，不是原始图片字节 ——
// Terminal.Gui 的 ImageLoader 契约就是如此，直接把 PNG/JPEG 喂进去不会出图。
// 实现见 ImageSixel.cs。
static byte[]? MarkdownImageLoader(string url) => LoadImageAsSixel(url, _lastBaseUrl);

static string _lastBaseUrl = "";

// 文章图片是否显示。**默认 false** —— 只有 `sip pic` 启动时才置 true。
//
// 为什么不能自动探测:Terminal.Gui 会发查询序列问终端"支持 sixel 吗",但 sixel
// 是 DCS 序列,中间必须过 ConPTY —— 那是真实的 conhost 实例而不是透传管道,
// 不认识的 DCS 会被直接吞掉。WezTerm 自带的 ConPTY 可能太老(实测 2024-02-03),
// 于是探测得到"支持"、实际一张图都出不来,画面还会错乱(图片区黑块 + 滚动时
// 残留)。Windows Terminal 1.22+ 自带的新 ConPTY 没问题。
// 所以这里不做自动判断,由用户在确认自己终端 OK 的前提下用 `sip pic` 显式打开。
// 要让 WezTerm 也能用,先跑 tools/upgrade-conpty-admin.cmd 升级它自带的 ConPTY。
static bool ImagesEnabled = false;

// HTML 正文转 Markdown（保留标题/粗体/斜体/删除线/分隔线/列表/代码/图片，供 TUI Markdown 渲染）

// TUI 报告页（卡片式）：j/k 移动 · a 归档 · x 删除 · Esc 返回
#pragma warning disable CS0618
static void ShowInsightsPage(string dbPath)
{
    var top = new Dialog
    {
        Title = " " + Lang.T("阅读情况报告") + " ",
        X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill()
    };

    if (!TelemetryService.IsEnabled)
    {
        var n = new Label
        {
            Text = Lang.T("阅读情况报告需要先开启遥测（仅本地、不上传）。运行 sip telemetry enable 后再试。"),
            X = 0, Y = 0, Width = Dim.Fill(), Height = 1
        };
        var ok = new Button { Text = Lang.T("OK"), X = 0, Y = 2, IsDefault = true };
        top.Add(n, ok);
        ok.Accepted += (s, e) => top.RequestStop();
        top.KeyDown += (s, e) => { if (e.KeyCode == KeyCode.Esc) { top.RequestStop(); e.Handled = true; } };
        Application.Run(top);
        return;
    }

    var view = new InsightsView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1), CanFocus = true };
    var hint = new Label
    {
        Text = Lang.T("  j/k 移动 · a 归档 · x 删除 · Esc 返回  "),
        X = 0, Y = Pos.AnchorEnd(0), Width = Dim.Fill(), Height = 1
    };
    top.Add(view, hint);

    void Rebuild()
    {
        var list = BuildInsights(dbPath, 30);
        view.SetFeeds(list);
        // 记录本次查看时间（供到期判定）
        var s = LoadSettings();
        s.LastInsightsAt = DateTime.Now.ToString("O");
        SaveSettings(s);
    }

    try
    {
        Rebuild();
        top.Initialized += (s, e) => view.SetFocus();
        top.KeyDown += (s, e) =>
        {
            int fid = view.SelectedFeedId;
            if (e.KeyCode == KeyCode.Esc) { top.RequestStop(); e.Handled = true; }
            else if (e.KeyCode == KeyCode.A)
            {
                if (fid != 0) { AddTimestampForRealId(fid, dbPath); Rebuild(); }
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.X)
            {
                if (fid != 0)
                {
                    if (MessageBox.Query(Application.Instance, Lang.T("Notice"), Lang.T("Delete feed {0}? This cannot be undone!", fid), Lang.T("OK"), Lang.T("Cancel")) == 0)
                    { DeleteFeedByRealId(fid, dbPath); Rebuild(); }
                }
                e.Handled = true;
            }
        };
        Application.Run(top);
    }
    catch (Exception ex)
    {
        MessageBox.Query(Application.Instance, Lang.T("Notice"), Lang.T("报告页出错: {0}", ex.Message), Lang.T("OK"));
    }
}
#pragma warning restore CS0618

}

// ===== TUI 视图组件(原 Tui.cs,合并至此)=====
// TUI 树节点（订阅源或文章）
class TuiNode
{
    public bool IsFeed { get; set; }    // true=订阅源父节点，false=文章叶子
    public int FeedId { get; set; }     // 归属源 Id（文章节点也带，便于操作）
    public long ItemId { get; set; }    // 文章 Id（源节点为 0）
    public string Status { get; set; } = "active";  // 文章状态：active/archived/deleted
    public string Title { get; set; } = "";
    public string Guid { get; set; } = "";        // 文章 Guid（同一篇文章的多个版本共享）
    public bool HasHistory { get; set; }          // 是否有被改过的旧版本（有则标题右侧有 ✎ 标记）
    public int VersionCount { get; set; } = 1;    // 该文章共有几个版本
}

class FeedManagerList : View
{
    public List<(int Id, string Line)> Rows { get; private set; } = new();
    public int Selected { get; private set; }
    public event EventHandler? SelectionChanged;

    public int SelectedId => Selected < Rows.Count ? Rows[Selected].Id : 0;

    public void SetRows(List<(int Id, string Line)> rows)
    {
        Rows = rows;
        Selected = Math.Clamp(Selected, 0, Math.Max(0, Rows.Count - 1));
        SetNeedsDraw();
    }

    public void MoveTo(int delta)
    {
        if (Rows.Count == 0) return;
        int before = Selected;
        Selected = Math.Clamp(Selected + delta, 0, Rows.Count - 1);
        if (Selected != before) { SelectionChanged?.Invoke(this, EventArgs.Empty); SetNeedsDraw(); }
    }

    // 方向键/PageUp/PageDown/Home/End 都由本视图单独处理，不被外层吞掉
    protected override bool OnKeyDown(Key key)
    {
        if (Rows.Count == 0) return false;
        switch (key.KeyCode)
        {
            case KeyCode.CursorDown:
            case KeyCode.J: MoveTo(1); return true;
            case KeyCode.CursorUp:
            case KeyCode.K: MoveTo(-1); return true;
            case KeyCode.PageDown: MoveTo(Math.Max(1, Viewport.Height - 1)); return true;
            case KeyCode.PageUp: MoveTo(-Math.Max(1, Viewport.Height - 1)); return true;
            case KeyCode.Home: MoveTo(-Rows.Count); return true;
            case KeyCode.End: MoveTo(Rows.Count); return true;
            default: return false;
        }
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        int w = Viewport.Width, h = Viewport.Height;
        SetAttribute(GetAttributeForRole(VisualRole.Normal));
        for (int y = 0; y < h; y++) AddStr(0, y, new string(' ', w));
        int top = Math.Max(0, Selected - h / 2);   // 让选中行尽量居中
        for (int i = top; i < Math.Min(Rows.Count, top + h); i++)
        {
            int sy = i - top;
            bool sel = i == Selected;
            SetAttribute(sel
                ? GetAttributeForRole(HasFocus ? VisualRole.Focus : VisualRole.Active)
                : GetAttributeForRole(VisualRole.Normal));
            string line = (sel ? "> " : "  ") + Rows[i].Line;
            int cols = line.GetColumns();
            if (cols > w) line = line[..Math.Max(0, w - 1)] + "…";
            AddStr(0, sy, line);
        }
        return true;
    }
}

class SidebarRow
{
    public TuiNode Node { get; set; } = new();
    public bool IsFeed { get; set; }
    public bool IsLastChild { get; set; }   // 是否为父源下最后一篇文章（决定 └─ / ├─ 与续行竖线）
    public List<string> Lines { get; set; } = new();
}

class SidebarView : View
{
    private readonly Func<int, IEnumerable<TuiNode>> _childLoader;
    private readonly List<TuiNode> _roots = new();
    private readonly Dictionary<int, List<TuiNode>> _articles = new();
    private readonly HashSet<int> _expanded = new();
    private readonly List<SidebarRow> _rows = new();
    private int _sel;
    private int _scrollTop;      // 第一行可见的「换行后行号」
    private int _layoutWidth = -1;
    private bool _layoutDirty = true;

    public event EventHandler? SelectionChanged;

    public SidebarView(Func<int, IEnumerable<TuiNode>> childLoader)
    {
        _childLoader = childLoader;
        CanFocus = true;
    }

    public TuiNode? SelectedObject
    {
        get
        {
            if (_rows.Count == 0) return null;
            _sel = Math.Clamp(_sel, 0, _rows.Count - 1);
            return _rows[_sel].Node;
        }
    }

    public void SetFeeds(IEnumerable<TuiNode> feeds)
    {
        _roots.Clear();
        _roots.AddRange(feeds);
        _articles.Clear();
        // 懒加载(百万级适配):启动/刷新时不再对每个源全量加载文章
        // (100 源 × 1 万篇 = 百万 TuiNode,启动即卡死/爆内存);
        // 只加载用户已展开的源,其余在 Toggle 展开时才加载
        var valid = new HashSet<int>(_roots.Select(f => f.FeedId));
        _expanded.RemoveWhere(id => !valid.Contains(id));
        foreach (var f in _roots)
            if (_expanded.Contains(f.FeedId))
                _articles[f.FeedId] = _childLoader(f.FeedId).ToList();
        _sel = 0;
        _scrollTop = 0;
        RebuildRows();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        SetNeedsDraw();
    }

    public void ExpandAll()
    {
        foreach (var f in _roots)
        {
            _expanded.Add(f.FeedId);
            if (!_articles.ContainsKey(f.FeedId))   // 懒加载
                _articles[f.FeedId] = _childLoader(f.FeedId).ToList();
        }
        RebuildRows();
        SetNeedsDraw();
    }

    public void Toggle(TuiNode n)
    {
        if (n == null || !n.IsFeed) return;
        bool nowExpanded = !_expanded.Remove(n.FeedId);
        if (nowExpanded)
        {
            _expanded.Add(n.FeedId);
            if (!_articles.ContainsKey(n.FeedId))   // 懒加载:展开时才拉取该源文章
                _articles[n.FeedId] = _childLoader(n.FeedId).ToList();
        }
        RebuildRows();
        int idx = _rows.FindIndex(r => ReferenceEquals(r.Node, n));
        if (idx >= 0) _sel = idx;
        EnsureSelectedVisible();
        SetNeedsDraw();
    }

    public void MovePageUp() => MoveSelection(-Math.Max(1, Viewport.Height));

    public void MovePageDown() => MoveSelection(Math.Max(1, Viewport.Height));

    public void MoveDown() => MoveSelection(1);

    public void MoveUp() => MoveSelection(-1);

    // 定位到指定文章（外部 CLI 全屏阅读按 W 进完整 TUI 时定位当前文章）；找不到返回 false
    public bool SelectItem(long itemId)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (!_rows[i].IsFeed && _rows[i].Node.ItemId == itemId)
            {
                _sel = i;
                OnSelectionChanged();
                EnsureSelectedVisible();
        return true;
    }
}

        return false;
    }

    // 当前选中文章在全部文章中的位置（不含源行）；选中的是源时返回该源前最后一篇的位置
    public (int Current, int Total) ArticlePosition()
    {
        int cur = 0, total = 0;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].IsFeed) continue;
            total++;
            if (i <= _sel) cur = total;
        }
        return (cur, total);
    }

    void RebuildRows()
    {
        _rows.Clear();
        foreach (var f in _roots)
        {
            _rows.Add(new SidebarRow { Node = f, IsFeed = true });
            if (_expanded.Contains(f.FeedId) && _articles.TryGetValue(f.FeedId, out var arts))
                for (int i = 0; i < arts.Count; i++)
                    _rows.Add(new SidebarRow { Node = arts[i], IsFeed = false, IsLastChild = i == arts.Count - 1 });
        }
        _sel = _rows.Count == 0 ? 0 : Math.Clamp(_sel, 0, _rows.Count - 1);
        _layoutDirty = true;
    }

    void EnsureLayout(int width)
    {
        if (!_layoutDirty && _layoutWidth == width) return;
        _layoutWidth = width;
        _layoutDirty = false;
        foreach (var row in _rows)
        {
            // 树状前缀：源用 ▼/▶ 折叠箭头，文章用 ├/└/│ 表示层级；
            // 前缀和续行缩进都按显示列宽算，保证换行的续行与首行文字对齐
            string prefix, continuation;
            if (row.IsFeed)
            {
                prefix = _expanded.Contains(row.Node.FeedId) ? "▼ " : "▶ ";
                continuation = "  ";
            }
            else
            {
                prefix = row.IsLastChild ? "  └─ " : "  ├─ ";
                continuation = "  │  ";
            }

            // 只对标题本体换行，再分别拼前缀（首行）与续行缩进（其余行）
            int prefixCols = prefix.GetColumns();
            var wrapped = Terminal.Gui.Text.TextFormatter.WordWrapText(row.Node.Title, Math.Max(1, width - prefixCols));
            if (wrapped.Count == 0) wrapped = new List<string> { "" };
            var lines = new List<string>(wrapped.Count);
            for (int i = 0; i < wrapped.Count; i++)
                lines.Add(i == 0 ? prefix + wrapped[i] : continuation + wrapped[i]);
            row.Lines = lines;
        }
        if (_scrollTop >= TotalLines() && TotalLines() > 0)
            _scrollTop = Math.Max(0, TotalLines() - 1);
    }

    int RowStartLine(int rowIndex)
    {
        int line = 0;
        for (int i = 0; i < rowIndex; i++) line += _rows[i].Lines.Count;
        return line;
    }

    int TotalLines()
    {
        int n = 0;
        foreach (var r in _rows) n += r.Lines.Count;
        return n;
    }

    int RowForLine(int line)
    {
        int l = 0;
        for (int i = 0; i < _rows.Count; i++)
        {
            l += _rows[i].Lines.Count;
            if (line < l) return i;
        }
        return _rows.Count - 1;
    }

    void OnSelectionChanged()
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        SetNeedsDraw();
    }

    void MoveSelection(int delta)
    {
        if (_rows.Count == 0) return;
        int target = Math.Clamp(_sel + delta, 0, _rows.Count - 1);
        if (target == _sel) return;
        _sel = target;
        OnSelectionChanged();
        EnsureSelectedVisible();
    }

    void EnsureSelectedVisible()
    {
        if (_rows.Count == 0) return;
        EnsureLayout(Viewport.Width);
        int h = Viewport.Height;
        int start = RowStartLine(_sel);
        int end = start + _rows[_sel].Lines.Count;
        if (start < _scrollTop) _scrollTop = start;
        else if (end > _scrollTop + h) _scrollTop = end - h;
        if (_scrollTop < 0) _scrollTop = 0;
    }

    protected override bool OnKeyDown(Key key)
    {
        switch (key.KeyCode)
        {
            case KeyCode.CursorUp:
                MoveSelection(-1);
                return true;
            case KeyCode.CursorDown:
                MoveSelection(1);
                return true;
            case KeyCode.Home:
                MoveSelection(-_rows.Count);
                return true;
            case KeyCode.End:
                MoveSelection(_rows.Count);
                return true;
        }
        return false;
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (mouse.Flags is MouseFlags.LeftButtonPressed or MouseFlags.LeftButtonClicked)
        {
            if (mouse.Position.HasValue)
            {
                EnsureLayout(Viewport.Width);
                int row = RowForLine(mouse.Position.Value.Y + _scrollTop);
                if (row >= 0 && row < _rows.Count)
                {
                    _sel = row;
                    OnSelectionChanged();
                    EnsureSelectedVisible();
                }
            }
            SetFocus();
            return true;
        }
        return false;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        int w = Viewport.Width;
        int h = Viewport.Height;
        EnsureLayout(w);
        SetAttribute(GetAttributeForRole(VisualRole.Normal));
        for (int y = 0; y < h; y++) AddStr(0, y, new string(' ', w));
        int line = 0;
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            bool selected = i == _sel;
            for (int li = 0; li < row.Lines.Count; li++)
            {
                int sy = line + li - _scrollTop;
                if (sy >= 0 && sy < h)
                {
                    Terminal.Gui.Drawing.Attribute attr = selected
                        ? (HasFocus ? GetAttributeForRole(VisualRole.Focus) : GetAttributeForRole(VisualRole.Active))
                        : (row.IsFeed ? GetAttributeForRole(VisualRole.HotNormal) : GetAttributeForRole(VisualRole.Normal));
                    SetAttribute(attr);
                    AddStr(0, sy, new string(' ', w));
                    AddStr(0, sy, row.Lines[li]);
                }
            }
            line += row.Lines.Count;
        }
        return true;
    }
}

class StartScreenView : View
{
    public string[] Lines { get; set; } = Array.Empty<string>();

    public StartScreenView()
    {
        SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.Black),
            HotNormal = new Terminal.Gui.Drawing.Attribute(StandardColor.BrightYellow, StandardColor.Black),
            Focus = new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.DarkBlue),
            HotFocus = new Terminal.Gui.Drawing.Attribute(StandardColor.BrightYellow, StandardColor.DarkBlue),
            Highlight = new Terminal.Gui.Drawing.Attribute(StandardColor.Black, StandardColor.BrightCyan)
        });
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        int w = Viewport.Width;
        int h = Viewport.Height;
        SetAttribute(GetAttributeForRole(VisualRole.Normal));
        for (int y = 0; y < h; y++) AddStr(0, y, new string(' ', w));
        if (Lines.Length == 0) return true;

        int totalW = 0;
        foreach (var l in Lines)
        {
            int c = l.GetColumns();
            if (c > totalW) totalW = c;
        }
        int x0 = 1;                       // 完全左对齐
        int y0 = 0;                       // 贴左上角
        for (int i = 0; i < Lines.Length; i++)
        {
            int row = y0 + i;
            if (row < 0 || row >= h) continue;
            SetAttribute(GetAttributeForRole(i == 0 ? VisualRole.HotNormal : VisualRole.Normal));
            AddStr(x0, row, Lines[i]);
        }
        return true;
    }
}

class InsightsView : View
{
    public List<InsightsFeed> Feeds { get; private set; } = new();
    public int Selected { get; private set; }
    public event EventHandler? SelectionChanged;

    public int SelectedFeedId => Selected < Feeds.Count ? Feeds[Selected].FeedId : 0;

    public void SetFeeds(List<InsightsFeed> feeds)
    {
        Feeds = feeds;
        Selected = Math.Clamp(Selected, 0, Math.Max(0, Feeds.Count - 1));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        SetNeedsDraw();
    }

    public void MoveTo(int delta)
    {
        if (Feeds.Count == 0) return;
        int before = Selected;
        Selected = Math.Clamp(Selected + delta, 0, Feeds.Count - 1);
        if (Selected != before) { SelectionChanged?.Invoke(this, EventArgs.Empty); SetNeedsDraw(); }
    }

    // 卡片固定 6 行：标题 / 订阅积压 / 打开读完 / 点赞+AI调用 / 观察 / 分隔线
    int CardHeight => 6;

    List<string> CardLines(InsightsFeed x)
    {
        string rate = x.Opened > 0 ? Math.Round(100.0 * x.Completed / x.Opened, 0) + "%" : "—";
        string status = x.Status == Lang.T("正常") ? "" : "  " + x.Status;
        string ai = (x.LlmCalls > 0 || x.EmbeddingCalls > 0) ? $" · AI 摘要 {x.LlmCalls} 次" : "";
        string reasons = x.Reasons.Count > 0 ? "   " + string.Join(" · ", x.Reasons) : "";
        return new List<string>
        {
            $"[{x.FeedId}] {x.Title}{status}",
            $"    订阅 {x.Active} 篇 · 未读积压 {x.Backlog}",
            $"    打开 {x.Opened} · 读完 {x.Completed} · 完成率 {rate} · 跳过 {x.Skipped}",
            $"    ♥ 你点赞 {x.UserLikes} · 🤖 AI 点赞 {x.AiLikes}{ai}",
            reasons,
            "──────────────────────"
        };
    }

    protected override bool OnKeyDown(Key key)
    {
        if (Feeds.Count == 0) return false;
        switch (key.KeyCode)
        {
            case KeyCode.CursorDown:
            case KeyCode.J: MoveTo(1); return true;
            case KeyCode.CursorUp:
            case KeyCode.K: MoveTo(-1); return true;
            case KeyCode.PageDown: MoveTo(Math.Max(1, Viewport.Height / CardHeight)); return true;
            case KeyCode.PageUp: MoveTo(-Math.Max(1, Viewport.Height / CardHeight)); return true;
            case KeyCode.Home: MoveTo(-Feeds.Count); return true;
            case KeyCode.End: MoveTo(Feeds.Count); return true;
            default: return false;
        }
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        int w = Viewport.Width, h = Viewport.Height;
        SetAttribute(GetAttributeForRole(VisualRole.Normal));
        for (int y = 0; y < h; y++) AddStr(0, y, new string(' ', w));
        if (Feeds.Count == 0) return true;

        int cardTop = Math.Max(0, Selected * CardHeight - (h - CardHeight) / 2);
        for (int i = 0; i < Feeds.Count; i++)
        {
            int top = i * CardHeight - cardTop;
            if (top + CardHeight < 0 || top >= h) continue;
            var lines = CardLines(Feeds[i]);
            bool sel = i == Selected;
            for (int k = 0; k < CardHeight; k++)
            {
                int sy = top + k;
                if (sy < 0 || sy >= h) continue;
                SetAttribute(sel && k == 0
                    ? GetAttributeForRole(HasFocus ? VisualRole.Focus : VisualRole.Active)
                    : GetAttributeForRole(sel ? VisualRole.Active : VisualRole.Normal));
                string line = (sel && k == 0 ? "▶ " : "  ") + (k < lines.Count ? lines[k] : "");
                int cols = line.GetColumns();
                if (cols > w) line = line[..Math.Max(0, w - 1)] + "…";
                AddStr(0, sy, line);
            }
        }
        return true;
    }
}

