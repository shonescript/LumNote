using MarkdownEditor.Engine.Render.Blocks;

namespace MarkdownEditor.Engine.Render;

/// <summary>
/// 块级绘制器注册表 - 创建默认的 IBlockPainter 列表，供渲染器按 BlockKind 调度（类似 Chromium 的 RenderObject 按类型分发 paint）。
/// 扩展新块样式时在此注册或增加专用 Painter 即可。
/// </summary>
public static class BlockPainterRegistry
{
    /// <summary>创建默认绘制器列表；当前由 DefaultBlockPainter 统一处理所有种类，后续可拆为按 Kind 的专用 Painter。</summary>
    public static IBlockPainter[] CreateDefault()
    {
        return [new DefaultBlockPainter()];
    }
}
