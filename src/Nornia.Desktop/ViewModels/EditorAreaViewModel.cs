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
/// offset, caret line/column and the document offsets of folded sections (restored on re-activate).</summary>
public sealed record EditorViewState(
    double VerticalOffset,
    int CaretLine,
    int CaretColumn,
    IReadOnlySet<int>? FoldedOffsets = null,
    IReadOnlySet<string>? FoldedSymbolIds = null);

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
    // 回收。128 MiB 是软上限：每个编辑器组当前选中的文件必须保留，以避免可见编辑器
    // 在后台被清空；超出预算的原因只可能是这些可见文件本身过大。
    private const long OpenFilePayloadBudgetBytes = 128L * 1024 * 1024;
    private int _maxOpenTabs = 30;
    private bool _enablePreviewTabs = true;
    private bool _editorLimitEnabled = true;
    private bool _applyingSettings;

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

    /// <summary>外部文件变更 → 重载所有打开该文件的预览标签(活动与非活动一致;Diff 标签不在此列,
    /// 由 Git 状态刷新机制负责)。</summary>
    private void OnFileChanged(object? sender, FileChangedEventArgs e)
    {
        foreach (var tab in Groups.AllTabs.OfType<FilePreviewTab>())
        {
            if (string.Equals(tab.Path, e.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                _ = ReloadPreviewAndTrimAsync(tab);
            }
        }
    }

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
        var tab = new FilePreviewTab(path, _decoder, _fileTypes, _searchService, _outlineParser)
        {
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
            _cachedDiffOptions = SettingsOptionsMapper.Diff(snapshot);
            return SettingsOptionsMapper.CodeReading(snapshot);
        }
        return _cachedReadingOptions ??= new CodeReadingOptions();
    }

    /// <summary>Persists reading-option toggles. Fire-and-forget:
    /// a failing write must never break the preview.</summary>
    private void OnPreviewOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not FilePreviewTab tab || _settingsService is null || _applyingSettings)
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
            MarkTabClosed(tab);
            return;
        }

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
                    var code = SettingsOptionsMapper.CodeReading(snapshot);
                    foreach (var tab in Groups.AllTabs.OfType<FilePreviewTab>().Where(tab =>
                                 snapshot.Context.LanguageId is null ||
                                 string.Equals(tab.FileType.LanguageId, snapshot.Context.LanguageId, StringComparison.OrdinalIgnoreCase)))
                    {
                        // An earlier write can publish its settings snapshot while newer local option
                        // changes are still queued. Keep the live tab as the local source of truth
                        // until its final captured snapshot has been persisted.
                        if (HasPendingPreviewOptions(tab)) continue;
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
            return new FilePreviewTab(path, _decoder, _fileTypes, _searchService, _outlineParser)
            {
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
                _logService, _clipboard, _confirmationService);
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

            return new FilePreviewTab(path, _decoder, _fileTypes, _searchService, _outlineParser);
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
            reading.ExpandedRegions?.ToHashSet());
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
            ExpandedRegions: viewState.FoldedOffsets?.ToArray(), SearchText: preview.SearchText,
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

/// <summary>编辑器折叠请求类型(视图把请求映射到 CodeDocumentView 的折叠操作)。</summary>
public enum EditorFoldRequest
{
    CollapseAll,
    ExpandAll,
    ToLevel,
}

/// <summary>One open tab in the shared editor group. Content is loaded lazily on first activation
/// and a file tab may evict its large payload while keeping its lightweight identity open.</summary>
public abstract partial class EditorTabItem : ObservableObject
{
    protected EditorTabItem(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(Name))
        {
            Name = path;
        }

        BreadcrumbText = TruncateBreadcrumb(path);
        BreadcrumbSegments = BuildBreadcrumbSegments(path);
    }

    public string Path { get; }
    public string Name { get; }

    /// <summary>Trailing path segments for the VS Code-style breadcrumb (ellipsized when deep).</summary>
    public string BreadcrumbText { get; }

    /// <summary>面包屑分段(驱动器/目录/文件;过深时以"…"折叠前段)。目录段点击可在资源管理器定位。</summary>
    public IReadOnlyList<BreadcrumbSegment> BreadcrumbSegments { get; }

    /// <summary>Identity used to deduplicate tabs (a file and a diff of that file are distinct).</summary>
    public abstract string TabKey { get; }

    public virtual string Glyph => Codicons.File;

    /// <summary>文件标签的语言元数据(语言缩写 Monogram / 色相);非文件标签为 null。</summary>
    public virtual CodeFileType? FileType => null;

    /// <summary>该标签是否为 diff 标签(驱动标签条上的暂存状态字母)。</summary>
    public virtual bool IsDiff => false;

    public virtual DiffTabStatus TabStatus => DiffTabStatus.Modified;
    public virtual string TabStatusMarker => string.Empty;
    public virtual string TabStatusToolTip => string.Empty;

    [ObservableProperty]
    private bool isActive;

    /// <summary>VS Code preview flag: file previews open as italic "preview" tabs and are replaced by
    /// the next file open until pinned (固定后即常驻)。</summary>
    [ObservableProperty]
    private bool isPreview;

    /// <summary>VS Code pinned flag: pinned tabs stay at the left end of the tab strip (固定标签;
    /// 会话级,不持久化,双击/右键菜单切换)。</summary>
    [ObservableProperty]
    private bool isPinned;

    /// <summary>运行时关闭标记:区分跨组移动期间的暂时离组与真正关闭,不参与布局持久化。</summary>
    [ObservableProperty]
    private bool isClosed;

    public abstract Task LoadAsync();

    /// <summary>Releases large payloads (text, diff rows, search matches) when the tab closes so the
    /// memory is reclaimed even if a stale reference lingers.</summary>
    public virtual void ReleaseResources()
    {
    }

    protected static string TruncateBreadcrumb(string path)
    {
        var segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 3)
        {
            return string.Join(" / ", segments);
        }

        return "… / " + string.Join(" / ", segments[^3..]);
    }

    /// <summary>把路径拆成面包屑分段:保留驱动器根,逐段累计完整路径(资源管理器定位用);
    /// 过深时以"…"折叠前段,只显示尾部 <paramref name="maxVisible"/> 段。</summary>
    private static IReadOnlyList<BreadcrumbSegment> BuildBreadcrumbSegments(string path, int maxVisible = 5)
    {
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return [new BreadcrumbSegment(path, null, false, false)];
        }

        var rows = new List<BreadcrumbSegment>();
        var full = string.Empty;
        for (var i = 0; i < parts.Length; i++)
        {
            full = i == 0 ? parts[i] : full + "\\" + parts[i];
            if (parts[i].EndsWith(':'))
            {
                full += "\\"; // 驱动器根 "D:\"
            }

            rows.Add(new BreadcrumbSegment(parts[i], full, i < parts.Length - 1, i != 0));
        }

        if (rows.Count <= maxVisible)
        {
            return rows;
        }

        var tail = rows[^maxVisible..];
        var result = new List<BreadcrumbSegment> { new("…", null, false, false) };
        result.Add(tail[0] with { ShowChevron = true });
        result.AddRange(tail[1..]);
        return result;
    }
}

/// <summary>Read-only content preview for files opened from the explorer. Capped so opening a large
/// repository never fills the managed heap; decoding rides the shared <see cref="TextDocumentDecoder"/>
/// (BOM / strict UTF-8 / GB18030 fallback), so legacy-encoded sources do not garble. The editor
/// may release the payload of an inactive tab and load it again when selected.</summary>
public sealed partial class FilePreviewTab : EditorTabItem
{
    private const long PreviewLimit = ReadOnlyContentCapacity.FullSourceBytes;
    private const int MaxSearchMatches = 10_000;

    private readonly ITextDocumentDecoder _decoder;
    private readonly ITextSearchService _search;
    private readonly ICodeOutlineParser _outlineParser;
    private readonly ICodeFoldingStrategy _folding;
    private readonly ICodeSymbolAnalyzer _symbolAnalyzer;
    private readonly HashSet<string> _collapsedSymbolIds = new(StringComparer.Ordinal);
    private CodeSymbolDocument _symbolDocument = CodeSymbolDocument.Empty;
    private readonly List<CodeSymbolNode> _wiredSymbolNodes = [];
    private Task? _loadTask;
    private bool _payloadUnloaded;
    private bool _resourcesReleased;
    private int _pipelineActive;
    private CancellationTokenSource? _presentationCancellation;
    private CancellationTokenSource? _markdownCancellation;
    private CancellationTokenSource? _derivedCancellation;
    private IReadOnlyDocumentSource? _documentSource;
    private int _presentationVersion;
    private int _derivedVersion;
    private int _searchVersion;
    private CancellationTokenSource? _searchCancellation;
    /// <summary>Authoritative windowed-search list (global line + column). <see cref="SearchMatches"/>
    /// holds the current window's subset remapped to window-local coordinates after each load.</summary>
    private List<DocumentSearchMatch> _windowedMatches = [];
    /// <summary>Number of text lines in the currently loaded window's <see cref="Content"/> (0 otherwise).</summary>
    private int _windowTextLines;
    /// <summary>Character offset where each line of the current window's <see cref="Content"/> starts
    /// (index = local line − 1).</summary>
    private IReadOnlyList<int> _windowLineOffsets = [0];

    /// <summary>E10: 查找栏击键防抖(VS Code 查找 widget 的 150–300ms 输入去抖)。一次击键风暴
    /// 合并为一次窗口化重扫——只有停顿后最后一个键触发扫描;Escape(<see cref="ClearSearch"/>)
    /// 立即 Cancel 挂起排程。每次 Schedule 重置计时(尾部去抖),线程安全。</summary>
    private readonly RunOnceScheduler _searchTextDebounce;

    /// <summary>查找文本防抖时长(毫秒);测试按此值等待收敛。</summary>
    public const int SearchTextDebounceMs = 250;

    // ===== External-change reload =====

    /// <summary>Raised on the UI thread immediately before published content is replaced by an
    /// external reload; the view captures scroll/caret/fold position (and the Markdown reading
    /// spot) so the reading position survives the content swap.</summary>
    public event EventHandler? ContentUpdating;

    /// <summary>Serializes load/reload passes: only the newest request runs the decode/rebuild
    /// pipeline, so rapid saves apply the latest content only and stale reads cannot interleave.</summary>
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    /// <summary>Serializes window publication with source replacement/disposal. Navigation requests
    /// also cancel the previous request and carry a monotonically increasing id, so an older read
    /// can neither overwrite a newer window nor calculate a local line against the wrong start.</summary>
    private readonly SemaphoreSlim _windowLoadGate = new(1, 1);
    private readonly object _windowRequestGate = new();
    private CancellationTokenSource? _windowCancellation;
    private int _windowRequestVersion;

    /// <summary>Monotonic content version shared by the initial load and every reload. Each pass
    /// captures it on entry and only the *current* version may publish, so an old decode (or the
    /// initial load racing a reload) can never overwrite newer content.</summary>
    private int _contentVersion;

    /// <summary>延迟重试调度:文件暂时不可读(写入中/锁定/已删除)时周期性重试,恢复后自动刷新。</summary>
    private static readonly TimeSpan ReloadRetryDelay = TimeSpan.FromMilliseconds(300);
    private CancellationTokenSource? _reloadRetryCancellation;

    private const long DocumentSourceBaseMemoryBytes = 32L * 1024;
    private const long DocumentSourceCheckpointBytes = 32L;
    private const int DocumentSourceCheckpointStride = 256;

    public FilePreviewTab(string path) : this(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance, TextSearchService.Instance, CodeOutlineParser.Instance, CodeFoldingStrategy.Instance, CodeSymbolAnalyzer.Instance)
    {
    }

    public FilePreviewTab(string path, ITextDocumentDecoder decoder, ICodeFileTypeRegistry registry)
        : this(path, decoder, registry, TextSearchService.Instance, CodeOutlineParser.Instance, CodeFoldingStrategy.Instance, CodeSymbolAnalyzer.Instance)
    {
    }

    public FilePreviewTab(string path, ITextDocumentDecoder decoder, ICodeFileTypeRegistry registry, ITextSearchService search)
        : this(path, decoder, registry, search, CodeOutlineParser.Instance, CodeFoldingStrategy.Instance, CodeSymbolAnalyzer.Instance)
    {
    }

    public FilePreviewTab(string path, ITextDocumentDecoder decoder, ICodeFileTypeRegistry registry, ITextSearchService search, ICodeOutlineParser outlineParser)
        : this(path, decoder, registry, search, outlineParser, CodeFoldingStrategy.Instance, CodeSymbolAnalyzer.Instance)
    {
    }

    public FilePreviewTab(string path, ITextDocumentDecoder decoder, ICodeFileTypeRegistry registry, ITextSearchService search, ICodeOutlineParser outlineParser, ICodeFoldingStrategy folding, ICodeSymbolAnalyzer? symbolAnalyzer = null) : base(path)
    {
        _decoder = decoder;
        _search = search;
        _outlineParser = outlineParser;
        _folding = folding;
        _symbolAnalyzer = symbolAnalyzer ?? CodeSymbolAnalyzer.Instance;
        _fileType = registry.FromPath(path);
        LanguageName = FileType.DisplayName;
        // VS Code preview semantics: 文件预览以斜体预览标签打开,被下一次打开替换,直到固定。
        IsPreview = true;
        ThemeEvents.ThemeChanged += OnAppThemeChanged; // ReleaseResources 中解除

        // RunOnceScheduler 的定时器在池线程到期;回到构造时所在的同步上下文(应用内即 UI
        // 线程)再触发 RefreshSearch,可观察状态只在原线程变更。无同步上下文的测试环境就地执行。
        var debounceContext = SynchronizationContext.Current;
        _searchTextDebounce = new RunOnceScheduler(SearchTextDebounceMs)
        {
            Action = () =>
            {
                if (debounceContext is not null)
                {
                    debounceContext.Post(_ => RefreshSearch(), null);
                }
                else
                {
                    RefreshSearch();
                }
            },
        };
    }

    /// <summary>Language descriptor (highlighting, icon, outline kind) resolved from the extension.</summary>
    public override CodeFileType FileType => _fileType;

    private CodeFileType _fileType;

    /// <summary>切换语言模式(VS Code"更改语言模式"):替换解析/高亮/大纲类型;加载完成后即时重建。</summary>
    public void SetLanguage(CodeFileType type)
    {
        if (type.LanguageId == _fileType.LanguageId)
        {
            return;
        }

        _fileType = type;
        OnPropertyChanged(nameof(FileType));
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(IsMarkdown));
        OnPropertyChanged(nameof(IsMarkdownRendered));
        OnPropertyChanged(nameof(ShowSourceSurface));
        LanguageName = type.DisplayName;
        if (IsLoadFinished)
        {
            _ = BuildDerivedContentAsync(_contentVersion);
            if (!(IsMarkdown && MarkdownMode == MarkdownViewMode.Rendered))
            {
                _ = BuildPresentationAsync();
            }
            _ = BuildMarkdownPreviewAsync();
        }
    }

    /// <summary>The tab icon reflects the file language: 语言缩写 Monogram(透明底 + 语言色相文字)。</summary>
    public override string Glyph => FileType.IconMonogram;

    public override string TabKey => $"file:{Path}";

    [ObservableProperty]
    private string content = string.Empty;

    /// <summary>Markdown files open as a rendered read-only document and can switch back to the
    /// existing AvalonEdit source surface.</summary>
    public bool IsMarkdown => string.Equals(FileType.LanguageId, "markdown", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private MarkdownViewMode markdownMode = MarkdownViewMode.Rendered;

    [ObservableProperty]
    private MarkdownRenderResult? markdownRenderResult;

    /// <summary>解析得到的标题列表:标签存活期间常驻(轻量),源码模式下光标→逻辑位置同步、
    /// 大纲跳转与重启恢复都不依赖渲染产物,因此 AST 释放/模式切换后仍可用。</summary>
    [ObservableProperty]
    private IReadOnlyList<MarkdownHeading> markdownHeadings = [];

    [ObservableProperty]
    private string markdownRenderNotice = string.Empty;

    [ObservableProperty]
    private double markdownVerticalOffset;

    /// <summary>当前 Markdown 标题锚点(统一逻辑位置状态的一部分;无标题时为空串)。
    /// 由预览滚动、源码光标和大纲点击统一写入,用于模式切换、标签切换与重启恢复。</summary>
    [ObservableProperty]
    private string markdownAnchor = string.Empty;

    /// <summary>当前 Markdown 标题的 1-based 源码行号(0 = 无);锚点失效时的回退定位目标。</summary>
    [ObservableProperty]
    private int markdownHeadingLine;

    public bool IsMarkdownRendered => IsMarkdown
        && MarkdownMode == MarkdownViewMode.Rendered
        && MarkdownRenderResult is not null;

    /// <summary>是否显示 AvalonEdit 源码面:Markdown 进入渲染模式即隐藏(不等渲染结果就绪),
    /// 加载/重载期间显示中性空白而非"源码 + 内置语法高亮"的闪帧;解析失败/大型文件切回
    /// 源码模式、或手动切到源码视图时自动恢复显示。</summary>
    public bool ShowSourceSurface => !(IsMarkdown && MarkdownMode == MarkdownViewMode.Rendered);

    partial void OnMarkdownModeChanged(MarkdownViewMode value)
    {
        OnPropertyChanged(nameof(IsMarkdownRendered));
        OnPropertyChanged(nameof(ShowSourceSurface));
        if (!IsMarkdown) return;

        if (value == MarkdownViewMode.Source)
        {
            _markdownCancellation?.Cancel();
            // 源码模式:释放渲染产物——绑定驱动视图清空 FlowDocument 并释放图片引用,
            // Markdig AST 随结果对象一并释放(不再与源码侧重复驻留)。标题列表常驻,
            // 源码模式下的光标→逻辑位置同步不中断;切回预览时重新解析。
            MarkdownRenderResult = null;
            if (IsLoadFinished && Content.Length > 0)
            {
                // 折叠整文扫描移到 worker(UI 线程发布),大 Markdown 切换不再卡顿。
                _ = ComputeFoldsAsync();
                _ = BuildPresentationAsync();
            }
        }
        else if (MarkdownRenderResult is null && IsLoadFinished)
        {
            // 切回预览:产物已被释放(或尚未构建)→ 重新解析渲染;
            // 阅读位置由视图按 锚点→行号→偏移 优先级恢复。
            _ = BuildMarkdownPreviewAsync();
        }
    }

    partial void OnMarkdownRenderResultChanged(MarkdownRenderResult? value) => OnPropertyChanged(nameof(IsMarkdownRendered));

    [RelayCommand]
    private void ToggleMarkdownView()
    {
        if (!IsMarkdown) return;
        MarkdownMode = MarkdownMode == MarkdownViewMode.Rendered
            ? MarkdownViewMode.Source
            : MarkdownViewMode.Rendered;
    }

    [RelayCommand]
    private void ShowMarkdownPreview()
    {
        if (IsMarkdown) MarkdownMode = MarkdownViewMode.Rendered;
    }

    [RelayCommand]
    private void ShowMarkdownSource() => MarkdownMode = MarkdownViewMode.Source;

    /// <summary>预览视图完成首次渲染后调用:把 Markdig AST 从结果中分离(FlowDocument 已承载
    /// 渲染内容),大 AST 不随预览长期驻留;主题变化/切回预览时从 Content 重新解析。</summary>
    public void DetachMarkdownDocument()
    {
        if (MarkdownRenderResult is { Document: not null } result)
        {
            result.Document = null;
        }
    }

    /// <summary>重新解析并构建预览(视图切回一个已释放/已分离渲染产物的标签时调用)。</summary>
    public Task RebuildMarkdownPreviewAsync() => BuildMarkdownPreviewAsync();

    /// <summary>Latest immutable language snapshot.  The view applies only this value, keeping
    /// TextMate/Roslyn work out of AvalonEdit's UI render path.</summary>
    [ObservableProperty]
    private CodePresentationSnapshot presentation = CodePresentationSnapshot.Empty();

    [ObservableProperty]
    private string notice = string.Empty;

    /// <summary>状态栏提示,独立于 <see cref="Notice"/>:文件外部变更但暂时无法读取/被删除时保留旧
    /// 内容并提示,文件恢复后自动刷新并清除。不用 Notice —— 那会把源码区切换成空态,而旧内容仍可读。</summary>
    [ObservableProperty]
    private string fileChangeNotice = string.Empty;

    /// <summary>Localized language label for the tab/breadcrumb/status bar.</summary>
    [ObservableProperty]
    private string languageName = string.Empty;

    [ObservableProperty]
    private string encodingName = string.Empty;

    [ObservableProperty]
    private string newlineTypeText = string.Empty;

    [ObservableProperty]
    private string fileSizeText = string.Empty;

    [ObservableProperty]
    private int lineCount;

    /// <summary>One-based global line represented by AvalonEdit line 1 in windowed mode.</summary>
    [ObservableProperty]
    private int windowStartLine = 1;

    public string AnalysisModeText => CapacityTier == ReadOnlyContentTier.Full ? "完整分析" : "窗口分析";

    [ObservableProperty]
    private bool isBinary;

    [ObservableProperty]
    private bool isTruncated;

    /// <summary>Capacity tier selected before the AvalonEdit document is created.  Full mode keeps
    /// every reading feature; larger tiers remain explicit rather than silently exhausting memory.</summary>
    [ObservableProperty]
    private ReadOnlyContentTier capacityTier = ReadOnlyContentTier.Full;

    // ===== Reading options (session-scoped; persisted later) =====
    // Defaults match VS Code: no word wrap, current line highlighted.

    [ObservableProperty]
    private bool wordWrap;

    [ObservableProperty]
    private bool showMinimap;

    [ObservableProperty]
    private bool showLineNumbers = true;

    [ObservableProperty]
    private bool showIndentGuides = true;

    [ObservableProperty]
    private bool showFoldingControls = true;

    /// <summary>Code font size (driven by Ctrl+wheel zoom; persisted as a reading preference).</summary>
    [ObservableProperty]
    private double fontSize = 14;

    /// <summary>源码/Diff 阅读器等宽字体(editor.fontFamily;视图渲染时转为 FontFamily)。</summary>
    [ObservableProperty]
    private string fontFamily = FontCatalog.DefaultEditorFamily;

    // ===== Document outline (right-hand symbol list) =====

    /// <summary>All symbols derived from the load state (C# / XML / JSON); empty for plain text.</summary>
    public BulkObservableCollection<CodeOutlineEntry> OutlineEntries { get; } = [];

    /// <summary>Outline entries narrowed by the outline filter box.</summary>
    public BulkObservableCollection<CodeOutlineEntry> FilteredOutlineEntries { get; } = [];

    /// <summary>Shared hierarchical symbols used by the tree view and folding projection.</summary>
    public BulkObservableCollection<CodeSymbolNode> SymbolRoots { get; } = [];

    /// <summary>Filtered symbol roots. Ancestors of matching nodes remain in the tree.</summary>
    public BulkObservableCollection<CodeSymbolNode> FilteredSymbolRoots { get; } = [];

    public CodeSymbolDocument SymbolDocument => _symbolDocument;
    public bool HasOutline => SymbolDocument.All.Any(node => node.ShowInOutline);

    /// <summary>光标行目前所属的符号祖先链(VS Code breadcrumb symbol segment 多级链,
    /// 外层 → 内层,最多 3 级;无符号时为空)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveSymbol))]
    private string activeSymbolText = string.Empty;

    public bool HasActiveSymbol => ActiveSymbolText.Length > 0;

    /// <summary>Active symbol ancestors, outermost to innermost, for breadcrumb navigation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveSymbol))]
    private IReadOnlyList<CodeSymbolNode> activeSymbolPath = [];

    /// <summary>VS Code sticky scroll 开关(会话级;命令 editor.action.toggleStickyScroll 切换)。</summary>
    [ObservableProperty]
    private bool showStickyScroll = true;

    /// <summary>迷你地图渲染模式:true=字符格,false=块(VS Code renderCharacters)。</summary>
    [ObservableProperty]
    private bool minimapRenderCharacters;

    /// <summary>迷你地图宽度(像素,60–320)。</summary>
    [ObservableProperty]
    private double minimapWidth = 130;

    /// <summary>相对行号(VS Code editor.lineNumbers relative:光标行为实号,其余显示相对距离)。</summary>
    [ObservableProperty]
    private bool lineNumbersRelative;

    /// <summary>垂直标尺列(editor.rulers,逗号分隔解析;空为无标尺)。</summary>
    [ObservableProperty]
    private double[]? rulerColumns;

    /// <summary>光标行变化 → 按符号范围更新当前节点、完整祖先链(面包屑),
    /// 并把当前逻辑位置同步到 Markdown 标题状态(源码模式)。</summary>
    public void UpdateCaretLine(int line)
    {
        var active = _symbolDocument.FindDeepestContaining(line);
        if (active is null)
        {
            foreach (var node in _wiredSymbolNodes)
            {
                node.IsActive = false;
            }

            ActiveSymbolPath = [];
            ActiveSymbolText = string.Empty;
            return;
        }

        ActivateSymbol(active);
        if (IsMarkdown)
        {
            SyncMarkdownHeadingFromLine(line);
        }
    }

    /// <summary>标记活动符号节点:大纲高亮、顶部面包屑祖先链;Markdown 标题自动展开所有父级,
    /// 保证活动节点在大纲树中可见。</summary>
    private void ActivateSymbol(CodeSymbolNode active)
    {
        foreach (var node in _wiredSymbolNodes)
        {
            node.IsActive = false;
        }

        active.IsActive = true;
        var chain = _symbolDocument.GetAncestors(active);
        if (IsMarkdown)
        {
            foreach (var ancestor in chain)
            {
                if (!ReferenceEquals(ancestor, active))
                {
                    ancestor.IsExpanded = true;
                }
            }
        }

        ActiveSymbolPath = chain;
        ActiveSymbolText = string.Join(" › ", chain.Select(entry => entry.DisplayName));
    }

    /// <summary>源码光标路径:光标行上方最近的标题 = 当前逻辑位置(无标题时保留上次状态)。</summary>
    private void SyncMarkdownHeadingFromLine(int line)
    {
        var heading = MarkdownHeadings.LastOrDefault(item => item.Line <= line);
        if (heading is null)
        {
            return;
        }

        MarkdownAnchor = heading.Anchor;
        MarkdownHeadingLine = heading.Line;
    }

    /// <summary>预览滚动路径:写入当前标题的逻辑位置,并同步大纲高亮、父级展开和顶部面包屑。
    /// 无标题(anchor 为空/行号非正,例如光标上方尚无任何标题)时不更新:保留上次逻辑位置,
    /// 也不改动大纲活动状态。</summary>
    public void SetMarkdownHeading(string? anchor, int line)
    {
        if (!IsMarkdown || anchor is not { Length: > 0 } || line <= 0)
        {
            return;
        }

        MarkdownAnchor = anchor;
        MarkdownHeadingLine = line;
        UpdateCaretLine(line);
    }

    [ObservableProperty]
    private string outlineFilterText = string.Empty;

    // ===== Folding + view state =====

    /// <summary>Foldable regions derived from the loaded content (braces / XML).</summary>
    private IReadOnlyList<CodeFoldSection> foldSections = [];

    public IReadOnlyList<CodeFoldSection> FoldSections
    {
        get => foldSections;
        private set => SetProperty(ref foldSections, value);
    }

    /// <summary>View state captured when the preview loses the editor (scroll offset, caret and
    /// folded section offsets) so re-activating the reused tab restores the previous reading spot.</summary>
    public EditorViewState? ViewState { get; set; }

    private sealed record DerivedContentSnapshot(
        CodeSymbolDocument Symbols,
        IReadOnlyList<CodeOutlineEntry> Outline,
        IReadOnlyList<CodeFoldSection> Folds,
        IReadOnlyList<TextSearchMatch> SearchMatches,
        string SearchError);

    /// <summary>Builds the expensive source projections away from the UI thread, then publishes
    /// one immutable result set. This mirrors VS Code's model/worker split: a newer content or
    /// language revision invalidates the worker result before it can mutate any bound collection.
    /// </summary>
    private async Task BuildDerivedContentAsync(int contentVersion, bool includeSearch = true)
    {
        _derivedCancellation?.Cancel();
        _derivedCancellation?.Dispose();
        var cancellation = _derivedCancellation = new CancellationTokenSource();
        var derivedVersion = Interlocked.Increment(ref _derivedVersion);

        var text = Content;
        var fileType = FileType;
        var outlineKind = fileType.OutlineKind;
        var query = SearchText;
        var caseSensitive = SearchCaseSensitive;
        var wholeWord = SearchWholeWord;
        var useRegex = SearchUseRegex;
        var searchInSelection = SearchInSelection;
        var selection = searchInSelection ? SelectionRangeProvider?.Invoke() : null;
        var renderedMarkdown = IsMarkdown && MarkdownMode == MarkdownViewMode.Rendered;

        try
        {
            var snapshot = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();

                IReadOnlyList<CodeOutlineEntry> compatibilityEntries = [];
                var symbols = CodeSymbolDocument.Empty;
                if (outlineKind != CodeOutlineKind.None && text.Length > 0)
                {
                    // Keep the injected parser as the compatibility/failure boundary. Both
                    // operations are pure and are now paid by a worker instead of the UI thread.
                    compatibilityEntries = _outlineParser.Parse(text, outlineKind);
                    symbols = _symbolAnalyzer.Analyze(text, outlineKind);
                }

                cancellation.Token.ThrowIfCancellationRequested();
                var outline = symbols.ToOutlineEntries();
                if (outline.Count == 0)
                {
                    outline = compatibilityEntries;
                }

                var folds = renderedMarkdown || text.Length == 0
                    ? Array.Empty<CodeFoldSection>()
                    : _folding.FindSections(text, fileType);

                IReadOnlyList<TextSearchMatch> matches = [];
                var searchError = string.Empty;
                if (includeSearch && !string.IsNullOrEmpty(query))
                {
                    searchError = useRegex
                        ? TextSearchService.GetRegexError(query, caseSensitive) ?? string.Empty
                        : string.Empty;
                    if (searchError.Length == 0)
                    {
                        var searchText = text;
                        var baseOffset = 0;
                        var baseLine = 0;
                        if (searchInSelection && selection is { } range && range.End > range.Start
                            && range.End <= text.Length)
                        {
                            searchText = text[range.Start..range.End];
                            baseOffset = range.Start;
                            for (var i = 0; i < range.Start; i++)
                            {
                                if (text[i] == '\n') baseLine++;
                            }
                        }

                        matches = _search.FindAll(searchText, query,
                                new TextSearchOptions(caseSensitive, wholeWord, useRegex))
                            .Select(match => new TextSearchMatch(
                                match.Length,
                                match.Offset + baseOffset,
                                match.Line + baseLine))
                            .Take(MaxSearchMatches)
                            .ToArray();
                    }
                }

                cancellation.Token.ThrowIfCancellationRequested();
                return new DerivedContentSnapshot(symbols, outline, folds, matches, searchError);
            }, cancellation.Token);

            if (cancellation.IsCancellationRequested
                || contentVersion != _contentVersion
                || derivedVersion != _derivedVersion
                || IsClosed
                || !ReferenceEquals(cancellation, _derivedCancellation))
            {
                return;
            }

            UnwireSymbolNodes();
            _symbolDocument = snapshot.Symbols;
            foreach (var root in _symbolDocument.Roots)
            {
                WireSymbolNode(root);
            }

            OutlineEntries.ReplaceRange(snapshot.Outline);
            SymbolRoots.ReplaceRange(_symbolDocument.Roots);
            RefreshFilteredOutline();
            OnPropertyChanged(nameof(HasOutline));
            OnPropertyChanged(nameof(SymbolDocument));
            UpdateCaretLine(Math.Max(1, LineCount > 0 ? 1 : 0));
            FoldSections = snapshot.Folds;

            // Search settings may change while the worker is running. Do not let a content
            // refresh overwrite a newer interactive search request.
            if (includeSearch
                && query == SearchText
                && caseSensitive == SearchCaseSensitive
                && wholeWord == SearchWholeWord
                && useRegex == SearchUseRegex
                && searchInSelection == SearchInSelection)
            {
                SearchErrorMessage = snapshot.SearchError;
                SearchMatches.ReplaceRange(snapshot.SearchMatches);
                MatchCount = SearchMatches.Count;
                CurrentMatchIndex = 0;
                CurrentMatchViewIndex = MatchCount > 0 ? 0 : -1;
            }
        }
        catch (OperationCanceledException)
        {
            // A newer content/language revision owns the next publication.
        }
    }

    private int _foldsVersion;

    /// <summary>折叠区间在 worker 线程整文计算,完成时经版本/内容双重校验后在 UI 线程发布
    /// (FoldSections 驱动视图重建,必须 UI 线程)。旧实现在渲染→源码切换时于 UI 线程
    /// 同步 FindSections,大文件可见卡顿。</summary>
    private async Task ComputeFoldsAsync()
    {
        var version = ++_foldsVersion;
        var content = Content;
        var fileType = FileType;
        IReadOnlyList<CodeFoldSection> folds;
        try
        {
            folds = content.Length == 0
                ? []
                : await Task.Run(() => _folding.FindSections(content, fileType));
        }
        catch (Exception)
        {
            return; // 折叠失败保留现状(视图自愈路径:下次内容管道重算)
        }

        if (version != _foldsVersion || !ReferenceEquals(Content, content))
        {
            return; // 期间又发起了更新或内容已替换:陈旧结果不得发布
        }

        FoldSections = folds;
    }

    partial void OnOutlineFilterTextChanged(string value) => RefreshFilteredOutline();

    [RelayCommand]
    private void JumpToOutline(CodeOutlineEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        // Keep the public quick-pick command's historical line event contract. The tree and
        // breadcrumb commands use JumpToSymbol for precise selection-range navigation.
        if (IsMarkdown && _symbolDocument.FindById(entry.Id) is { } node)
        {
            ApplyOutlineJumpPosition(node);
        }
        else if (IsMarkdown && MarkdownHeadings.FirstOrDefault(item => item.Line == entry.Line) is { } heading)
        {
            MarkdownAnchor = heading.Anchor;
            MarkdownHeadingLine = heading.Line;
        }

        // Line-only jump through the shared go-to-line command: windowed documents, line
        // clamping and event dispatch stay identical to the search-result jump.
        GoToLineInput = Math.Max(1, entry.Line).ToString();
        GoToLineCommand.Execute(null);
    }

    [RelayCommand]
    private void JumpToSymbol(CodeSymbolNode? node)
    {
        if (node is null)
        {
            return;
        }

        if (IsMarkdown)
        {
            // 大纲点击统一写入当前逻辑位置(锚点 + 行号),再交给视图执行跳转;
            // 预览模式跳锚点、源码模式跳源码行,两种模式的状态收敛一致。
            ApplyOutlineJumpPosition(node);
        }

        // Route through the shared go-to-line command instead of raising the raw position event
        // directly: windowed documents load the target window first and the line is clamped to
        // the document — exactly the path the search-result jump (OpenFileAtAsync) uses, so
        // outline clicks and search clicks behave the same.
        GoToLineInput = $"{Math.Max(1, node.SelectionRange.StartLine)}:{Math.Max(1, node.SelectionRange.StartColumn)}";
        GoToLineCommand.Execute(null);
    }

    /// <summary>大纲跳转的统一位置写入:锚点/行号来自同一标题模型,活动节点与面包屑同步高亮。</summary>
    private void ApplyOutlineJumpPosition(CodeSymbolNode node)
    {
        if (MarkdownHeadings.FirstOrDefault(item => item.Line == node.Range.StartLine) is { } heading)
        {
            MarkdownAnchor = heading.Anchor;
            MarkdownHeadingLine = heading.Line;
        }

        ActivateSymbol(node);
    }

    private void BuildOutline()
    {
        UnwireSymbolNodes();
        _symbolDocument = CodeSymbolDocument.Empty;
        IReadOnlyList<CodeOutlineEntry> entries = [];
        IReadOnlyList<CodeSymbolNode> roots = [];
        if (FileType.OutlineKind != CodeOutlineKind.None && Content.Length > 0)
        {
            // Keep the injected parser as a compatibility/failure boundary, while the shared
            // analyzer supplies ranges and parent/child links for navigation and folding.
            var compatibilityEntries = _outlineParser.Parse(Content, FileType.OutlineKind);
            _symbolDocument = _symbolAnalyzer.Analyze(Content, FileType.OutlineKind);
            entries = _symbolDocument.ToOutlineEntries();
            if (entries.Count == 0)
            {
                entries = compatibilityEntries;
            }

            foreach (var root in _symbolDocument.Roots)
            {
                WireSymbolNode(root);
            }

            roots = _symbolDocument.Roots;
        }

        OutlineEntries.ReplaceRange(entries);
        SymbolRoots.ReplaceRange(roots);
        RefreshFilteredOutline();
        OnPropertyChanged(nameof(HasOutline));
        OnPropertyChanged(nameof(SymbolDocument));
        UpdateCaretLine(Math.Max(1, LineCount > 0 ? 1 : 0));
    }

    private void RefreshFilteredOutline()
    {
        var query = OutlineFilterText?.Trim() ?? string.Empty;
        var filteredEntries = OutlineEntries.Where(entry => query.Length == 0
                || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        FilteredOutlineEntries.ReplaceRange(filteredEntries);
        FilteredSymbolRoots.ReplaceRange(_symbolDocument.Filter(query));
    }

    private void WireSymbolNode(CodeSymbolNode node)
    {
        _wiredSymbolNodes.Add(node);
        node.IsExpanded = !_collapsedSymbolIds.Contains(node.Id);
        node.PropertyChanged += OnSymbolNodePropertyChanged;
        foreach (var child in node.Children)
        {
            WireSymbolNode(child);
        }
    }

    private void UnwireSymbolNodes()
    {
        foreach (var node in _wiredSymbolNodes)
        {
            node.PropertyChanged -= OnSymbolNodePropertyChanged;
            node.IsActive = false;
        }

        _wiredSymbolNodes.Clear();
        ActiveSymbolPath = [];
        ActiveSymbolText = string.Empty;
    }

    private void OnSymbolNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CodeSymbolNode node || e.PropertyName != nameof(CodeSymbolNode.IsExpanded))
        {
            return;
        }

        if (node.IsExpanded)
        {
            _collapsedSymbolIds.Remove(node.Id);
        }
        else
        {
            _collapsedSymbolIds.Add(node.Id);
        }
    }

    // ===== Find state =====

    [ObservableProperty]
    private bool isFindBarOpen;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private bool searchCaseSensitive;

    [ObservableProperty]
    private bool searchWholeWord;

    [ObservableProperty]
    private bool searchUseRegex;

    [ObservableProperty]
    private int matchCount;

    /// <summary>0-based index of the currently focused match (resets on every search).</summary>
    [ObservableProperty]
    private int currentMatchIndex;

    /// <summary>0-based index of the current match inside <see cref="SearchMatches"/> (the view's
    /// render index): full-text mode mirrors <see cref="CurrentMatchIndex"/>; windowed mode holds the
    /// current match's position in the current window's match subset, or −1 when it is outside.</summary>
    [ObservableProperty]
    private int currentMatchViewIndex = -1;

    /// <summary>Find-bar error text (currently: invalid regular expression).</summary>
    [ObservableProperty]
    private string searchErrorMessage = string.Empty;

    /// <summary>All capped matches of the active search; the view renders these as background markers.</summary>
    public BulkObservableCollection<TextSearchMatch> SearchMatches { get; } = [];

    /// <summary>"3 / 40" style counter for the find bar (or 无匹配 / 空查找).</summary>
    public string CurrentMatchDisplay => string.IsNullOrEmpty(SearchText)
        ? string.Empty
        : MatchCount == 0 ? "无匹配" : $"{CurrentMatchIndex + 1} / {MatchCount}";

    // ===== Go-to-line state =====

    [ObservableProperty]
    private bool isGoToLineBarOpen;

    [ObservableProperty]
    private string goToLineInput = string.Empty;

    /// <summary>Raised after a validated go-to-line jump; the view centers + emphasizes the line.</summary>
    public event EventHandler<int>? GoToLineRequested;

    /// <summary>Raised for symbol navigation that has a precise selection range.</summary>
    public event EventHandler<(int Line, int Column)>? GoToPositionRequested;

    /// <summary>折叠请求(由视图映射到 CodeDocumentView;VS Code Ctrl+K Ctrl+0/J/1..9)。</summary>
    public event EventHandler<(EditorFoldRequest Kind, int Level)>? FoldRequested;

    /// <summary>主状态栏"当前文档状态"段的光标位置显示(由视图 code-behind 随 caret/选区变化写入,
    /// 形如 "Ln 12, Col 34 (已选择 8 字符 / 2 行)";原视图内独立底栏的内容已并入窗口主状态栏)。</summary>
    [ObservableProperty]
    private string caretPositionText = string.Empty;

    [RelayCommand]
    private void CollapseAllFold() => FoldRequested?.Invoke(this, (EditorFoldRequest.CollapseAll, 0));

    [RelayCommand]
    private void ExpandAllFold() => FoldRequested?.Invoke(this, (EditorFoldRequest.ExpandAll, 0));

    [RelayCommand]
    private void FoldToLevel(int level) => FoldRequested?.Invoke(this, (EditorFoldRequest.ToLevel, level));

    /// <summary>True once the preview payload is resident and its initial load has completed. A
    /// cache-evicted tab deliberately becomes false again so activation reads the latest file.</summary>
    public bool IsLoadFinished => !_payloadUnloaded && _loadTask?.IsCompleted == true;

    /// <summary>True when the decoded/derived payload is resident and no load or reload pass is
    /// still publishing it. The editor cache uses this as its eviction eligibility gate.</summary>
    internal bool IsPayloadLoaded => !IsClosed
        && !_payloadUnloaded
        && _loadTask?.IsCompleted == true
        && Volatile.Read(ref _pipelineActive) == 0;

    /// <summary>Approximate managed bytes retained by this tab's source payload. It intentionally
    /// overestimates the text plus common derived projections; it is accounting for eviction, not
    /// a profiler reading. Large file checkpoints are included even when the visible window is small.</summary>
    internal long RetainedMemoryBytes
    {
        get
        {
            var estimate = checked((long)Content.Length * 4L); // UTF-16 text + derived projections
            if (_documentSource is not null)
            {
                var checkpoints = Math.Max(1L, ((long)Math.Max(1, LineCount) / DocumentSourceCheckpointStride) + 1);
                estimate = checked(estimate
                    + DocumentSourceBaseMemoryBytes
                    + checkpoints * DocumentSourceCheckpointBytes);
            }

            return estimate;
        }
    }

    /// <summary>One shared load task: the editor activates tabs both eagerly (OpenTabAsync awaits) and
    /// from the selection-changed handler, so a single task guarantees every caller observes the same
    /// finished state instead of racing two file reads.</summary>
    public override Task LoadAsync()
    {
        if (IsClosed || _resourcesReleased)
        {
            return Task.CompletedTask;
        }

        if (_loadTask is { IsCompleted: false } loading)
        {
            return loading;
        }

        // A completed task remains cached after a failed initial read. Only an explicit payload
        // eviction is allowed to replace it and trigger a fresh lazy read.
        if (_loadTask is not null && !_payloadUnloaded)
        {
            return _loadTask;
        }

        _payloadUnloaded = false;
        _loadTask = RunContentPipelineAsync(initialLoad: true);
        OnPropertyChanged(nameof(IsLoadFinished));
        OnPropertyChanged(nameof(IsPayloadLoaded));
        return _loadTask;
    }

    /// <summary>外部变更重载:文件事件(保存/替换/创建/删除/重命名)后重新读取,所有已打开的预览
    /// (包括非活动标签)都经此串行化重载。只有最新一次请求运行解码管线,旧任务不得覆盖新内容。</summary>
    public Task ReloadAsync() => IsClosed || _resourcesReleased || _payloadUnloaded
        ? Task.CompletedTask
        : RunContentPipelineAsync(initialLoad: false);

    internal void UnloadContentForCache()
    {
        if (IsClosed || _resourcesReleased || _payloadUnloaded || !IsLoadFinished
            || Volatile.Read(ref _pipelineActive) != 0 || RetainedMemoryBytes <= 0)
        {
            return;
        }

        // Invalidate all workers before clearing their inputs. ContentUpdating runs while the
        // payload is still considered loaded, allowing the visible view to capture its position.
        _contentVersion++;
        _presentationVersion++;
        _derivedVersion++;
        _markdownVersion++;
        _foldsVersion++;
        _searchVersion++;
        CancelPendingWindowLoad();
        CancelPendingReloadRetry();
        ContentUpdating?.Invoke(this, EventArgs.Empty);

        MarkdownRenderResult = null;
        MarkdownHeadings = [];
        MarkdownRenderNotice = string.Empty;
        _searchTextDebounce.Cancel();
        CancelAndDispose(ref _presentationCancellation);
        CancelAndDispose(ref _derivedCancellation);
        CancelAndDispose(ref _markdownCancellation);
        if (_documentSource is not null)
        {
            var stale = _documentSource;
            _documentSource = null;
            _ = DisposeDocumentSourceAsync(stale);
        }

        Content = string.Empty;
        Presentation = CodePresentationSnapshot.Empty();
        Notice = string.Empty;
        FileChangeNotice = string.Empty;
        ClearFindResults();
        SearchErrorMessage = string.Empty;
        _windowedMatches.Clear();
        _windowTextLines = 0;
        _windowLineOffsets = [0];
        _searchCancellation?.Cancel();
        _searchCancellation = null;
        FilteredOutlineEntries.ReplaceRange([]);
        OutlineEntries.ReplaceRange([]);
        FoldSections = [];
        SymbolRoots.ReplaceRange([]);
        FilteredSymbolRoots.ReplaceRange([]);
        UnwireSymbolNodes();
        _symbolDocument = CodeSymbolDocument.Empty;

        _payloadUnloaded = true;
        _loadTask = null;
        OnPropertyChanged(nameof(IsLoadFinished));
        OnPropertyChanged(nameof(IsPayloadLoaded));
        OnPropertyChanged(nameof(RetainedMemoryBytes));
    }

    private async Task RunContentPipelineAsync(bool initialLoad)
    {
        if (IsClosed)
        {
            return;
        }

        Interlocked.Increment(ref _pipelineActive);
        var version = ++_contentVersion;
        var gateEntered = false;
        try
        {
            await _reloadGate.WaitAsync();
            gateEntered = true;
            if (version != _contentVersion || IsClosed)
            {
                return; // 等待门闩期间已被更新的请求超越
            }

            if (initialLoad)
            {
                await InitialLoadCoreAsync(version);
            }
            else
            {
                await ReloadCoreAsync(version);
            }
        }
        catch (ObjectDisposedException)
        {
            return; // 标签已释放,排队中的重载直接放弃
        }
        catch (Exception ex)
        {
            if (version != _contentVersion || IsClosed || _resourcesReleased)
            {
                return;
            }

            if (initialLoad)
            {
                EnsureMarkdownSourceMode();
                Notice = $"无法预览文件：{SingleLineMessage(ex)}";
            }
            else
            {
                FileChangeNotice = SummarizeReloadFailure(SingleLineMessage(ex), File.Exists(Path));
                ScheduleReloadRetry();
            }
        }
        finally
        {
            if (gateEntered)
            {
                _reloadGate.Release();
            }

            Interlocked.Decrement(ref _pipelineActive);
        }
    }

    private async Task InitialLoadCoreAsync(int version)
    {
        IReadOnlyDocumentSource? source = null;
        try
        {
            if (File.Exists(Path) && new FileInfo(Path).Length > PreviewLimit)
            {
                source = new FileReadOnlyDocumentSource(Path);
                _documentSource = source;
                var metadata = await source.GetMetadataAsync();
                if (version != _contentVersion || IsClosed)
                {
                    if (ReferenceEquals(source, _documentSource)) _documentSource = null;
                    await DisposeDocumentSourceAsync(source);
                    return;
                }
                FileSizeText = TextDocumentDecoder.FormatSize(metadata.ByteLength);
                CapacityTier = metadata.CapacityTier;
                OnPropertyChanged(nameof(AnalysisModeText));
                LanguageName = FileType.DisplayName;
                EncodingName = metadata.Encoding.WebName;
                NewlineTypeText = metadata.LineEnding == "\r\n" ? "CRLF" : metadata.LineEnding == "\n" ? "LF" : "CR";
                LineCount = metadata.TotalLines;
                if (metadata.IsBinary)
                {
                    _documentSource = null;
                    await DisposeDocumentSourceAsync(source);
                    IsBinary = true;
                    EnsureMarkdownSourceMode();
                    Notice = "二进制文件，无法预览。";
                    return;
                }

                if (CapacityTier == ReadOnlyContentTier.Summary)
                {
                    IsTruncated = true;
                    EnsureMarkdownSourceMode();
                    Notice = "文件超过 32 MB，已进入摘要模式；可通过跳转行进入窗口阅读。";
                    return;
                }

                // 窗口阅读不产生渲染预览:模式显式落到源码,源码面(含提示)可见;
                // 先于 LoadWindowAsync 切换,窗口派生工作(折叠等)按源码模式计算。
                EnsureMarkdownSourceMode();
                if (!await LoadWindowAsync(1))
                {
                    return;
                }
                if (version != _contentVersion || IsClosed) return;
                Notice = $"文件超过 8 MB，已进入大型文件窗口阅读：当前显示第 1–{Math.Min(FileReadOnlyDocumentSource.DefaultWindowLines, LineCount)} 行，共 {LineCount} 行。";
                return;
            }

            var result = await _decoder.DecodeAsync(Path, PreviewLimit);
            if (version != _contentVersion || IsClosed) return;
            FileSizeText = TextDocumentDecoder.FormatSize(result.FileSize);
            CapacityTier = ReadOnlyContentCapacity.ForSource(result.FileSize);
            OnPropertyChanged(nameof(AnalysisModeText));
            LanguageName = FileType.DisplayName;

            if (result.IsBinary)
            {
                IsBinary = true;
                EnsureMarkdownSourceMode();
                Notice = "二进制文件，无法预览。";
                return;
            }

            if (!result.Success)
            {
                IsTruncated = result.IsTruncated;
                EnsureMarkdownSourceMode();
                Notice = result.Error ?? "无法读取文件。";
                return;
            }

            await ApplyDecodedTextAsync(result, version);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException
            or System.Text.DecoderFallbackException
            or System.Xml.XmlException
            or System.Text.Json.JsonException)
        {
            if (version != _contentVersion || IsClosed) return;
            if (source is not null)
            {
                if (ReferenceEquals(source, _documentSource)) _documentSource = null;
                await DisposeDocumentSourceAsync(source);
            }
            Content = string.Empty;
            EnsureMarkdownSourceMode();
            Notice = $"无法预览文件：{ex.Message}";
        }
    }

    /// <summary>外部变更重载管线:成功(或确定性新状态)时发布;暂时不可读/被删除时保留旧内容并
    /// 提示(独立于 Notice),稍后自动重试。</summary>
    private async Task ReloadCoreAsync(int version)
    {
        try
        {
            if (File.Exists(Path) && new FileInfo(Path).Length > PreviewLimit)
            {
                if (version != _contentVersion || IsClosed) return;
                await ReloadWindowedCoreAsync(version);
                return;
            }

            // 窗口化 → 全文迁移:释放旧文件源,避免活动查找/窗口跳转命中已失效的稀疏索引。
            if (_documentSource is not null)
            {
                var stale = _documentSource;
                _documentSource = null;
                CancelPendingWindowLoad();
                _ = DisposeDocumentSourceAsync(stale);
                _windowedMatches.Clear();
                _windowTextLines = 0;
                _windowLineOffsets = [0];
                _searchCancellation?.Cancel();
            }

            var result = await _decoder.DecodeAsync(Path, PreviewLimit);
            if (version != _contentVersion || IsClosed) return;

            if (result.IsBinary)
            {
                // 文件真正变成二进制:按初始加载的二进制空态策略显示新状态。
                CancelDerivedWork();
                ContentUpdating?.Invoke(this, EventArgs.Empty);
                Content = string.Empty;
                IsBinary = true;
                IsTruncated = false;
                EnsureMarkdownSourceMode();
                Notice = "二进制文件，无法预览。";
                FileChangeNotice = string.Empty;
                EncodingName = string.Empty;
                NewlineTypeText = string.Empty;
                LineCount = 0;
                CancelPendingReloadRetry();
                return;
            }

            if (!result.Success)
            {
                // 暂时不可读(写入中/被锁定/已删除):保留旧内容,提示;恢复后自动重试刷新。
                FileChangeNotice = SummarizeReloadFailure(result.Error, File.Exists(Path));
                ScheduleReloadRetry();
                return;
            }

            await ApplyDecodedTextAsync(result, version);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException
            or System.Text.DecoderFallbackException
            or System.Xml.XmlException
            or System.Text.Json.JsonException)
        {
            if (version != _contentVersion || IsClosed) return;
            FileChangeNotice = SummarizeReloadFailure(SingleLineMessage(ex), File.Exists(Path));
            ScheduleReloadRetry();
        }
    }

    /// <summary>成功解码后的统一发布:写新内容、重算查找/大纲/折叠/语义高亮/Markdown 预览。
    /// 发布前触发 <see cref="ContentUpdating"/>,让视图保存滚动/光标/选区/折叠位置以便恢复。</summary>
    private async Task ApplyDecodedTextAsync(TextDecodeResult result, int version)
    {
        ContentUpdating?.Invoke(this, EventArgs.Empty);
        Content = result.Text ?? string.Empty;
        EncodingName = result.EncodingName ?? string.Empty;
        NewlineTypeText = result.NewlineType switch
        {
            NewlineType.CrLf => "CRLF",
            NewlineType.Lf => "LF",
            NewlineType.Cr => "CR",
            NewlineType.Mixed => "混合",
            _ => string.Empty,
        };
        LineCount = result.LineCount;
        FileSizeText = TextDocumentDecoder.FormatSize(result.FileSize);
        CapacityTier = ReadOnlyContentCapacity.ForSource(result.FileSize);
        OnPropertyChanged(nameof(AnalysisModeText));
        LanguageName = FileType.DisplayName;
        IsBinary = false;
        IsTruncated = false;
        Notice = string.Empty;
        FileChangeNotice = string.Empty;
        CancelPendingReloadRetry();
        if (version != _contentVersion || IsClosed) return;

        if (version != _contentVersion || IsClosed) return;

        // VS Code-style staged publication: source text and metadata become available first;
        // expensive derived work runs off the UI thread and publishes only when this content
        // revision is still current. Starting the passes together avoids making a large syntax
        // pass serialize behind outline/folding work.
        var derivedTask = BuildDerivedContentAsync(version);
        var presentationTask = IsMarkdown && MarkdownMode == MarkdownViewMode.Rendered
            ? Task.CompletedTask
            : BuildPresentationAsync();
        var markdownTask = BuildMarkdownPreviewAsync();
        await Task.WhenAll(derivedTask, presentationTask, markdownTask);
    }

    /// <summary>大文件(窗口模式)重载:重建稀疏字节/行索引并保留当前阅读窗口附近的位置;索引
    /// 失效后,活动查找按新文件全文重跑再重挂到当前窗口。</summary>
    private async Task ReloadWindowedCoreAsync(int version)
    {
        var focusGlobalLine = WindowStartLine;
        CancelPendingWindowLoad();
        await _windowLoadGate.WaitAsync().ConfigureAwait(true);
        IReadOnlyDocumentSource? source = null;
        try
        {
            if (version != _contentVersion || IsClosed)
            {
                return;
            }

            if (_documentSource is not null)
            {
                var stale = _documentSource;
                _documentSource = null;
                _windowedMatches.Clear();
                _searchCancellation?.Cancel();
                await stale.DisposeAsync().ConfigureAwait(true);
            }

            source = new FileReadOnlyDocumentSource(Path);
            _documentSource = source;
            var metadata = await source.GetMetadataAsync().ConfigureAwait(true);
            if (version != _contentVersion || IsClosed)
            {
                _documentSource = null;
                await source.DisposeAsync().ConfigureAwait(true);
                return;
            }

            FileSizeText = TextDocumentDecoder.FormatSize(metadata.ByteLength);
            CapacityTier = metadata.CapacityTier;
            OnPropertyChanged(nameof(AnalysisModeText));
            LanguageName = FileType.DisplayName;
            EncodingName = metadata.Encoding.WebName;
            NewlineTypeText = metadata.LineEnding == "\r\n" ? "CRLF" : metadata.LineEnding == "\n" ? "LF" : "CR";
            LineCount = metadata.TotalLines;
            if (metadata.IsBinary)
            {
                CancelDerivedWork();
                ContentUpdating?.Invoke(this, EventArgs.Empty);
                Content = string.Empty;
                IsBinary = true;
                IsTruncated = false;
                EnsureMarkdownSourceMode();
                Notice = "二进制文件，无法预览。";
                FileChangeNotice = string.Empty;
                CancelPendingReloadRetry();
                return;
            }

            if (metadata.CapacityTier == ReadOnlyContentTier.Summary)
            {
                CancelDerivedWork();
                ContentUpdating?.Invoke(this, EventArgs.Empty);
                Content = string.Empty;
                IsTruncated = true;
                EnsureMarkdownSourceMode();
                Notice = "文件超过 32 MB，已进入摘要模式；可通过跳转行进入窗口阅读。";
                FileChangeNotice = string.Empty;
                CancelPendingReloadRetry();
                return;
            }

            IsBinary = false;
            IsTruncated = false;
            Notice = string.Empty;
            FileChangeNotice = string.Empty;
            ContentUpdating?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            if (source is not null && ReferenceEquals(source, _documentSource))
            {
                _documentSource = null;
                await source.DisposeAsync().ConfigureAwait(true);
            }

            throw;
        }
        finally
        {
            _windowLoadGate.Release();
        }

        if (!await LoadWindowAsync(Math.Max(1, focusGlobalLine)))
        {
            return;
        }

        if (version != _contentVersion || IsClosed) return;
        // 窗口阅读不产生渲染预览:模式显式落到源码(全文→窗口迁移时释放旧渲染产物),
        // 源码面(含提示)保持可见;放在窗口内容发布后,模式切换的派生工作面向新窗口。
        EnsureMarkdownSourceMode();
        CancelPendingReloadRetry();
        // 稀疏索引已重建:活动查找按新文件全文重跑,再把匹配重挂到当前窗口。
        if (!string.IsNullOrEmpty(SearchText))
        {
            await RefreshWindowedSearchAsync(SearchText);
        }
    }

    /// <summary>确定性新状态(二进制/摘要模式)发布时取消在途的派生工作(语义高亮/窗口化查找)。</summary>
    private void CancelDerivedWork()
    {
        _derivedCancellation?.Cancel();
        _presentationCancellation?.Cancel();
        _searchCancellation?.Cancel();
        _markdownCancellation?.Cancel();
    }

    private static void CancelAndDispose(ref CancellationTokenSource? cancellation)
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
    }

    private void CancelPendingWindowLoad()
    {
        CancellationTokenSource? cancellation;
        lock (_windowRequestGate)
        {
            _windowRequestVersion++;
            cancellation = _windowCancellation;
        }

        cancellation?.Cancel();
    }

    private (int Version, CancellationTokenSource Cancellation) BeginWindowLoad()
    {
        CancellationTokenSource? previous;
        CancellationTokenSource current;
        int version;
        lock (_windowRequestGate)
        {
            previous = _windowCancellation;
            current = new CancellationTokenSource();
            _windowCancellation = current;
            version = ++_windowRequestVersion;
        }

        previous?.Cancel();
        return (version, current);
    }

    private bool IsCurrentWindowLoad(int version, IReadOnlyDocumentSource source, CancellationToken cancellationToken)
    {
        lock (_windowRequestGate)
        {
            return version == _windowRequestVersion
                && ReferenceEquals(source, _documentSource)
                && !cancellationToken.IsCancellationRequested
                && !_resourcesReleased
                && !IsClosed;
        }
    }

    private void FinishWindowLoad(int version, CancellationTokenSource cancellation)
    {
        lock (_windowRequestGate)
        {
            if (version == _windowRequestVersion && ReferenceEquals(cancellation, _windowCancellation))
            {
                _windowCancellation = null;
            }
        }

        cancellation.Dispose();
    }

    private async Task DisposeDocumentSourceAsync(IReadOnlyDocumentSource source)
    {
        try
        {
            await _windowLoadGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _windowLoadGate.Release();
            }
        }
        catch (Exception) when (_resourcesReleased || IsClosed)
        {
            // A closing tab only needs best-effort source cleanup; the source is no longer
            // reachable from the tab after detachment.
        }
        catch (Exception)
        {
            // The source was detached before disposal. Cleanup is best effort even during a
            // full-file/windowed migration; observing the exception here prevents an unobserved
            // fire-and-forget disposal task from surfacing as a process-level UI error.
        }
    }

    private void CancelPendingReloadRetry()
    {
        _reloadRetryCancellation?.Cancel();
        _reloadRetryCancellation?.Dispose();
        _reloadRetryCancellation = null;
    }

    /// <summary>文件暂时不可读的提示文案(独立于 Notice,源码区保留旧内容)。</summary>
    private static string SummarizeReloadFailure(string? detail, bool fileExists) =>
        fileExists
            ? $"文件发生外部变更但暂时无法读取（可能仍在保存中）：{detail ?? "未知错误"}"
            : "文件已被删除或暂时不可读；内容恢复后将自动刷新。";

    /// <summary>短暂延迟后自动重试(写入中/锁定/删除恢复);仅在标签存活时调度,发布成功即取消。</summary>
    private void ScheduleReloadRetry()
    {
        if (IsClosed)
        {
            return;
        }

        _reloadRetryCancellation?.Cancel();
        _reloadRetryCancellation?.Dispose();
        var cancellation = _reloadRetryCancellation = new CancellationTokenSource();
        _ = Task.Delay(ReloadRetryDelay, cancellation.Token).ContinueWith(_ =>
        {
            if (cancellation.IsCancellationRequested || IsClosed)
            {
                return;
            }

            // 回到 UI 线程再触发重载,确保内容发布与 ContentUpdating 都在 UI 线程执行。
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                _ = ReloadAsync();
            }
            else
            {
                dispatcher.BeginInvoke(() => _ = ReloadAsync());
            }
        }, TaskScheduler.Default);
    }

    private async Task<bool> LoadWindowAsync(int globalLine)
    {
        var source = _documentSource;
        if (source is null)
        {
            return false;
        }

        var (requestVersion, cancellation) = BeginWindowLoad();
        var gateEntered = false;
        try
        {
            await _windowLoadGate.WaitAsync(cancellation.Token).ConfigureAwait(true);
            gateEntered = true;
            if (!IsCurrentWindowLoad(requestVersion, source, cancellation.Token))
            {
                return false;
            }

            var contentVersion = _contentVersion;
            var start = Math.Max(1, globalLine - FileReadOnlyDocumentSource.DefaultWindowLines / 3);
            var window = await source.ReadWindowAsync(
                new DocumentRange(start, FileReadOnlyDocumentSource.DefaultWindowLines), cancellation.Token);
            if (!IsCurrentWindowLoad(requestVersion, source, cancellation.Token)
                || contentVersion != _contentVersion)
            {
                return false;
            }

            WindowStartLine = window.StartLine;
            Content = window.Text;
            // 记录窗口行起点:查找匹配的全局行号→窗口内偏移依赖它。
            _windowLineOffsets = window.LineOffsets;
            _windowTextLines = Math.Max(0, window.LineOffsets.Count - 1);
            var derivedTask = BuildDerivedContentAsync(contentVersion, includeSearch: false);
            var presentationTask = BuildPresentationAsync();
            await Task.WhenAll(derivedTask, presentationTask);
            if (!IsCurrentWindowLoad(requestVersion, source, cancellation.Token)
                || contentVersion != _contentVersion)
            {
                return false;
            }

            // 活动查找下的窗口切换:把全文匹配列表重挂到新窗口坐标(当前匹配落位由视图侧完成)。
            if (!string.IsNullOrEmpty(SearchText))
            {
                ApplyWindowedMatches();
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (IsCurrentWindowLoad(requestVersion, source, cancellation.Token))
            {
                Notice = $"无法加载文件窗口：{SingleLineMessage(ex)}";
            }

            return false;
        }
        finally
        {
            if (gateEntered)
            {
                _windowLoadGate.Release();
            }

            FinishWindowLoad(requestVersion, cancellation);
        }
    }

    private async Task BuildPresentationAsync()
    {
        _presentationCancellation?.Cancel();
        _presentationCancellation?.Dispose();
        _presentationCancellation = new CancellationTokenSource();
        var version = ++_presentationVersion;
        try
        {
            var cacheIdentity = CodePresentationCacheIdentity.FromFile(Path, EncodingName, WindowStartLine);
            // 首屏增量发布:部分快照(IsComplete=false)覆盖首屏,让可见文本在 1~2 帧内拿到
            // 最终配色,消除"缺省色 → 正确高亮"闪帧;全文快照在分词完成后发布。
            var syncContext = SynchronizationContext.Current;
            var snapshot = await CodePresentationService.Instance.AnalyzeTokensAsync(
                Content,
                FileType,
                version,
                cacheIdentity,
                onFirstBatch: partial =>
                {
                    if (syncContext is null)
                    {
                        // 无同步上下文的测试环境:内联发布(与其他管线续帖在测试线程外落地的
                        // 既有语义一致)。
                        PublishPresentation(partial, version);
                    }
                    else
                    {
                        syncContext.Post(_ => PublishPresentation(partial, version), null);
                    }
                },
                _presentationCancellation.Token);
            PublishPresentation(snapshot, version);
        }
        catch (OperationCanceledException)
        {
            // Replaced/closed tabs intentionally cancel stale presentation work.
        }
    }

    /// <summary>版本门控发布快照:只有最新一次构建可发布;该版本完整快照已发布后,迟到的
    /// 部分快照丢弃(同步上下文通常已保证先发后至,此为兜底)。</summary>
    private void PublishPresentation(CodePresentationSnapshot snapshot, int version)
    {
        if (version != _presentationVersion || _presentationCancellation is { IsCancellationRequested: true }) return;
        if (!snapshot.IsComplete && Presentation is { DocumentVersion: var current, IsComplete: true } && current == version) return;
        Presentation = snapshot;
    }

    private int _markdownVersion;

    /// <summary>渲染预览不可用的确定性状态(二进制/摘要/窗口阅读/解码失败)显式落到源码模式:
    /// 源码面可见性(ShowSourceSurface)、折叠与语义高亮派生都按模式收敛,旧渲染产物随
    /// OnMarkdownModeChanged 释放。幂等:非 Markdown 或已在源码模式时为空操作。</summary>
    private void EnsureMarkdownSourceMode()
    {
        if (IsMarkdown && MarkdownMode == MarkdownViewMode.Rendered)
        {
            MarkdownMode = MarkdownViewMode.Source;
        }
    }

    private async Task BuildMarkdownPreviewAsync()
    {
        // 版本化构建:外部重载 / 主题变化 / 切回预览可并发发起;只有最新一次能发布结果,
        // 旧解析(内容已再次变化)不得覆盖新内容。
        var version = ++_markdownVersion;
        _markdownCancellation?.Cancel();
        // 不提前置空渲染结果:重载/主题切换期间旧渲染文档保持显示,新结果就绪后由绑定整体
        // 替换(视图侧 Rebuild 按结果对象身份换文档),消除"渲染视图 → 空白/源码 → 新渲染"
        // 的闪帧。不产生新结果的分支在返回前显式清空,旧 AST 不随标签长期驻留。
        MarkdownRenderNotice = string.Empty;
        OnPropertyChanged(nameof(IsMarkdown));
        if (!IsMarkdown)
        {
            // 语言模式改到非 Markdown:释放渲染产物,视图文档随绑定清空。
            MarkdownRenderResult = null;
            return;
        }
        if (MarkdownMode == MarkdownViewMode.Source)
        {
            // 源码模式不产生渲染产物;切到源码时结果已被 OnMarkdownModeChanged 清空,幂等兜底。
            MarkdownRenderResult = null;
            return;
        }
        if (CapacityTier != ReadOnlyContentTier.Full)
        {
            // 切到源码模式:OnMarkdownModeChanged 负责清空结果。
            MarkdownMode = MarkdownViewMode.Source;
            MarkdownRenderNotice = "大型 Markdown 文件使用源码视图；渲染预览仅对完整加载文件启用。";
            return;
        }

        MarkdownRenderResult result;
        var cancellation = _markdownCancellation = new CancellationTokenSource();
        try
        {
            result = await MarkdownPreviewService.Instance.ParseAsync(Content, Path, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // 令牌默认不取消;保留显式分支以防未来接入取消。
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // 仅解析失败才回退源码模式;结果赋值触发的视图侧异常不属于"渲染失败"。
            if (version != _markdownVersion) return;
            MarkdownMode = MarkdownViewMode.Source;
            MarkdownRenderNotice = $"Markdown 渲染失败，已切换到源码视图：{SingleLineMessage(ex)}";
            return;
        }

        if (version != _markdownVersion || cancellation.IsCancellationRequested)
        {
            // This result will never be handed to MarkdownPreviewView, so the view cannot
            // perform its normal ownership handoff/release for the worker-side preheat refs.
            MarkdownPreviewService.ReleasePreheatedPaths(result.PreheatedPaths);
            return;
        }
        MarkdownHeadings = result.Headings;
        // 完成时的模式为准:解析期间用户已切到源码则丢弃 AST(切回预览再解析),
        // 避免后台标签持有解析产物。
        if (MarkdownMode == MarkdownViewMode.Rendered)
        {
            MarkdownRenderResult = result;
        }
        else
        {
            MarkdownPreviewService.ReleasePreheatedPaths(result.PreheatedPaths);
        }
    }

    /// <summary>把异常消息压成单行供状态栏提示:换行符(\r\n/\n/\r)→空格、连续空白折叠、
    /// 超长截断——提示位是单行 TextBlock,多行消息会破坏状态栏布局。</summary>
    internal static string SingleLineMessage(Exception ex)
    {
        var message = ex.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return "未知错误";
        }

        var builder = new StringBuilder(message.Length);
        var pendingSpace = false;
        foreach (var ch in message)
        {
            if (ch is '\r' or '\n' or '\t' or ' ')
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            pendingSpace = false;
            builder.Append(ch);
        }

        var oneLine = builder.ToString().Trim();
        return oneLine.Length <= 120 ? oneLine : oneLine[..120] + "…";
    }

    /// <summary>主题在预览模式运行时变化:FlowDocument 需要整树重渲染,而 AST 已在渲染完成后
    /// 分离,故重新解析(非活动标签不重建——其视图不可见,AST 会失去渲染出口)。</summary>
    private void OnAppThemeChanged(object? sender, AppTheme theme)
    {
        if (IsMarkdown && IsActive && IsLoadFinished
            && MarkdownMode == MarkdownViewMode.Rendered
            && MarkdownRenderResult is not null)
        {
            _ = BuildMarkdownPreviewAsync();
        }
    }

    // ===== Find state: refresh + navigation commands =====

    /// <summary>E10: 击键防抖——**窗口化(大文件流式)路径**每次输入重置 250ms 尾部去抖,
    /// 停顿后才发起一次流式重扫,不再每键取消旧任务并从头扫描。空文本与非法正则不产生
    /// 扫描,保持立即生效(Escape/清空与“无匹配”错误提示不被去抖拖慢);选项切换
    /// (大小写/全词/正则)是显式用户动作,仍立即刷新。防抖窗口内旧匹配列表保留,
    /// 新结果按差分替换(见 <see cref="ReplaceWindowedMatches"/>)。
    /// 全文(内存 ≤8MB)路径 FindAll 是廉价同步扫描,保持“输入即出匹配”的既有语义,
    /// 不去抖(既有测试对同步重计数有断言)。</summary>
    partial void OnSearchTextChanged(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            _searchTextDebounce.Cancel();
            RefreshSearch();
            return;
        }

        if (SearchUseRegex && TextSearchService.GetRegexError(value, SearchCaseSensitive) is not null)
        {
            // 非法正则:立即走错误路径(不扫描、不等待)。
            _searchTextDebounce.Cancel();
            RefreshSearch();
            return;
        }

        if (SearchErrorMessage is not { Length: 0 })
        {
            // 查询重新变合法:先清掉过期的错误提示,结果由(去抖后的)扫描替换。
            SearchErrorMessage = string.Empty;
        }

        if (_documentSource is null)
        {
            RefreshSearch();
            return;
        }

        _searchTextDebounce.Schedule();
    }

    partial void OnSearchCaseSensitiveChanged(bool value) => RefreshSearch();

    partial void OnSearchWholeWordChanged(bool value) => RefreshSearch();

    partial void OnSearchUseRegexChanged(bool value) => RefreshSearch();

    /// <summary>仅在编辑器当前选区范围内查找(VS Code find-in-selection;窗口化大文件模式忽略)。</summary>
    [ObservableProperty]
    private bool searchInSelection;

    partial void OnSearchInSelectionChanged(bool value) => RefreshSearch();

    /// <summary>编辑器当前是否存在文本选区(驱动"所选范围"复选框显隐;由视图维护)。</summary>
    [ObservableProperty]
    private bool hasTextSelection;

    /// <summary>是否高亮全部匹配(VS Code "全部高亮"开关;关闭时视图隐藏装饰,导航仍可用)。</summary>
    [ObservableProperty]
    private bool showAllHighlights = true;

    /// <summary>由视图注入:返回当前选区在文本中的字符范围(start,end);无选区返回 null。</summary>
    public Func<(int Start, int End)?>? SelectionRangeProvider { get; set; }

    partial void OnMatchCountChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentMatchDisplay));
        // CanExecute 走方法名(nameof(CanStepMatch)),框架不会自动追踪属性变化;
        // 匹配数变化(含窗口化异步搜索完成)必须显式刷新"上一个/下一个"按钮可用态,
        // 否则按钮停留在初始的禁用外观。
        FindNextCommand.NotifyCanExecuteChanged();
        FindPreviousCommand.NotifyCanExecuteChanged();
    }

    partial void OnCurrentMatchIndexChanged(int value) => OnPropertyChanged(nameof(CurrentMatchDisplay));

    /// <summary>Re-runs the active search over the loaded content. Capped so huge files never flood
    /// the find overlay; the counter reports the capped count. A malformed regular expression is
    /// surfaced through <see cref="SearchErrorMessage"/> instead of a silent “无匹配”.</summary>
    private void RefreshSearch()
    {
        if (string.IsNullOrEmpty(SearchText))
        {
            CancelWindowedSearch();
            SearchErrorMessage = string.Empty;
            ClearFindResults();
            return;
        }

        if (SearchUseRegex && TextSearchService.GetRegexError(SearchText, SearchCaseSensitive) is { } error)
        {
            CancelWindowedSearch();
            ClearFindResults();
            _windowedMatches.Clear();
            SearchErrorMessage = error;
            return;
        }

        SearchErrorMessage = string.Empty;

        if (_documentSource is not null)
        {
            _ = RefreshWindowedSearchAsync(SearchText);
            return;
        }

        if (Content.Length == 0)
        {
            ClearFindResults();
            return;
        }

        IReadOnlyList<TextSearchMatch> all;
        if (SearchInSelection && SelectionRangeProvider?.Invoke() is { } range && range.End > range.Start
            && range.End <= Content.Length)
        {
            // 选区查找:在选区窗口内搜索,再把行号重挂回全文坐标。
            var window = Content[range.Start..range.End];
            var baseLine = 0;
            for (var i = 0; i < range.Start && i < Content.Length; i++)
            {
                if (Content[i] == '\n') baseLine++;
            }
            all = _search.FindAll(window, SearchText,
                    new TextSearchOptions(SearchCaseSensitive, SearchWholeWord, SearchUseRegex))
                .Select(match => new TextSearchMatch(match.Length, match.Offset + range.Start, baseLine + match.Line))
                .ToArray();
        }
        else
        {
            all = _search.FindAll(Content, SearchText, new TextSearchOptions(SearchCaseSensitive, SearchWholeWord, SearchUseRegex));
        }

        SearchMatches.ReplaceRange(all.Take(MaxSearchMatches));

        MatchCount = SearchMatches.Count;
        CurrentMatchIndex = 0;
        CurrentMatchViewIndex = MatchCount > 0 ? 0 : -1;
    }

    private void ClearFindResults()
    {
        SearchMatches.ReplaceRange([]);
        MatchCount = 0;
        CurrentMatchIndex = 0;
        CurrentMatchViewIndex = -1;
    }

    private void CancelWindowedSearch()
    {
        Interlocked.Increment(ref _searchVersion);
        _searchCancellation?.Cancel();
    }

    private async Task RefreshWindowedSearchAsync(string query)
    {
        var source = _documentSource;
        if (source is null) return;
        // 版本令牌必须原子自增:快速连续切换选项会并发派发多个流式搜索,
        // 非原子的 ++ 会丢失更新,让过期的旧搜索"获胜"并写回陈旧结果。
        var version = Interlocked.Increment(ref _searchVersion);
        // 只 Cancel 不 Dispose:旧搜索任务可能仍在引用其令牌(取消传播),Dispose 留给 GC。
        _searchCancellation?.Cancel();
        var cancellation = _searchCancellation = new CancellationTokenSource();
        var matches = new List<DocumentSearchMatch>();
        try
        {
            var options = new TextSearchOptions(SearchCaseSensitive, SearchWholeWord, SearchUseRegex);
            await foreach (var match in source.SearchAsync(query, MaxSearchMatches, options, null, cancellation.Token)) matches.Add(match);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (version == _searchVersion
                && query == SearchText
                && ReferenceEquals(source, _documentSource)
                && !IsClosed
                && !_resourcesReleased)
            {
                SearchErrorMessage = SingleLineMessage(ex);
            }

            return;
        }

        if (version != _searchVersion || query != SearchText
            || !ReferenceEquals(source, _documentSource)
            || cancellation.IsCancellationRequested) return;
        ReplaceWindowedMatches(matches);
        MatchCount = matches.Count;
        CurrentMatchIndex = 0;
        ApplyWindowedMatches();
    }

    /// <summary>E10: 权威窗口化匹配列表的增量替换。新结果里存活的匹配保留上一轮的实例
    /// (稳定引用,视图可按引用做段差量、只换变化的段),过期的直接跳过(相当于移除),
    /// 新增的追加——不再 Clear+整表重挂。严格细化查询(字面前缀 + 更长)的结果是上一轮
    /// 按位置的子集,因此只剩"移除"一侧,旧列表在扫描期间一直保留显示。</summary>
    private void ReplaceWindowedMatches(IReadOnlyList<DocumentSearchMatch> incoming)
    {
        var previous = _windowedMatches;
        if (previous.Count == 0)
        {
            _windowedMatches = new List<DocumentSearchMatch>(incoming);
            return;
        }

        if (previous.Count == incoming.Count)
        {
            var unchanged = true;
            for (var i = 0; i < previous.Count; i++)
            {
                if (previous[i] != incoming[i])
                {
                    unchanged = false;
                    break;
                }
            }

            if (unchanged)
            {
                return;
            }
        }

        var previousByPosition = new Dictionary<(int Line, int Column), int>(previous.Count);
        for (var i = 0; i < previous.Count; i++)
        {
            previousByPosition[(previous[i].Line, previous[i].Column)] = i;
        }

        var kept = new bool[previous.Count];
        var merged = new List<DocumentSearchMatch>(incoming.Count);
        foreach (var match in incoming)
        {
            // 位置命中且值相等(含长度/预览)才复用旧实例;细化后同一位置长度变化视为
            // "变化的段",用新实例替换。
            if (previousByPosition.TryGetValue((match.Line, match.Column), out var index)
                && !kept[index]
                && previous[index] == match)
            {
                kept[index] = true;
                merged.Add(previous[index]);
            }
            else
            {
                merged.Add(match);
            }
        }

        _windowedMatches = merged;
    }

    /// <summary>Remaps the authoritative windowed matches onto the currently loaded window: only
    /// matches inside the window enter <see cref="SearchMatches"/> (window-local line + offset
    /// resolved from the window text); <see cref="MatchCount"/> keeps the full-file count so the
    /// counter stays global while the view highlights only what is displayed. E10: 子集按差分
    /// 挂回(见 <see cref="ReplaceSearchMatchesIncremental"/>)——存活的匹配保留实例,过期移除、
    /// 新增追加,不再整表 Clear+重挂。</summary>
    private void ApplyWindowedMatches()
    {
        if (_documentSource is null || _windowedMatches.Count == 0)
        {
            ClearFindResults();
            return;
        }

        var windowEnd = WindowStartLine + _windowTextLines; // exclusive
        var subset = new List<TextSearchMatch>(_windowedMatches.Count);
        var viewIndex = -1;
        for (var i = 0; i < _windowedMatches.Count; i++)
        {
            var match = _windowedMatches[i];
            if (match.Line < WindowStartLine || match.Line >= windowEnd) continue;
            var localLine = match.Line - WindowStartLine + 1;
            // LineOffsets[k] 记录的是分隔线 k 与 k+1 的换行符位置,行起点 = 换行符位置 + 1。
            var lineStart = localLine == 1 ? 0 : _windowLineOffsets[localLine - 1] + 1;
            subset.Add(new TextSearchMatch(match.Length, lineStart + (match.Column - 1), localLine));
            if (i == CurrentMatchIndex) viewIndex = subset.Count - 1;
        }

        ReplaceSearchMatchesIncremental(subset);

        MatchCount = _windowedMatches.Count;
        CurrentMatchViewIndex = viewIndex;
    }

    /// <summary>E10: <see cref="SearchMatches"/> 的增量替换(段集合差分语义)。目标子集中与当前
    /// 集合同位置且值相等的匹配复用既有实例——细化/重扫后未变的段保持引用稳定,视图按引用
    /// 差量即可只换变化的段;过期的移除、新增的追加。内容相同则零通知;有变化时一次合并
    /// Reset,视图侧保持单次重挂(避免逐条通知触发多次全量重应用)。</summary>
    private void ReplaceSearchMatchesIncremental(IReadOnlyList<TextSearchMatch> incoming)
    {
        var current = SearchMatches;
        if (current.Count == 0)
        {
            if (incoming.Count > 0)
            {
                current.AddRange(incoming);
            }
            return;
        }

        var unchanged = incoming.Count == current.Count;
        if (unchanged)
        {
            for (var i = 0; i < current.Count; i++)
            {
                if (current[i] != incoming[i])
                {
                    unchanged = false;
                    break;
                }
            }
        }

        if (unchanged)
        {
            return;
        }

        var currentByPosition = new Dictionary<(int Line, int Offset), int>(current.Count);
        for (var i = 0; i < current.Count; i++)
        {
            currentByPosition[(current[i].Line, current[i].Offset)] = i;
        }

        var kept = new bool[current.Count];
        var resolved = new List<TextSearchMatch>(incoming.Count);
        foreach (var match in incoming)
        {
            if (currentByPosition.TryGetValue((match.Line, match.Offset), out var index)
                && !kept[index]
                && current[index] == match)
            {
                kept[index] = true;
                resolved.Add(current[index]);
            }
            else
            {
                resolved.Add(match);
            }
        }

        current.ReplaceRange(resolved);
    }

    private bool IsLineInCurrentWindow(int globalLine) =>
        _documentSource is not null && globalLine >= WindowStartLine && globalLine < WindowStartLine + _windowTextLines;

    [RelayCommand]
    private void OpenFindBar() => IsFindBarOpen = true;

    [RelayCommand]
    private void ClearSearch()
    {
        _searchTextDebounce.Cancel(); // E10: Escape 立即清空,挂起的去抖扫描不得再跑
        SearchText = string.Empty;
        IsFindBarOpen = false;
        SearchMatches.ReplaceRange([]);
        MatchCount = 0;
        CurrentMatchIndex = 0;
        SearchErrorMessage = string.Empty;
        CurrentMatchViewIndex = -1;
        _windowedMatches.Clear();
        CancelWindowedSearch(); // 中止进行中的窗口化搜索 I/O
    }

    /// <summary>全部高亮开关(VS Code toggle find highlight):关闭时视图隐藏文档内匹配装饰,
    /// 导航计数器与跳转仍可用。</summary>
    [RelayCommand]
    private void ToggleHighlightAll() => ShowAllHighlights = !ShowAllHighlights;

    [RelayCommand(CanExecute = nameof(CanStepMatch))]
    private void FindNext() => StepMatch(+1);

    [RelayCommand(CanExecute = nameof(CanStepMatch))]
    private void FindPrevious() => StepMatch(-1);

    private bool CanStepMatch() => MatchCount > 0;

    private void StepMatch(int offset)
    {
        if (MatchCount == 0)
        {
            return;
        }

        var next = (CurrentMatchIndex + offset + MatchCount) % MatchCount;
        CurrentMatchIndex = next;

        if (_documentSource is not null && _windowedMatches.Count > 0)
        {
            // 窗口化模式:目标匹配落在当前窗口外时先加载其所在窗口;
            // LoadWindowAsync 载入新窗口后会重建窗口内匹配子集并落位。
            var target = _windowedMatches[next];
            if (IsLineInCurrentWindow(target.Line))
            {
                ApplyWindowedMatches();
            }
            else
            {
                CurrentMatchViewIndex = -1;
                _ = LoadWindowAsync(target.Line);
            }
        }
        else
        {
            CurrentMatchViewIndex = next;
        }
    }

    [RelayCommand]
    private void ToggleWordWrap() => WordWrap = !WordWrap;

    [RelayCommand]
    private void ToggleShowMinimap() => ShowMinimap = !ShowMinimap;

    /// <summary>Frees the decoded text, search matches, outline and the Markdown parse artifacts
    /// (Markdig AST / render result / heading list) so a closed tab stops holding the managed heap.
    /// 阅读状态(锚点/行号/偏移)在 <c>MarkTabClosed</c> 中先于本方法持久化。</summary>
    public override void ReleaseResources()
    {
        if (_resourcesReleased)
        {
            return;
        }

        _resourcesReleased = true;
        ThemeEvents.ThemeChanged -= OnAppThemeChanged;
        // 使任何在途的加载/重载管线立刻失效(它们检查 version != _contentVersion 即放弃发布),
        // 关闭后的标签不得再被外部变更或重试写回内容。
        _contentVersion++;
        _presentationVersion++;
        _derivedVersion++;
        _markdownVersion++;
        _foldsVersion++;
        _searchVersion++;
        CancelPendingWindowLoad();
        CancelPendingReloadRetry();
        FileChangeNotice = string.Empty;
        // 渲染产物整体释放:绑定驱动视图清空 FlowDocument 并逐处释放图片引用;
        // Markdig AST 随结果对象释放(若已分离则为空)。
        MarkdownRenderResult = null;
        MarkdownHeadings = [];
        MarkdownAnchor = string.Empty;
        MarkdownHeadingLine = 0;
        MarkdownVerticalOffset = 0;
        MarkdownRenderNotice = string.Empty;

        _searchTextDebounce.Dispose(); // E10: 关闭标签不得再触发挂起的查找扫描
        CancelAndDispose(ref _presentationCancellation);
        CancelAndDispose(ref _derivedCancellation);
        CancelAndDispose(ref _markdownCancellation);
        if (_documentSource is not null)
        {
            var stale = _documentSource;
            _documentSource = null;
            _ = DisposeDocumentSourceAsync(stale);
        }
        Content = string.Empty;
        Presentation = CodePresentationSnapshot.Empty();
        Notice = string.Empty;
        SearchMatches.ReplaceRange([]);
        _windowedMatches.Clear();
        _windowTextLines = 0;
        _windowLineOffsets = [0];
        _searchCancellation?.Cancel();
        _searchCancellation = null;
        FilteredOutlineEntries.ReplaceRange([]);
        OutlineEntries.ReplaceRange([]);
        FoldSections = [];
        SymbolRoots.ReplaceRange([]);
        FilteredSymbolRoots.ReplaceRange([]);
        UnwireSymbolNodes();
        _symbolDocument = CodeSymbolDocument.Empty;
        _collapsedSymbolIds.Clear();
        ViewState = null;
        _payloadUnloaded = true;
        _loadTask = null;
    }

    // ===== Go to line / column =====

    [RelayCommand]
    private void OpenGoToLineBar()
    {
        GoToLineInput = string.Empty;
        IsGoToLineBarOpen = true;
    }

    /// <summary>Parses "line" or "line:column", clamps the line to the document and raises the jump.</summary>
    [RelayCommand]
    private void GoToLine()
    {
        IsGoToLineBarOpen = false;

        var input = GoToLineInput?.Trim() ?? string.Empty;
        if (input.Length == 0)
        {
            return;
        }

        var parts = input.Split(':', 2);
        if (!int.TryParse(parts[0], out var line) || line < 1)
        {
            return;
        }

        // Search results use the same compact form as the go-to-line bar (line:column).
        // Keep line-only input backward compatible, but preserve a valid column so the
        // view can place the caret at the actual match instead of only selecting the line.
        int? column = null;
        if (parts.Length == 2 && int.TryParse(parts[1], out var parsedColumn) && parsedColumn >= 1)
        {
            column = parsedColumn;
        }

        var effectiveLine = Math.Min(line, Math.Max(LineCount, 1));
        if (_documentSource is null)
        {
            if (column is int effectiveColumn)
            {
                GoToPositionRequested?.Invoke(this, (effectiveLine, effectiveColumn));
            }
            else
            {
                GoToLineRequested?.Invoke(this, effectiveLine);
            }
        }
        else
        {
            _ = NavigateWindowedAsync(effectiveLine, column);
        }
    }

    private async Task NavigateWindowedAsync(int globalLine, int? column = null)
    {
        if (CapacityTier == ReadOnlyContentTier.Summary) CapacityTier = ReadOnlyContentTier.Windowed;
        if (!await LoadWindowAsync(globalLine))
        {
            return;
        }

        Notice = $"窗口分析 · 全局第 {WindowStartLine}–{Math.Min(LineCount, WindowStartLine + FileReadOnlyDocumentSource.DefaultWindowLines - 1)} 行";
        var localLine = Math.Clamp(globalLine - WindowStartLine + 1, 1, Math.Max(1, Content.Count(character => character == '\n') + 1));
        if (column is int effectiveColumn)
        {
            GoToPositionRequested?.Invoke(this, (localLine, effectiveColumn));
        }
        else
        {
            GoToLineRequested?.Invoke(this, localLine);
        }
    }

    public async Task<bool> LoadAdjacentWindowAsync(bool next)
    {
        if (_documentSource is null || CapacityTier == ReadOnlyContentTier.Full) return false;
        var target = next
            ? WindowStartLine + FileReadOnlyDocumentSource.DefaultWindowLines
            : Math.Max(1, WindowStartLine - FileReadOnlyDocumentSource.DefaultWindowLines);
        if (target == WindowStartLine || target > LineCount) return false;
        if (!await LoadWindowAsync(target))
        {
            return false;
        }

        Notice = $"窗口分析 · 全局第 {WindowStartLine}–{Math.Min(LineCount, WindowStartLine + FileReadOnlyDocumentSource.DefaultWindowLines - 1)} 行";
        return true;
    }
}

/// <summary>A diff tab in the shared editor group. Loads its content lazily from git and exposes
/// safe hunk-level source-control operations for working-tree and index diffs.</summary>
public sealed partial class DiffTab : EditorTabItem
{
    private const int MaxDiffLines = ReadOnlyContentCapacity.FullDiffLines;

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

    [ObservableProperty]
    private bool isHunkOperationBusy;

    /// <summary>Applies one hunk through Git, reloads this Diff tab and notifies the SCM page.</summary>
    public async Task<bool> ApplyHunkAsync(int hunkIndex, GitHunkOperation operation, CancellationToken cancellationToken = default)
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
            if (operation == GitHunkOperation.Restore && !_restoreConfirmedForSession)
            {
                var confirmed = _confirmationService?.Confirm(
                    "确认还原 Diff 块",
                    $"将还原“{Path}”的第 {hunkIndex + 1} 个 Diff 块，且无法撤销。选择“否”可安全取消。") == true;
                if (!confirmed)
                {
                    _logService.Write("INFO", $"用户取消了还原 Diff 块：{Path} #{hunkIndex + 1}。");
                    return false;
                }

                _restoreConfirmedForSession = true;
            }

            await _gitService.ApplyHunkAsync(_request.RepositoryPath, Path, DisplayIsStaged, hunk, operation, operationToken).ConfigureAwait(true);
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
                    _gitService.StreamCommitFileDiffAsync(_request.RepositoryPath, _request.CommitHash, Path, ReadOnlyContentCapacity.WindowedDiffLines, IgnoreTrimWhitespace, _lifetimeCancellation.Token),
                    _lifetimeCancellation.Token);
                var metadata = await _diffContentSource.GetMetadataAsync();
                CapacityTier = metadata.CapacityTier;
                diff = CollectWindow((await _diffContentSource.ReadWindowAsync(new DocumentRange(1, MaxDiffLines))).Events, Path, false);
                if (metadata.TotalLines > MaxDiffLines) DiffNotice = $"大型提交 diff，当前窗口显示前 {MaxDiffLines:N0} 行。";
            }
            else
            {
                _diffContentSource = await SpoolingDiffContentSource.CreateAsync(
                    _gitService.StreamDiffAsync(_request.RepositoryPath, Path, _request.IsStaged, _request.IsUntracked, ReadOnlyContentCapacity.WindowedDiffLines, IgnoreTrimWhitespace, _lifetimeCancellation.Token),
                    _lifetimeCancellation.Token);
                var metadata = await _diffContentSource.GetMetadataAsync();
                CapacityTier = metadata.CapacityTier;
                var window = await _diffContentSource.ReadWindowAsync(new DocumentRange(1, MaxDiffLines));
                diff = CollectWindow(window.Events, Path, _request.IsStaged);
                if (metadata.TotalLines > MaxDiffLines)
                {
                    DiffNotice = CapacityTier == ReadOnlyContentTier.Summary
                        ? $"diff 超过 {ReadOnlyContentCapacity.WindowedDiffLines:N0} 行，已保留 hunk 索引并显示首个阅读窗口。"
                        : $"大型 diff，当前窗口显示前 {MaxDiffLines:N0} 行。";
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
                    _gitService.StreamDiffAsync(_request.RepositoryPath, Path, otherStaged, false, ReadOnlyContentCapacity.WindowedDiffLines, IgnoreTrimWhitespace, _lifetimeCancellation.Token),
                    _lifetimeCancellation.Token);
                var otherDiff = CollectWindow((await otherSource.ReadWindowAsync(new DocumentRange(1, MaxDiffLines))).Events, Path, otherStaged);
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
                ? BuildDiffSnapshot(diff)
                : await Task.Run(() => BuildDiffSnapshot(diff), _lifetimeCancellation.Token).ConfigureAwait(false);

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
    private static DiffBuildResult BuildDiffSnapshot(GitFileDiff diff)
    {
        // 折叠“仅行尾换行符状态变化”的 删/增 对(文字完全相同),避免末尾出现
        // 实际没有内容更改却标红的假变更块;通知行保留,原因仍可见。
        diff = DiffLineEndingAdjuster.Adjust(diff);

        if (diff.IsBinary)
        {
            return new DiffBuildResult([], [], [], ReadOnlyContentTier.Full, 0, 0, 0, "二进制文件，无法显示 diff。", Binary: true);
        }

        var sourceLineCount = diff.Hunks.Sum(hunk => hunk.Lines.Count);
        var capacityTier = ReadOnlyContentCapacity.ForDiff(sourceLineCount);

        // 限量填充 + 单遍统计(替代原先 Added/Removed/ChangeCount 的三次全量扫描)。
        // 切块规则与 DiffDocumentBuilders.CountBlocks 完全一致:提示行不切断变更块。
        var lines = new List<GitDiffLine>(Math.Min(sourceLineCount, MaxDiffLines));
        var addedCount = 0;
        var removedCount = 0;
        var changeCount = 0;
        var inChangeBlock = false;
        var capped = false;
        foreach (var hunk in diff.Hunks)
        {
            foreach (var line in hunk.Lines)
            {
                if (lines.Count >= MaxDiffLines)
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

        var rows = new List<GitSideBySideRow>(Math.Min(sourceLineCount, MaxDiffLines));
        foreach (var row in diff.ToSideBySideRows())
        {
            if (rows.Count >= MaxDiffLines)
            {
                break;
            }

            rows.Add(row);
        }

        string? notice = null;
        if (capped)
        {
            notice = capacityTier == ReadOnlyContentTier.Summary
                ? $"diff 过大，已进入摘要模式并仅显示前 {MaxDiffLines} 行。"
                : $"大型 diff，已加载前 {MaxDiffLines} 行阅读窗口。";
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
