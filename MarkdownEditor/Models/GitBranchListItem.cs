namespace MarkdownEditor.Models;

/// <summary>分支下拉中的一项：名称、是否当前检出、非当前分支可显示删除。</summary>
public sealed class GitBranchListItem
{
    public GitBranchListItem(string name, bool isCurrent)
    {
        Name = name;
        IsCurrent = isCurrent;
    }

    public string Name { get; }
    public bool IsCurrent { get; }
    public bool ShowDelete => !IsCurrent;

    public override string ToString() => Name;
}
