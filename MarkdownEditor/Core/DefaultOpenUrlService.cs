using System.Diagnostics;

namespace MarkdownEditor.Core;

/// <summary>使用系统默认浏览器/关联程序打开 URL，.NET BCL 跨平台。</summary>
public sealed class DefaultOpenUrlService : IOpenUrlService
{
    public void Open(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            if (
                !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            )
                url = "https://" + url;
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // 忽略：无默认浏览器、权限等
        }
    }
}
