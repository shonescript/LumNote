using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace MarkdownEditor.Helpers;

/// <summary>
/// 使用 Avalonia 11.3+ 剪贴板 API 获取文件或图片，供编辑区粘贴（插入链接/插入图片）使用。直接调用，兼容 AOT 发布。
/// </summary>
internal static class ClipboardPasteHelper
{
    public sealed record FileOrImageResult(string? FirstFilePath, Bitmap? Bitmap);

    public static async System.Threading.Tasks.Task<FileOrImageResult?> TryGetFileOrImageAsync(IClipboard? clipboard)
    {
        if (clipboard == null) return null;

        var files = await clipboard.TryGetFilesAsync();
        if (files != null && files.Length > 0 && files[0].TryGetLocalPath() is { } path && !string.IsNullOrEmpty(path))
            return new FileOrImageResult(path, null);

        var bitmap = await clipboard.TryGetBitmapAsync();
        if (bitmap != null)
            return new FileOrImageResult(null, bitmap);

        return null;
    }
}
