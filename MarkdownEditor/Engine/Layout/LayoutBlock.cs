using MarkdownEditor.Engine.Document;
using SkiaSharp;

namespace MarkdownEditor.Engine.Layout;

/// <summary>
/// 已布局块 - 包含测量后的行与运行
/// 供渲染器批量绘制，支持选区命中。
/// 布局引擎只填充尺寸（Bounds 的 Width/Height 或等价）；全局位置由 RenderEngine 通过 SetGlobalBounds 统一设置。
/// </summary>
public sealed class LayoutBlock
{
    public int BlockIndex { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public SKRect Bounds { get; set; }
    public List<LayoutLine> Lines { get; } = [];
    public BlockKind Kind { get; set; }
    /// <summary>代码块/HTML 块的内容实际宽度（用于块内横向滚动条）。未设置为 0。</summary>
    public float ContentWidth { get; set; }

    /// <summary>由渲染引擎统一设置块在文档中的全局矩形（布局阶段只产出尺寸）。</summary>
    public void SetGlobalBounds(float left, float top, float right, float bottom)
    {
        Bounds = new SKRect(left, top, right, bottom);
    }
    /// <summary> 表格网格信息，用于绘制线框和底纹 </summary>
    public TableLayoutInfo? TableInfo { get; set; }
}

/// <summary>
/// 表格布局信息 - 列宽、行高、单元格矩形
/// </summary>
public sealed class TableLayoutInfo
{
    public float[] ColumnWidths { get; set; } = [];
    public float[] RowHeights { get; set; } = [];
    public int ColCount { get; set; }
    public int RowCount { get; set; }
}

public sealed class LayoutLine
{
    public float Y { get; set; }
    public float Height { get; set; }
    public List<LayoutRun> Runs { get; } = [];
}
