namespace MarkdownEditor.Export;

/// <summary>
/// 占位导出器，返回「尚未实现」。用于导出菜单在具体导出器实现前可点击。
/// </summary>
public sealed class PlaceholderExporter : IMarkdownExporter
{
    public PlaceholderExporter(string formatId, string displayName, string[] fileExtensions)
    {
        FormatId = formatId;
        DisplayName = displayName;
        FileExtensions = fileExtensions;
    }

    public string FormatId { get; }
    public string DisplayName { get; }
    public string[] FileExtensions { get; }

    public Task<ExportResult> ExportAsync(string markdown, string documentBasePath, string outputPath, ExportOptions? options = null, CancellationToken ct = default)
    {
        return Task.FromResult(new ExportResult(false, "该格式导出尚未实现。"));
    }
}
