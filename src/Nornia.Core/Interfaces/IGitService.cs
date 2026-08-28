using Nornia.Core.Models;

namespace Nornia.Core.Interfaces;

/// <summary>Git source-control operations backed by the git CLI. Repositories are addressed by path;
/// all git commands run with <c>git -C &lt;repositoryPath&gt;</c> so no working-directory state is
/// required from the caller.
/// <para>Authentication policy: Nornia never accepts or stores credentials in-memory. Token fields,
/// URI credentials, and <c>git -c credential.*</c> overrides are intentionally not exposed. Push/pull
/// rely on Git Credential Manager (GCM) reading the Windows Credential Manager vault.</para></summary>
public interface IGitService
{
    /// <summary>Reads branch/upstream info and staged/unstaged changes. Returns
    /// <see cref="GitRepositoryStatus.NotARepository"/> when the path is not inside a git worktree.</summary>
    Task<GitRepositoryStatus> GetStatusAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>Initializes a new local Git repository in an existing directory. This creates only
    /// Git metadata; it does not stage files, create a commit, or configure a remote.</summary>
    Task InitializeRepositoryAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("此 Git 服务不支持初始化仓库。"));

    /// <summary>Reads the unified diff of one file. Untracked files are rendered as fully-added lines
    /// from their current content; binary files are flagged instead of diffed.</summary>
    Task<GitFileDiff?> GetDiffAsync(
        string repositoryPath,
        string path,
        bool staged,
        bool isUntracked = false,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a lightweight identity for one working-tree/index diff. The identity is
    /// based on Git object ids and file existence rather than the complete diff text, so callers
    /// can decide whether an already-open diff needs to be reloaded after an index event.</summary>
    Task<string> GetDiffRevisionAsync(
        string repositoryPath,
        string path,
        bool staged,
        bool isUntracked = false,
        CancellationToken cancellationToken = default);

    /// <summary>Diff-revision identity that reuses porcelain v2 blob ids already captured by the
    /// status parser (<see cref="Models.GitFileChange.HeadBlobId"/> / <see cref="Models.GitFileChange.IndexBlobId"/>),
    /// eliminating the <c>rev-parse</c>/<c>ls-files</c> processes for staged changes. Default
    /// implementation (test stubs) falls back to <see cref="GetDiffRevisionAsync"/>.</summary>
    Task<string> GetDiffRevisionWithBlobsAsync(
        string repositoryPath,
        string path,
        bool staged,
        bool isUntracked,
        string? headBlobId,
        string? indexBlobId,
        CancellationToken cancellationToken = default) =>
        GetDiffRevisionAsync(repositoryPath, path, staged, isUntracked, cancellationToken);

    /// <summary>Streams one file diff from the git process through the incremental parser. The
    /// terminal event always carries the process exit status and output-limit state.</summary>
    async IAsyncEnumerable<GitDiffEvent> StreamDiffAsync(
        string repositoryPath,
        string path,
        bool staged,
        bool isUntracked = false,
        int maximumLines = 500_000,
        bool ignoreWhitespaceEndOfLine = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 默认实现(测试桩)忽略行尾空白标记;真实 GitService 重写按 --ignore-space-at-eol 流式解析。
        var diff = await GetDiffAsync(repositoryPath, path, staged, isUntracked, cancellationToken).ConfigureAwait(false);
        if (diff is null)
        {
            yield return new GitDiffCompletedEvent(-1, false);
            yield break;
        }

        yield return new GitDiffMetadataEvent(diff.Path, diff.OldPath, diff.IsStaged, diff.IsBinary, diff.IsNewFile);
        var emitted = 0;
        foreach (var hunk in diff.Hunks)
        {
            yield return new GitDiffHunkEvent(hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount, hunk.Header);
            foreach (var line in hunk.Lines)
            {
                if (line.Kind == GitDiffLineKind.HunkHeader) continue;
                if (++emitted > maximumLines)
                {
                    yield return new GitDiffCompletedEvent(0, true);
                    yield break;
                }
                yield return new GitDiffLineEvent(line);
            }
        }
        yield return new GitDiffCompletedEvent(0, false);
    }

    async IAsyncEnumerable<GitDiffEvent> StreamCommitFileDiffAsync(
        string repositoryPath,
        string commitHash,
        string path,
        int maximumLines = 500_000,
        bool ignoreWhitespaceEndOfLine = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 默认实现(测试桩)忽略行尾空白标记;真实 GitService 重写按 --ignore-space-at-eol 流式解析。
        var diff = await GetCommitFileDiffAsync(repositoryPath, commitHash, path, cancellationToken).ConfigureAwait(false);
        if (diff is null) { yield return new GitDiffCompletedEvent(-1, false); yield break; }
        yield return new GitDiffMetadataEvent(diff.Path, diff.OldPath, false, diff.IsBinary, diff.IsNewFile);
        var emitted = 0;
        foreach (var hunk in diff.Hunks)
        {
            yield return new GitDiffHunkEvent(hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount, hunk.Header);
            foreach (var line in hunk.Lines)
            {
                if (line.Kind == GitDiffLineKind.HunkHeader) continue;
                if (++emitted > maximumLines) { yield return new GitDiffCompletedEvent(0, true); yield break; }
                yield return new GitDiffLineEvent(line);
            }
        }
        yield return new GitDiffCompletedEvent(0, false);
    }

    /// <summary>Returns the raw unified diff text (for CLI output). Empty <paramref name="paths"/>
    /// diffs the whole worktree (or index when <paramref name="staged"/> is set).</summary>
    Task<string> GetRawDiffAsync(
        string repositoryPath,
        bool staged,
        IReadOnlyList<string>? paths = null,
        CancellationToken cancellationToken = default);

    /// <summary>Applies exactly one currently visible hunk. The implementation must re-read and
    /// validate the raw patch before applying it so a stale Diff tab cannot mutate another hunk.</summary>
    Task ApplyHunkAsync(
        string repositoryPath,
        string path,
        bool staged,
        GitDiffHunk hunk,
        GitHunkOperation operation,
        CancellationToken cancellationToken = default);

    /// <summary>Stages the given paths (relative to the repository root); an empty collection stages
    /// every change.</summary>
    Task StageAsync(string repositoryPath, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Unstages the given paths; an empty collection unstages every change.</summary>
    Task UnstageAsync(string repositoryPath, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Discards changes for the given paths, reverting index and worktree to HEAD; an empty
    /// collection discards every change.</summary>
    Task DiscardAsync(string repositoryPath, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Discards only the WORKTREE changes for the given paths (index/staged content stays
    /// intact); an empty collection discards every un-staged change, including untracked files.
    /// Used for "丢弃" from the unstaged list — staged content must never be discarded directly.</summary>
    Task DiscardUnstagedAsync(string repositoryPath, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default);

    Task CommitAsync(string repositoryPath, string message, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitCommitInfo>> GetLogAsync(string repositoryPath, int count = 30, CancellationToken cancellationToken = default);

    /// <summary>仅存在于上游的提交(待拉取,<c>HEAD..upstream</c>)。</summary>
    Task<IReadOnlyList<GitCommitInfo>> GetIncomingCommitsAsync(
        string repositoryPath,
        string upstreamReference,
        int count = 30,
        CancellationToken cancellationToken = default);

    /// <summary>仅存在于本地的提交(待推送,<c>upstream..HEAD</c>)。</summary>
    Task<IReadOnlyList<GitCommitInfo>> GetOutgoingCommitsAsync(
        string repositoryPath,
        string upstreamReference,
        int count = 30,
        CancellationToken cancellationToken = default);

    /// <summary>列出贮藏记录(<c>git stash list</c>)。</summary>
    Task<IReadOnlyList<GitStashInfo>> GetStashesAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>贮藏当前全部更改(含未跟踪文件)。</summary>
    Task StashAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>应用并移除一条贮藏(索引按 <c>stash list</c> 顺序)。</summary>
    Task PopStashAsync(string repositoryPath, int index, CancellationToken cancellationToken = default);

    /// <summary>丢弃一条贮藏。</summary>
    Task DropStashAsync(string repositoryPath, int index, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(string repositoryPath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitBranchInfo>> GetRemoteBranchesAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>Creates the branch and switches to it.</summary>
    Task CreateBranchAsync(string repositoryPath, string branchName, CancellationToken cancellationToken = default);

    Task SwitchBranchAsync(string repositoryPath, string branchName, CancellationToken cancellationToken = default);

    /// <summary>Downloads remote refs without modifying the current worktree.</summary>
    Task FetchAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>Pulls using <see cref="GitPullOptions.SafeDefault"/> (fast-forward only, no merge).
    /// Credentials are never injected from Nornia; Git Credential Manager handles them via the
    /// Windows Credential Manager vault.</summary>
    Task PullAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>Pulls with an explicit strategy. See <see cref="GitPullOptions"/> for the supported
    /// flags. No credential helpers or tokens are appended to the argument list.</summary>
    Task<GitPullResult> PullAsync(string repositoryPath, GitPullOptions options, CancellationToken cancellationToken = default);

    /// <summary>Pushes using <see cref="GitPushOptions"/> defaults (set-upstream, no force).</summary>
    Task PushAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>Pushes with an explicit strategy. Force-variant is always
    /// <see cref="GitPushOptions.ForceWithLease"/>; plain <c>--force</c> is not exposed.</summary>
    Task PushAsync(string repositoryPath, GitPushOptions options, CancellationToken cancellationToken = default);

    /// <summary>Lists the files changed by a commit (name-status style).</summary>
    Task<IReadOnlyList<GitFileChange>> GetCommitFilesAsync(string repositoryPath, string commitHash, CancellationToken cancellationToken = default);

    /// <summary>传入提交对文件的合并影响(<c>git diff --name-status HEAD...upstream</c>):自合并基
    /// 到上游的净变化——同一文件被多个传入提交修改只计一次,拉取后工作区即为此结果。</summary>
    Task<IReadOnlyList<GitFileChange>> GetIncomingFilesAsync(
        string repositoryPath,
        string upstreamReference,
        CancellationToken cancellationToken = default);

    /// <summary>传出提交对文件的合并影响(<c>git diff --name-status upstream...HEAD</c>):自合并基
    /// 到本地的净变化——推送后在远端形成的内容差异。</summary>
    Task<IReadOnlyList<GitFileChange>> GetOutgoingFilesAsync(
        string repositoryPath,
        string upstreamReference,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the diff of one file within a specific commit.</summary>
    Task<GitFileDiff?> GetCommitFileDiffAsync(string repositoryPath, string commitHash, string path, CancellationToken cancellationToken = default);
}
