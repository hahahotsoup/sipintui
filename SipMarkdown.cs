// ===== RSS 阅读视图:Terminal.Gui Markdown 的子类 =====
//
// 唯一职责:把图片 sixel 在视口里水平居中。
//
// 为什么必须自己做:Terminal.Gui 的 Markdown 把图片按段落布局走,图片的"锚点"是
// 占位文本 "[alt]" 的首字符位置 —— 也就是段落的左对齐起点。但 Markdown 没有
// 任何图片对齐属性,且私有方法 DrawRenderedLine / TryQueueSixel 都不是虚方法,
// 无法 override。
//
// 迂回路径:TryQueueSixel 会把 SixelToRender 塞进 base.Driver.GetSixels() 队列,
// 而 SixelToRender.ScreenPosition 是 public set。Markdown 的 OnDrawingSubViews
// 走完一轮之后,driver 才消费这个队列把 sixel 写到终端 —— 这中间有窗口覆盖
// 行为。
//
// 第一版栽过的坑(见 2026-08-30 项目 memory):
//   - 缓存"自然位置"+每帧重算 → SixelToRender.Id 形如 "url:X:Y",滚动时 drawRow
//     变,Id 变,缓存全部失效,第一遍读到的是"上帧已经加过 offset 的 ScreenPosition",
//     第二遍再 +offset → 累积偏移,图片一路向右飞出视口。
//
// 现在的写法:
//   - 完全不缓存位置信息:每帧用 Markdown 视口在屏幕上的 X 偏移(父 View 不动就
//     稳定) + 居中偏移直接计算,幂等。
//   - alt 文本最小化:由 sipcore 输出 `![\u200B](url)`,base 渲染成 `[\u200B]`
//     —— "[" + ZWSP + "]",仅 2 列可见。**这 2 列接受残留,不做任何涂除。**
//
// 关于"抹掉"alt —— 试过两次,都撤回了(hotsoup 明确要求不要涂):
//   - 第一次:FillRect 涂整行(altWidth 最大 32 列)。base 涂 cell grid 与 driver
//     写 sixel 像素是两条独立路径,中间的"已涂空但 sixel 还没画"窗口有
//     18 行 × 32 列那么大 → 翻页时"图片明显落后于界面" + 间歇性空隙。
//   - 第二次:只涂 `[` `]` 两个字符列(窗口窄 16 倍)。hotsoup 仍然要求撤掉。
//
// 结论:Markdig image 解析路径下 "[]" 2 列是硬下限(GetFallbackText 私有,空 alt
// 会兜底成 "[image]" 6 列更糟)。真要归零只能绕开 Markdig image 解析、改用
// RasterImageCommand 自己提交图片 —— 那是另一套架构,暂不做。
//
// 单位换算(实测确认,见 .probe/ 下的探针):
//   * SixelEncoder.EncodeSixel(Color[W,H]) 输出的头是  ESC P 0;0;0 q "1;1;W;H
//     注意**没有闭合引号** —— 光栅属性由第一个非数字字符(调色板的 '#')终止。
//     所以正则不能写结尾的 "。
//   * W/H 的单位是**像素**,不是单元。验证:Color[20,10] → 头 "1;1;20;10",
//     band 数 2 == ceil(10/6)。
//   * 像素 → 单元:cells = ceil(px / Resolution),Resolution 由终端能力探测得出,
//     默认 10x20(每列 10 像素、每行 20 像素)。见 SixelSupportResult.Resolution
//     和 ImageView 的 GetSixelCellSize()。
//
// 关键依赖:base.OnDrawingSubViews 期间新排队的 sixel,其 Id 形如 "url:X:Y"。
// 用 URL 前缀 + 边界字符 ':' 校验可以反推出这张图属于哪个条目(URL 可能自带
// 端口号 ':',所以光 StartsWith 不够)。
//
// 注意:状态是 static 的。本应用同一时刻只有一个内容 Markdown 视图,够用;
// 若以后多视图同屏,需把 _imageWidths 改成实例字段。
using System;
using System.Collections.Generic;
using System.Drawing;               // Point(ViewportToScreen 用的是它)
using System.Text.RegularExpressions;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

public class SipMarkdown : Markdown
{
    // 缓存:URL -> 图片在终端里的"列宽"(单元数)。切换文章时无需清空 —— 旧 URL
    // 不在视野里也无所谓,新的同 URL 会覆盖。
    static readonly Dictionary<string, int> _imageWidths = new();

    // 解析 sixel 头的 "1;1;W;H"。实测输出形如:ESC P 0;0;0 q "1;1;20;10#0;2;...
    // 没有闭合引号,所以正则到 H 就停。
    static readonly Regex SixelSizeRegex = new(
        "\"1;1;(\\d+);(\\d+)",
        RegexOptions.Compiled);

    /// <summary>由图片加载回调调用,把 URL 和它的终端列宽(单元)登记进注册表。</summary>
    public static void RegisterImage(string url, int widthCells)
    {
        if (widthCells > 0) _imageWidths[url] = widthCells;
    }

    /// <summary>
    /// 后台图片到货时调用(由 ImageSixel 通过 Application.Invoke 排到主循环)。
    /// Tui.cs 负责把它设成触发重绘的动作,例如 () => contentView.SetNeedsDraw()。
    /// </summary>
    public static Action? OnImageReady;

    /// <summary>切换文章时清空,避免旧 URL 一直占着注册表。</summary>
    public static void ClearImageRegistry() => _imageWidths.Clear();

    /// <summary>终端每个单元对应的 sixel 像素数。探测没完成时回落到 10x20。</summary>
    static (int W, int H) SixelResolution
    {
        get
        {
            try
            {
                // Application.Driver 是 legacy 静态入口(已标 Obsolete),但本项目
                // 整体都还在用它(Tui.cs 里也是),保持一致。
#pragma warning disable CS0618
                var r = Application.Driver?.SixelSupport?.Resolution;
#pragma warning restore CS0618
                if (r.HasValue && r.Value.Width > 0 && r.Value.Height > 0)
                    return (r.Value.Width, r.Value.Height);
            }
            catch { /* driver 还没起来,用默认值 */ }
            return (10, 20);
        }
    }

    /// <summary>把 sixel 头里的像素宽高换算成终端单元数。</summary>
    static (int W, int H) PixelsToCells(int pxW, int pxH)
    {
        var res = SixelResolution;
        return (
            Math.Max(1, (int)Math.Ceiling(pxW / (double)res.W)),
            Math.Max(1, (int)Math.Ceiling(pxH / (double)res.H)));
    }

    /// <summary>
    /// 从 sixel 载荷里解析出图片占多少终端单元。返回 (0,0) 表示解析失败。
    /// </summary>
    public static (int WidthCells, int HeightCells) ParseSixelSizeCells(string sixelUtf8)
    {
        var m = SixelSizeRegex.Match(sixelUtf8);
        if (!m.Success) return (0, 0);
        return PixelsToCells(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    protected override bool OnDrawingSubViews(DrawContext? context)
    {
        bool result = base.OnDrawingSubViews(context);

        // Driver 不是 View 上的属性 —— Terminal.Gui 内部一律走 App?.Driver。
        var driver = App?.Driver;
        if (driver == null) return result;

        int viewW = Viewport.Width;
        if (viewW <= 0) return result;
        var queue = driver.GetSixels();

        // Markdown 视口在屏幕上的 X 偏移。父 View 不动就稳定。
        // 把六性放在这里 + 居中偏移,就完全跳过了 base 写入的 ScreenPosition,
        // 无论 base 因为 Id 变化重入了多少对象,结果都收敛到同一个屏幕位置。
        int mdScreenX = ViewportToScreen(new Point(0, 0)).X;

        foreach (var sixel in queue)
        {
            if (sixel.Id == null) continue;

            // 反查这张六性的列宽(单元)。Id 形如 "url:X:Y",URL 前缀 + ':' 边界
            // 校验。URL 可能含 ':'(端口号),所以要再确认边界字符确实是 ':'。
            int widthCells = 0;
            foreach (var kv in _imageWidths)
            {
                if (sixel.Id.Length > kv.Key.Length &&
                    sixel.Id[kv.Key.Length] == ':' &&
                    sixel.Id.StartsWith(kv.Key))
                {
                    widthCells = kv.Value;
                    break;
                }
            }
            if (widthCells <= 0) continue;

            int offset = Math.Max(0, (viewW - widthCells) / 2);
            // 只动 X。sixel 从光标位置向右下铺像素,改 Y 会盖到别的内容上。
            // Y 保留 base 写入的值(等于行内 Y,即 viewPosition.Y)。
            sixel.ScreenPosition = new Point(mdScreenX + offset, sixel.ScreenPosition.Y);
        }

        return result;
    }
}
