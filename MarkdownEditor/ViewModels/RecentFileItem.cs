using System.IO;

namespace MarkdownEditor.ViewModels;

/// <summary>最近打开的一项文件记录，用于持久化与欢迎页展示（按打开时间排序、显示文件大小）。</summary>
public sealed class RecentFileItem
{
    public string FullPath { get; }
    public System.DateTime LastOpenTimeUtc { get; }

    public string DisplayName => string.IsNullOrEmpty(FullPath) ? "" : Path.GetFileName(FullPath);

    /// <summary>文件大小（字节），若无法获取或文件不存在则为 null。</summary>
    public long? FileSizeBytes { get; }

    /// <summary>用于界面显示的文件大小，如 "1.2 KB"、"3.5 MB"。</summary>
    public string FileSizeText => FormatFileSize(FileSizeBytes);

    /// <summary>用于界面显示的相对时间，如 "刚刚"、"5 分钟前"、"昨天"、"3 天前"。</summary>
    public string LastOpenText => FormatRelativeTime(LastOpenTimeUtc);

    public RecentFileItem(string fullPath, System.DateTime lastOpenTimeUtc, long? fileSizeBytes = null)
    {
        FullPath = fullPath ?? "";
        LastOpenTimeUtc = lastOpenTimeUtc;
        FileSizeBytes = fileSizeBytes;
    }

    private static string FormatFileSize(long? bytes)
    {
        if (bytes == null || bytes < 0) return "—";
        if (bytes == 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB" };
        int u = 0;
        double v = bytes.Value;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{v:F0} {units[u]}" : $"{v:F1} {units[u]}";
    }

    private static string FormatRelativeTime(System.DateTime utc)
    {
        var now = System.DateTime.UtcNow;
        var diff = now - utc;
        if (diff.TotalSeconds < 60) return "刚刚";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} 分钟前";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} 小时前";
        if (diff.TotalDays < 2 && now.Date != utc.Date) return "昨天";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} 天前";
        if (diff.TotalDays < 30) return $"{(int)(diff.TotalDays / 7)} 周前";
        return utc.ToLocalTime().ToString("yyyy-MM-dd");
    }
}
