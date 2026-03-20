using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using MarkdownEditor.Models;
using MarkdownEditor.Services;

namespace MarkdownEditor.ViewModels;

/// <summary>版本管理面板 ViewModel：仓库选择、变更列表、暂存、提交、分支、拉取/推送。</summary>
public sealed class GitPaneViewModel : ViewModelBase
{
    private readonly Func<IReadOnlyList<string>> _getWorkspaceRoots;
    private List<GitRepositoryInfo> _repositories = [];
    private GitRepositoryInfo? _selectedRepository;
    private string _currentBranchName = "";
    private List<string> _branchNames = [];
    private GitBranchListItem? _selectedBranchItem;
    private bool _suppressBranchPickerSync;
    private string _commitMessage = "";
    private bool _isRefreshing;
    private bool _isCommitting;
    private string? _statusMessage;
    private string? _pullPushError;
    private string _historyTitle = "当前分支最新提交";
    private int _gitPaneTabIndex;

    /// <summary>正在刷新选中提交的改动文件列表；为真时视图应忽略文件 ListBox 的 SelectionChanged，避免误触发比对。</summary>
    public bool SuppressCommitFileCompareSelection { get; private set; }

    public GitPaneViewModel(Func<IReadOnlyList<string>> getWorkspaceRoots)
    {
        _getWorkspaceRoots = getWorkspaceRoots;
        Changes = new ObservableCollection<GitChangeItem>();
        RefreshRepositoriesCommand = new RelayCommand(RefreshRepositories);
        InitRepositoryCommand = new RelayCommand(InitRepository);
        RefreshStatusCommand = new RelayCommand(() => RefreshStatus());
        StageAllCommand = new RelayCommand(StageAll);
        UnstageAllCommand = new RelayCommand(UnstageAll);
        StageSelectedCommand = new RelayCommand(StageSelected);
        UnstageSelectedCommand = new RelayCommand(UnstageSelected);
        CommitCommand = new RelayCommand(Commit);
        CreateBranchCommand = new RelayCommand(CreateBranch);
        PullCommand = new RelayCommand(PullAsync);
        PushCommand = new RelayCommand(PushAsync);
        HardResetToSelectedCommitCommand = new RelayCommand(RequestHardReset);
        RevertSelectedCommitCommand = new RelayCommand(RequestRevert);
    }

    /// <summary>拉取/推送时用于获取凭证；由视图设置为弹窗等。若未设置则使用空凭证（可能失败）。</summary>
    public Func<string?, string?, (string? username, string? password)?>? CredentialsProvider { get; set; }

    public ObservableCollection<GitChangeItem> Changes { get; }

    /// <summary>当前文件的时间线（提交历史）；由 <see cref="LoadTimelineForFile"/> 填充。</summary>
    public ObservableCollection<GitCommitItem> FileHistory { get; } = new();

    /// <summary>当前用于时间线的文件路径（绝对路径）；设为 null 或非仓库内文件时清空 FileHistory。</summary>
    public string? TimelineFilePath
    {
        get => _timelineFilePath;
        set
        {
            if (SetProperty(ref _timelineFilePath, value))
                LoadTimelineForFile();
        }
    }
    private string? _timelineFilePath;

    /// <summary>根据 <see cref="TimelineFilePath"/> 与当前所选仓库加载该文件的提交历史到 <see cref="FileHistory"/>。</summary>
    public void LoadTimelineForFile()
    {
        FileHistory.Clear();
        if (string.IsNullOrWhiteSpace(_timelineFilePath) || _selectedRepository == null) return;
        var repoRoot = GitService.FindRepositoryRootForPath(_timelineFilePath);
        if (string.IsNullOrEmpty(repoRoot) || !string.Equals(repoRoot, _selectedRepository.WorkingDirectory, StringComparison.OrdinalIgnoreCase)) return;
        var list = GitService.GetFileLog(repoRoot, _timelineFilePath);
        foreach (var item in list)
            FileHistory.Add(item);
    }

    /// <summary>当前分支（HEAD）最近提交列表。</summary>
    public ObservableCollection<GitCommitItem> BranchCommits { get; } = new();

    private GitCommitItem? _selectedBranchCommit;

    /// <summary>「历史」页选中的提交；变更 <see cref="SelectedCommitChangedFiles"/>。</summary>
    public GitCommitItem? SelectedBranchCommit
    {
        get => _selectedBranchCommit;
        set
        {
            if (!SetProperty(ref _selectedBranchCommit, value)) return;
            LoadSelectedCommitChangedFiles();
            if (value != null)
                HistoryTitle = $"{value.MessageShort} · {value.Date:yyyy-MM-dd HH:mm}";
            else
                HistoryTitle = "当前分支最新提交";
            NotifyBrowseModeChanged();
        }
    }

    /// <summary>选中提交相对父提交的改动文件列表。</summary>
    public ObservableCollection<GitCommitChangedFileItem> SelectedCommitChangedFiles { get; } = new();

    /// <summary>0=当前（提交/未提交），1=当前分支提交历史。</summary>
    public int GitPaneTabIndex
    {
        get => _gitPaneTabIndex;
        set
        {
            if (SetProperty(ref _gitPaneTabIndex, value))
                NotifyBrowseModeChanged();
        }
    }

    public bool IsCommitSelected => SelectedBranchCommit != null;

    /// <summary>「当前」页显示提交说明与 Commit。</summary>
    public bool ShowCommitWorkspace => SelectedRepositoryIsInitialized && GitPaneTabIndex == 0;

    /// <summary>「历史」页且已选提交时的提示条。</summary>
    public bool ShowHistoryBrowseHint => SelectedRepositoryIsInitialized && GitPaneTabIndex == 1 && IsCommitSelected;

    public string HistoryTitle
    {
        get => _historyTitle;
        private set
        {
            if (SetProperty(ref _historyTitle, value))
                OnPropertyChanged(nameof(HistoryChangedFilesMergedCaption));
        }
    }

    /// <summary>历史页：合并「此提交改动文件」与当前选中提交摘要（单行）。</summary>
    public string HistoryChangedFilesMergedCaption =>
        !IsCommitSelected ? "" : $"此提交改动文件 · {HistoryTitle}";

    /// <summary>请求将工作区硬重置到某提交（由主窗确认后执行）。</summary>
    public event EventHandler<string>? HardResetToCommitRequested;

    /// <summary>请求 revert 某提交（由主窗确认后执行）。</summary>
    public event EventHandler<string>? RevertCommitRequested;

    /// <summary>请求将某文件与当前工作区在双栏 diff 中比对。参数：绝对路径与提交 Sha。</summary>
    public event EventHandler<(string fullPath, string commitSha)>? CompareFileWithWorkingRequested;

    /// <summary>提交说明为空时用户点击 Commit，由视图弹框提示。</summary>
    public event EventHandler? CommitMessageEmptyRequested;

    /// <summary>切换分支或提交后工作区文件集可能变化，请求主窗刷新资源管理器树。</summary>
    public event EventHandler? ExplorerRefreshRequested;

    /// <summary>当前选中的变更项（单条，用于“暂存所选”/“取消暂存所选”）。</summary>
    public GitChangeItem? SelectedChangeItem
    {
        get => _selectedChangeItem;
        set => SetProperty(ref _selectedChangeItem, value);
    }
    private GitChangeItem? _selectedChangeItem;

    public IReadOnlyList<GitRepositoryInfo> Repositories => _repositories;
    public bool HasMultipleRepositories => _repositories.Count > 1;
    public bool HasRepositories => _repositories.Count > 0;

    public GitRepositoryInfo? SelectedRepository
    {
        get => _selectedRepository;
        set
        {
            if (SetProperty(ref _selectedRepository, value))
            {
                OnPropertyChanged(nameof(SelectedRepositoryIsInitialized));
                OnPropertyChanged(nameof(ShowInitRepositoryPanel));
                NotifyBrowseModeChanged();
                RefreshStatus();
                LoadTimelineForFile();
                OnPropertyChanged(nameof(CurrentBranchName));
                OnPropertyChanged(nameof(BranchNames));
            }
        }
    }

    /// <summary>当前所选目录是否已为 Git 仓库（否则显示“初始化”入口）。</summary>
    public bool SelectedRepositoryIsInitialized => _selectedRepository?.IsInitialized ?? false;

    /// <summary>是否显示“初始化 Git 仓库”面板（有工作区且当前所选目录未初始化）。</summary>
    public bool ShowInitRepositoryPanel => HasRepositories && _selectedRepository != null && !_selectedRepository.IsInitialized;

    public string CurrentBranchName
    {
        get => _currentBranchName;
        private set => SetProperty(ref _currentBranchName, value);
    }

    public IReadOnlyList<string> BranchNames => _branchNames;

    /// <summary>分支下拉数据源（含当前标记，非当前项可显示删除）。</summary>
    public ObservableCollection<GitBranchListItem> BranchListItems { get; } = new();

    /// <summary>当前选中的分支项；用户从下拉选择时执行 <see cref="CheckoutBranch"/>。</summary>
    public GitBranchListItem? SelectedBranchItem
    {
        get => _selectedBranchItem;
        set
        {
            if (!SetProperty(ref _selectedBranchItem, value)) return;
            if (_suppressBranchPickerSync || value == null) return;
            CheckoutBranch(value.Name);
        }
    }

    /// <summary>“更改数(n)” 文案。</summary>
    public string ChangesCountText => $"更改数({Changes.Count})";

    /// <summary>工作区未提交更改标题（与「共 n 项」合并）。</summary>
    public string UncommittedChangesHeader =>
        Changes.Count > 0 ? $"当前未提交更改（共 {Changes.Count} 项）" : "当前未提交更改（无）";

    public string CommitMessage
    {
        get => _commitMessage;
        set => SetProperty(ref _commitMessage, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetProperty(ref _isRefreshing, value);
    }

    /// <summary>正在执行提交（后台线程写 Git），用于禁用 Commit 与提示等待。</summary>
    public bool IsCommitting
    {
        get => _isCommitting;
        private set => SetProperty(ref _isCommitting, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string? PullPushError
    {
        get => _pullPushError;
        private set => SetProperty(ref _pullPushError, value);
    }

    public ICommand RefreshRepositoriesCommand { get; }
    public ICommand InitRepositoryCommand { get; }
    public ICommand RefreshStatusCommand { get; }
    public ICommand StageAllCommand { get; }
    public ICommand UnstageAllCommand { get; }
    public ICommand StageSelectedCommand { get; }
    public ICommand UnstageSelectedCommand { get; }
    public ICommand CommitCommand { get; }
    public ICommand CreateBranchCommand { get; }
    public ICommand PullCommand { get; }
    public ICommand PushCommand { get; }

    public ICommand HardResetToSelectedCommitCommand { get; }
    public ICommand RevertSelectedCommitCommand { get; }

    /// <summary>在侧栏中选中改动文件后，与当前工作区双栏比对。</summary>
    public void RequestCompareFileWithWorking(GitCommitChangedFileItem item)
    {
        if (_selectedRepository == null || _selectedBranchCommit == null) return;
        var rel = item.RelativePath.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_selectedRepository.WorkingDirectory, rel));
        CompareFileWithWorkingRequested?.Invoke(this, (full, _selectedBranchCommit.Sha));
    }

    private void RequestHardReset()
    {
        if (_selectedBranchCommit == null) return;
        HardResetToCommitRequested?.Invoke(this, _selectedBranchCommit.Sha);
    }

    private void RequestRevert()
    {
        if (_selectedBranchCommit == null) return;
        RevertCommitRequested?.Invoke(this, _selectedBranchCommit.Sha);
    }

    private void LoadBranchCommits()
    {
        if (_selectedRepository == null || !_selectedRepository.IsInitialized)
        {
            BranchCommits.Clear();
            SelectedBranchCommit = null;
            HistoryTitle = "当前分支最新提交";
            NotifyBrowseModeChanged();
            return;
        }

        // 刷新状态后会再次调用本方法；勿清空用户正在查看的提交，否则文件列表会一闪消失。
        var keepSha = _selectedBranchCommit?.Sha;
        var root = _selectedRepository.WorkingDirectory;
        Task.Run(() =>
        {
            var list = GitService.GetRecentCommitsOnHead(root, 80);
            Dispatcher.UIThread.Post(() =>
            {
                BranchCommits.Clear();
                foreach (var c in list)
                    BranchCommits.Add(c);

                if (!string.IsNullOrEmpty(keepSha))
                {
                    var restored = BranchCommits.FirstOrDefault(c => c.Sha == keepSha);
                    if (restored != null)
                        SelectedBranchCommit = restored;
                    else
                    {
                        SelectedBranchCommit = null;
                        HistoryTitle = "当前分支最新提交";
                    }
                }
                else
                    HistoryTitle = "当前分支最新提交";

                NotifyBrowseModeChanged();
            });
        });
    }

    private void LoadSelectedCommitChangedFiles()
    {
        void EndSuppress() =>
            Dispatcher.UIThread.Post(() => { SuppressCommitFileCompareSelection = false; }, DispatcherPriority.Loaded);

        SuppressCommitFileCompareSelection = true;
        SelectedCommitChangedFiles.Clear();
        if (_selectedRepository == null || _selectedBranchCommit == null)
        {
            EndSuppress();
            return;
        }

        var root = _selectedRepository.WorkingDirectory;
        var sha = _selectedBranchCommit.Sha;
        Task.Run(() =>
        {
            try
            {
                var files = GitService.GetChangedFilesInCommit(root, sha);
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        SelectedCommitChangedFiles.Clear();
                        foreach (var f in files)
                            SelectedCommitChangedFiles.Add(f);
                    }
                    finally
                    {
                        EndSuppress();
                    }
                });
            }
            catch
            {
                Dispatcher.UIThread.Post(EndSuppress);
            }
        });
    }

    private void RebuildBranchListItemsFromNames(string? currentBranchName)
    {
        _suppressBranchPickerSync = true;
        try
        {
            BranchListItems.Clear();
            foreach (var n in _branchNames)
                BranchListItems.Add(new GitBranchListItem(n, string.Equals(n, currentBranchName, StringComparison.OrdinalIgnoreCase)));
            _selectedBranchItem = BranchListItems.FirstOrDefault(x => x.IsCurrent);
            OnPropertyChanged(nameof(SelectedBranchItem));
        }
        finally
        {
            _suppressBranchPickerSync = false;
        }
    }

    public void RefreshRepositories()
    {
        var roots = _getWorkspaceRoots();
        var prevPath = _selectedRepository?.WorkingDirectory;
        _repositories = GitService.DiscoverRepositories(roots).ToList();
        OnPropertyChanged(nameof(Repositories));
        OnPropertyChanged(nameof(HasMultipleRepositories));
        OnPropertyChanged(nameof(HasRepositories));
        OnPropertyChanged(nameof(ShowInitRepositoryPanel));
        if (_repositories.Count == 0)
            SelectedRepository = null;
        else if (!string.IsNullOrEmpty(prevPath))
        {
            var match = _repositories.FirstOrDefault(r => string.Equals(r.WorkingDirectory, prevPath, StringComparison.OrdinalIgnoreCase));
            SelectedRepository = match ?? _repositories[0];
        }
        else
            SelectedRepository = _repositories[0];
        RefreshStatus();
    }

    /// <param name="statusMessageAfterRefresh">刷新完成并清空列表后写入的状态文案；默认 null 表示清除提示。</param>
    public void RefreshStatus(string? statusMessageAfterRefresh = null)
    {
        if (_selectedRepository == null)
        {
            Changes.Clear();
            CurrentBranchName = "";
            _branchNames = [];
            OnPropertyChanged(nameof(BranchNames));
            BranchListItems.Clear();
            _suppressBranchPickerSync = true;
            _selectedBranchItem = null;
            OnPropertyChanged(nameof(SelectedBranchItem));
            _suppressBranchPickerSync = false;
            OnPropertyChanged(nameof(UncommittedChangesHeader));
            StatusMessage = "未选择仓库";
            NotifyBrowseModeChanged();
            return;
        }
        if (!_selectedRepository.IsInitialized)
        {
            Changes.Clear();
            CurrentBranchName = "";
            _branchNames = [];
            OnPropertyChanged(nameof(BranchNames));
            BranchListItems.Clear();
            _suppressBranchPickerSync = true;
            _selectedBranchItem = null;
            OnPropertyChanged(nameof(SelectedBranchItem));
            _suppressBranchPickerSync = false;
            OnPropertyChanged(nameof(UncommittedChangesHeader));
            NotifyBrowseModeChanged();
            return;
        }
        IsRefreshing = true;
        StatusMessage = "刷新中…";
        Task.Run(() =>
        {
            var status = GitService.GetStatus(_selectedRepository.WorkingDirectory);
            var branch = GitService.GetCurrentBranchName(_selectedRepository.WorkingDirectory);
            var branches = GitService.GetLocalBranchNames(_selectedRepository.WorkingDirectory);
            Dispatcher.UIThread.Post(() =>
            {
                Changes.Clear();
                foreach (var item in status)
                    Changes.Add(item);
                CurrentBranchName = branch ?? "";
                _branchNames = branches.ToList();
                OnPropertyChanged(nameof(BranchNames));
                RebuildBranchListItemsFromNames(branch);
                OnPropertyChanged(nameof(ChangesCountText));
                OnPropertyChanged(nameof(UncommittedChangesHeader));
                IsRefreshing = false;
                StatusMessage = statusMessageAfterRefresh;
                LoadBranchCommits();
            });
        });
    }

    private void NotifyBrowseModeChanged()
    {
        OnPropertyChanged(nameof(IsCommitSelected));
        OnPropertyChanged(nameof(ShowCommitWorkspace));
        OnPropertyChanged(nameof(ShowHistoryBrowseHint));
        OnPropertyChanged(nameof(HistoryChangedFilesMergedCaption));
    }

    private void InitRepository()
    {
        if (_selectedRepository == null || _selectedRepository.IsInitialized) return;
        var (ok, err) = GitService.InitRepository(_selectedRepository.WorkingDirectory);
        if (ok)
        {
            RefreshRepositories();
            StatusMessage = "已创建 Git 仓库：已在 main 分支完成首次提交（空提交），可用于版本管理。";
            ExplorerRefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        else
            StatusMessage = err ?? "初始化失败";
    }

    private void StageAll()
    {
        if (_selectedRepository == null) return;
        var paths = Changes.Select(c => c.FullPath).ToList();
        if (GitService.Stage(_selectedRepository.WorkingDirectory, paths))
            RefreshStatus();
        else
            StatusMessage = "暂存失败";
    }

    private void UnstageAll()
    {
        if (_selectedRepository == null) return;
        var paths = Changes.Select(c => c.FullPath).ToList();
        if (GitService.Unstage(_selectedRepository.WorkingDirectory, paths))
            RefreshStatus();
        else
            StatusMessage = "取消暂存失败";
    }

    private void StageSelected()
    {
        if (_selectedRepository == null || _selectedChangeItem == null) return;
        if (GitService.Stage(_selectedRepository.WorkingDirectory, new[] { _selectedChangeItem.FullPath }))
            RefreshStatus();
    }

    private void UnstageSelected()
    {
        if (_selectedRepository == null || _selectedChangeItem == null) return;
        if (GitService.Unstage(_selectedRepository.WorkingDirectory, new[] { _selectedChangeItem.FullPath }))
            RefreshStatus();
    }

    /// <summary>智能提交（Smart Commit）：在后台线程暂存并提交，避免大量文件时阻塞 UI。</summary>
    private async void Commit()
    {
        if (_selectedRepository == null || IsCommitting) return;
        if (string.IsNullOrWhiteSpace(CommitMessage))
        {
            CommitMessageEmptyRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var repo = _selectedRepository.WorkingDirectory;
        var message = CommitMessage.Trim();
        var paths = Changes.Select(c => c.FullPath).ToList();

        IsCommitting = true;
        StatusMessage = paths.Count > 0 ? $"正在提交（暂存并写入 {paths.Count} 项）…" : "正在提交…";
        try
        {
            var (success, error) = await Task.Run(() =>
            {
                if (paths.Count > 0 && !GitService.Stage(repo, paths))
                    return (false, "暂存失败");
                return GitService.Commit(repo, message);
            }).ConfigureAwait(true);

            if (success)
            {
                CommitMessage = "";
                OnPropertyChanged(nameof(CommitMessage));
                RefreshStatus("已保存版本");
                ExplorerRefreshRequested?.Invoke(this, EventArgs.Empty);
            }
            else
                StatusMessage = error ?? "提交失败";
        }
        finally
        {
            IsCommitting = false;
        }
    }

    private void CreateBranch()
    {
        if (_selectedRepository == null) return;
        CreateBranchRequested?.Invoke(this, _selectedRepository.WorkingDirectory);
    }

    /// <summary>请求创建新分支时由视图弹出输入框并调用 GitService.CreateBranchAndCheckout，然后 RefreshStatus。</summary>
    public event EventHandler<string>? CreateBranchRequested;

    /// <summary>请求切换分支时由视图提供分支名并调用 GitService.CheckoutBranch，然后 RefreshStatus。</summary>
    public void CheckoutBranch(string branchName)
    {
        if (_selectedRepository == null) return;
        var (success, error) = GitService.CheckoutBranch(_selectedRepository.WorkingDirectory, branchName);
        if (success)
        {
            RefreshStatus();
            ExplorerRefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            StatusMessage = error ?? "切换失败";
            RebuildBranchListItemsFromNames(CurrentBranchName);
        }
    }

    private async void PullAsync()
    {
        if (_selectedRepository == null) return;
        PullPushError = null;
        var (success, error) = await GitService.PullAsync(_selectedRepository.WorkingDirectory, (url, user, _) => CredentialsProvider?.Invoke(url, user)).ConfigureAwait(true);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (success)
            {
                RefreshStatus();
                PullPushError = null;
                ExplorerRefreshRequested?.Invoke(this, EventArgs.Empty);
            }
            else
                PullPushError = error;
        });
    }

    private async void PushAsync()
    {
        if (_selectedRepository == null) return;
        PullPushError = null;
        var (success, error) = await GitService.PushAsync(_selectedRepository.WorkingDirectory, (url, user, _) => CredentialsProvider?.Invoke(url, user)).ConfigureAwait(true);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (success)
            {
                RefreshStatus();
                PullPushError = null;
                ExplorerRefreshRequested?.Invoke(this, EventArgs.Empty);
            }
            else
                PullPushError = error;
        });
    }
}
