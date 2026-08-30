// ===== RSS 文章图片 -> Sixel 编码 =====
//
// Terminal.Gui 的 Markdown.ImageLoader 要求回调返回**已编码的 sixel 字节**，
// 而不是原始图片字节。官方 XML 注释原文：
//     "Gets or sets an optional callback that loads image data
//      as UTF-8 encoded sixel payloads."
//
// 但 Terminal.Gui 自带 SixelEncoder（编码）却不带任何图像解码器（没有
// ImageSharp / SkiaSharp 依赖）。所以"PNG/JPEG/GIF/WebP 字节 -> 像素"这一步
// 必须由调用方自己补齐，否则把原始 PNG 喂进去是出不来图的。
//
// 单独放一个文件的原因：ImageSharp 和 Terminal.Gui 都有 Color / Image 这些名字，
// 混在 Tui.cs 里会撞名。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Terminal.Gui.Drawing;
using SixLabors.ImageSharp.Processing;

public partial class Program
{
    // 复用一个 HttpClient。每次 new 一个会在高频滚动时耗尽 socket（TIME_WAIT 堆积）。
    static readonly HttpClient ImageHttpClient = new() { Timeout = TimeSpan.FromSeconds(8) };

    // SixelEncoder.EncodeSixel 是实例方法，复用一个实例即可（无状态依赖）。
    static readonly SixelEncoder SixelEnc = new();

    // SixelEncoder 的 Quantizer / AvoidBottomScroll 保持默认：
    // 默认量化器已经会按终端上报的 MaxPaletteColors 收敛，不在这儿重复造轮子。

    /// <summary>
    /// 终端每个单元对应的 sixel 像素数(宽, 高)。探测没完成时回落 10x20
    /// —— 这也是 Terminal.Gui 里 SixelSupportResult.Resolution 的默认值。
    /// </summary>
    static (int W, int H) SixelCellPixelSize()
    {
        try
        {
            // Application.Driver 是 legacy 静态入口(已标 Obsolete),本项目整体
            // 仍在用(Tui.cs 里也是),保持一致。
#pragma warning disable CS0618
            var r = Terminal.Gui.App.Application.Driver?.SixelSupport?.Resolution;
#pragma warning restore CS0618
            if (r.HasValue && r.Value.Width > 0 && r.Value.Height > 0)
                return (r.Value.Width, r.Value.Height);
        }
        catch { /* driver 还没起来,用默认值 */ }
        return (10, 20);
    }

    // 日志默认完全关闭。往 stderr 打印会直接糊在 TUI 画面上 —— 全屏应用里
    // stderr 就是终端本身。需要排查时设 SIP_SIXEL_DEBUG=1，日志落到临时文件。
    static void SixelLog(string msg)
    {
        if (Environment.GetEnvironmentVariable("SIP_SIXEL_DEBUG") != "1") return;
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "sip-sixel.log"),
                $"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        }
        catch { /* 日志失败绝不能影响渲染 */ }
    }

    /// <summary>
    /// Markdown.ImageLoader 的回调。**只查缓存,绝不在这里做网络/解码** ——
    /// 这个函数跑在渲染线程上,任何阻塞都会让整屏冻住(实测打开带图文章会明显卡)。
    ///
    /// 未命中时:让后台去取(见 DownloadAndCache),本帧先返回 null 不画图。
    /// 图到货后由 SipMarkdown.OnImageReady 通知重绘。
    /// </summary>
    static byte[]? LoadImageAsSixel(string url, string baseUrl)
    {
        try
        {
            // 相对路径兜底：用当前文章的 URL 作为 base 解析
            if (!Uri.TryCreate(url, UriKind.Absolute, out _) && !string.IsNullOrWhiteSpace(baseUrl))
            {
                try { url = new Uri(new Uri(baseUrl), url).AbsoluteUri; }
                catch { }
            }

            if (TuiImageCache.Map.TryGetValue(url, out var cached))
            {
                // 每帧都会走到这里,所以只做 O(1) 的字典写入,绝不解字符串。
                // 重登记是必要的:切换文章时注册表被清空过,不补就找不回列宽。
                SipMarkdown.RegisterImage(url, cached.WidthCells);
                return cached.Sixel;
            }

            // 缓存没货:后台去取,本帧先空着。渲染线程一毫秒都不等。
            PrefetchInBackground(url);
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 提前把图片拉下来。切文章 / 预读时调用,让首次打开带图文章不再卡顿。
    /// 不阻塞调用方 —— 只是把任务丢给线程池。
    /// </summary>
    public static void PrefetchImages(IEnumerable<string> urls, string baseUrl)
    {
        if (urls == null) return;
        foreach (var u in urls)
        {
            if (string.IsNullOrWhiteSpace(u)) continue;
            string url = u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out _) && !string.IsNullOrWhiteSpace(baseUrl))
            {
                try { url = new Uri(new Uri(baseUrl), url).AbsoluteUri; }
                catch { }
            }
            PrefetchInBackground(url);
        }
    }

    /// <summary>把单个 URL 丢给后台,已缓存或已在下载中则跳过。</summary>
    static void PrefetchInBackground(string url)
    {
        if (TuiImageCache.Map.ContainsKey(url)) return;
        if (!TuiImageCache.InFlight.TryAdd(url, 0)) return;   // 已在下载
        _ = Task.Run(() =>
        {
            try { DownloadAndCache(url); }
            finally { TuiImageCache.InFlight.TryRemove(url, out _); }
        });
    }

    /// <summary>
    /// 取图片字节:同时支持 http(s) 与本地文件。
    /// - http(s):走复用 HttpClient(8s 超时)。
    /// - file:// 或裸本地路径:直接读磁盘。导入的电子书图片就是这么存的,
    ///   HttpClient 抓不了 file://,必须单独处理。
    /// 失败返回 null(调用方跳过该图,不阻塞渲染)。
    /// </summary>
    static byte[]? FetchImageBytes(string url)
    {
        // 本地文件:file:// 或裸路径。Uri.LocalPath 在 Windows 上把
        // file:///C:/x.png 正确转成 C:\x.png,Linux 上 /home/x.png 原样返回。
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "file")
        {
            try { var b = File.ReadAllBytes(uri.LocalPath); return b.Length == 0 ? null : b; }
            catch (Exception ex) { SixelLog($"  file read fail {uri.LocalPath}: {ex.Message}"); return null; }
        }
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try { var b = File.ReadAllBytes(url); return b.Length == 0 ? null : b; }
            catch (Exception ex) { SixelLog($"  local read fail {url}: {ex.Message}"); return null; }
        }
        // http(s)
        try
        {
            var resp = ImageHttpClient.GetAsync(url).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) { SixelLog($"  HTTP {(int)resp.StatusCode}"); return null; }
            var raw = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return raw.Length == 0 ? null : raw;
        }
        catch (Exception ex) { SixelLog($"  http exception: {ex.Message}"); return null; }
    }

    /// <summary>后台线程:下载/读盘 -> 解码 -> sixel 编码 -> 入缓存 -> 通知重绘。</summary>
    static void DownloadAndCache(string url)
    {
        if (TuiImageCache.Map.ContainsKey(url)) return;
        try
        {
            SixelLog($"GET {url}");
            var raw = FetchImageBytes(url);
            if (raw is null) return;

            var sixel = EncodeToSixel(raw);
            if (sixel is null || sixel.Length == 0)
            {
                SixelLog("  encode failed (unsupported/corrupt image)");
                return;
            }

            SixelLog($"  ok {raw.Length}B image -> {sixel.Length}B sixel");

            // 把图片在终端里的列宽登记给 SipMarkdown,子类靠它做水平居中。
            // 头格式是 ESC P 0;0;0 q "1;1;W;H(无闭合引号),W/H 单位是像素,
            // ParseSixelSizeCells 会按终端每单元像素数换算成单元。
            var sixelText = System.Text.Encoding.UTF8.GetString(sixel);
            var (widthCells, heightCells) = SipMarkdown.ParseSixelSizeCells(sixelText);
            SixelLog($"  {widthCells}x{heightCells} cells");

            TuiImageCache.Map[url] = (sixel, widthCells);
            SipMarkdown.RegisterImage(url, widthCells);

            // 通知 UI 重绘。Application.Invoke 会把动作排到主循环里执行,
            // 这是在后台线程里碰 UI 的唯一安全方式。
            try
            {
#pragma warning disable CS0618   // legacy 静态入口,本项目整体都还在用
                Terminal.Gui.App.Application.Invoke(() => SipMarkdown.OnImageReady?.Invoke());
#pragma warning restore CS0618
            }
            catch { /* 应用可能正在退出 */ }
        }
        catch (Exception ex)
        {
            SixelLog($"  exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>图片字节 -> sixel 字节。失败返回 null。</summary>
    static byte[]? EncodeToSixel(byte[] imageBytes)
    {
        try
        {
            using var img = SixLabors.ImageSharp.Image
                .Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(imageBytes);

            // 尺寸单位很绕,这里说清楚:
            //   * SixelEncoder 把 Color[,] 的维度**当像素**写进 sixel 头 "1;1;W;H"。
            //   * 终端按  单元数 = ceil(像素 / 每单元像素数)  来铺,每单元像素数由
            //     终端能力探测给出,默认 10(宽) x 20(高)。
            // 所以想让图片占 N 个终端单元,数组维度得是 N * 每单元像素数。
            //
            // 目标:最多 60 单元宽 x 18 单元高。60 单元在多数终端里约占正文区
            // 六到八成宽,居中后两边留白;18 单元高约是正文区高度的一半。
            const int MaxWidthCells = 60;
            const int MaxHeightCells = 18;
            var (cellPxW, cellPxH) = SixelCellPixelSize();
            int maxW = MaxWidthCells * cellPxW;
            int maxH = MaxHeightCells * cellPxH;

            // sixel 是逐像素逐列编码的,原图直出体积会爆炸(动辄几 MB 的转义
            // 序列),编码耗时也会明显卡顿,所以先缩到终端能用的尺寸。
            int w = img.Width, h = img.Height;
            if (w > maxW) { h = Math.Max(1, h * maxW / w); w = maxW; }
            if (h > maxH) { w = Math.Max(1, w * maxH / h); h = maxH; }
            if (w != img.Width || h != img.Height)
            {
                img.Mutate(x => x.Resize(w, h));
            }

            var pixels = new Color[w, h];

            img.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        var p = row[x];

                        if (p.A == 255)
                        {
                            pixels[x, y] = new Color(p.R, p.G, p.B);
                        }
                        else
                        {
                            // Terminal.Gui 的 Color 不支持 alpha（文档明确写了
                            // "Alpha channel is not currently supported"），
                            // 透明像素要先压到背景色上，否则透明区会变成脏色块。
                            float a = p.A / 255f;
                            pixels[x, y] = new Color(
                                (int)(p.R * a),
                                (int)(p.G * a),
                                (int)(p.B * a));
                        }
                    }
                }
            });

            // EncodeSixel 返回的是 string；ImageLoader 契约要的是字节
            // （"UTF-8 encoded sixel payloads"），所以这里再转一次。
            var sixel = SixelEnc.EncodeSixel(pixels);
            return string.IsNullOrEmpty(sixel) ? null : Encoding.UTF8.GetBytes(sixel);
        }
        catch
        {
            return null;
        }
    }
}
