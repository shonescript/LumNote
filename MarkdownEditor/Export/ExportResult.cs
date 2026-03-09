namespace MarkdownEditor.Export;

/// <summary>
/// 导出操作结果。
/// </summary>
public record ExportResult(bool Success, string? ErrorMessage = null);
