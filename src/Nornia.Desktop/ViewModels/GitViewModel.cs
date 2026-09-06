using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Core.Collections;
using Nornia.Desktop;
using Nornia.Desktop.Services;
using Nornia.Desktop.Configuration;
using Nornia.Git;
using Nornia.Project.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;

namespace Nornia.Desktop.ViewModels;

/// <summary>VS Code-style source-control view: repository state, staged/unstaged changes, commit,
/// branch management and history. Diff content is shown in the shared editor area (the workbench
/// forwards <see cref="DiffOpenRequested"/>); this view never edits files — content changes go
/// through git operations only.</summary>
public partial class GitViewModel : PageViewModel, INavigationTarget
{
    private readonly IGitService _gitService;
    private readonly IFolderPickerService _folderPicker;
    private readonly IConfirmationService _confirmationService;
    private readonly ISettingsService _scopedSettings;
    private ISettingsSession? _settingsSession;
    private IApplicationStateStore? _stateStore;
    private readonly IProjectCatalogService _projectCatalogService;
    private readonly EditorAreaViewModel _editor;
    private readonly IClipboardService _clipboard;
    private readonly IGitRepositoryWatcher _repositoryWatcher;
    private readonly IProjectWorkspaceService _workspaceService;
    private readonly SemaphoreSlim _repositoryContextGate = new(1, 1);
    // The service publishes a new context before each consumer finishes rebinding. Keep the
    // repository-side context separate until this view model has acquired the gate, so an
    // in-flight Git operation can finish against its original repository without observing the
    // service's already-updated Current value.
    private ProjectWorkspaceContext? _workspaceContext;
    // The view model is composed on the WPF UI thread in the application. Capture that owning
    // context instead of consulting Application.Current from a timer continuation: tests and
    // secondary WPF hosts can expose a global Dispatcher that belongs to another, idle thread.
    private readonly SynchronizationContext? _uiContext;

    // Silent auto-refresh bookkeeping (watcher-driven): at most one refresh in flight, at most one
    // merged refresh pending; a pending refresh that hits a busy (user operation) state is deferred
    // until IsBusy goes back to false (see the OnPropertyChanged override). The in-flight flag is an
    // int taken with CAS because the retry timer can re-enter from a thread-pool thread.
    private int _quietRefreshInFlight; // 0 = idle, 1 = a refresh is in flight
    private int _quietRefreshPending;
    // A remote-tracking ref can move without moving local HEAD (for example after
    // `git push --force-with-lease`). Preserve that signal across debounce/busy merging so the
    // next quiet refresh reloads branches, incoming/outgoing commits and the commit graph.
    private int _quietRefreshRequiresFullLoad;
    private bool _enableScmAutoRefresh = true;
    private readonly RevisionGate _settingsRevisionGate = new();

    // Minimum interval between silent (watcher-driven) refreshes, VS Code-style throttle (debounce
    // + single flight + post-completion cooldown): during continuous saves/builds the watcher
    // reports a new 200ms quiet period constantly, and without the interval every period would
    // start a fresh `git status`. Bursts inside the window only record a pending refresh that a
    // single retry timer runs once the window elapses. Manual refreshes and the catch-up refresh
    // after a user operation are exempt (they pass immediate: true).
    // 500ms (was 2000ms): the watcher already debounces 200ms, so a 2s post-completion cooldown made
    // every change landing right after a refresh wait ~2s (and mid-flight changes a second full
    // cooldown) before the changes column updated — the "changes not picked up in time" complaint.
    // 500ms caps git-status frequency at 2/s during bursts while keeping update latency < ~1s.
    internal const long QuietRefreshMinimumIntervalMs = 500;
    private long _lastQuietRefreshCompletedUtcTicks; // Environment.TickCount64 value, monotonic
    private int _quietRefreshRetryScheduled; // at most one pending retry timer

    // HEAD signature of the last full state load: working-tree edits cannot move HEAD, so quiet
    // refreshes stay status-only while a moved HEAD (external commit / branch switch) escalates
    // to a full reload that catches branches and history up.
    private bool _hasFullStateLoad;
    private string? _lastHeadSignature;
    private bool _scmLayoutRestored;

    /// <summary>The shared editor area (diff tabs). Also the main content of this page.</summary>
    public EditorAreaViewModel Editor => _editor;

    /// <summary>Workspace generation that owns the currently displayed Git status. Consumers of
    /// <see cref="StatusRefreshed"/> use this identity to reject a delayed status from a previous
    /// project when two projects share the same repository root.</summary>
    public ProjectWorkspaceContext? WorkspaceContext => _workspaceContext;

    public BulkObservableCollection<GitChangeItem> StagedChanges { get; } = [];
    public BulkObservableCollection<GitChangeItem> UnstagedChanges { get; } = [];
    public BulkObservableCollection<GitCommitInfo> Logs { get; } = [];
    public BulkObservableCollection<GitBranchInfo> Branches { get; } = [];
    public BulkObservableCollection<GitBranchInfo> RemoteBranches { get; } = [];

    public IReadOnlyList<GitBranchInfo> GraphBranchRefs => Branches.Concat(RemoteBranches).ToArray();

    // Multi-selection sets (SelectionMode="Extended" + MultiSelectorBinding). Each list keeps its own
    // selection; the single-selection properties below stay bound to the ListBox SelectedItem so the
    // existing copy / context-menu behaviors keep working unchanged.
    public ObservableCollection<GitChangeItem> SelectedUnstagedChanges { get; } = [];
    public ObservableCollection<GitChangeItem> SelectedStagedChanges { get; } = [];
    public ObservableCollection<GitBranchInfo> SelectedBranches { get; } = [];
    public ObservableCollection<GitLogRow> SelectedCommits { get; } = [];

    /// <summary>Raised when a change or commit file is selected; the page opens it as a diff tab
    /// in the shared editor area.</summary>
    public event EventHandler<GitDiffRequest>? DiffOpenRequested;

    /// <summary>Raised after every status refresh so the explorer page can re-sync tree decorations.</summary>
    public event EventHandler<GitRepositoryStatus>? StatusRefreshed;

    /// <summary>Raised when "在资源管理器中显示" is clicked; the explorer page reveals the file in the tree.</summary>
    public event EventHandler<string>? RevealRequested;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private string repositoryPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    [NotifyCanExecuteChangedFor(nameof(CommitAndPushCommand))]
    [NotifyCanExecuteChangedFor(nameof(CommitAndSyncCommand))]
    [NotifyCanExecuteChangedFor(nameof(PullCommand))]
    [NotifyCanExecuteChangedFor(nameof(PushCommand))]
    [NotifyCanExecuteChangedFor(nameof(InitializeRepositoryCommand))]
    private bool isRepository;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InitializeRepositoryCommand))]
    private bool hasWorkspace;

    [ObservableProperty] private string repositorySummary = "未选择仓库。";

    [ObservableProperty] private GitChangeItem? selectedUnstagedChange;
    [ObservableProperty] private GitChangeItem? selectedStagedChange;
    [ObservableProperty] private GitBranchInfo? selectedBranch;

    // ===== Branch / sync status shown next to the commit box (VS Code SCM) =====
    // Structured twins of the RepositorySummary text so the sidebar can render branch
    // chips and ↑/↓ badges without re-parsing the summary string.
    [ObservableProperty] private string currentBranch = string.Empty;
    [ObservableProperty] private int aheadCount;
    [ObservableProperty] private int behindCount;

    /// <summary>当前分支与上游不同步时,同步按钮高亮提示(有待拉取或待推送的提交)。</summary>
    public bool NeedsSync => AheadCount > 0 || BehindCount > 0;

    // ===== Change-list layout (VS Code "view as tree / flat list") =====
    [ObservableProperty] private ScmLayout scmLayout = ScmLayout.List;

    public bool IsTreeLayout => ScmLayout == ScmLayout.Tree;
    public bool IsListLayout => ScmLayout == ScmLayout.List;

    partial void OnScmLayoutChanged(ScmLayout value)
    {
        OnPropertyChanged(nameof(IsTreeLayout));
        OnPropertyChanged(nameof(IsListLayout));
        if (_scmLayoutRestored)
        {
            _ = PersistScmLayoutAsync(value);
        }
    }

    /// <summary>树状布局:切换回平铺或反之(VS Code 标题栏视图切换按钮)。</summary>
    [RelayCommand]
    private void ToggleScmLayout() => ScmLayout = ScmLayout == ScmLayout.Tree ? ScmLayout.List : ScmLayout.Tree;

    // ===== Tree rows (folder grouping of the change lists) =====

    /// <summary>树状布局下未暂存更改的扁平行序列(文件夹行 + 缩进文件行)。</summary>
    public BulkObservableCollection<ScmRowNode> UnstagedTreeRows { get; } = [];

    /// <summary>树状布局下已暂存更改的扁平行序列。</summary>
    public BulkObservableCollection<ScmRowNode> StagedTreeRows { get; } = [];

    /// <summary>树状布局下未暂存列表的单选(点击文件行打开 diff;文件夹行选中无效果)。</summary>
    [ObservableProperty] private ScmRowNode? selectedUnstagedTreeRow;

    /// <summary>树状布局下已暂存列表的单选。</summary>
    [ObservableProperty] private ScmRowNode? selectedStagedTreeRow;

    /// <summary>树状布局下未暂存列表的多选(批量命令与平铺布局的多选合并生效)。</summary>
    public ObservableCollection<ScmRowNode> SelectedUnstagedTreeRows { get; } = [];

    /// <summary>树状布局下已暂存列表的多选。</summary>
    public ObservableCollection<ScmRowNode> SelectedStagedTreeRows { get; } = [];

    /// <summary>用户折叠的目录(会话级,按完整目录路径)。</summary>
    private readonly HashSet<string> _collapsedFolders = new(StringComparer.OrdinalIgnoreCase);

    partial void OnSelectedUnstagedTreeRowChanged(ScmRowNode? value)
    {
        if (value is ScmFileNode { Change: { } change })
        {
            // 单击开预览 diff(VS Code SCM 语义;双击改开文件,见 OpenChangeFilePermanent)。
            RaiseDiffOpenRequested(change, isPreview: true);
        }
    }

    partial void OnSelectedStagedTreeRowChanged(ScmRowNode? value)
    {
        if (value is ScmFileNode { Change: { } change })
        {
            RaiseDiffOpenRequested(change, isPreview: true);
        }
    }

    /// <summary>折叠 / 展开一个树目录(VS Code 树状视图)。</summary>
    [RelayCommand]
    private void ToggleFolder(ScmFolderNode? folder)
    {
        if (folder is null)
        {
            return;
        }

        if (!_collapsedFolders.Remove(folder.FolderPath))
        {
            _collapsedFolders.Add(folder.FolderPath);
        }

        RebuildTreeRows();
    }

    /// <summary>当前布局下共同选中的未暂存更改。树状视图选中文件夹时展开为该目录下的全部文件。</summary>
    private IEnumerable<GitChangeItem> UnstagedSelection =>
        UniquifyChanges(SelectedUnstagedChanges.Concat(
            ExpandTreeSelection(SelectedUnstagedTreeRows, UnstagedChanges)));

    /// <summary>当前布局下共同选中的已暂存更改。树状视图选中文件夹时展开为该目录下的全部文件。</summary>
    private IEnumerable<GitChangeItem> StagedSelection =>
        UniquifyChanges(SelectedStagedChanges.Concat(
            ExpandTreeSelection(SelectedStagedTreeRows, StagedChanges)));

    private static IEnumerable<GitChangeItem> ExpandTreeSelection(
        IEnumerable<ScmRowNode> rows, IEnumerable<GitChangeItem> changes)
    {
        var available = changes.ToArray();
        foreach (var row in rows)
        {
            switch (row)
            {
                case ScmFileNode file:
                    yield return file.Change;
                    break;
                case ScmFolderNode folder:
                    foreach (var change in ChangesUnderFolder(available, folder))
                    {
                        yield return change;
                    }
                    break;
            }
        }
    }

    private static IReadOnlyList<GitChangeItem> ChangesUnderFolder(
        IEnumerable<GitChangeItem> changes, ScmFolderNode? folder)
    {
        if (folder is null)
        {
            return [];
        }

        var prefix = folder.FolderPath.Trim('/').Replace('\\', '/') + "/";
        return changes.Where(change =>
        {
            var path = change.Path.Replace('\\', '/');
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }).ToArray();
    }

    private static IEnumerable<GitChangeItem> UniquifyChanges(IEnumerable<GitChangeItem> changes) =>
        changes.GroupBy(change => change.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
    private void RebuildTreeRows()
    {
        FillTreeRows(UnstagedTreeRows, BuildTreeRows(UnstagedChanges, _collapsedFolders));
        FillTreeRows(StagedTreeRows, BuildTreeRows(StagedChanges, _collapsedFolders));
        PruneSelection(SelectedUnstagedTreeRows, UnstagedTreeRows);
        PruneSelection(SelectedStagedTreeRows, StagedTreeRows);
    }

    private static void FillTreeRows(BulkObservableCollection<ScmRowNode> target, IReadOnlyList<ScmRowNode> rows) =>
        target.ReplaceRange(rows);

    /// <summary>把更改按目录路径组织成扁平树行序列:文件夹行 + 缩进的文件行,同级条目按名称
    /// 混排(VS Code 树状视图)。collapsedFolders 记录折叠的目录。</summary>
    public static IReadOnlyList<ScmRowNode> BuildTreeRows(
        IEnumerable<GitChangeItem> changes,
        IReadOnlySet<string>? collapsedFolders = null)
    {
        var root = new ScmTreeFolder();
        foreach (var change in changes)
        {
            var node = root;
            var segments = change.Path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length - 1; i++)
            {
                node = node.GetOrAddFolder(segments[i]);
            }

            node.Files.Add(change);
        }

        var rows = new List<ScmRowNode>();
        FlattenTree(root, string.Empty, 0, collapsedFolders ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase), rows);
        foreach (var row in rows)
        {
            // 竖线 1px 宽,画在列中心 12k + 9:取左缘 12k + 8.5,与文件夹自己列内(折叠图标列,
            // col0 宽 18,居中线中心 12d + 9)完全对齐;子行把这些线重画到自身整行高度,
            // 上层折叠图标列的竖线便从第一个子行起连续向下,文件夹行自身不画该列竖线
            row.AncestorGuideLefts = Enumerable.Range(0, row.Depth).Select(k => 12.0 * k + 8.5).ToArray();
        }
        return rows;
    }

    private static void FlattenTree(
        ScmTreeFolder folder,
        string prefix,
        int depth,
        IReadOnlySet<string> collapsed,
        List<ScmRowNode> rows)
    {
        var entries = folder.Folders.Select(pair => (Name: pair.Key, IsFolder: true))
            .Concat(folder.Files.Select(file => (Name: file.FileName, IsFolder: false)))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.IsFolder ? 0 : 1);
        foreach (var entry in entries)
        {
            if (entry.IsFolder)
            {
                var child = folder.Folders[entry.Name];
                var fullPath = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
                var isCollapsed = collapsed.Contains(fullPath);
                rows.Add(new ScmFolderNode(entry.Name, fullPath, isCollapsed, depth));
                if (!isCollapsed)
                {
                    FlattenTree(child, fullPath, depth + 1, collapsed, rows);
                }
            }
            else
            {
                rows.Add(new ScmFileNode(folder.Files.First(file => file.FileName == entry.Name), depth));
            }
        }
    }

    private sealed class ScmTreeFolder
    {
        public Dictionary<string, ScmTreeFolder> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<GitChangeItem> Files { get; } = [];

        public ScmTreeFolder GetOrAddFolder(string name)
        {
            if (!Folders.TryGetValue(name, out var child))
            {
                child = new ScmTreeFolder();
                Folders[name] = child;
            }

            return child;
        }
    }

    // ===== 提交图形泳道分配(纯函数,可单元测试) =====

    /// <summary>为提交序列分配泳道并产出每行的线段模型,保证"一条分支一条线":
    /// <list type="bullet">
    /// <item>提交命中既有泳道则在其上画圆点(线从上方延续);未命中则新开泳道(分支起点)。</item>
    /// <item>每个泳道带主/侧角色:首父落位的泳道为主线(primary),非首父开辟的泳道为侧线(side)。</item>
    /// <item>首父延续圆点泳道;其余父提交各开侧泳道(分叉),已在其它泳道的父提交由曲线汇入。</item>
    /// <item>首父被侧线"占位"时(如二父链先显示、首父链后处理),由主线泳道把首父接回
    ///     (<see cref="GitGraphRow.MergeFromLanes"/> 记录原侧线在本行汇入主线),合并基始终
    ///     留在首父链上 —— 否则一条分支会画成多根线。</item>
    /// </list>
    /// 结果与 <c>git log --topo-order --graph</c> 的规范画法一致。</summary>
    public static IReadOnlyList<GitGraphRow> BuildCommitGraph(IReadOnlyList<GitCommitInfo> commits) =>
        BuildCommitGraph(commits, null);

    public static IReadOnlyList<GitGraphRow> BuildCommitGraph(IReadOnlyList<GitCommitInfo> commits, IReadOnlyList<GitBranchInfo>? branches)
    {
        // Branch -> color key.
        Dictionary<string, string>? tipToKey = null;
        if (branches is { Count: > 0 })
        {
            var branchColor = new Dictionary<GitBranchInfo, string>();
            foreach (var b in branches)
            {
                if (b.IsCurrent) branchColor[b] = "GraphCurrentBranchBrush";
                else if (b.IsRemote) branchColor[b] = $"GraphRemote{(StableHash(b.Name) % 6) + 1}Brush";
                else branchColor[b] = $"GraphLane{(StableHash(b.Name) % 6) + 1}Brush";
            }

            // tip -> best branch (Current > Local > Remote)
            var ordered = branches.OrderByDescending(b => b.IsCurrent ? 2 : b.IsRemote ? 0 : 1).ToArray();
            tipToKey = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var b in ordered)
            {
                if (string.IsNullOrWhiteSpace(b.TipHash)) continue;
                if (!tipToKey.ContainsKey(b.TipHash!))
                    tipToKey[b.TipHash!] = branchColor[b];
            }
        }

        string FallbackKey(int lane) => $"GraphLane{(Math.Abs(lane) % 6) + 1}Brush";

        var working = new List<GitGraphRow>(commits.Count);
        var lanes = new List<LaneEntry>();
        var laneColorKeys = new List<string?>();
        var maxLanes = 1;
        // Temporary store per-row raw color arrays before padding.
        var rawKeysPerRow = new List<string[]?>();
        foreach (var commit in commits)
        {
            var dotLane = IndexOfLane(lanes, commit.Hash);
            var continuesFromAbove = dotLane >= 0;
            if (!continuesFromAbove)
            {
                dotLane = AllocateLane(lanes);
                // ensure laneColorKeys size
                while (laneColorKeys.Count <= dotLane) laneColorKeys.Add(null);
                while (lanes.Count > laneColorKeys.Count) laneColorKeys.Add(null);
                if (tipToKey is not null && tipToKey.TryGetValue(commit.Hash, out var key))
                    laneColorKeys[dotLane] = key;
                else if (tipToKey is not null)
                    laneColorKeys[dotLane] = FallbackKey(dotLane);
                lanes[dotLane] = new LaneEntry(commit.Hash, false);
            }
            else if (tipToKey is not null && tipToKey.TryGetValue(commit.Hash, out var segmentKey))
            {
                // 泳道分段着色:延续中的泳道命中更深层分支的 tip 时,自本行(含圆点)起切换为该
                // 分支色键。提交自上而下单调处理,后命中的 tip 必然更深,无需额外的层级比较;
                // 线性历史下 main 领先 origin/main 的仓库由此在 tip 行分界,以上为当前分支色、
                // 以下为远端色,本地/远端段一眼可辨。
                while (laneColorKeys.Count <= dotLane) laneColorKeys.Add(null);
                laneColorKeys[dotLane] = segmentKey;
            }

            var passLanes = new List<int>();
            for (var i = 0; i < lanes.Count; i++)
            {
                if (i != dotLane && lanes[i].Hash is not null)
                {
                    passLanes.Add(i);
                }
            }

            lanes[dotLane] = lanes[dotLane] with { Hash = null };
            var dotLaneFresh = !continuesFromAbove;
            var links = new List<GitGraphLink>();
            var mergeFromLanes = new List<int>();
            for (var parentIndex = 0; parentIndex < commit.ParentList.Count; parentIndex++)
            {
                var parent = commit.ParentList[parentIndex];
                var lane = IndexOfLane(lanes, parent);
                if (lane >= 0 && lane != dotLane)
                {
                    var isFirstParent = parentIndex == 0;
                    if (isFirstParent && lanes[dotLane].IsPrimary && !lanes[lane].IsPrimary)
                    {
                        lanes[lane] = lanes[lane] with { Hash = null };
                        // keep dotLane color, clear side lane color
                        laneColorKeys[lane] = null;
                        lanes[dotLane] = new LaneEntry(parent, true);
                        passLanes.Remove(lane);
                        mergeFromLanes.Add(lane);
                        links.Add(new GitGraphLink(dotLane, dotLane));
                    }
                    else
                    {
                        links.Add(new GitGraphLink(dotLane, lane));
                    }
                }
                else if (lane < 0)
                {
                    if (parentIndex == 0)
                    {
                        lane = dotLane;
                        lanes[dotLane] = new LaneEntry(parent, dotLaneFresh || lanes[dotLane].IsPrimary);
                        links.Add(new GitGraphLink(dotLane, dotLane));
                    }
                    else
                    {
                        lane = AllocateLane(lanes);
                        while (laneColorKeys.Count <= lane) laneColorKeys.Add(null);
                        while (lanes.Count > laneColorKeys.Count) laneColorKeys.Add(null);
                        if (tipToKey is not null && tipToKey.TryGetValue(parent, out var pkey))
                            laneColorKeys[lane] = pkey;
                        else if (tipToKey is not null)
                            laneColorKeys[lane] = FallbackKey(lane);
                        lanes[lane] = new LaneEntry(parent, false);
                        links.Add(new GitGraphLink(dotLane, lane));
                    }
                }
                else
                {
                    links.Add(new GitGraphLink(dotLane, dotLane));
                }
            }

            maxLanes = Math.Max(maxLanes, lanes.Count);
            // snapshot current lane colors for this row
            string[]? snapshot = null;
            if (tipToKey is not null)
            {
                snapshot = new string[lanes.Count];
                for (var i = 0; i < lanes.Count; i++)
                {
                    snapshot[i] = laneColorKeys[i] ?? FallbackKey(i);
                }
            }
            rawKeysPerRow.Add(snapshot!);
            working.Add(new GitGraphRow(dotLane, passLanes, links, maxLanes, continuesFromAbove, mergeFromLanes, snapshot));
        }

        // Pad LaneColorKeys to maxLanes and fix LaneCount; each row's incoming keys are the
        // previous row's final keys (first row: its own) so the color switch lands on the dot.
        var result = new List<GitGraphRow>(working.Count);
        string[]? prevFinal = null;
        for (var i = 0; i < working.Count; i++)
        {
            var row = working[i];
            IReadOnlyList<string>? finalKeys = null;
            if (row.LaneColorKeys is not null)
            {
                var arr = new string[maxLanes];
                var src = rawKeysPerRow[i]!;
                for (var k = 0; k < maxLanes; k++)
                {
                    if (k < src.Length && src[k] is not null) arr[k] = src[k]!;
                    else arr[k] = FallbackKey(k);
                }
                finalKeys = arr;
            }

            var incomingKeys = finalKeys is null ? null : prevFinal ?? finalKeys;
            prevFinal = finalKeys as string[];
            result.Add(row with { LaneCount = maxLanes, LaneColorKeys = finalKeys, LaneIncomingColorKeys = incomingKeys });
        }
        return result;
    }

    internal static uint StableHash(string text)
    {
        unchecked
        {
            uint hash = 2166136261u;
            foreach (var c in text)
            {
                hash ^= (uint)c;
                hash *= 16777619u;
            }
            return hash;
        }
    }

    /// <summary>泳道条目:占位的父哈希 + 是否主线(首父链)。角色只在泳道持有哈希时起作用,
    /// 哈希被认领后空位保留角色供后续判断(如"原侧线本行汇入")。</summary>
    private sealed record LaneEntry(string? Hash, bool IsPrimary)
    {
        public static LaneEntry Free(bool isPrimary) => new(null, isPrimary);
    }

    private static int IndexOfLane(List<LaneEntry> lanes, string hash)
    {
        for (var i = 0; i < lanes.Count; i++)
        {
            if (string.Equals(lanes[i].Hash, hash, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static int AllocateLane(List<LaneEntry> lanes)
    {
        var free = lanes.FindIndex(lane => lane.Hash is null);
        if (free >= 0)
        {
            return free;
        }

        lanes.Add(LaneEntry.Free(false));
        return lanes.Count - 1;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateBranchCommand))]
    private string newBranchName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    [NotifyCanExecuteChangedFor(nameof(CommitAndPushCommand))]
    [NotifyCanExecuteChangedFor(nameof(CommitAndSyncCommand))]
    private string commitMessage = string.Empty;

    /// <summary>提交消息为空(驱动输入框水印占位符)。</summary>
    public bool HasCommitMessage => !string.IsNullOrWhiteSpace(CommitMessage);

    partial void OnCommitMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasCommitMessage));
        OnPropertyChanged(nameof(CanCommit));
    }

    // Session-only section expansion (VS Code): the initial expansion mirrors the state of the repo
    // (unstaged ⇔ has changes, staged ⇔ has staged changes, branches collapsed, log expanded) unless
    // the user has toggled the section this session. 提交行自身是否展开属于 GitLogRow 行状态。
    [ObservableProperty] private bool isUnstagedSectionExpanded;
    [ObservableProperty] private bool isStagedSectionExpanded;
    [ObservableProperty] private bool isBranchesSectionExpanded;
    [ObservableProperty] private bool isLogSectionExpanded = true;

    // ===== 视图面板折叠状态:更改(工作区状态) / 图表(提交历史与分支),两视图独立折叠 =====

    [ObservableProperty] private bool isChangesViewExpanded = true;
    [ObservableProperty] private bool isGraphViewExpanded = true;

    /// <summary>更改视图徽标:已暂存 + 未暂存更改总数。</summary>
    public int ChangesViewBadgeCount => StagedChanges.Count + UnstagedChanges.Count;

    /// <summary>图表视图徽标:最近提交条数。</summary>
    public int GraphViewBadgeCount => Logs.Count;

    private bool _unstagedUserToggled;
    private bool _stagedUserToggled;
    private bool _graphCollapsedForNoRepository;

    /// <summary>未暂存更改的默认视图(绑定列表)。多选与按路径恢复选择等机制不受影响。</summary>
    public ICollectionView UnstagedChangesView { get; }

    /// <summary>已暂存更改的默认视图,同 <see cref="UnstagedChangesView"/>。</summary>
    public ICollectionView StagedChangesView { get; }

    partial void OnAheadCountChanged(int value) => OnPropertyChanged(nameof(NeedsSync));

    partial void OnBehindCountChanged(int value) => OnPropertyChanged(nameof(NeedsSync));

    public GitViewModel(
        IGitService gitService,
        IFolderPickerService folderPicker,
        IConfirmationService confirmationService,
        IProjectCatalogService projectCatalogService,
        EditorAreaViewModel editor,
        IUiLogService logService,
        IClipboardService clipboard,
        IGitRepositoryWatcher repositoryWatcher,
        IProjectWorkspaceService workspaceService,
        IApplicationStateStore stateStore,
        ISettingsService settingsService) : base("源代码管理", logService)
    {
        _gitService = gitService;
        _folderPicker = folderPicker;
        _confirmationService = confirmationService;
        _projectCatalogService = projectCatalogService;
        _editor = editor;
        _clipboard = clipboard;
        _repositoryWatcher = repositoryWatcher;
        _workspaceService = workspaceService;
        _workspaceContext = workspaceService.Current;
        _uiContext = SynchronizationContext.Current;
        _stateStore = stateStore;
        _scopedSettings = settingsService;
        _repositoryWatcher.ChangesDetected += (_, changes) => HandleWatcherChanges(changes);
        _workspaceService.ContextChanged += ApplyWorkspaceContextAsync;
        DiffOpenRequested += (_, request) => _ = _editor.OpenDiffAsync(request);
        _editor.DiffMutationCompleted += OnEditorDiffMutationCompleted;
        UnstagedChangesView = CollectionViewSource.GetDefaultView(UnstagedChanges);
        StagedChangesView = CollectionViewSource.GetDefaultView(StagedChanges);
        HookSelectionChanges(SelectedUnstagedChanges);
        HookSelectionChanges(SelectedStagedChanges);
        HookSelectionChanges(SelectedUnstagedTreeRows);
        HookSelectionChanges(SelectedStagedTreeRows);
        HookSelectionChanges(SelectedBranches);
        HookSelectionChanges(SelectedCommits);
    }

    private void OnEditorDiffMutationCompleted(object? sender, GitDiffRequest request)
    {
        if (string.Equals(
                Path.GetFullPath(request.RepositoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(RepositoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            // Reuse the existing coalesced watcher refresh path so a Git operation and the
            // corresponding .git/index event never race two status loads. The operation just
            // completed, so the catch-up refresh bypasses the silent-refresh minimum interval.
            StartQuietRefresh(immediate: true);
        }
    }

    /// <summary>Persisted SCM changes/graph pane pixel heights (the splitter split when both view
    /// containers are expanded). Written by the GitView splitter, read back on view load.</summary>
    public double? RestoredScmChangesHeight { get; private set; }

    public double? RestoredScmGraphHeight { get; private set; }

    public async Task RestoreScmPaneHeightsAsync()
    {
        var state = await _stateStore!.LoadAsync();
        if (state.ScmChangesHeight is { } changesHeight)
        {
            RestoredScmChangesHeight = changesHeight;
        }

        if (state.ScmGraphHeight is { } graphHeight)
        {
            RestoredScmGraphHeight = graphHeight;
        }
    }

    private async Task RestoreScmLayoutAsync()
    {
        try
        {
            var state = await _stateStore!.LoadAsync();
            if (Enum.TryParse<ScmLayout>(state.ScmLayout, ignoreCase: true, out var layout))
            {
                ScmLayout = layout;
            }
        }
        catch (Exception)
        {
            // Layout preference is best effort; Git activation must still load repository data.
        }
        finally
        {
            _scmLayoutRestored = true;
        }
    }

    private async Task PersistScmLayoutAsync(ScmLayout value)
    {
        try
        {
            await _stateStore!.CommitAsync(new([
                new(ApplicationStateField.ScmLayout, value.ToString()),
            ]));
        }
        catch (Exception)
        {
            // Layout preference is best effort and must never block Git refreshes.
        }
    }

    /// <summary>Persists the splitter-driven SCM pane split in pixels — best effort, never blocks UI.</summary>
    public void RecordScmPaneHeights(double changesPixels, double graphPixels)
    {
        if (changesPixels <= 0 || graphPixels <= 0)
        {
            return;
        }

        _ = PersistScmPaneHeightsAsync(changesPixels, graphPixels);
    }

    private async Task PersistScmPaneHeightsAsync(double changesPixels, double graphPixels)
    {
        try
        {
            await _stateStore!.CommitAsync(new([
                new(ApplicationStateField.ScmChangesHeight, changesPixels),
                new(ApplicationStateField.ScmGraphHeight, graphPixels),
            ]));
        }
        catch (Exception)
        {
            // Persisting splitter state is best effort.
        }
    }

    /// <summary>The secondary left sidebar shows the git status view (staged/unstaged/commit).</summary>
    public override object? Sidebar => this;

    public bool CanCommit => IsRepository && StagedChanges.Count > 0 && !string.IsNullOrWhiteSpace(CommitMessage);
    public bool IsClean => IsRepository && StagedChanges.Count == 0 && UnstagedChanges.Count == 0;
    public string StagedCountLabel => $"暂存更改 ({StagedChanges.Count})";
    public string UnstagedCountLabel => $"更改 ({UnstagedChanges.Count})";

    /// <summary>Total working-tree changes (staged + unstaged), surfaced as an activity-bar badge.</summary>
    public int ChangeCount => StagedChanges.Count + UnstagedChanges.Count;

    protected override async Task OnFirstActivatedAsync()
    {
        await RestoreScmLayoutAsync();
        await BindScopedSettingsAsync();
        await _workspaceService.EnsureInitializedAsync();
        // Another workbench page can initialize the workspace before SCM is first activated. In
        // that case no new ContextChanged event is raised here, so bind the existing context once.
        if (!HasWorkspace && _workspaceService.Current is { } context)
        {
            await ApplyWorkspaceContextAsync(context);
        }
    }

    public void ApplyNavigationContext(NavigationContext? context)
    {
        switch (context)
        {
            case NavigationContext.Open: _ = OpenRepositoryAsync(); break;
            case NavigationContext.Refresh: _ = RefreshAsync(); break;
        }
    }

    /// <summary>Called by the project workbench when its root changes, keeping the explorer and
    /// source-control panes bound to the same repository.</summary>
    public async Task SetRepositoryAsync(string path)
    {
        await _workspaceService.ActivateAsync(path);
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            var normalizedLeft = Path.GetFullPath(left)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedRight = Path.GetFullPath(right)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private bool IsRepositoryContextCurrent(ProjectWorkspaceContext? context, string? path)
    {
        // Isolated Git view tests/embedders can provide a repository path without a workspace
        // service context. Preserve that mode while making the production path identity-based.
        if (_workspaceService.Current is null && _workspaceContext is null)
        {
            return !string.IsNullOrWhiteSpace(path) && PathsEqual(RepositoryPath, path);
        }

        return context is not null
            && ReferenceEquals(_workspaceContext, context)
            && PathsEqual(RepositoryPath, path)
            && PathsEqual(context.GitRepositoryPath, path);
    }

    private bool IsServiceContextCurrent()
    {
        if (_workspaceService.Current is null && _workspaceContext is null) return true;
        return _workspaceContext is not null
            && ReferenceEquals(_workspaceService.Current, _workspaceContext);
    }

    private bool IsSettingsContextCurrent(SettingsContext context)
    {
        var workspace = _workspaceContext?.ProjectPath;
        return string.Equals(context.NormalizedWorkspacePath,
            workspace is null ? null : Path.GetFullPath(workspace)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> RunRepositoryAsync(
        string operation,
        Func<CancellationToken, Task> action,
        string recommendedNextStep = "",
        bool canCancel = true)
    {
        await _repositoryContextGate.WaitAsync();
        try
        {
            var context = _workspaceContext;
            var path = RepositoryPath;
            if (!IsRepositoryContextCurrent(context, path)) return false;

            return await RunAsync(operation, async cancellationToken =>
            {
                if (!IsRepositoryContextCurrent(context, path))
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                await action(cancellationToken);
                if (!IsRepositoryContextCurrent(context, path))
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }, recommendedNextStep, canCancel);
        }
        finally
        {
            _repositoryContextGate.Release();
        }
    }

    [RelayCommand]
    private async Task OpenRepositoryAsync()
    {
        var selected = _folderPicker.PickFolder(RepositoryPath);
        if (selected is null)
        {
            return;
        }

        await _workspaceService.ActivateAsync(selected);
    }

    [RelayCommand]
    private Task RefreshAsync() => RunRepositoryAsync("刷新 Git 状态", LoadStateAsync, "确认目录是 Git 仓库，或重新选择仓库。", canCancel: true);

    /// <summary>Creates a local repository for the currently open project only. The subsequent
    /// workspace reactivation re-discovers the Git root and notifies every workspace consumer.</summary>
    [RelayCommand(CanExecute = nameof(CanInitializeRepository))]
    private async Task InitializeRepositoryAsync()
    {
        var context = _workspaceContext ?? _workspaceService.Current;
        var projectPath = context?.ProjectPath;
        if (context is null || string.IsNullOrWhiteSpace(projectPath))
        {
            return;
        }

        var initialized = false;
        await _repositoryContextGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(_workspaceService.Current, context)
                || !ReferenceEquals(_workspaceContext, context)) return;

            initialized = await RunAsync("初始化 Git 仓库", async cancellationToken =>
            {
                await _gitService.InitializeRepositoryAsync(projectPath, cancellationToken);
            }, "请确认已安装 Git，且当前项目目录可写。", canCancel: true);
        }
        finally
        {
            _repositoryContextGate.Release();
        }

        // Re-discover the Git root only after the repository operation has released the context
        // gate. If the user requested another project meanwhile, its pending context event wins;
        // never reactivate the old project after that switch.
        if (initialized && ReferenceEquals(_workspaceService.Current, context)
            && ReferenceEquals(_workspaceContext, context))
        {
            await _workspaceService.ActivateAsync(projectPath);
        }
    }

    /// <summary>Publishes the only state a stage/unstage/discard operation can change: the index
    /// and working-tree status. Branches, refs, history and stashes deliberately stay cached until
    /// an operation that can affect them (commit, checkout, fetch, stash, explicit refresh).
    /// The watcher lease covers this process's own index/worktree notifications until this status
    /// snapshot is applied, preventing a redundant watcher-driven catch-up refresh.</summary>
    private async Task RefreshStatusAfterMutationAsync(
        Func<CancellationToken, Task> mutation,
        IReadOnlyCollection<string>? affectedWorkingTreePaths,
        CancellationToken cancellationToken)
    {
        using var suppression = _repositoryWatcher.BeginOperationSuppression(affectedWorkingTreePaths);
        await mutation(cancellationToken);
        await LoadStateLiteAsync(cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanStageAll))]
    private async Task StageAllAsync()
    {
        await RunRepositoryAsync("全部暂存", async cancellationToken =>
        {
            await RefreshStatusAfterMutationAsync(
                ct => _gitService.StageAsync(RepositoryPath, [], ct), [], cancellationToken);
        }, canCancel: true);
        RefreshChangeCommands();
    }

    [RelayCommand(CanExecute = nameof(CanUnstageAll))]
    private async Task UnstageAllAsync()
    {
        await RunRepositoryAsync("取消全部暂存", async cancellationToken =>
        {
            await RefreshStatusAfterMutationAsync(
                ct => _gitService.UnstageAsync(RepositoryPath, [], ct), [], cancellationToken);
        }, canCancel: true);
        RefreshChangeCommands();
    }

    [RelayCommand(CanExecute = nameof(CanDiscardAll))]
    private async Task DiscardAllAsync()
    {
        // 丢弃只针对未暂存内容;已暂存更改需先"取消暂存"再丢弃,不能直接抹掉。
        if (!_confirmationService.Confirm("确认丢弃全部更改", "将丢弃所有未暂存的更改（含未跟踪文件），且无法撤销；已暂存的更改保持不变。选择“否”可安全取消。"))
        {
            LogService.Write("INFO", "用户取消了丢弃全部更改。");
            return;
        }

        await RunRepositoryAsync("丢弃全部更改", async cancellationToken =>
        {
            await RefreshStatusAfterMutationAsync(
                ct => _gitService.DiscardUnstagedAsync(RepositoryPath, [], ct), null, cancellationToken);
        }, canCancel: true);
        RefreshChangeCommands();
    }

    [RelayCommand(CanExecute = nameof(CanStageChange))]
    private async Task StageChangeAsync(GitChangeItem? item)
    {
        if (item is null)
        {
            return;
        }

        await RunRepositoryAsync($"暂存 {item.Path}", async cancellationToken =>
        {
            await RefreshStatusAfterMutationAsync(
                ct => _gitService.StageAsync(RepositoryPath, [item.Path], ct), [], cancellationToken);
        }, canCancel: true);
        RefreshChangeCommands();
    }

    [RelayCommand(CanExecute = nameof(CanUnstageChange))]
    private async Task UnstageChangeAsync(GitChangeItem? item)
    {
        if (item is null)
        {
            return;
        }

        await RunRepositoryAsync($"取消暂存 {item.Path}", async cancellationToken =>
        {
            await RefreshStatusAfterMutationAsync(
                ct => _gitService.UnstageAsync(RepositoryPath, [item.Path], ct), [], cancellationToken);
        }, canCancel: true);
        RefreshChangeCommands();
    }

    [RelayCommand(CanExecute = nameof(CanDiscardChange))]
    private async Task DiscardChangeAsync(GitChangeItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (!_confirmationService.Confirm("确认丢弃更改", $"将丢弃“{item.DisplayPath}”的更改并恢复到上次提交，且无法撤销。选择“否”可安全取消。"))
        {
            LogService.Write("INFO", "用户取消了丢弃更改。");
            return;
        }

        await RunRepositoryAsync($"丢弃 {item.DisplayPath}", async cancellationToken =>
        {
            await RefreshStatusAfterMutationAsync(
                ct => _gitService.DiscardUnstagedAsync(RepositoryPath, [item.Path], ct), [item.Path], cancellationToken);
        }, canCancel: true);
        RefreshChangeCommands();
    }

    [RelayCommand(CanExecute = nameof(CanStageFolder))]
    private async Task StageFolderAsync(ScmRowNode? node)
    {
        // The tree context menu is hosted by the ListBox, so a file row can briefly remain
        // its PlacementTarget.SelectedItem while the menu is being rebuilt. Accept the common
        // tree-row base type and reject files here instead of letting the generated command
        // throw while coercing ScmFileNode to ScmFolderNode.
        if (node is not ScmFolderNode folder)
        {
            return;
        }

        var paths = ChangesUnderFolder(UnstagedChanges, folder)
            .Select(change => change.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        await RunRepositoryAsync($"暂存文件夹 {folder.Name}（{paths.Length} 项）", async cancellationToken =>
        {
            await RefreshStatusAfterMutationAsync(
                ct => _gitService.StageAsync(RepositoryPath, paths, ct), [], cancellationToken);
        }, canCancel: true);
        RefreshChangeCommands();
    }

    private bool CanStageFolder(ScmRowNode? node) =>
        node is ScmFolderNode folder
        && IsRepository
        && ChangesUnderFolder(UnstagedChanges, folder).Count > 0;

    [RelayCommand(CanExecute = nameof(CanUnstageFolder))]
    private async Task UnstageFolderAsync(ScmRowNode? node)
    {
        if (node is not ScmFolderNode folder)
        {
            return;
        }

        var paths = ChangesUnderFolder(StagedChanges, folder)
            .Select(change => change.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        await RunRepositoryAsync($"取消暂存文件夹 {folder.Name}（{paths.Length} 项）", async cancellationToken =>
        {
            await RefreshStatusAfterMutationAsync(
                ct => _gitService.UnstageAsync(RepositoryPath, paths, ct), [], cancellationToken);
        }, canCancel: true);
        RefreshChangeCommands();
    }

    private bool CanUnstageFolder(ScmRowNode? node) =>
        node is ScmFolderNode folder
        && IsRepository
        && ChangesUnderFolder(StagedChanges, folder).Count > 0;

    [RelayCommand(CanExecute = nameof(CanDiscardFolder))]
    private async Task DiscardFolderAsync(ScmRowNode? node)
    {
        if (node is not ScmFolderNode folder)
        {
            return;
        }

        var paths = ChangesUnderFolder(UnstagedChanges, folder)
            .Select(change => change.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        if (!_confirmationService.Confirm(
            "确认丢弃文件夹更改",
            $"将丢弃文件夹“{folder.FolderPath}”下的 {paths.Length} 项未暂存更改，且无法撤销。选择“否”可安全取消。"))
        {
            LogService.Write("INFO", $"用户取消了丢弃文件夹更改：{folder.FolderPath}。");
            return;
        }

        await RunRepositoryAsync($"丢弃文件夹 {folder.Name}（{paths.Length} 项）", async cancellationToken =>
        {
            await RefreshStatusAfterMutationAsync(
                ct => _gitService.DiscardUnstagedAsync(RepositoryPath, paths, ct), paths, cancellationToken);
        }, canCancel: true);
        RefreshChangeCommands();
    }

    private bool CanDiscardFolder(ScmRowNode? node) =>
        node is ScmFolderNode folder
        && IsRepository
        && ChangesUnderFolder(UnstagedChanges, folder).Count > 0;

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync()
    {
        var message = CommitMessage.Trim();
        await RunRepositoryAsync("提交更改", async cancellationToken =>
        {
            await _gitService.CommitAsync(RepositoryPath, message, cancellationToken);
            CommitMessage = string.Empty;
            RecordCommitMessage(message);
            await LoadStateAsync(cancellationToken);
        }, "查看 Problems 中的 git 输出后重试。", canCancel: true);
        RefreshChangeCommands();
    }

    /// <summary>提交并推送(提交按钮拆分菜单,VS Code):提交当前暂存内容后直接推送当前分支,
    /// 不要求工作区干净(与严格版 <see cref="SyncAsync"/> 不同)。</summary>
    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAndPushAsync()
    {
        var message = CommitMessage.Trim();
        await RunRepositoryAsync("提交并推送", async cancellationToken =>
        {
            await _gitService.CommitAsync(RepositoryPath, message, cancellationToken);
            CommitMessage = string.Empty;
            RecordCommitMessage(message);
            await _gitService.PushAsync(RepositoryPath, cancellationToken);
            await LoadStateAsync(cancellationToken);
        }, "查看 Problems 中的 git 输出后重试。", canCancel: true);
        RefreshChangeCommands();
    }

    /// <summary>提交并同步(提交按钮拆分菜单,VS Code):提交后先快进拉取再推送;剩余的未暂存
    /// 更改不影响同步(与要求干净工作区的 <see cref="SyncAsync"/> 不同)。</summary>
    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAndSyncAsync()
    {
        var message = CommitMessage.Trim();
        await RunRepositoryAsync("提交并同步", async cancellationToken =>
        {
            await _gitService.CommitAsync(RepositoryPath, message, cancellationToken);
            CommitMessage = string.Empty;
            RecordCommitMessage(message);
            var pull = await _gitService.PullAsync(RepositoryPath, GitPullOptions.SafeDefault, cancellationToken);
            if (!pull.Success)
            {
                throw new InvalidOperationException(pull.Message ?? "拉取未能安全完成，未执行推送。");
            }

            await _gitService.PushAsync(RepositoryPath, cancellationToken);
            await LoadStateAsync(cancellationToken);
        }, "请处理非快进更新或冲突后再重试。", canCancel: true);
        RefreshChangeCommands();
    }

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task FetchAsync()
    {
        await RunRepositoryAsync("获取远端更新", async cancellationToken =>
        {
            await _gitService.FetchAsync(RepositoryPath, cancellationToken);
            await LoadStateAsync(cancellationToken);
        }, "查看 Problems 中的 git 输出后重试。", canCancel: true);
    }

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task PullAsync()
    {
        await RunRepositoryAsync("拉取", async cancellationToken =>
        {
            await _gitService.PullAsync(RepositoryPath, cancellationToken);
            await LoadStateAsync(cancellationToken);
        }, "查看 Problems 中的 git 输出后重试。", canCancel: true);
    }

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task PushAsync()
    {
        await RunRepositoryAsync("推送", async cancellationToken =>
        {
            await _gitService.PushAsync(RepositoryPath, cancellationToken);
            await LoadStateAsync(cancellationToken);
        }, "查看 Problems 中的 git 输出后重试。", canCancel: true);
    }

    /// <summary>Conservative VS Code-style synchronization: never asks Git to merge a dirty
    /// worktree and never pushes when the fast-forward-only pull was refused.</summary>
    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task SyncAsync()
    {
        await RunRepositoryAsync("安全同步", async cancellationToken =>
        {
            var status = await _gitService.GetStatusAsync(RepositoryPath, cancellationToken);
            if (!status.IsClean)
                throw new InvalidOperationException("工作区存在未提交更改。请先提交、暂存或丢弃更改后再同步。");

            var pull = await _gitService.PullAsync(RepositoryPath, GitPullOptions.SafeDefault, cancellationToken);
            if (!pull.Success)
                throw new InvalidOperationException(pull.Message ?? "拉取未能安全完成，未执行推送。");

            await _gitService.PushAsync(RepositoryPath, cancellationToken);
            await LoadStateAsync(cancellationToken);
        }, "请处理本地更改、非快进更新或冲突后再重试。", canCancel: true);
    }

    private bool CanSync() => IsRepository;

    partial void OnIsRepositoryChanged(bool value)
    {
        if (!value)
        {
            _graphCollapsedForNoRepository = true;
            IsGraphViewExpanded = false;
        }
        else if (_graphCollapsedForNoRepository)
        {
            _graphCollapsedForNoRepository = false;
            IsGraphViewExpanded = true;
        }
    }

    /// <summary>"在资源管理器中显示": hands the changed path to the workbench, which switches to the
    /// explorer section and reveals the file in the tree.</summary>
    [RelayCommand(CanExecute = nameof(CanRevealInExplorer))]
    private void RevealInExplorer(GitChangeItem? item)
    {
        if (item is null)
        {
            return;
        }

        RevealRequested?.Invoke(this, item.Path);
    }

    private bool CanRevealInExplorer(GitChangeItem? item) => item is not null;

    /// <summary>Opens the diff of a change row (context-menu "打开 diff"). Setting the row selection
    /// normally opens the diff; when the row is already the current selection the change handler does
    /// not fire again, so the request is raised directly.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenChangeDiff))]
    private void OpenChangeDiff(GitChangeItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsStaged)
        {
            if (!ReferenceEquals(SelectedStagedChange, item))
            {
                SelectedStagedChange = item;
            }
            else
            {
                RaiseDiffOpenRequested(item, isPreview: true);
            }
        }
        else
        {
            if (!ReferenceEquals(SelectedUnstagedChange, item))
            {
                SelectedUnstagedChange = item;
            }
            else
            {
                RaiseDiffOpenRequested(item, isPreview: true);
            }
        }
    }

    private bool CanOpenChangeDiff(GitChangeItem? item) => item is not null;

    /// <summary>双击变更行:把变更对应的工作区文件在共享编辑器中打开为常驻标签(与资源管理器
    /// 树双击一致;单击是预览 diff,见 OnSelectedUnstagedChangeChanged)。兼容平铺行
    /// (GitChangeItem)与树状行(ScmFileNode)两种 DataContext。</summary>
    [RelayCommand]
    private void OpenChangeFilePermanent(object? row)
    {
        var change = row switch
        {
            GitChangeItem item => item,
            ScmFileNode node => node.Change,
            _ => null
        };
        if (change is null)
        {
            return;
        }

        var fullPath = System.IO.Path.Combine(RepositoryPath, change.Path);
        _ = _editor.OpenFileAsync(fullPath, permanent: true);
    }

    /// <summary>"打开文件":把变更对应的工作区文件在共享编辑器中以只读预览打开(VS Code 右键"打开文件")。</summary>
    [RelayCommand]
    private void OpenFilePreview(GitChangeItem? item)
    {
        if (item is null)
        {
            return;
        }

        var fullPath = System.IO.Path.Combine(RepositoryPath, item.Path);
        _ = _editor.OpenFileAsync(fullPath);
    }

    // ===== 拖放暂存(未暂存 ↔ 已暂存 分区之间拖动文件行) =====

    /// <summary>拖放到"已暂存的更改"分区:对拖入的路径执行 git add。</summary>
    [RelayCommand]
    private async Task StageDroppedChangesAsync(object? parameter)
    {
        if (parameter is not string[] { Length: > 0 } paths)
        {
            return;
        }

        await RunRepositoryAsync($"暂存拖入的 {paths.Length} 项更改", async cancellationToken =>
        {
            await RefreshStatusAfterMutationAsync(
                ct => _gitService.StageAsync(RepositoryPath, paths, ct), [], cancellationToken);
        }, canCancel: true);
        RefreshChangeCommands();
    }

    /// <summary>拖放到"未暂存的更改"分区:对拖入的路径执行取消暂存。</summary>
    [RelayCommand]
    private async Task UnstageDroppedChangesAsync(object? parameter)
    {
        if (parameter is not string[] { Length: > 0 } paths)
        {
            return;
        }

        await RunRepositoryAsync($"取消暂存拖入的 {paths.Length} 项更改", async cancellationToken =>
        {
            await RefreshStatusAfterMutationAsync(
                ct => _gitService.UnstageAsync(RepositoryPath, paths, ct), [], cancellationToken);
        }, canCancel: true);
        RefreshChangeCommands();
    }

    // ===== 提交消息历史(最近 10 条,会话内) =====

    public ObservableCollection<string> RecentCommitMessages { get; } = [];

    private void RecordCommitMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var existing = RecentCommitMessages.FirstOrDefault(candidate => string.Equals(candidate, message, StringComparison.Ordinal));
        if (existing is not null)
        {
            RecentCommitMessages.Remove(existing);
        }

        RecentCommitMessages.Insert(0, message);
        while (RecentCommitMessages.Count > 10)
        {
            RecentCommitMessages.RemoveAt(RecentCommitMessages.Count - 1);
        }
    }

    /// <summary>从历史菜单选择一条消息填入提交输入框。</summary>
    [RelayCommand]
    private void ApplyCommitMessage(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            CommitMessage = message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreateBranch))]
    private async Task CreateBranchAsync()
    {
        var name = NewBranchName.Trim();
        await RunRepositoryAsync($"创建分支 {name}", async cancellationToken =>
        {
            await _gitService.CreateBranchAsync(RepositoryPath, name, cancellationToken);
            NewBranchName = string.Empty;
            await LoadStateAsync(cancellationToken);
        }, canCancel: true);
    }

    [RelayCommand(CanExecute = nameof(CanSwitchBranch))]
    private async Task SwitchBranchAsync(GitBranchInfo? branch)
    {
        if (branch is null || branch.IsCurrent)
        {
            return;
        }

        await RunRepositoryAsync($"切换到分支 {branch.Name}", async cancellationToken =>
        {
            await _gitService.SwitchBranchAsync(RepositoryPath, branch.Name, cancellationToken);
            await LoadStateAsync(cancellationToken);
        }, "若有未提交更改，git 会拒绝切换并给出原因。", canCancel: true);
    }

    private bool CanSwitchBranch(GitBranchInfo? branch) => branch is { IsCurrent: false };

    // ===== Git 标签管理 =====

    /// <summary>当前选中提交上的标签徽标(仅 Kind==Tag),供最近提交行右键菜单呈现每个标签的子操作。</summary>
    public IReadOnlyList<GitLogRefBadge> SelectedLogRowTags =>
        SelectedLogRow?.RefBadges.Where(badge => badge.IsTag).ToArray() ?? [];

    /// <summary>选中提交上每个标签的完整操作节点,供最近提交行右键菜单渲染嵌套「标签」子菜单。
    /// 数据源为 <see cref="SelectedLogRowTags"/>(仅 Kind==Tag),随选中提交变化重算。</summary>
    public IReadOnlyList<GitTagMenuNode> SelectedLogRowTagMenus =>
        SelectedLogRowTags
            .Select(badge => new GitTagMenuNode(
                badge.Name,
                [
                    // TagName 随条目携带:子菜单操作项位于嵌套弹出层,RelativeSource AncestorType
                    // 不跨越 Popup 边界,靠祖先查找取标签名会解析为 null → 命令静默无操作
                    // (删除标签点击无反应的根因)。
                    new GitMenuCommandItem("复制标签名", CopyTagNameCommand, badge.Name),
                    new GitMenuCommandItem("推送标签", PushTagCommand, badge.Name),
                    new GitMenuCommandItem("检出标签", CheckoutTagCommand, badge.Name),
                    new GitMenuCommandItem("删除标签", DeleteTagCommand, badge.Name),
                ]))
            .ToArray();

    [RelayCommand]
    private async Task CreateTagAtCommitAsync()
    {
        var row = SelectedLogRow;
        var hash = row?.Commit.Hash;
        await PromptAndCreateTagAsync(hash, hash is null ? "在 HEAD 创建标签" : $"在提交 {row!.Commit.ShortHash} 创建标签");
    }

    [RelayCommand]
    private async Task CreateTagAtHeadAsync() => await PromptAndCreateTagAsync(null, "在 HEAD 创建标签");

    /// <summary>弹出标签名对话框,确认后创建标签(轻量或注释化)并刷新历史徽标。</summary>
    private async Task PromptAndCreateTagAsync(string? targetRef, string operation)
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        var result = Views.Dialogs.TextPromptDialog.ShowPrompt(owner, operation, "输入新标签名（可用空格分隔，不带 refs/tags/ 前缀）");
        if (!result.Confirmed || string.IsNullOrWhiteSpace(result.Text))
        {
            LogService.Write("INFO", $"已取消：{operation}。");
            return;
        }

        var name = result.Text;
        await RunRepositoryAsync(operation, async cancellationToken =>
        {
            await _gitService.CreateTagAsync(RepositoryPath, name, result.Annotated, result.Message, targetRef, cancellationToken);
            await LoadStateAsync(cancellationToken);
        }, canCancel: true);
    }

    [RelayCommand]
    private async Task CopyTagNameAsync(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return;
        _clipboard.SetText(tagName);
        LogService.Write("INFO", $"已复制标签名：{tagName}");
    }

    [RelayCommand]
    private async Task PushTagAsync(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return;
        await RunRepositoryAsync($"推送标签 {tagName}", async cancellationToken =>
        {
            await _gitService.PushTagAsync(RepositoryPath, tagName, cancellationToken);
            await LoadStateAsync(cancellationToken);
        }, canCancel: true);
    }

    [RelayCommand]
    private async Task PushAllTagsAsync()
    {
        await RunRepositoryAsync("推送全部标签", async cancellationToken =>
        {
            await _gitService.PushAllTagsAsync(RepositoryPath, cancellationToken);
            await LoadStateAsync(cancellationToken);
        }, canCancel: true);
    }

    [RelayCommand]
    private async Task FetchTagsAsync()
    {
        await RunRepositoryAsync("拉取所有标签", async cancellationToken =>
        {
            await _gitService.FetchTagsAsync(RepositoryPath, cancellationToken);
            await LoadStateAsync(cancellationToken);
        }, canCancel: true);
    }

    [RelayCommand]
    private async Task DeleteTagAsync(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return;
        if (!_confirmationService.Confirm("删除标签", $"确定删除本地标签“{tagName}”？该操作不可撤销（远端标签需另行删除）。"))
        {
            LogService.Write("INFO", $"已取消删除标签：{tagName}。");
            return;
        }

        await RunRepositoryAsync($"删除标签 {tagName}", async cancellationToken =>
        {
            await _gitService.DeleteTagAsync(RepositoryPath, tagName, cancellationToken);
            await LoadStateAsync(cancellationToken);
        }, canCancel: true);
    }

    [RelayCommand]
    private async Task CheckoutTagAsync(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return;
        if (!_confirmationService.Confirm("检出标签", $"将检出到标签“{tagName}”，HEAD 会进入分离状态（detached HEAD）。此后提交不归属于任何分支，建议先在目标分支上新建分支再继续开发。确定继续？"))
        {
            LogService.Write("INFO", $"已取消检出标签：{tagName}。");
            return;
        }

        await RunRepositoryAsync($"检出标签 {tagName}", async cancellationToken =>
        {
            await _gitService.CheckoutTagAsync(RepositoryPath, tagName, cancellationToken);
            await LoadStateAsync(cancellationToken);
        }, "已在分离 HEAD 状态；如需继续开发，请先创建或切换到新分支。", canCancel: true);
    }

    // ===== Section expansion (session-only) =====

    [RelayCommand]
    private void ToggleSection(string section)
    {
        switch (section)
        {
            case "changesView":
                IsChangesViewExpanded = !IsChangesViewExpanded;
                break;
            case "graphView":
                IsGraphViewExpanded = !IsGraphViewExpanded;
                break;
            case "unstaged":
                _unstagedUserToggled = true;
                IsUnstagedSectionExpanded = !IsUnstagedSectionExpanded;
                break;
            case "staged":
                _stagedUserToggled = true;
                IsStagedSectionExpanded = !IsStagedSectionExpanded;
                break;
            case "branches":
                IsBranchesSectionExpanded = !IsBranchesSectionExpanded;
                break;
            case "log":
                IsLogSectionExpanded = !IsLogSectionExpanded;
                break;
        }
    }

    // ================================= Copy / selection commands =================================

    [RelayCommand(CanExecute = nameof(CanCopyChangePaths))]
    private void CopyChangePaths() =>
        _clipboard.SetText(string.Join(Environment.NewLine, UnstagedSelection.Select(item => item.Path).Concat(StagedSelection.Select(item => item.Path))));

    private bool CanCopyChangePaths() => UnstagedSelection.Any() || StagedSelection.Any();

    [RelayCommand(CanExecute = nameof(CanCopyBranchNames))]
    private void CopyBranchNames() =>
        _clipboard.SetText(string.Join(Environment.NewLine, SelectedBranches.Select(branch => branch.Name)));

    private bool CanCopyBranchNames() => SelectedBranches.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCopyCommitHashes))]
    private void CopyCommitHashes() =>
        _clipboard.SetText(string.Join(Environment.NewLine, SelectedCommits.Select(row => row.Commit.Hash)));

    private bool CanCopyCommitHashes() => SelectedCommits.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCopyCommitSubjects))]
    private void CopyCommitSubjects() =>
        _clipboard.SetText(string.Join(Environment.NewLine, SelectedCommits.Select(row => row.Commit.Subject)));

    private bool CanCopyCommitSubjects() => SelectedCommits.Count > 0;

    /// <summary>Stages every selected unstaged change in ONE git call (no per-row loops).</summary>
    [RelayCommand(CanExecute = nameof(CanStageSelectedChanges))]
    private async Task StageSelectedChangesAsync()
    {
        var paths = UnstagedSelection.Select(item => item.Path).ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        await RunRepositoryAsync($"暂存 {paths.Length} 项更改", async cancellationToken =>
        {
            await RefreshStatusAfterMutationAsync(
                ct => _gitService.StageAsync(RepositoryPath, paths, ct), [], cancellationToken);
        }, canCancel: true);
        RefreshChangeCommands();
    }

    private bool CanStageSelectedChanges() => IsRepository && UnstagedSelection.Any();

    /// <summary>Unstages all selected staged changes in ONE git call.</summary>
    [RelayCommand(CanExecute = nameof(CanUnstageSelectedChanges))]
    private async Task UnstageSelectedChangesAsync()
    {
        var paths = StagedSelection.Select(item => item.Path).ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        await RunRepositoryAsync($"取消暂存 {paths.Length} 项更改", async cancellationToken =>
        {
            await RefreshStatusAfterMutationAsync(
                ct => _gitService.UnstageAsync(RepositoryPath, paths, ct), [], cancellationToken);
        }, canCancel: true);
        RefreshChangeCommands();
    }

    private bool CanUnstageSelectedChanges() => IsRepository && StagedSelection.Any();

    /// <summary>Discards the selected UNSTAGED changes in one git call. Staged changes are never part
    /// of a discard — they must be unstaged first (VS Code semantics).</summary>
    [RelayCommand(CanExecute = nameof(CanDiscardSelectedChanges))]
    private async Task DiscardSelectedChangesAsync()
    {
        var paths = UnstagedSelection.Select(item => item.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        if (!_confirmationService.Confirm("确认丢弃选中更改", $"将丢弃选中的 {paths.Length} 项未暂存更改（恢复到暂存区内容），且无法撤销。选择“否”可安全取消。"))
        {
            return;
        }

        await RunRepositoryAsync($"丢弃 {paths.Length} 项更改", async cancellationToken =>
        {
            await RefreshStatusAfterMutationAsync(
                ct => _gitService.DiscardUnstagedAsync(RepositoryPath, paths, ct), paths, cancellationToken);
        }, canCancel: true);
        RefreshChangeCommands();
    }

    private bool CanDiscardSelectedChanges() => IsRepository && UnstagedSelection.Any();

    // The unstaged and staged change lists keep INDEPENDENT selections (a change lives in exactly one
    // list). Sharing a single SelectedItem between InputSelectors would make WPF clear the selection
    // as soon as the other list fails to find the item — so each list selects its own row.
    partial void OnSelectedUnstagedChangeChanged(GitChangeItem? value)
    {
        if (value is not null)
        {
            // 单击开预览 diff(VS Code SCM 语义;双击改开文件,见 OpenChangeFilePermanent)。
            RaiseDiffOpenRequested(value, isPreview: true);
        }
    }

    partial void OnSelectedStagedChangeChanged(GitChangeItem? value)
    {
        if (value is not null)
        {
            RaiseDiffOpenRequested(value, isPreview: true);
        }
    }

    private void RaiseDiffOpenRequested(GitChangeItem value, bool isPreview = false) =>
        DiffOpenRequested?.Invoke(this, new GitDiffRequest(RepositoryPath, value.Change.Path, value.IsStaged, value.IsUntracked,
            IsPreview: isPreview, HeadBlobId: value.Change.HeadBlobId, IndexBlobId: value.Change.IndexBlobId,
            WorkspaceContext: _workspaceContext));

    partial void OnRepositoryPathChanged(string value)
    {
        // Switching repositories must stop the old working-tree watcher before the refresh
        // (re-)attaches it to the new location.
        _repositoryWatcher.Detach();
        _hasFullStateLoad = false;
        _lastHeadSignature = null;
        Interlocked.Exchange(ref _quietRefreshRequiresFullLoad, 0);
        _graphCollapsedForNoRepository = true;
        IsGraphViewExpanded = false;
        IsRepository = false;
        RepositorySummary = "未检测到 Git 仓库。";
        ClearChanges();
    }

    /// <summary>Runs the deferred silent refresh once the user operation that owned IsBusy
    /// completes, so watcher events arriving mid-operation never get lost. The operation just
    /// finished, so the catch-up refresh bypasses the silent-refresh minimum interval.</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsBusy) && !IsBusy && Volatile.Read(ref _quietRefreshPending) == 1)
        {
            StartQuietRefresh(immediate: true);
        }

        // G7: 展开被折叠的区段时,补齐此前跳过的 log / 传入传出 / stash 数据。
        if (e.PropertyName is nameof(IsGraphViewExpanded) or nameof(IsChangesViewExpanded))
        {
            _ = EnsureLazySectionsLoadedAsync();
        }
    }

    /// <summary>Watcher-driven silent refresh entry point (single flight + merged pending + minimum
    /// interval). Runs outside <see cref="PageViewModel.RunAsync"/> on purpose: no status banner,
    /// no operation log entry, no cancel button (VS Code auto-refresh is invisible).</summary>
    private void HandleWatcherChanges(GitRepositoryChangesDetectedEventArgs? changes = null)
    {
        if (!IsServiceContextCurrent())
        {
            return;
        }

        if (changes is { HeadOrRefsChanged: true } or { IsUnknown: true })
        {
            Interlocked.Exchange(ref _quietRefreshRequiresFullLoad, 1);
        }

        StartQuietRefresh(immediate: false);
    }

    /// <summary>Attempts to start the silent refresh. The in-flight flag is taken atomically so
    /// concurrent callers (watcher thread, retry timer, property-changed path) can never start two
    /// refreshes; the pending flag is consumed here — a refresh that starts now satisfies every
    /// request merged so far, and <see cref="RunQuietRefreshAsync"/>/finally only re-runs for
    /// requests that arrive while it is in flight.</summary>
    private void StartQuietRefresh(bool immediate)
    {
        if (Interlocked.CompareExchange(ref _quietRefreshInFlight, 1, 0) == 1)
        {
            Interlocked.Exchange(ref _quietRefreshPending, 1);
            return;
        }

        if (IsBusy)
        {
            // A user operation is running; catch up with the latest state when it finishes.
            Interlocked.Exchange(ref _quietRefreshInFlight, 0);
            Interlocked.Exchange(ref _quietRefreshPending, 1);
            return;
        }

        if (!immediate)
        {
            var elapsedMs = Environment.TickCount64 - _lastQuietRefreshCompletedUtcTicks;
            if (elapsedMs < QuietRefreshMinimumIntervalMs)
            {
                // Inside the cooldown after the last silent refresh: record the pending refresh
                // and run it once the interval elapses (single retry timer, at most one).
                Interlocked.Exchange(ref _quietRefreshInFlight, 0);
                Interlocked.Exchange(ref _quietRefreshPending, 1);
                ScheduleQuietRefreshRetry(QuietRefreshMinimumIntervalMs - elapsedMs);
                return;
            }
        }

        Interlocked.Exchange(ref _quietRefreshPending, 0);
        _ = RunQuietRefreshAsync(); // its finally releases the in-flight flag
    }

    /// <summary>Schedules at most one retry of the pending silent refresh after
    /// <paramref name="delayMs"/>. The retry re-enters through <see cref="HandleWatcherChanges"/>,
    /// which re-checks every guard (in flight, busy, interval) before actually refreshing.</summary>
    private void ScheduleQuietRefreshRetry(long delayMs)
    {
        if (Interlocked.Exchange(ref _quietRefreshRetryScheduled, 1) == 1)
        {
            return; // a retry is already scheduled; it will pick up the pending flag
        }

        _ = Task.Delay(TimeSpan.FromMilliseconds(delayMs)).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _quietRefreshRetryScheduled, 0);
            if (Volatile.Read(ref _quietRefreshPending) == 0)
            {
                return;
            }

            if (_uiContext is null)
            {
                HandleWatcherChanges(); // no WPF app (unit tests without STA context)
                return;
            }

            // Marshal back to the context that owns the ViewModel and its bound collections.
            // Do not use Application.Current.Dispatcher here: it may belong to an unrelated WPF
            // host (notably a shared STA test host) whose queue is not currently being pumped.
            _uiContext.Post(_ =>
            {
                HandleWatcherChanges();
            }, null);
        });
    }

    private async Task RunQuietRefreshAsync()
    {
        await _repositoryContextGate.WaitAsync();
        try
        {
            var context = _workspaceContext;
            var path = RepositoryPath;
            if (!IsRepositoryContextCurrent(context, path)) return;

            // git may write back the index stat cache during/after the read pass; arm the
            // suppression window so those .git\index events do not retrigger a refresh.
            _repositoryWatcher.BeginSuppressionWindow();
            var requiresFullLoad = Interlocked.Exchange(ref _quietRefreshRequiresFullLoad, 0) == 1;
            var signature = ReadHeadSignature(path);
            if (!requiresFullLoad && _hasFullStateLoad && string.Equals(signature, _lastHeadSignature, StringComparison.Ordinal))
            {
                // HEAD did not move: edits cannot change branches / history / stashes, a single
                // git status call keeps the view current without the full subprocess fan-out.
                await LoadStateLiteAsync();
            }
            else
            {
                await LoadStateAsync();
            }
        }
        catch (Exception ex)
        {
            // Keep watching; the next burst retries. Only a single WARNING, no banner.
            LogService.Write("WARNING", $"自动刷新源代码管理状态失败：{ex.Message}");
        }
        finally
        {
            _repositoryContextGate.Release();
            Interlocked.Exchange(ref _quietRefreshInFlight, 0);
            // Post-completion cooldown (VS Code throttle): bursts observed while this refresh was
            // in flight (they set the pending flag above) may not restart another status for at
            // least QuietRefreshMinimumIntervalMs.
            _lastQuietRefreshCompletedUtcTicks = Environment.TickCount64;
            if (Interlocked.Exchange(ref _quietRefreshPending, 0) == 1)
            {
                HandleWatcherChanges();
            }
        }
    }

    /// <summary>Status-only variant of <see cref="LoadStateAsync"/> for watcher-driven refreshes
    /// whose HEAD signature is unchanged: one git status call refreshes the change lists, branch
    /// chip and ahead/behind badges (and the explorer decorations via
    /// <see cref="StatusRefreshed"/>), while branches / history / stashes are left untouched.</summary>
    private async Task LoadStateLiteAsync(CancellationToken cancellationToken = default)
    {
        var path = RepositoryPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            _repositoryWatcher.Detach();
            _hasFullStateLoad = false;
            IsRepository = false;
            RepositorySummary = string.IsNullOrWhiteSpace(path) ? "未选择仓库。" : "目录不存在。";
            ResetBranchStatus();
            ClearChanges();
            return;
        }

        // Watcher refreshes only need the current status shape. Avoid expanding every untracked
        // directory into every file on each build/save burst; an explicit/full refresh still uses
        // the detailed all-files form below.
        var status = await _gitService.GetStatusAsync(path, cancellationToken, includeAllUntracked: false);
        if (!IsServiceContextCurrent()) return;
        StatusRefreshed?.Invoke(this, status);
        IsRepository = status.IsRepository;
        if (!status.IsRepository)
        {
            _repositoryWatcher.Detach();
            _hasFullStateLoad = false;
            RepositorySummary = "未检测到 Git 仓库。";
            ResetBranchStatus();
            ClearChanges();
            return;
        }

        CurrentBranch = status.Branch ?? string.Empty;
        UpstreamName = status.Upstream ?? string.Empty;
        HasUpstream = UpstreamName.Length > 0;
        AheadCount = status.AheadCount;
        BehindCount = status.BehindCount;
        RepositorySummary = BuildRepositorySummary(status);
        PopulateChanges(status);
        if (_enableScmAutoRefresh) _repositoryWatcher.Attach(path);
    }

    /// <summary>Cheap signature of the repository HEAD (resolved ref content read straight from
    /// <c>.git</c>, no git subprocess): detached HEAD signs with the hash itself, a symbolic HEAD
    /// signs with "ref + resolved hash" (an unborn branch signs with the ref name alone).</summary>
    private static string? ReadHeadSignature(string repositoryPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(repositoryPath))
            {
                return null;
            }

            var gitDirectory = Path.Combine(repositoryPath, ".git");
            var head = File.ReadAllText(Path.Combine(gitDirectory, "HEAD")).Trim();
            if (!head.StartsWith("ref: ", StringComparison.OrdinalIgnoreCase))
            {
                return head;
            }

            var reference = head["ref: ".Length..].Replace('/', Path.DirectorySeparatorChar);
            var referenceFile = Path.Combine(gitDirectory, reference);
            return File.Exists(referenceFile)
                ? $"{head}@{File.ReadAllText(referenceFile).Trim()}"
                : head;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private void HookSelectionChanges<T>(ObservableCollection<T> collection) =>
        collection.CollectionChanged += (_, _) => NotifySelectionCommands();

    private void NotifySelectionCommands()
    {
        CopyChangePathsCommand.NotifyCanExecuteChanged();
        CopyBranchNamesCommand.NotifyCanExecuteChanged();
        CopyCommitHashesCommand.NotifyCanExecuteChanged();
        CopyCommitSubjectsCommand.NotifyCanExecuteChanged();
        StageSelectedChangesCommand.NotifyCanExecuteChanged();
        UnstageSelectedChangesCommand.NotifyCanExecuteChanged();
        DiscardSelectedChangesCommand.NotifyCanExecuteChanged();
        StageFolderCommand.NotifyCanExecuteChanged();
        UnstageFolderCommand.NotifyCanExecuteChanged();
        DiscardFolderCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadStateAsync(CancellationToken cancellationToken = default)
    {
        // Full-load in-flight marker: the G7 lazy-section loader must never run concurrently with a
        // full reload (both would fetch the log), so expansion toggles fired mid-load are ignored.
        Interlocked.Exchange(ref _fullLoadRunning, 1);
        try
        {
            await LoadStateCoreAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Exchange(ref _fullLoadRunning, 0);
        }
    }

    private async Task LoadStateCoreAsync(CancellationToken cancellationToken = default)
    {
        var path = RepositoryPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            _repositoryWatcher.Detach();
            IsRepository = false;
            RepositorySummary = string.IsNullOrWhiteSpace(path) ? "未选择仓库。" : "目录不存在。";
            ResetBranchStatus();
            ClearChanges();
            return;
        }

        var status = await _gitService.GetStatusAsync(path, cancellationToken);
        if (!IsServiceContextCurrent()) return;
        StatusRefreshed?.Invoke(this, status);
        IsRepository = status.IsRepository;
        if (!status.IsRepository)
        {
            _repositoryWatcher.Detach();
            RepositorySummary = "未检测到 Git 仓库。";
            ResetBranchStatus();
            ClearChanges();
            return;
        }

        CurrentBranch = status.Branch ?? string.Empty;
        UpstreamName = status.Upstream ?? string.Empty;
        HasUpstream = UpstreamName.Length > 0;
        AheadCount = status.AheadCount;
        BehindCount = status.BehindCount;
        RepositorySummary = BuildRepositorySummary(status);
        PopulateChanges(status);
        if (status.Truncated)
        {
            // G2: status 超条目上限 → UI 降级提示(列表只保留前 N 条,不继续无界缓冲)。
            LogService.Write("WARNING",
                $"仓库变更条目过多，仅显示前 {GitService.StatusMaximumEntries} 条。");
        }

        // G3: 相互独立的 git 读命令一次性并发发出(总耗时从"和"变"最大值")。
        // G7: 折叠区段对应的读命令不发——其数据按展开懒加载(见 EnsureLazySectionsLoadedAsync)。
        var branchesTask = LoadBranchesSectionAsync(path, cancellationToken);
        var remoteBranchesTask = LoadRemoteBranchesSectionAsync(path, cancellationToken);
        var logTask = LoadLogSectionAsync(path, cancellationToken);
        var stashesTask = IsChangesViewExpanded
            ? LoadStashesSectionAsync(path, cancellationToken)
            : Task.FromResult<IReadOnlyList<GitStashInfo>>([]);
        var remoteCommitsTask = FetchRemoteCommitsAsync(path, status.Upstream, cancellationToken);

        // Observe every sibling before consuming any one result. If a later section fails, the
        // other process tasks must still be awaited so their exceptions are not left unobserved.
        await Task.WhenAll(branchesTask, remoteBranchesTask, logTask, stashesTask, remoteCommitsTask);
        var branches = await branchesTask;
        var remoteBranches = await remoteBranchesTask;
        var commits = await logTask;
        var stashes = await stashesTask;
        if (!IsServiceContextCurrent()) return;

        var branchesChanged = SyncCollection(Branches, branches);
        var remoteChanged = SyncCollection(RemoteBranches, remoteBranches);
        var tipToColorChanged = branchesChanged || remoteChanged;
        SelectedBranch = Branches.FirstOrDefault(branch => branch.IsCurrent);

        if (IsGraphViewExpanded)
        {
            var logsChanged = SyncCollection(Logs, commits);
            _logsExhausted = Logs.Count < LogPageSize;
            if (logsChanged)
            {
                // Identical history must not rebuild the graph rows: the clear + re-add churn is what
                // makes the commit graph flicker on every silent watcher refresh.
                RebuildLogRows();
                OnPropertyChanged(nameof(GraphViewBadgeCount));
                LoadMoreCommitsCommand.NotifyCanExecuteChanged();
            }
            else if (tipToColorChanged)
            {
                // Branches moved without history change: re-color the graph.
                RebuildLogRows();
            }

            var (outgoing, incoming) = await remoteCommitsTask;
            var outgoingChanged = SyncCollection(OutgoingCommits, outgoing);
            var incomingChanged = SyncCollection(IncomingCommits, incoming);
            if (outgoingChanged || incomingChanged)
            {
                OnPropertyChanged(nameof(HasOutgoing));
                OnPropertyChanged(nameof(HasIncoming));
                // 最近提交图直接包含传入/传出的真实提交；即使 ahead/behind 数量未变，
                // 提交集合变化也必须重建，否则边界下面仍显示上一轮内容。
                RebuildLogRows();
            }

            // 同步边界由上游与 ahead/behind 计数驱动；签名变化时更新边界位置与标签。
            var syncSignature = $"{status.Upstream}|{status.AheadCount}|{status.BehindCount}";
            if (syncSignature != _lastSyncSignature)
            {
                _lastSyncSignature = syncSignature;
                RebuildLogRows();
            }
        }
        // else: 图分区折叠(G7)→ log / 传入传出 子进程整段跳过,已有历史内容原样保留;
        // 展开时由 EnsureLazySectionsLoadedAsync 重新补齐。

        if (IsChangesViewExpanded)
        {
            SyncCollection(Stashes, stashes);
        }
        _logSectionLoaded = IsGraphViewExpanded;
        _stashesLoaded = IsChangesViewExpanded;

        // Remember the HEAD signature so subsequent watcher refreshes can stay status-only.
        _lastHeadSignature = ReadHeadSignature(path);
        _hasFullStateLoad = true;

        // A healthy state load (re-)arms the watcher; the re-attach also refreshes the
        // suppression window so index write-backs from this very refresh stay ignored.
        if (_enableScmAutoRefresh) _repositoryWatcher.Attach(path);
    }

    private void PopulateChanges(GitRepositoryStatus status)
    {
        var previousUnstaged = SelectedUnstagedChange?.Path;
        var previousStaged = SelectedStagedChange?.Path;
        var stagedChanged = SyncCollection(StagedChanges, status.StagedChanges
            .OrderBy(change => change.Path, StringComparer.OrdinalIgnoreCase)
            .Select(change => new GitChangeItem(change, IsStagedSection: true))
            .ToList());
        var unstagedChanged = SyncCollection(UnstagedChanges, status.UnstagedChanges
            .OrderBy(change => change.Path, StringComparer.OrdinalIgnoreCase)
            .Select(change => new GitChangeItem(change, IsStagedSection: false))
            .ToList());
        if (!stagedChanged && !unstagedChanged)
        {
            // Identical change lists: raise nothing so the lists (and their tree views) stay
            // visually untouched during silent watcher refreshes.
            return;
        }

        OnPropertyChanged(nameof(StagedCountLabel));
        OnPropertyChanged(nameof(UnstagedCountLabel));
        OnPropertyChanged(nameof(IsClean));
        OnPropertyChanged(nameof(ChangeCount));
        OnPropertyChanged(nameof(ChangesViewBadgeCount));
        RefreshChangeCommands();

        // Multi-selections must not keep rows that no longer exist after the refresh (e.g. the user
        // staged the selected changes): prune them so copy/stage/discard-always reflect the live lists.
        PruneSelection(SelectedUnstagedChanges, UnstagedChanges);
        PruneSelection(SelectedStagedChanges, StagedChanges);

        // Automatic (pre-user-interaction) section state mirrors the change counts: unstaged opens
        // when there is any change, staged opens when something is staged, and a staged count dropping
        // to zero collapses the staged section again (VS Code).
        if (!_unstagedUserToggled)
        {
            IsUnstagedSectionExpanded = UnstagedChanges.Count > 0;
        }

        if (!_stagedUserToggled)
        {
            IsStagedSectionExpanded = StagedChanges.Count > 0;
        }

        // Restore each list's selection by path; follow a change that moved between lists (e.g. the
        // user staged the selected change) so the highlighted row tracks its new location.
        SelectedUnstagedChange = UnstagedChanges.FirstOrDefault(item => item.Path == previousUnstaged);
        SelectedStagedChange = StagedChanges.FirstOrDefault(item => item.Path == previousStaged);
        if (SelectedUnstagedChange is null && previousUnstaged is not null)
        {
            SelectedStagedChange ??= StagedChanges.FirstOrDefault(item => item.Path == previousUnstaged);
        }

        if (SelectedStagedChange is null && previousStaged is not null)
        {
            SelectedUnstagedChange ??= UnstagedChanges.FirstOrDefault(item => item.Path == previousStaged);
        }

        RebuildTreeRows();
    }

    private static void PruneSelection<T>(ObservableCollection<T> selection, ObservableCollection<T> current)
    {
        for (var i = selection.Count - 1; i >= 0; i--)
        {
            if (!current.Contains(selection[i]))
            {
                selection.RemoveAt(i);
            }
        }
    }

    /// <summary>Syncs <paramref name="target"/> to <paramref name="desired"/> with minimal churn:
    /// returns false and raises nothing when the sequence already matches (silent watcher
    /// refreshes must not repaint unchanged lists — that churn is what flickers the graph). When it
    /// changes, the prefix/suffix diff (<see cref="CollectionDiffer"/>) replays only the affected
    /// middle range as Remove/Insert so unchanged rows keep their instances and containers
    /// (scroll anchor, expansion state survive); a wholesale change (no common prefix) keeps the
    /// single-Reset contract callers rely on to restore path-based selections after publishing.</summary>
    internal static bool SyncCollection<T>(BulkObservableCollection<T> target, IReadOnlyList<T> desired)
    {
        var comparer = EqualityComparer<T>.Default;
        if (target.Count == desired.Count)
        {
            var same = true;
            for (var i = 0; i < desired.Count; i++)
            {
                if (!comparer.Equals(target[i], desired[i]))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return false;
            }
        }

        // No common prefix = the whole sequence is replaced: one Reset, exactly like the old
        // ReplaceRange path (callers restore selection by path right after this returns true).
        if (CollectionDiffer.Compute(target, desired, comparer).RemoveStart == 0)
        {
            target.ReplaceRange(desired);
            return true;
        }

        CollectionDiffer.Apply(target, desired, comparer);
        return true;
    }

    private void ClearChanges()
    {
        StagedChanges.Clear();
        UnstagedChanges.Clear();
        UnstagedTreeRows.Clear();
        StagedTreeRows.Clear();
        SelectedUnstagedTreeRows.Clear();
        SelectedStagedTreeRows.Clear();
        SelectedUnstagedTreeRow = null;
        SelectedStagedTreeRow = null;
        Branches.Clear();
        RemoteBranches.Clear();
        Logs.Clear();
        LogRows.Clear();
        OutgoingCommits.Clear();
        IncomingCommits.Clear();
        OutgoingCommits.Clear();
        IncomingCommits.Clear();
        _lastSyncSignature = string.Empty;
        HasUpstream = false;
        UpstreamName = string.Empty;
        Stashes.Clear();
        _logsExhausted = false;
        SelectedBranch = null;
        SelectedStagedChange = null;
        SelectedUnstagedChange = null;
        OnPropertyChanged(nameof(StagedCountLabel));
        OnPropertyChanged(nameof(UnstagedCountLabel));
        OnPropertyChanged(nameof(IsClean));
        OnPropertyChanged(nameof(ChangeCount));
        OnPropertyChanged(nameof(ChangesViewBadgeCount));
        OnPropertyChanged(nameof(GraphViewBadgeCount));
        RefreshChangeCommands();
    }

    private void RefreshChangeCommands()
    {
        OnPropertyChanged(nameof(CanCommit));
        StageAllCommand.NotifyCanExecuteChanged();
        UnstageAllCommand.NotifyCanExecuteChanged();
        DiscardAllCommand.NotifyCanExecuteChanged();
        StageChangeCommand.NotifyCanExecuteChanged();
        UnstageChangeCommand.NotifyCanExecuteChanged();
        DiscardChangeCommand.NotifyCanExecuteChanged();
        CommitCommand.NotifyCanExecuteChanged();
        CommitAndPushCommand.NotifyCanExecuteChanged();
        CommitAndSyncCommand.NotifyCanExecuteChanged();
        CreateBranchCommand.NotifyCanExecuteChanged();
        NotifySelectionCommands();
    }

    /// <summary>折叠栏点击:切换提交行展开;首次展开时懒加载该提交的更改文件并缓存
    /// (同一提交只调一次 <c>git show --name-status</c>,折叠再展开不重复请求),
    /// 加载/失败状态落在行上,不占用全局忙碌态。</summary>
    [RelayCommand]
    private async Task ToggleLogRowAsync(GitLogRow? row)
    {
        if (row is null || !IsServiceContextCurrent())
        {
            return;
        }

        row.IsExpanded = !row.IsExpanded;
        if (!row.IsExpanded || row.IsLoaded || row.IsLoading)
        {
            return;
        }

        await _repositoryContextGate.WaitAsync();
        if (!IsServiceContextCurrent())
        {
            _repositoryContextGate.Release();
            return;
        }

        row.IsLoading = true;
        try
        {
            var path = RepositoryPath;
            var context = _workspaceContext;
            var changes = await _gitService.GetCommitFilesAsync(path, row.Commit.Hash);
            if (!IsRepositoryContextCurrent(context, path)) return;
            foreach (var change in changes)
            {
                row.Files.Add(new LogFileRow(row, change));
            }

            row.IsLoaded = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            LogService.Write("WARNING", $"无法读取提交 {row.Commit.ShortHash} 的文件清单：{ex.Message}");
        }
        finally
        {
            row.IsLoading = false;
            _repositoryContextGate.Release();
        }
    }

    /// <summary>点击展开行内的文件:以该提交的 diff 在共享编辑器打开(带 commit hash 的标签键)。</summary>
    [RelayCommand]
    private void OpenLogFileDiff(LogFileRow? file)
    {
        if (file is null || !IsServiceContextCurrent())
        {
            return;
        }

        var path = RepositoryPath;
        DiffOpenRequested?.Invoke(this, new GitDiffRequest(path, file.Change.Path, false, false, file.Row.Commit.Hash,
            WorkspaceContext: _workspaceContext));
    }

    private bool CanCopyLogFilePath(LogFileRow? file) => file is not null;

    /// <summary>复制展开行内单个文件的路径(模拟 VS Code 行内悬浮动作)。</summary>
    [RelayCommand(CanExecute = nameof(CanCopyLogFilePath))]
    private void CopyLogFilePath(LogFileRow? file)
    {
        if (file is not null)
        {
            _clipboard.SetText(file.Change.Path);
        }
    }

    // ===== 提交历史:分页 + 图形泳道行 + 传入/传出 + 贮藏(阶段三) =====

    private const int LogPageSize = 30;
    private bool _logsExhausted;

    /// <summary>历史列表行 = 提交 + 左缘图形泳道(折叠栏;ListBox SelectedItem 仅用于行高亮与多选)。</summary>
    public BulkObservableCollection<GitLogRow> LogRows { get; } = [];

    /// <summary>选中行变化必须联动通知标签菜单的两个派生属性:ContextMenu 子树的绑定只在
    /// 打开时求值一次,缺少通知会让子菜单永远停留在首次打开时的快照(过期标签/空菜单)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedLogRowTags), nameof(SelectedLogRowTagMenus))]
    private GitLogRow? selectedLogRow;

    public bool CanLoadMoreCommits => IsRepository && !_logsExhausted && Logs.Count > 0;

    /// <summary>历史底部"加载更多":按页增量拉取并重建图形。</summary>
    [RelayCommand(CanExecute = nameof(CanLoadMoreCommits))]
    private async Task LoadMoreCommitsAsync()
    {
        await _repositoryContextGate.WaitAsync();
        try
        {
            var context = _workspaceContext;
            var path = RepositoryPath;
            if (!IsRepositoryContextCurrent(context, path)) return;

            var prior = Logs.Count;
            var fetched = await _gitService.GetLogAsync(path, prior + LogPageSize);
            if (!IsRepositoryContextCurrent(context, path)) return;
            foreach (var commit in fetched.Skip(prior))
            {
                Logs.Add(commit);
            }

            _logsExhausted = fetched.Count < prior + LogPageSize;
            RebuildLogRows();
            OnPropertyChanged(nameof(GraphViewBadgeCount));
            LoadMoreCommitsCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            _repositoryContextGate.Release();
        }
    }

    private async Task<IReadOnlyList<GitBranchInfo>> LoadBranchesSectionAsync(
        string path, CancellationToken cancellationToken)
    {
        try
        {
            return await _gitService.GetBranchesAsync(path, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Write("WARNING", $"加载本地分支失败：{ex.Message}");
            return [];
        }
    }

    private async Task<IReadOnlyList<GitBranchInfo>> LoadRemoteBranchesSectionAsync(
        string path, CancellationToken cancellationToken)
    {
        try
        {
            return await _gitService.GetRemoteBranchesAsync(path, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Write("WARNING", $"加载远程分支失败：{ex.Message}");
            return [];
        }
    }

    private async Task<IReadOnlyList<GitStashInfo>> LoadStashesSectionAsync(
        string path, CancellationToken cancellationToken)
    {
        try
        {
            return await _gitService.GetStashesAsync(path, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Write("WARNING", $"加载贮藏列表失败：{ex.Message}");
            return [];
        }
    }

    /// <summary>当前分支(本地)的泳道色键:传出的更改行使用。</summary>
    private const string LocalColorKey = "GraphCurrentBranchBrush";

    internal void RebuildLogRows()
    {
        // 同时把本地 HEAD 与上游 tip 放入一张拓扑图：两个同步边界先各自占据一条泳道，
        // 传出/传入提交沿各自的父链向下，最后在共同历史汇合。不能先画完整本地历史再把
        // “传入的更改”追加到底部——那会把上游误画成一条线性历史，而非第二条分支。
        var outgoing = OutgoingCommits.Select(row => row.Commit).DistinctBy(commit => commit.Hash, StringComparer.OrdinalIgnoreCase).ToList();
        var outgoingHashes = outgoing.Select(commit => commit.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (outgoing.Count < AheadCount)
        {
            foreach (var commit in Logs.Where(commit => !outgoingHashes.Contains(commit.Hash)).Take(AheadCount - outgoing.Count))
            {
                outgoing.Add(commit);
                outgoingHashes.Add(commit.Hash);
            }
        }

        var common = Logs.Where(commit => !outgoingHashes.Contains(commit.Hash)).ToArray();
        var occupiedHashes = Logs.Select(commit => commit.Hash).Concat(outgoingHashes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var incoming = IncomingCommits.Select(row => row.Commit)
            .Where(commit => occupiedHashes.Add(commit.Hash))
            .DistinctBy(commit => commit.Hash, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        const string outgoingBoundaryHash = "refs/nornia/sync/outgoing";
        const string incomingBoundaryHash = "refs/nornia/sync/incoming";
        var commonTip = common.FirstOrDefault()?.Hash;
        var outgoingGraphCommits = ConnectGraphSequence(outgoing, commonTip);
        // 远端独有提交先沿上游泳道向下，最后接到“传入的更改”边界；该边界再汇入
        // 共同历史。因此分界行视觉上位于远端提交与本地/共同提交之间。
        var incomingGraphCommits = ConnectGraphSequence(
            incoming,
            HasUpstream && BehindCount > 0 ? incomingBoundaryHash : commonTip,
            forceTailParent: HasUpstream && BehindCount > 0);
        var commonGraphCommits = ConnectGraphSequence(common, null);

        var graphCommits = new List<GitCommitInfo>();
        var displayCommits = new List<GitCommitInfo?>();
        var outgoingBoundaryIndex = -1;
        var incomingBoundaryIndex = -1;

        if (HasUpstream && AheadCount > 0)
        {
            outgoingBoundaryIndex = graphCommits.Count;
            graphCommits.Add(GraphBoundaryCommit(outgoingBoundaryHash, outgoingGraphCommits.FirstOrDefault()?.Hash ?? commonTip));
            displayCommits.Add(null);
        }

        AppendGraphCommits(outgoingGraphCommits, outgoing, graphCommits, displayCommits);

        AppendGraphCommits(incomingGraphCommits, incoming, graphCommits, displayCommits);

        if (HasUpstream && BehindCount > 0)
        {
            incomingBoundaryIndex = graphCommits.Count;
            graphCommits.Add(GraphBoundaryCommit(incomingBoundaryHash, commonTip));
            displayCommits.Add(null);
        }

        AppendGraphCommits(commonGraphCommits, common, graphCommits, displayCommits);

        var graphBranches = GraphBranchRefs.ToList();
        if (outgoingBoundaryIndex >= 0)
            graphBranches.Add(new GitBranchInfo(CurrentBranch, true, TipHash: outgoingBoundaryHash));
        if (incomingBoundaryIndex >= 0)
            graphBranches.Add(new GitBranchInfo(UpstreamName, false, IsRemote: true, TipHash: incomingBoundaryHash));

        var graph = BuildCommitGraph(graphCommits, graphBranches);
        var rows = new List<GitLogRow>(graph.Count);
        for (var i = 0; i < graph.Count; i++)
        {
            if (i == outgoingBoundaryIndex)
            {
                rows.Add(SyncGroupRow($"{UpstreamName}...HEAD", "传出的更改", CurrentBranch,
                    graph[i], OutgoingCommits.FirstOrDefault()?.Commit.AuthorDate));
            }
            else if (i == incomingBoundaryIndex)
            {
                rows.Add(SyncGroupRow($"HEAD...{UpstreamName}", "传入的更改", UpstreamName,
                    graph[i], IncomingCommits.FirstOrDefault()?.Commit.AuthorDate));
            }
            else
            {
                rows.Add(new GitLogRow(displayCommits[i]!, graph[i]));
            }
        }

        // 一次 Replace(单条 Reset)取代 Clear()+逐行 Add:行对象按快照整体重建(图形泳道 /
        // 分支 ref 徽标跟随当前分支数据),但列表只发一条通知,视图一次重放而非 N+1 次。
        LogRows.ReplaceRange(rows);
    }

    /// <summary>同步边界行:虚线空心圆点 + 下段竖线;Commit.Hash 为 diff 范围
    /// (HEAD...upstream / upstream...HEAD),展开懒加载的正是该范围的合并文件影响。</summary>
    private static GitLogRow SyncGroupRow(
        string rangeHash,
        string subject,
        string target,
        GitGraphRow graph,
        DateTimeOffset? date)
    {
        return new GitLogRow(
            new GitCommitInfo(rangeHash, "", subject, null, target, "-", date ?? DateTimeOffset.MinValue),
            graph with { DotHollow = true, DotDashed = true },
            syncTarget: target);
    }

    private static GitCommitInfo GraphBoundaryCommit(string hash, string? parent) =>
        new(hash, hash, hash, null, "Nornia", "-", DateTimeOffset.MinValue,
            string.IsNullOrWhiteSpace(parent) ? [] : [parent]);

    private static IReadOnlyList<GitCommitInfo> ConnectGraphSequence(
        IReadOnlyList<GitCommitInfo> commits,
        string? tailParent,
        bool forceTailParent = false)
    {
        var result = commits.ToArray();
        for (var i = 0; i < result.Length; i++)
        {
            if (forceTailParent && i == result.Length - 1 && !string.IsNullOrWhiteSpace(tailParent))
            {
                // 在远端独有段与其原首父(共同历史)之间插入可见的同步边界节点。
                // 保留合并提交的其它父边，只替换首父链上的这一段。
                result[i] = result[i] with { Parents = [tailParent, .. result[i].ParentList.Skip(1)] };
                continue;
            }

            if (result[i].ParentList.Count > 0)
                continue;

            var inferredParent = i + 1 < result.Length ? result[i + 1].Hash : tailParent;
            if (!string.IsNullOrWhiteSpace(inferredParent))
                result[i] = result[i] with { Parents = [inferredParent] };
        }

        return result;
    }

    private static void AppendGraphCommits(
        IReadOnlyList<GitCommitInfo> graphSource,
        IReadOnlyList<GitCommitInfo> displaySource,
        ICollection<GitCommitInfo> graphTarget,
        ICollection<GitCommitInfo?> displayTarget)
    {
        for (var i = 0; i < graphSource.Count; i++)
        {
            graphTarget.Add(graphSource[i]);
            displayTarget.Add(displaySource[i]);
        }
    }

    /// <summary>上游分支的语义色键(上游通常是远端分支 → GraphRemote*;分支列表里找不到时
    /// 回退当前分支色)。传入的更改行使用。</summary>
    public static string UpstreamColorKey(string? upstream, IReadOnlyList<GitBranchInfo>? branches)
    {
        var key = LocalColorKey;
        if (branches is { Count: > 0 } && !string.IsNullOrWhiteSpace(upstream))
        {
            var match = branches.FirstOrDefault(branch => string.Equals(branch.Name, upstream, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                key = match.IsCurrent ? LocalColorKey
                    : match.IsRemote ? $"GraphRemote{(StableHash(match.Name) % 6) + 1}Brush"
                    : $"GraphLane{(StableHash(match.Name) % 6) + 1}Brush";
            }
        }

        return key;
    }

    // ===== 传入 / 传出真实提交集（由 RebuildLogRows 排到对应同步边界之后） =====

    public BulkObservableCollection<GitLogRow> OutgoingCommits { get; } = [];

    public BulkObservableCollection<GitLogRow> IncomingCommits { get; } = [];

    public bool HasOutgoing => OutgoingCommits.Count > 0;
    public bool HasIncoming => IncomingCommits.Count > 0;

    /// <summary>G7 懒加载门:整段历史(日志 + 传入/传出)是否已由最近的
    /// <see cref="LoadStateAsync"/> 加载(图视图折叠时整段跳过,展开后由
    /// <see cref="EnsureLazySectionsLoadedAsync"/> 补载)。</summary>
    private bool _logSectionLoaded;

    /// <summary>G7 懒加载门:贮藏列表是否已加载(更改视图折叠时跳过)。</summary>
    private bool _stashesLoaded;

    /// <summary>G7 懒加载 single-flight:至少一个补载在途时不再重复发起。</summary>
    private int _lazySectionsRunning;

    /// <summary>G7 全量加载在途标记:LoadStateAsync 期间的展开切换不应触发补载,避免与新全量
    /// 加载并发重复拉取 log / 传入传出。</summary>
    private int _fullLoadRunning;

    private async Task<IReadOnlyList<GitCommitInfo>> LoadLogSectionAsync(string path, CancellationToken cancellationToken)
    {
        if (!IsGraphViewExpanded)
        {
            return []; // G7: 图分区折叠 → 本次全量加载不发 log 子进程,展开后懒加载。
        }

        try
        {
            return await _gitService.GetLogAsync(path, LogPageSize, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 全新 init 且尚无提交的仓库:git log 报 "no commits yet" —— 按空历史处理。
            // 其它区段读取失败也只影响该区段；不能让并发的其它区段任务变成未观察异常。
            LogService.Write("WARNING", $"加载提交历史失败：{ex.Message}");
            return [];
        }
    }

    /// <summary>并发拉取传入/传出提交(G3:两个进程并行,不再串行 await);上游为空或图分区
    /// 折叠时不发进程(G7),返回空结果。</summary>
    private async Task<(IReadOnlyList<GitLogRow> Outgoing, IReadOnlyList<GitLogRow> Incoming)> FetchRemoteCommitsAsync(
        string path,
        string? upstream,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(upstream) || !IsGraphViewExpanded)
        {
            return ([], []);
        }

        try
        {
            var outgoingTask = _gitService.GetOutgoingCommitsAsync(path, upstream, LogPageSize, cancellationToken);
            var incomingTask = _gitService.GetIncomingCommitsAsync(path, upstream, LogPageSize, cancellationToken);
            await Task.WhenAll(outgoingTask, incomingTask);
            var outgoing = (await outgoingTask).Select(commit => new GitLogRow(commit, null)).ToArray();
            var incoming = (await incomingTask).Select(commit => new GitLogRow(commit, null)).ToArray();
            return (outgoing, incoming);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Write("WARNING", $"加载传入/传出提交失败：{ex.Message}");
            return ([], []);
        }
    }

    /// <summary>分区展开后的懒加载:图视图/更改视图从折叠转为展开时,补齐跳过的
    /// log / 传入传出 / stash(G7)。single-flight 防止与全量加载并发双载。</summary>
    private async Task EnsureLazySectionsLoadedAsync()
    {
        if (!IsRepository || Volatile.Read(ref _fullLoadRunning) == 1)
        {
            return; // 全量加载在途时由 LoadStateAsync 负责,展开切换不补载
        }

        if (Interlocked.CompareExchange(ref _lazySectionsRunning, 1, 0) != 0)
        {
            return;
        }

        await _repositoryContextGate.WaitAsync();
        try
        {
            var context = _workspaceContext;
            var path = RepositoryPath;
            if (!IsRepositoryContextCurrent(context, path) || !IsRepository
                || Volatile.Read(ref _fullLoadRunning) == 1) return;

            if (IsGraphViewExpanded && !_logSectionLoaded)
            {
                await LoadLogAndRemoteSectionsLazyAsync(path, context);
                if (!IsRepositoryContextCurrent(context, path)) return;
                _logSectionLoaded = true;
            }

            if (IsChangesViewExpanded && !_stashesLoaded)
            {
                var stashes = await _gitService.GetStashesAsync(path);
                if (!IsRepositoryContextCurrent(context, path)) return;
                SyncCollection(Stashes, stashes);
                _stashesLoaded = true;
            }
        }
        catch (OperationCanceledException)
        {
            // A context switch or shutdown can invalidate a lazy request while the settings/view
            // event is still unwinding. There is no user operation to report this cancellation to.
        }
        catch (Exception ex)
        {
            // Expansion is started from PropertyChanged and therefore is fire-and-forget. Keep a
            // failed git command observable in Output without allowing an async-void-equivalent
            // continuation to reach the dispatcher as an unhandled exception. The section stays
            // marked unloaded so collapsing/reopening it can retry.
            LogService.Write("WARNING", $"加载 Git 懒加载区段失败：{ex.Message}");
        }
        finally
        {
            _repositoryContextGate.Release();
            Interlocked.Exchange(ref _lazySectionsRunning, 0);
        }
    }

    private async Task LoadLogAndRemoteSectionsLazyAsync(string path, ProjectWorkspaceContext? context)
    {
        var commits = await LoadLogSectionAsync(path, CancellationToken.None);
        if (!IsRepositoryContextCurrent(context, path)) return;
        var logsChanged = SyncCollection(Logs, commits);
        _logsExhausted = Logs.Count < LogPageSize;
        if (logsChanged)
        {
            RebuildLogRows();
            OnPropertyChanged(nameof(GraphViewBadgeCount));
            LoadMoreCommitsCommand.NotifyCanExecuteChanged();
        }

        var (outgoing, incoming) = await FetchRemoteCommitsAsync(path, UpstreamName, CancellationToken.None);
        if (!IsRepositoryContextCurrent(context, path)) return;
        var outgoingChanged = SyncCollection(OutgoingCommits, outgoing);
        var incomingChanged = SyncCollection(IncomingCommits, incoming);
        if (outgoingChanged || incomingChanged)
        {
            OnPropertyChanged(nameof(HasOutgoing));
            OnPropertyChanged(nameof(HasIncoming));
            RebuildLogRows();
        }
    }

    /// <summary>当前分支是否有上游。</summary>
    [ObservableProperty] private bool hasUpstream;

    /// <summary>上游短名(如 origin/main),显示在传入同步边界旁。</summary>
    [ObservableProperty] private string upstreamName = string.Empty;

    /// <summary>上次重建行集合时的同步签名(上游|ahead|behind):变化才重建,避免静默刷新闪烁。</summary>
    private string _lastSyncSignature = string.Empty;

    // ===== 贮藏(VS Code Stash) =====

    public BulkObservableCollection<GitStashInfo> Stashes { get; } = [];

    /// <summary>贮藏当前全部更改(含未跟踪文件)。</summary>
    [RelayCommand(CanExecute = nameof(CanStashAllChanges))]
    private async Task StashAllChangesAsync()
    {
        await RunRepositoryAsync("贮藏全部更改", async cancellationToken =>
        {
            await _gitService.StashAsync(RepositoryPath, cancellationToken);
            await LoadStateAsync(cancellationToken);
        }, canCancel: true);
        RefreshChangeCommands();
    }

    private bool CanStashAllChanges() => IsRepository && ChangeCount > 0;

    /// <summary>应用并移除一条贮藏。</summary>
    [RelayCommand(CanExecute = nameof(CanStashCommandTarget))]
    private async Task PopStashAsync(GitStashInfo? stash)
    {
        if (stash is null)
        {
            return;
        }

        await RunRepositoryAsync($"应用贮藏 {stash.Index}", async cancellationToken =>
        {
            await _gitService.PopStashAsync(RepositoryPath, stash.Index, cancellationToken);
            await LoadStateAsync(cancellationToken);
        }, canCancel: true);
        RefreshChangeCommands();
    }

    /// <summary>丢弃一条贮藏(需确认)。</summary>
    [RelayCommand(CanExecute = nameof(CanStashCommandTarget))]
    private async Task DropStashAsync(GitStashInfo? stash)
    {
        if (stash is null)
        {
            return;
        }

        if (!_confirmationService.Confirm("确认丢弃贮藏", $"将丢弃“{stash.Message}”且无法撤销。选择“否”可安全取消。"))
        {
            return;
        }

        await RunRepositoryAsync($"丢弃贮藏 {stash.Index}", async cancellationToken =>
        {
            await _gitService.DropStashAsync(RepositoryPath, stash.Index, cancellationToken);
            await LoadStateAsync(cancellationToken);
        }, canCancel: true);
        RefreshChangeCommands();
    }

    private bool CanStashCommandTarget(GitStashInfo? stash) => stash is not null;

    /// <summary>清空提交区的分支 / 同步状态(目录无效或不是仓库时)。</summary>
    private void ResetBranchStatus()
    {
        CurrentBranch = string.Empty;
        AheadCount = 0;
        BehindCount = 0;
    }

    private static string BuildRepositorySummary(GitRepositoryStatus status)
    {
        var parts = new List<string> { status.Branch ?? "(游离 HEAD)" };
        if (status.AheadCount > 0)
        {
            parts.Add($"领先 {status.AheadCount}");
        }

        if (status.BehindCount > 0)
        {
            parts.Add($"落后 {status.BehindCount}");
        }

        if (!string.IsNullOrWhiteSpace(status.Upstream))
        {
            parts.Add(status.Upstream);
        }

        return string.Join(" · ", parts);
    }

    private async Task BindScopedSettingsAsync()
    {
        var context = _workspaceContext ?? _workspaceService.Current;
        var session = await _scopedSettings.OpenSessionAsync(new(context?.ProjectPath),
            [BuiltInSettingsCatalog.GitAutoRefresh.Id]);
        if (!ReferenceEquals(_workspaceContext, context)
            || !ReferenceEquals(_workspaceService.Current, context))
        {
            await session.DisposeAsync();
            return;
        }

        var previous = _settingsSession;
        _settingsSession = session;
        if (previous is not null) await previous.DisposeAsync();
        session.Changed += (_, _) => ApplyScopedSettingsSafely(session);
        ApplyScopedSettingsSafely(session);
    }

    private void ApplyScopedSettingsSafely(ISettingsSession session)
    {
        if (session.Current is not { } snapshot)
        {
            return;
        }

        try
        {
            ApplyScopedSettings(snapshot);
        }
        catch (Exception ex)
        {
            LogService.Write("WARNING", $"Git 设置应用失败：{ex.Message}");
        }
    }

    private void ApplyScopedSettings(SettingsSnapshot snapshot)
    {
        if (!IsSettingsContextCurrent(snapshot.Context)) return;
        if (!_settingsRevisionGate.TryAccept(snapshot)) return;
        _enableScmAutoRefresh = snapshot.Effective(BuiltInSettingsCatalog.GitAutoRefresh);
        if (_enableScmAutoRefresh && IsRepository) _repositoryWatcher.Attach(RepositoryPath);
        else _repositoryWatcher.Detach();
    }

    private async Task ApplyWorkspaceContextAsync(ProjectWorkspaceContext? context)
    {
        await _repositoryContextGate.WaitAsync();
        try
        {
            _workspaceContext = context;
            HasWorkspace = context is not null;
            if (context?.GitRepositoryPath is not { Length: > 0 } repositoryPath)
            {
                RepositoryPath = string.Empty;
                _repositoryWatcher.Detach();
                _hasFullStateLoad = false;
                IsRepository = false;
                RepositorySummary = context is null ? "未打开项目。" : "当前项目未关联 Git 仓库。";
                ResetBranchStatus();
                ClearChanges();
                StatusRefreshed?.Invoke(this, GitRepositoryStatus.NotARepository);
                return;
            }

            var repositoryChanged = !PathsEqual(RepositoryPath, repositoryPath);
            RepositoryPath = repositoryPath;
            // Re-activating the same repository creates a new workspace generation. Do not let
            // equal Git rows/logs from the old generation suppress the first load of the new one.
            if (!repositoryChanged)
            {
                ClearChanges();
            }

            await LoadStateAsync();
        }
        finally
        {
            _repositoryContextGate.Release();
        }
    }

    private bool CanStageAll() => IsRepository && UnstagedChanges.Count > 0;
    private bool CanInitializeRepository() => HasWorkspace && !IsRepository;
    private bool CanUnstageAll() => IsRepository && StagedChanges.Count > 0;
    private bool CanDiscardAll() => IsRepository && (StagedChanges.Count > 0 || UnstagedChanges.Count > 0);

    // Per-row buttons decide their own availability from the row item, not from the current
    // selection, so 暂存/取消/丢弃 work no matter which row is highlighted.
    private bool CanStageChange(GitChangeItem? item) => IsRepository && item is { HasUnstagedChanges: true };
    private bool CanUnstageChange(GitChangeItem? item) => IsRepository && item is { IsStaged: true };
    // 丢弃只允许作用于未暂存内容:已暂存更改必须取消暂存后才能丢弃。
    private bool CanDiscardChange(GitChangeItem? item) => IsRepository && item is { HasUnstagedChanges: true };
    private bool CanCreateBranch() => IsRepository && !string.IsNullOrWhiteSpace(NewBranchName);
}

/// <summary>UI wrapper for a git status change, exposing display text and the status badge letter.
/// Besides the full path, the row layout asks for the file name, the parent directory and the
/// rename/copy decoration separately (pure display derivations — the underlying
/// <see cref="GitFileChange"/> model is untouched). A partially staged change can appear in both
/// sections, so <see cref="IsStagedSection"/> keeps the row's display/diff side explicit.</summary>
public sealed record GitChangeItem(GitFileChange Change, bool? IsStagedSection = null)
{
    private static readonly char[] PathSeparators = [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar];

    public string Path => Change.Path;

    public string OriginalPath => Change.OriginalPath ?? string.Empty;

    public string DisplayPath => Change.IndexStatus == GitChangeStatus.Renamed || Change.WorkTreeStatus == GitChangeStatus.Renamed
        ? $"{Change.Path}（原路径：{Change.OriginalPath}）"
        : Change.Path;

    /// <summary>File-name part of the path, e.g. "A.cs" for "src/A.cs" (VS Code resource label).</summary>
    public string FileName
    {
        get
        {
            var index = Path.LastIndexOfAny(PathSeparators);
            return index >= 0 ? Path[(index + 1)..] : Path;
        }
    }

    /// <summary>Parent directory of the path relative to the repository root, e.g. "src" for
    /// "src/A.cs"; empty when the change lives at the repository root.</summary>
    public string RelativeDirectory
    {
        get
        {
            var index = Path.LastIndexOfAny(PathSeparators);
            return index > 0 ? Path[..index] : string.Empty;
        }
    }

    /// <summary>Rename/copy decoration shown next to the file name; empty for
    /// plain changes (VS Code shows the previous name on renames).</summary>
    public string RenameSuffix =>
        (Change.IndexStatus is GitChangeStatus.Renamed or GitChangeStatus.Copied ||
         Change.WorkTreeStatus is GitChangeStatus.Renamed or GitChangeStatus.Copied)
        && !string.IsNullOrWhiteSpace(Change.OriginalPath)
            ? Change.OriginalPath
            : string.Empty;

    public string RenameSuffixGlyph => string.IsNullOrEmpty(RenameSuffix) ? string.Empty : Codicons.ArrowLeft;

    /// <summary>Whether this row represents the index side. Null preserves the underlying Git
    /// status semantics for callers that construct a standalone item.</summary>
    public bool IsStaged => IsStagedSection ?? Change.IsStaged;

    /// <summary>Whether the worktree side still has changes. A partially staged file has both
    /// index and worktree changes and therefore appears in both SCM sections.</summary>
    public bool HasUnstagedChanges =>
        Change.WorkTreeStatus != GitChangeStatus.Unmodified || Change.IsUntracked || Change.IsUnmerged;

    public bool IsUntracked => Change.IsUntracked;

    public bool IsUnmerged => Change.IsUnmerged;

    /// <summary>Badge letter for the side represented by this row. A partially staged AD file is
    /// therefore A in the staged section and D in the unstaged section. Untracked files are shown
    /// as green A instead of '?'.</summary>
    public string StatusLetter => IsStaged
        ? Change.IndexStatus.ToStatusLetter().ToString()
        : Change.IsUntracked
            ? "A"
            : Change.WorkTreeStatus.ToStatusLetter().ToString();
}

/// <summary>源代码管理侧栏更改列表的布局(VS Code "视图: 平铺/树状")。</summary>
public enum ScmLayout
{
    /// <summary>平铺:按路径排序的文件行。</summary>
    List,

    /// <summary>树状:按目录分组,文件夹行可折叠。</summary>
    Tree,
}

/// <summary>树状布局下的一行(文件夹或文件)。扁平序列渲染,Depth 驱动缩进(每层 12px)。
/// 层级导线:每层图标列中心落在 x = 12k + 9(1px 竖线左缘为 12k + 8.5);子行把全部祖先竖线
/// 重画到自身整行高度,使上层折叠图标下的竖线连续延伸到整棵子树;文件夹行自身不在折叠图标列
/// 画线——该列竖线由第一个子行起接续,折叠按钮区保持干净。</summary>
public abstract record ScmRowNode(int Depth)
{
    /// <summary>文件夹行标记(XAML 容器样式用于取消选中高亮)。</summary>
    public bool IsFolder => this is ScmFolderNode;

    /// <summary>行缩进(VS Code 树状视图节奏)。</summary>
    public System.Windows.Thickness IndentMargin => new(12 * Depth, 0, 0, 0);

    /// <summary>祖先层级竖线的左缘 x(第 k 层列中心 12k + 9,1px 宽 → 左缘 12k + 8.5)。
    /// 子行把这些线重画到自身整行高度,保证上层竖线穿过所有后代行,不会在行边界断开。</summary>
    public double[] AncestorGuideLefts { get; internal set; } = [];

    /// <summary>深度大于 0 的行位于某个文件夹子树内。</summary>
    public bool HasParent => Depth > 0;
}

/// <summary>树状布局的文件夹行;点击整行切换折叠。</summary>
public sealed record ScmFolderNode(string Name, string FolderPath, bool IsCollapsed, int Depth) : ScmRowNode(Depth);

/// <summary>树状布局的文件行,包住平铺布局共用的 <see cref="GitChangeItem"/>。</summary>
public sealed record ScmFileNode(GitChangeItem Change, int Depth) : ScmRowNode(Depth)
{
    public string FileName => Change.FileName;
}

/// <summary>历史列表一行(折叠栏):提交 + 左缘图形泳道描述 + 展开状态与懒加载的文件清单。
/// 值相等按 <see cref="Commit"/> 判定,静默刷新时由 <see cref="GitViewModel"/> 保持实例,
/// 展开状态与已加载文件随之存活。Graph 为 null 表示该列表不带泳道图(传入/传出)。</summary>
public sealed partial class GitLogRow : ObservableObject
{
    public GitLogRow(GitCommitInfo commit, GitGraphRow? graph, string? directionGlyph = null, string? syncTarget = null)
    {
        Commit = commit;
        Graph = graph;
        DirectionGlyph = directionGlyph;
        SyncTarget = syncTarget;

        // 提交引用转成徽标(名称 + 图标字形 + 分支色键):本地分支用 git-branch、远端分支用
        // cloud、标签用 pin,三类引用在提交折叠栏和悬浮窗中共用同一套徽标模板。
        // 徽标颜色与图形泳道同源:当前分支 = GraphCurrentBranchBrush,远端 = GraphRemoteN,
        // 其余本地分支 = GraphLaneN(N = StableHash(name) % 6 + 1)。
        var badges = new List<GitLogRefBadge>();
        foreach (var reference in commit.RefList)
        {
            var glyph = reference.Kind switch
            {
                GitRefKind.RemoteBranch => Codicons.Cloud,
                GitRefKind.Tag => Codicons.Tag,
                _ => Codicons.GitBranch
            };
            var colorKey = reference.IsHead
                ? "GraphCurrentBranchBrush"
                : reference.Kind == GitRefKind.RemoteBranch
                    ? $"GraphRemote{(GitViewModel.StableHash(reference.Name) % 6) + 1}Brush"
                    : reference.Kind == GitRefKind.Tag
                        ? "InfoAccentBrush"
                        : $"GraphLane{(GitViewModel.StableHash(reference.Name) % 6) + 1}Brush";
            badges.Add(new GitLogRefBadge(reference.Name, glyph, colorKey, reference.Kind));
        }

        RefBadges = badges;
    }

    public GitCommitInfo Commit { get; }

    public GitGraphRow? Graph { get; }

    /// <summary>Codicon direction marker used by incoming/outgoing synchronization rows.</summary>
    public string? DirectionGlyph { get; }

    /// <summary>同步边界对应的本地/上游分支名；普通提交行为 null。</summary>
    public string? SyncTarget { get; }

    public bool IsSyncBoundary => !string.IsNullOrWhiteSpace(SyncTarget);

    /// <summary>折叠栏是否展开(展开后显示该提交的更改文件)。</summary>
    [ObservableProperty] private bool isExpanded;

    /// <summary>文件清单是否已加载(<see cref="Files"/> 已就绪,再次展开不再请求)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoFiles))]
    private bool isLoaded;

    /// <summary>首次展开后正在加载文件清单。</summary>
    [ObservableProperty] private bool isLoading;

    /// <summary>该提交行内联的更改文件(懒加载,每个提交只加载一次)。</summary>
    public ObservableCollection<LogFileRow> Files { get; } = [];

    /// <summary>已加载且没有文件更改(空提交 / 根提交)时显示"无文件更改"占位。</summary>
    public bool HasNoFiles => IsLoaded && Files.Count == 0;

    /// <summary>指向该提交的分支徽标(本地分支 = git-branch 字形,远端分支 = cloud 字形;
    /// 无引用时为空,行与悬浮窗都不渲染)。</summary>
    public IReadOnlyList<GitLogRefBadge> RefBadges { get; }

    /// <summary>该提交是否有可渲染的引用徽标。</summary>
    public bool HasRefBadges => RefBadges.Count > 0;

    /// <summary>纯文本形式的提交详情(保留给既有单测;视图已改用结构化主题悬浮窗渲染)。</summary>
    public string ToolTipText
    {
        get
        {
            return $"提交: {Commit.ShortHash} ({Commit.Hash})\n提交人: {Commit.AuthorName} <{Commit.AuthorEmail}>\n提交时间: {Commit.AuthorDate:yyyy-MM-dd HH:mm}（距今 {CommitAgeText}）\n{FullMessage}";
        }
    }

    /// <summary>提交信息全文(主题 + 正文),交悬浮窗的 markdown 渲染器使用。</summary>
    public string FullMessage =>
        !string.IsNullOrWhiteSpace(Commit.Message)
            ? Commit.Message.TrimEnd()
            : string.IsNullOrWhiteSpace(Commit.Body) ? Commit.Subject : Commit.Subject + "\n\n" + Commit.Body;

    /// <summary>提交时间相对当前本地时间的间隔,用于悬浮窗中的人类可读时间提示。</summary>
    public string CommitAgeText => FormatCommitAge(Commit.AuthorDate, DateTimeOffset.Now);

    /// <summary>把提交时间与本地当前时间的差值格式化为中文相对时间。</summary>
    public static string FormatCommitAge(DateTimeOffset commitDate, DateTimeOffset localNow)
    {
        var elapsed = localNow.ToLocalTime() - commitDate.ToLocalTime();
        if (elapsed < TimeSpan.Zero)
        {
            var future = elapsed.Duration();
            return future < TimeSpan.FromMinutes(1)
                ? "即将发生"
                : $"{FormatLargestUnit(future)}后";
        }

        if (elapsed < TimeSpan.FromMinutes(1)) return "刚刚";
        if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes} 分钟前";
        if (elapsed < TimeSpan.FromDays(1)) return $"{(int)elapsed.TotalHours} 小时前";
        if (elapsed < TimeSpan.FromDays(30)) return $"{(int)elapsed.TotalDays} 天前";
        return elapsed < TimeSpan.FromDays(365)
            ? $"{(int)(elapsed.TotalDays / 30)} 个月前"
            : $"{(int)(elapsed.TotalDays / 365)} 年前";
    }

    private static string FormatLargestUnit(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)elapsed.TotalMinutes)} 分钟";
        if (elapsed < TimeSpan.FromDays(1)) return $"{Math.Max(1, (int)elapsed.TotalHours)} 小时";
        if (elapsed < TimeSpan.FromDays(30)) return $"{Math.Max(1, (int)elapsed.TotalDays)} 天";
        return elapsed < TimeSpan.FromDays(365)
            ? $"{Math.Max(1, (int)(elapsed.TotalDays / 30))} 个月"
            : $"{Math.Max(1, (int)(elapsed.TotalDays / 365))} 年";
    }

    /// <summary>提交变更统计(文件/新增/删除行;无 <c>--shortstat</c> 数据时为空)。</summary>
    public GitCommitStats? Stats => Commit.Stats;

    /// <summary>统计段是否可显示(有任一非零计数;悬浮窗 +/− 行)。</summary>
    public bool HasStats => Stats is { } stats
        && (stats.FilesChanged > 0 || stats.Insertions > 0 || stats.Deletions > 0);

    /// <summary>同一提交的两行视为同一行(提交哈希唯一标识)。</summary>
    public override bool Equals(object? obj) => obj is GitLogRow other && Commit.Equals(other.Commit);

    public override int GetHashCode() => Commit.GetHashCode();
}

/// <summary>提交行/悬浮窗里的一个引用徽标:显示名 + 图标字形(本地分支 git-branch,
/// 远端分支 cloud,标签 tag)+ 分支色键(主题令牌名,与图形泳道同源,随主题换肤)+ 引用类型,
/// 用于让"标签操作"仅对标签徽标生效。</summary>
public sealed record GitLogRefBadge(string Name, string Glyph, string ColorKey, GitRefKind Kind)
{
    public bool IsTag => Kind == GitRefKind.Tag;
}

/// <summary>右键子菜单里一项可执行操作(标题 + 命令 + 目标标签名)。TagName 随条目携带:
/// 嵌套弹出层内的项无法用 RelativeSource AncestorType 跨 Popup 查找父项取参,祖先查找
/// 解析为 null 会让命令静默无操作。</summary>
public sealed record GitMenuCommandItem(string Header, System.Windows.Input.ICommand Command, string TagName);

/// <summary>「选中提交的标签」子菜单中的一个标签节点(标签名 + 它的全部操作)。</summary>
public sealed record GitTagMenuNode(string TagName, IReadOnlyList<GitMenuCommandItem> Actions);

/// <summary>折叠栏内联的一个更改文件:包住 <see cref="GitFileChange"/> 并提供显示派生与所属行
/// (行携带提交哈希,点击文件即可打开该提交的 diff)。</summary>
public sealed record LogFileRow(GitLogRow Row, GitFileChange Change)
{
    public string Path => Change.Path;

    /// <summary>重命名/复制时显示"新路径 ← 原名"。</summary>
    public string DisplayPath => Change.OriginalPath is { Length: > 0 }
        ? $"{Change.Path}（原路径：{Change.OriginalPath}）"
        : Change.Path;

    /// <summary>索引状态字母(文件行右侧徽标,复用 ScmStatusLetterStyle 着色)。</summary>
    public string StatusLetter => Change.IndexStatus.ToStatusLetter().ToString();
}

/// <summary>一行提交图形:圆点所在泳道、贯穿的竖线泳道、从圆点指向各父提交泳道的连线、
/// 圆点泳道是否从上方延续(<see langword="false"/> = 分支起点,线从本圆点才开始),
/// 以及需要在本行汇入圆点泳道的"侧线"(原占位首父的泳道,竖线画到行中后折线汇入主线)。
/// LaneColorKeys 为本行泳道向下的色键(圆点与下半段线,与分支语义关联);LaneIncomingColorKeys
/// 为从上方接入本行的入色(= 上一行的 LaneColorKeys,首行与自身相同)。分段着色的颜色切换精确
/// 落在圆点上:圆点以上半段画入色、圆点及以下画出色,相邻两个圆点之间的连线全程单色。
/// DotHollow 为空心圆点(分支顶端的同步节点,上游 tip 位置,点击展开传入/传出提交)。
/// 二者为 null(无分支数据)时单元格回退 lane %6。</summary>
public sealed record GitGraphRow(
    int DotLane,
    IReadOnlyList<int> PassLanes,
    IReadOnlyList<GitGraphLink> Links,
    int LaneCount,
    bool DotLaneContinuesFromAbove = false,
    IReadOnlyList<int>? MergeFromLanes = null,
    IReadOnlyList<string>? LaneColorKeys = null,
    IReadOnlyList<string>? LaneIncomingColorKeys = null,
    bool DotHollow = false,
    bool DotDashed = false);

/// <summary>圆点泳道到父提交泳道的连线(同泳道为直线,异泳道为分叉曲线)。</summary>
public sealed record GitGraphLink(int FromLane, int ToLane);
