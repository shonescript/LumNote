using MarkdownEditor.Engine.Layout.Blocks;

namespace MarkdownEditor.Engine.Layout;

/// <summary>
/// 块级布局器注册表 - 创建默认的 IBlockLayouter 列表，供布局引擎按节点类型调度（类似 Chromium 的 RenderObject 按类型分发 layout）。
/// 扩展新块类型时在此注册即可。
/// </summary>
public static class BlockLayouterRegistry
{
    /// <summary>创建默认布局器列表；传入的 environment 可供需要上下文的布局器使用。</summary>
    public static IBlockLayouter[] CreateDefault(ILayoutEnvironment environment)
    {
        return
        [
            new HeadingBlockLayouter(),
            // 其余块类型仍由 SkiaLayoutEngine.LayoutWithSwitch 处理，后续可逐步拆为独立 Layouter
        ];
    }
}
