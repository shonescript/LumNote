using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;

namespace MarkdownEditor.ViewModels;

/// <summary>按文件分组的搜索结果，用于侧栏“按文件列出、文件内可折叠”。</summary>
public sealed class SearchResultGroup : INotifyPropertyChanged
{
    public string FilePath { get; }
    public string FileName => string.IsNullOrEmpty(FilePath) ? "" : Path.GetFileName(FilePath);
    /// <summary>所在目录路径（用于区分同名文件），可为空。</summary>
    public string FolderPath => string.IsNullOrEmpty(FilePath) ? "" : Path.GetDirectoryName(FilePath) ?? "";
    public bool HasFolderPath => !string.IsNullOrEmpty(FolderPath);

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandArrow)));
        }
    }

    /// <summary>折叠时 ▶，展开时 ▼，用于文件树风格箭头。</summary>
    public string ExpandArrow => _isExpanded ? "\u25BC" : "\u25B6";

    public ObservableCollection<SearchResultItem> Items { get; }

    public SearchResultGroup(string filePath, IEnumerable<SearchResultItem> items)
    {
        FilePath = filePath ?? "";
        Items = new ObservableCollection<SearchResultItem>(items ?? []);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
