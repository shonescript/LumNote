using System;

namespace MarkdownEditor.ViewModels;

public sealed class DocumentItem
{
    public string FullPath { get; }
    public string RelativePath { get; }
    public string DisplayName => string.IsNullOrEmpty(FullPath) ? RelativePath : System.IO.Path.GetFileName(FullPath);

    /// <summary>Tab 标签中使用的文档图标（与文件树保持一致）。</summary>
    public string IconText => "📄";

    /// <summary>
    /// 当前文档在内存中的内容缓存，用于多标签快速切换。
    /// </summary>
    public string? CachedMarkdown { get; set; }

    /// <summary>
    /// 当前文档是否在编辑选项卡中打开。
    /// </summary>
    public bool IsOpen { get; set; }

    /// <summary>
    /// 当前文档是否有未保存修改。
    /// </summary>
    public bool IsModified { get; set; }

    /// <summary>
    /// 上次从磁盘加载或保存时记录的写入时间，用于检测外部修改（不占用文件句柄）。
    /// </summary>
    public DateTime? LastKnownWriteTimeUtc { get; set; }

    /// <summary>本文档的编辑区缩放（1.0=100%），切换回来时恢复。</summary>
    public double EditorZoomLevel { get; set; } = 1.0;

    /// <summary>本文档的预览区缩放（1.0=100%），切换回来时恢复。</summary>
    public double PreviewZoomLevel { get; set; } = 1.0;

    /// <summary>本文档当前使用的编码名（如 UTF-8、GBK），用于重读与保存。</summary>
    public string EncodingName { get; set; } = "UTF-8";

    /// <summary>编辑区内上次光标偏移（用于 Alt+Left/Right 在同一文档内跳转）。</summary>
    public int LastCaretOffset { get; set; }

    public DocumentItem(string fullPath, string relativePath)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
    }
}
