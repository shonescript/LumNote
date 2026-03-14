using MarkdownEditor.Core;

namespace MarkdownEditor.Engine.Layout;

/// <summary>
/// 块级布局器 - 每个块类型实现此接口，负责将自己的 AST 节点布局为 LayoutBlock（类似 Chromium 中 RenderBlock 的 layout 职责）。
/// 统一接口便于扩展与增量布局。
/// </summary>
public interface IBlockLayouter
{
    /// <summary>是否处理该节点类型。</summary>
    bool Matches(MarkdownNode node);

    /// <summary>对当前块进行布局，返回填充好的 LayoutBlock（尺寸以本地坐标为准，全局位置由 RenderEngine 设置）。</summary>
    LayoutBlock Layout(MarkdownNode node, in BlockLayoutContext ctx);
}
