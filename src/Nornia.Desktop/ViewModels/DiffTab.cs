using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nornia.Core.Coalescing;
using Nornia.Core.Collections;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Desktop.Code;
using Nornia.Desktop.Configuration;
using Nornia.Desktop.Markdown;
using Nornia.Desktop.Services;
using Nornia.Project.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Nornia.Desktop.ViewModels;

/// <summary>A diff tab in the shared editor group. Loads its content lazily from git and exposes
/// safe hunk-level source-control operations for working-tree and index diffs.</summary>
public sealed partial class DiffTab : EditorTabItem
{
    /// <summary>大文件分层阈值(设置 <c>nornia.reading.*</c> 由 EditorArea 在创建/设置变化时写入;
    /// 在下一次 diff 加载时生效)。</summary>
    public ReadOnlyContentLimits ContentLimits { get; set; } = ReadOnlyContentLimits.Default;

    private readonly IGitService _gitService;
    private readonly GitDiffRequest _request;
    private readonly IUiLogService _logService;
    private readonly IClipboardService _clipboard;
    private readonly IConfirmationService? _confirmationService;
    private readonly SemaphoreSlim _hunkOperationGate = new(1, 1);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _loadStarted;
    private bool _disposed;
    private bool _loadFailed;
    private string? _diffRevision;
    private IDiffContentSource? _diffContentSource;
    private bool _restoreConfirmedForSession;

    /// <summary>侧别覆盖:请求的 IsStaged/IsUntracked 来自打开时刻的状态快照,可能滞后于实际
    /// index/HEAD(刷新未落地、或请求构建后更改被外部暂存/提交)——此时请求侧的 git diff 无输出,
    /// 加载会对另一侧重试并采用有内容的侧(见 LoadCoreAsync 的空 diff 回退)。回退生效后
    /// <c>null</c> 被替换为实际侧;显示(来源标签/标签状态)与块操作(暂存/取消暂存/还原)
    /// 都跟随该值而非请求侧,保证"看到的差异"与"能做的操作"一致。</summary>
    private bool? _displayStaged;

    public bool DisplayIsStaged => _displayStaged ?? _request.IsStaged;

    /// <summary>未跟踪侧仅当未发生回退时成立(回退必然落到暂存/未暂存侧)。</summary>
    private bool DisplayIsUntracked => _displayStaged is null && _request.IsUntracked;

    /// <summary>D1 代次门:后启动的加载是唯一允许发布结果的加载。spool 读回 / 后台构建可能
    /// 跨多次 await 才落地,迟到的旧代次结果不得覆盖更新的加载(与 FilePreviewTab 的
    /// _presentationVersion 版门同构,但守护的是 diff 安装而非 TextMate 快照)。</summary>
    private int _loadGeneration;

    /// <summary>原始行数不超过该值时 diff 构建留在调用线程(同步快速路径):小 diff 的构建
    /// 成本可忽略,跨线程只增加一跳且会破坏"加载完成即就绪"的既有同步时序。更大的 diff
    /// 走 Task.Run 后台构建 + 单次 Dispatcher 换入。</summary>
    private const int InlineBuildMaxLines = 2_000;

    public DiffTab(IGitService gitService, GitDiffRequest request, IUiLogService logService, IClipboardService clipboard, IConfirmationService? confirmationService = null) : base(request.Path)
    {
        _gitService = gitService;
        _request = request;
        _logService = logService;
        _clipboard = clipboard;
        _confirmationService = confirmationService;
        SelectedDiffLines.CollectionChanged += (_, _) => CopySelectedDiffLinesCommand.NotifyCanExecuteChanged();
    }

    public override string TabKey => _request.TabKey;
    public override string Glyph => Codicons.SourceControl;
    public override bool IsDiff => true;

    /// <summary>恢复用的 diff 描述(仓库路径 / 文件路径 / 暂存状态 / 提交哈希)。</summary>
    public GitDiffRequest Request => _request;

    /// <summary>"在代码标签页打开文件"请求(由 EditorAreaViewModel 接线到 OpenFileAsync)。</summary>
    public event EventHandler? OpenInCodeRequested;

    [RelayCommand]
    private void OpenInCode() => OpenInCodeRequested?.Invoke(this, EventArgs.Empty);

    public BulkObservableCollection<GitDiffLine> DiffLines { get; } = [];
    public BulkObservableCollection<GitSideBySideRow> SideBySideRows { get; } = [];

    /// <summary>Hunks represented by the current Diff snapshot. This is kept beside the flattened
    /// display collections so hunk actions never infer identity from visual rows.</summary>
    public IReadOnlyList<GitDiffHunk> Hunks { get; private set; } = [];

    /// <summary>Raised after a hunk mutation succeeds and the tab has reloaded its Diff.</summary>
    public event EventHandler? HunkMutationCompleted;

    /// <summary>Multi-selection for the inline diff list (Ctrl+C / 复制选中).</summary>
    public ObservableCollection<GitDiffLine> SelectedDiffLines { get; } = [];

    [ObservableProperty]
    private string diffTitle = string.Empty;

    [ObservableProperty]
    private string diffNotice = string.Empty;

    [ObservableProperty]
    private ReadOnlyContentTier capacityTier = ReadOnlyContentTier.Full;

    [ObservableProperty]
    private GitDiffMode diffMode = GitDiffMode.SideBySide;

    /// <summary>Collapses long unchanged runs into a review-friendly projection. Raw Git lines
    /// remain untouched for copying, statistics and source-line authority.</summary>
    [ObservableProperty]
    private bool isContextCollapsed = true;

    /// <summary>Diff editor font size, seeded from the persisted code reading options so diff and
    /// preview editors render at the same size (independent of the global UI font scale).</summary>
    [ObservableProperty]
    private double editorFontSize = 14;

    /// <summary>Diff 阅读器等宽字体(editor.fontFamily;与源码阅读器一致)。</summary>
    [ObservableProperty]
    private string fontFamily = FontCatalog.DefaultEditorFamily;

    [ObservableProperty]
    private bool showIntralineChanges = true;

    [ObservableProperty]
    private bool showOverviewRuler = true;

    [ObservableProperty]
    private bool synchronizeScrolling = true;

    /// <summary>窄窗自动内联(VS Code diffEditor.useInlineViewWhenSpaceIsLimited)。</summary>
    [ObservableProperty]
    private bool useInlineWhenNarrow = true;

    /// <summary>忽略行尾空白(diffEditor.ignoreTrimWhitespace):切换后按 git --ignore-space-at-eol 重算。</summary>
    [ObservableProperty]
    private bool ignoreTrimWhitespace;

    /// <summary>主状态栏"当前文档状态"段的光标位置显示(由视图 code-behind 随最后一个获得焦点的
    /// 编辑面 caret 变化写入,形如 "Ln 12, Col 34";原视图内独立底栏的内容已并入窗口主状态栏)。</summary>
    [ObservableProperty]
    private string caretPositionText = string.Empty;

    // ===== Diff 内查找(统一文本;视图负责匹配定位与高亮) =====

    [ObservableProperty]
    private bool isFindBarOpen;

    [ObservableProperty]
    private string findText = string.Empty;

    [ObservableProperty]
    private int findIndex;

    [ObservableProperty]
    private int findCount;

    /// <summary>diff 查找选项开关(与文件阅读查找框同款)。此前 diff 侧固定忽略大小写/非正则
    /// (<see cref="DiffTextSearch.DefaultOptions"/>),与文件侧行为不一致;现在由查找框直接绑定。</summary>
    [ObservableProperty]
    private bool findCaseSensitive;

    [ObservableProperty]
    private bool findWholeWord;

    [ObservableProperty]
    private bool findUseRegex;

    /// <summary>正则非法时展示的编译错误(输入修正后自动清空),与文件阅读查找框一致。</summary>
    [ObservableProperty]
    private string findErrorMessage = string.Empty;

    /// <summary>查找框聚焦请求序号:每次 <see cref="OpenDiffFindBarCommand"/> 递增(含已打开时),
    /// 视图据此重新聚焦输入框。</summary>
    [ObservableProperty]
    private int findFocusRequest;

    /// <summary>编辑器选中文本 → 查找框播种。由视图在 DataContext 变化时注入
    /// (读 AvalonEdit 选区必须持有编辑器实例,留在视图层),返回 null 表示无可用选区。</summary>
    public Func<string?>? FindSelectionSeeder { get; set; }

    /// <summary>查找步进请求(±1)。匹配列表与跳转落在视图层(编辑器实例),VM 只发方向。</summary>
    public event Action<int>? FindStepRequested;

    [RelayCommand]
    private void OpenDiffFindBar()
    {
        if (FindSelectionSeeder?.Invoke() is { Length: > 0 } seed)
        {
            FindText = seed;
        }

        IsFindBarOpen = true;
        FindFocusRequest++;
    }

    [RelayCommand]
    private void FindNext() => FindStepRequested?.Invoke(1);

    [RelayCommand]
    private void FindPrevious() => FindStepRequested?.Invoke(-1);

    /// <summary>当前查找选项快照,供视图驱动 <see cref="DiffTextSearch"/>(视图不自行拼装语义)。</summary>
    public TextSearchOptions FindOptions => new(FindCaseSensitive, FindWholeWord, FindUseRegex);

    public string CurrentMatchDisplay => string.IsNullOrEmpty(FindText)
        ? string.Empty
        : FindCount == 0 ? "无匹配" : $"{FindIndex + 1} / {FindCount}";

    partial void OnFindTextChanged(string value) => OnPropertyChanged(nameof(CurrentMatchDisplay));

    partial void OnFindCountChanged(int value) => OnPropertyChanged(nameof(CurrentMatchDisplay));

    partial void OnFindIndexChanged(int value) => OnPropertyChanged(nameof(CurrentMatchDisplay));

    [RelayCommand]
    private void ToggleIgnoreWhitespace()
    {
        IgnoreTrimWhitespace = !IgnoreTrimWhitespace;
        _ = ReloadDiffAsync();
    }

    /// <summary>以当前选项重新加载差异(重算窗口重置,集合由事件驱动视图重建)。</summary>
    public async Task ReloadDiffAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (_disposed)
            {
                return;
            }

            var previousLines = DiffLines.ToArray();
            var previousRows = SideBySideRows.ToArray();
            var previousHunks = Hunks;
            var previousRevision = _diffRevision;
            var previousDisplayStaged = _displayStaged;

            _loadStarted = false;
            IsLoaded = false;
            DiffLines.Clear();
            SideBySideRows.Clear();
            Hunks = [];
            OnPropertyChanged(nameof(Hunks));
            DiffNotice = string.Empty;
            if (_diffContentSource is not null)
            {
                await _diffContentSource.DisposeAsync().ConfigureAwait(true);
                _diffContentSource = null;
            }

            await LoadCoreAsync().ConfigureAwait(true);
            if (_loadFailed && !_disposed)
            {
                var refreshNotice = DiffNotice;
                DiffLines.ReplaceRange(previousLines);
                SideBySideRows.ReplaceRange(previousRows);

                Hunks = previousHunks;
                _diffRevision = previousRevision;
                _displayStaged = previousDisplayStaged;
                IsLoaded = true;
                DiffNotice = string.IsNullOrWhiteSpace(refreshNotice)
                    ? "无法刷新 Diff，已保留上一版本。"
                    : $"{refreshNotice} 已保留上一版本。";
                OnPropertyChanged(nameof(Hunks));
                OnPropertyChanged(nameof(HasDiff));
                OnPropertyChanged(nameof(IsEmptyDiff));
            }

            OnPropertyChanged(nameof(HasDiff));
            OnPropertyChanged(nameof(IsEmptyDiff));
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public async Task RefreshIfChangedAsync()
    {
        if (_disposed || _request.CommitHash is not null || !IsLoaded)
        {
            return;
        }

        try
        {
            // 不用请求携带的 blob id(打开时刻状态快照,暂存/提交后即过期):复用它们会让 index
            // 变化(如 git add)在修订标识上不可见,标签错过重载、一直显示旧侧内容。重查当前
            // index/HEAD 的 blob(ls-files / rev-parse)使标识对侧别变化保持敏感;侧别跟随实际
            // 显示侧(空 diff 回退后)。
            var revision = await _gitService.GetDiffRevisionAsync(
                _request.RepositoryPath,
                Path,
                DisplayIsStaged,
                DisplayIsUntracked,
                _lifetimeCancellation.Token).ConfigureAwait(true);
            if (!_disposed && !string.Equals(revision, _diffRevision, StringComparison.Ordinal))
            {
                await ReloadDiffAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            _logService.Write("WARNING", $"无法检查 Diff 变化：{ex.Message}");
        }
    }

    /// <summary>True once the diff has finished loading (even when it turned out empty or failed),
    /// so the view can stop showing the "loading" empty state.</summary>
    [ObservableProperty]
    private bool isLoaded;

    public bool IsInlineDiff => DiffMode == GitDiffMode.Inline;
    public bool IsSideBySideDiff => DiffMode == GitDiffMode.SideBySide;
    public bool HasDiff => DiffLines.Count > 0 || SideBySideRows.Count > 0;

    /// <summary>True when loading finished and there is nothing to render (binary / empty / error).</summary>
    public bool IsEmptyDiff => IsLoaded && !HasDiff;

    /// <summary>Empty-state message after a finished load: prefer the load notice when present.</summary>
    public string EmptyStateTitle => string.IsNullOrWhiteSpace(DiffNotice) ? "该文件没有可显示的差异。" : DiffNotice;

    // ===== Change statistics + navigation =====

    private int _addedCount;
    private int _removedCount;
    private int _changeCount;

    /// <summary>数量 of added lines in the loaded diff.</summary>
    public int AddedCount => _addedCount;

    /// <summary>Number of removed lines in the loaded diff.</summary>
    public int RemovedCount => _removedCount;

    /// <summary>"新增 N 行 · 删除 M 行" summary for the diff top bar.</summary>
    public string ChangeSummary => $"+{_addedCount} −{_removedCount}";

    /// <summary>Number of contiguous added/removed blocks (drives 上一个/下一个更改).</summary>
    public int ChangeCount => _changeCount;

    /// <summary>0-based index of the change block the user navigated to.</summary>
    [ObservableProperty]
    private int currentChangeIndex;

    public string ChangeCounter => ChangeCount == 0 ? string.Empty : $"{CurrentChangeIndex + 1} / {ChangeCount}";

    /// <summary>Label of the diff source: 暂存 / 未暂存 / 未跟踪 / 提交 short-hash. Follows the
    /// display side so an empty-diff fallback that adopted the other side is labeled correctly.</summary>
    public string SourceLabel => _request.CommitHash is not null
        ? $"提交 {ShortHash(_request.CommitHash)}"
        : DisplayIsUntracked ? "未跟踪" : DisplayIsStaged ? "已暂存" : "未暂存";

    /// <summary>Compact SCM marker used in the unified editor tab strip.</summary>
    public override DiffTabStatus TabStatus => _request.CommitHash is not null
        ? DiffTabStatus.Commit
        : DisplayIsUntracked ? DiffTabStatus.Untracked : DisplayIsStaged ? DiffTabStatus.Staged : DiffTabStatus.Modified;

    public override string TabStatusMarker => TabStatus switch
    {
        DiffTabStatus.Staged => "S",
        DiffTabStatus.Untracked => "U",
        DiffTabStatus.Commit => "C",
        _ => "M",
    };

    public override string TabStatusToolTip => $"{TabStatusMarker} · {SourceLabel}";

    /// <summary>Raised with the 0-based change-block index after 上一个/下一个更改; the view centers
    /// the target block in its current layout (inline line / side-by-side row).</summary>
    public event EventHandler<int>? ChangeNavigationRequested;

    partial void OnCurrentChangeIndexChanged(int value) => OnPropertyChanged(nameof(ChangeCounter));

    [RelayCommand(CanExecute = nameof(CanNavigateChanges))]
    private void GoToNextChange()
    {
        if (_changeCount == 0)
        {
            return;
        }

        CurrentChangeIndex = (CurrentChangeIndex + 1) % _changeCount;
        ChangeNavigationRequested?.Invoke(this, CurrentChangeIndex);
    }

    [RelayCommand(CanExecute = nameof(CanNavigateChanges))]
    private void GoToPreviousChange()
    {
        if (_changeCount == 0)
        {
            return;
        }

        CurrentChangeIndex = (CurrentChangeIndex - 1 + _changeCount) % _changeCount;
        ChangeNavigationRequested?.Invoke(this, CurrentChangeIndex);
    }

    private bool CanNavigateChanges() => _changeCount > 0;

    public bool IsLayoutManuallySelected { get; set; }

    public bool ShouldAutoUseInlineForWidth(double availableWidth) =>
        UseInlineWhenNarrow && !IsLayoutManuallySelected && availableWidth < NarrowInlineWidth;

    public const double NarrowInlineWidth = 700;

    public bool CanApplyHunk(int hunkIndex, GitHunkOperation operation)
    {
        if (_disposed
            || _request.CommitHash is not null
            || DisplayIsUntracked
            || hunkIndex < 0
            || hunkIndex >= Hunks.Count
            || IsHunkOperationBusy)
        {
            return false;
        }

        // 侧别跟随实际显示侧:空 diff 回退后内容属于另一侧,块操作必须作用到那一侧。
        return operation switch
        {
            GitHunkOperation.Stage => !DisplayIsStaged,
            GitHunkOperation.Unstage => DisplayIsStaged,
            GitHunkOperation.Restore => !DisplayIsStaged,
            _ => false,
        };
    }

    /// <summary>Number of contiguous changed blocks (maximal +/- runs, Notice 行视作分隔) inside
    /// the hunk. git 会把间距不超过 2×上下文行的多处修改合并成一个 hunk,块数 &gt; 1 时块级操作
    /// 才与整 hunk 操作不同。</summary>
    internal static int CountChangedBlocks(GitDiffHunk hunk)
    {
        var blocks = 0;
        var inBlock = false;
        foreach (var line in HunkBodyLines(hunk))
        {
            if (line.Kind is GitDiffLineKind.Added or GitDiffLineKind.Removed)
            {
                if (!inBlock)
                {
                    blocks++;
                    inBlock = true;
                }
            }
            else
            {
                inBlock = false;
            }
        }

        return blocks;
    }

    /// <summary>Resolves which contiguous changed block of the hunk contains the hovered anchor
    /// line: a removed line matches by old file line number, an added line by new file line
    /// number. 两个锚点都为空 → 整 hunk(null);给定了锚点却匹配不到任何块 → -1,由调用方拒绝
    /// 应用(宁可不操作,也不能错落到相邻块)。</summary>
    internal static int? ResolveHunkBlockOrdinal(GitDiffHunk hunk, int? anchorOldLine, int? anchorNewLine)
    {
        if (anchorOldLine is null && anchorNewLine is null)
        {
            return null;
        }

        var matched = -1;
        var ordinal = -1;
        var inBlock = false;
        foreach (var line in HunkBodyLines(hunk))
        {
            if (line.Kind is not (GitDiffLineKind.Added or GitDiffLineKind.Removed))
            {
                inBlock = false;
                continue;
            }

            if (!inBlock)
            {
                ordinal++;
                inBlock = true;
            }

            var matches = (line.Kind == GitDiffLineKind.Removed && line.OldLineNumber is { } old && old == anchorOldLine)
                || (line.Kind == GitDiffLineKind.Added && line.NewLineNumber is { } New && New == anchorNewLine);
            if (matches)
            {
                matched = ordinal;
            }
        }

        return matched;
    }

    private static IEnumerable<GitDiffLine> HunkBodyLines(GitDiffHunk hunk) =>
        hunk.Lines.SkipWhile(line => line.Kind == GitDiffLineKind.HunkHeader);

    [ObservableProperty]
    private bool isHunkOperationBusy;

    /// <summary>Applies one hunk through Git, reloads this Diff tab and notifies the SCM page.</summary>
    public async Task<bool> ApplyHunkAsync(int hunkIndex, GitHunkOperation operation, CancellationToken cancellationToken = default) =>
        await ApplyHunkBlockCoreAsync(hunkIndex, null, null, operation, cancellationToken).ConfigureAwait(true);

    /// <summary>Applies one contiguous changed block (悬停的变更块) inside a hunk. git 会把相近的
    /// 两处修改合并成同一个 hunk——按块操作才能只暂存其中一处。<paramref name="blockAnchorOldLine"/>
    /// /<paramref name="blockAnchorNewLine"/> 是悬停块首行的旧/新文件行号(取得到的那侧),由此在
    /// hunk 原始行里解析出块序号;两个都为空时回退为整 hunk 操作。</summary>
    public async Task<bool> ApplyHunkBlockAsync(
        int hunkIndex,
        int? blockAnchorOldLine,
        int? blockAnchorNewLine,
        GitHunkOperation operation,
        CancellationToken cancellationToken = default) =>
        await ApplyHunkBlockCoreAsync(hunkIndex, blockAnchorOldLine, blockAnchorNewLine, operation, cancellationToken).ConfigureAwait(true);

    private async Task<bool> ApplyHunkBlockCoreAsync(
        int hunkIndex,
        int? blockAnchorOldLine,
        int? blockAnchorNewLine,
        GitHunkOperation operation,
        CancellationToken cancellationToken)
    {
        if (!CanApplyHunk(hunkIndex, operation))
        {
            return false;
        }

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetimeCancellation.Token);
        var operationToken = operationCancellation.Token;
        var gateEntered = false;
        try
        {
            await _hunkOperationGate.WaitAsync(operationToken).ConfigureAwait(true);
            gateEntered = true;
            // Reloads can be triggered by the repository watcher while a hover menu is still
            // open. Re-check after acquiring the gate before indexing the current hunk snapshot.
            if (!CanApplyHunk(hunkIndex, operation) || hunkIndex >= Hunks.Count)
            {
                return false;
            }

            IsHunkOperationBusy = true;
            var hunk = Hunks[hunkIndex];
            var blockOrdinal = ResolveHunkBlockOrdinal(hunk, blockAnchorOldLine, blockAnchorNewLine);
            if (blockOrdinal is { } resolved && (resolved < 0 || resolved >= CountChangedBlocks(hunk)))
            {
                // 悬停快照与当前 hunk 行不一致:拒绝应用,绝不能把块序号错落到相邻块上。
                _logService.Write("WARNING", $"无法定位悬停的变更块：{Path} #{hunkIndex + 1}。");
                return false;
            }

            if (operation == GitHunkOperation.Restore && !_restoreConfirmedForSession)
            {
                var target = blockOrdinal is { } ordinal && CountChangedBlocks(hunk) > 1
                    ? $"第 {hunkIndex + 1} 个 Diff 块内的一个变更块"
                    : $"第 {hunkIndex + 1} 个 Diff 块";
                var confirmed = _confirmationService?.Confirm(
                    "确认还原 Diff 块",
                    $"将还原“{Path}”的{target}，且无法撤销。选择“否”可安全取消。") == true;
                if (!confirmed)
                {
                    _logService.Write("INFO", $"用户取消了还原 Diff 块：{Path} #{hunkIndex + 1}。");
                    return false;
                }

                _restoreConfirmedForSession = true;
            }

            await _gitService.ApplyHunkAsync(_request.RepositoryPath, Path, DisplayIsStaged, hunk, operation, blockOrdinal, operationToken).ConfigureAwait(true);
            await ReloadDiffAsync(operationToken).ConfigureAwait(true);
            HunkMutationCompleted?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (OperationCanceledException) when (_disposed || _lifetimeCancellation.IsCancellationRequested)
        {
            // Closing a diff tab cancels a pending menu operation. The WPF event handler is
            // async-void, so consume only this lifecycle cancellation instead of surfacing it as
            // an application-level unhandled exception.
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = $"无法执行 Diff 块操作：{ex.Message}";
            _logService.Write("WARNING", message);
            await ReloadDiffAsync().ConfigureAwait(true);
            DiffNotice = message;
            return false;
        }
        finally
        {
            if (gateEntered)
            {
                IsHunkOperationBusy = false;
                _hunkOperationGate.Release();
            }
        }
    }

    [RelayCommand]
    private void ToggleDiffMode()
    {
        IsLayoutManuallySelected = true;
        DiffMode = DiffMode == GitDiffMode.Inline ? GitDiffMode.SideBySide : GitDiffMode.Inline;
    }

    public string ContextCollapseToolTip => IsContextCollapsed ? "显示未更改上下文" : "隐藏未更改上下文";

    partial void OnIsContextCollapsedChanged(bool value) =>
        OnPropertyChanged(nameof(ContextCollapseToolTip));

    [RelayCommand]
    private void ToggleContextCollapse() => IsContextCollapsed = !IsContextCollapsed;

    // ===== Copy commands: 复制选中 diff / 复制全部 diff =====

    /// <summary>Reconstructs the git-style line (sign markers) from the stripped display text.</summary>
    private static string FormatDiffLine(GitDiffLine line) => line.Kind switch
    {
        GitDiffLineKind.Added => "+" + line.Text,
        GitDiffLineKind.Removed => "-" + line.Text,
        GitDiffLineKind.Context => " " + line.Text,
        _ => line.Text,
    };

    [RelayCommand(CanExecute = nameof(CanCopySelectedDiffLines))]
    private void CopySelectedDiffLines() =>
        _clipboard.SetText(string.Join(Environment.NewLine, SelectedDiffLines.Select(FormatDiffLine)));

    private bool CanCopySelectedDiffLines() => SelectedDiffLines.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCopyAllDiffLines))]
    private void CopyAllDiffLines() =>
        _clipboard.SetText(string.Join(Environment.NewLine, DiffLines.Where(line => line.Kind != GitDiffLineKind.HunkHeader).Select(FormatDiffLine)));

    private bool CanCopyAllDiffLines() => DiffLines.Count > 0;

    /// <summary>Frees diff rows and selections when the tab closes.</summary>
    public override void ReleaseResources()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loadGeneration++; // D1: 任何在途构建/安装/发布一律失效
        _lifetimeCancellation.Cancel();
        if (_diffContentSource is not null)
        {
            _ = _diffContentSource.DisposeAsync();
            _diffContentSource = null;
        }
        DiffLines.Clear();
        SideBySideRows.Clear();
        Hunks = [];
        SelectedDiffLines.Clear();
        // Do not dispose these synchronization/cancellation primitives here. A hover-menu event
        // can still be unwinding on another continuation after the tab leaves the visual tree;
        // the lifetime cancellation makes it stop, and the objects are reclaimed with the tab.
    }

    partial void OnDiffModeChanged(GitDiffMode value)
    {
        OnPropertyChanged(nameof(IsInlineDiff));
        OnPropertyChanged(nameof(IsSideBySideDiff));
        OnPropertyChanged(nameof(DiffModeLabel));
        GoToNextChangeCommand.NotifyCanExecuteChanged();
        GoToPreviousChangeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Status-bar label of the current layout.</summary>
    public string DiffModeLabel => DiffMode == GitDiffMode.Inline ? "内联 Diff" : "并排 Diff";

    partial void OnIsLoadedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmptyDiff));
    }

    partial void OnDiffNoticeChanged(string value)
    {
        OnPropertyChanged(nameof(EmptyStateTitle));
    }

    public override async Task LoadAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _loadGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!_disposed)
            {
                await LoadCoreAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task LoadCoreAsync()
    {
        if (_loadStarted)
        {
            return;
        }

        _loadStarted = true;
        _loadFailed = false;
        _displayStaged = null; // 每次加载从请求侧重新开始;空 diff 回退可在本次加载中重新设置
        DiffTitle = _request.CommitHash is null
            ? Path
            : $"提交 {ShortHash(_request.CommitHash)} · {Path}";

        // D1: 代次在加载入口自增;只有最新代次允许安装快照与发布完成态。
        var generation = ++_loadGeneration;
        var installed = false;
        // 捕获进入加载时的同步上下文:应用内所有加载入口都在 UI 线程(打开/修订重载经
        // WpfUiDispatcher 回排),后台构建完成后续帖回这里安装;测试环境为空 → 就地执行。
        // 不用 Application.Current 查找 Dispatcher:跨线程读取会拿到别处创建的 Application,
        // 其 Dispatcher 可能根本不在泵,排过去的动作会被饿死。
        var uiContext = SynchronizationContext.Current;

        try
        {
            // 阶段一(spool 建立 + 元数据 + 窗口读回):异步 I/O,路径与原先一致。
            GitFileDiff? diff;
            if (_request.CommitHash is not null)
            {
                _diffContentSource = await SpoolingDiffContentSource.CreateAsync(
                    _gitService.StreamCommitFileDiffAsync(_request.RepositoryPath, _request.CommitHash, Path, ContentLimits.EffectiveWindowedDiffLines, IgnoreTrimWhitespace, _lifetimeCancellation.Token),
                    ContentLimits,
                    _lifetimeCancellation.Token);
                var metadata = await _diffContentSource.GetMetadataAsync();
                CapacityTier = metadata.CapacityTier;
                diff = CollectWindow((await _diffContentSource.ReadWindowAsync(new DocumentRange(1, ContentLimits.FullDiffLines))).Events, Path, false);
                if (metadata.TotalLines > ContentLimits.FullDiffLines) DiffNotice = $"大型提交 diff，当前窗口显示前 {ContentLimits.FullDiffLines:N0} 行。";
            }
            else
            {
                _diffContentSource = await SpoolingDiffContentSource.CreateAsync(
                    _gitService.StreamDiffAsync(_request.RepositoryPath, Path, _request.IsStaged, _request.IsUntracked, ContentLimits.EffectiveWindowedDiffLines, IgnoreTrimWhitespace, _lifetimeCancellation.Token),
                    ContentLimits,
                    _lifetimeCancellation.Token);
                var metadata = await _diffContentSource.GetMetadataAsync();
                CapacityTier = metadata.CapacityTier;
                var window = await _diffContentSource.ReadWindowAsync(new DocumentRange(1, ContentLimits.FullDiffLines));
                diff = CollectWindow(window.Events, Path, _request.IsStaged);
                if (metadata.TotalLines > ContentLimits.FullDiffLines)
                {
                    DiffNotice = CapacityTier == ReadOnlyContentTier.Summary
                        ? $"diff 超过 {ContentLimits.EffectiveWindowedDiffLines:N0} 行，已保留 hunk 索引并显示首个阅读窗口。"
                        : $"大型 diff，当前窗口显示前 {ContentLimits.FullDiffLines:N0} 行。";
                }
            }

            if (diff is null)
            {
                DiffNotice = "无法读取该文件的 diff。";
                return;
            }

            // 空差异回退:请求的侧别来自打开时刻的状态快照,可能滞后于实际 index/HEAD(静默刷新
            // 未落地,或请求构建后更改被外部暂存/提交)。此时请求侧的 git diff 无输出,用户会看到
            // "该文件没有可显示的差异"——而更改其实存在于另一侧。对另一侧重取一次,有内容即采用;
            // 显示与块操作随后跟随 _displayStaged(实际侧)。两侧都空时保持原样(空态即事实)。
            // Only a truly metadata-free result proves that the requested side went stale. Binary,
            // rename/copy and empty-file changes can legitimately have no hunks and must remain on
            // the side the user selected.
            if (_request.CommitHash is null && !diff.HasMetadata && diff.Hunks.Count == 0)
            {
                var otherStaged = !_request.IsStaged;
                var otherSource = await SpoolingDiffContentSource.CreateAsync(
                    _gitService.StreamDiffAsync(_request.RepositoryPath, Path, otherStaged, false, ContentLimits.EffectiveWindowedDiffLines, IgnoreTrimWhitespace, _lifetimeCancellation.Token),
                    ContentLimits,
                    _lifetimeCancellation.Token);
                var otherDiff = CollectWindow((await otherSource.ReadWindowAsync(new DocumentRange(1, ContentLimits.FullDiffLines))).Events, Path, otherStaged);
                if (otherDiff is not null && otherDiff.Hunks.Count > 0)
                {
                    if (_diffContentSource is not null)
                    {
                        await _diffContentSource.DisposeAsync().ConfigureAwait(true);
                    }

                    _diffContentSource = otherSource;
                    diff = otherDiff;
                    _displayStaged = otherStaged;
                    OnPropertyChanged(nameof(SourceLabel));
                    OnPropertyChanged(nameof(TabStatus));
                    OnPropertyChanged(nameof(TabStatusMarker));
                    OnPropertyChanged(nameof(TabStatusToolTip));
                }
                else
                {
                    await otherSource.DisposeAsync().ConfigureAwait(true);
                }
            }

            // 阶段二(D1):行尾归一 → 限量填充 → 并排行构建 → 增删/块计数 全部在后台线程
            // 完成,产出不可变快照;再经单次 Dispatcher 跳安装到可观察集合。小 diff 走
            // 同步快速路径,构建留在调用线程,保持既有的"加载完成即就绪"时序。
            var rawLineCount = diff.Hunks.Sum(hunk => hunk.Lines.Count);
            var built = rawLineCount <= InlineBuildMaxLines
                ? BuildDiffSnapshot(diff, ContentLimits)
                : await Task.Run(() => BuildDiffSnapshot(diff, ContentLimits), _lifetimeCancellation.Token).ConfigureAwait(false);

            await RunOnUiAsync(uiContext, () => installed = InstallDiffSnapshot(built, generation));
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _loadFailed = true;
            var notice = $"无法显示 diff：{ex.Message}";
            _logService.Write("WARNING", notice);
            await RunOnUiAsync(uiContext, () =>
            {
                if (!_disposed && generation == _loadGeneration)
                {
                    DiffNotice = notice;
                }
            });
        }
        finally
        {
            if (!_disposed)
            {
                // 完成态统一在 UI 线程发布;旧代次(已被更新的加载取代)不再发布任何状态。
                await RunOnUiAsync(uiContext, () =>
                {
                    if (generation != _loadGeneration)
                    {
                        return;
                    }

                    if (!installed)
                    {
                        // 错误/空 diff:计数沿用旧实现对空集合求值的语义(归零)。
                        _addedCount = 0;
                        _removedCount = 0;
                        _changeCount = 0;
                    }

                    IsLoaded = true;
                    if (CurrentChangeIndex >= _changeCount)
                    {
                        CurrentChangeIndex = 0;
                    }

                    OnPropertyChanged(nameof(HasDiff));
                    OnPropertyChanged(nameof(IsEmptyDiff));
                    OnPropertyChanged(nameof(EmptyStateTitle));
                    OnPropertyChanged(nameof(ChangeSummary));
                    OnPropertyChanged(nameof(AddedCount));
                    OnPropertyChanged(nameof(RemovedCount));
                    OnPropertyChanged(nameof(ChangeCount));
                    OnPropertyChanged(nameof(ChangeCounter));
                    OnPropertyChanged(nameof(SourceLabel));
                    CopyAllDiffLinesCommand.NotifyCanExecuteChanged();
                    GoToNextChangeCommand.NotifyCanExecuteChanged();
                    GoToPreviousChangeCommand.NotifyCanExecuteChanged();
                });

                if (_request.CommitHash is null)
                {
                    try
                    {
                        // 与 RefreshIfChangedAsync 同一口径:重查当前 index/HEAD 的 blob id(不复用
                        // 请求里过期的快照值),且按实际显示侧计算——两侧口径一致,重载判定才闭环。
                        var revision = await _gitService.GetDiffRevisionAsync(
                            _request.RepositoryPath,
                            Path,
                            DisplayIsStaged,
                            DisplayIsUntracked,
                            _lifetimeCancellation.Token).ConfigureAwait(true);
                        if (!_disposed && generation == _loadGeneration)
                        {
                            _diffRevision = revision;
                        }
                    }
                    catch (OperationCanceledException) when (_disposed)
                    {
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
                    {
                        _logService.Write("WARNING", $"无法记录 Diff 修订标识：{ex.Message}");
                    }
                }
            }
        }
    }

    /// <summary>D1 不可变构建结果:在 UI 线程之外产出,单次换入可观察集合。
    /// <see cref="AddedCount"/>,<see cref="RemovedCount"/>,<see cref="ChangeCount"/> 在填充
    /// <see cref="Lines"/> 的同一次遍历中累加(D10),不再有填充后的三次全量扫描。</summary>
    private sealed record DiffBuildResult(
        IReadOnlyList<GitDiffLine> Lines,
        IReadOnlyList<GitSideBySideRow> Rows,
        IReadOnlyList<GitDiffHunk> Hunks,
        ReadOnlyContentTier CapacityTier,
        int AddedCount,
        int RemovedCount,
        int ChangeCount,
        string? Notice,
        bool Binary);

    /// <summary>diff 构建阶段(纯 CPU,无 UI/可观察状态):行尾归一 → 限量填充 → 并排对齐 →
    /// 单遍计数。可安全在 Task.Run 内执行(只读入参 GitFileDiff,产出不可变快照)。</summary>
    private static DiffBuildResult BuildDiffSnapshot(GitFileDiff diff, ReadOnlyContentLimits limits)
    {
        // 折叠“仅行尾换行符状态变化”的 删/增 对(文字完全相同),避免末尾出现
        // 实际没有内容更改却标红的假变更块;通知行保留,原因仍可见。
        diff = DiffLineEndingAdjuster.Adjust(diff);

        if (diff.IsBinary)
        {
            return new DiffBuildResult([], [], [], ReadOnlyContentTier.Full, 0, 0, 0, "二进制文件，无法显示 diff。", Binary: true);
        }

        var sourceLineCount = diff.Hunks.Sum(hunk => hunk.Lines.Count);
        var capacityTier = limits.ForDiff(sourceLineCount);

        // 限量填充 + 单遍统计(替代原先 Added/Removed/ChangeCount 的三次全量扫描)。
        // 切块规则与 DiffDocumentBuilders.CountBlocks 完全一致:提示行不切断变更块。
        var lines = new List<GitDiffLine>(Math.Min(sourceLineCount, limits.FullDiffLines));
        var addedCount = 0;
        var removedCount = 0;
        var changeCount = 0;
        var inChangeBlock = false;
        var capped = false;
        foreach (var hunk in diff.Hunks)
        {
            foreach (var line in hunk.Lines)
            {
                if (lines.Count >= limits.FullDiffLines)
                {
                    capped = true;
                    break;
                }

                lines.Add(line);
                switch (line.Kind)
                {
                    case GitDiffLineKind.Added:
                        addedCount++;
                        if (!inChangeBlock)
                        {
                            changeCount++;
                            inChangeBlock = true;
                        }
                        break;
                    case GitDiffLineKind.Removed:
                        removedCount++;
                        if (!inChangeBlock)
                        {
                            changeCount++;
                            inChangeBlock = true;
                        }
                        break;
                    case GitDiffLineKind.Notice:
                        break;
                    default:
                        inChangeBlock = false;
                        break;
                }
            }

            if (capped)
            {
                break;
            }
        }

        var rows = new List<GitSideBySideRow>(Math.Min(sourceLineCount, limits.FullDiffLines));
        foreach (var row in diff.ToSideBySideRows())
        {
            if (rows.Count >= limits.FullDiffLines)
            {
                break;
            }

            rows.Add(row);
        }

        string? notice = null;
        if (capped)
        {
            notice = capacityTier == ReadOnlyContentTier.Summary
                ? $"diff 过大，已进入摘要模式并仅显示前 {limits.FullDiffLines:N0} 行。"
                : $"大型 diff，已加载前 {limits.FullDiffLines:N0} 行阅读窗口。";
        }
        else if (diff.IsNewFile && diff.Hunks.Count == 0)
        {
            notice = "空文件。";
        }

        return new DiffBuildResult(lines, rows, diff.Hunks, capacityTier, addedCount, removedCount, changeCount, notice, Binary: false);
    }

    /// <summary>D1 单次 UI 线程安装:把不可变构建结果换入可观察集合(集合由调用方保证为空——
    /// 初始加载本就为空,重载前由 ReloadDiffAsync 清空)。代次 + 释放双门:迟到的旧结果
    /// 既不覆盖更新的加载,也不写已关闭的标签。返回是否真的安装。</summary>
    private bool InstallDiffSnapshot(DiffBuildResult built, int generation)
    {
        if (_disposed || generation != _loadGeneration)
        {
            return false;
        }

        if (built.Binary)
        {
            _addedCount = 0;
            _removedCount = 0;
            _changeCount = 0;
            DiffNotice = built.Notice ?? "二进制文件，无法显示 diff。";
            return true;
        }

        Hunks = built.Hunks;
        OnPropertyChanged(nameof(Hunks));
        CapacityTier = built.CapacityTier;

        // One Reset per projection prevents AvalonEdit/list bindings from laying out once per
        // diff line. The snapshot is already fully built off the UI thread.
        DiffLines.ReplaceRange(built.Lines);
        SideBySideRows.ReplaceRange(built.Rows);

        _addedCount = built.AddedCount;
        _removedCount = built.RemovedCount;
        _changeCount = built.ChangeCount;
        if (built.Notice is not null)
        {
            DiffNotice = built.Notice;
        }

        return true;
    }

    /// <summary>把动作排回加载入口捕获的同步上下文并等待其完成(D1 安装/发布的“单次
    /// Dispatcher 跳”)。已在该上下文(同步快速路径)或无上下文(测试环境)时就地执行,
    /// 保持既有时序;动作异常经 Task 传播给加载方。</summary>
    private static Task RunOnUiAsync(SynchronizationContext? context, Action action)
    {
        if (context is null || context.Equals(SynchronizationContext.Current))
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ =>
        {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, null);
        return tcs.Task;
    }

    private static GitFileDiff? CollectWindow(IReadOnlyList<GitDiffEvent> events, string path, bool staged)
    {
        var hunks = new List<GitDiffHunk>();
        GitDiffMetadataEvent? metadata = null;
        GitDiffHunkEvent? current = null;
        List<GitDiffLine>? lines = null;
        foreach (var item in events)
        {
            switch (item)
            {
                case GitDiffMetadataEvent value: metadata = value; break;
                case GitDiffHunkEvent value:
                    Flush();
                    current = value;
                    lines = [new GitDiffLine(GitDiffLineKind.HunkHeader, null, null, value.Header)];
                    break;
                case GitDiffLineEvent value when lines is not null: lines.Add(value.Line); break;
                case GitDiffCompletedEvent { ExitCode: not 0 }: return null;
            }
        }
        Flush();
        return new GitFileDiff(path, metadata?.OldPath, staged, metadata?.IsBinary == true,
            metadata?.IsNewFile == true, hunks, HasMetadata: metadata is not null);

        void Flush()
        {
            if (current is null || lines is null) return;
            hunks.Add(new GitDiffHunk(current.OldStart, current.OldCount, current.NewStart, current.NewCount, current.Header, lines));
            current = null;
            lines = null;
        }
    }

    private static string ShortHash(string hash) => hash.Length <= 7 ? hash : hash[..7];
}
