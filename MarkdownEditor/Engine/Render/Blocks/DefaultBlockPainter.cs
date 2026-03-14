using MarkdownEditor.Engine.Document;
using MarkdownEditor.Engine.Layout;

namespace MarkdownEditor.Engine.Render.Blocks;

/// <summary>
/// 默认块绘制器 - 先绘制块级装饰（按 BlockKind），再统一绘制块内所有 Run 与选区（类似 Chromium 的 paint 顺序：背景 → 内容）。
/// 匹配所有 BlockKind，由 BlockPainterRegistry 作为默认使用。
/// </summary>
public sealed class DefaultBlockPainter : IBlockPainter
{
    public bool Matches(BlockKind kind) => true;

    public void Paint(LayoutBlock block, BlockPaintContext ctx)
    {
        ctx.DrawBlockBackground();
        ctx.DrawBlockContent();
    }
}
