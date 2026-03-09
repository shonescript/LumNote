using MarkdownEditor.Core;
using MarkdownEditor.Engine.Document;

namespace MarkdownEditor.Engine.Layout;

/// <summary>
/// 布局引擎 - 将 AST 块转为可绘制布局
/// 支持增量、缓存、对象池
/// </summary>
public interface ILayoutEngine
{
    /// <summary>
    /// 布局单个块。
    /// </summary>
    LayoutBlock Layout(MarkdownNode node, float width, int blockIndex, int startLine, int endLine);

    /// <summary>
    /// 可用宽度变化时是否需重新布局
    /// </summary>
    bool RequiresRelayoutOnWidthChange { get; }

    /// <summary>
    /// 测量文本内 x 位置对应的字符偏移（用于命中测试）
    /// </summary>
    int MeasureTextOffset(string text, float x, RunStyle style);
}
