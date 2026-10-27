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

public enum GitDiffMode
{
    Inline,
    SideBySide
}

/// <summary>Source-control identity shown on a diff tab.</summary>
public enum DiffTabStatus
{
    Modified,
    Staged,
    Untracked,
    Commit
}

/// <summary>Editor reading position captured when the preview loses the editor: vertical scroll
/// offset, horizontal offset, caret line/column and the document offsets of folded sections
/// (restored on re-activate). CaretLine is a global source line; windowed documents translate it
/// to the currently loaded AvalonEdit document before restoring the caret.</summary>
public sealed record EditorViewState(
    double VerticalOffset,
    int CaretLine,
    int CaretColumn,
    IReadOnlySet<int>? FoldedOffsets = null,
    IReadOnlySet<string>? FoldedSymbolIds = null,
    double HorizontalOffset = 0);

/// <summary>A git diff the source-control view asks the shared editor to open. Covers working-tree
/// changes (staged / unstaged / untracked) and commit-file diffs (<see cref="CommitHash"/> set).</summary>
public sealed record GitDiffRequest(
    string RepositoryPath,
    string Path,
    bool IsStaged,
    bool IsUntracked,
    string? CommitHash = null,
    bool IsPreview = false,
    string? HeadBlobId = null,
    string? IndexBlobId = null,
    ProjectWorkspaceContext? WorkspaceContext = null)
{
    /// <summary>Stable identity so re-opening the same change activates its existing tab. The
    /// working-tree side (w = 未暂存, s = 已暂存, u = 未跟踪) is part of the key: the staged and
    /// unstaged diffs of one file are different documents, and keying on path alone made a re-open
    /// from the other side silently activate the stale tab (and layout restore dropped one of the
    /// two tabs under the duplicate-key dedupe).</summary>
    public string TabKey => CommitHash is not null
        ? $"diff:{CommitHash}:{Path}"
        : $"diff:{Path}:{(IsUntracked ? "u" : IsStaged ? "s" : "w")}";
}

/// <summary>VS Code-style editor group shared by the explorer and the source-control panes. Explorer
/// file opens become read-only content tabs and source-control selections become diff tabs — all in
/// the same tab strip, closable and switchable like a real editor group.</summary>
public sealed partial class EditorAreaViewModel : ObservableObject, IShutdownParticipant, IDisposable
{
    // 标签条本身只保留轻量身份/阅读状态；文件文本与派生投影按估算的驻留大小做 LRU
    // 回收。64 MiB 软上限(低内存定位;只回收非选中标签的载荷,点击时重载):
    // 每个编辑器组当前选中的文件必须保留,以避免可见编辑器在后台被清空;
    // 超出预算的原因只可能是这些可见文件本身过大。
    private const long OpenFilePayloadBudgetBytes = 64L * 1024 * 1024;

    /// <summary>UI 线程调度抽象(标签回载等需要切回 UI 线程)。不要直接读
    /// <c>Application.Current.Dispatcher</c> —— 见架构守卫测试。</summary>
    private readonly IUiDispatcher _uiDispatcher = new WpfUiDispatcher();
    private int _maxOpenTabs = 30;
    private bool _enablePreviewTabs = true;
    private bool _editorLimitEnabled = true;
    private ReadOnlyContentLimits _contentLimits = ReadOnlyContentLimits.Default;
    private bool _applyingSettings;

    /// <summary>Blame 注解的会话级偏好(GitLens 式全局开关心智):任一标签开/关时记录,
    /// 之后新开的文件标签在装载完成后自动跟随;已开标签不追溯(窗口化/二进制自然静默放弃)。</summary>
    public bool BlamePreferred { get; private set; }

    private readonly IGitService _gitService;
    private readonly IUiLogService _logService;
    private readonly IClipboardService _clipboard;
    private readonly IConfirmationService? _confirmationService;
    private readonly ICodeFileTypeRegistry _fileTypes;
    private readonly ITextDocumentDecoder _decoder;
    private readonly ICodeOutlineParser _outlineParser;
    private readonly ITextSearchService _searchService;
    private readonly IProjectLauncher? _projectLauncher;
    private ISettingsService? _settingsService;
    private IProjectWorkspaceService? _workspaceService;
    private IApplicationStateStore? _stateStore;
    private readonly Dictionary<string, ISettingsSession> _languageSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<EditorGroupViewModel> _wiredGroups = [];
    private readonly RevisionGate _settingsRevisionGate = new();
    private readonly SemaphoreSlim _settingsApplyGate = new(1, 1);
    private readonly SemaphoreSlim _settingsNotificationGate = new(1, 1);
    private readonly SemaphoreSlim _previewOptionsGate = new(1, 1);
    private readonly object _pendingPreviewOptionsGate = new();
    private readonly Dictionary<FilePreviewTab, int> _pendingPreviewOptionWrites = new(ReferenceEqualityComparer.Instance);
    // Use the context that owns this view model. A process-global WPF Application may belong to
    // another dispatcher (notably the shared STA test host) whose queue is currently idle.
    private readonly SynchronizationContext? _uiContext;

    /// <summary>外部文件监听(源代码视图自动刷新):按目录复用的 FileSystemWatcher 只报告已打开
    /// 的 <see cref="FilePreviewTab"/> 目标文件;关闭/预览槽替换/LRU 驱逐时经差量同步取消监听。</summary>
    private readonly IFileContentWatcher _fileContentWatcher;
    private readonly HashSet<string> _watchedFilePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly IGitRepositoryWatcher _repositoryWatcher;
    private ProjectWorkspaceContext? _activeWorkspaceContext;
    private bool _workspaceContextBoundaryEnabled;
    private CancellationTokenSource? _workspaceSwitchCancellation;
    private bool _suppressWorkspacePersistence;

    /// <summary>可拆分的编辑器组网格:编辑器区现在由多个组构成,本类保留为统一门面。
    /// <see cref="OpenTabs"/> / <see cref="SelectedTab"/> 是活动组的兼容投影,视图渲染使用具体组。</summary>
    public EditorGroupsViewModel Groups { get; }

    /// <summary>活动组的标签集合投影(组切换时实例随之切换)。</summary>
    public ObservableCollection<EditorTabItem> OpenTabs => Groups.ActiveGroup?.Tabs ?? _emptyTabs;

    /// <summary>活动组的当前标签投影。</summary>
    public EditorTabItem? SelectedTab
    {
        get => Groups.ActiveGroup?.SelectedTab;
        set
        {
            if (Groups.ActiveGroup is { } group)
            {
                group.SelectedTab = value;
            }
        }
    }

    private static readonly ObservableCollection<EditorTabItem> _emptyTabs = [];

    public bool HasSelectedTab => SelectedTab is not null;

    public EditorAreaViewModel(IGitService gitService, IUiLogService logService,
        IFileContentWatcher? fileContentWatcher = null,
        IGitRepositoryWatcher? repositoryWatcher = null)
        : this(gitService, logService, NullClipboardService.Instance, fileContentWatcher, repositoryWatcher)
    {
    }

    public EditorAreaViewModel(IGitService gitService, IUiLogService logService, IClipboardService clipboard,
        IFileContentWatcher? fileContentWatcher = null,
        IGitRepositoryWatcher? repositoryWatcher = null)
        : this(gitService, logService, clipboard, CodeFileTypeRegistry.Instance, TextDocumentDecoder.Instance, CodeOutlineParser.Instance, fileContentWatcher, repositoryWatcher)
    {
    }

    public EditorAreaViewModel(
        IGitService gitService,
        IUiLogService logService,
        IClipboardService clipboard,
        ICodeFileTypeRegistry fileTypes,
        ITextDocumentDecoder decoder,
        ICodeOutlineParser outlineParser,
        IFileContentWatcher? fileContentWatcher = null,
        IGitRepositoryWatcher? repositoryWatcher = null)
        : this(gitService, logService, clipboard, fileTypes, decoder, outlineParser, TextSearchService.Instance, null, null, fileContentWatcher, repositoryWatcher)
    {
    }

    public EditorAreaViewModel(
        IGitService gitService,
        IUiLogService logService,
        IClipboardService clipboard,
        ICodeFileTypeRegistry fileTypes,
        ITextDocumentDecoder decoder,
        ICodeOutlineParser outlineParser,
        ITextSearchService searchService,
        IProjectLauncher? projectLauncher,
        IConfirmationService? confirmationService = null,
        IFileContentWatcher? fileContentWatcher = null,
        IGitRepositoryWatcher? repositoryWatcher = null)
    {
        _gitService = gitService;
        _logService = logService;
        _clipboard = clipboard;
        _confirmationService = confirmationService;
        _fileTypes = fileTypes;
        _decoder = decoder;
        _outlineParser = outlineParser;
        _searchService = searchService;
        _projectLauncher = projectLauncher;
        // Only a WPF dispatcher context owns UI-bound collections. Test frameworks also install
        // custom SynchronizationContexts; capturing one of those makes background settings
        // notifications depend on the runner's scheduling/pump and can stall under CI load.
        _uiContext = SynchronizationContext.Current is System.Windows.Threading.DispatcherSynchronizationContext
            ? SynchronizationContext.Current
            : null;
        _fileContentWatcher = fileContentWatcher ?? NullFileContentWatcher.Instance;
        _repositoryWatcher = repositoryWatcher ?? NullGitRepositoryWatcher.Instance;
        _fileContentWatcher.FileChanged += OnFileChanged;
        _repositoryWatcher.ChangesDetected += OnRepositoryChangesDetected;
        Groups = new EditorGroupsViewModel();
        WireGroups(Groups.ActiveGroup);
        Groups.LayoutChanged += (_, _) =>
        {
            foreach (var group in Groups.Groups)
            {
                WireGroups(group);
            }

            OnGroupTabsCollectionChanged();
            PersistLayoutAsync();
        };
        Groups.ActiveGroupChanged += (_, _) =>
        {
            WireGroups(Groups.ActiveGroup);
            OnGroupSelectionChanged();
            OnGroupTabsCollectionChanged();
        };
    }

    public EditorAreaViewModel(
        IGitService gitService,
        IUiLogService logService,
        IClipboardService clipboard,
        ICodeFileTypeRegistry fileTypes,
        ITextDocumentDecoder decoder,
        ICodeOutlineParser outlineParser,
        ITextSearchService searchService,
        IProjectLauncher? projectLauncher,
        ISettingsService settingsService,
        IProjectWorkspaceService workspaceService,
        IApplicationStateStore stateStore,
        IConfirmationService? confirmationService = null,
        IFileContentWatcher? fileContentWatcher = null,
        IGitRepositoryWatcher? repositoryWatcher = null)
        : this(gitService, logService, clipboard, fileTypes, decoder, outlineParser, searchService, projectLauncher, confirmationService, fileContentWatcher, repositoryWatcher)
    {
        _settingsService = settingsService;
        _workspaceService = workspaceService;
        _stateStore = stateStore;
        _activeWorkspaceContext = workspaceService.Current;
        // Editor-only embedders/tests may provide the workspace-aware constructor before the
        // first context is activated. Keep that initial, context-less surface usable; once the
        // service publishes a context, all subsequent opens are identity-bound.
        _workspaceContextBoundaryEnabled = workspaceService.Current is not null;
        _workspaceService.ContextChanged += OnWorkspaceSettingsContextChangedAsync;
    }

    /// <summary>Raised whenever the active editor tab changes (open/close/select/group switch).</summary>
    public event EventHandler? SelectedTabChanged;

    /// <summary>Raised whenever the tabs visible in the active group change (membership, order or
    /// group switch) — 驱动 Workbench 的活动组投影重建。</summary>
    public event EventHandler? OpenTabsChanged;

    /// <summary>Raised after a Diff tab applies a hunk and reloads its content. GitViewModel uses
    /// this to refresh status and Explorer decorations without coupling the editor to the SCM page.</summary>
    public event EventHandler<GitDiffRequest>? DiffMutationCompleted;

    public int ShutdownOrder => 200;

    private void WireGroups(EditorGroupViewModel group)
    {
        if (!_wiredGroups.Add(group))
        {
            return;
        }

        group.CloseTabRequested += (_, tab) => CloseTab(tab);
        group.CloseAllTabsRequested += (_, _) => CloseAllTabsInGroup(group);
        group.CloseOtherTabsRequested += (_, keep) =>
        {
            foreach (var tab in group.Tabs.Where(tab => !ReferenceEquals(tab, keep)).ToArray())
            {
                CloseTab(tab);
            }

            if (keep is not null && group.Tabs.Contains(keep))
            {
                group.SelectedTab = keep;
            }
        };
        group.SplitRequested += (_, args) =>
            Groups.SplitGroup(group, args.Orientation, args.Tab ?? group.SelectedTab);
        group.CopyPathRequested += (_, tab) => _clipboard.SetText(tab.Path);
        group.OpenInExternalEditorRequested += (_, preview) => _ = OpenInExternalEditorAsync(preview);
        group.RevealInExplorerRequested += (_, path) => RevealInSystemExplorer(path);
        group.SelectedTabChanged += (_, _) =>
        {
            OnGroupSelectionChanged();
            Groups.TouchEditorMru(group.SelectedTab);
        };
        group.TabsChanged += (_, _) =>
        {
            OnGroupTabsCollectionChanged();
            Groups.PruneEditorMru();
            PersistLayoutAsync();
            // 打开/关闭/预览槽替换/LRU 驱逐/跨组移动都经 Tabs 集合变化:差量同步文件监听集合。
            SyncFileWatcher();
        };
    }

    /// <summary>外部文件变更 → 重载所有打开该文件的标签,不区分预览/正式或活动状态。</summary>
    private void OnFileChanged(object? sender, FileChangedEventArgs e)
    {
        foreach (var tab in Groups.AllTabs.OfType<FilePreviewTab>())
        {
            if (string.Equals(tab.Path, e.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                _ = ReloadPreviewAndTrimAsync(tab);
            }
        }
        foreach (var tab in Groups.AllTabs.OfType<DiffTab>())
        {
            if (GetDiffWatchPath(tab) is { } path &&
                string.Equals(path, e.FullPath, StringComparison.OrdinalIgnoreCase))
                _ = RefreshDiffForFileChangeAsync(tab);
        }
    }

    private async Task RefreshDiffForFileChangeAsync(DiffTab tab)
    {
        try
        {
            if (tab.IsLoaded) await tab.RefreshIfChangedAsync();
            else await tab.ReloadDiffAsync();
        }
        catch (Exception ex)
        {
            _logService.Write("WARNING", $"无法刷新 Diff {tab.Path}：{ex.Message}");
        }
    }

    private static string? GetDiffWatchPath(DiffTab tab) => tab.Request.CommitHash is null
        ? System.IO.Path.GetFullPath(System.IO.Path.Combine(tab.Request.RepositoryPath, tab.Request.Path))
        : null;

    private void OnRepositoryChangesDetected(object? sender, GitRepositoryChangesDetectedEventArgs e)
    {
        foreach (var tab in Groups.AllTabs.OfType<DiffTab>())
        {
            if (tab.Request.CommitHash is not null ||
                !string.Equals(tab.Request.RepositoryPath, e.RepositoryPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var pathMatches = e.ChangedPaths.Contains(NormalizeGitPath(tab.Request.Path), StringComparer.OrdinalIgnoreCase);
            if (pathMatches || e.IndexChanged || e.HeadOrRefsChanged || e.IsUnknown)
            {
                _ = tab.RefreshIfChangedAsync();
            }
        }
    }

    private static string NormalizeGitPath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    /// <summary>打开标签集合 ↔ 监听集合的差量同步:Watch 新开文件,Unwatch 已关闭文件。
    /// 单一事实来源是 <see cref="EditorGroupViewModel.Tabs"/> 集合,因此不依赖具体关闭路径
    /// (关闭 / 预览槽替换 / LRU 驱逐 / 全部关闭 / 恢复重建)。</summary>
    private void SyncFileWatcher()
    {
        var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in Groups.AllTabs.OfType<FilePreviewTab>())
        {
            open.Add(tab.Path);
        }
        foreach (var tab in Groups.AllTabs.OfType<DiffTab>())
        {
            if (GetDiffWatchPath(tab) is { } path) open.Add(path);
        }

        foreach (var path in _watchedFilePaths.Except(open).ToArray())
        {
            _fileContentWatcher.Unwatch(path);
            _watchedFilePaths.Remove(path);
        }

        foreach (var path in open.Except(_watchedFilePaths))
        {
            _fileContentWatcher.Watch(path);
            _watchedFilePaths.Add(path);
        }
    }

    /// <summary>在资源管理器中定位文件或目录(VS Code "在资源管理器中显示";文件不存在时静默)。</summary>
    private static void RevealInSystemExplorer(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
    }

    /// <summary>面包屑符号段点击 → 符号选择器请求(由 MainViewModel 接线到 QuickInput)。</summary>
    public event EventHandler<EditorTabItem?>? SymbolPickerRequested;

    /// <summary>状态栏语言按钮点击 → 语言选择器请求(由 MainViewModel 接线到 QuickInput)。</summary>
    public event EventHandler<EditorTabItem?>? LanguagePickerRequested;

    /// <summary>打开当前标签的符号选择器(面包屑符号段点击)。</summary>
    [RelayCommand]
    private void OpenSymbolPicker() => SymbolPickerRequested?.Invoke(this, SelectedTab);

    /// <summary>打开当前标签的语言选择器(状态栏语言按钮;VS Code"更改语言模式")。</summary>
    [RelayCommand]
    private void OpenLanguagePicker() => LanguagePickerRequested?.Invoke(this, SelectedTab);

    /// <summary>面包屑目录/文件段点击 → 在资源管理器中定位该路径。</summary>
    [RelayCommand]
    private void RevealBreadcrumb(BreadcrumbSegment? segment)
    {
        if (segment?.FullPath is { Length: > 0 } path)
        {
            RevealInSystemExplorer(path);
        }
    }

    private bool _suppressSelectionLoad;

    private void OnGroupSelectionChanged()
    {
        foreach (var tab in Groups.AllTabs)
        {
            tab.IsActive = ReferenceEquals(tab, SelectedTab);
        }

        OnPropertyChanged(nameof(SelectedTab));
        OnPropertyChanged(nameof(HasSelectedTab));
        SelectedTabChanged?.Invoke(this, EventArgs.Empty);

        // VS Code 恢复语义:标签条先出现,内容按需加载 —— 选中预览标签时才读取文件内容。
        if (!_suppressSelectionLoad && SelectedTab is FilePreviewTab { IsLoadFinished: false } preview)
        {
            _ = LoadPreviewAndTrimAsync(preview);
        }
    }

    private async Task LoadPreviewAndTrimAsync(FilePreviewTab preview)
    {
        try
        {
            await preview.LoadAsync();
        }
        catch (Exception ex)
        {
            // Selection changes are fire-and-forget. Observe failures here so a malformed or
            // temporarily inaccessible file cannot become an unobserved task exception.
            _logService.Write("WARNING", $"无法加载文件预览 {preview.Path}：{ex.Message}");
        }
        finally
        {
            TrimOpenFilePayloadCache();
        }
    }

    private async Task ReloadPreviewAndTrimAsync(FilePreviewTab preview)
    {
        try
        {
            await preview.ReloadAsync();
        }
        catch (Exception ex)
        {
            _logService.Write("WARNING", $"无法重载文件预览 {preview.Path}：{ex.Message}");
        }
        finally
        {
            TrimOpenFilePayloadCache();
        }
    }

    /// <summary>按文件预览载荷的估算大小做全局 LRU 回收。标签仍留在标签条中，下一次选中
    /// 时由 <see cref="FilePreviewTab.LoadAsync"/> 从磁盘懒加载；每个编辑器组的当前标签受
    /// 保护，因为拆分编辑器的所有组都是同时可见的。这里刻意释放引用而不强制 GC，交由
    /// CLR 在合适的时机回收大字符串、解析结果和派生集合。</summary>
    private void TrimOpenFilePayloadCache()
    {
        var loaded = Groups.AllTabs.OfType<FilePreviewTab>()
            .Where(tab => tab.IsPayloadLoaded)
            .ToArray();
        var retained = loaded.Sum(tab => tab.RetainedMemoryBytes);
        if (retained <= OpenFilePayloadBudgetBytes)
        {
            return;
        }

        var protectedTabs = Groups.Groups
            .Select(group => group.SelectedTab)
            .OfType<FilePreviewTab>()
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var visited = new HashSet<EditorTabItem>(ReferenceEqualityComparer.Instance);

        // EditorsInMruOrder is newest-first. Reverse it so the least recently used payload is
        // considered first; AllTabs is a defensive fallback for restored tabs not yet touched.
        foreach (var candidate in Groups.EditorsInMruOrder.Reverse().Concat(Groups.AllTabs))
        {
            if (!visited.Add(candidate)
                || candidate is not FilePreviewTab preview
                || protectedTabs.Contains(preview)
                || !preview.IsPayloadLoaded)
            {
                continue;
            }

            var before = preview.RetainedMemoryBytes;
            preview.UnloadContentForCache();
            retained -= before - preview.RetainedMemoryBytes;
            if (retained <= OpenFilePayloadBudgetBytes)
            {
                break;
            }
        }
    }

    private void OnGroupTabsCollectionChanged()
    {
        OnPropertyChanged(nameof(OpenTabs));
        OpenTabsChanged?.Invoke(this, EventArgs.Empty);
        TrimOpenFilePayloadCache();
    }

    /// <summary>Opens a file preview initialized from the persisted reading options (word wrap /
    /// font size), and persists reading-option toggles back to settings for the next
    /// session. <paramref name="permanent"/> (树双击) opens a regular tab instead of a preview,
    /// and promotes an already-open preview of the same file.</summary>
    public async Task OpenFileAsync(
        string path,
        bool permanent = false,
        ProjectWorkspaceContext? expectedWorkspaceContext = null)
    {
        var context = expectedWorkspaceContext ?? _workspaceService?.Current;
        if (expectedWorkspaceContext is not null && !IsWorkspaceContextCurrent(context)) return;
        var languageId = _fileTypes.FromPath(path).LanguageId;
        var options = await EnsureReadingOptionsAsync(languageId);
        if (!IsWorkspaceContextCurrent(context)) return;
        var tab = new FilePreviewTab(path, _decoder, _fileTypes, _searchService, _outlineParser, _uiDispatcher)
        {
            ContentLimits = _contentLimits,
            BlameLoader = LoadBlameForAsync,
            AutoEnableBlameOnce = BlamePreferred,
            WordWrap = options.WordWrap,
            ShowMinimap = options.ShowMinimap,
            ShowStickyScroll = options.StickyScroll,
            MinimapRenderCharacters = options.MinimapRenderCharacters,
            MinimapWidth = options.MinimapWidth,
            LineNumbersRelative = options.LineNumbersRelative,
            RulerColumns = options.RulerColumns,
            FontSize = options.FontSize > 0 ? options.FontSize : 14,
            FontFamily = options.FontFamily,
            ShowLineNumbers = options.ShowLineNumbers,
            ShowIndentGuides = options.ShowIndentGuides,
            ShowFoldingControls = options.ShowFoldingControls,
            IsPreview = _enablePreviewTabs && !permanent,
        };
        await RestoreReadingStateAsync(tab, context);
        if (!IsWorkspaceContextCurrent(context))
        {
            tab.ReleaseResources();
            return;
        }

        tab.PropertyChanged += OnPreviewOptionChanged;
        await OpenTabAsync(tab, promote: permanent, workspaceContext: context);
    }

    /// <summary>Opens a read-only file and positions the active preview at a 1-based line/column.
    /// Search and other navigation surfaces use this façade instead of reaching into a tab's
    /// loading implementation.</summary>
    public async Task OpenFileAtAsync(
        string path,
        int line,
        int column,
        bool permanent = false,
        ProjectWorkspaceContext? expectedWorkspaceContext = null)
    {
        var context = expectedWorkspaceContext ?? _workspaceService?.Current;
        if (expectedWorkspaceContext is not null && !IsWorkspaceContextCurrent(context)) return;
        await OpenFileAsync(path, permanent, expectedWorkspaceContext);
        if (!IsWorkspaceContextCurrent(context)) return;
        if (SelectedTab is not FilePreviewTab preview)
        {
            return;
        }

        preview.GoToLineInput = $"{Math.Max(1, line)}:{Math.Max(1, column)}";
        preview.GoToLineCommand.Execute(null);
    }

    private CodeReadingOptions? _cachedReadingOptions;
    private DiffReadingOptions? _cachedDiffOptions;

    private async Task<CodeReadingOptions> EnsureReadingOptionsAsync(string? languageId = null)
    {
        if (_settingsService is not null)
        {
            var session = await GetSettingsSessionAsync(languageId);
            var snapshot = session.Current ?? await session.RefreshAsync();
            _maxOpenTabs = snapshot.Effective(BuiltInSettingsCatalog.EditorLimitValue);
            _enablePreviewTabs = snapshot.Effective(BuiltInSettingsCatalog.EnablePreview);
            _contentLimits = SettingsOptionsMapper.ReadingLimits(snapshot);
            _cachedDiffOptions = SettingsOptionsMapper.Diff(snapshot);
            return SettingsOptionsMapper.CodeReading(snapshot);
        }
        return _cachedReadingOptions ??= new CodeReadingOptions();
    }

    /// <summary>Persists reading-option toggles. Fire-and-forget:
    /// a failing write must never break the preview.</summary>
    private void OnPreviewOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not FilePreviewTab tab)
        {
            return;
        }

        // Blame 偏好与会话级跟随(不持久化、不依赖设置服务)。
        if (e.PropertyName == nameof(FilePreviewTab.IsBlameVisible))
        {
            BlamePreferred = tab.IsBlameVisible;
        }

        if (_settingsService is null || _applyingSettings)
        {
            return;
        }

        if (e.PropertyName is not (nameof(FilePreviewTab.WordWrap)
            or nameof(FilePreviewTab.ShowMinimap)
            or nameof(FilePreviewTab.FontSize)
            or nameof(FilePreviewTab.ShowLineNumbers)
            or nameof(FilePreviewTab.ShowIndentGuides)
            or nameof(FilePreviewTab.ShowFoldingControls)
            or nameof(FilePreviewTab.ShowStickyScroll)
            or nameof(FilePreviewTab.MinimapRenderCharacters)
            or nameof(FilePreviewTab.MinimapWidth)
            or nameof(FilePreviewTab.LineNumbersRelative)
            or nameof(FilePreviewTab.RulerColumns)))
        {
            return;
        }

        // Capture the complete option set at the event boundary. Persistence is serialized and
        // settings notifications from an earlier write may update the live tab before a queued
        // write starts; reading the tab inside that queued write would then persist stale values
        // over a newer user choice.
        var options = new PreviewOptionsSnapshot(
            tab.FileType.LanguageId,
            tab.WordWrap,
            tab.ShowMinimap,
            tab.FontSize,
            tab.ShowLineNumbers,
            tab.ShowIndentGuides,
            tab.ShowFoldingControls,
            tab.ShowStickyScroll,
            tab.MinimapRenderCharacters,
            tab.MinimapWidth,
            tab.LineNumbersRelative,
            tab.RulerColumns);
        MarkPreviewOptionsPending(tab);
        _ = PersistPreviewOptionsAsync(tab, options, _workspaceService?.Current);
    }

    private async Task PersistPreviewOptionsAsync(
        FilePreviewTab tab,
        PreviewOptionsSnapshot options,
        ProjectWorkspaceContext? workspaceContext)
    {
        var gateEntered = false;
        try
        {
            await _previewOptionsGate.WaitAsync();
            gateEntered = true;
            if (!IsWorkspaceContextCurrent(workspaceContext)) return;
            if (_settingsService is not null)
            {
                var session = await GetSettingsSessionAsync(options.LanguageId, workspaceContext);
                if (!IsWorkspaceContextCurrent(workspaceContext)) return;
                // 持久化作者字号(options.FontSize 是显示字号 = 作者字号 × uiScale),避免下次加载
                // 重复放大。倍率取自会话快照而非静态缓存,保证与显示侧一致且不受测试静态污染。
                var scale = UiFontService.ClampScale(
                    session.Current?.Effective(BuiltInSettingsCatalog.UiScale) ?? UiFontService.DefaultScale);
                SettingOperation[] operations =
                [
                    new(BuiltInSettingsCatalog.EditorWordWrap.Id, JsonValue.Create(options.WordWrap ? "on" : "off"), LanguageId: options.LanguageId),
                    new(BuiltInSettingsCatalog.MinimapEnabled.Id, JsonValue.Create(options.ShowMinimap), LanguageId: options.LanguageId),
                    // 缩放字号写回 User 作用域(不带 LanguageId):Ctrl+滚轮调整的是"整个阅读器"的
                    // 字号,所有语言一起变。若写成语言作用域,调整 .cs 只对 csharp 生效,
                    // .csproj(xml)等其他语言仍是旧值,标签之间就会显示不同字号。
                    new(BuiltInSettingsCatalog.EditorFontSize.Id,
                        JsonValue.Create(Math.Round(options.FontSize / scale, 1))),
                    new(BuiltInSettingsCatalog.IndentationGuides.Id, JsonValue.Create(options.ShowIndentGuides), LanguageId: options.LanguageId),
                    new(BuiltInSettingsCatalog.Folding.Id, JsonValue.Create(options.ShowFoldingControls), LanguageId: options.LanguageId),
                    new(BuiltInSettingsCatalog.StickyScroll.Id, JsonValue.Create(options.ShowStickyScroll), LanguageId: options.LanguageId),
                    new(BuiltInSettingsCatalog.MinimapRenderCharacters.Id, JsonValue.Create(options.MinimapRenderCharacters), LanguageId: options.LanguageId),
                    new(BuiltInSettingsCatalog.MinimapWidth.Id, JsonValue.Create(options.MinimapWidth), LanguageId: options.LanguageId),
                    new(BuiltInSettingsCatalog.LineNumbers.Id,
                        JsonValue.Create(!options.ShowLineNumbers ? "off" : options.LineNumbersRelative ? "relative" : "on"),
                        LanguageId: options.LanguageId),
                    new(BuiltInSettingsCatalog.EditorRulers.Id,
                        JsonValue.Create(options.RulerColumns is { Length: > 0 }
                            ? string.Join(",", options.RulerColumns.Select(value => value.ToString(CultureInfo.InvariantCulture)))
                            : string.Empty),
                        LanguageId: options.LanguageId),
                ];
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var result = await session.CommitAsync(SettingScope.User, operations);
                    if (result.Status == SettingsCommitStatus.Success)
                    {
                        return;
                    }

                    if (attempt == 2 || result.Status is not (SettingsCommitStatus.FileError or SettingsCommitStatus.Conflict))
                    {
                        return;
                    }

                    await Task.Delay(50 * (attempt + 1));
                    await session.RefreshAsync();
                }
                return;
            }
        }
        catch (Exception ex)
        {
            // Settings persistence is best effort; a disposed store/session or a transient
            // conflict must never surface from the property-changed fire-and-forget path.
            _logService.Write("WARNING", $"无法保存编辑器阅读选项：{ex.Message}");
        }
        finally
        {
            if (gateEntered)
            {
                try
                {
                    _previewOptionsGate.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Shutdown can dispose the gate after the write acquired it.
                }
            }

            MarkPreviewOptionsCompleted(tab);
        }
    }

    private void MarkPreviewOptionsPending(FilePreviewTab tab)
    {
        lock (_pendingPreviewOptionsGate)
        {
            _pendingPreviewOptionWrites.TryGetValue(tab, out var count);
            _pendingPreviewOptionWrites[tab] = count + 1;
        }
    }

    private void MarkPreviewOptionsCompleted(FilePreviewTab tab)
    {
        lock (_pendingPreviewOptionsGate)
        {
            if (!_pendingPreviewOptionWrites.TryGetValue(tab, out var count) || count <= 1)
            {
                _pendingPreviewOptionWrites.Remove(tab);
            }
            else
            {
                _pendingPreviewOptionWrites[tab] = count - 1;
            }
        }
    }

    private bool HasPendingPreviewOptions(FilePreviewTab tab)
    {
        lock (_pendingPreviewOptionsGate)
        {
            return _pendingPreviewOptionWrites.ContainsKey(tab);
        }
    }

    private sealed record PreviewOptionsSnapshot(
        string LanguageId,
        bool WordWrap,
        bool ShowMinimap,
        double FontSize,
        bool ShowLineNumbers,
        bool ShowIndentGuides,
        bool ShowFoldingControls,
        bool ShowStickyScroll,
        bool MinimapRenderCharacters,
        double MinimapWidth,
        bool LineNumbersRelative,
        double[]? RulerColumns);

    public async Task OpenDiffAsync(GitDiffRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = request.WorkspaceContext ?? _workspaceService?.Current;
        if (request.WorkspaceContext is not null && !IsWorkspaceContextCurrent(context)) return;
        if (_workspaceService is not null && !IsWorkspaceRepositoryCurrent(context, request.RepositoryPath)) return;
        // Diff editors follow the same reading preference as the code preview (default 14), so the
        // inline/side-by-side editors no longer inherit the smaller window font size.
        var options = await EnsureReadingOptionsAsync();
        var diffOptions = _cachedDiffOptions ?? new DiffReadingOptions();
        var tab = new DiffTab(_gitService, request, _logService, _clipboard, _confirmationService)
        {
            ContentLimits = _contentLimits,
            EditorFontSize = options.FontSize > 0 ? options.FontSize : 14,
            FontFamily = options.FontFamily,
            DiffMode = diffOptions.DefaultLayout == DiffLayoutMode.SideBySide ? GitDiffMode.SideBySide : GitDiffMode.Inline,
            IsLayoutManuallySelected = false,
            IsContextCollapsed = diffOptions.CollapseUnchangedContext,
            ShowIntralineChanges = diffOptions.ShowIntralineChanges,
            ShowOverviewRuler = diffOptions.ShowOverviewRuler,
            SynchronizeScrolling = diffOptions.SynchronizeScrolling,
            UseInlineWhenNarrow = diffOptions.UseInlineWhenNarrow,
            IgnoreTrimWhitespace = diffOptions.IgnoreWhitespaceEndOfLine,
            // 更改页单击打开的 diff 走预览语义(斜体、被下一次打开替换),与 VS Code SCM 一致。
            IsPreview = request.IsPreview && _enablePreviewTabs,
        };
        tab.HunkMutationCompleted += OnDiffHunkMutationCompleted;
        // diff 右键"在代码标签页打开文件"→ 在共享编辑器组打开该文件。
        tab.OpenInCodeRequested += (_, _) => _ = OpenFileAsync(
            request.Path,
            expectedWorkspaceContext: request.WorkspaceContext ?? context);
        if (!IsWorkspaceRepositoryCurrent(context, request.RepositoryPath))
        {
            tab.ReleaseResources();
            return;
        }

        await OpenTabAsync(tab, workspaceContext: context);
    }

    private async Task OpenTabAsync(
        EditorTabItem tab,
        bool promote = false,
        ProjectWorkspaceContext? workspaceContext = null)
    {
        if (!IsWorkspaceContextCurrent(workspaceContext))
        {
            tab.ReleaseResources();
            return;
        }

        // 跨组去重:同一个 TabKey 只允许存在于一个组;再次打开时激活已有标签所在的组。
        var existing = Groups.FindTabByKey(tab.TabKey);
        if (existing is { } hit)
        {
            // OpenFileAsync/OpenDiffAsync build a candidate before the cross-group lookup. Dispose
            // that unused candidate (FilePreviewTab owns a theme subscription and debounce timer)
            // so repeatedly opening an already-open file cannot leak a hidden cache entry.
            if (tab is FilePreviewTab candidatePreview)
            {
                candidatePreview.PropertyChanged -= OnPreviewOptionChanged;
            }
            tab.ReleaseResources();
            // 双击再次打开(树双击/标签双击):已打开的预览直接转正为常驻标签。
            if (promote && hit.Tab is { IsPreview: true } existingPreview)
            {
                existingPreview.IsPreview = false;
            }
            ActivateTab(hit.Tab);
            return;
        }

        var group = Groups.ActiveGroup;
        // VS Code preview semantics: opening a new preview replaces the current preview tab instead
        // of stacking another (preview = 斜体标签,固定后即常驻). 预览槽在每个组内独立且跨类型
        // 共用(文件预览与更改页的 diff 预览互斥,同一时刻每组至多一个预览);已固定的预览
        // (IsPinned)不参与替换——固定的标签必须真正保留下来。
        if (_enablePreviewTabs && tab.IsPreview && group.SelectedTab is { IsPreview: true, IsPinned: false } currentPreview)
        {
            // 预览槽替换 = 真正的关闭:必须走 MarkTabClosed,否则被替换的预览标签仍留在
            // 工作台条带里(IsClosed 未置位 → 投影不清理),而它已不属于任何组,
            // 点关闭时 CloseTab 找不到所属组而静默失败,永远无法关闭。
            MarkTabClosed(currentPreview);
            group.Tabs.Remove(currentPreview);
        }

        if (_editorLimitEnabled && group.Tabs.Count >= _maxOpenTabs)
        {
            // VS Code workbench.editor.limit:驱逐最近最少使用的标签(排除当前标签与固定标签),而非简单 FIFO;
            // 设置关闭时不施加标签上限。
            if (group.EvictLeastRecentlyUsed() is { } leastRecentlyUsed)
            {
                // 同预览槽替换:驱逐即关闭,完整关闭语义避免工作台条带残留幽灵标签。
                MarkTabClosed(leastRecentlyUsed);
                group.Tabs.Remove(leastRecentlyUsed);
            }
        }

        group.Tabs.Add(tab);
        group.SelectedTab = tab;
        try
        {
            await tab.LoadAsync();
        }
        finally
        {
            // The newly selected tab is protected by TrimOpenFilePayloadCache; older inactive
            // tabs can be released immediately after its payload has become resident.
            TrimOpenFilePayloadCache();
        }

        if (!IsWorkspaceContextCurrent(workspaceContext))
        {
            // The switch handler normally closes this tab. This fallback covers a switch that
            // arrives after the handler's clear pass but before a slow document load completes.
            MarkTabClosed(tab, workspaceContext, persist: workspaceContext is not null);
            if (Groups.FindGroupContaining(tab) is { } owner)
            {
                owner.Tabs.Remove(tab);
                if (ReferenceEquals(owner.SelectedTab, tab)) owner.SelectedTab = owner.SelectNextRecentlyActive();
            }
        }
    }

    /// <summary>激活已有标签所在的组并选中它(资源管理器 / Git 再次打开时的目标行为)。</summary>
    public void ActivateTab(EditorTabItem tab)
    {
        if (Groups.FindGroupContaining(tab) is not { } owning || !Groups.Groups.Contains(owning))
        {
            return;
        }

        if (!ReferenceEquals(Groups.ActiveGroup, owning))
        {
            Groups.ActiveGroup = owning;
        }

        owning.SelectedTab = tab;
    }

    [RelayCommand]
    public void CloseTab(EditorTabItem? tab)
    {
        if (tab is null)
        {
            return;
        }

        var group = Groups.FindGroupContaining(tab);
        if (group is null)
        {
            // 无主标签(已脱离任何组):补齐关闭语义(持久化/IsClosed/释放),让工作台投影
            // 能把它清理掉——静默失败会留下永远无法关闭的幽灵标签。
            RecordClosedTab(tab);
            MarkTabClosed(tab);
            return;
        }

        RecordClosedTab(tab);
        MarkTabClosed(tab);
        group.Tabs.Remove(tab);
        if (ReferenceEquals(group.SelectedTab, tab))
        {
            // VS Code 关闭回选:激活"最近使用"的剩余标签,而不是简单邻居 (openNextRecentlyActiveEditor)。
            group.SelectedTab = group.SelectNextRecentlyActive();
        }

        // 关闭最后一个标签时自动移除空组(始终至少保留一个编辑器组)。
        Groups.RemoveEmptyGroup(group);
    }

    /// <summary>标签关闭的完整语义:持久化阅读状态、置 <see cref="EditorTabItem.IsClosed"/>
    /// 并释放载荷。工作台投影只清理 IsClosed 且已离组的标签,因此一切"真正关闭"路径
    /// (关闭、预览槽替换、LRU 驱逐、关闭全部)都必须经过本方法——裸 Remove 会在工作台
    /// 标签条留下不属于任何组、无法关闭的幽灵标签。</summary>
    private void MarkTabClosed(EditorTabItem tab)
    {
        if (tab is FilePreviewTab preview)
        {
            preview.PropertyChanged -= OnPreviewOptionChanged;
        }

        if (tab is DiffTab diffTab)
        {
            diffTab.HunkMutationCompleted -= OnDiffHunkMutationCompleted;
        }

        _ = PersistReadingStateSafelyAsync(tab, _workspaceService?.Current);
        tab.IsClosed = true;
        tab.ReleaseResources();
    }

    private void MarkTabClosed(EditorTabItem tab, ProjectWorkspaceContext? persistenceContext, bool persist)
    {
        if (tab is FilePreviewTab preview)
        {
            preview.PropertyChanged -= OnPreviewOptionChanged;
        }

        if (tab is DiffTab diffTab)
        {
            diffTab.HunkMutationCompleted -= OnDiffHunkMutationCompleted;
        }

        if (persist)
        {
            _ = PersistReadingStateSafelyAsync(tab, persistenceContext);
        }

        tab.IsClosed = true;
        tab.ReleaseResources();
    }

    private void OnDiffHunkMutationCompleted(object? sender, EventArgs e)
    {
        if (sender is DiffTab { Request: { } request })
        {
            DiffMutationCompleted?.Invoke(this, request);
        }
    }

    /// <summary>Blame 装载器:解析当前工作区的 Git 根;文件在仓库外或路径异常返回 null
    /// (标签据此提示"当前文件不在 Git 仓库中")。</summary>
    private async Task<GitBlame?> LoadBlameForAsync(string path, CancellationToken cancellationToken)
    {
        var repository = _workspaceService?.Current?.GitRepositoryPath;
        if (string.IsNullOrWhiteSpace(repository))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(Path.GetFullPath(repository), full);
            if (relative.Length == 0
                || relative.StartsWith("..", StringComparison.Ordinal)
                || Path.IsPathRooted(relative))
            {
                return null;
            }

            return await _gitService.GetBlameAsync(repository, relative.Replace('\\', '/'), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    // ===== 重开已关闭编辑器(Ctrl+Shift+T) =====
    // 只记描述符(路径/请求),不持有标签对象——关闭的标签已释放载荷,复活对象不如重建。
    // 预览槽替换与 LRU 驱逐不是用户关闭,不入史(VS Code 同语义)。

    private readonly List<ClosedTabRecord> _closedTabs = [];
    private const int MaxClosedTabHistory = 20;

    private readonly record struct ClosedTabRecord(string? Path, GitDiffRequest? DiffRequest);

    private void RecordClosedTab(EditorTabItem tab)
    {
        switch (tab)
        {
            case FilePreviewTab file:
                _closedTabs.Add(new ClosedTabRecord(file.Path, null));
                break;
            case DiffTab { Request: { } request }:
                _closedTabs.Add(new ClosedTabRecord(null, request));
                break;
            default:
                return;
        }

        while (_closedTabs.Count > MaxClosedTabHistory)
        {
            _closedTabs.RemoveAt(0);
        }
    }

    /// <summary>重开最近关闭的编辑器:取栈顶描述符重建(文件固定打开,不再进预览槽);
    /// 文件已消失的记录跳过并继续尝试下一条。</summary>
    [RelayCommand]
    private async Task ReopenClosedTabAsync()
    {
        while (_closedTabs.Count > 0)
        {
            var entry = _closedTabs[^1];
            _closedTabs.RemoveAt(_closedTabs.Count - 1);
            if (entry.DiffRequest is { } request)
            {
                await OpenDiffAsync(request);
                return;
            }

            if (entry.Path is { Length: > 0 } path && File.Exists(path))
            {
                await OpenFileAsync(path, permanent: true);
                return;
            }
        }
    }

    /// <summary>关闭组内全部标签(释放资源;空组自动移除)。先清空选中再一次性清空集合,
    /// 避免逐个移除时投影把尚未移除的标签重新加回已清空的标签条。</summary>
    public void CloseAllTabsInGroup(EditorGroupViewModel group)
        => CloseAllTabsInGroupCore(group, _workspaceService?.Current, persist: true);

    private void CloseAllTabsInGroupCore(
        EditorGroupViewModel group,
        ProjectWorkspaceContext? persistenceContext,
        bool persist)
    {
        foreach (var tab in group.Tabs.ToArray())
        {
            RecordClosedTab(tab);
            MarkTabClosed(tab, persistenceContext, persist);
        }

        group.SelectedTab = null;
        group.Tabs.Clear();
        Groups.RemoveEmptyGroup(group);
    }

    [RelayCommand]
    public void CloseAllTabs()
        => CloseAllTabsCore(_workspaceService?.Current, persist: true);

    private void CloseAllTabsCore(ProjectWorkspaceContext? persistenceContext, bool persist)
    {
        foreach (var group in Groups.Groups.ToArray())
        {
            CloseAllTabsInGroupCore(group, persistenceContext, persist);
        }

        // 全部关闭后收敛为单组(布局保持可用且简洁)。
        if (Groups.GroupCount > 1)
        {
            Groups.ResetLayout();
        }
    }

    private void CloseAllTabsInContext(ProjectWorkspaceContext? persistenceContext, bool persist)
    {
        foreach (var group in Groups.Groups.ToArray())
        {
            CloseAllTabsInGroupCore(group, persistenceContext, persist);
        }

        if (Groups.GroupCount > 1)
        {
            Groups.ResetLayout();
        }
    }

    /// <summary>Ctrl+W: closes the active tab (no-op when no tab is selected).</summary>
    [RelayCommand(CanExecute = nameof(CanCloseSelectedTab))]
    private void CloseSelectedTab() => CloseTab(SelectedTab);

    private bool CanCloseSelectedTab() => SelectedTab is not null;

    /// <summary>Ctrl+PgUp/PgDn: cycles the active group's tabs (wraps around the tab strip,
    /// VS Code-style; group switching happens through the workbench projection).</summary>
    [RelayCommand]
    private void GoToAdjacentTab(int offset)
    {
        var group = Groups.ActiveGroup;
        if (group is null || group.Tabs.Count == 0)
        {
            return;
        }

        var index = group.Tabs.IndexOf(group.SelectedTab ?? group.Tabs[^1]);
        var next = (index + offset + group.Tabs.Count) % group.Tabs.Count;
        group.SelectedTab = group.Tabs[next];
    }

    /// <summary>复制标签路径: copies the active tab's full path (VS Code tab context menu).</summary>
    [RelayCommand(CanExecute = nameof(CanCopySelectedTabPath))]
    private void CopySelectedTabPath() => _clipboard.SetText(SelectedTab!.Path);

    private bool CanCopySelectedTabPath() => SelectedTab is not null;

    /// <summary>在外部编辑器打开: passes the previewed file to the configured editor command (Code /
    /// other). No-op when the launcher or settings are unavailable (e.g. in tests).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenInExternalEditor))]
    private async Task OpenInExternalEditorAsync(FilePreviewTab? preview)
    {
        if (preview is null || _projectLauncher is null || _settingsService is null)
        {
            return;
        }

        var command = (await _settingsService.GetSnapshotAsync(CurrentSettingsContext()))
            .Effective(BuiltInSettingsCatalog.ExternalEditor);
        try
        {
            await _projectLauncher.OpenAsync(preview.Path, command);
            _logService.Write("INFO", $"已在外部编辑器打开：{preview.Path}");
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or System.Security.SecurityException)
        {
            _logService.Write("ERROR", $"无法在外部编辑器打开 {preview.Path}：{ex.Message}");
        }
    }

    private bool CanOpenInExternalEditor(FilePreviewTab? preview) =>
        preview is not null && _projectLauncher is not null && _settingsService is not null;

    /// <summary>Switches the inline/side-by-side layout of the active diff tab. Content tabs ignore it.</summary>
    [RelayCommand]
    private void ToggleActiveDiffMode()
    {
        if (SelectedTab is DiffTab diff)
        {
            diff.IsLayoutManuallySelected = true;
            diff.DiffMode = diff.DiffMode == GitDiffMode.Inline ? GitDiffMode.SideBySide : GitDiffMode.Inline;
        }
    }

    private SettingsContext CurrentSettingsContext(string? languageId = null) =>
        new(_workspaceService?.Current?.ProjectPath, languageId);

    private async Task<ISettingsSession> GetSettingsSessionAsync(
        string? languageId,
        ProjectWorkspaceContext? workspaceContext = null)
    {
        var key = languageId ?? "<all>";
        if (_languageSessions.TryGetValue(key, out var existing)) return existing;
        if (languageId is not null && !_languageSessions.ContainsKey("<all>"))
        {
            // 语言会话与 <all> 会话并存:设置服务按会话上下文过滤变更,语言会话收不到
            // "所有语言"提交;先建立 <all> 会话作为"所有语言"变更的入口(见 OnAllLanguagesSettingsChangedAsync)。
            _ = await GetSettingsSessionAsync(null, workspaceContext);
        }

        var keys = new[]
        {
            BuiltInSettingsCatalog.EnablePreview.Id, BuiltInSettingsCatalog.EditorLimitEnabled.Id,
            BuiltInSettingsCatalog.EditorLimitValue.Id,
            BuiltInSettingsCatalog.EditorFontSize.Id, BuiltInSettingsCatalog.EditorFontFamily.Id,
            BuiltInSettingsCatalog.EditorWordWrap.Id,
            BuiltInSettingsCatalog.MinimapEnabled.Id,
            BuiltInSettingsCatalog.LineNumbers.Id, BuiltInSettingsCatalog.IndentationGuides.Id,
            BuiltInSettingsCatalog.Folding.Id, BuiltInSettingsCatalog.StickyScroll.Id,
            BuiltInSettingsCatalog.MinimapRenderCharacters.Id, BuiltInSettingsCatalog.MinimapWidth.Id,
            BuiltInSettingsCatalog.EditorRulers.Id, BuiltInSettingsCatalog.DiffSideBySide.Id,
            BuiltInSettingsCatalog.DiffHideUnchanged.Id, BuiltInSettingsCatalog.DiffIntraline.Id,
            BuiltInSettingsCatalog.DiffOverview.Id, BuiltInSettingsCatalog.DiffSyncScroll.Id,
            BuiltInSettingsCatalog.DiffIgnoreTrimWhitespace.Id, BuiltInSettingsCatalog.DiffNarrowInline.Id,
            // 界面缩放变化 → 重建阅读选项,使源码/Diff 读者字号实时跟随缩放倍率。
            BuiltInSettingsCatalog.UiScale.Id,
        };
        var settingsContext = workspaceContext is null
            ? CurrentSettingsContext(languageId)
            : new SettingsContext(workspaceContext.ProjectPath, languageId);
        var session = await _settingsService!.OpenSessionAsync(settingsContext, keys);
        if (languageId is null)
        {
            session.Changed += (_, _) => _ = OnAllLanguagesSettingsChangedSafelyAsync(session);
        }
        else
        {
            session.Changed += (_, _) => _ = ApplyLanguageSettingsSafelyAsync(session);
        }

        _languageSessions[key] = session;
        return session;
    }

    /// <summary>"所有语言"设置提交 → 立即应用到全部打开标签;语言会话因按上下文过滤而收不到该提交,
    /// 必须显式刷新快照,否则语言会话会把变更前的旧快照当作 Current(旧值覆盖新值)。</summary>
    private async Task OnAllLanguagesSettingsChangedAsync(ISettingsSession allSession)
    {
        await _settingsNotificationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (allSession.Current is not { } snapshot) return;
            await ApplySettingsSnapshotAsync(snapshot);
            _cachedReadingOptions = null;
            _cachedDiffOptions = null;
            foreach (var session in _languageSessions.Values)
            {
                if (session.Context.LanguageId is null) continue;
                try
                {
                    await ApplySettingsSnapshotAsync(await session.RefreshAsync());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // 某一语言的设置文件暂不可读时保留其余语言的应用结果。
                }
            }
        }
        finally
        {
            _settingsNotificationGate.Release();
        }
    }

    private async Task ApplySettingsSnapshotAsync(SettingsSnapshot snapshot)
    {
        await _settingsApplyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_workspaceService is not null)
            {
                var snapshotWorkspace = snapshot.Context.NormalizedWorkspacePath;
                var currentWorkspace = _workspaceService.Current?.ProjectPath;
                if (snapshotWorkspace is null
                    ? currentWorkspace is not null
                    : !PathsEqual(snapshotWorkspace, currentWorkspace))
                {
                    return;
                }
            }

            if (!_settingsRevisionGate.TryAccept(snapshot)) return;
            void Apply()
            {
                if (_workspaceService is not null)
                {
                    var snapshotWorkspace = snapshot.Context.NormalizedWorkspacePath;
                    var currentWorkspace = _workspaceService.Current?.ProjectPath;
                    if (snapshotWorkspace is null
                        ? currentWorkspace is not null
                        : !PathsEqual(snapshotWorkspace, currentWorkspace))
                    {
                        return;
                    }
                }

                _applyingSettings = true;
                try
                {
                _editorLimitEnabled = snapshot.Effective(BuiltInSettingsCatalog.EditorLimitEnabled);
                _maxOpenTabs = snapshot.Effective(BuiltInSettingsCatalog.EditorLimitValue);
                _enablePreviewTabs = snapshot.Effective(BuiltInSettingsCatalog.EnablePreview);
                _contentLimits = SettingsOptionsMapper.ReadingLimits(snapshot);
                var code = SettingsOptionsMapper.CodeReading(snapshot);
                foreach (var tab in Groups.AllTabs.OfType<FilePreviewTab>().Where(tab =>
                                 snapshot.Context.LanguageId is null ||
                                 string.Equals(tab.FileType.LanguageId, snapshot.Context.LanguageId, StringComparison.OrdinalIgnoreCase)))
                {
                    // An earlier write can publish its settings snapshot while newer local option
                    // changes are still queued. Keep the live tab as the local source of truth
                    // until its final captured snapshot has been persisted.
                    if (HasPendingPreviewOptions(tab)) continue;
                    tab.ContentLimits = _contentLimits;
                    tab.WordWrap = code.WordWrap;
                        tab.ShowMinimap = code.ShowMinimap;
                        tab.FontSize = code.FontSize;
                        tab.FontFamily = code.FontFamily;
                        tab.ShowLineNumbers = code.ShowLineNumbers;
                        tab.ShowIndentGuides = code.ShowIndentGuides;
                        tab.ShowFoldingControls = code.ShowFoldingControls;
                        tab.ShowStickyScroll = code.StickyScroll;
                        tab.MinimapRenderCharacters = code.MinimapRenderCharacters;
                        tab.MinimapWidth = code.MinimapWidth;
                        tab.LineNumbersRelative = code.LineNumbersRelative;
                        tab.RulerColumns = code.RulerColumns;
                    }
                    if (snapshot.Context.LanguageId is null)
                    {
                        var diff = SettingsOptionsMapper.Diff(snapshot);
                        foreach (var tab in Groups.AllTabs.OfType<DiffTab>())
                        {
                            tab.ContentLimits = _contentLimits;
                            tab.EditorFontSize = code.FontSize;
                            tab.FontFamily = code.FontFamily;
                            tab.DiffMode = diff.DefaultLayout == DiffLayoutMode.SideBySide ? GitDiffMode.SideBySide : GitDiffMode.Inline;
                            tab.IsContextCollapsed = diff.CollapseUnchangedContext;
                            tab.ShowIntralineChanges = diff.ShowIntralineChanges;
                            tab.ShowOverviewRuler = diff.ShowOverviewRuler;
                            tab.SynchronizeScrolling = diff.SynchronizeScrolling;
                            tab.UseInlineWhenNarrow = diff.UseInlineWhenNarrow;
                            tab.IgnoreTrimWhitespace = diff.IgnoreWhitespaceEndOfLine;
                        }
                    }
                    // 降低标签上限:只回收预览标签,常驻/固定标签不因设置变化被强制关闭。
                    if (_editorLimitEnabled)
                    {
                        foreach (var group in Groups.Groups)
                        {
                            while (group.Tabs.Count > _maxOpenTabs)
                            {
                                if (group.EvictLeastRecentlyUsedPreview() is not { } preview)
                                {
                                    break;
                                }

                                MarkTabClosed(preview);
                                group.Tabs.Remove(preview);
                            }
                        }
                    }
                }
                finally { _applyingSettings = false; }
            }

            if (_uiContext is null || ReferenceEquals(SynchronizationContext.Current, _uiContext))
            {
                Apply();
            }
            else
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _uiContext.Post(_ =>
                {
                    try
                    {
                        Apply();
                        completion.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                }, null);
                await completion.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _settingsApplyGate.Release();
        }
    }

    private async Task OnWorkspaceSettingsContextChangedAsync(ProjectWorkspaceContext? context)
    {
        _workspaceContextBoundaryEnabled = true;
        var previous = _activeWorkspaceContext;
        _activeWorkspaceContext = context;
        var switchCancellation = ReplaceWorkspaceSwitchCancellation();

        // Current is already the new context when this event is raised. Flush the old editor state
        // with its explicit key before replacing the shared tab tree; never infer the key from the
        // mutable workspace service during this transition.
        if (previous is not null && !ReferenceEquals(previous, context))
        {
            await FlushWorkspaceStateAsync(previous, CancellationToken.None);
        }

        if (!IsWorkspaceContextCurrent(context))
        {
            return;
        }

        var persistenceSuppressed = _suppressWorkspacePersistence;
        _suppressWorkspacePersistence = true;
        try
        {
            // A workspace with no saved layout must still start with an empty editor. Otherwise the
            // previous project's tabs remain visible in the newly selected project.
            CloseAllTabsInContext(previous, persist: previous is not null);
        }
        finally
        {
            _suppressWorkspacePersistence = persistenceSuppressed;
        }

        foreach (var session in _languageSessions.Values) await session.DisposeAsync();
        _languageSessions.Clear();
        _cachedReadingOptions = null;
        _cachedDiffOptions = null;

        if (context is null)
        {
            return;
        }

        foreach (var language in Groups.AllTabs.OfType<FilePreviewTab>().Select(tab => tab.FileType.LanguageId).Distinct())
        {
            var session = await GetSettingsSessionAsync(language);
            if (!IsWorkspaceContextCurrent(context) || switchCancellation.IsCancellationRequested) return;
            await ApplySettingsSnapshotAsync(session.Current!);
        }

        var allSession = await GetSettingsSessionAsync(null);
        if (!IsWorkspaceContextCurrent(context) || switchCancellation.IsCancellationRequested) return;
        await ApplySettingsSnapshotAsync(allSession.Current!);
        // 启动自动恢复工作区时不回放编辑器文件标签(源码文件不随启动自动打开);
        // 用户主动激活工作区时仍按保存的布局恢复。
        if (_workspaceService?.IsStartupAutoRestore is not true)
        {
            try
            {
                await RestoreRecentTabsAsync(context, switchCancellation);
            }
            catch (OperationCanceledException) when (switchCancellation.IsCancellationRequested)
            {
                // A newer context owns the editor now; cancellation is an expected switch result.
            }
        }
    }

    private async Task OnAllLanguagesSettingsChangedSafelyAsync(ISettingsSession session)
    {
        try
        {
            await OnAllLanguagesSettingsChangedAsync(session);
        }
        catch (Exception ex)
        {
            _logService.Write("WARNING", $"应用编辑器设置失败：{ex.Message}");
        }
    }

    private async Task ApplyLanguageSettingsSafelyAsync(ISettingsSession session)
    {
        await _settingsNotificationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (session.Current is { } snapshot)
            {
                await ApplySettingsSnapshotAsync(snapshot);
            }
        }
        catch (Exception ex)
        {
            _logService.Write("WARNING", $"应用语言编辑器设置失败：{ex.Message}");
        }
        finally
        {
            _settingsNotificationGate.Release();
        }
    }

    private CancellationToken ReplaceWorkspaceSwitchCancellation()
    {
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _workspaceSwitchCancellation, next);
        previous?.Cancel();
        previous?.Dispose();
        return next.Token;
    }

    private bool IsWorkspaceContextCurrent(ProjectWorkspaceContext? context)
    {
        // The lightweight constructors are also used by editor-only tests and by embedders that
        // do not participate in the workbench context service. In that mode there is no context
        // boundary to validate.
        if (_workspaceService is null || !_workspaceContextBoundaryEnabled) return true;
        return ReferenceEquals(_workspaceService.Current, context)
            && ReferenceEquals(_activeWorkspaceContext, context);
    }

    private bool IsWorkspaceRepositoryCurrent(ProjectWorkspaceContext? context, string repositoryPath)
    {
        if (_workspaceService is null || !_workspaceContextBoundaryEnabled) return true;
        return IsWorkspaceContextCurrent(context)
            && context?.GitRepositoryPath is { } currentRepository
            && PathsEqual(currentRepository, repositoryPath);
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

    private async Task FlushWorkspaceStateAsync(ProjectWorkspaceContext context, CancellationToken cancellationToken)
    {
        if (_stateStore is null) return;

        try
        {
            foreach (var tab in Groups.AllTabs.ToArray())
            {
                tab.IsActive = false;
                await PersistReadingStateAsync(tab, context, cancellationToken);
            }

            await _stateStore.CommitAsync(new([
                new(ApplicationStateField.WorkspaceEditorLayout, Groups.CaptureLayout(), context.ProjectPath),
                new(ApplicationStateField.WorkspaceRecentTabs, Groups.AllTabs.Select(tab => tab.TabKey).ToArray(), context.ProjectPath),
                new(ApplicationStateField.WorkspaceActiveEditor, SelectedTab?.TabKey, context.ProjectPath),
            ]), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logService.Write("WARNING", $"无法保存工作区编辑器状态：{ex.Message}");
        }
    }

    /// <summary>工作区切换/启动时恢复编辑器布局:优先恢复 v2 布局树;旧状态文件(
    /// 只有 <c>RecentTabs</c> / <c>ActiveEditor</c>)无损迁移为单编辑器组。</summary>
    private async Task RestoreRecentTabsAsync(ProjectWorkspaceContext context, CancellationToken cancellationToken)
    {
        if (_stateStore is null || string.IsNullOrWhiteSpace(context.ProjectPath)) return;
        var workspace = Path.GetFullPath(context.ProjectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var state = await _stateStore.LoadAsync(cancellationToken);
        if (!IsWorkspaceContextCurrent(context) || cancellationToken.IsCancellationRequested) return;
        var saved = state.Workspaces?.FirstOrDefault(item => string.Equals(item.Key, workspace,
            StringComparison.OrdinalIgnoreCase)).Value;
        if (saved is null) return;

        // 惰性恢复:整个恢复过程(含活动标签的选中)都抑制自动加载 —— 文件内容、
        // TextMate 语法与 Roslyn 轮廓推迟到用户首次交互(点标签/点编辑区)才解码,
        // 不再占用启动关键路径。会话中正常打开的文件不受影响(选中即加载)。
        _suppressSelectionLoad = true;
        try
        {
            // 恢复会整体替换组树:先把现存未关闭标签按"真正关闭"语义清掉(持久化阅读状态、
            // 置 IsClosed、释放载荷)。否则旧标签脱离任何组但 IsClosed 未置位,工作台条带会
            // 留下永远无法关闭的幽灵标签(投影只清理已关闭且离组的标签;CloseTab 对无主标签
            // 过去会静默失败)。
            if (Groups.AllTabs.Any()) CloseAllTabsInContext(context, persist: false);

            if (saved.EditorLayout is { } layout)
            {
                // v2:恢复组树 + 标签描述(不存在的文件 / 无法重建的 diff 标签跳过并写日志)。
                Groups.ReplaceWithRestoredLayout(layout, CreateRestoredTab);
            }
            else
            {
                // v1 迁移:扁平 RecentTabs → 单编辑器组。
                var tabs = new List<EditorTabItem>();
                foreach (var key in saved.SafeRecentTabs)
                {
                    if (CreateLegacyTab(key) is { } tab)
                    {
                        tabs.Add(tab);
                    }
                }

                Groups.ReplaceWithSingleGroup(tabs, saved.ActiveEditor);
            }

            if (!IsWorkspaceContextCurrent(context) || cancellationToken.IsCancellationRequested) return;
            await ApplyRestoredReadingOptionsAsync(context, cancellationToken);
            if (!IsWorkspaceContextCurrent(context) || cancellationToken.IsCancellationRequested) return;
            // 恢复出的文件标签同样进入监听集合(防御性差量同步;常规路径已由 TabsChanged 覆盖)。
            SyncFileWatcher();
        }
        finally
        {
            _suppressSelectionLoad = false;
        }
    }

    private EditorTabItem? CreateRestoredTab(EditorTabState tabState)
    {
        if (!string.IsNullOrWhiteSpace(tabState.FilePath) || tabState.TabKey.StartsWith("file:", StringComparison.Ordinal))
        {
            var path = tabState.FilePath ?? tabState.TabKey["file:".Length..];
            if (!File.Exists(path))
            {
                _logService.Write("WARNING", $"跳过不存在的文件标签：{path}");
                return null;
            }

            var languageId = _fileTypes.FromPath(path).LanguageId;
            return new FilePreviewTab(path, _decoder, _fileTypes, _searchService, _outlineParser, _uiDispatcher)
            {
                ContentLimits = _contentLimits,
                BlameLoader = LoadBlameForAsync,
                IsPreview = tabState.IsPreview,
                ShowLineNumbers = true,
                ShowIndentGuides = true,
                ShowFoldingControls = true,
            };
        }

        if (!string.IsNullOrWhiteSpace(tabState.DiffPath) || tabState.TabKey.StartsWith("diff:", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(tabState.RepositoryPath) || string.IsNullOrWhiteSpace(tabState.DiffPath))
            {
                _logService.Write("WARNING", $"跳过无法恢复的 diff 标签：{tabState.TabKey}");
                return null;
            }

            var tab = new DiffTab(_gitService,
                new GitDiffRequest(tabState.RepositoryPath, tabState.DiffPath, tabState.IsStaged, tabState.IsUntracked,
                    tabState.CommitHash),
                _logService, _clipboard, _confirmationService)
            {
                ContentLimits = _contentLimits,
            };
            tab.HunkMutationCompleted += OnDiffHunkMutationCompleted;
            return tab;
        }

        _logService.Write("WARNING", $"跳过未知类型的标签：{tabState.TabKey}");
        return null;
    }

    /// <summary>v1 扁平 TabKey → 标签。文件标签校验存在性;diff 标签无法区分暂存/未跟踪,
    /// 按"未暂存修改"恢复(提交 diff 保留提交哈希)。</summary>
    private EditorTabItem? CreateLegacyTab(string key)
    {
        if (key.StartsWith("file:", StringComparison.Ordinal))
        {
            var path = key[5..];
            if (!File.Exists(path))
            {
                _logService.Write("WARNING", $"跳过不存在的文件标签：{path}");
                return null;
            }

            return new FilePreviewTab(path, _decoder, _fileTypes, _searchService, _outlineParser, _uiDispatcher)
            {
                ContentLimits = _contentLimits,
                BlameLoader = LoadBlameForAsync,
            };
        }

        if (key.StartsWith("diff:", StringComparison.Ordinal))
        {
            // v1 只持久化了 TabKey("diff:<path>" / "diff:<hash>:<path>"),无法恢复仓库路径;
            // 与 v1 自身恢复行为一致(旧版恢复时跳过 diff 标签),跳过并写日志。
            _logService.Write("WARNING", $"跳过无法恢复的旧版 diff 标签：{key}");
            return null;
        }

        _logService.Write("WARNING", $"跳过未知类型的标签：{key}");
        return null;
    }

    /// <summary>恢复完成后应用阅读偏好(字号/折行等)与阅读位置。恢复出的标签一律不预加载:
    /// 内容解码推迟到用户首次交互(点标签/点编辑区/换选标签),启动关键路径只保留轻量布局恢复。</summary>
    private async Task ApplyRestoredReadingOptionsAsync(
        ProjectWorkspaceContext context,
        CancellationToken cancellationToken)
    {
        foreach (var preview in Groups.AllTabs.OfType<FilePreviewTab>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsWorkspaceContextCurrent(context)) return;
            var options = await EnsureReadingOptionsAsync(preview.FileType.LanguageId);
            if (!IsWorkspaceContextCurrent(context)) return;
            preview.WordWrap = options.WordWrap;
            preview.ShowMinimap = options.ShowMinimap;
            preview.FontSize = options.FontSize > 0 ? options.FontSize : 14;
            preview.FontFamily = options.FontFamily;
            preview.ShowLineNumbers = options.ShowLineNumbers;
            preview.ShowIndentGuides = options.ShowIndentGuides;
            preview.ShowFoldingControls = options.ShowFoldingControls;
            preview.ShowStickyScroll = options.StickyScroll;
            preview.MinimapRenderCharacters = options.MinimapRenderCharacters;
            preview.MinimapWidth = options.MinimapWidth;
            preview.LineNumbersRelative = options.LineNumbersRelative;
            preview.RulerColumns = options.RulerColumns;
            preview.PropertyChanged += OnPreviewOptionChanged;
            await RestoreReadingStateAsync(preview, context, cancellationToken);
        }

        foreach (var diff in Groups.AllTabs.OfType<DiffTab>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsWorkspaceContextCurrent(context)) return;
            var options = await EnsureReadingOptionsAsync();
            if (!IsWorkspaceContextCurrent(context)) return;
            var diffOptions = _cachedDiffOptions ?? new DiffReadingOptions();
            diff.EditorFontSize = options.FontSize > 0 ? options.FontSize : 14;
            diff.FontFamily = options.FontFamily;
            diff.DiffMode = diffOptions.DefaultLayout == DiffLayoutMode.SideBySide ? GitDiffMode.SideBySide : GitDiffMode.Inline;
            diff.IsContextCollapsed = diffOptions.CollapseUnchangedContext;
            diff.ShowIntralineChanges = diffOptions.ShowIntralineChanges;
            diff.ShowOverviewRuler = diffOptions.ShowOverviewRuler;
            diff.SynchronizeScrolling = diffOptions.SynchronizeScrolling;
            diff.UseInlineWhenNarrow = diffOptions.UseInlineWhenNarrow;
            diff.IgnoreTrimWhitespace = diffOptions.IgnoreWhitespaceEndOfLine;
        }

        // 惰性恢复:活动标签同样不预加载 —— 首次交互(标签条/编辑区点击或换选标签)才解码。
    }

    /// <summary>结构变化(拆分/移动/关闭/比例)后即时持久化布局,重启同工作区可恢复。</summary>
    private void PersistLayoutAsync()
    {
        if (_suppressWorkspacePersistence || _stateStore is null || _workspaceService?.Current is not { } context)
        {
            return;
        }

        // Capture the layout synchronously with the context. The commit itself is asynchronous and
        // may execute after a project switch; reading Groups inside that delayed task would then
        // overwrite the old project's saved layout with the new project's tabs.
        var layout = Groups.CaptureLayout();
        var recentTabs = Groups.AllTabs.Select(tab => tab.TabKey).ToArray();
        var activeEditor = SelectedTab?.TabKey;
        _ = PersistLayoutCoreAsync(context.ProjectPath, layout, recentTabs, activeEditor);
    }

    private async Task PersistLayoutCoreAsync(
        string workspace,
        EditorLayoutState layout,
        IReadOnlyList<string> recentTabs,
        string? activeEditor)
    {
        try
        {
            var store = _stateStore;
            if (store is null)
            {
                return;
            }

            await store.CommitAsync(new([
                new(ApplicationStateField.WorkspaceEditorLayout, layout, workspace),
                new(ApplicationStateField.WorkspaceRecentTabs, recentTabs, workspace),
                new(ApplicationStateField.WorkspaceActiveEditor, activeEditor, workspace),
            ]));
        }
        catch (Exception ex)
        {
            // 布局持久化是尽力而为,失败不影响编辑器使用。
            _logService.Write("WARNING", $"无法保存编辑器布局：{ex.Message}");
        }
    }

    private async Task RestoreReadingStateAsync(
        FilePreviewTab tab,
        ProjectWorkspaceContext? workspaceContext,
        CancellationToken cancellationToken = default)
    {
        if (_stateStore is null || workspaceContext?.ProjectPath is not { } workspace) return;
        var state = await _stateStore.LoadAsync(cancellationToken);
        if (!IsWorkspaceContextCurrent(workspaceContext) || cancellationToken.IsCancellationRequested) return;
        var workspaceKey = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var saved = state.Workspaces?.FirstOrDefault(item => string.Equals(item.Key, workspaceKey,
            StringComparison.OrdinalIgnoreCase)).Value;
        if (saved is null || !saved.SafeReadingStates.TryGetValue(tab.Path, out var reading)) return;
        tab.ViewState = new(reading.VerticalOffset, reading.CaretLine, reading.CaretColumn,
            reading.ExpandedRegions?.ToHashSet(), reading.FoldedSymbolIds?.ToHashSet(),
            reading.HorizontalOffset);
        tab.SearchText = reading.SearchText ?? string.Empty;
        if (Enum.TryParse<MarkdownViewMode>(reading.MarkdownMode, true, out var markdownMode))
        {
            tab.MarkdownMode = markdownMode;
        }
        tab.MarkdownVerticalOffset = reading.MarkdownVerticalOffset;
        tab.MarkdownAnchor = reading.MarkdownAnchor ?? string.Empty;
        tab.MarkdownHeadingLine = reading.MarkdownHeadingLine;
    }

    private Task PersistReadingStateAsync(
        EditorTabItem tab,
        ProjectWorkspaceContext? workspaceContext,
        CancellationToken cancellationToken = default)
    {
        if (_stateStore is null || workspaceContext?.ProjectPath is not { } workspace ||
            tab is not FilePreviewTab preview || preview.ViewState is not { } viewState) return Task.CompletedTask;
        var reading = new EditorReadingState(viewState.VerticalOffset, viewState.CaretLine, viewState.CaretColumn,
            HorizontalOffset: viewState.HorizontalOffset,
            ExpandedRegions: viewState.FoldedOffsets?.ToArray(),
            FoldedSymbolIds: viewState.FoldedSymbolIds?.ToArray(), SearchText: preview.SearchText,
            MarkdownMode: preview.MarkdownMode.ToString(), MarkdownVerticalOffset: preview.MarkdownVerticalOffset,
            MarkdownAnchor: string.IsNullOrEmpty(preview.MarkdownAnchor) ? null : preview.MarkdownAnchor,
            MarkdownHeadingLine: preview.MarkdownHeadingLine);
        return _stateStore.CommitAsync(new([
            new(ApplicationStateField.WorkspaceReadingState, reading, workspace, preview.Path),
        ]), cancellationToken);
    }

    /// <summary>关闭/替换标签时的后台阅读状态写入边界。状态保存不应把异常留在未观察的
    /// Task 上，尤其是应用退出时状态存储可能已经开始释放。</summary>
    private async Task PersistReadingStateSafelyAsync(
        EditorTabItem tab,
        ProjectWorkspaceContext? workspaceContext)
    {
        try
        {
            await PersistReadingStateAsync(tab, workspaceContext);
        }
        catch (Exception ex)
        {
            _logService.Write("WARNING", $"无法保存文件阅读状态：{ex.Message}");
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        // Option changes are intentionally saved in the background during interaction. Shutdown
        // and tests need a real completion boundary so the last captured snapshot cannot be left
        // behind or observed halfway through the serialized write queue.
        await _previewOptionsGate.WaitAsync(cancellationToken);
        _previewOptionsGate.Release();

        if (_workspaceService?.Current is not { } context) return;
        await FlushWorkspaceStateAsync(context, cancellationToken);
    }

    public void Dispose()
    {
        var workspaceSwitchCancellation = Interlocked.Exchange(ref _workspaceSwitchCancellation, null);
        workspaceSwitchCancellation?.Cancel();
        workspaceSwitchCancellation?.Dispose();
        if (_workspaceService is not null)
        {
            _workspaceService.ContextChanged -= OnWorkspaceSettingsContextChangedAsync;
        }

        // DI disposal can happen while the workbench still holds the tab view-models. Release
        // their payloads explicitly so closing the application does not leave decoded text,
        // Markdown results or diff buffers reachable through those stale UI references.
        foreach (var tab in Groups.AllTabs.ToArray())
        {
            tab.IsClosed = true;
            tab.ReleaseResources();
        }

        _fileContentWatcher.FileChanged -= OnFileChanged;
        _repositoryWatcher.ChangesDetected -= OnRepositoryChangesDetected;
        _fileContentWatcher.Dispose();
        _watchedFilePaths.Clear();
        // Background option writes are deliberately fire-and-forget. Do not dispose the gate
        // while one of those continuations may still be waiting; the gate is reclaimed with this
        // view model after the pending task observes the shutdown/store failure.
    }

}
