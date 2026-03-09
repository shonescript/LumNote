namespace MarkdownEditor.Engine.Render;

/// <summary>
/// 基于文档目录解析相对路径的图片加载器，用于导出等场景。
/// </summary>
public sealed class BasePathImageLoader : IImageLoader
{
    private readonly string _basePath;
    private readonly DefaultImageLoader _inner = new();

    public BasePathImageLoader(string basePath)
    {
        _basePath = basePath ?? "";
    }

    public event Action? ImageLoaded
    {
        add => _inner.ImageLoaded += value;
        remove => _inner.ImageLoaded -= value;
    }

    public SkiaSharp.SKBitmap? TryGetImage(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var resolved = url;
        if (!Path.IsPathRooted(url)
            && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(_basePath))
        {
            resolved = Path.GetFullPath(Path.Combine(_basePath, url.Replace('/', Path.DirectorySeparatorChar)));
        }
        return _inner.TryGetImage(resolved);
    }
}
