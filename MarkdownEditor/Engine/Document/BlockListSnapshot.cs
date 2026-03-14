using MarkdownEditor.Core;

namespace MarkdownEditor.Engine.Document;

/// <summary>
/// 不可变的块列表快照：包含解析后的 AST 块及其对应的源码行范围。
/// 由 IncrementalParseManager 产出，供 RenderEngine 与 LayoutTaskScheduler 只读使用。
/// </summary>
public sealed class BlockListSnapshot
{
    /// <summary>按文档顺序排列的 AST 块（可为 null 表示占位）。</summary>
    public IReadOnlyList<MarkdownNode?> Blocks { get; }

    /// <summary>与 Blocks 一一对应的源码行范围 (StartLine, EndLine)。</summary>
    public IReadOnlyList<(int startLine, int endLine)> Ranges { get; }

    public BlockListSnapshot(
        IReadOnlyList<MarkdownNode?> blocks,
        IReadOnlyList<(int startLine, int endLine)> ranges)
    {
        Blocks = blocks ?? [];
        Ranges = ranges ?? [];
    }

    public int Count => Blocks.Count;
}
