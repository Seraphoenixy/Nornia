namespace Nornia.Core.Models;

/// <summary>Git worktree status of a single file. Mirrors the X/Y status letters of
/// <c>git status --porcelain</c>.</summary>
public enum GitChangeStatus
{
    Unmodified,
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    Untracked,
    TypeChanged,
    Unmerged
}

public static class GitChangeStatusExtensions
{
    public static char ToStatusLetter(this GitChangeStatus status) => status switch
    {
        GitChangeStatus.Unmodified => '.',
        GitChangeStatus.Added => 'A',
        GitChangeStatus.Modified => 'M',
        GitChangeStatus.Deleted => 'D',
        GitChangeStatus.Renamed => 'R',
        GitChangeStatus.Copied => 'C',
        GitChangeStatus.Untracked => '?',
        GitChangeStatus.TypeChanged => 'T',
        GitChangeStatus.Unmerged => 'U',
        _ => '.'
    };

    public static GitChangeStatus FromStatusLetter(char letter) => letter switch
    {
        'A' => GitChangeStatus.Added,
        'M' => GitChangeStatus.Modified,
        'D' => GitChangeStatus.Deleted,
        'R' => GitChangeStatus.Renamed,
        'C' => GitChangeStatus.Copied,
        'T' => GitChangeStatus.TypeChanged,
        'U' => GitChangeStatus.Unmerged,
        '?' => GitChangeStatus.Untracked,
        _ => GitChangeStatus.Unmodified
    };
}

/// <summary>A file-level change reported by git status.</summary>
/// <param name="Path">Path relative to the repository root.</param>
/// <param name="IndexStatus">Index (staged) status letter, <see cref="GitChangeStatus.Unmodified"/> when unchanged.</param>
/// <param name="WorkTreeStatus">Worktree status letter.</param>
/// <param name="OriginalPath">Previous path for renames/copies.</param>
/// <param name="HeadBlobId"><c>hH</c> (HEAD blob object id) from porcelain v2 field 6, when the record
/// carries it. Lets the diff-revision identity skip <c>git rev-parse HEAD:path</c> entirely.</param>
/// <param name="IndexBlobId"><c>hI</c> (index blob object id) from porcelain v2 field 7, when the
/// record carries it. Lets the diff-revision identity skip <c>git ls-files -s</c> entirely.</param>
public sealed record GitFileChange(
    string Path,
    GitChangeStatus IndexStatus,
    GitChangeStatus WorkTreeStatus,
    string? OriginalPath = null,
    string? HeadBlobId = null,
    string? IndexBlobId = null)
{
    public bool IsStaged => IndexStatus != GitChangeStatus.Unmodified;

    public bool IsUntracked =>
        IndexStatus == GitChangeStatus.Unmodified && WorkTreeStatus == GitChangeStatus.Untracked;

    public bool IsUnmerged =>
        IndexStatus == GitChangeStatus.Unmerged || WorkTreeStatus == GitChangeStatus.Unmerged;
}

/// <summary>Aggregated repository status: branch info plus staged and unstaged change lists.</summary>
public sealed record GitRepositoryStatus(
    bool IsRepository,
    string? Branch,
    string? Upstream,
    int AheadCount,
    int BehindCount,
    IReadOnlyList<GitFileChange> StagedChanges,
    IReadOnlyList<GitFileChange> UnstagedChanges,
    bool Truncated = false)
{
    public static GitRepositoryStatus NotARepository { get; } = new(false, null, null, 0, 0, [], []);

    public bool IsClean => StagedChanges.Count == 0 && UnstagedChanges.Count == 0;
}

public enum GitDiffLineKind
{
    /// <summary>Hunk header line (@@ -a,b +c,d @@).</summary>
    HunkHeader,

    /// <summary>Unchanged context line.</summary>
    Context,

    /// <summary>Line added by the change.</summary>
    Added,

    /// <summary>Line removed by the change.</summary>
    Removed,

    /// <summary>Meta notice such as "\ No newline at end of file".</summary>
    Notice,

    /// <summary>Placeholder used to keep side-by-side rows aligned.</summary>
    None
}

public sealed record GitDiffLine(GitDiffLineKind Kind, int? OldLineNumber, int? NewLineNumber, string Text);

public sealed record GitDiffHunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    string Header,
    IReadOnlyList<GitDiffLine> Lines);

/// <summary>Mutation applied to one unified-diff hunk.</summary>
public enum GitHunkOperation
{
    Stage,
    Unstage,
    Restore,
}

public abstract record GitDiffEvent;
public sealed record GitDiffMetadataEvent(string Path, string? OldPath, bool IsStaged, bool IsBinary, bool IsNewFile) : GitDiffEvent;
public sealed record GitDiffHunkEvent(int OldStart, int OldCount, int NewStart, int NewCount, string Header) : GitDiffEvent;
public sealed record GitDiffLineEvent(GitDiffLine Line) : GitDiffEvent;
public sealed record GitDiffCompletedEvent(int ExitCode, bool OutputLimitReached, string? Error = null) : GitDiffEvent;

/// <summary>Parsed unified diff for a single file.</summary>
public sealed record GitFileDiff(
    string Path,
    string? OldPath,
    bool IsStaged,
    bool IsBinary,
    bool IsNewFile,
    IReadOnlyList<GitDiffHunk> Hunks)
{
    public bool IsEmpty => Hunks.Count == 0;

    /// <summary>Aligns the unified diff into old/new column pairs for a side-by-side view.
    /// Context lines pair one-to-one; removed/added lines are paired by order with the shorter
    /// side padded with empty cells.</summary>
    public IReadOnlyList<GitSideBySideRow> ToSideBySideRows()
    {
        var rows = new List<GitSideBySideRow>();
        for (var hunkIndex = 0; hunkIndex < Hunks.Count; hunkIndex++)
        {
            var hunk = Hunks[hunkIndex];
            rows.Add(new GitSideBySideRow(null, GitDiffLineKind.None, string.Empty, null, GitDiffLineKind.None, string.Empty, HunkIndex: hunkIndex));

            var removed = new Queue<GitDiffLine>();
            var added = new Queue<GitDiffLine>();
            foreach (var line in hunk.Lines)
            {
                switch (line.Kind)
                {
                    case GitDiffLineKind.HunkHeader:
                        rows.Add(new GitSideBySideRow(null, GitDiffLineKind.HunkHeader, line.Text, null, GitDiffLineKind.HunkHeader, line.Text, HunkIndex: hunkIndex));
                        break;
                    case GitDiffLineKind.Removed:
                        removed.Enqueue(line);
                        break;
                    case GitDiffLineKind.Added:
                        added.Enqueue(line);
                        break;
                    case GitDiffLineKind.Notice:
                        rows.Add(new GitSideBySideRow(null, GitDiffLineKind.Notice, line.Text, null, GitDiffLineKind.Notice, line.Text, HunkIndex: hunkIndex));
                        break;
                    default:
                        FlushPairs(rows, removed, added, hunkIndex);
                        rows.Add(new GitSideBySideRow(
                            line.OldLineNumber, GitDiffLineKind.Context, line.Text,
                            line.NewLineNumber, GitDiffLineKind.Context, line.Text, HunkIndex: hunkIndex));
                        break;
                }
            }

            FlushPairs(rows, removed, added, hunkIndex);
        }

        return rows;
    }

    private static void FlushPairs(List<GitSideBySideRow> rows, Queue<GitDiffLine> removed, Queue<GitDiffLine> added, int hunkIndex)
    {
        while (removed.Count > 0 || added.Count > 0)
        {
            var old = removed.Count > 0 ? removed.Dequeue() : null;
            var next = added.Count > 0 ? added.Dequeue() : null;
            rows.Add(new GitSideBySideRow(
                old?.OldLineNumber, old?.Kind ?? GitDiffLineKind.None, old?.Text ?? string.Empty,
                next?.NewLineNumber, next?.Kind ?? GitDiffLineKind.None, next?.Text ?? string.Empty, HunkIndex: hunkIndex));
        }
    }
}

/// <summary>One aligned row of a side-by-side diff view.</summary>
public sealed record GitSideBySideRow(
    int? OldLineNumber,
    GitDiffLineKind OldKind,
    string OldText,
    int? NewLineNumber,
    GitDiffLineKind NewKind,
    string NewText,
    int HunkIndex = -1)
{
    public bool HasOldContent => OldKind is GitDiffLineKind.Context or GitDiffLineKind.Removed;
    public bool HasNewContent => NewKind is GitDiffLineKind.Context or GitDiffLineKind.Added;
}

/// <summary>提交引用类型:本地分支 / 远端分支 / 标签(VS Code 历史项徽标语义)。</summary>
public enum GitRefKind
{
    LocalBranch,
    RemoteBranch,
    Tag,
}

/// <summary>提交变更统计(<c>git log --shortstat</c>):文件数、新增行、删除行。
/// 用于提交悬浮窗的 +/− 统计行(VS Code 历史项悬浮窗的 shortStats 段)。</summary>
public sealed record GitCommitStats(int FilesChanged, int Insertions, int Deletions);

/// <summary>挂在某个提交上的引用(来自 <c>git log --decorate=full</c> 的 <c>%D</c>),
/// 用于提交行与悬浮窗中的分支徽标。<paramref name="IsHead"/> 表示该本地分支是当前 HEAD
/// 所在分支(<c>%D</c> 中带 <c>HEAD -&gt;</c> 前缀),徽标使用当前分支高亮色。</summary>
public sealed record GitRefInfo(string Name, GitRefKind Kind, bool IsHead = false);

/// <summary>A commit entry from <c>git log</c>.</summary>
/// <param name="Parents">Parent commit hashes (empty for roots); drives the commit-graph lanes.</param>
/// <param name="Refs">指向该提交的引用(HEAD 已剔除;无引用时为空)。</param>
/// <param name="Stats">文件/新增/删除行统计(<c>--shortstat</c>;未请求或缺失时为空)。</param>
public sealed record GitCommitInfo(
    string Hash,
    string ShortHash,
    string Subject,
    string? Body,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthorDate,
    IReadOnlyList<string>? Parents = null,
    IReadOnlyList<GitRefInfo>? Refs = null,
    GitCommitStats? Stats = null)
{
    /// <summary>非空父哈希视图(旧构造未传时为空)。</summary>
    public IReadOnlyList<string> ParentList => Parents ?? [];

    /// <summary>非空引用视图(旧构造/旧输出未传时为空)。</summary>
    public IReadOnlyList<GitRefInfo> RefList => Refs ?? [];
}

/// <summary>一条贮藏记录(<c>git stash list</c>)。</summary>
public sealed record GitStashInfo(int Index, string Message);

/// <summary>A local branch.</summary>
public sealed record GitBranchInfo(
    string Name,
    bool IsCurrent,
    string? Upstream = null,
    int AheadCount = 0,
    int BehindCount = 0,
    bool IsRemote = false,
    string? TipHash = null);

/// <summary>Configured pull strategy for <c>git pull</c>. Nornia does not accept credentials in-memory;
/// authentication is delegated to Git Credential Manager (GCM) and the Windows Credential Manager vault.</summary>
public sealed class GitPullOptions
{
    /// <summary>Only allow fast-forward updates; never create a merge commit. This is the safest default
    /// for automated tooling because it cannot silently rewrite history.</summary>
    public bool FastForwardOnly { get; set; } = true;

    /// <summary>Rebase the current branch onto the fetched upstream instead of merging. When
    /// <see cref="FastForwardOnly"/> is also enabled a normal fast-forward is still preferred.</summary>
    public bool Rebase { get; set; } = false;

    /// <summary>Fetch all remotes before integrating. Equivalent to <c>git pull --all</c>.</summary>
    public bool AllRemotes { get; set; } = false;

    /// <summary>Automatically stash/unstash local changes if the working tree is dirty so the pull can
    /// proceed. Equivalent to <c>git pull --autostash</c>.</summary>
    public bool AutoStash { get; set; } = false;

    /// <summary>When true, run <c>git fetch</c> first then inspect the ahead/behind counts to determine
    /// whether a merge or rebase is needed before touching the worktree.</summary>
    public bool CheckFirst { get; set; } = true;

    public static GitPullOptions SafeDefault => new() { FastForwardOnly = true, CheckFirst = true };
    public static GitPullOptions RebaseClean => new() { FastForwardOnly = true, Rebase = true, AutoStash = true };

    public IEnumerable<string> BuildArguments()
    {
        yield return "pull";

        if (FastForwardOnly) yield return "--ff-only";
        else if (Rebase) yield return "--rebase";

        if (AllRemotes) yield return "--all";
        if (AutoStash) yield return "--autostash";

        // NOTE: no --username / --password / token flags are ever emitted. Nornia relies on
        // git-credential-manager to surface the Windows Credential Manager vault transparently.
    }
}

public enum GitPullResultKind
{
    FastForwarded,
    Merged,
    Rebased,
    AlreadyUpToDate,
    RefusedFastForward,
    MergeConflict,
    Error
}

public sealed record GitPullResult(
    GitPullResultKind Kind,
    int AddedCommits,
    string? Message,
    IReadOnlyList<string> ChangedPaths)
{
    public bool Success => Kind is GitPullResultKind.FastForwarded
        or GitPullResultKind.Merged
        or GitPullResultKind.Rebased
        or GitPullResultKind.AlreadyUpToDate;
}

public sealed record GitPushOptions
{
    /// <summary>Set upstream tracking on first push of a new local branch.</summary>
    public bool SetUpstream { get; set; } = true;

    /// <summary>Require a signed push (GPG/SSH). Equivalent to <c>git push --signed</c>.</summary>
    public bool Signed { get; set; } = false;

    /// <summary>Force-with-lease: only overwrite the remote ref if the remote still matches what the
    /// local repository expects. Never use --force; this is the only safe force variant.</summary>
    public bool ForceWithLease { get; set; } = false;

    public IEnumerable<string> BuildArguments(string branchName)
    {
        yield return "push";

        if (ForceWithLease) yield return "--force-with-lease";
        if (Signed) yield return "--signed";
        if (SetUpstream) yield return "--set-upstream";

        yield return "origin";
        yield return branchName;
    }
}
