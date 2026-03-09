namespace MarkdownEditor.Core;

/// <summary>全局打开 URL 服务入口，由应用启动时设置，默认使用 <see cref="DefaultOpenUrlService"/>。</summary>
public static class OpenUrlService
{
    private static IOpenUrlService _instance = new DefaultOpenUrlService();

    public static IOpenUrlService Instance
    {
        get => _instance;
        set => _instance = value ?? new DefaultOpenUrlService();
    }

    public static void Open(string url) => Instance.Open(url);
}
