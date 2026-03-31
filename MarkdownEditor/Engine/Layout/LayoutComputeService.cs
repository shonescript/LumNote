using MarkdownEditor.Core;
using MarkdownEditor.Engine.Document;

namespace MarkdownEditor.Engine.Layout;

/// <summary>
/// 纯布局计算服务：从块列表快照计算 LayoutBlocksSnapshot，可在后台线程执行。
/// 不依赖 RenderEngine 内部状态，仅使用 ILayoutEngine 进行测量与布局。
/// </summary>
public static class LayoutComputeService
{
    /// <summary>
    /// 从已归一化的块列表计算完整布局快照。
    /// </summary>
    /// <param name="blocks">归一化后的 AST 块列表（与 ranges 一一对应）</param>
    /// <param name="ranges">每块对应的源码行范围</param>
    /// <param name="width">内容区域宽度</param>
    /// <param name="layoutEngine">布局引擎</param>
    /// <param name="config">引擎配置（BlockIndent、ExtraBottomPadding 等）</param>
    public static LayoutBlocksSnapshot ComputeFull(
        IReadOnlyList<MarkdownNode?> blocks,
        IReadOnlyList<(int startLine, int endLine)> ranges,
        float width,
        ILayoutEngine layoutEngine,
        EngineConfig config)
    {
        if (blocks == null || blocks.Count == 0)
        {
            return new LayoutBlocksSnapshot(
                [],
                [0f],
                config.ExtraBottomPadding,
                Math.Max(1, width - config.BlockIndent) + config.BlockIndent,
                (-1, -1));
        }

        float blockIndent = config.BlockIndent;
        var contentWidth = Math.Max(1, width - blockIndent);
        float maxRight = contentWidth;

        // 第一遍：计算每块高度并布局，得到 LayoutBlock
        var cum = new float[blocks.Count + 1];
        cum[0] = 0;
        var layoutBlocks = new List<LayoutBlock>(blocks.Count);

        for (int i = 0; i < blocks.Count; i++)
        {
            var ast = blocks[i];
            if (ast == null)
            {
                cum[i + 1] = cum[i];
                continue;
            }

            var (startLine, endLine) = i < ranges.Count ? ranges[i] : (0, 0);
            var lb = layoutEngine.Layout(ast, contentWidth, i, startLine, endLine);
            float h = lb.Bounds.Height;
            cum[i + 1] = cum[i] + h;

            lb.SetGlobalBounds(blockIndent, cum[i], width, cum[i] + h);
            layoutBlocks.Add(lb);

            if (lb.Lines != null)
            {
                foreach (var line in lb.Lines)
                {
                    foreach (var run in line.Runs)
                    {
                        if (run.Bounds.Right > maxRight)
                            maxRight = run.Bounds.Right;
                    }
                }
            }
        }

        // 裁掉末尾无可见行的块（与 EnsureLayoutFull 行为一致）
        float totalY = cum[blocks.Count];
        for (int i = layoutBlocks.Count - 1; i >= 0; i--)
        {
            var b = layoutBlocks[i];
            if (b.Lines == null || b.Lines.Count == 0)
            {
                totalY -= b.Bounds.Height;
                layoutBlocks.RemoveAt(i);
            }
            else
            {
                break;
            }
        }

        float contentWidthResult = Math.Max(contentWidth, maxRight);
        float totalHeight = totalY + config.ExtraBottomPadding;

        return new LayoutBlocksSnapshot(
            layoutBlocks,
            cum,
            totalHeight,
            contentWidthResult,
            (-1, -1));
    }

    /// <summary>
    /// 长文档轻量布局：先用行数估计高度得到 cum，仅对可见窗口内的块做完整 Layout。
    /// 用于块数超过阈值时，避免全量 Skia 测量导致的数分钟卡顿。
    /// </summary>
    /// <param name="previousCum">上一帧的累积高度数组，长度需为 blocks.Count+1 才使用；用于保持布局一致性，消除滚动跳动。</param>
    public static LayoutBlocksSnapshot ComputeSlim(
        IReadOnlyList<MarkdownNode?> blocks,
        IReadOnlyList<(int startLine, int endLine)> ranges,
        float width,
        float scrollY,
        float viewportHeight,
        ILayoutEngine layoutEngine,
        EngineConfig config,
        float[]? previousCum = null)
    {
        if (blocks == null || blocks.Count == 0)
        {
            return new LayoutBlocksSnapshot(
                [],
                [0f],
                config.ExtraBottomPadding,
                Math.Max(1, width - config.BlockIndent) + config.BlockIndent,
                (0, 0));
        }

        float blockIndent = config.BlockIndent;
        var contentWidth = Math.Max(1, width - blockIndent);
        float lineHeight = config.BaseFontSize * config.LineSpacing;
        float maxRight = contentWidth;

        // 以 previousCum 为基准（若长度匹配），否则用行数估计 cum，保持布局一致性消除滚动跳动
        float[] cum;
        float estimatedTotalHeight;
        if (previousCum != null && previousCum.Length == blocks.Count + 1)
        {
            cum = (float[])previousCum.Clone();
            estimatedTotalHeight = cum[blocks.Count] + config.ExtraBottomPadding;
        }
        else
        {
            cum = new float[blocks.Count + 1];
            cum[0] = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                var (startLine, endLine) = i < ranges.Count ? ranges[i] : (0, 0);
                int lineCount = Math.Max(1, endLine - startLine);
                float estH = lineCount * lineHeight;
                cum[i + 1] = cum[i] + estH;
            }
            estimatedTotalHeight = cum[blocks.Count] + config.ExtraBottomPadding;
        }

        // 根据文档总高度与视口高度动态确定预渲染窗口：
        // - 短文档（总高度 <= 10 个视口）：一次性覆盖整篇，相当于“大缓存区”；
        // - 长文档：上下各约 1.5 个视口，保持 ComputeSlim 原有行为。
        float margin;
        float yStart;
        float yEnd;
        bool isShortDocument = viewportHeight > 0 &&
                               estimatedTotalHeight <= viewportHeight * 10;

        if (isShortDocument)
        {
            // 整篇文档都纳入可见窗口，后续 visibleStart/visibleEnd 会扩展到 0..blocks.Count
            margin = estimatedTotalHeight;
            yStart = 0;
            yEnd = estimatedTotalHeight;
        }
        else
        {
            // 预渲染上下各约 1.5 视口，便于低速/正常滚动时流畅预览
            margin = viewportHeight * 1.5f;
            yStart = Math.Max(0, scrollY - margin);
            yEnd = scrollY + viewportHeight + margin;
        }

        int visibleStart = 0;
        while (visibleStart < cum.Length - 1 && cum[visibleStart + 1] <= yStart)
            visibleStart++;
        int visibleEnd = visibleStart;
        while (visibleEnd < cum.Length - 1 && cum[visibleEnd] < yEnd)
            visibleEnd++;
        visibleEnd = Math.Min(visibleEnd + 1, blocks.Count);
        if (visibleStart >= visibleEnd)
        {
            visibleStart = 0;
            visibleEnd = Math.Max(1, blocks.Count);
        }

        // 第二遍：仅对可见块做完整 Layout；块高写入 heights[] 后一次前缀和重建 cum，避免每块 O(n) 尾部修正。
        var heights = new float[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
            heights[i] = cum[i + 1] - cum[i];

        var layoutPairs = new List<(int Index, LayoutBlock Block)>(visibleEnd - visibleStart);
        for (int i = visibleStart; i < visibleEnd; i++)
        {
            var ast = blocks[i];
            if (ast == null)
                continue;

            var (startLine, endLine) = i < ranges.Count ? ranges[i] : (0, 0);
            var lb = layoutEngine.Layout(ast, contentWidth, i, startLine, endLine);
            heights[i] = lb.Bounds.Height;
            layoutPairs.Add((i, lb));
        }

        cum[0] = 0;
        for (int i = 0; i < blocks.Count; i++)
            cum[i + 1] = cum[i] + heights[i];
        LayoutDiagnostics.OnComputeSlimCumulativePass();

        var layoutBlocks = new List<LayoutBlock>(layoutPairs.Count);
        foreach (var (i, lb) in layoutPairs)
        {
            float y0 = cum[i];
            float h = heights[i];
            lb.SetGlobalBounds(blockIndent, y0, width, y0 + h);
            layoutBlocks.Add(lb);

            if (lb.Lines != null)
            {
                foreach (var line in lb.Lines)
                {
                    foreach (var run in line.Runs)
                    {
                        if (run.Bounds.Right > maxRight)
                            maxRight = run.Bounds.Right;
                    }
                }
            }
        }

        float rawTotal = cum[blocks.Count] + config.ExtraBottomPadding;
        // 使用实际高度与估算高度中的较大值，去掉 500 像素对齐，避免短文档底部出现大段“空白滚动区”。
        float totalHeight = Math.Max(estimatedTotalHeight, rawTotal);
        float contentWidthResult = Math.Max(contentWidth, maxRight);

        return new LayoutBlocksSnapshot(
            layoutBlocks,
            cum,
            totalHeight,
            contentWidthResult,
            (visibleStart, visibleEnd));
    }

    /// <summary>估算单块高度（行数 × 行高），用于 ComputeSlim 的快速 cum。</summary>
    internal static float EstimateBlockHeight(int startLine, int endLine, EngineConfig config)
    {
        float lineHeight = config.BaseFontSize * config.LineSpacing;
        int lineCount = Math.Max(1, endLine - startLine);
        return lineCount * lineHeight;
    }
}
