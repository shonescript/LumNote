using MarkdownEditor.Engine.Layout;

namespace MarkdownEditor.Engine;

/// <summary>
/// 与渲染器一致的文本测量接口。表格布局使用此接口测量时，可与绘制宽度一致，避免文字被裁切。
/// </summary>
public interface ITextMeasurer
{
    /// <summary>使用与渲染阶段相同的字体测量文本宽度（含行内公式的 8px 预留）。</summary>
    float MeasureText(string text, RunStyle style);
}
