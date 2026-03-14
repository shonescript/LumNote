using System.Collections.Generic;

namespace MarkdownEditor.Engine.Layout;

/// <summary>
/// 布局结果快照：包含一段连续块的 LayoutBlock 列表及其聚合信息，
/// 可从后台线程计算完成后传回 UI 线程，供 RenderEngine/SkiaRenderer 只读使用。
/// </summary>
public sealed class LayoutBlocksSnapshot
{
    /// <summary>按文档顺序排列的布局块。</summary>
    public IReadOnlyList<LayoutBlock> Blocks { get; }

    /// <summary>累积高度数组 Y[i] = 0..i-1 块高度之和，Y[0] = 0。</summary>
    public IReadOnlyList<float> CumulativeY { get; }

    /// <summary>文档总高度（含额外底部留白）。</summary>
    public float TotalHeight { get; }

    /// <summary>内容区域实际最大宽度（用于横向滚动）。</summary>
    public float ContentWidth { get; }

    /// <summary>当前增量布局窗口 [start, end)，对应 Blocks 中覆盖的块索引范围；全量布局时为 (-1,-1)。</summary>
    public (int start, int end) LayoutWindow { get; }

    public LayoutBlocksSnapshot(
        IReadOnlyList<LayoutBlock> blocks,
        IReadOnlyList<float> cumulativeY,
        float totalHeight,
        float contentWidth,
        (int start, int end) layoutWindow)
    {
        Blocks = blocks;
        CumulativeY = cumulativeY;
        TotalHeight = totalHeight;
        ContentWidth = contentWidth;
        LayoutWindow = layoutWindow;
    }
}

