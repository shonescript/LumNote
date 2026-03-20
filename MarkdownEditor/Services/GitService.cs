using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using MarkdownEditor.Models;

namespace MarkdownEditor.Services;

/// <summary>Git 仓库信息（工作区根或发现到的仓库根）。未初始化时 IsInitialized 为 false，可在此目录执行“初始化 Git 仓库”。</summary>
public sealed class GitRepositoryInfo
{
    public string WorkingDirectory { get; }
    public string DisplayName { get; }
    /// <summary>该目录下是否已有 .git（已初始化仓库）。</summary>
    public bool IsInitialized { get; }

    public GitRepositoryInfo(string workingDirectory, string displayName, bool isInitialized = true)
    {
        WorkingDirectory = workingDirectory;
        DisplayName = displayName;
        IsInitialized = isInitialized;
    }
}

/// <summary>从工作区根目录列表发现 Git 仓库；对每个仓库提供 Status、Stage、Commit、分支、Log、Diff、Pull/Push。多根时按仓库根分别维护。</summary>
public sealed class GitService
{
    /// <summary>从多个工作区根列出所有目录：根目录下已有 .git 的为已初始化仓库，没有的也列入以便“在此初始化”。每个根只出现一次。</summary>
    public static IReadOnlyList<GitRepositoryInfo> DiscoverRepositories(IEnumerable<string> workspaceRoots)
    {
        var list = new List<GitRepositoryInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in workspaceRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!seen.Add(normalized)) continue;
            var displayName = Path.GetFileName(normalized) ?? normalized;
            var gitDir = Path.Combine(normalized, ".git");
            var isInitialized = Directory.Exists(gitDir) || File.Exists(gitDir);
            if (isInitialized)
            {
                try
                {
                    using var repo = new Repository(normalized);
                    if (!string.IsNullOrEmpty(repo.Info.WorkingDirectory))
                    {
                        list.Add(new GitRepositoryInfo(normalized, displayName, isInitialized: true));
                        continue;
                    }
                }
                catch
                {
                    isInitialized = false;
                }
            }
            list.Add(new GitRepositoryInfo(normalized, displayName, isInitialized: false));
        }
        return list;
    }

    /// <summary>在指定目录初始化 Git（创建 .git），并在 <c>main</c> 上创建首次空提交（若尚无提交）。已存在仓库则直接返回成功。</summary>
    public static (bool success, string? errorMessage) InitRepository(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            return (false, "路径无效。");
        try
        {
            if (!string.IsNullOrEmpty(Repository.Discover(directoryPath)))
                return (true, null);
            Repository.Init(directoryPath);
            using var repo = new Repository(directoryPath);
            return EnsureInitialCommitOnMainBranch(repo);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (bool success, string? errorMessage) EnsureInitialCommitOnMainBranch(Repository repo)
    {
        try
        {
            if (repo.Commits.Any())
                return (true, null);
            var sig = new Signature("User", "user@local", DateTimeOffset.Now);
            var tree = repo.ObjectDatabase.CreateTree(new TreeDefinition());
            var commit = repo.ObjectDatabase.CreateCommit(sig, sig, "Initial commit", tree, Enumerable.Empty<Commit>(), false);
            const string mainRef = "refs/heads/main";
            if (repo.Refs[mainRef] != null)
                repo.Refs.Remove(mainRef);
            repo.Refs.Add(mainRef, commit.Id);
            repo.Refs.UpdateTarget("HEAD", mainRef);
            try
            {
                if (repo.Refs["refs/heads/master"] != null)
                    repo.Refs.Remove("refs/heads/master");
            }
            catch
            {
                // ignore
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>获取指定路径所在的 Git 仓库根（从该路径向上查找 .git）；不存在则返回 null。</summary>
    public static string? FindRepositoryRootForPath(string fileOrFolderPath)
    {
        if (string.IsNullOrWhiteSpace(fileOrFolderPath)) return null;
        var dir = File.Exists(fileOrFolderPath) ? Path.GetDirectoryName(fileOrFolderPath) : fileOrFolderPath;
        if (string.IsNullOrEmpty(dir)) return null;
        try
        {
            var gitPath = Repository.Discover(dir);
            if (string.IsNullOrEmpty(gitPath)) return null;
            using var repo = new Repository(gitPath);
            var workDir = repo.Info.WorkingDirectory;
            return string.IsNullOrEmpty(workDir) ? null : Path.GetFullPath(workDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>获取仓库状态：变更列表（已修改、已暂存、未跟踪等）。</summary>
    public static IReadOnlyList<GitChangeItem> GetStatus(string repositoryRoot)
    {
        var result = new List<GitChangeItem>();
        try
        {
            using var repo = new Repository(repositoryRoot);
            var status = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true });
            var workDir = repo.Info.WorkingDirectory ?? repositoryRoot;
            foreach (var entry in status)
            {
                var rel = entry.FilePath.Replace('/', Path.DirectorySeparatorChar);
                var full = Path.Combine(workDir, rel);
                var st = entry.State;
                var isStaged = st.HasFlag(FileStatus.NewInIndex) || st.HasFlag(FileStatus.ModifiedInIndex) || st.HasFlag(FileStatus.DeletedFromIndex) || st.HasFlag(FileStatus.RenamedInIndex) || st.HasFlag(FileStatus.TypeChangeInIndex);
                var isModified = st.HasFlag(FileStatus.ModifiedInWorkdir) || st.HasFlag(FileStatus.NewInWorkdir) || st.HasFlag(FileStatus.DeletedFromWorkdir) || st.HasFlag(FileStatus.RenamedInWorkdir) || st.HasFlag(FileStatus.TypeChangeInWorkdir);
                var isNew = st.HasFlag(FileStatus.NewInWorkdir) || st.HasFlag(FileStatus.NewInIndex);
                var isDeleted = st.HasFlag(FileStatus.DeletedFromWorkdir) || st.HasFlag(FileStatus.DeletedFromIndex);
                var isUntracked = isNew && !isStaged;
                result.Add(new GitChangeItem(rel, full, isStaged, isModified, isNew, isDeleted, isUntracked));
            }
        }
        catch
        {
            // 返回空列表
        }
        return result;
    }

    /// <summary>暂存指定文件。</summary>
    public static bool Stage(string repositoryRoot, IEnumerable<string> paths)
    {
        try
        {
            using var repo = new Repository(repositoryRoot);
            foreach (var p in paths)
            {
                var rel = GetRelativePath(repo.Info.WorkingDirectory!, p);
                if (rel != null) Commands.Stage(repo, rel);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>取消暂存指定文件。</summary>
    public static bool Unstage(string repositoryRoot, IEnumerable<string> paths)
    {
        try
        {
            using var repo = new Repository(repositoryRoot);
            foreach (var p in paths)
            {
                var rel = GetRelativePath(repo.Info.WorkingDirectory!, p);
                if (rel != null) Commands.Unstage(repo, rel);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>提交已暂存更改。authorName/authorEmail 用于生成 Signature；若为空则使用默认值。</summary>
    public static (bool success, string? errorMessage) Commit(string repositoryRoot, string message, string? authorName = null, string? authorEmail = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return (false, "请填写提交说明。");
        try
        {
            using var repo = new Repository(repositoryRoot);
            var status = repo.RetrieveStatus();
            var anyStaged = status.Any(e => e.State.HasFlag(FileStatus.NewInIndex) || e.State.HasFlag(FileStatus.ModifiedInIndex) || e.State.HasFlag(FileStatus.DeletedFromIndex) || e.State.HasFlag(FileStatus.RenamedInIndex) || e.State.HasFlag(FileStatus.TypeChangeInIndex));
            if (!anyStaged) return (false, "没有已暂存的更改，请先勾选要纳入本次版本的文件。");
            var name = authorName ?? "User";
            var email = authorEmail ?? "user@local";
            var sig = new Signature(name, email, DateTimeOffset.Now);
            repo.Commit(message.Trim(), sig, sig);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>当前分支名；无 HEAD 时返回 null。</summary>
    public static string? GetCurrentBranchName(string repositoryRoot)
    {
        try
        {
            using var repo = new Repository(repositoryRoot);
            return repo.Head?.FriendlyName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>本地分支名列表（不含远程前缀）。</summary>
    public static IReadOnlyList<string> GetLocalBranchNames(string repositoryRoot)
    {
        try
        {
            using var repo = new Repository(repositoryRoot);
            return repo.Branches.Where(b => !b.IsRemote).Select(b => b.FriendlyName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>创建新分支并切换到该分支。branchName 建议小写、无空格。</summary>
    public static (bool success, string? errorMessage) CreateBranchAndCheckout(string repositoryRoot, string branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName)) return (false, "请输入分支名。");
        var sanitized = branchName.Trim().Replace(' ', '-');
        if (string.IsNullOrEmpty(sanitized)) return (false, "分支名无效。");
        try
        {
            using var repo = new Repository(repositoryRoot);
            var branch = repo.CreateBranch(sanitized);
            Commands.Checkout(repo, branch);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>删除指定本地分支（不得为当前检出分支）。若分支含未合并提交，Git 可能拒绝删除。</summary>
    public static (bool success, string? errorMessage) DeleteLocalBranch(string repositoryRoot, string branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName)) return (false, "分支名无效。");
        try
        {
            using var repo = new Repository(repositoryRoot);
            var headName = repo.Head?.FriendlyName;
            if (string.Equals(headName, branchName, StringComparison.OrdinalIgnoreCase))
                return (false, "不能删除当前检出的分支，请先切换到其他分支。");
            var b = repo.Branches[branchName];
            if (b == null) return (false, "分支不存在。");
            repo.Branches.Remove(b);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>切换到指定本地分支。</summary>
    public static (bool success, string? errorMessage) CheckoutBranch(string repositoryRoot, string branchName)
    {
        try
        {
            using var repo = new Repository(repositoryRoot);
            var branch = repo.Branches[branchName];
            if (branch == null) return (false, "分支不存在。");
            Commands.Checkout(repo, branch);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>某文件在仓库中的提交历史（Timeline）。filePath 为绝对路径或相对仓库根的路径。</summary>
    public static IReadOnlyList<GitCommitItem> GetFileLog(string repositoryRoot, string filePath, int maxCount = 100)
    {
        var result = new List<GitCommitItem>();
        try
        {
            using var repo = new Repository(repositoryRoot);
            var workDir = repo.Info.WorkingDirectory ?? repositoryRoot;
            var rel = Path.IsPathRooted(filePath) ? GetRelativePath(workDir, filePath) : filePath;
            if (rel == null) return result;
            rel = rel.Replace(Path.DirectorySeparatorChar, '/');
            var count = 0;
            foreach (var logEntry in repo.Commits.QueryBy(rel))
            {
                if (count >= maxCount) break;
                var c = logEntry.Commit;
                if (c == null) continue;
                var msg = c.MessageShort ?? "";
                if (msg.Length > 60) msg = msg.Substring(0, 57) + "...";
                result.Add(new GitCommitItem(
                    c.Sha.Substring(0, Math.Min(7, c.Sha.Length)),
                    c.Sha,
                    msg,
                    c.Author?.Name ?? "",
                    c.Author?.When ?? DateTimeOffset.MinValue));
                count++;
            }
        }
        catch
        {
            // 返回空
        }
        return result;
    }

    /// <summary>获取某次提交中某文件的内容（文本）。filePath 为绝对路径；返回 null 表示该版本中无此文件或无法读取。</summary>
    public static string? GetFileContentAtCommit(string repositoryRoot, string filePath, string commitSha)
    {
        try
        {
            using var repo = new Repository(repositoryRoot);
            var workDir = repo.Info.WorkingDirectory ?? repositoryRoot;
            var rel = GetRelativePath(workDir, filePath);
            if (rel == null) return null;
            rel = rel.Replace(Path.DirectorySeparatorChar, '/');
            var commit = repo.Lookup<Commit>(commitSha);
            if (commit == null) return null;
            var entry = commit[rel];
            if (entry?.Target is Blob blob)
            {
                using var stream = blob.GetContentStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>计算当前工作区文件内容与 HEAD（或指定 commit）的行级 diff，映射到“当前文档行号 → 差异类型”。currentContent 为当前编辑器或工作区文件内容；若 compareToCommit 为空则与 HEAD 比较。</summary>
    public static GitDiffLineMap GetDiffLineMap(string repositoryRoot, string filePath, string currentContent, string? compareToCommit = null)
    {
        var map = new GitDiffLineMap();
        try
        {
            using var repo = new Repository(repositoryRoot);
            var workDir = repo.Info.WorkingDirectory ?? repositoryRoot;
            var rel = GetRelativePath(workDir, filePath);
            if (rel == null) return map;
            rel = rel.Replace(Path.DirectorySeparatorChar, '/');
            string? oldContent = null;
            if (!string.IsNullOrEmpty(compareToCommit))
            {
                var commit = repo.Lookup<Commit>(compareToCommit);
                var entry = commit?[rel];
                if (entry?.Target is Blob blob)
                {
                    using var stream = blob.GetContentStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    oldContent = reader.ReadToEnd();
                }
            }
            else
            {
                var head = repo.Head?.Tip;
                if (head != null)
                {
                    var entry = head[rel];
                    if (entry?.Target is Blob blob)
                    {
                        using var stream = blob.GetContentStream();
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        oldContent = reader.ReadToEnd();
                    }
                }
            }
            if (oldContent == null)
            {
                // 文件在旧版本中不存在，当前全部视为新增
                var lines = currentContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                for (var i = 0; i < lines.Length; i++)
                    map.Set(i + 1, GitDiffLineKind.Added);
                return map;
            }
            var oldLines = oldContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var newLines = currentContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            BuildLineMapFromLineDiff(oldLines, newLines, map);
        }
        catch
        {
            // 返回空 map
        }
        return map;
    }

    private static void BuildLineMapFromLineDiff(string[] oldLines, string[] newLines, GitDiffLineMap map)
    {
        var n = newLines.Length;
        var m = oldLines.Length;
        var lcs = new int[n + 1, m + 1];
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
                lcs[i, j] = newLines[i - 1] == oldLines[j - 1]
                    ? lcs[i - 1, j - 1] + 1
                    : Math.Max(lcs[i - 1, j], lcs[i, j - 1]);
        var newIdx = n;
        var oldIdx = m;
        var stack = new List<(int ni, int oi, char kind)>();
        while (newIdx > 0 || oldIdx > 0)
        {
            if (newIdx > 0 && oldIdx > 0 && newLines[newIdx - 1] == oldLines[oldIdx - 1])
            {
                stack.Add((newIdx, oldIdx, 'u'));
                newIdx--;
                oldIdx--;
            }
            else if (oldIdx > 0 && (newIdx == 0 || lcs[newIdx, oldIdx - 1] >= lcs[newIdx - 1, oldIdx]))
            {
                stack.Add((newIdx, oldIdx, 'r'));
                oldIdx--;
            }
            else
            {
                stack.Add((newIdx, oldIdx, 'a'));
                newIdx--;
            }
        }
        // 从文件头向文件尾遍历：i 从大往小，所以“前一项”（文件顺序）在 stack[i+1]
        for (var i = stack.Count - 1; i >= 0; i--)
        {
            var (ni, oi, k) = stack[i];
            if (k == 'r')
            {
                var content = oi >= 1 && oi <= oldLines.Length ? oldLines[oi - 1] : "";
                var showBefore = ni <= 0 ? 1 : ni;
                map.AddDeletedLine(showBefore, content);
            }
            if (ni <= 0) continue;
            if (k == 'a')
            {
                var prevIsRemoved = i + 1 < stack.Count && stack[i + 1].kind == 'r';
                var nextIsRemoved = i > 0 && stack[i - 1].kind == 'r';
                map.Set(ni, (prevIsRemoved || nextIsRemoved) ? GitDiffLineKind.Modified : GitDiffLineKind.Added);
            }
        }
    }

    /// <summary>拉取当前分支。credentialsProvider 在需要时被调用（url, usernameFromUrl, types），返回 (username, password) 或 null 表示取消。</summary>
    public static async Task<(bool success, string? errorMessage)> PullAsync(
        string repositoryRoot,
        Func<string?, string?, SupportedCredentialTypes, (string? username, string? password)?> credentialsProvider,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Run(() =>
            {
                using var repo = new Repository(repositoryRoot);
                var sig = new Signature("User", "user@local", DateTimeOffset.Now);
                var options = new PullOptions
                {
                    FetchOptions = new FetchOptions
                    {
                        CredentialsProvider = (url, usernameFromUrl, types) =>
                        {
                            var result = credentialsProvider(url, usernameFromUrl, types);
                            if (result == null) return null;
                            return new UsernamePasswordCredentials { Username = result.Value.username ?? "", Password = result.Value.password ?? "" };
                        }
                    }
                };
                Commands.Pull(repo, sig, options);
            }, cancellationToken).ConfigureAwait(false);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>推送到当前分支的 upstream。credentialsProvider 同上。</summary>
    public static async Task<(bool success, string? errorMessage)> PushAsync(
        string repositoryRoot,
        Func<string?, string?, SupportedCredentialTypes, (string? username, string? password)?> credentialsProvider,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Run(() =>
            {
                using var repo = new Repository(repositoryRoot);
                var head = repo.Head;
                if (head == null || !head.IsTracking) throw new InvalidOperationException("当前分支未设置远程。");
                var options = new PushOptions
                {
                    CredentialsProvider = (url, usernameFromUrl, types) =>
                    {
                        var result = credentialsProvider(url, usernameFromUrl, types);
                        if (result == null) return null;
                        return new UsernamePasswordCredentials { Username = result.Value.username ?? "", Password = result.Value.password ?? "" };
                    }
                };
                repo.Network.Push(head, options);
            }, cancellationToken).ConfigureAwait(false);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>当前 HEAD 可达的最近若干条提交（用于分支历史列表）。</summary>
    public static IReadOnlyList<GitCommitItem> GetRecentCommitsOnHead(string repositoryRoot, int maxCount = 100)
    {
        var result = new List<GitCommitItem>();
        try
        {
            using var repo = new Repository(repositoryRoot);
            var tip = repo.Head?.Tip;
            if (tip == null) return result;
            var filter = new CommitFilter { IncludeReachableFrom = tip };
            var n = 0;
            foreach (var c in repo.Commits.QueryBy(filter))
            {
                if (n >= maxCount) break;
                var msg = c.MessageShort ?? "";
                if (msg.Length > 60) msg = msg.Substring(0, 57) + "...";
                result.Add(new GitCommitItem(
                    c.Sha.Substring(0, Math.Min(7, c.Sha.Length)),
                    c.Sha,
                    msg,
                    c.Author?.Name ?? "",
                    c.Author?.When ?? DateTimeOffset.MinValue));
                n++;
            }
        }
        catch
        {
            // ignore
        }
        return result;
    }

    /// <summary>指定分支可达的最近若干条提交。</summary>
    public static IReadOnlyList<GitCommitItem> GetRecentCommitsOnBranch(string repositoryRoot, string branchName, int maxCount = 40)
    {
        var result = new List<GitCommitItem>();
        try
        {
            using var repo = new Repository(repositoryRoot);
            var branch = repo.Branches[branchName];
            var tip = branch?.Tip;
            if (tip == null) return result;
            var filter = new CommitFilter { IncludeReachableFrom = tip };
            var n = 0;
            foreach (var c in repo.Commits.QueryBy(filter))
            {
                if (n >= maxCount) break;
                var msg = c.MessageShort ?? "";
                if (msg.Length > 60) msg = msg.Substring(0, 57) + "...";
                result.Add(new GitCommitItem(
                    c.Sha.Substring(0, Math.Min(7, c.Sha.Length)),
                    c.Sha,
                    msg,
                    c.Author?.Name ?? "",
                    c.Author?.When ?? DateTimeOffset.MinValue));
                n++;
            }
        }
        catch
        {
            // ignore
        }
        return result;
    }

    /// <summary>某提交相对其父提交的变更文件列表（合并提交与第一父比较）；首提交与空树比较。</summary>
    public static IReadOnlyList<GitCommitChangedFileItem> GetChangedFilesInCommit(string repositoryRoot, string commitSha)
    {
        var result = new List<GitCommitChangedFileItem>();
        try
        {
            using var repo = new Repository(repositoryRoot);
            var commit = repo.Lookup<LibGit2Sharp.Commit>(commitSha);
            if (commit?.Tree == null) return result;
            var parent = commit.Parents.FirstOrDefault();
            if (parent == null)
            {
                CollectAddedBlobs(commit.Tree, "", result);
                return result.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
            }
            var changes = repo.Diff.Compare<TreeChanges>(parent.Tree, commit.Tree);
            foreach (var c in changes)
            {
                var kind = c.Status switch
                {
                    ChangeKind.Added => GitTreeChangeKind.Added,
                    ChangeKind.Deleted => GitTreeChangeKind.Deleted,
                    ChangeKind.Modified => GitTreeChangeKind.Modified,
                    ChangeKind.Renamed => GitTreeChangeKind.Renamed,
                    ChangeKind.TypeChanged => GitTreeChangeKind.TypeChanged,
                    ChangeKind.Copied => GitTreeChangeKind.Added,
                    _ => GitTreeChangeKind.Modified
                };
                var rel = (c.Path ?? "").Replace('/', Path.DirectorySeparatorChar);
                if (c.Status == ChangeKind.Renamed && !string.IsNullOrEmpty(c.OldPath))
                    result.Add(new GitCommitChangedFileItem(rel, kind, c.OldPath.Replace('/', Path.DirectorySeparatorChar)));
                else
                    result.Add(new GitCommitChangedFileItem(rel, kind));
            }
        }
        catch
        {
            // ignore
        }
        return result.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void CollectAddedBlobs(Tree tree, string prefix, List<GitCommitChangedFileItem> result)
    {
        foreach (var entry in tree)
        {
            var path = string.IsNullOrEmpty(prefix) ? entry.Name : prefix + "/" + entry.Name;
            if (entry.TargetType == TreeEntryTargetType.Blob)
                result.Add(new GitCommitChangedFileItem(path.Replace('/', Path.DirectorySeparatorChar), GitTreeChangeKind.Added));
            else if (entry.TargetType == TreeEntryTargetType.Tree && entry.Target is Tree sub)
                CollectAddedBlobs(sub, path, result);
        }
    }

    /// <summary>将工作区与暂存区重置为当前 HEAD 指向的提交（丢弃未提交修改，不移动分支指针）。</summary>
    public static (bool success, string? errorMessage) ResetWorkingTreeToHead(string repositoryRoot)
    {
        try
        {
            using var repo = new Repository(repositoryRoot);
            var tip = repo.Head?.Tip;
            if (tip == null) return (false, "当前无提交（无 HEAD），无法丢弃本地修改。");
            repo.Reset(ResetMode.Hard, tip);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>删除仓库根目录下的 <c>.git</c>（目录或 gitdir 指针文件），该文件夹不再作为 Git 仓库。</summary>
    public static (bool success, string? errorMessage) DeleteDotGit(string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
            return (false, "路径无效。");
        try
        {
            var root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var gitPath = Path.Combine(root, ".git");
            if (Directory.Exists(gitPath))
            {
                DeleteDirectoryRecursive(gitPath);
                return (true, null);
            }

            if (File.Exists(gitPath))
            {
                File.Delete(gitPath);
                return (true, null);
            }

            return (false, "未找到 .git。");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static void DeleteDirectoryRecursive(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var f in Directory.EnumerateFiles(path))
        {
            File.SetAttributes(f, FileAttributes.Normal);
            File.Delete(f);
        }

        foreach (var d in Directory.EnumerateDirectories(path))
            DeleteDirectoryRecursive(d);
        Directory.Delete(path, false);
    }

    /// <summary>硬重置到指定提交（丢弃工作区与暂存区未提交内容，并移动 HEAD）。</summary>
    public static (bool success, string? errorMessage) ResetHard(string repositoryRoot, string commitSha)
    {
        try
        {
            using var repo = new Repository(repositoryRoot);
            var commit = repo.Lookup<LibGit2Sharp.Commit>(commitSha);
            if (commit == null) return (false, "找不到该提交。");
            repo.Reset(ResetMode.Hard, commit);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>生成撤销指定提交的新提交（安全，不改写历史）。</summary>
    public static (bool success, string? errorMessage) RevertCommit(string repositoryRoot, string commitSha)
    {
        try
        {
            using var repo = new Repository(repositoryRoot);
            var commit = repo.Lookup<LibGit2Sharp.Commit>(commitSha);
            if (commit == null) return (false, "找不到该提交。");
            var sig = new Signature("User", "user@local", DateTimeOffset.Now);
            repo.Revert(commit, sig, new RevertOptions());
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string? GetRelativePath(string baseDir, string fullPath)
    {
        try
        {
            var full = Path.GetFullPath(fullPath);
            var baseFull = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase)) return null;
            var sub = full.Substring(baseFull.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return sub;
        }
        catch
        {
            return null;
        }
    }
}
