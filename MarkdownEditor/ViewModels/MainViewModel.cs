using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace MarkdownEditor.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    public Core.AppConfig Config { get; } = Core.AppConfig.Load(Core.AppConfig.DefaultConfigPath);

    /// <summary>最近打开的文件列表（按打开时间排序，含路径、时间、大小等，欢迎页与持久化）。</summary>
    public ObservableCollection<RecentFileItem> RecentFileItems => _recentFileItems;

    /// <summary>最近打开的文件夹路径列表（欢迎页与持久化）。</summary>
    public ObservableCollection<string> RecentFolderPaths => _recentFolderPaths;

    /// <summary>无打开文档时显示欢迎页（最近打开列表）。</summary>
    public bool ShowWelcomeView => _openDocuments.Count == 0;

    /// <summary>有打开文档时显示编辑/预览区（与 ShowWelcomeView 互斥）。</summary>
    public bool ShowEditorView => _openDocuments.Count > 0;

    public MainViewModel()
    {
        LoadRecentFilesFromDisk();
        LoadRecentFoldersFromDisk(_recentFolderPaths);
        TryRestoreLastSession();
    }

    /// <summary>退出时由视图调用，确保最近列表已持久化。</summary>
    public void SaveRecentState()
    {
        SaveRecentFilesToDisk();
    }

    private string _documentFolder = "";
    private string _searchQuery = "";
    private string _currentMarkdown = "";
    private string _currentFilePath = "";
    private string _currentFileName = "";
    private DocumentItem? _selectedDocument;
    private FileTreeNode? _selectedTreeNode;
    private ObservableCollection<DocumentItem> _documents = [];
    private ObservableCollection<DocumentItem> _filteredDocuments = [];
    private ObservableCollection<DocumentItem> _openDocuments = [];
    private ObservableCollection<FileTreeNode> _fileTreeRoot = [];
    private readonly ObservableCollection<SearchResultItem> _searchResults = [];
    private readonly ObservableCollection<SearchResultGroup> _searchResultGroups = [];
    private int _caretLine = 1;
    private int _caretColumn = 1;
    private bool _isModified;
    private EditorLayoutMode _layoutMode = EditorLayoutMode.Both;
    private bool _isExplorerActive = true;
    private bool _isSearchActive;
    private bool _isSettingsActive;
    private bool _isGitActive;

    private DocumentItem? _activeDocument;
    private readonly Stack<(string path, int offset)> _focusBackStack = new();
    private readonly Stack<(string path, int offset)> _focusForwardStack = new();
    private const int MaxRecentFiles = 20;
    private const int MaxRecentFolders = 10;
    private readonly ObservableCollection<RecentFileItem> _recentFileItems = [];
    private readonly ObservableCollection<string> _recentFolderPaths = [];
    private CancellationTokenSource? _searchCts;
    private Task? _searchTask;

    /// <summary>无文档时编辑区默认缩放。</summary>
    private double _editorZoomLevel = 1.0;
    /// <summary>无文档时预览区默认缩放（与 Config 同步）。</summary>
    private double _previewZoomLevelDefault = 1.0;
    /// <summary>当前激活窗格：Editor / Preview，用于 Ctrl+/- 和状态栏显示。</summary>
    private string _activePane = "Editor";
    /// <summary>文件在外部被修改且当前未修改时为 true，可提示用户重新加载。</summary>
    private bool _fileChangedExternally;
    /// <summary>当前选择的编码显示名（用于状态栏与保存时）。</summary>
    private string _currentEncodingName = "UTF-8";

    public string DocumentFolder
    {
        get => _documentFolder;
        set => SetProperty(ref _documentFolder, value);
    }

    /// <summary>搜索关键词。过滤文档列表立即生效；实际全文搜索由视图防抖后调用 <see cref="DoSearch"/>。</summary>
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
                FilterDocuments();
        }
    }

    public ObservableCollection<SearchResultItem> SearchResults => _searchResults;

    /// <summary>按文件分组的搜索结果，用于侧栏按文件列出、文件内可折叠。</summary>
    public ObservableCollection<SearchResultGroup> SearchResultGroups => _searchResultGroups;

    private SearchResultItem? _selectedSearchResult;
    /// <summary>用户点击某条搜索结果时设置，由视图订阅并调用 NavigateToSearchResult。</summary>
    public SearchResultItem? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set
        {
            if (_selectedSearchResult == value) return;
            _selectedSearchResult = value;
            OnPropertyChanged(nameof(SelectedSearchResult));
        }
    }

    public int CaretLine { get => _caretLine; set => SetProperty(ref _caretLine, value); }
    public int CaretColumn { get => _caretColumn; set => SetProperty(ref _caretColumn, value); }

    /// <summary>为 true 时，编辑区→预览的滚动同步应跳过，避免预览发起更新（如 todo 勾选）后编辑器写回触发同步覆盖预览滚动恢复。</summary>
    public bool SkipEditorToPreviewScrollSync { get; set; }

    public string CurrentMarkdown
    {
        get => _currentMarkdown;
        set
        {
            var text = value ?? string.Empty;
            if (SetProperty(ref _currentMarkdown, text) && !string.IsNullOrEmpty(CurrentFilePath))
            {
                IsModified = true;
                if (_activeDocument is not null)
                {
                    _activeDocument.CachedMarkdown = text;
                    _activeDocument.IsModified = true;
                }
            }
        }
    }

    /// <summary>
    /// 将 Markdown 文本中的有序列表编号规范化为连续编号（从每段列表的首行编号开始递增），
    /// 避免 1. 1. 1. 这类硬编码编号在渲染时与预期不符。
    /// </summary>
    private static string NormalizeOrderedLists(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return markdown ?? string.Empty;

        var lines = markdown.Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            // 匹配形如 "1. xxx" 的有序列表项（允许前置缩进）
            int j = 0;
            while (j < trimmed.Length && char.IsDigit(trimmed[j]))
                j++;
            if (j == 0 || j + 1 >= trimmed.Length || trimmed[j] != '.' || trimmed[j + 1] != ' ')
            {
                i++;
                continue;
            }

            // 进入一个有序列表块
            var indentLen = line.Length - trimmed.Length;
            var indent = indentLen > 0 ? line[..indentLen] : string.Empty;
            int startNum = int.TryParse(trimmed[..j], out var n) ? n : 1;
            int currentNum = startNum;
            int k = i;
            while (k < lines.Length)
            {
                var l = lines[k];
                var t = l.TrimStart();
                if (t.Length == 0)
                {
                    // 空行视为列表中断
                    break;
                }
                int jj = 0;
                while (jj < t.Length && char.IsDigit(t[jj]))
                    jj++;
                if (jj == 0 || jj + 1 >= t.Length || t[jj] != '.' || t[jj + 1] != ' ')
                {
                    break;
                }

                // 用规范化编号替换前缀，保持原有缩进和正文内容
                var content = t[(jj + 2)..];
                lines[k] = indent + currentNum.ToString() + ". " + content;
                currentNum++;
                k++;
            }

            i = k;
        }

        return string.Join("\n", lines);
    }

    private string _documentBasePath = "";

    public string CurrentFilePath
    {
        get => _currentFilePath;
        set
        {
            if (SetProperty(ref _currentFilePath, value))
            {
                _documentBasePath = string.IsNullOrEmpty(value) ? "" : Path.GetDirectoryName(value) ?? "";
                OnPropertyChanged(nameof(DocumentBasePath));
            }
        }
    }

    /// <summary>
    /// 当前文档所在目录，用于预览中解析图片相对路径
    /// </summary>
    public string DocumentBasePath => _documentBasePath;

    public string CurrentFileName
    {
        get => _currentFileName;
        set => SetProperty(ref _currentFileName, value);
    }

    public DocumentItem? SelectedDocument
    {
        get => _selectedDocument;
        set
        {
            if (_selectedDocument == value) return;
            if (_isModified && !string.IsNullOrEmpty(_currentFilePath))
            {
                TrySaveCurrent();
            }
            _selectedDocument = value;
            OnPropertyChanged();
            if (value != null)
                LoadDocument(value.FullPath);
            else
                ClearEditor();
        }
    }

    public ObservableCollection<DocumentItem> Documents => _documents;
    public ObservableCollection<DocumentItem> FilteredDocuments => _filteredDocuments;

    /// <summary>当前已打开的文档选项卡集合。</summary>
    public ObservableCollection<DocumentItem> OpenDocuments => _openDocuments;

    /// <summary>当前活动的文档（与选项卡选中项保持一致）。</summary>
    public DocumentItem? ActiveDocument
    {
        get => _activeDocument;
        set
        {
            if (_activeDocument == value) return;

            if (_isModified && !string.IsNullOrEmpty(_currentFilePath))
            {
                // 保持原有行为：切换前尝试保存当前文档
                TrySaveCurrent();
            }

            _activeDocument = value;
            OnPropertyChanged();

            if (value is null)
            {
                ClearEditor();
            }
            else
            {
                LoadFromDocumentItem(value);
                // 切换文档后恢复该文档的缩放与编码，并同步到预览配置
                Config.Markdown.ZoomLevel = value.PreviewZoomLevel;
                _currentEncodingName = value.EncodingName;
                OnPropertyChanged(nameof(EditorZoomLevel));
                OnPropertyChanged(nameof(PreviewZoomLevel));
                OnPropertyChanged(nameof(ActivePaneZoomLevel));
                OnPropertyChanged(nameof(CurrentEncodingName));
            }
        }
    }

    /// <summary>文件树根节点（VSCode 风格侧边栏用）。</summary>
    public ObservableCollection<FileTreeNode> FileTreeRoot => _fileTreeRoot;

    public FileTreeNode? SelectedTreeNode
    {
        get => _selectedTreeNode;
        set
        {
            if (_selectedTreeNode == value) return;
            _selectedTreeNode = value;
            OnPropertyChanged();

            // 统一行为：
            // - 单击文件行：打开文件
            // - 单击文件夹行：仅改变选中项，展开/折叠由内置箭头或双击控制
            if (value is { IsFolder: false } node)
            {
                if (_isModified && !string.IsNullOrEmpty(_currentFilePath))
                    TrySaveCurrent();
                LoadDocument(node.FullPath);
            }
        }
    }

    public bool IsModified
    {
        get => _isModified;
        set
        {
            if (SetProperty(ref _isModified, value) && _activeDocument is not null)
            {
                _activeDocument.IsModified = value;
            }
        }
    }

    /// <summary>预览区缩放（1.0=100%）。有当前文档时使用该文档的 PreviewZoomLevel，否则用全局。</summary>
    public double PreviewZoomLevel
    {
        get => _activeDocument != null ? _activeDocument.PreviewZoomLevel : _previewZoomLevelDefault;
        set
        {
            var v = Math.Clamp(value, 0.5, 2.5);
            if (_activeDocument != null)
            {
                if (Math.Abs(_activeDocument.PreviewZoomLevel - v) < 0.001) return;
                _activeDocument.PreviewZoomLevel = v;
            }
            else
            {
                if (Math.Abs(_previewZoomLevelDefault - v) < 0.001) return;
                _previewZoomLevelDefault = v;
            }
            Config.Markdown.ZoomLevel = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActivePaneZoomLevel));
        }
    }

    /// <summary>编辑区缩放（1.0=100%）。有当前文档时使用该文档的 EditorZoomLevel，否则用全局。</summary>
    public double EditorZoomLevel
    {
        get => _activeDocument != null ? _activeDocument.EditorZoomLevel : _editorZoomLevel;
        set
        {
            var v = Math.Clamp(value, 0.5, 2.5);
            if (_activeDocument != null)
            {
                if (Math.Abs(_activeDocument.EditorZoomLevel - v) < 0.001) return;
                _activeDocument.EditorZoomLevel = v;
            }
            else
            {
                if (Math.Abs(_editorZoomLevel - v) < 0.001) return;
                _editorZoomLevel = v;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActivePaneZoomLevel));
        }
    }

    /// <summary>当前激活窗格：Editor / Preview。</summary>
    public string ActivePane
    {
        get => _activePane;
        set
        {
            if (SetProperty(ref _activePane, value))
                OnPropertyChanged(nameof(ActivePaneZoomLevel));
        }
    }

    /// <summary>当前激活窗格的缩放（用于状态栏显示与滑动调整）。</summary>
    public double ActivePaneZoomLevel
    {
        get => _activePane == "Preview" ? PreviewZoomLevel : EditorZoomLevel;
        set
        {
            if (_activePane == "Preview")
                PreviewZoomLevel = value;
            else
                EditorZoomLevel = value;
        }
    }

    /// <summary>当前激活窗格名称（用于状态栏）。</summary>
    public string ActivePaneName => _activePane == "Preview" ? "预览" : "编辑";

    /// <summary>当前编码显示名（状态栏点击可选）。有当前文档时与文档一致。</summary>
    public string CurrentEncodingName
    {
        get => _activeDocument != null ? _activeDocument.EncodingName : _currentEncodingName;
        set
        {
            var name = value ?? "UTF-8";
            if (_activeDocument != null)
                _activeDocument.EncodingName = name;
            if (SetProperty(ref _currentEncodingName, name) && _activeDocument != null && !string.IsNullOrEmpty(CurrentFilePath) && File.Exists(CurrentFilePath))
                ReloadWithCurrentEncoding();
        }
    }

    /// <summary>状态栏点击编码时设置编码（CommandParameter 为编码名字符串），并实时按新编码重读当前文件。</summary>
    public ICommand SetEncodingCommand => new RelayCommand<object>(param =>
    {
        if (param is string name && !string.IsNullOrWhiteSpace(name))
            CurrentEncodingName = name;
    });

    /// <summary>文件在磁盘上被外部修改（未保存时可重新加载）。</summary>
    public bool FileChangedExternally
    {
        get => _fileChangedExternally;
        set => SetProperty(ref _fileChangedExternally, value);
    }

    /// <summary>从磁盘重新加载当前文件内容（不占用文件，使用当前文档编码）。</summary>
    public void ReloadFromDisk()
    {
        if (string.IsNullOrWhiteSpace(CurrentFilePath) || _activeDocument == null) return;
        ReloadWithCurrentEncoding();
    }

    /// <summary>按当前文档所选编码重新读取当前文件并更新编辑区（编码切换时调用）。</summary>
    private void ReloadWithCurrentEncoding()
    {
        if (string.IsNullOrWhiteSpace(CurrentFilePath) || _activeDocument == null || !File.Exists(CurrentFilePath)) return;
        try
        {
            var path = CurrentFilePath;
            var enc = GetEncodingByName(_activeDocument.EncodingName);
            var text = File.ReadAllText(path, enc);
            _activeDocument.CachedMarkdown = text;
            _activeDocument.IsModified = false;
            _activeDocument.LastKnownWriteTimeUtc = File.GetLastWriteTimeUtc(path);
            _currentMarkdown = text;
            _isModified = false;
            FileChangedExternally = false;
            OnPropertyChanged(nameof(CurrentMarkdown));
            OnPropertyChanged(nameof(IsModified));
        }
        catch { }
    }

    private static Encoding GetEncodingByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Encoding.UTF8;
        try
        {
            return name.ToUpperInvariant() switch
            {
                "UTF-16 BE" => Encoding.BigEndianUnicode,
                "UTF-16" => Encoding.Unicode,
                _ => Encoding.GetEncoding(name)
            };
        }
        catch { return Encoding.UTF8; }
    }

    public ICommand ZoomInCommand => new RelayCommand(() =>
    {
        if (_activePane == "Preview")
            PreviewZoomLevel = Math.Min(2.5, PreviewZoomLevel + 0.1);
        else
            EditorZoomLevel = Math.Min(2.5, EditorZoomLevel + 0.1);
    });
    public ICommand ZoomOutCommand => new RelayCommand(() =>
    {
        if (_activePane == "Preview")
            PreviewZoomLevel = Math.Max(0.5, PreviewZoomLevel - 0.1);
        else
            EditorZoomLevel = Math.Max(0.5, EditorZoomLevel - 0.1);
    });

    /// <summary>底部状态栏点击缩放标签时，从下拉选择预设缩放比例（CommandParameter 为字符串如 "1.0" 或数值）。</summary>
    public ICommand SetActivePaneZoomCommand => new RelayCommand<object>(param =>
    {
        var v = param switch
        {
            double d => d,
            string s when double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => (double?)null
        };
        if (v.HasValue)
            ActivePaneZoomLevel = Math.Clamp(v.Value, 0.5, 2.5);
    });
    /// <summary>仅缩放预览区（预览标题栏按钮用）。</summary>
    public ICommand ZoomPreviewInCommand => new RelayCommand(() => PreviewZoomLevel = Math.Min(2.5, PreviewZoomLevel + 0.1));
    public ICommand ZoomPreviewOutCommand => new RelayCommand(() => PreviewZoomLevel = Math.Max(0.5, PreviewZoomLevel - 0.1));
    public ICommand ReloadFromDiskCommand => new RelayCommand(ReloadFromDisk);
    public ICommand DismissFileChangedCommand => new RelayCommand(() => FileChangedExternally = false);

    public ICommand SaveCommand => new RelayCommand(SaveCurrent);
    public ICommand CloseDocumentCommand => new RelayCommand<DocumentItem>(CloseDocument);
    public ICommand OpenRecentDocumentCommand => new RelayCommand<string>(OpenDocument);
    /// <summary>侧栏搜索结果点击某条时由视图绑定，设置 SelectedSearchResult 以便视图跳转。</summary>
    public ICommand NavigateToSearchResultCommand => new RelayCommand<SearchResultItem>(item =>
    {
        if (item != null) SelectedSearchResult = item;
    });

    /// <summary>欢迎页点击最近文件夹时打开该文件夹。</summary>
    public ICommand OpenRecentFolderCommand => new RelayCommand<string>(path =>
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            LoadFolder(path);
    });

    /// <summary>持久化用 DTO：路径 + 最后打开时间（UTC）。</summary>
    private sealed class RecentFileEntryDto
    {
        public string Path { get; set; } = "";
        public string LastOpenTimeUtc { get; set; } = ""; // ISO 8601
    }

    private void LoadRecentFilesFromDisk()
    {
        try
        {
            var path = Core.AppConfig.RecentFilesPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            _recentFileItems.Clear();
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            // 新格式：[{ "Path": "...", "LastOpenTimeUtc": "..." }]
            var list = JsonSerializer.Deserialize<List<RecentFileEntryDto>>(json);
            if (list != null && list.Count > 0)
            {
                var items = new List<RecentFileItem>();
                foreach (var e in list)
                {
                    if (string.IsNullOrWhiteSpace(e.Path)) continue;
                    var fullPath = Path.GetFullPath(e.Path);
                    var time = DateTime.UtcNow;
                    if (!string.IsNullOrEmpty(e.LastOpenTimeUtc) && DateTime.TryParse(e.LastOpenTimeUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                        time = parsed.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : parsed.ToUniversalTime();
                    long? size = null;
                    try { if (File.Exists(fullPath)) size = new FileInfo(fullPath).Length; } catch { }
                    items.Add(new RecentFileItem(fullPath, time, size));
                }
                foreach (var item in items.OrderByDescending(x => x.LastOpenTimeUtc).Take(MaxRecentFiles))
                    _recentFileItems.Add(item);
                OnPropertyChanged(nameof(RecentFileItems));
                return;
            }
            // 旧格式：["path1", "path2"]
            var legacy = JsonSerializer.Deserialize<List<string>>(json);
            if (legacy == null) return;
            var legacyItems = new List<RecentFileItem>();
            foreach (var p in legacy)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                var fullPath = Path.GetFullPath(p);
                long? size = null;
                try { if (File.Exists(fullPath)) size = new FileInfo(fullPath).Length; } catch { }
                legacyItems.Add(new RecentFileItem(fullPath, DateTime.UtcNow, size));
            }
            foreach (var item in legacyItems.Take(MaxRecentFiles))
                _recentFileItems.Add(item);
            OnPropertyChanged(nameof(RecentFileItems));
        }
        catch { }
    }

    private void SaveRecentFilesToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(Core.AppConfig.RecentFilesPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var list = _recentFileItems
                .Take(MaxRecentFiles)
                .Select(x => new RecentFileEntryDto { Path = x.FullPath, LastOpenTimeUtc = x.LastOpenTimeUtc.ToString("O") })
                .ToList();
            var json = JsonSerializer.Serialize(list);
            File.WriteAllText(Core.AppConfig.RecentFilesPath, json);
        }
        catch { }
    }

    private static void LoadRecentFoldersFromDisk(ObservableCollection<string> target)
    {
        try
        {
            var path = Core.AppConfig.RecentFoldersPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<string>>(json);
            if (list == null) return;
            target.Clear();
            foreach (var p in list)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                var full = Path.GetFullPath(p);
                target.Add(full);
            }
        }
        catch { }
    }

    private static void SaveRecentFoldersToDisk(IList<string> paths)
    {
        try
        {
            var dir = Path.GetDirectoryName(Core.AppConfig.RecentFoldersPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var list = paths.Take(MaxRecentFolders).ToList();
            var json = JsonSerializer.Serialize(list);
            File.WriteAllText(Core.AppConfig.RecentFoldersPath, json);
        }
        catch { }
    }

    private void NotifyWelcomeViewChanged()
    {
        OnPropertyChanged(nameof(ShowWelcomeView));
        OnPropertyChanged(nameof(ShowEditorView));
    }

    private void PushRecentFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        var normalized = Path.GetFullPath(path);
        for (int i = 0; i < _recentFolderPaths.Count; i++)
        {
            if (string.Equals(Path.GetFullPath(_recentFolderPaths[i]), normalized, StringComparison.OrdinalIgnoreCase))
            {
                _recentFolderPaths.RemoveAt(i);
                _recentFolderPaths.Insert(0, normalized);
                SaveRecentFoldersToDisk(_recentFolderPaths);
                OnPropertyChanged(nameof(RecentFolderPaths));
                return;
            }
        }
        _recentFolderPaths.Insert(0, normalized);
        while (_recentFolderPaths.Count > MaxRecentFolders)
            _recentFolderPaths.RemoveAt(_recentFolderPaths.Count - 1);
        SaveRecentFoldersToDisk(_recentFolderPaths);
        OnPropertyChanged(nameof(RecentFolderPaths));
    }

    private void PushRecentFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = Path.GetFullPath(path);
        long? size = null;
        try { if (File.Exists(normalized)) size = new FileInfo(normalized).Length; } catch { }
        var now = DateTime.UtcNow;
        for (int i = 0; i < _recentFileItems.Count; i++)
        {
            if (string.Equals(Path.GetFullPath(_recentFileItems[i].FullPath), normalized, StringComparison.OrdinalIgnoreCase))
            {
                _recentFileItems.RemoveAt(i);
                _recentFileItems.Insert(0, new RecentFileItem(normalized, now, size));
                SaveRecentFilesToDisk();
                OnPropertyChanged(nameof(RecentFileItems));
                return;
            }
        }
        _recentFileItems.Insert(0, new RecentFileItem(normalized, now, size));
        while (_recentFileItems.Count > MaxRecentFiles)
            _recentFileItems.RemoveAt(_recentFileItems.Count - 1);
        SaveRecentFilesToDisk();
        OnPropertyChanged(nameof(RecentFileItems));
    }

    /// <summary>在跳转或切换文档前调用，将当前 (path, caret) 压入后退栈。</summary>
    public void PushBack(string path, int offset)
    {
        if (string.IsNullOrEmpty(path)) return;
        _focusBackStack.Push((path, offset));
    }

    /// <summary>Alt+Left：后退到上一焦点。返回 (目标 path, 目标 offset)，若无法后退则 path 为 null。</summary>
    public (string? path, int offset) GoBack(string currentPath, int currentOffset)
    {
        if (_focusBackStack.Count == 0) return (null, 0);
        _focusForwardStack.Push((currentPath, currentOffset));
        var (path, offset) = _focusBackStack.Pop();
        return (path, offset);
    }

    /// <summary>Alt+Right：前进到下一焦点。</summary>
    public (string? path, int offset) GoForward(string currentPath, int currentOffset)
    {
        if (_focusForwardStack.Count == 0) return (null, 0);
        _focusBackStack.Push((currentPath, currentOffset));
        var (path, offset) = _focusForwardStack.Pop();
        return (path, offset);
    }

    public EditorLayoutMode LayoutMode
    {
        get => _layoutMode;
        set
        {
            if (SetProperty(ref _layoutMode, value))
            {
                OnPropertyChanged(nameof(ShowEditor));
                OnPropertyChanged(nameof(ShowPreview));
            }
        }
    }

    /// <summary>启动时尝试恢复上一次会话：优先恢复最近文件，其次恢复最近文件夹。</summary>
    private void TryRestoreLastSession()
    {
        try
        {
            // 1) 优先：最近打开的文件（按时间倒序）
            var lastFile = _recentFileItems
                .OrderByDescending(x => x.LastOpenTimeUtc)
                .FirstOrDefault();
            if (lastFile != null && File.Exists(lastFile.FullPath))
            {
                var dir = Path.GetDirectoryName(lastFile.FullPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    LoadFolder(dir);
                }
                OpenDocument(lastFile.FullPath);
                return;
            }

            // 2) 其次：最近打开的文件夹
            var lastFolder = _recentFolderPaths.FirstOrDefault();
            if (!string.IsNullOrEmpty(lastFolder) && Directory.Exists(lastFolder))
            {
                LoadFolder(lastFolder);
            }
        }
        catch
        {
            // 恢复失败不影响启动，静默忽略
        }
    }

    public bool ShowEditor => _layoutMode is EditorLayoutMode.Both or EditorLayoutMode.EditorOnly;
    public bool ShowPreview => _layoutMode is EditorLayoutMode.Both or EditorLayoutMode.PreviewOnly;

    public bool IsExplorerActive
    {
        get => _isExplorerActive;
        set
        {
            if (SetProperty(ref _isExplorerActive, value))
            {
                OnPropertyChanged(nameof(ShowExplorerPane));
                if (value)
                {
                    IsSearchActive = false;
                    IsSettingsActive = false;
                    IsGitActive = false;
                }
            }
        }
    }

    public bool IsSearchActive
    {
        get => _isSearchActive;
        set
        {
            if (SetProperty(ref _isSearchActive, value))
            {
                OnPropertyChanged(nameof(ShowSearchPane));
                if (value)
                {
                    IsExplorerActive = false;
                    IsSettingsActive = false;
                    IsGitActive = false;
                }
            }
        }
    }

    public bool IsSettingsActive
    {
        get => _isSettingsActive;
        set
        {
            if (SetProperty(ref _isSettingsActive, value))
            {
                OnPropertyChanged(nameof(ShowSettingsPane));
                if (value)
                {
                    IsExplorerActive = false;
                    IsSearchActive = false;
                    IsGitActive = false;
                }
            }
        }
    }

    public bool IsGitActive
    {
        get => _isGitActive;
        set
        {
            if (SetProperty(ref _isGitActive, value))
            {
                OnPropertyChanged(nameof(ShowGitPane));
                if (value)
                {
                    IsExplorerActive = false;
                    IsSearchActive = false;
                    IsSettingsActive = false;
                }
            }
        }
    }

    public bool ShowExplorerPane => IsExplorerActive;
    public bool ShowSearchPane => IsSearchActive;
    public bool ShowSettingsPane => IsSettingsActive;
    public bool ShowGitPane => IsGitActive;

    public void LoadFolder(string path)
    {
        if (!Directory.Exists(path)) return;

        DocumentFolder = path;
        _documents.Clear();
        _filteredDocuments.Clear();
        _fileTreeRoot.Clear();
        _openDocuments.Clear();
        _activeDocument = null;
        ClearEditor();

        foreach (var file in Directory.EnumerateFiles(path, "*.md", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(path, file);
            _documents.Add(new DocumentItem(file, rel));
        }

        BuildFileTree(path);
        FilterDocuments();
        PushRecentFolder(path);
        NotifyWelcomeViewChanged();
    }

    /// <summary>刷新侧栏文件树（由根目录“刷新”按钮等调用）。</summary>
    public void RefreshFileTree()
    {
        if (!string.IsNullOrEmpty(DocumentFolder))
            BuildFileTree(DocumentFolder);
    }

    private void BuildFileTree(string rootPath)
    {
        _fileTreeRoot.Clear();
        var rootMap = new Dictionary<string, FileTreeNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in _documents)
        {
            var segments = doc.RelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Length == 0) continue;

            ObservableCollection<FileTreeNode> parentChildren = _fileTreeRoot;
            string currentPath = rootPath;

            for (int i = 0; i < segments.Length; i++)
            {
                bool isLast = i == segments.Length - 1;
                currentPath = Path.Combine(currentPath, segments[i]);

                if (isLast)
                {
                    var fileNode = new FileTreeNode(segments[i], doc.FullPath, false);
                    parentChildren.Add(fileNode);
                    continue;
                }

                if (!rootMap.TryGetValue(currentPath, out var folderNode))
                {
                    folderNode = new FileTreeNode(segments[i], currentPath, true);
                    rootMap[currentPath] = folderNode;
                    parentChildren.Add(folderNode);
                }
                parentChildren = folderNode.Children;
            }
        }

        SortTreeNodes(_fileTreeRoot);
    }

    private static void SortTreeNodes(ObservableCollection<FileTreeNode> nodes)
    {
        var list = nodes.OrderBy(n => n.IsFolder ? 0 : 1).ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        nodes.Clear();
        foreach (var n in list)
        {
            nodes.Add(n);
            SortTreeNodes(n.Children);
        }
    }

    public void OnMarkdownChanged()
    {
        IsModified = true;
    }

    public void CloseDocument(DocumentItem? doc)
    {
        if (doc is null) return;
        var list = _openDocuments;
        var idx = list.IndexOf(doc);
        if (idx < 0) return;
        if (_activeDocument == doc)
        {
            if (list.Count > 1)
            {
                var next = idx > 0 ? list[idx - 1] : list[idx + 1];
                list.RemoveAt(idx);
                OnPropertyChanged(nameof(OpenDocuments));
                NotifyWelcomeViewChanged();
                ActiveDocument = next;
            }
            else
            {
                list.RemoveAt(idx);
                doc.IsOpen = false;
                ActiveDocument = null;
                OnPropertyChanged(nameof(OpenDocuments));
                NotifyWelcomeViewChanged();
                return;
            }
        }
        else
        {
            list.RemoveAt(idx);
        }
        doc.IsOpen = false;
        OnPropertyChanged(nameof(OpenDocuments));
        NotifyWelcomeViewChanged();
    }

    public void SaveCurrent() => TrySaveCurrent();

    /// <summary>另存为：将当前内容按当前编码写入指定路径并切换为当前文档。</summary>
    public void SaveToPath(string path)
    {
        try
        {
            var enc = _activeDocument != null ? GetEncodingByName(_activeDocument.EncodingName) : Encoding.UTF8;
            var textToSave = NormalizeOrderedLists(_currentMarkdown);
            _currentMarkdown = textToSave;
            OnPropertyChanged(nameof(CurrentMarkdown));
            File.WriteAllText(path, textToSave, enc);
            if (string.IsNullOrEmpty(CurrentFilePath) && _activeDocument != null)
            {
                _openDocuments.Remove(_activeDocument);
                _activeDocument.IsOpen = false;
                OnPropertyChanged(nameof(OpenDocuments));
                NotifyWelcomeViewChanged();
            }
            LoadDocument(path);
            IsModified = false;
            if (_activeDocument != null)
            {
                _activeDocument.CachedMarkdown = textToSave;
                _activeDocument.IsModified = false;
                _activeDocument.LastKnownWriteTimeUtc = File.GetLastWriteTimeUtc(path);
            }
        }
        catch { }
    }

    private bool TrySaveCurrent()
    {
        try
        {
            if (!string.IsNullOrEmpty(CurrentFilePath))
            {
                var enc = _activeDocument != null ? GetEncodingByName(_activeDocument.EncodingName) : Encoding.UTF8;
                var textToSave = NormalizeOrderedLists(_currentMarkdown);
                _currentMarkdown = textToSave;
                OnPropertyChanged(nameof(CurrentMarkdown));
                File.WriteAllText(CurrentFilePath, textToSave, enc);
                IsModified = false;
                if (_activeDocument is not null)
                {
                    _activeDocument.CachedMarkdown = textToSave;
                    _activeDocument.IsModified = false;
                    _activeDocument.LastKnownWriteTimeUtc = File.GetLastWriteTimeUtc(CurrentFilePath);
                }
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    /// <summary>打开并激活指定路径的文档（供视图导航等调用）。</summary>
    public void OpenDocument(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        LoadDocument(path);
    }

    /// <summary>在指定目录下新建子文件夹并刷新树。返回新文件夹路径，失败返回 null。</summary>
    public string? NewFolderInFolder(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            return null;
        var dir = Path.GetFullPath(directoryPath);
        var baseName = "新文件夹";
        var path = Path.Combine(dir, baseName);
        var n = 1;
        while (Directory.Exists(path))
            path = Path.Combine(dir, $"{baseName} {n++}");
        try
        {
            Directory.CreateDirectory(path);
            BuildFileTree(DocumentFolder);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>在指定目录下新建 Markdown 文件并打开。返回新文件路径，失败返回 null。</summary>
    public string? NewFileInFolder(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            return null;
        var dir = Path.GetFullPath(directoryPath);
        var baseName = "新文档.md";
        var path = Path.Combine(dir, baseName);
        var n = 1;
        while (File.Exists(path))
            path = Path.Combine(dir, $"新文档 {n++}.md");
        try
        {
            File.WriteAllText(path, "");
            LoadDocument(path);
            BuildFileTree(DocumentFolder);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>关闭并删除指定文件，从列表和树中移除。仅支持文件。返回是否成功。</summary>
    public bool DeleteFileByPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;
        var path = Path.GetFullPath(filePath);
        var doc = _openDocuments.FirstOrDefault(d => string.Equals(d.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (doc != null)
        {
            _openDocuments.Remove(doc);
            doc.IsOpen = false;
            if (_activeDocument == doc)
                ActiveDocument = _openDocuments.FirstOrDefault();
            OnPropertyChanged(nameof(OpenDocuments));
        }
        for (int i = _documents.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_documents[i].FullPath, path, StringComparison.OrdinalIgnoreCase))
                _documents.RemoveAt(i);
        }
        try
        {
            File.Delete(path);
        }
        catch
        {
            return false;
        }
        BuildFileTree(DocumentFolder);
        return true;
    }

    /// <summary>重命名文件（仅文件）。新名为完整路径或仅文件名。返回新路径，失败返回 null。</summary>
    public string? RenameFileByPath(string oldFilePath, string newNameOrFullPath)
    {
        if (string.IsNullOrWhiteSpace(oldFilePath) || !File.Exists(oldFilePath))
            return null;
        var oldPath = Path.GetFullPath(oldFilePath);
        var dir = Path.GetDirectoryName(oldPath);
        if (string.IsNullOrEmpty(dir)) return null;

        var newPath = Path.IsPathRooted(newNameOrFullPath)
            ? Path.GetFullPath(newNameOrFullPath)
            : Path.Combine(dir, newNameOrFullPath.Trim());
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            return oldPath;
        if (File.Exists(newPath)) return null;

        try
        {
            File.Move(oldPath, newPath);
        }
        catch
        {
            return null;
        }

        var openDoc = _openDocuments.FirstOrDefault(d => string.Equals(d.FullPath, oldPath, StringComparison.OrdinalIgnoreCase));
        DocumentItem? newDocRef = null;
        if (openDoc != null)
        {
            var newRel = string.IsNullOrEmpty(DocumentFolder) ? Path.GetFileName(newPath) : Path.GetRelativePath(DocumentFolder, newPath);
            var newDoc = new DocumentItem(newPath, newRel)
            {
                CachedMarkdown = openDoc.CachedMarkdown,
                IsOpen = true,
                IsModified = openDoc.IsModified,
                LastKnownWriteTimeUtc = openDoc.LastKnownWriteTimeUtc,
                EditorZoomLevel = openDoc.EditorZoomLevel,
                PreviewZoomLevel = openDoc.PreviewZoomLevel,
                EncodingName = openDoc.EncodingName,
                LastCaretOffset = openDoc.LastCaretOffset
            };
            int idx = _openDocuments.IndexOf(openDoc);
            _openDocuments.Remove(openDoc);
            openDoc.IsOpen = false;
            _openDocuments.Insert(idx, newDoc);
            newDocRef = newDoc;
            if (_activeDocument == openDoc)
                ActiveDocument = newDoc;
            OnPropertyChanged(nameof(OpenDocuments));
        }

        for (int i = _documents.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_documents[i].FullPath, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                if (newDocRef != null)
                {
                    _documents[i] = newDocRef;
                }
                else
                {
                    var rel = string.IsNullOrEmpty(DocumentFolder) ? Path.GetFileName(newPath) : Path.GetRelativePath(DocumentFolder, newPath);
                    _documents[i] = new DocumentItem(newPath, rel);
                }
            }
        }

        BuildFileTree(DocumentFolder);
        return newPath;
    }

    private static int _untitledCounter;

    /// <summary>
    /// 新建一个空白可编辑窗口（不基于文件）。持久化通过“保存/另存为”再选择路径完成。
    /// 与“在文件管理窗口新建文件”不同：后者会先创建磁盘文件再打开（<see cref="NewFileInFolder"/>）。
    /// </summary>
    public void NewDocument()
    {
        _untitledCounter++;
        var rel = _untitledCounter == 1 ? "未命名" : $"未命名 {_untitledCounter}";
        var doc = new DocumentItem("", rel);
        doc.CachedMarkdown = "";
        doc.IsModified = false;
        doc.IsOpen = true;
        _openDocuments.Add(doc);
        OnPropertyChanged(nameof(OpenDocuments));
        NotifyWelcomeViewChanged();
        ActiveDocument = doc;
    }

    private void LoadDocument(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            PushRecentFile(path);

        // 在所有文档列表中查找对应项，没有则创建一个挂载到当前文件夹下的 DocumentItem。
        var doc = _documents.FirstOrDefault(d => string.Equals(d.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (doc is null)
        {
            var rel = string.IsNullOrEmpty(DocumentFolder)
                ? Path.GetFileName(path)
                : Path.GetRelativePath(DocumentFolder, path);
            doc = new DocumentItem(path, rel);
            _documents.Add(doc);
        }

        // 首次打开时在后台读取内容（使用文档编码）；后续切换使用缓存，避免同步 IO 阻塞 UI。
        if (doc.CachedMarkdown is null && File.Exists(path))
        {
            var docRef = doc;
            var pathCopy = Path.GetFullPath(path);
            Task.Run(() =>
            {
                string text = "";
                DateTime? writeTime = null;
                try
                {
                    var enc = GetEncodingByName(docRef.EncodingName);
                    text = File.ReadAllText(pathCopy, enc);
                    if (File.Exists(pathCopy))
                        writeTime = File.GetLastWriteTimeUtc(pathCopy);
                }
                catch
                {
                    text = "";
                }

                Dispatcher.UIThread.Post(() =>
                {
                    docRef.CachedMarkdown = text;
                    docRef.IsModified = false;
                    if (writeTime.HasValue)
                        docRef.LastKnownWriteTimeUtc = writeTime.Value;

                    if (_activeDocument == docRef)
                    {
                        _currentMarkdown = text;
                        _isModified = false;
                        OnPropertyChanged(nameof(CurrentMarkdown));
                        OnPropertyChanged(nameof(IsModified));
                    }
                });
            });
        }

        if (!_openDocuments.Contains(doc))
        {
            doc.IsOpen = true;
            _openDocuments.Add(doc);
            OnPropertyChanged(nameof(OpenDocuments));
            NotifyWelcomeViewChanged();
        }

        if (File.Exists(path))
            doc.LastKnownWriteTimeUtc = File.GetLastWriteTimeUtc(path);
        ActiveDocument = doc;
    }

    /// <summary>由视图定时调用，检测当前文件是否在外部被修改（不占用文件）。</summary>
    public void CheckFileChangedExternally()
    {
        if (string.IsNullOrWhiteSpace(CurrentFilePath) || !File.Exists(CurrentFilePath) || _activeDocument == null)
            return;
        if (IsModified)
            return;
        try
        {
            var current = File.GetLastWriteTimeUtc(CurrentFilePath);
            var known = _activeDocument.LastKnownWriteTimeUtc;
            if (known.HasValue && current != known.Value)
                FileChangedExternally = true;
        }
        catch { }
    }

    private void ClearEditor()
    {
        _currentMarkdown = "";
        _currentFilePath = "";
        _currentFileName = "";
        _isModified = false;
        _activeDocument = null;
        OnPropertyChanged(nameof(CurrentMarkdown));
        OnPropertyChanged(nameof(CurrentFilePath));
        OnPropertyChanged(nameof(CurrentFileName));
        OnPropertyChanged(nameof(IsModified));
    }

    private void LoadFromDocumentItem(DocumentItem doc)
    {
        _currentMarkdown = doc.CachedMarkdown ?? "";
        _currentFilePath = doc.FullPath;
        _currentFileName = doc.DisplayName;
        _isModified = doc.IsModified;

        OnPropertyChanged(nameof(CurrentMarkdown));
        OnPropertyChanged(nameof(CurrentFilePath));
        OnPropertyChanged(nameof(CurrentFileName));
        OnPropertyChanged(nameof(IsModified));
    }

    private void FilterDocuments()
    {
        _filteredDocuments.Clear();
        var query = SearchQuery.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(query))
        {
            foreach (var d in _documents)
                _filteredDocuments.Add(d);
        }
        else
        {
            foreach (var d in _documents)
            {
                if (d.DisplayName.ToLowerInvariant().Contains(query) ||
                    d.RelativePath.ToLowerInvariant().Contains(query))
                {
                    _filteredDocuments.Add(d);
                }
            }
        }
    }

    /// <summary>单次搜索从磁盘读取的最大文件数，避免输入时卡顿。</summary>
    private const int MaxFilesToReadForSearch = 50;

    /// <summary>参与搜索的单文件最大字节数，超过则仅在有缓存时搜索。</summary>
    private const int MaxFileSizeForSearch = 2 * 1024 * 1024;

    /// <summary>在已加载的文档列表中搜索，结果可点击定位到行。由视图防抖后调用；优先使用 CachedMarkdown，必要时才读盘且受数量与大小限制。</summary>
    public void DoSearch()
    {
        var q = SearchQuery?.Trim();
        if (string.IsNullOrEmpty(q))
        {
            _searchResults.Clear();
            _searchResultGroups.Clear();
            OnPropertyChanged(nameof(SearchResults));
            OnPropertyChanged(nameof(SearchResultGroups));
            return;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var token = cts.Token;

        var docsSnapshot = _documents.ToList();
        var queryLower = q.ToLowerInvariant();

        _searchTask = Task.Run(() =>
        {
            var results = new List<SearchResultItem>();

            int readFromDisk = 0;
            foreach (var doc in docsSnapshot)
            {
                if (token.IsCancellationRequested)
                    return;

                var text = doc.CachedMarkdown ?? "";
                if (string.IsNullOrEmpty(text))
                {
                    if (readFromDisk >= MaxFilesToReadForSearch) continue;
                    try
                    {
                        if (File.Exists(doc.FullPath))
                        {
                            var fi = new FileInfo(doc.FullPath);
                            if (fi.Length > MaxFileSizeForSearch) continue;
                            text = File.ReadAllText(doc.FullPath);
                            readFromDisk++;
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(text))
                    continue;

                var lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (token.IsCancellationRequested)
                        return;
                    var line = lines[i];
                    if (line.IndexOf(queryLower, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var preview = line.Trim();
                        if (preview.Length > 80) preview = preview[..77] + "...";
                        results.Add(new SearchResultItem(doc.FullPath, i + 1, preview));
                    }
                }
            }

            if (token.IsCancellationRequested)
                return;

            var groups = results
                .GroupBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(gr => gr.Key, StringComparer.OrdinalIgnoreCase)
                .Select(gr => new SearchResultGroup(gr.Key, gr.OrderBy(r => r.LineNumber).ToList()))
                .ToList();

            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested)
                    return;

                _searchResults.Clear();
                _searchResultGroups.Clear();
                foreach (var r in results)
                    _searchResults.Add(r);
                foreach (var g in groups)
                    _searchResultGroups.Add(g);

                OnPropertyChanged(nameof(SearchResults));
                OnPropertyChanged(nameof(SearchResultGroups));
            });
        }, token);
    }
}
