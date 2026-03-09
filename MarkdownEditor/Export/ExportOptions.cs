namespace MarkdownEditor.Export;

/// <summary>
/// 统一导出选项（页宽、边距、是否含 TOC 等）。
/// </summary>
public record ExportOptions(
    int? PageWidthPx = null,
    float? MarginPt = null
);
