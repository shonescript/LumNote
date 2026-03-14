using MarkdownEditor.Core;
using MarkdownEditor.Engine.Document;

namespace MarkdownEditor.Engine.Layout;

/// <summary>
/// 块级布局上下文 - 传入每个 BlockLayouter，包含可用宽度、块索引、行范围及布局环境（类似 DOM 节点的 layout 入参）。
/// </summary>
public readonly struct BlockLayoutContext
{
    public float Width { get; }
    public int BlockIndex { get; }
    public int StartLine { get; }
    public int EndLine { get; }
    public ILayoutEnvironment Environment { get; }

    public BlockLayoutContext(float width, int blockIndex, int startLine, int endLine, ILayoutEnvironment environment)
    {
        Width = width;
        BlockIndex = blockIndex;
        StartLine = startLine;
        EndLine = endLine;
        Environment = environment;
    }
}
