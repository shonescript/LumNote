namespace MarkdownEditor.Models;

/// <summary>某次提交中相对父提交的一条文件变更。</summary>
public sealed class GitCommitChangedFileItem
{
    public GitCommitChangedFileItem(
        string relativePath,
        GitTreeChangeKind changeKind,
        string? oldPathIfRenamed = null)
    {
        RelativePath = relativePath;
        ChangeKind = changeKind;
        OldPathIfRenamed = oldPathIfRenamed;
    }

    /// <summary>仓库内相对路径（使用 / 分隔）。</summary>
    public string RelativePath { get; }

    public GitTreeChangeKind ChangeKind { get; }

    /// <summary>重命名时旧路径。</summary>
    public string? OldPathIfRenamed { get; }

    public string StatusLetter => ChangeKind switch
    {
        GitTreeChangeKind.Added => "A",
        GitTreeChangeKind.Modified => "M",
        GitTreeChangeKind.Deleted => "D",
        GitTreeChangeKind.Renamed => "R",
        GitTreeChangeKind.TypeChanged => "T",
        _ => "?"
    };

    /// <summary>悬停在状态字母上时显示的简短中文说明。</summary>
    public string StatusLetterTooltip => ChangeKind switch
    {
        GitTreeChangeKind.Added => "新增（A）",
        GitTreeChangeKind.Modified => "修改（M）",
        GitTreeChangeKind.Deleted => "删除（D）",
        GitTreeChangeKind.Renamed => "重命名（R）",
        GitTreeChangeKind.TypeChanged => "类型变更（T）",
        _ => "未知（?）"
    };
}

public enum GitTreeChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    TypeChanged
}
