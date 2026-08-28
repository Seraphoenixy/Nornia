using System.Text;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Desktop.Code;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>D1: DiffTab load off the UI thread — the expensive build phase (spool read-back →
/// event collection → row building → counts) runs off the calling thread for large diffs and the
/// result is installed into the observable collections in one hop; a generation guard keeps a
/// late older load from clobbering a newer one; cancellation through the lifetime token leaves
/// the tab in a clean unloaded state. Small diffs keep the synchronous fast path so the
/// "loaded == ready" contract (asserted synchronously by the pre-existing tests) is preserved.</summary>
public sealed class DiffTabLoadPerformanceTests
{
    private static GitFileDiff SmallDiff() => new(
        "src/A.cs",
        null,
        false,
        false,
        false,
        [
            new GitDiffHunk(1, 2, 1, 3, "@@ -1,2 +1,3 @@",
            [
                new GitDiffLine(GitDiffLineKind.Context, 1, 1, "keep"),
                new GitDiffLine(GitDiffLineKind.Removed, 2, null, "old-line"),
                new GitDiffLine(GitDiffLineKind.Added, null, 2, "new-line"),
                new GitDiffLine(GitDiffLineKind.Added, null, 3, "extra"),
            ])
        ]);

    /// <summary>500 hunks × 5 lines = 2500 lines — above the 2000-line inline fast-path bound,
    /// so the build runs through Task.Run. Known ground truth: 1000 added, 500 removed, 500 blocks.</summary>
    private static (GitFileDiff Diff, int Added, int Removed, int Blocks) LargeDiff()
    {
        var hunks = new List<GitDiffHunk>(500);
        for (var i = 0; i < 500; i++)
        {
            var baseLine = 2 * i + 1;
            hunks.Add(new GitDiffHunk(
                baseLine,
                3,
                baseLine,
                4,
                $"@@ -{baseLine},3 +{baseLine},4 @@",
                [
                    new GitDiffLine(GitDiffLineKind.Context, baseLine, baseLine, $"ctx {i}"),
                    new GitDiffLine(GitDiffLineKind.Removed, baseLine + 1, null, $"old {i}"),
                    new GitDiffLine(GitDiffLineKind.Added, null, baseLine + 1, $"new {i}"),
                    new GitDiffLine(GitDiffLineKind.Added, null, baseLine + 2, $"extra {i}"),
                    new GitDiffLine(GitDiffLineKind.Context, baseLine + 2, baseLine + 3, $"ctx2 {i}"),
                ]));
        }

        return (new GitFileDiff("big/a.txt", null, false, false, false, hunks), 1000, 500, 500);
    }

    private static DiffTab CreateTab(FakeGitService git) =>
        new(git, new GitDiffRequest(@"C:\repo", "src/A.cs", false, false),
            new FakeUiLogService(), new FakeClipboardService());

    [Fact]
    public async Task SmallDiff_LoadCompletesAndCollectionsBind()
    {
        var git = new FakeGitService { DiffResult = SmallDiff() };
        var editor = new EditorAreaViewModel(git, new FakeUiLogService());

        await editor.OpenDiffAsync(new GitDiffRequest(@"C:\repo", "src/A.cs", false, false));

        var tab = Assert.IsType<DiffTab>(Assert.Single(editor.OpenTabs));
        Assert.True(tab.IsLoaded);
        Assert.True(tab.HasDiff);
        Assert.False(tab.IsEmptyDiff);
        Assert.Equal("src/A.cs", tab.DiffTitle);
        Assert.Equal(string.Empty, tab.DiffNotice);
        Assert.Equal(ReadOnlyContentTier.Full, tab.CapacityTier);

        // Inline lines: one hunk header + the four payload lines, in order.
        Assert.Equal(5, tab.DiffLines.Count);
        Assert.Equal(GitDiffLineKind.HunkHeader, tab.DiffLines[0].Kind);
        Assert.Equal("old-line", tab.DiffLines.First(l => l.Kind == GitDiffLineKind.Removed).Text);
        Assert.Equal(2, tab.DiffLines.Count(l => l.Kind == GitDiffLineKind.Added));

        // Side-by-side rows: spacer + hunk + context + (removed|added) + (—|added).
        Assert.Equal(5, tab.SideBySideRows.Count);
        Assert.Equal(GitDiffLineKind.None, tab.SideBySideRows[0].OldKind);
        Assert.Equal("old-line", tab.SideBySideRows[3].OldText);
        Assert.Equal(GitDiffLineKind.Added, tab.SideBySideRows[3].NewKind);
        Assert.Equal("new-line", tab.SideBySideRows[3].NewText);
        Assert.Equal(string.Empty, tab.SideBySideRows[4].OldText);
        Assert.Equal("extra", tab.SideBySideRows[4].NewText);

        Assert.Single(tab.Hunks);
    }

    [Fact]
    public async Task LargeDiff_BuildsOffUiThreadAndCollectionsBind()
    {
        var (diff, added, removed, blocks) = LargeDiff();
        var git = new FakeGitService { DiffResult = diff };
        var tab = CreateTab(git);

        await tab.LoadAsync();

        // 3000 行 > InlineBuildMaxLines: the build ran in Task.Run and was installed in one
        // hop — LoadAsync must still return with the tab fully ready.
        Assert.True(tab.IsLoaded);
        Assert.True(tab.HasDiff);
        Assert.Equal(3000, tab.DiffLines.Count);
        Assert.Equal(6 * 500, tab.SideBySideRows.Count); // spacer+hunk+ctx+pair+pad+ctx per hunk
        Assert.Equal(500, tab.Hunks.Count);
        Assert.Equal(added, tab.AddedCount);
        Assert.Equal(removed, tab.RemovedCount);
        Assert.Equal(blocks, tab.ChangeCount);
        Assert.Equal($"+{added} −{removed}", tab.ChangeSummary);
        Assert.Equal("1 / 500", tab.ChangeCounter);
        Assert.Equal(ReadOnlyContentTier.Full, tab.CapacityTier);
        Assert.Equal("src/A.cs", tab.DiffTitle);
        Assert.Equal(string.Empty, tab.DiffNotice);
    }

    [Fact]
    public async Task OverlappingLoads_LastLoadWins()
    {
        var git = new GatedFakeGitService
        {
            FirstResult = new GitFileDiff("a.cs", null, false, false, false,
            [
                new GitDiffHunk(1, 2, 1, 2, "@@ -1,2 +1,2 @@",
                [
                    new GitDiffLine(GitDiffLineKind.Context, 1, 1, "keep"),
                    new GitDiffLine(GitDiffLineKind.Added, null, 2, "AAA-marker"),
                ])
            ]),
            SecondResult = new GitFileDiff("a.cs", null, false, false, false,
            [
                new GitDiffHunk(1, 2, 1, 3, "@@ -1,2 +1,3 @@",
                [
                    new GitDiffLine(GitDiffLineKind.Context, 1, 1, "keep"),
                    new GitDiffLine(GitDiffLineKind.Added, null, 2, "BBB-marker"),
                    new GitDiffLine(GitDiffLineKind.Removed, 2, null, "old-1"),
                    new GitDiffLine(GitDiffLineKind.Removed, 3, null, "old-2"),
                ])
            ]),
        };
        var tab = new DiffTab(git, new GitDiffRequest(@"C:\repo", "a.cs", false, false),
            new FakeUiLogService(), new FakeClipboardService());

        // load1 parks inside the spool phase (first GetDiffAsync awaits the gate).
        var load1 = tab.LoadAsync();
        Assert.Equal(1, git.DiffCalls); // reached the spool, holding the load gate

        // load2 starts while load1 is still in flight; it must be the one whose result is kept.
        var load2 = tab.ReloadDiffAsync();
        Assert.Equal(1, git.DiffCalls); // still serialized behind load1

        git.ReleaseFirstCall();
        await Task.WhenAll(load1, load2);
        Assert.Equal(2, git.DiffCalls);

        // Last-started load wins: no mix of the two generations, counts follow the new load.
        Assert.True(tab.IsLoaded);
        Assert.DoesNotContain(tab.DiffLines, line => line.Text == "AAA-marker");
        Assert.Contains(tab.DiffLines, line => line.Text == "BBB-marker");
        Assert.Equal(1, tab.AddedCount);
        Assert.Equal(2, tab.RemovedCount);
        Assert.Single(tab.Hunks);
    }

    [Fact]
    public async Task CancelledLoad_DoesNotPublishAfterDispose()
    {
        var git = new GatedFakeGitService
        {
            FirstResult = new GitFileDiff("a.cs", null, false, false, false,
            [
                new GitDiffHunk(1, 1, 1, 1, "@@ -1,1 +1,1 @@",
                [
                    new GitDiffLine(GitDiffLineKind.Added, null, 1, "late"),
                ])
            ]),
        };
        var tab = new DiffTab(git, new GitDiffRequest(@"C:\repo", "a.cs", false, false),
            new FakeUiLogService(), new FakeClipboardService());

        var load = tab.LoadAsync();
        Assert.Equal(1, git.DiffCalls); // parked in the spool phase

        // Dispose the tab while the load is in flight: the lifetime token cancels and the
        // generation guard invalidates whatever result arrives later.
        tab.ReleaseResources();
        git.ReleaseFirstCall();

        await load; // must complete without throwing

        Assert.Empty(tab.DiffLines);
        Assert.Empty(tab.SideBySideRows);
        Assert.Empty(tab.Hunks);
        Assert.False(tab.IsLoaded);
        Assert.Equal(0, tab.AddedCount);
    }
}

/// <summary>D10: added/removed/change counts are accumulated while the diff lines are filled
/// (single pass) and must equal the actual line kinds in the bound collections.</summary>
public sealed class DiffTabCountTests
{
    private static DiffTab CreateTab(GitFileDiff diff)
    {
        var git = new FakeGitService { DiffResult = diff };
        return new DiffTab(git, new GitDiffRequest(@"C:\repo", "a.cs", false, false),
            new FakeUiLogService(), new FakeClipboardService());
    }

    [Fact]
    public async Task Counts_EqualActualLineKinds_WithSeparateBlocks()
    {
        // Two change blocks separated by context: (R,A,A) and (R). Notice lines do not split blocks.
        var diff = new GitFileDiff("a.cs", null, false, false, false,
        [
            new GitDiffHunk(1, 6, 1, 7, "@@ -1,6 +1,7 @@",
            [
                new GitDiffLine(GitDiffLineKind.Context, 1, 1, "keep"),
                new GitDiffLine(GitDiffLineKind.Removed, 2, null, "one"),
                new GitDiffLine(GitDiffLineKind.Added, null, 2, "uno"),
                new GitDiffLine(GitDiffLineKind.Added, null, 3, "extra"),
                new GitDiffLine(GitDiffLineKind.Context, 3, 4, "keep2"),
                new GitDiffLine(GitDiffLineKind.Removed, 4, null, "two"),
            ])
        ]);
        var tab = CreateTab(diff);

        await tab.LoadAsync();

        // Cross-check the published counters against the bound collection itself.
        Assert.Equal(tab.DiffLines.Count(line => line.Kind == GitDiffLineKind.Added), tab.AddedCount);
        Assert.Equal(tab.DiffLines.Count(line => line.Kind == GitDiffLineKind.Removed), tab.RemovedCount);
        Assert.Equal(2, tab.AddedCount);
        Assert.Equal(2, tab.RemovedCount);
        Assert.Equal(2, tab.ChangeCount);
        Assert.Equal("+2 −2", tab.ChangeSummary);
        Assert.Equal("1 / 2", tab.ChangeCounter);
    }

    [Fact]
    public async Task Counts_EqualActualLineKinds_LargeDiff()
    {
        // 625 hunks × (1 ctx + 1 removed + 2 added) = 2500 lines — above the inline fast-path
        // bound, so the counts come from the single-pass background build.
        var hunks = new List<GitDiffHunk>(625);
        for (var i = 0; i < 625; i++)
        {
            var baseLine = 2 * i + 1;
            hunks.Add(new GitDiffHunk(baseLine, 2, baseLine, 3, $"@@ -{baseLine},2 +{baseLine},3 @@",
            [
                new GitDiffLine(GitDiffLineKind.Context, baseLine, baseLine, $"ctx {i}"),
                new GitDiffLine(GitDiffLineKind.Removed, baseLine + 1, null, $"old {i}"),
                new GitDiffLine(GitDiffLineKind.Added, null, baseLine + 1, $"new {i}"),
                new GitDiffLine(GitDiffLineKind.Added, null, baseLine + 2, $"extra {i}"),
            ]));
        }

        var tab = CreateTab(new GitFileDiff("a.cs", null, false, false, false, hunks));
        await tab.LoadAsync();

        // 625 hunks × (1 ctx + 1 removed + 2 added) = 2500 内容行 + 625 条 hunk 头 = 3125。
        Assert.Equal(3125, tab.DiffLines.Count);
        Assert.Equal(1250, tab.AddedCount);
        Assert.Equal(625, tab.RemovedCount);
        Assert.Equal(625, tab.ChangeCount);
        Assert.Equal(tab.DiffLines.Count(line => line.Kind == GitDiffLineKind.Added), tab.AddedCount);
        Assert.Equal(tab.DiffLines.Count(line => line.Kind == GitDiffLineKind.Removed), tab.RemovedCount);
    }
}

/// <summary>E10: windowed find is debounced — a burst of search-text changes does not launch a
/// scan per keystroke; the previous match list is kept until the debounced scan lands, and a
/// strict refinement (same prefix, longer literal query) preserves the earlier results as a
/// subset while unchanged segments keep their instances (incremental replacement, not
/// clear+re-add).</summary>
public sealed class WindowedFindDebounceTests : IDisposable
{
    private const string Filler = "lorem ipsum dolor sit amet consectetur adipiscing elit sed do";
    private const int TotalLines = 150_000; // ≈ 9.5 MB — above the 8 MB full-source threshold
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nornia-find-debounce-{Guid.NewGuid():N}");

    public WindowedFindDebounceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }

    private static async Task<FilePreviewTab> CreateWindowedTabAsync(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        var lines = new string[TotalLines];
        Array.Fill(lines, Filler);
        lines[99] = "needle alpha";     // line 100 (initial window)
        lines[9999] = "needle beta";    // line 10000
        lines[19999] = "NEEDLE gamma";  // line 20000
        lines[29999] = "pneedle delta"; // line 30000
        await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(false));

        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);
        await tab.LoadAsync();
        Assert.Equal(1, tab.WindowStartLine);
        return tab;
    }

    private static async Task WaitForAsync(Func<bool> condition, Func<string>? describe = null, int timeoutMs = 30_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Find state did not converge in time. " + (describe?.Invoke() ?? string.Empty));
            }

            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Keystrokes_DebounceAndRetainPreviousResults()
    {
        var tab = await CreateWindowedTabAsync(_tempDir, "debounce.txt");

        tab.SearchText = "needle";
        await WaitForAsync(() => tab.MatchCount == 4 && tab.SearchMatches.Count == 1);
        Assert.Equal(100, Assert.Single(tab.SearchMatches).Line);

        // Single keystroke change: inside the 250 ms debounce window no scan has started, so the
        // previous result must still be displayed (a per-keystroke scan would already have run).
        tab.SearchText = "pneedle";
        await Task.Delay(100);
        Assert.Equal(4, tab.MatchCount);
        Assert.Equal(100, Assert.Single(tab.SearchMatches).Line);
        await WaitForAsync(() => tab.MatchCount == 1 && tab.SearchMatches.Count == 0);

        // Burst: three more keystrokes coalesce; still the previous result until the single
        // debounced sweep for the final text lands.
        tab.SearchText = "pnee";
        tab.SearchText = "pne";
        tab.SearchText = "zzz";
        await Task.Delay(100);
        Assert.Equal(1, tab.MatchCount);
        await WaitForAsync(() => tab.MatchCount == 0);
        Assert.Empty(tab.SearchMatches);

        // Escape clears immediately (no debounce).
        tab.SearchText = "needle";
        await WaitForAsync(() => tab.MatchCount == 4 && tab.SearchMatches.Count == 1);
        tab.ClearSearchCommand.Execute(null);
        Assert.Equal(string.Empty, tab.SearchText);
        Assert.Equal(0, tab.MatchCount);
        Assert.Empty(tab.SearchMatches);
    }

    [Fact]
    public async Task Refinement_KeepsPreviousMatchesAndReusesUnchangedSegments()
    {
        var tab = await CreateWindowedTabAsync(_tempDir, "refine.txt");

        tab.SearchText = "needle";
        await WaitForAsync(() => tab.MatchCount == 4 && tab.SearchMatches.Count == 1);

        // Strict refinement (prefix + longer literal): the previous list stays visible while the
        // debounced scan runs, and the new result is a subset of it (only line 100 survives).
        tab.SearchText = "needle alpha";
        await Task.Delay(100);
        Assert.Equal(4, tab.MatchCount); // retained until the new result lands
        await WaitForAsync(() => tab.MatchCount == 1 && tab.SearchMatches.Count == 1);

        var refined = Assert.Single(tab.SearchMatches);
        Assert.Equal(100, refined.Line);
        Assert.Equal(99 * (Filler.Length + 1), refined.Offset);
        Assert.Equal("needle alpha".Length, refined.Length); // changed segment: new length

        // Re-scans with identical results keep the unchanged segment's instance (identity is the
        // hook the view uses to swap only changed segments instead of rebuilding everything).
        tab.SearchCaseSensitive = true;
        await WaitForAsync(() => tab.MatchCount == 1 && tab.SearchMatches.Count == 1);
        tab.SearchCaseSensitive = false;
        await WaitForAsync(() => tab.MatchCount == 1 && tab.SearchMatches.Count == 1);
        Assert.Same(refined, Assert.Single(tab.SearchMatches));
    }
}

/// <summary>IGitService fake for the overlapping-load test: the first diff call parks on a gate
/// (so load 1 stays in flight while load 2 is started), then each call returns a distinct diff.
/// Only the diff-streaming surface is exercised; everything else returns inert defaults.</summary>
internal sealed class GatedFakeGitService : IGitService
{
    private int _diffCalls;
    private readonly TaskCompletionSource _firstCallGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GitFileDiff? FirstResult { get; init; }
    public GitFileDiff? SecondResult { get; init; }

    public int DiffCalls => Volatile.Read(ref _diffCalls);

    public void ReleaseFirstCall() => _firstCallGate.TrySetResult();

    public async Task<GitFileDiff?> GetDiffAsync(string repositoryPath, string path, bool staged, bool isUntracked = false, CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _diffCalls);
        if (call == 1)
        {
            await _firstCallGate.Task;
        }

        return call == 1 ? FirstResult : SecondResult;
    }

    public Task<string> GetDiffRevisionAsync(string repositoryPath, string path, bool staged, bool isUntracked = false, CancellationToken cancellationToken = default) =>
        Task.FromResult("revision-gated");

    public Task<GitRepositoryStatus> GetStatusAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(GitRepositoryStatus.NotARepository);

    public Task<string> GetRawDiffAsync(string repositoryPath, bool staged, IReadOnlyList<string>? paths = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Empty);

    public Task ApplyHunkAsync(string repositoryPath, string path, bool staged, GitDiffHunk hunk, GitHunkOperation operation, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task StageAsync(string repositoryPath, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task UnstageAsync(string repositoryPath, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DiscardAsync(string repositoryPath, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DiscardUnstagedAsync(string repositoryPath, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task CommitAsync(string repositoryPath, string message, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<GitCommitInfo>> GetLogAsync(string repositoryPath, int count = 30, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GitCommitInfo>>([]);

    public Task<IReadOnlyList<GitCommitInfo>> GetIncomingCommitsAsync(string repositoryPath, string upstreamReference, int count = 30, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GitCommitInfo>>([]);

    public Task<IReadOnlyList<GitCommitInfo>> GetOutgoingCommitsAsync(string repositoryPath, string upstreamReference, int count = 30, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GitCommitInfo>>([]);

    public Task<IReadOnlyList<GitStashInfo>> GetStashesAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GitStashInfo>>([]);

    public Task StashAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task PopStashAsync(string repositoryPath, int stashIndex, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DropStashAsync(string repositoryPath, int stashIndex, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GitBranchInfo>>([]);

    public Task<IReadOnlyList<GitBranchInfo>> GetRemoteBranchesAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GitBranchInfo>>([]);

    public Task CreateBranchAsync(string repositoryPath, string branchName, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SwitchBranchAsync(string repositoryPath, string branchName, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task FetchAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task PullAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<GitPullResult> PullAsync(string repositoryPath, GitPullOptions options, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GitPullResult(GitPullResultKind.AlreadyUpToDate, 0, null, []));

    public Task PushAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task PushAsync(string repositoryPath, GitPushOptions options, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<GitFileChange>> GetCommitFilesAsync(string repositoryPath, string commitHash, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GitFileChange>>([]);

    public Task<IReadOnlyList<GitFileChange>> GetIncomingFilesAsync(string repositoryPath, string upstreamReference, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GitFileChange>>([]);

    public Task<IReadOnlyList<GitFileChange>> GetOutgoingFilesAsync(string repositoryPath, string upstreamReference, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GitFileChange>>([]);

    public Task<GitFileDiff?> GetCommitFileDiffAsync(string repositoryPath, string commitHash, string path, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitFileDiff?>(null);
}
