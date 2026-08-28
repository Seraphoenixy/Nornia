using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nornia.Core.Collections;
using Nornia.Core.Coalescing;
using Nornia.Desktop.Code;
using Nornia.Desktop.Configuration;
using Nornia.Desktop.Services;

namespace Nornia.Desktop.ViewModels;

public enum SearchTreeNodeKind
{
    Workspace,
    Folder,
    File,
    Match,
}

public enum SearchResultsViewMode
{
    Tree,
    List,
}

public sealed class SearchTreeNode : ObservableObject
{
    private bool _isExpanded = true;
    private double[]? _ancestorGuideLefts;
    private IReadOnlyList<SearchPreviewSegment>? _previewSegments;

    public SearchTreeNode(SearchTreeNodeKind kind, string name, string relativePath, string? fullPath = null,
        SearchTreeNode? parent = null, int depth = 0, bool isListResult = false)
    {
        Kind = kind;
        Name = name;
        RelativePath = relativePath;
        FullPath = fullPath;
        Parent = parent;
        Depth = depth;
        IsListResult = isListResult;
        Indent = new Thickness(depth * 12, 0, 0, 0);
    }

    public SearchTreeNodeKind Kind { get; }
    public string Name { get; }
    public string RelativePath { get; }
    public string? FullPath { get; }
    public SearchTreeNode? Parent { get; }
    public int Depth { get; }
    public Thickness Indent { get; }
    public ObservableCollection<SearchTreeNode> Children { get; } = [];
    public WorkspaceSearchMatch? Match { get; }
    public bool IsListResult { get; }
    /// <summary>Same 12px rhythm and 8.5px guide left used by Explorer's flattened tree rows.
    /// 缓存:旧实现每次绑定求值都新分配数组(每个可见匹配行 × 每帧重绑定)。</summary>
    public double[] AncestorGuideLefts => _ancestorGuideLefts ??=
        Depth switch
        {
            0 => [],
            _ => Enumerable.Range(0, Depth).Select(level => 12.0 * level + 8.5).ToArray()
        };
    /// <summary>预览高亮分段;匹配数据创建后不变,缓存避免每次绑定重建分段列表。</summary>
    public IReadOnlyList<SearchPreviewSegment> PreviewSegments =>
        _previewSegments ??= BuildPreviewSegments(Match?.Preview, Match?.Column, Match?.Length);

    // S10: the match row renders its preview as one TextBlock with at most three Runs
    // (before / match / after) instead of an ItemsControl of per-segment TextBlocks. The
    // logical parts are derived from the same immutable PreviewSegments source and cached;
    // the segment order produced by BuildPreviewSegments is [before?, match, after?], so the
    // match segment is always the middle one when all three parts are present.
    private (bool HasBefore, string Before, bool HasMatch, string Match, bool HasAfter, string After)? _previewParts;

    private (bool HasBefore, string Before, bool HasMatch, string Match, bool HasAfter, string After) PreviewParts
        => _previewParts ??= ComputePreviewParts(PreviewSegments);

    public bool HasPreviewBefore => PreviewParts.HasBefore;
    public string PreviewBeforeText => PreviewParts.Before;
    public bool HasPreviewMatch => PreviewParts.HasMatch;
    public string PreviewMatchText => PreviewParts.Match;
    public bool HasPreviewAfter => PreviewParts.HasAfter;
    public string PreviewAfterText => PreviewParts.After;

    private static (bool HasBefore, string Before, bool HasMatch, string Match, bool HasAfter, string After)
        ComputePreviewParts(IReadOnlyList<SearchPreviewSegment> segments)
    {
        if (segments.Count == 0)
        {
            return (false, string.Empty, false, string.Empty, false, string.Empty);
        }

        var first = segments[0];
        var before = first.IsMatch ? (false, string.Empty) : (true, first.Text);
        var last = segments[^1];
        var after = last.IsMatch ? (false, string.Empty) : (true, last.Text);
        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i].IsMatch)
            {
                return (before.Item1, before.Item2, true, segments[i].Text, after.Item1, after.Item2);
            }
        }

        return (before.Item1, before.Item2, false, string.Empty, after.Item1, after.Item2);
    }

    /// <summary>Single-line primary label. Match rows use the compact location label; the file
    /// path is intentionally not repeated in list-view match rows.</summary>
    public string DisplayName => Kind == SearchTreeNodeKind.Match ? LocationLabel
        : IsListResult && Kind == SearchTreeNodeKind.File ? RelativePath : Name;
    /// <summary>1-based line and column in the same compact form used by the search result row.</summary>
    public string LocationLabel => Match is null ? string.Empty : $"{Match.Line}:{Match.Column}";
    /// <summary>Full path context remains available without consuming a second visual row.</summary>
    public string ToolTipText => IsMatch
        ? $"{RelativePath}:{LocationLabel}\n{Match?.Preview}"
        : RelativePath;
    public int MatchCount { get; private set; }
    public bool ShowsMatchBadge => Kind is SearchTreeNodeKind.Folder or SearchTreeNodeKind.File;
    public bool ShowsGlyph => Kind is SearchTreeNodeKind.File or SearchTreeNodeKind.Match;
    public bool HasChildren => Children.Count > 0;
    // Search rows use one fixed prefix: 18px expander + 20px square icon slot. Folders leave
    // the icon slot empty, while files and match rows render their icon there.
    // The slot stays after the expander for every row, so nested match rows advance by the same
    // 12px tree step as their parent file/folder rows.
    public bool GlyphInExpanderCell => false;
    public string Glyph => Kind switch
    {
        SearchTreeNodeKind.Workspace or SearchTreeNodeKind.Folder => Codicons.Folder,
        SearchTreeNodeKind.File => Codicons.File,
        _ => Codicons.GoToFile,
    };
    public bool IsMatch => Kind == SearchTreeNodeKind.Match;
    public bool IsFile => Kind == SearchTreeNodeKind.File;
    public bool IsContainer => Kind != SearchTreeNodeKind.Match;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public SearchTreeNode(SearchTreeNodeKind kind, string name, string relativePath,
        WorkspaceSearchMatch match, string fullPath, SearchTreeNode? parent, int depth, bool isListResult = false)
        : this(kind, name, relativePath, fullPath, parent, depth, isListResult)
    {
        Match = match;
        IsExpanded = false;
    }

    public void AddChild(SearchTreeNode child)
    {
        Children.Add(child);
        IncrementMatchCount(child.Kind == SearchTreeNodeKind.Match ? 1 : child.MatchCount);
        OnPropertyChanged(nameof(HasChildren));
    }

    /// <summary>Removes a child and walks the match counters back down the parent chain (S6:
    /// refinement prunes stale file nodes without tearing down the whole tree).</summary>
    public void RemoveChild(SearchTreeNode child)
    {
        if (!Children.Remove(child)) return;
        DecrementMatchCount(child.Kind == SearchTreeNodeKind.Match ? 1 : child.MatchCount);
        OnPropertyChanged(nameof(HasChildren));
    }

    private void IncrementMatchCount(int count)
    {
        if (count <= 0) return;
        MatchCount += count;
        OnPropertyChanged(nameof(MatchCount));
        Parent?.IncrementMatchCount(count);
    }

    private void DecrementMatchCount(int count)
    {
        if (count <= 0) return;
        MatchCount = Math.Max(0, MatchCount - count);
        OnPropertyChanged(nameof(MatchCount));
        Parent?.DecrementMatchCount(count);
    }

    private static IReadOnlyList<SearchPreviewSegment> BuildPreviewSegments(string? preview, int? column, int? length)
    {
        if (string.IsNullOrEmpty(preview) || column is null || length is null || length <= 0)
            return Array.Empty<SearchPreviewSegment>();

        var start = Math.Clamp(column.Value - 1, 0, preview.Length);
        var end = Math.Clamp(start + length.Value, start, preview.Length);
        var segments = new List<SearchPreviewSegment>(3);
        if (start > 0) segments.Add(new(preview[..start], false));
        if (end > start) segments.Add(new(preview[start..end], true));
        if (end < preview.Length) segments.Add(new(preview[end..], false));
        return segments;
    }
}

public sealed record SearchPreviewSegment(string Text, bool IsMatch);

/// <summary>VS Code-like read-only workspace search sidebar. Search state remains in memory for the
/// lifetime of the singleton VM, while each scan is cancellable and generation-gated.</summary>
public partial class SearchViewModel : PageViewModel
{
    /// <summary>V5: files batched per UI update, raised from 16 to 64 so a 10k-match search
    /// triggers ~N/64 UI passes instead of ~N/16 (each pass also gains the tail-append fast
    /// path below, O(batch) instead of O(batch×N)).</summary>
    private const int UiBatchSize = 64;

    /// <summary>S7: base debounce for search-on-type. Regex searches extend it (up to
    /// <see cref="MaxDebounceMultiplier"/>×) when the previous search's probe showed a high
    /// hits-per-file density; plain literal searches always keep the base delay.</summary>
    private const int BaseDebounceMs = 300;
    private const int MaxDebounceMultiplier = 3;
    private const double HighHitDensity = 8;   // ≥8 hits/file → 3× base
    private const double MediumHitDensity = 3; // ≥3 hits/file → 2× base

    private readonly IWorkspaceSearchService _searchService;
    private readonly IProjectWorkspaceService _workspace;
    private readonly EditorAreaViewModel _editor;
    private readonly IUiDispatcher _dispatcher;
    private readonly IApplicationStateStore? _stateStore;
    private CancellationTokenSource? _debounceCancellation;
    private CancellationTokenSource? _searchCancellation;
    private readonly Dictionary<string, bool> _listExpansion = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>列表视图文件行包装的复用缓存(按文件 FullPath);清空结果时一并重置。</summary>
    private readonly Dictionary<string, SearchTreeNode> _listFileCache = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>S9: per-level child index — parent node → (child name, OrdinalIgnoreCase) →
    /// child — so AddResults finds/creates folder and file nodes by dictionary lookup instead
    /// of a linear FirstOrDefault over the siblings at every level. Reset with the tree.</summary>
    private readonly Dictionary<SearchTreeNode, Dictionary<string, SearchTreeNode>> _childrenIndex = new();
    private int _generation;
    private SearchTreeNode? _root;
    private int _filesMatched;
    private int _matchesFound;
    private int _filesScanned;

    // S6: refinement bookkeeping — the previous query that the current tree was built from.
    private RefinementState? _refinement;
    private string? _lastQueryRoot;
    private string? _lastQueryPattern;
    private TextSearchOptions? _lastQueryOptions;
    private string _lastQueryInclude = string.Empty;
    private string _lastQueryExclude = string.Empty;
    private bool _lastQueryUseIgnoreFiles = true;

    // S7: probe state — cumulative hits-per-file of the in-flight/last search; the multiplier
    // only ever applies to the NEXT keystroke when it is a regex search.
    private bool _currentSearchUseRegex;
    private int _probeFiles;
    private long _probeMatches;
    private int _probeMultiplier = 1;
    private bool _probeWasRegex;

    /// <summary>Test/metrics: AddResults passes that used the V5 tail-append fast path.</summary>
    internal int TailAppendBatchCount { get; private set; }

    /// <summary>Test/metrics: AddResults passes that fell back to the full re-flatten.</summary>
    internal int FullRebuildCount { get; private set; }

    /// <summary>搜索进度合并器(250ms 尾部去抖):旧实现每个被扫描/匹配文件都
    /// BeginInvoke 一次 UI 更新,大搜索是上千条 UI 消息;现最多 4 次/秒
    /// (对照 VS Code 的 RunOnceScheduler 进度合帧)。载荷携带发起时的代次,
    /// 防止被取代的旧搜索的延迟合并帧用新代次发出旧值。</summary>
    private readonly DebouncedCoalescer<(int Generation, WorkspaceSearchProgress Value)> _progressCoalescer = new(250);

    public SearchViewModel(
        IWorkspaceSearchService searchService,
        IProjectWorkspaceService workspace,
        EditorAreaViewModel editor,
        IUiLogService logService)
        : this(searchService, workspace, editor, logService, new WpfUiDispatcher(), null)
    {
    }

    public SearchViewModel(
        IWorkspaceSearchService searchService,
        IProjectWorkspaceService workspace,
        EditorAreaViewModel editor,
        IUiLogService logService,
        IUiDispatcher dispatcher)
        : this(searchService, workspace, editor, logService, dispatcher, null)
    {
    }

    public SearchViewModel(
        IWorkspaceSearchService searchService,
        IProjectWorkspaceService workspace,
        EditorAreaViewModel editor,
        IUiLogService logService,
        IUiDispatcher dispatcher,
        IApplicationStateStore? stateStore)
        : base("搜索", logService)
    {
        _searchService = searchService;
        _workspace = workspace;
        _editor = editor;
        _dispatcher = dispatcher;
        _stateStore = stateStore;
        _workspace.ContextChanged += OnWorkspaceContextChangedAsync;
        _progressCoalescer.Debounced += payload =>
        {
            var (generation, value) = payload;
            void Publish()
            {
                if (generation != _generation) return;
                _filesScanned = value.FilesScanned;
                _filesMatched = value.FilesMatched;
                _matchesFound = value.MatchCount;
                IsTruncated = value.IsTruncated;
                OnPropertyChanged(nameof(SearchSummary));
            }

            // Progress<T> 回调在线程池(ConfigureAwait(false) 链上无 SynchronizationContext),
            // 定时器触发时切回 UI 线程;Flush() 已在 UI 线程上则同步发布(保证完成摘要计数即时)。
            if (_dispatcher.CheckAccess())
            {
                Publish();
            }
            else
            {
                _dispatcher.BeginInvoke(Publish);
            }
        };
    }

    public override object Sidebar => this;

    public ObservableCollection<SearchTreeNode> Rows { get; } = [];

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private bool searchCaseSensitive;

    [ObservableProperty]
    private bool searchWholeWord;

    [ObservableProperty]
    private bool searchUseRegex;

    [ObservableProperty]
    private string includePattern = string.Empty;

    [ObservableProperty]
    private string excludePattern = string.Empty;

    [ObservableProperty]
    private bool useIgnoreFiles = true;

    [ObservableProperty]
    private bool showSearchDetails;

    [ObservableProperty]
    private SearchResultsViewMode resultsViewMode = SearchResultsViewMode.Tree;

    [ObservableProperty]
    private bool isSearching;

    [ObservableProperty]
    private bool isTruncated;

    [ObservableProperty]
    private string searchErrorMessage = string.Empty;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    public bool HasWorkspace => _workspace.Current is not null;
    public bool IsTreeView => ResultsViewMode == SearchResultsViewMode.Tree;
    public bool IsListView => ResultsViewMode == SearchResultsViewMode.List;
    public bool HasResults => Rows.Count > 0;
    public bool HasSearchError => !string.IsNullOrEmpty(SearchErrorMessage);
    public string SearchSummary => IsSearching
        ? $"已扫描 {_filesScanned} 个文件 · {_matchesFound} 个匹配"
        : _matchesFound == 0 ? string.Empty : $"{_filesMatched} 个文件 · {_matchesFound} 个匹配";

    public event EventHandler? FocusRequested;

    protected override async Task OnFirstActivatedAsync()
    {
        await RestoreViewModeAsync();
        await _workspace.EnsureInitializedAsync();
        OnPropertyChanged(nameof(HasWorkspace));
    }

    partial void OnSearchTextChanged(string value) => ScheduleSearch();
    partial void OnSearchCaseSensitiveChanged(bool value) => ScheduleSearch();
    partial void OnSearchWholeWordChanged(bool value) => ScheduleSearch();
    partial void OnSearchUseRegexChanged(bool value) => ScheduleSearch();
    partial void OnIncludePatternChanged(string value) => ScheduleSearch();
    partial void OnExcludePatternChanged(string value) => ScheduleSearch();
    partial void OnUseIgnoreFilesChanged(bool value) => ScheduleSearch();
    partial void OnResultsViewModeChanged(SearchResultsViewMode value)
    {
        OnPropertyChanged(nameof(IsTreeView));
        OnPropertyChanged(nameof(IsListView));
        RebuildRows();
        _ = PersistViewModeAsync(value);
    }

    private async Task RestoreViewModeAsync()
    {
        if (_stateStore is null)
        {
            return;
        }

        try
        {
            var state = await _stateStore.LoadAsync();
            if (Enum.TryParse<SearchResultsViewMode>(state.SearchResultsViewMode, ignoreCase: true, out var mode))
            {
                ResultsViewMode = mode;
            }
        }
        catch (Exception)
        {
            // View preference is best effort; search activation must remain available if the
            // state file is unavailable or contains an older value.
        }
    }

    private async Task PersistViewModeAsync(SearchResultsViewMode value)
    {
        if (_stateStore is null)
        {
            return;
        }

        try
        {
            await _stateStore.CommitAsync(new([
                new(ApplicationStateField.SearchResultsViewMode, value.ToString()),
            ]));
        }
        catch (Exception)
        {
            // View preference is best effort and must never delay a search result update.
        }
    }

    partial void OnSearchErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasSearchError));
    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(SearchSummary));
    partial void OnIsTruncatedChanged(bool value) => OnPropertyChanged(nameof(SearchSummary));

    public void FocusSearchInput() => FocusRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ToggleSearchDetails() => ShowSearchDetails = !ShowSearchDetails;

    [RelayCommand]
    private void ShowTreeView() => ResultsViewMode = SearchResultsViewMode.Tree;

    [RelayCommand]
    private void ShowListView() => ResultsViewMode = SearchResultsViewMode.List;

    [RelayCommand]
    private void CancelSearch() =>
        _searchCancellation?.Cancel();

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
        ClearResults();
    }

    [RelayCommand]
    private void ExpandAll()
    {
        _listExpansion.Clear();
        SetExpanded(_root, expanded: true, keepRootVisible: true);
        RebuildRows();
    }

    [RelayCommand]
    private void CollapseAll()
    {
        _listExpansion.Clear();
        SetExpanded(_root, expanded: false, keepRootVisible: true);
        RebuildRows();
    }

    [RelayCommand]
    private void ToggleNode(SearchTreeNode? node)
    {
        if (node is null || !node.IsContainer || !node.HasChildren) return;
        node.IsExpanded = !node.IsExpanded;
        if (node.IsListResult && node.Kind == SearchTreeNodeKind.File && node.FullPath is not null)
        {
            _listExpansion[node.FullPath] = node.IsExpanded;
        }
        RebuildRows();
    }

    [RelayCommand]
    private async Task OpenMatchAsync(SearchTreeNode? node)
    {
        if (node?.Match is not { } match || string.IsNullOrWhiteSpace(node.FullPath)) return;
        await _editor.OpenFileAtAsync(node.FullPath, match.Line, match.Column, permanent: false);
    }

    [RelayCommand]
    private async Task OpenMatchPermanentAsync(SearchTreeNode? node)
    {
        if (node?.Match is not { } match || string.IsNullOrWhiteSpace(node.FullPath)) return;
        await _editor.OpenFileAtAsync(node.FullPath, match.Line, match.Column, permanent: true);
    }

    private void ScheduleSearch()
    {
        SearchErrorMessage = SearchUseRegex
            ? TextSearchService.GetRegexError(SearchText, SearchCaseSensitive) ?? string.Empty
            : string.Empty;
        _debounceCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _debounceCancellation = cancellation;
        var generation = Interlocked.Increment(ref _generation);
        _searchCancellation?.Cancel();

        if (string.IsNullOrWhiteSpace(SearchText) || !string.IsNullOrEmpty(SearchErrorMessage))
        {
            ClearResults();
            IsSearching = false;
            return;
        }

        _ = DebounceAndSearchAsync(generation, cancellation);
    }

    private async Task DebounceAndSearchAsync(int generation, CancellationTokenSource debounce)
    {
        try
        {
            // S7: probe-based debounce scaling. The previous regex search's hits-per-file probe
            // (updated after each UI batch) extends this delay up to MaxDebounceMultiplier× the
            // base — more matches per file means a full scan is more expensive, so the next
            // keystroke waits longer. Plain literal searches always keep the base delay.
            var delay = BaseDebounceMs;
            if (SearchUseRegex && _probeWasRegex && _probeMultiplier > 1)
            {
                delay = BaseDebounceMs * Math.Min(_probeMultiplier, MaxDebounceMultiplier);
            }

            await Task.Delay(delay, debounce.Token).ConfigureAwait(false);
            await RunSearchAsync(generation, debounce.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (debounce.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_debounceCancellation, debounce))
            {
                _debounceCancellation = null;
            }
            debounce.Dispose();
        }
    }

    private async Task RunSearchAsync(int generation, CancellationToken debounceToken)
    {
        var root = _workspace.Current?.ProjectPath;
        if (string.IsNullOrWhiteSpace(root) || generation != _generation) return;

        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(debounceToken);
        var token = _searchCancellation.Token;
        var query = new WorkspaceSearchQuery(root, SearchText,
            new(SearchCaseSensitive, SearchWholeWord, SearchUseRegex),
            IncludePattern, ExcludePattern, UseIgnoreFiles);
        await _dispatcher.InvokeAsync(() =>
        {
            // S6: a refinement (same workspace + options, pattern extended as a prefix) keeps
            // the existing result tree — files are overwritten in place and stale entries are
            // pruned as the new scan arrives, so typing a longer term never flashes an empty
            // list. Anything else falls back to the original clear-then-fill.
            var refinement = StartRefinementIfPossible(query);
            if (!refinement)
            {
                // Clear the previous tree without cancelling the newly-created scan token below.
                // ClearResults normally cancels the active scan; doing that here would cancel
                // this search before the first directory is enumerated.
                ClearResults(cancelSearch: false);
            }

            _lastQueryRoot = query.RootPath;
            _lastQueryPattern = query.Pattern;
            _lastQueryOptions = query.Options;
            _lastQueryInclude = query.IncludePattern;
            _lastQueryExclude = query.ExcludePattern;
            _lastQueryUseIgnoreFiles = query.UseIgnoreFiles;
            _currentSearchUseRegex = query.Options.UseRegex;
            _probeFiles = 0;
            _probeMatches = 0;
            IsSearching = true;
            StatusMessage = "正在搜索…";
        });

        var progress = new Progress<WorkspaceSearchProgress>(value =>
        {
            if (generation != _generation) return;
            _progressCoalescer.Raise((generation, value));
        });
        var pending = new List<WorkspaceSearchFileResult>(UiBatchSize);
        try
        {
            await foreach (var file in _searchService.SearchAsync(query, progress, token).WithCancellation(token).ConfigureAwait(false))
            {
                if (generation != _generation) return;
                pending.Add(file);
                if (pending.Count >= UiBatchSize)
                {
                    var batch = pending.ToArray();
                    pending.Clear();
                    await _dispatcher.InvokeAsync(() => AddResults(generation, batch));
                }
            }

            if (pending.Count > 0)
            {
                var batch = pending.ToArray();
                await _dispatcher.InvokeAsync(() => AddResults(generation, batch));
            }

            await _dispatcher.InvokeAsync(() =>
            {
                if (generation != _generation) return;
                // 立即发布最后一次进度(等待合并的尾批),保证完成摘要的计数是最终值。
                _progressCoalescer.Flush();
                if (_refinement is { ReceivedFiles: 0 } refinementState)
                {
                    // S6: the refined query matched no file at all, so no batch ever ran its
                    // prune pass — drop every retained entry now (each one still carries a
                    // preview that would have made the new scan return it, otherwise).
                    PruneStaleFiles(refinementState);
                    RebuildRows();
                }

                _refinement = null;
                IsSearching = false;
                StatusMessage = _matchesFound == 0 ? "未找到匹配项" : IsTruncated ? "结果已截断" : "搜索完成";
                OnPropertyChanged(nameof(SearchSummary));
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (generation != _generation) return;
                _refinement = null;
                IsSearching = false;
                StatusMessage = "搜索已取消";
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (generation != _generation) return;
                _refinement = null;
                IsSearching = false;
                StatusMessage = "搜索失败";
                LogService.WriteException("ERROR", "工作区搜索失败", ex);
            });
        }
    }

    private void AddResults(int generation, IEnumerable<WorkspaceSearchFileResult> results)
    {
        if (generation != _generation) return;
        var refinement = _refinement;
        var pureAppend = true;
        if (refinement is not null && !refinement.Pruned)
        {
            // S6: first batch of a refinement — drop entries that cannot still match the
            // refined query (pruning before the first overwrite keeps the visible rows honest).
            pureAppend = PruneStaleFiles(refinement) == 0;
            refinement.Pruned = true;
        }

        var appended = new List<SearchTreeNode>();
        foreach (var file in results)
        {
            _root ??= CreateWorkspaceRoot();
            var (parent, fileName, createdFolders) = GetOrCreateFolderChain(file.RelativePath);
            SearchTreeNode? existing = null;
            if (_childrenIndex.TryGetValue(parent, out var siblings))
            {
                siblings.TryGetValue(fileName, out existing);
            }

            if (existing is { Kind: SearchTreeNodeKind.File })
            {
                if (!MatchesUnchanged(existing, file.Matches))
                {
                    // S6: the file already exists in the tree — overwrite its match set in place
                    // (the file row keeps its identity; only its match rows are replaced). The
                    // list-view wrapper is invalidated so the re-flatten rebuilds its match rows.
                    pureAppend = false;
                    if (existing.FullPath is { } stalePath)
                    {
                        _listFileCache.Remove(stalePath);
                    }

                    ReplaceFileMatches(existing, file.Matches);
                }
                // Identical match set: nothing to do — the existing rows are reused as-is.
            }
            else
            {
                var fileDepth = ReferenceEquals(parent, _root) ? 0 : parent.Depth + 1;
                var fileNode = new SearchTreeNode(SearchTreeNodeKind.File, fileName, file.RelativePath, file.FullPath,
                    parent, fileDepth);
                parent.AddChild(fileNode);
                if (existing is null)
                {
                    GetOrCreateChildren(parent)[fileName] = fileNode;
                }
                foreach (var match in file.Matches)
                {
                    fileNode.AddChild(new SearchTreeNode(SearchTreeNodeKind.Match, $"第 {match.Line} 行",
                        file.RelativePath, match, file.FullPath, fileNode, fileNode.Depth + 1));
                }

                if (pureAppend)
                {
                    if (!ChainFullyExpanded(fileNode, _root))
                    {
                        // A newly created folder landed under a collapsed ancestor: its rows are
                        // not visible, so the visible list is no longer "old rows + new tail".
                        pureAppend = false;
                        appended.Clear();
                    }
                    else
                    {
                        if (createdFolders is not null)
                        {
                            // V5: folders created by this file's chain (root→leaf order) appear
                            // before the file's own rows in the flatten.
                            appended.AddRange(createdFolders);
                        }

                        if (IsListView)
                        {
                            Flatten(GetOrBuildListFileNode(fileNode), appended);
                        }
                        else
                        {
                            Flatten(fileNode, appended);
                        }
                    }
                }
            }

            if (refinement is not null) refinement.ReceivedFiles++;
            UpdateProbe(file);
        }

        if (pureAppend && appended.Count > 0)
        {
            // V5 tail-append fast path: this batch only extended the result tail (new files and
            // newly created folders, nothing replaced or pruned, all ancestors expanded), so the
            // visible rows are exactly the previous rows plus the flattened new nodes — append
            // them directly instead of re-flattening the whole tree (O(batch), not O(batch×N)).
            foreach (var node in appended)
            {
                Rows.Add(node);
            }

            TailAppendBatchCount++;
            OnPropertyChanged(nameof(HasResults));
        }
        else
        {
            FullRebuildCount++;
            RebuildRows();
        }
    }

    /// <summary>S6: a search is a refinement of the previous one when it searches the same
    /// workspace with identical options and its pattern extends the previous pattern as a
    /// prefix. Returns true (and arms <see cref="_refinement"/>) when the existing result tree
    /// must be kept; the caller clears the tree in every other case.</summary>
    private bool StartRefinementIfPossible(WorkspaceSearchQuery query)
    {
        _refinement = null;
        if (_root is null || _lastQueryPattern is null || string.IsNullOrEmpty(query.Pattern))
        {
            return false;
        }

        var comparison = query.Options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!(_lastQueryRoot == query.RootPath
              && _lastQueryOptions is { } options
              && options.CaseSensitive == query.Options.CaseSensitive
              && options.WholeWord == query.Options.WholeWord
              && options.UseRegex == query.Options.UseRegex
              && _lastQueryInclude == query.IncludePattern
              && _lastQueryExclude == query.ExcludePattern
              && _lastQueryUseIgnoreFiles == query.UseIgnoreFiles
              && query.Pattern.StartsWith(_lastQueryPattern, comparison)))
        {
            return false;
        }

        _refinement = new RefinementState
        {
            Query = query,
            Regex = query.Options.UseRegex
                ? new Regex(query.Pattern, TextSearchService.BuildRegexOptions(query.Options.CaseSensitive))
                : null,
            Comparison = comparison,
        };
        return true;
    }

    /// <summary>S6: removes file nodes whose stored matches cannot still match the refined
    /// query. For a literal prefix refinement every line matching the new pattern also matched
    /// the old one, so the stored (preview) lines of a surviving file still contain the new
    /// pattern; a file whose previews no longer do cannot be a result of the new scan. Returns
    /// the number of removed files (0 keeps the tail-append fast path viable).</summary>
    private int PruneStaleFiles(RefinementState refinement)
    {
        if (_root is null) return 0;
        var removed = 0;
        foreach (var file in EnumerateFiles(_root).ToList())
        {
            if (FileStillMatches(file, refinement)) continue;
            RemoveFileNode(file);
            removed++;
        }

        return removed;
    }

    private static bool FileStillMatches(SearchTreeNode file, RefinementState refinement)
    {
        foreach (var child in file.Children)
        {
            if (child.IsMatch is not true || child.Match is not { } match) continue;
            if (refinement.Regex is { } regex)
            {
                if (regex.IsMatch(match.Preview)) return true;
            }
            else if (match.Preview.IndexOf(refinement.Query.Pattern, refinement.Comparison) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private void RemoveFileNode(SearchTreeNode fileNode)
    {
        if (fileNode.Parent is { } parent)
        {
            parent.RemoveChild(fileNode);
            if (_childrenIndex.TryGetValue(parent, out var siblings))
            {
                siblings.Remove(fileNode.Name);
            }
        }

        if (fileNode.FullPath is { } path)
        {
            _listFileCache.Remove(path);
        }
    }

    private static bool MatchesUnchanged(SearchTreeNode fileNode, IReadOnlyList<WorkspaceSearchMatch> matches)
    {
        var children = fileNode.Children;
        if (children.Count != matches.Count) return false;
        for (var i = 0; i < matches.Count; i++)
        {
            if (children[i].Match is not { } stored || !stored.Equals(matches[i])) return false;
        }

        return true;
    }

    private static void ReplaceFileMatches(SearchTreeNode fileNode, IReadOnlyList<WorkspaceSearchMatch> matches)
    {
        foreach (var child in fileNode.Children.Where(child => child.IsMatch).ToArray())
        {
            fileNode.RemoveChild(child);
        }

        foreach (var match in matches)
        {
            fileNode.AddChild(new SearchTreeNode(SearchTreeNodeKind.Match, $"第 {match.Line} 行",
                fileNode.RelativePath, match, fileNode.FullPath ?? string.Empty, fileNode, fileNode.Depth + 1));
        }
    }

    /// <summary>S9: walks (and lazily creates) the folder chain for a relative path using the
    /// per-level child dictionaries — dictionary lookup per segment instead of a linear
    /// FirstOrDefault over the siblings. Returns the file's parent, the file name, and the
    /// folders created by this call (null when none were missing).</summary>
    private (SearchTreeNode Parent, string FileName, List<SearchTreeNode>? CreatedFolders) GetOrCreateFolderChain(string relativePath)
    {
        var parent = _root!;
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        List<SearchTreeNode>? created = null;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var segment = segments[index];
            var siblings = GetOrCreateChildren(parent);
            SearchTreeNode folder;
            if (siblings.TryGetValue(segment, out var existing) && existing.Kind == SearchTreeNodeKind.Folder)
            {
                folder = existing;
            }
            else
            {
                // A same-named file sibling is impossible on the case-insensitive filesystem the
                // search runs on; if it ever happened (in-memory fakes), mirror the old behavior
                // of adding a folder alongside without disturbing the file's index entry.
                var relative = string.Join('/', segments.Take(index + 1));
                var depth = ReferenceEquals(parent, _root) ? 0 : parent.Depth + 1;
                folder = new(SearchTreeNodeKind.Folder, segment, relative, parent: parent, depth: depth);
                parent.AddChild(folder);
                if (existing is null)
                {
                    siblings[segment] = folder;
                }

                created ??= [];
                created.Add(folder);
            }

            parent = folder;
        }

        return (parent, segments[^1], created);
    }

    private Dictionary<string, SearchTreeNode> GetOrCreateChildren(SearchTreeNode parent)
    {
        if (!_childrenIndex.TryGetValue(parent, out var siblings))
        {
            siblings = new Dictionary<string, SearchTreeNode>(StringComparer.OrdinalIgnoreCase);
            _childrenIndex[parent] = siblings;
        }

        return siblings;
    }

    private static bool ChainFullyExpanded(SearchTreeNode node, SearchTreeNode root)
    {
        for (var current = node.Parent; current is not null && !ReferenceEquals(current, root); current = current.Parent)
        {
            if (!current.IsExpanded) return false;
        }

        return true;
    }

    private SearchTreeNode CreateWorkspaceRoot()
    {
        var project = _workspace.Current?.ProjectPath;
        var node = new SearchTreeNode(SearchTreeNodeKind.Workspace,
            Path.GetFileName(project?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "工作区",
            project ?? string.Empty);
        _root = node;
        return node;
    }

    /// <summary>S7: after each UI batch, re-measure the hits-per-file density of the in-flight
    /// search; the multiplier scales the debounce for the NEXT keystroke (see
    /// <see cref="DebounceAndSearchAsync"/>). Only regex searches probe — plain literal
    /// searches keep the base delay.</summary>
    private void UpdateProbe(WorkspaceSearchFileResult file)
    {
        if (!_currentSearchUseRegex) return;
        _probeFiles++;
        _probeMatches += file.Matches.Count;
        var density = (double)_probeMatches / _probeFiles;
        _probeMultiplier = density >= HighHitDensity ? MaxDebounceMultiplier
            : density >= MediumHitDensity ? 2
            : 1;
        _probeWasRegex = true;
    }

    /// <summary>行投影增量同步:树节点是持久对象(流式搜索只向 _root 追加,不重建),
    /// 因此前后缀 diff 后只有新增文件/切换展开的行区间产生 CollectionChanged。
    /// 旧实现每批 16 个文件就 Rows.Clear() + 全量重扁平化(O(N²)、滚动位置丢失、闪烁)。</summary>
    private void RebuildRows()
    {
        var desired = new List<SearchTreeNode>(Math.Max(4, Rows.Count));
        if (_root is not null)
        {
            if (IsListView)
            {
                foreach (var file in EnumerateFiles(_root))
                {
                    Flatten(GetOrBuildListFileNode(file), desired);
                }
            }
            else
            {
                // The workspace node is an internal grouping root, not a visible search-result row.
                foreach (var child in _root.Children)
                {
                    Flatten(child, desired);
                }
            }
        }

        CollectionDiffer.Apply(Rows, desired, ReferenceEqualityComparer.Instance);
        OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>列表视图的文件行是 _root 树节点的投影包装;按 FullPath 缓存复用,
    /// 流式追加时已存在文件的包装行不重建(其匹配集在文件结果到达时一次性确定)。</summary>
    private SearchTreeNode GetOrBuildListFileNode(SearchTreeNode file)
    {
        if (file.FullPath is { } path &&
            _listFileCache.TryGetValue(path, out var cached) &&
            cached.MatchCount == file.MatchCount)
        {
            cached.IsExpanded = _listExpansion.TryGetValue(path, out var remembered)
                ? remembered
                : file.IsExpanded;
            return cached;
        }

        var listFile = new SearchTreeNode(SearchTreeNodeKind.File, file.Name, file.RelativePath,
            file.FullPath, parent: null, depth: 0, isListResult: true);
        foreach (var match in file.Children.Where(child => child.IsMatch))
        {
            listFile.AddChild(new SearchTreeNode(SearchTreeNodeKind.Match, match.Name, match.RelativePath,
                match.Match!, match.FullPath!, listFile, depth: 1));
        }

        listFile.IsExpanded = file.FullPath is { } p2 && _listExpansion.TryGetValue(p2, out var ex)
            ? ex
            : file.IsExpanded;
        if (file.FullPath is not null)
        {
            _listFileCache[file.FullPath] = listFile;
        }

        return listFile;
    }

    private static void Flatten(SearchTreeNode node, ICollection<SearchTreeNode> target)
    {
        target.Add(node);
        if (!node.IsExpanded) return;
        foreach (var child in node.Children) Flatten(child, target);
    }

    private static IEnumerable<SearchTreeNode> EnumerateFiles(SearchTreeNode node)
    {
        foreach (var child in node.Children)
        {
            if (child.Kind == SearchTreeNodeKind.File)
            {
                yield return child;
            }
            else
            {
                foreach (var file in EnumerateFiles(child)) yield return file;
            }
        }
    }

    private static void SetExpanded(SearchTreeNode? node, bool expanded, bool keepRootVisible = false)
    {
        if (node is null) return;
        node.IsExpanded = expanded || keepRootVisible && node.Kind == SearchTreeNodeKind.Workspace;
        foreach (var child in node.Children) SetExpanded(child, expanded);
    }

    private void ClearResults(bool cancelSearch = true)
    {
        if (cancelSearch) _searchCancellation?.Cancel();
        _root = null;
        _listExpansion.Clear();
        _listFileCache.Clear();
        _childrenIndex.Clear();
        _refinement = null;
        _lastQueryRoot = null;
        _lastQueryPattern = null;
        _lastQueryOptions = null;
        _lastQueryInclude = string.Empty;
        _lastQueryExclude = string.Empty;
        _lastQueryUseIgnoreFiles = true;
        _probeFiles = 0;
        _probeMatches = 0;
        _probeMultiplier = 1;
        _probeWasRegex = false;
        Rows.Clear();
        _filesScanned = 0;
        _filesMatched = 0;
        _matchesFound = 0;
        IsTruncated = false;
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(SearchSummary));
    }

    private async Task OnWorkspaceContextChangedAsync(ProjectWorkspaceContext? context)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            _generation++;
            _searchCancellation?.Cancel();
            ClearResults();
            OnPropertyChanged(nameof(HasWorkspace));
        });
        if (context is not null && !string.IsNullOrWhiteSpace(SearchText)) ScheduleSearch();
    }

    /// <summary>S6: per-refinement state. The refined query (plus a precompiled probe regex)
    /// drives the one-time prune of stale entries and the in-place overwrite of file nodes by
    /// FullPath; <see cref="ReceivedFiles"/> reaches 0 only when the refined scan returned no
    /// file at all (in which case the completion path prunes the whole retained tree).</summary>
    private sealed class RefinementState
    {
        public WorkspaceSearchQuery Query = null!; // 由 StartRefinementIfPossible 组装
        public Regex? Regex;
        public StringComparison Comparison;
        public bool Pruned;
        public int ReceivedFiles;
    }
}
