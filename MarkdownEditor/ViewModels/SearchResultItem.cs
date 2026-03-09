using System.IO;

namespace MarkdownEditor.ViewModels;

public sealed class SearchResultItem
{
    public string FilePath { get; }
    public int LineNumber { get; }
    public string LinePreview { get; }
    /// <summary>用于紧凑列表显示的文件名。</summary>
    public string FileName => string.IsNullOrEmpty(FilePath) ? "" : Path.GetFileName(FilePath);

    public SearchResultItem(string filePath, int lineNumber, string linePreview)
    {
        FilePath = filePath ?? "";
        LineNumber = lineNumber;
        LinePreview = linePreview ?? "";
    }
}
