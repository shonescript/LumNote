using MarkdownEditor.Engine.Document;
using MarkdownEditor.Engine.Layout;

namespace MarkdownEditor.Engine.Render;

/// <summary>
/// 块级绘制器 - 每个块种类实现此接口，负责绘制自身背景与内容（类似 Chromium 中 RenderBlock 的 paint 职责）。
/// 统一接口便于扩展与稳定渲染。
/// </summary>
public interface IBlockPainter
{
    /// <summary>是否处理该 BlockKind。</summary>
    bool Matches(BlockKind kind);

    /// <summary>绘制该块：先绘制块级装饰（背景/边框等），再按行绘制 Runs 与选区。</summary>
    void Paint(LayoutBlock block, BlockPaintContext ctx);
}
