using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nornia.Core.Collections;
using Nornia.Core.Models;
using Nornia.Desktop.Services;
using Nornia.Desktop.Configuration;
using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using System.Windows;

namespace Nornia.Desktop.ViewModels;

/// <summary>Lazy read-only project explorer. Directories are enumerated only on expansion and files
/// open in the shared editor area (VS Code-style), so opening a large repository never fills the
/// managed heap. Also carries VS Code-style git decorations (M/A/D/? letters + changed-folder dots)
/// synced from the source-control view.</summary>
public partial class WorkspaceViewModel : PageViewModel
{
    private readonly IFolderPickerService _folderPicker;
    private readonly IClipboardService _clipboard;
    private readonly ISettingsService _scopedSettings;
    private readonly IProjectWorkspaceService _workspaceService;
    private readonly IWorkspaceFileWatcher _fileWatcher;
    // View models belong to the context on which they are composed. Application.Current can
    // point at an unrelated, idle WPF dispatcher in tests or secondary hosts.
    private readonly SynchronizationContext? _uiContext;
    private ISettingsSession? _settingsSession;
    private readonly WorkspaceStatusSource _statusSource = new();
    private bool _suppressSelectionOpen;
    private bool _suppressTreeSelectionOpen;
    private bool _compactFoldersEnabled;
    private readonly RevisionGate _settingsRevisionGate = new();

    // ===== 树自动刷新(文件增删改的结构性监听,VS Code explorer 语义)=====
    // 与 GitViewModel 的静默刷新同族:至多一次在途 + 至多一批合并的 pending + 最小间隔冷却。
    // 监视器侧已做 200ms 尾部去抖;这里的 500ms 冷却保证构建期的高频事件每秒至多触发两轮
    // 目录重枚举,而普通单次变更的树更新延迟 < ~1s。
    private int _treeRefreshInFlight; // 0 = idle, 1 = a tree refresh is in flight
    private int _treeRefreshPending;
    private readonly object _pendingTreeGate = new();
    private readonly HashSet<string> _pendingTreePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _treeMutationGate = new(1, 1);
    internal const long TreeRefreshMinimumIntervalMs = 500;
    private long _lastTreeRefreshCompletedTicks; // Environment.TickCount64 value, monotonic
    private int _treeRefreshRetryScheduled; // at most one pending retry timer

    /// <summary>行对象复用池:每节点至多一个存活行(身份即节点引用,对照 VS Code RowCache 的
    /// templateId 复用)。增量投影只发受影响区间的 CollectionChanged,未变行不重建容器;
    /// 同时保证节点上的 PropertyChanged 订阅始终只有一份(旧整表重建会累积死行)。</summary>
    private readonly Dictionary<WorkspaceNode, WorkspaceTreeRow> _rowCache = new(ReferenceEqualityComparer.Instance);

    /// <summary>V7 防抖:单击选中即开文件在键盘/鼠标快速遍历下会每次跳行都新建一个预览标签
    /// (反闲聊)。选中→打开用 100ms 尾部去抖(与搜索进度合并同族的 RunOnceScheduler):
    /// 只有静默期后仍为最新的选中文件会被打开;双击常驻打开与 RevealNodeAsync
    /// (reveal 走 _suppressSelectionOpen,不经过此路径)保持即时。</summary>
    private readonly Nornia.Core.Coalescing.RunOnceScheduler _selectionOpenScheduler = new(100);
    private readonly object _pendingSelectionOpenGate = new();
    private WorkspaceFileRow? _pendingSelectionOpenRow;
    private long _workspaceGeneration;
    private long _pendingSelectionOpenGeneration;

    /// <summary>当前紧凑文件夹投影开关(设置驱动;内部供测试断言实际接线)。</summary>
    internal bool CompactFoldersEnabled => _compactFoldersEnabled;

    public ObservableCollection<WorkspaceNode> RootNodes { get; } = [];
    /// <summary>Flattened, virtualizable projection of the currently visible workspace nodes.
    /// WorkspaceNode remains the disk-backed source of truth; rows only describe layout.</summary>
    public ObservableCollection<WorkspaceTreeRow> WorkspaceTreeRows { get; } = [];

    /// <summary>Raised when a workspace root changes (the project workbench syncs terminal + git).</summary>
    public event EventHandler<string>? WorkspaceOpened;

    /// <summary>Raised after the watcher identifies a visible workspace change. Consumers such as
    /// quick open can invalidate their derived file index without subscribing to the native watcher
    /// directly.</summary>
    public event EventHandler? WorkspaceFilesChanged;

    /// <summary>Raised by the folder picker. The page-level workbench turns this into a shared
    /// project-context activation instead of letting the tree own a separate workspace.</summary>
    public event EventHandler<string>? WorkspaceSelectionRequested;

    /// <summary>Raised when a file is opened from the tree; the workbench forwards it to the shared editor.</summary>
    public event EventHandler<string>? FileOpenRequested;

    /// <summary>Raised when a file row is double-clicked (VS Code: 单击开预览,双击开常驻);
    /// the workbench opens it as a regular tab in the shared editor.</summary>
    public event EventHandler<string>? FileOpenPermanentRequested;

    /// <summary>Raised when "查看更改" is clicked on a changed file; the workbench opens its diff.</summary>
    public event EventHandler<WorkspaceNode>? DiffOpenRequested;

    /// <summary>行投影发生增量变更后触发(展开/折叠/刷新/reveal);视图据此保持滚动锚点
    /// (可视首行),避免整表 Reset 造成的滚动位置丢失与闪烁。</summary>
    public event EventHandler? TreeRowsChanged;

    /// <summary>reveal 定位完成后触发;视图把目标行滚入视野(VS Code 资源管理器 reveal 行为)。</summary>
    public event Action<WorkspaceTreeRow>? RevealRowRequested;

    [ObservableProperty]
    private string workspacePath = string.Empty;

    [ObservableProperty]
    private WorkspaceTreeRow? selectedTreeRow;

    [ObservableProperty]
    private bool areTreeGuidesVisible;

    public WorkspaceViewModel(IFolderPickerService folderPicker, ISettingsService settings,
        IProjectWorkspaceService workspaceService, IUiLogService logService, IClipboardService clipboard,
        IWorkspaceFileWatcher? fileWatcher = null) : base("资源管理器", logService)
    {
        _folderPicker = folderPicker;
        _scopedSettings = settings;
        _workspaceService = workspaceService;
        _clipboard = clipboard;
        _fileWatcher = fileWatcher ?? NullWorkspaceFileWatcher.Instance;
        _uiContext = SynchronizationContext.Current is System.Windows.Threading.DispatcherSynchronizationContext
            ? SynchronizationContext.Current
            : null;
        _fileWatcher.FilesChanged += OnWorkspaceFilesChanged;
        _workspaceService.ContextChanged += OnWorkspaceSettingsChangedAsync;
        _selectionOpenScheduler.Action = RunPendingSelectionOpen;
        _selectionOpenScheduler.OnError = ex => LogService.Write("ERROR", $"树选中打开文件失败：{ex.Message}");
    }

    protected override async Task OnFirstActivatedAsync()
    {
        await BindSettingsAsync();
    }

    private async Task BindSettingsAsync()
    {
        var generation = Volatile.Read(ref _workspaceGeneration);
        var context = _workspaceService.Current;
        var session = await _scopedSettings.OpenSessionAsync(new(context?.ProjectPath),
            [BuiltInSettingsCatalog.ExplorerTreeGuides.Id, BuiltInSettingsCatalog.FilesExclude.Id,
                BuiltInSettingsCatalog.ExplorerCompactFolders.Id]);
        if (generation != Volatile.Read(ref _workspaceGeneration)
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
            LogService.Write("WARNING", $"资源管理器设置应用失败：{ex.Message}");
        }
    }

    private void ApplyScopedSettings(SettingsSnapshot snapshot)
    {
        if (!IsSettingsContextCurrent(snapshot.Context)) return;
        if (!_settingsRevisionGate.TryAccept(snapshot)) return;
        void Apply()
        {
            if (!IsSettingsContextCurrent(snapshot.Context)) return;
            AreTreeGuidesVisible = snapshot.Effective(BuiltInSettingsCatalog.ExplorerTreeGuides);
            var compactChanged = _compactFoldersEnabled != snapshot.Effective(BuiltInSettingsCatalog.ExplorerCompactFolders);
            _compactFoldersEnabled = snapshot.Effective(BuiltInSettingsCatalog.ExplorerCompactFolders);
            _statusSource.SetExcludes(snapshot.Effective(BuiltInSettingsCatalog.FilesExclude));
            // 重载树:排除规则作用于磁盘枚举;紧凑文件夹投影在重建行时读取 _compactFoldersEnabled。
            _ = RefreshAsync();
            if (compactChanged)
            {
                // 紧凑模式翻转:既有节点(刷新前后缀保活的)的计数缓存必须同步换规则,
                // 否则计数(链合并)与渲染(普通展开)不一致。
                foreach (var root in RootNodes)
                {
                    SetCompactFoldersRecursive(root, _compactFoldersEnabled);
                }

                SyncTreeRows();
            }
        }
        if (_uiContext is null || ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            Apply();
        }
        else
        {
            _uiContext.Post(_ => Apply(), null);
        }
    }

    private async Task OnWorkspaceSettingsChangedAsync(ProjectWorkspaceContext? context)
    {
        var generation = Interlocked.Increment(ref _workspaceGeneration);
        CancelPendingSelectionOpen();
        lock (_pendingTreeGate)
        {
            _pendingTreePaths.Clear();
        }

        Interlocked.Exchange(ref _treeRefreshPending, 0);
        await _treeMutationGate.WaitAsync();
        try
        {
            if (generation == Volatile.Read(ref _workspaceGeneration))
            {
                ClearTreeProjection(clearWorkspacePath: context is null);
            }
        }
        finally
        {
            _treeMutationGate.Release();
        }

        await BindSettingsAsync();
    }

    private bool IsSettingsContextCurrent(SettingsContext context)
    {
        var current = _workspaceService.Current;
        return string.Equals(context.NormalizedWorkspacePath,
            current?.ProjectPath is { } path ? NormalizePath(path) : null,
            StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCurrentGeneration(long generation) => generation == Volatile.Read(ref _workspaceGeneration);

    private bool IsCurrentTreeNode(WorkspaceNode node)
    {
        var root = node;
        while (root.Parent is not null)
        {
            root = root.Parent;
        }

        return RootNodes.Any(candidate => ReferenceEquals(candidate, root));
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void ClearTreeProjection(bool clearWorkspacePath)
    {
        CancelPendingSelectionOpen();
        lock (_pendingTreeGate)
        {
            _pendingTreePaths.Clear();
        }

        foreach (var root in RootNodes.ToArray())
        {
            DropTreeRecursive(root);
        }

        foreach (var row in _rowCache.Values.ToArray())
        {
            row.Detach();
        }

        _rowCache.Clear();
        _suppressTreeSelectionOpen = true;
        try
        {
            SelectedTreeRow = null;
            WorkspaceTreeRows.Clear();
        }
        finally
        {
            _suppressTreeSelectionOpen = false;
        }

        RootNodes.Clear();
        _statusSource.Clear();
        _lastStatusMap = null;
        _fileWatcher.UpdateDirectories([]);
        if (clearWorkspacePath)
        {
            WorkspacePath = string.Empty;
        }
    }

    [RelayCommand]
    private async Task ChooseWorkspaceAsync()
    {
        var selected = _folderPicker.PickFolder(WorkspacePath);
        if (selected is not null)
        {
            WorkspaceSelectionRequested?.Invoke(this, selected);
        }
    }

    public async Task OpenWorkspaceAsync(string path)
    {
        var generation = Interlocked.Increment(ref _workspaceGeneration);
        CancelPendingSelectionOpen();
        await _treeMutationGate.WaitAsync();
        try
        {
            if (generation != Volatile.Read(ref _workspaceGeneration)) return;

            WorkspacePath = path;
            ClearTreeProjection(clearWorkspacePath: false);
            var root = new WorkspaceNode(path, true, path, _statusSource);
            root.CompactFolders = _compactFoldersEnabled;
            RootNodes.Add(root);
            // V4 诊断计数器随工作区重置。
            IncrementalSpliceCount = 0;
            FullSyncCount = 0;
            // VS Code opens a workspace with the root folder expanded so the tree is visible at once.
            root.IsExpanded = true;
            // Attach before the first enumeration: otherwise a file created between enumeration and
            // Attach remains absent until a manual refresh.
            _fileWatcher.Attach(path);
            await root.LoadChildrenAsync();
            if (generation != Volatile.Read(ref _workspaceGeneration)
                || RootNodes.Count == 0 || !ReferenceEquals(RootNodes[0], root)) return;

            SyncTreeRows();
            RefreshWatchedDirectories();
            WorkspaceOpened?.Invoke(this, path);
        }
        finally
        {
            _treeMutationGate.Release();
        }
    }

    /// <summary>Selection-driven open: folders are expanded/collapsed by the row click or the chevron
    /// (see <c>WorkspaceView</c>), never by selection itself; files open a preview tab in the shared
    /// editor area, exactly like a VS Code explorer single click.</summary>
    [RelayCommand]
    private Task OpenNodeAsync(WorkspaceNode? node)
    {
        if (node is null || !IsCurrentTreeNode(node) || _suppressSelectionOpen || node.IsDirectory)
        {
            return Task.CompletedTask;
        }

        FileOpenRequested?.Invoke(this, node.Path);
        return Task.CompletedTask;
    }

    /// <summary>Expands or collapses one flattened folder row. Loading stays lazy: an expanded
    /// directory is enumerated only once, then rebuilding only changes the visible row projection.
    /// V4 快路径:展开/折叠只拼接受影响子树的区间(O(Δ),行对象按节点复用),不再整表重投影;
    /// 缓存计数/行序不可用时(紧凑链形态变化、行不在投影中)回退全量 <see cref="SyncTreeRows"/>。
    /// 必须允许并发执行(AllowConcurrentExecutions):展开路径要 await 磁盘枚举(大目录可达
    /// 数百毫秒~秒级)。AsyncRelayCommand 默认在运行中自禁用(CanExecute=false),用户在这段
    /// 窗口内补点会被静默吞掉,表现为"点两次才生效";允许并发后补点按正常切换语义处理
    /// (后到的折叠会令在途展开落空:delta 按实时 IsExpanded 计算为 0,不会投影出幽灵子行)。</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleTreeFolderAsync(WorkspaceFolderRow? row)
    {
        if (row is null) return;

        var node = row.Node;
        var generation = Volatile.Read(ref _workspaceGeneration);
        if (!IsCurrentTreeNode(node)) return;

        // 防御兜底:"chevron 显示已展开但子树未加载"的不一致状态(IsExpanded=true 且只剩占位符)——
        // 旧刷新行为的遗留(现由 DropChildren 同步重置),或未来某条 drop 路径忘记重置展开态。
        // 此状态下按正常逻辑翻转只会切换空展开(delta=0,无可见变化),用户需点两次才生效;而用户
        // 心智模型是"该目录未展开"(chevron 朝下但无子行),故本次点击直接加载并重投影,一次生效。
        // 正常流程中 LoadChildrenAsync 会先登记进行中的任务再枚举;在途展开不会命中本分支。
        if (node.IsExpanded && !node.IsLoaded)
        {
            await node.LoadChildrenAsync();
            if (!IsCurrentGeneration(generation) || !IsCurrentTreeNode(node)) return;
            SyncTreeRows();
            RefreshWatchedDirectories();
            return;
        }

        var previousCount = node.VisibleRowCount;
        node.IsExpanded = !node.IsExpanded;
        if (node.IsExpanded)
        {
            if (node.IsLoaded)
            {
                // Collapsed directories are intentionally unwatched; refresh their immediate
                // children on re-open so stale cached entries never survive the collapsed period.
                await node.ReconcileChildrenAsync();
            }
            else
            {
                await node.LoadChildrenAsync();
            }
        }

        if (!IsCurrentGeneration(generation) || !IsCurrentTreeNode(node)) return;

        RefreshWatchedDirectories();

        var delta = node.VisibleRowCount - previousCount;
        var start = -1;
        var fastPath = node.Parent is not null
            && node.SingleDirectoryChild is null // 该行仍代表节点自身(加载后若新增单目录子节点,紧凑链
            // 的合并行改属子节点 → 行身份变化,必须全量重建)
            && TryGetRowSpan(node, out start, out _);

        if (delta == 0)
        {
            if (fastPath)
            {
                // 行数不变(如空目录切换):行序列不动,保持"每次切换通知一次"的旧契约
                // (视图据此维持滚动锚点)。
                TreeRowsChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                SyncTreeRows();
            }
            return;
        }

        if (fastPath && delta > 0)
        {
            IncrementalSpliceCount++;
            var inserted = new List<WorkspaceTreeRow>(delta);
            foreach (var child in node.Children)
            {
                if (!child.IsPlaceholder)
                {
                    CollectVisibleRows(child, row.Depth + 1, inserted);
                }
            }

            if (inserted.Count == delta)
            {
                for (var i = 0; i < delta; i++)
                {
                    WorkspaceTreeRows.Insert(start + 1 + i, inserted[i]);
                }

                TreeRowsChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
        }
        else if (fastPath && delta < 0)
        {
            IncrementalSpliceCount++;
            var removeCount = -delta;
            if (start + 1 + removeCount <= WorkspaceTreeRows.Count)
            {
                // 自尾向头移除(与 CollectionDiffer.Apply 同序):下标不漂移;被移除行解除订阅
                // 并移出复用池(与全量同步的行生命周期一致:折叠子树的行不保留)。
                for (var i = start + 1 + removeCount - 1; i >= start + 1; i--)
                {
                    var removed = WorkspaceTreeRows[i];
                    WorkspaceTreeRows.RemoveAt(i);
                    if (_rowCache.Remove(removed.Node, out var cached) && ReferenceEquals(cached, removed))
                    {
                        removed.Detach();
                    }
                }

                TreeRowsChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        SyncTreeRows();
    }

    /// <summary>V4:取节点在当前扁平投影中的行区间 [start, start+count):start 为节点自身行的
    /// 扁平下标(紧凑链中为链顶合并行的下标——那正是链末端节点的行),count 取节点子树可见行数
    /// 缓存。start 由父链 + 每节点 O(1) 计数缓存惰性导出(O(深度×兄弟数)),每次拼接后无需全表
    /// 重新打戳;节点在投影中无行(紧凑链中间成员/占位符)时返回 false。</summary>
    internal bool TryGetRowSpan(WorkspaceNode node, out int start, out int count)
    {
        start = 0;
        count = 0;
        if (node.IsPlaceholder)
        {
            return false;
        }

        var top = node;
        while (top.Parent is not null)
        {
            top = top.Parent;
        }

        var index = 0;
        foreach (var root in RootNodes)
        {
            if (ReferenceEquals(root, top))
            {
                break;
            }

            index += root.VisibleRowCount;
        }

        var current = top;
        var rowPos = index;
        while (!ReferenceEquals(current, node))
        {
            var child = FindChildAncestorOf(current, node);
            if (child is null)
            {
                return false;
            }

            var chainPassesThrough = _compactFoldersEnabled
                && current.SingleDirectoryChild is not null
                && ReferenceEquals(current.SingleDirectoryChild, child);

            if (ReferenceEquals(child, node))
            {
                if (chainPassesThrough)
                {
                    // 节点在 current 的紧凑链内:合并行位于链顶位置。链末端成员的行正是该合并行;
                    // 中间成员没有自己的行。
                    if (node.SingleDirectoryChild is not null)
                    {
                        return false;
                    }

                    start = rowPos;
                    count = node.VisibleRowCount;
                    return true;
                }

                // 非紧凑链分支:节点自身行只有在 current 展开时才投影。
                if (current.IsDirectory && !current.IsExpanded)
                {
                    return false;
                }

                start = rowPos + 1 + SiblingRowsBefore(current, child);
                count = child.VisibleRowCount;
                return true;
            }

            if (!chainPassesThrough)
            {
                // 非紧凑链:child 子树只有在 current 展开时才投影(折叠分支无行)。
                if (current.IsDirectory && !current.IsExpanded)
                {
                    return false;
                }

                // child 渲染自身行:current 自身行 + 其前序兄弟行都在 child 之前。
                rowPos = rowPos + 1 + SiblingRowsBefore(current, child);
            }
            // 紧凑链情形:child 子树的行从同一位置开始(合并行共享,只计一次)。
            current = child;
        }

        start = rowPos;
        count = node.VisibleRowCount;
        return true;
    }

    private static WorkspaceNode? FindChildAncestorOf(WorkspaceNode parent, WorkspaceNode node)
    {
        var candidate = node;
        while (candidate.Parent is not null && !ReferenceEquals(candidate.Parent, parent))
        {
            candidate = candidate.Parent;
        }

        return ReferenceEquals(candidate.Parent, parent) ? candidate : null;
    }

    private static int SiblingRowsBefore(WorkspaceNode parent, WorkspaceNode node)
    {
        var sum = 0;
        foreach (var child in parent.Children)
        {
            if (ReferenceEquals(child, node))
            {
                break;
            }

            if (!child.IsPlaceholder)
            {
                sum += child.VisibleRowCount;
            }
        }

        return sum;
    }

    [RelayCommand]
    private Task OpenTreeFileAsync(WorkspaceFileRow? row) => OpenNodeAsync(row?.Node);

    /// <summary>双击文件行:打开常驻标签(单击开预览,见 <see cref="OpenNodeAsync"/>;已打开的
    /// 预览由 EditorAreaViewModel 就地转正)。</summary>
    [RelayCommand]
    private Task OpenTreeFilePermanentAsync(WorkspaceFileRow? row)
    {
        var node = row?.Node;
        if (node is null || !IsCurrentTreeNode(node) || node.IsDirectory)
        {
            return Task.CompletedTask;
        }

        FileOpenPermanentRequested?.Invoke(this, node.Path);
        return Task.CompletedTask;
    }

    /// <summary>"查看更改" on a decorated changed file: hands the node to the workbench, which builds
    /// a diff request from the current status map and opens it in the shared editor.</summary>
    [RelayCommand(CanExecute = nameof(CanRequestDiff))]
    private void RequestDiff(WorkspaceNode? node)
    {
        if (node is null || !IsCurrentTreeNode(node) || node.IsDirectory || _statusSource.Lookup(node.RelativePath) is null)
        {
            return;
        }

        DiffOpenRequested?.Invoke(this, node);
    }

    private bool CanRequestDiff(WorkspaceNode? node) =>
        node is not null && !node.IsDirectory && _statusSource.Lookup(node.RelativePath) is not null;

    /// <summary>Copies the node's absolute path to the clipboard.</summary>
    [RelayCommand]
    private void CopyPath(WorkspaceNode? node)
    {
        if (node is not null)
        {
            _clipboard.SetText(node.Path);
        }
    }

    /// <summary>Copies the repository-relative path (VS Code "Copy Relative Path").</summary>
    [RelayCommand]
    private void CopyRelativePath(WorkspaceNode? node)
    {
        if (node is not null)
        {
            _clipboard.SetText(node.RelativePath);
        }
    }

    /// <summary>Opens the node in the system file explorer with the item selected. Best effort:
    /// shell launches fail silently on unusual environments.</summary>
    [RelayCommand]
    private void RevealInSystemExplorer(WorkspaceNode? node)
    {
        if (node is null || (!File.Exists(node.Path) && !Directory.Exists(node.Path)))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{node.Path}\"") { UseShellExecute = false });
        }
        catch (Exception)
        {
            // Best-effort only (e.g. stripped shell environments).
        }
    }

    [RelayCommand]
    private async Task ExpandAllAsync(WorkspaceNode? node)
    {
        var generation = Volatile.Read(ref _workspaceGeneration);
        if (node is not null && IsCurrentTreeNode(node))
        {
            await ExpandRecursiveAsync(node);
            if (!IsCurrentGeneration(generation) || !IsCurrentTreeNode(node)) return;
            SyncTreeRows();
            RefreshWatchedDirectories();
        }
    }

    [RelayCommand]
    private void CollapseAll(WorkspaceNode? node)
    {
        if (node is not null && IsCurrentTreeNode(node))
        {
            CollapseRecursive(node);
            SyncTreeRows();
        }
    }

    /// <summary>Reloads the tree from disk (VS Code Explorer refresh). Expansion state resets for
    /// the refreshed subtree; git decorations re-apply from the current status map on load.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        var generation = Volatile.Read(ref _workspaceGeneration);
        var workspacePath = WorkspacePath;
        if (RootNodes.Count == 0 || string.IsNullOrWhiteSpace(workspacePath))
        {
            return;
        }

        await _treeMutationGate.WaitAsync();
        try
        {
            if (!IsCurrentGeneration(generation) || RootNodes.Count == 0
                || !PathsEqual(WorkspacePath, workspacePath)) return;

            _fileWatcher.Attach(workspacePath);
            var root = RootNodes[0];
            DropTreeRecursive(root);
            root.IsExpanded = true;
            await root.LoadChildrenAsync();
            if (!IsCurrentGeneration(generation) || RootNodes.Count == 0
                || !ReferenceEquals(RootNodes[0], root)) return;

            SyncTreeRows();
            RefreshWatchedDirectories();
        }
        finally
        {
            _treeMutationGate.Release();
        }
    }

    /// <summary>Watcher burst (UI thread): filter to visible, relevant structural changes and start
    /// a throttled tree refresh. Everything irrelevant — git metadata churn, excluded files, and
    /// anything inside a name-ignored directory (node_modules/bin/obj/…) — is dropped here so an
    /// external <c>git add</c> or a build never causes a single directory re-enumeration.</summary>
    private void OnWorkspaceFilesChanged(object? sender, WorkspaceFilesChangedEventArgs e)
    {
        var root = WorkspacePath;
        if (string.IsNullOrWhiteSpace(root) || RootNodes.Count == 0)
        {
            return;
        }

        if (!string.Equals(Path.GetFullPath(e.RootPath), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
        {
            return; // delayed callback from a previous workspace generation
        }

        var rootFull = root.TrimEnd('\\', '/');
        foreach (var fullPath in e.ChangedPaths)
        {
            var trimmed = fullPath.TrimEnd('\\', '/');
            if (trimmed.Length <= rootFull.Length ||
                !trimmed.StartsWith(rootFull + "\\", StringComparison.OrdinalIgnoreCase))
            {
                continue; // outside the workspace (the root itself / parents are skipped —
                          // a root-level change would surface as its children's parent = root)
            }

            var relative = trimmed[(rootFull.Length + 1)..].Replace('\\', '/');
            // .git 元数据变化不构成可见树变化(Git 视图有自己的监听链)。
            if (relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 枚举过滤会隐藏 Ignored 命名的目录(node_modules/bin/obj/.vs):其内部变化不可见。
            // 末段是 Ignored 名字的文件(如名为 "bin" 的文件)仍会显示,只在其确为目录时跳过。
            var segments = relative.Split('/');
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (WorkspaceNode.Ignored.Contains(segments[i]))
                {
                    goto next;
                }
            }

            if (WorkspaceNode.Ignored.Contains(segments[^1]) && Directory.Exists(trimmed))
            {
                goto next;
            }

            if (_statusSource.IsExcluded(relative))
            {
                goto next;
            }

            lock (_pendingTreeGate)
            {
                _pendingTreePaths.Add(fullPath);
            }
        next:
            ;
        }

        bool hasPending;
        if (e.IsOverflowed)
        {
            // The native event path set is intentionally bounded. A burst overflow means the
            // precise set is incomplete, so reconcile from the workspace root once instead of
            // retaining an unbounded managed hash set.
            lock (_pendingTreeGate)
            {
                _pendingTreePaths.Add(root);
            }
        }

        lock (_pendingTreeGate)
        {
            hasPending = _pendingTreePaths.Count > 0;
        }

        if (hasPending)
        {
            WorkspaceFilesChanged?.Invoke(this, EventArgs.Empty);
            StartTreeRefresh(immediate: false);
        }
    }

    /// <summary>Single-flight + merged pending + minimum-interval entry for the watcher-driven tree
    /// refresh (same shape as GitViewModel's silent refresh; the tree has no IsBusy to gate on —
    /// manual refreshes and auto refreshes simply coalesce through the in-flight flag).</summary>
    private void StartTreeRefresh(bool immediate)
    {
        if (Interlocked.CompareExchange(ref _treeRefreshInFlight, 1, 0) == 1)
        {
            Interlocked.Exchange(ref _treeRefreshPending, 1);
            return;
        }

        if (!immediate)
        {
            var elapsedMs = Environment.TickCount64 - _lastTreeRefreshCompletedTicks;
            if (elapsedMs < TreeRefreshMinimumIntervalMs)
            {
                Interlocked.Exchange(ref _treeRefreshInFlight, 0);
                Interlocked.Exchange(ref _treeRefreshPending, 1);
                ScheduleTreeRefreshRetry(TreeRefreshMinimumIntervalMs - elapsedMs);
                return;
            }
        }

        Interlocked.Exchange(ref _treeRefreshPending, 0);
        _ = RunTreeRefreshAsync(); // its finally releases the in-flight flag
    }

    /// <summary>Schedules at most one retry of the pending tree refresh after
    /// <paramref name="delayMs"/>; re-enters through <see cref="StartTreeRefresh"/>, which
    /// re-checks every guard before actually refreshing.</summary>
    private void ScheduleTreeRefreshRetry(long delayMs)
    {
        if (Interlocked.Exchange(ref _treeRefreshRetryScheduled, 1) == 1)
        {
            return; // a retry is already scheduled; it will pick up the pending flag
        }

        _ = Task.Delay(TimeSpan.FromMilliseconds(delayMs)).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _treeRefreshRetryScheduled, 0);
            if (Volatile.Read(ref _treeRefreshPending) == 0)
            {
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                StartTreeRefresh(immediate: false); // no WPF app (unit tests without STA context)
                return;
            }

            // Never run UI-bound collection mutations on a ThreadPool fallback. If the dispatcher
            // is busy, keeping this retry queued is safe; it will run when the UI can consume it.
            dispatcher.BeginInvoke(new Action(() =>
            {
                StartTreeRefresh(immediate: false);
            }));
        });
    }

    /// <summary>Re-enumerates only the loaded directories that contain a changed path (the nearest
    /// loaded ancestor per path) and re-projects the rows once. Unloaded (never expanded) folders
    /// are left alone — they enumerate fresh on their next expansion, exactly like a lazy tree
    /// should. Expansion state is preserved: this is an in-place update, not a refresh-reset.</summary>
    private async Task RunTreeRefreshAsync()
    {
        var generation = Volatile.Read(ref _workspaceGeneration);
        var gateHeld = false;
        try
        {
            await _treeMutationGate.WaitAsync();
            gateHeld = true;
            if (!IsCurrentGeneration(generation)) return;
            HashSet<string> paths;
            lock (_pendingTreeGate)
            {
                paths = new HashSet<string>(_pendingTreePaths, StringComparer.OrdinalIgnoreCase);
                _pendingTreePaths.Clear();
            }

            if (paths.Count == 0)
            {
                return;
            }

            // V-诊断计数器(与 IncrementalSpliceCount / FullSyncCount 同族;测试断言自动刷新是否启动)。
            TreeAutoRefreshRuns++;

            // Index of every loaded directory node (the tree is lazy: only expanded/loaded dirs exist).
            var loadedDirs = new Dictionary<string, WorkspaceNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in RootNodes)
            {
                if (root.IsLoaded)
                {
                    CollectLoadedDirs(root, loadedDirs);
                }
            }

            var rootFull = WorkspacePath.TrimEnd('\\', '/');
            var toReload = new List<WorkspaceNode>();
            foreach (var fullPath in paths)
            {
                // The changed item's parent is the directory whose child list must change; walk up
                // until a loaded ancestor is found (a brand-new top-level folder's parent is the
                // root, which is loaded right after the workspace opens).
                var candidate = Path.GetDirectoryName(fullPath.TrimEnd('\\', '/'));
                while (!string.IsNullOrEmpty(candidate))
                {
                    if (string.Equals(candidate.TrimEnd('\\', '/'), rootFull, StringComparison.OrdinalIgnoreCase))
                    {
                        if (RootNodes.Count > 0 && RootNodes[0].IsLoaded)
                        {
                            toReload.Add(RootNodes[0]);
                        }

                        break;
                    }

                    if (loadedDirs.TryGetValue(candidate, out var node))
                    {
                        toReload.Add(node);
                        break;
                    }

                    candidate = Path.GetDirectoryName(candidate);
                }
            }

            if (toReload.Count == 0)
            {
                return;
            }

            // 就地协调:保留存活子节点对象(身份 = 已加载态/展开态/选区/行缓存),只增删真正
            // 变化的条目;目录 I/O 并行(各自在线程池枚举),子树变更回 UI 线程落地。
            var loads = new List<Task>(toReload.Count);
            foreach (var node in toReload.Distinct())
            {
                loads.Add(node.ReconcileChildrenAsync());
            }

            await Task.WhenAll(loads);
            if (!IsCurrentGeneration(generation)) return;
            SyncTreeRows();
            RefreshWatchedDirectories();
        }
        finally
        {
            if (gateHeld)
            {
                _treeMutationGate.Release();
            }
            Interlocked.Exchange(ref _treeRefreshInFlight, 0);
            _lastTreeRefreshCompletedTicks = Environment.TickCount64;
            if (Interlocked.Exchange(ref _treeRefreshPending, 0) == 1)
            {
                StartTreeRefresh(immediate: false);
            }
        }
    }

    /// <summary>Watch directories whose children are currently materialized. Never-loaded lazy
    /// folders reconcile on their next expansion; loaded descendants remain watched because the
    /// native watchers are non-recursive and compact-folder rows can expose a deeper directory
    /// through a collapsed ancestor.</summary>
    private void RefreshWatchedDirectories()
    {
        var directories = new List<string>();
        foreach (var root in RootNodes)
        {
            CollectWatchedDirectories(root, directories);
        }

        _fileWatcher.UpdateDirectories(directories);
    }

    private static void CollectWatchedDirectories(WorkspaceNode node, List<string> directories)
    {
        // Watch every materialized directory, not only expanded ones. Watchers are
        // non-recursive, and a loaded child can still be represented by a compact-folder
        // row while one of its ancestors is collapsed. Keeping all loaded directories watched
        // prevents changes in that effective row from being missed; never-loaded folders remain
        // lazy and are still not watched.
        if (!node.IsDirectory || !node.IsLoaded)
        {
            return;
        }

        directories.Add(node.Path);
        foreach (var child in node.Children)
        {
            if (child.IsDirectory && !child.IsPlaceholder)
            {
                CollectWatchedDirectories(child, directories);
            }
        }
    }

    /// <summary>Collects this loaded directory node and the loaded descendants reachable through
    /// its non-placeholder children into <paramref name="index"/>, keyed by full path.</summary>
    private static void CollectLoadedDirs(WorkspaceNode node, Dictionary<string, WorkspaceNode> index)
    {
        if (!node.IsDirectory)
        {
            return;
        }

        index[node.Path] = node;
        foreach (var child in node.Children)
        {
            if (child.IsDirectory && !child.IsPlaceholder && child.IsLoaded)
            {
                CollectLoadedDirs(child, index);
            }
        }
    }

    /// <summary>并行展开目录树(旧实现是 DFS 串行 await,大仓库"展开全部"逐目录等待磁盘 I/O)。
    /// BFS + 有界并发(最多 8 个在途枚举);LoadChildrenAsync 对同一节点并发安全(合并进行中加载)。</summary>
    private static async Task ExpandRecursiveAsync(WorkspaceNode root)
    {
        if (!root.IsDirectory)
        {
            return;
        }

        const int maxParallel = 8;
        var pending = new System.Collections.Concurrent.ConcurrentQueue<WorkspaceNode>();
        var inFlight = new List<Task>();

        root.IsExpanded = true;
        await root.LoadChildrenAsync();
        pending.Enqueue(root);

        while (pending.Count > 0 || inFlight.Count > 0)
        {
            while (inFlight.Count < maxParallel && pending.TryDequeue(out var dir))
            {
                inFlight.Add(ExpandOneLevelAsync(dir, pending));
            }

            var finished = await Task.WhenAny(inFlight);
            inFlight.Remove(finished);
            await finished; // 传播异常
        }
    }

    private static async Task ExpandOneLevelAsync(WorkspaceNode dir, System.Collections.Concurrent.ConcurrentQueue<WorkspaceNode> pending)
    {
        var next = new List<WorkspaceNode>();
        foreach (var child in dir.Children)
        {
            if (!child.IsDirectory || child.IsPlaceholder)
            {
                continue;
            }

            child.IsExpanded = true;
            await child.LoadChildrenAsync();
            next.Add(child);
        }

        foreach (var child in next)
        {
            pending.Enqueue(child);
        }
    }

    private static void CollapseRecursive(WorkspaceNode node)
    {
        if (!node.IsDirectory)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            CollapseRecursive(child);
        }

        node.IsExpanded = false;
    }

    private static void DropTreeRecursive(WorkspaceNode node)
    {
        foreach (var child in node.Children.ToArray())
        {
            DropTreeRecursive(child);
        }

        node.DropChildren();
    }

    /// <summary>紧凑模式翻转时把标志写进已加载节点并重算计数(沿父链传播)。</summary>
    private static void SetCompactFoldersRecursive(WorkspaceNode node, bool compact)
    {
        node.CompactFolders = compact;
        node.RecomputeVisibleRowCountChain();
        foreach (var child in node.Children)
        {
            SetCompactFoldersRecursive(child, compact);
        }
    }

    /// <summary>Decorations (letter + staged/untracked flags) for a repository-relative path, used to
    /// build a diff request from the explorer.</summary>
    public WorkspaceGitInfo? GetGitInfo(string relativePath) => _statusSource.Lookup(relativePath);

    /// <summary>Applies the latest repository status as decorations on the loaded tree. Safe to call
    /// repeatedly after each source-control refresh; lazily loaded nodes pick up the current map
    /// automatically via <see cref="WorkspaceStatusSource"/>.</summary>
    public void ApplyGitStatus(GitRepositoryStatus? status)
    {
        var map = new Dictionary<string, WorkspaceGitInfo>(StringComparer.OrdinalIgnoreCase);
        if (status is { IsRepository: true })
        {
            foreach (var change in status.StagedChanges)
            {
                map[change.Path] = new WorkspaceGitInfo(change.IndexStatus.ToStatusLetter(), IsStaged: true, IsUntracked: false);
            }

            foreach (var change in status.UnstagedChanges)
            {
                // 未跟踪文件与更改树(GitView)徽章一致:用绿色 "A" 表达"待添加",不再使用 '?'。
                map[change.Path] = change.IsUntracked
                    ? new WorkspaceGitInfo('A', IsStaged: false, IsUntracked: true)
                    : new WorkspaceGitInfo(change.WorkTreeStatus.ToStatusLetter(), IsStaged: false, IsUntracked: false);
            }
        }

        // V6/G9: 状态 map 无变化 → 整轮跳过(静默刷新的装饰刷新从"全树递归"变成零开销);
        // 有变化 → 只刷新受影响路径 + 其祖先链的节点,不再遍历全部已加载节点做 decoration。
        // 跳过的前提是"已加载节点的装饰仍与 _lastStatusMap 一致",即状态源仍持有它;
        // 状态源被外部重置(清空/换 map)时,即使 map 无变化也必须全量重刷。
        var changedPaths = ComputeChangedPaths(_lastStatusMap, map);
        if (changedPaths.Count == 0 && ReferenceEquals(_statusSource.CurrentMap, _lastStatusMap))
        {
            return;
        }

        _lastStatusMap = map;
        _statusSource.Set(map);
        RefreshDecorations(changedPaths);
        RequestDiffCommand.NotifyCanExecuteChanged();
    }

    /// <summary>上次应用的状态 map(用于等值跳过与差集计算)。</summary>
    private IReadOnlyDictionary<string, WorkspaceGitInfo>? _lastStatusMap;

    /// <summary>V4 模型诊断计数器:展开/折叠走 O(Δ) 区间拼接的次数(快速路径)。</summary>
    internal int IncrementalSpliceCount { get; private set; }

    /// <summary>V4 模型诊断计数器:整表重建(CollectVisibleRows + CollectionDiffer)的次数。</summary>
    internal int FullSyncCount { get; private set; }

    /// <summary>V-诊断计数器:文件监视驱动的树自动刷新实际执行的次数(过滤后无可刷新内容的不计)。</summary>
    internal int TreeAutoRefreshRuns { get; private set; }

    /// <summary>测试/诊断:强制整表同步一次(与真实路径同一实现)。</summary>
    internal void SyncTreeRowsForTest() => SyncTreeRows();

    /// <summary>测试/诊断:当前扁平行序列的快照(行对象按节点复用,引用同一)。</summary>
    internal WorkspaceTreeRow[] TreeRowsSnapshot() => WorkspaceTreeRows.ToArray();

    /// <summary>返回旧/新 map 之间发生变化的路径集合(新增、移除或字母/暂存标志变化)。</summary>
    private static HashSet<string> ComputeChangedPaths(
        IReadOnlyDictionary<string, WorkspaceGitInfo>? previous,
        IReadOnlyDictionary<string, WorkspaceGitInfo> current)
    {
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (previous is null)
        {
            changed.UnionWith(current.Keys);
            return changed;
        }

        foreach (var (path, info) in current)
        {
            if (!previous.TryGetValue(path, out var old) || old != info)
            {
                changed.Add(path);
            }
        }

        foreach (var path in previous.Keys)
        {
            if (!current.ContainsKey(path))
            {
                changed.Add(path);
            }
        }

        return changed;
    }

    /// <summary>Locates a file by its workspace-relative path: expands ancestor folders and selects
    /// the node so the user can see where the change lives. Used by source-control reveal and active-editor sync.</summary>
    public async Task<bool> RevealNodeAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var generation = Volatile.Read(ref _workspaceGeneration);
        var root = RootNodes.FirstOrDefault();
        if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(relativePath)
            || root is null)
        {
            return false;
        }

        var segments = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            await current.LoadChildrenAsync();
            if (cancellationToken.IsCancellationRequested || !IsCurrentGeneration(generation)
                || !IsCurrentTreeNode(current))
            {
                return false;
            }

            var match = current.Children.FirstOrDefault(child =>
                !child.IsPlaceholder && string.Equals(child.Name, segments[index], StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return false;
            }

            if (index < segments.Length - 1)
            {
                match.IsExpanded = true;
                current = match;
                if (!IsCurrentGeneration(generation) || !IsCurrentTreeNode(current)) return false;
            }
            else
            {
                if (!IsCurrentGeneration(generation) || !IsCurrentTreeNode(match)) return false;
                _suppressSelectionOpen = true;
                try
                {
                    match.IsSelected = true;
                }
                finally
                {
                    _suppressSelectionOpen = false;
                }

                SyncTreeRows(match);
                RefreshWatchedDirectories();

                // 增量同步不再重置列表滚动;显式通知视图把目标行滚入视野(旧整表重建靠选区
                // 变化隐式滚动,增量更新下选中项可能已可见,需显式 reveal)。
                if (_rowCache.TryGetValue(match, out var row))
                {
                    RevealRowRequested?.Invoke(row);
                }

                return true;
            }
        }

        return false;
    }

    private void RefreshDecorations(IReadOnlySet<string> changedPaths)
    {
        if (changedPaths.Count == 0)
        {
            return;
        }

        // 受影响集合 = 每个变化路径本身 + 其全部祖先目录(VS Code 装饰按需计算只刷新
        // 命中节点 + 祖先链;目录点/字母由 WorkspaceStatusSource 的 O(1) 索引驱动)。
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in changedPaths)
        {
            var rest = path.Replace('\\', '/');
            affected.Add(rest);
            while (true)
            {
                var slash = rest.LastIndexOf('/');
                if (slash < 0)
                {
                    affected.Add(string.Empty); // 仓库根目录节点
                    break;
                }

                rest = rest[..slash];
                affected.Add(rest);
            }
        }

        foreach (var path in affected)
        {
            FindLoadedNode(path)?.RefreshDecoration();
        }
    }

    /// <summary>Resolves one loaded node by workspace-relative path without walking unrelated
    /// loaded subtrees. Git status updates typically touch a handful of paths; recursively
    /// scanning every expanded node made that common path O(the whole explorer).</summary>
    private WorkspaceNode? FindLoadedNode(string relativePath)
    {
        if (RootNodes.Count == 0)
        {
            return null;
        }

        WorkspaceNode? current = RootNodes[0];
        if (string.IsNullOrEmpty(relativePath))
        {
            return current;
        }

        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is null || !current.IsDirectory || !current.IsLoaded)
            {
                return null;
            }

            var next = current.Children.FirstOrDefault(child =>
                !child.IsPlaceholder && string.Equals(child.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    partial void OnSelectedTreeRowChanged(WorkspaceTreeRow? value)
    {
        if (_suppressTreeSelectionOpen) return;

        if (value is null)
        {
            // 选区被程序化清空(行被移除/外部置空):撤销尚未落地的打开。
            CancelPendingSelectionOpen();
            return;
        }

        if (!IsCurrentTreeNode(value.Node) || !WorkspaceTreeRows.Contains(value))
        {
            CancelPendingSelectionOpen();
            _suppressTreeSelectionOpen = true;
            SelectedTreeRow = null;
            _suppressTreeSelectionOpen = false;
            return;
        }

        if (value is WorkspaceFolderRow)
        {
            _suppressTreeSelectionOpen = true;
            SelectedTreeRow = null;
            _suppressTreeSelectionOpen = false;
            // 最新的选区是文件夹(不打开文件):撤销之前排队中的文件打开,只有最后一次选中打开。
            CancelPendingSelectionOpen();
            return;
        }

        value.Node.IsSelected = true;
        if (value is WorkspaceFileRow fileRow)
        {
            // V7: 只排队、不立即打开;100ms 静默后仅打开最后一次选中的文件。
            lock (_pendingSelectionOpenGate)
            {
                _pendingSelectionOpenRow = fileRow;
                _pendingSelectionOpenGeneration = Volatile.Read(ref _workspaceGeneration);
            }

            _selectionOpenScheduler.Schedule();
        }
    }

    private void CancelPendingSelectionOpen()
    {
        _selectionOpenScheduler.Cancel();
        lock (_pendingSelectionOpenGate)
        {
            _pendingSelectionOpenRow = null;
            _pendingSelectionOpenGeneration = 0;
        }
    }

    private void RunPendingSelectionOpen()
    {
        WorkspaceFileRow? row;
        long generation;
        lock (_pendingSelectionOpenGate)
        {
            row = _pendingSelectionOpenRow;
            _pendingSelectionOpenRow = null;
            generation = _pendingSelectionOpenGeneration;
            _pendingSelectionOpenGeneration = 0;
        }

        if (row is null || _suppressTreeSelectionOpen || !IsCurrentGeneration(generation)
            || !IsCurrentTreeNode(row.Node) || !WorkspaceTreeRows.Contains(row))
        {
            return;
        }

        void Run() => OpenTreeFileCommand.Execute(row);

        // 定时器在线程池线程触发:不在 UI 线程时切回再建标签(与 SearchViewModel 的合并回调同约定)。
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Run();
        }
        else
        {
            dispatcher.BeginInvoke(Run);
        }
    }

    /// <summary>
    /// 增量同步扁平行投影(VS Code listView splice 的 .NET 对应物):
    /// 计算目标行序列 → 与当前序列做引用级前后缀 diff → 只对中间区间发 Remove/Insert。
    /// 未受影响的行与 ListBox 容器保持不动;行对象按节点复用,因此 Git 装饰等行内
    /// 状态在展开/折叠/刷新中不丢失。选区按节点身份保持。
    /// </summary>
    private void SyncTreeRows(WorkspaceNode? preferredNode = null)
    {
        FullSyncCount++;
        preferredNode ??= SelectedTreeRow?.Node;

        var desired = new List<WorkspaceTreeRow>(Math.Max(4, WorkspaceTreeRows.Count));
        foreach (var root in RootNodes)
        {
            CollectVisibleRows(root, depth: 0, sink: desired);
        }

        // 仍在目标序列中的行(含中间区间内被移动、删后重插的行)不得解除订阅。
        var kept = new HashSet<WorkspaceTreeRow>(desired.Count, ReferenceEqualityComparer.Instance);
        foreach (var row in desired) kept.Add(row);

        _suppressTreeSelectionOpen = true;
        try
        {
            // 前后缀 diff + 中间区间先删后插:未受影响行与容器保持不动。
            var removed = CollectionDiffer.Apply(WorkspaceTreeRows, desired, ReferenceEqualityComparer.Instance);
            foreach (var row in removed)
            {
                if (!kept.Contains(row) && _rowCache.Remove(row.Node, out var cached) && ReferenceEquals(cached, row))
                {
                    row.Detach();
                }
            }
        }
        finally
        {
            _suppressTreeSelectionOpen = false;
        }

        if (SelectedTreeRow is not null && !desired.Contains(SelectedTreeRow, ReferenceEqualityComparer.Instance))
        {
            CancelPendingSelectionOpen();
            _suppressTreeSelectionOpen = true;
            try
            {
                SelectedTreeRow = null;
            }
            finally
            {
                _suppressTreeSelectionOpen = false;
            }
        }

        // 选区:被移除的选中项会清空 ListBox 选区,按节点身份重新指回同一行对象。
        if (preferredNode is not null)
        {
            WorkspaceTreeRow? selectedRow = null;
            foreach (var row in desired)
            {
                if (ReferenceEquals(row.Node, preferredNode))
                {
                    selectedRow = row;
                    break;
                }
            }

            if (selectedRow is not null && !ReferenceEquals(SelectedTreeRow, selectedRow))
            {
                _suppressTreeSelectionOpen = true;
                SelectedTreeRow = selectedRow;
                _suppressTreeSelectionOpen = false;
            }
        }

        TreeRowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把可见节点序列写入 sink(不触碰集合)。行对象来自复用池,紧凑文件夹合并行
    /// 的显示名就地更新。与旧 AddVisibleRows 的投影规则完全一致。</summary>
    private void CollectVisibleRows(WorkspaceNode node, int depth, List<WorkspaceTreeRow> sink)
    {
        if (node.IsPlaceholder) return;

        // VS Code compact folders:目录链上每个目录都恰好只有一个子目录时,合并为 "父 / 子" 单行
        // (叶子目录的展开/折叠作用于链最深层)。占位节点不参与合并。
        if (_compactFoldersEnabled && node.IsDirectory)
        {
            var segments = new List<string> { node.Name };
            var current = node;
            while (current.Children.Count == 1 && current.Children[0] is { IsDirectory: true, IsPlaceholder: false } child)
            {
                segments.Add(child.Name);
                current = child;
            }

            if (segments.Count > 1)
            {
                // 合并行代表链最深层目录:展开/折叠、Git 装饰与定位都以它为对象。
                var merged = GetOrCreateRow(current, depth);
                if (merged is WorkspaceFolderRow folder)
                {
                    folder.SetDisplayNameOverride(string.Join(" / ", segments));
                }

                sink.Add(merged);
                if (current.IsExpanded)
                {
                    foreach (var child in current.Children)
                    {
                        CollectVisibleRows(child, depth + 1, sink);
                    }
                }

                return;
            }
        }

        var row = GetOrCreateRow(node, depth);
        if (row is WorkspaceFolderRow plainFolder)
        {
            plainFolder.SetDisplayNameOverride(null);
        }

        sink.Add(row);
        if (!node.IsDirectory || !node.IsExpanded) return;

        foreach (var child in node.Children)
        {
            CollectVisibleRows(child, depth + 1, sink);
        }
    }

    /// <summary>取(或创建)节点的行;同节点复用同一行对象(类型变化时替换并解除旧订阅)。</summary>
    private WorkspaceTreeRow GetOrCreateRow(WorkspaceNode node, int depth)
    {
        if (_rowCache.TryGetValue(node, out var existing) && KindMatches(existing, node))
        {
            existing.SetDepth(depth);
            return existing;
        }

        DetachCachedRow(node);
        var row = node.IsDirectory
            ? (WorkspaceTreeRow)new WorkspaceFolderRow(node, depth)
            : new WorkspaceFileRow(node, depth);
        _rowCache[node] = row;
        return row;
    }

    private static bool KindMatches(WorkspaceTreeRow row, WorkspaceNode node) =>
        (row is WorkspaceFolderRow) == node.IsDirectory;

    private void DetachCachedRow(WorkspaceNode node)
    {
        if (_rowCache.Remove(node, out var row))
        {
            row.Detach();
        }
    }
}

/// <summary>Common visual projection for a flat workspace tree row. Values are deliberately
/// derived from WorkspaceNode so disk loading and Git decorations never have two sources of truth.
/// Rows forward the node's decoration changes (<see cref="WorkspaceNode.StatusLetter"/> and
/// <see cref="WorkspaceNode.HasChange"/>) as their own property changes, so a git status refresh
/// updates the visible rows in place instead of rebuilding the whole flattened projection.</summary>
public abstract class WorkspaceTreeRow : System.ComponentModel.INotifyPropertyChanged
{
    private readonly WorkspaceNode _node;
    private double[]? _ancestorGuideLefts;
    private bool _detached;

    protected WorkspaceTreeRow(WorkspaceNode node, int depth)
    {
        _node = node;
        Depth = depth;
        _node.PropertyChanged += OnNodePropertyChanged;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public WorkspaceNode Node => _node;

    /// <summary>行在扁平列表中的缩进深度。行对象被增量同步复用,深度可能随紧凑文件夹链
    /// 重排而变化;变化时通知 IndentMargin / AncestorGuideLefts。</summary>
    public int Depth { get; private set; }

    public string Name => _node.Name;

    /// <summary>行显示名:默认即节点名;紧凑文件夹合并行覆盖为 "父 / 子" 组合名。</summary>
    public virtual string DisplayName => Name;

    public string Path => _node.Path;
    public string RelativePath => _node.RelativePath;
    public bool IsFolder => _node.IsDirectory;
    public string StatusLetter => _node.StatusLetter;
    public bool HasChange => _node.HasChange;
    public Thickness IndentMargin => new(12 * Depth, 0, 0, 0);

    /// <summary>各级引导线 X 坐标。按深度缓存(VS Code 的 guide 位置是纯算术,
    /// 旧实现每次绑定求值都新分配数组)。</summary>
    public double[] AncestorGuideLefts => _ancestorGuideLefts ??=
        Depth switch
        {
            0 => [],
            _ => Enumerable.Range(0, Depth).Select(level => 12.0 * level + 8.5).ToArray()
        };

    /// <summary>设置深度(增量同步复用时);仅在变化时通知。</summary>
    internal void SetDepth(int depth)
    {
        if (Depth == depth) return;
        Depth = depth;
        _ancestorGuideLefts = null;
        OnPropertyChanged(nameof(IndentMargin));
        OnPropertyChanged(nameof(AncestorGuideLefts));
    }

    protected void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

    /// <summary>解除与节点的订阅。行被增量同步移除(或工作区整体替换)时调用;
    /// 旧实现整表重建时不解除,节点事件表会累积死行引用(泄漏)。</summary>
    internal void Detach()
    {
        if (_detached) return;
        _detached = true;
        _node.PropertyChanged -= OnNodePropertyChanged;
    }

    private void OnNodePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceNode.StatusLetter)
            or nameof(WorkspaceNode.HasChange)
            or nameof(WorkspaceNode.IsExpanded))
        {
            PropertyChanged?.Invoke(this, e);
        }
    }
}

public sealed class WorkspaceFolderRow(WorkspaceNode node, int depth) : WorkspaceTreeRow(node, depth)
{
    public bool IsCollapsed => !Node.IsExpanded;
    public bool IsExpanded => Node.IsExpanded;

    /// <summary>紧凑文件夹合并行显示组合名(默认 null = 使用节点名)。
    /// 增量同步复用行对象时由 <see cref="SetDisplayNameOverride"/> 更新。</summary>
    public string? DisplayNameOverride { get; private set; }

    public override string DisplayName => DisplayNameOverride ?? base.DisplayName;

    internal void SetDisplayNameOverride(string? value)
    {
        if (string.Equals(DisplayNameOverride, value, StringComparison.Ordinal)) return;
        DisplayNameOverride = value;
        OnPropertyChanged(nameof(DisplayName));
    }
}

public sealed class WorkspaceFileRow(WorkspaceNode node, int depth) : WorkspaceTreeRow(node, depth);

/// <summary>Git decoration info for a workspace file: the status letter to render (untracked files
/// are shown as "A", matching the source-control badge) plus enough context to build a diff request
/// (staged vs untracked).</summary>
public readonly record struct WorkspaceGitInfo(char Letter, bool IsStaged, bool IsUntracked);

/// <summary>Shared, mutable status map referenced by all <see cref="WorkspaceNode"/> instances so a
/// refresh updates already-loaded nodes and lazily-loaded nodes pick up the same current map.</summary>
public sealed class WorkspaceStatusSource
{
    private IReadOnlyDictionary<string, WorkspaceGitInfo>? _map;

    /// <summary>Precomputed folder-index: directory prefix (normalized, no trailing slash; empty
    /// string = repository root) → highest-priority descendant status letter. Rebuilt once per
    /// status refresh so per-folder lookups stay O(1) — scanning the whole change map per folder
    /// made explorer decoration refresh O(loaded-folders × changes) on the UI thread.</summary>
    private Dictionary<string, char>? _folderBest;

    private List<string> _excludePatterns = [];

    /// <summary>当前持有的状态 map 引用(调用方据此判断"装饰与上次 map 一致"的跳过前提
    /// 是否仍然成立;Clear/换 map 之后引用必然不等)。</summary>
    public IReadOnlyDictionary<string, WorkspaceGitInfo>? CurrentMap => _map;

    public void Set(IReadOnlyDictionary<string, WorkspaceGitInfo>? map)
    {
        _map = map;
        _folderBest = BuildFolderIndex(map);
    }

    public void Clear()
    {
        _map = null;
        _folderBest = null;
    }

    public void SetExcludes(IReadOnlyDictionary<string, bool> excludes)
    {
        // Normalize once per rule, not once per file (enumeration calls IsExcluded for every entry).
        var patterns = new List<string>(excludes.Count);
        foreach (var (pattern, enabled) in excludes)
        {
            if (!enabled) continue;
            patterns.Add(pattern.Replace("**", "*", StringComparison.Ordinal));
        }
        _excludePatterns = patterns;
    }

    public bool IsExcluded(string relativePath)
    {
        if (_excludePatterns.Count == 0)
        {
            return false;
        }

        var normalized = relativePath.Replace('\\', '/');
        var fileName = System.IO.Path.GetFileName(normalized);
        foreach (var pattern in _excludePatterns)
        {
            if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, normalized, true) ||
                System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, fileName, true))
                return true;
        }
        return false;
    }

    public WorkspaceGitInfo? Lookup(string relativePath) =>
        _map is not null && relativePath.Length > 0 && _map.TryGetValue(relativePath, out var info) ? info : null;

    /// <summary>True when any changed path lives under a folder's relative path (the VS Code-style
    /// changed-folder dot). The workspace root (empty path) is dirty whenever anything changed.</summary>
    public bool HasChangeUnder(string directoryRelativePath)
    {
        return HighestStatusUnder(directoryRelativePath) is not null;
    }

    /// <summary>Returns the most important descendant status for a folder. Modified content wins
    /// over every other status so a folder containing both M and A/D files is rendered as modified.
    /// O(1) dictionary lookup into the precomputed folder index.</summary>
    public char? HighestStatusUnder(string directoryRelativePath)
    {
        if (_map is null || _map.Count == 0 || _folderBest is null)
        {
            return null;
        }

        var normalizedDirectory = directoryRelativePath.Replace('\\', '/').Trim('/');
        return _folderBest.TryGetValue(normalizedDirectory, out var letter) ? letter : null;
    }

    private static Dictionary<string, char>? BuildFolderIndex(IReadOnlyDictionary<string, WorkspaceGitInfo>? map)
    {
        if (map is null || map.Count == 0)
        {
            return null;
        }

        var index = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, info) in map)
        {
            var priority = StatusPriority(info.Letter);
            var rest = path.Replace('\\', '/');
            // Walk every ancestor directory (including the repository root, stored under "").
            // Keep walking even when a deeper ancestor already holds a better letter: a shallower
            // directory may still need this one.
            while (true)
            {
                var slash = rest.LastIndexOf('/');
                if (slash < 0)
                {
                    TryImprove(index, string.Empty, info.Letter, priority);
                    break;
                }

                var directory = rest[..slash];
                TryImprove(index, directory, info.Letter, priority);
                rest = directory;
            }
        }

        return index;
    }

    private static void TryImprove(Dictionary<string, char> index, string directory, char letter, int priority)
    {
        if (!index.TryGetValue(directory, out var current) || StatusPriority(current) < priority)
        {
            index[directory] = letter;
        }
    }

    private static int StatusPriority(char status) => status switch
    {
        'M' => 100, // explicit requirement: modified has the highest folder priority
        'C' or 'T' => 80,
        'D' or 'U' => 70,
        'A' => 60, // 未跟踪文件渲染为 "A",与已暂存 Added 同级
        'R' => 50,
        _ => 0,
    };
}

public sealed partial class WorkspaceNode : ObservableObject
{
    /// <summary>目录名黑名单(枚举时隐藏)。<see cref="WorkspaceViewModel"/> 的树自动刷新过滤
    /// 必须与它同源,否则会被这些目录内部的变化白白触发重枚举。</summary>
    internal static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "bin", "obj", ".vs" };

    private readonly string _rootPath;
    private readonly WorkspaceStatusSource _statusSource;

    public WorkspaceNode(string path, bool isDirectory, string rootPath, WorkspaceStatusSource statusSource)
    {
        Path = path;
        IsDirectory = isDirectory;
        _rootPath = rootPath;
        _statusSource = statusSource;
        Name = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(Name))
        {
            Name = path;
        }

        RelativePath = ComputeRelativePath();
        if (isDirectory)
        {
            Children.Add(new WorkspaceNode(string.Empty, false, rootPath, statusSource) { IsPlaceholder = true, Parent = this });
        }

        RefreshDecoration();
        VisibleRowCount = ComputeVisibleRowCount();
    }

    public string Path { get; }
    public string Name { get; }
    public string RelativePath { get; }
    public bool IsDirectory { get; }
    public bool IsPlaceholder { get; private set; }
    public string Glyph => IsDirectory ? Codicons.Folder : Codicons.File;
    public ObservableCollection<WorkspaceNode> Children { get; } = [];

    /// <summary>父节点(V4 增量投影:可见行数变化沿父链向上传播;根节点为 null)。</summary>
    internal WorkspaceNode? Parent { get; set; }

    /// <summary>紧凑文件夹是否生效(VM 绑定设置后写入;计数/链判定必须与 CollectVisibleRows
    /// 的投影规则同源——否则"目录只有一个目录子节点"时计数与渲染行数会不一致)。</summary>
    internal bool CompactFolders { get; set; }

    /// <summary>V4 缓存的可见行数:当前投影下本子树渲染的行数(目录渲染一行、文件渲染自身
    /// 一行;占位符为 0;紧凑文件夹链中链顶/中间成员不另计行,合并行计入链末端)。
    /// 只在展开/折叠与子节点加载/丢弃时增量维护(沿父链传播,O(深度×兄弟数)),
    /// SyncTreeRows 的快路径据此做 O(Δ) 区间拼接而非全表重建。</summary>
    internal int VisibleRowCount { get; private set; }

    /// <summary>紧凑文件夹判定:恰有一个非占位目录子节点时返回它(该节点会被并入链),否则 null。</summary>
    internal WorkspaceNode? SingleDirectoryChild
    {
        get
        {
            if (!IsDirectory || Children.Count != 1)
            {
                return null;
            }

            var only = Children[0];
            return only.IsDirectory && !only.IsPlaceholder ? only : null;
        }
    }

    private int ComputeVisibleRowCount()
    {
        if (IsPlaceholder)
        {
            return 0;
        }

        if (!IsDirectory)
        {
            return 1;
        }

        // 与 CollectVisibleRows 的投影规则同构(紧凑链的合并行属于链末端子树的行,链上各
        // 节点不重复计数;折叠目录只有自身一行,子树行尚未投影)。紧凑模式关闭时单目录子节点
        // 照常渲染自身一行 + 子树,不合并。
        var single = CompactFolders ? SingleDirectoryChild : null;
        if (single is not null)
        {
            return single.VisibleRowCount;
        }

        var sum = 0;
        if (IsExpanded)
        {
            foreach (var child in Children)
            {
                if (!child.IsPlaceholder)
                {
                    sum += child.VisibleRowCount;
                }
            }
        }

        return 1 + sum;
    }

    /// <summary>重算自身可见行数并沿父链传播,直到某个祖先计数不变(其祖先输入未变,可停)。</summary>
    internal void RecomputeVisibleRowCountChain()
    {
        var node = this;
        while (true)
        {
            var before = node.VisibleRowCount;
            node.VisibleRowCount = node.ComputeVisibleRowCount();
            if (node.VisibleRowCount == before || node.Parent is null)
            {
                return;
            }

            node = node.Parent;
        }
    }

    [ObservableProperty]
    private bool isLoaded;

    [ObservableProperty]
    private string statusLetter = string.Empty;

    [ObservableProperty]
    private bool hasChange;

    [ObservableProperty]
    private bool isExpanded;

    [ObservableProperty]
    private bool isSelected;

    partial void OnIsExpandedChanged(bool value)
    {
        // V4: 展开/折叠立即更新可见行数缓存(此刻子树尚未加载变化,计数按当前子节点
        // 重算;随后的加载完成会再次重算并向父链传播)。
        RecomputeVisibleRowCountChain();
        if (value)
        {
            _ = LoadChildrenAsync();
        }
    }

    private Task? _pendingLoad;
    private int _pendingLoadGeneration;
    private int _childrenLoadGeneration;

    /// <summary>枚举目录内容(枚举在 ThreadPool 执行:.NET 无异步目录枚举 API,超大目录的
    /// 枚举不再阻塞 UI 线程——展开/打开卡顿的修复点之一)。并发调用加入进行中的同一次加载
    /// (展开触发是 fire-and-forget,RevealNode / 测试 await 同一任务)。</summary>
    public Task LoadChildrenAsync()
    {
        if (!IsDirectory)
        {
            return Task.CompletedTask;
        }

        // 进行中的加载优先:CoreAsync 在首个 await 前即登记 _pendingLoad,
        // 若先判 IsLoaded,并发调用者会误以为已加载而拿到空 Children。DropChildren
        // 会推进代次,因此刷新期间不能复用已经失效的枚举任务。
        var generation = Volatile.Read(ref _childrenLoadGeneration);
        if (_pendingLoad is not null && _pendingLoadGeneration == generation)
        {
            return _pendingLoad;
        }

        if (IsLoaded)
        {
            return Task.CompletedTask;
        }

        var task = LoadChildrenCoreAsync(generation);
        _pendingLoad = task;
        _pendingLoadGeneration = generation;
        return task;
    }

    private async Task LoadChildrenCoreAsync(int generation)
    {
        try
        {
            var (directories, files) = await EnumerateChildrenCoreAsync();

            // 刷新/切换工作区可能在磁盘枚举期间丢弃了该节点。旧结果不能重新物化到
            // 已经失效的节点,否则新一轮加载会被旧任务覆盖。
            if (generation != Volatile.Read(ref _childrenLoadGeneration))
            {
                return;
            }

            // Children 的变更回到 UI 线程(续延捕获自 UI 上下文);顺序与旧同步版一致:
            // 排序后的目录在前,排序后的文件在后。
            Children.Clear();
            foreach (var directory in directories)
            {
                Children.Add(CreateChildNode(directory, true));
            }

            foreach (var file in files)
            {
                Children.Add(CreateChildNode(file, false));
            }

            // 只有完整枚举并成功物化后才标记为已加载。这样临时的权限/IO/删除竞态
            // 不会把失败结果永久缓存成“空目录”。
            IsLoaded = true;
        }
        catch (Exception)
        {
            if (generation != Volatile.Read(ref _childrenLoadGeneration))
            {
                return;
            }

            // 保留旧的物化子节点；首次加载失败时补回占位节点，下一次展开仍可重试。
            IsLoaded = false;
            if (Children.Count == 0)
            {
                Children.Add(new WorkspaceNode(string.Empty, false, _rootPath, _statusSource)
                {
                    IsPlaceholder = true,
                    Parent = this,
                });
            }
        }
        finally
        {
            if (_pendingLoadGeneration == generation)
            {
                _pendingLoad = null;
            }

            // V4: 子节点集合变化(占位符 → 真实子树)可能改变紧凑链形态与可见行数,
            // 重算自身并向父链传播(未展开/枚举失败时计数不变,传播即刻停止)。
            if (generation == Volatile.Read(ref _childrenLoadGeneration))
            {
                RecomputeVisibleRowCountChain();
            }
        }
    }

    /// <summary>枚举本目录的可见条目(同名/排除规则过滤 + 排序)。.NET 没有异步目录枚举:
    /// 同步枚举放到线程池,超大目录(构建产物/依赖)的枚举不再阻塞 UI 线程——展开/打开卡顿
    /// 的修复点之一。失败时抛出(调用方决定降级)。</summary>
    private Task<(string[] Directories, string[] Files)> EnumerateChildrenCoreAsync() => Task.Run(() =>
    {
        var directories = Directory.EnumerateDirectories(Path)
            .Where(path => !Ignored.Contains(System.IO.Path.GetFileName(path)) &&
                           !_statusSource.IsExcluded(System.IO.Path.GetRelativePath(_rootPath, path)))
            .OrderBy(System.IO.Path.GetFileName)
            .ToArray();
        var files = Directory.EnumerateFiles(Path)
            .Where(path => !_statusSource.IsExcluded(System.IO.Path.GetRelativePath(_rootPath, path)))
            .OrderBy(System.IO.Path.GetFileName)
            .ToArray();
        return (directories, files);
    });

    private WorkspaceNode CreateChildNode(string fullPath, bool isDirectory) =>
        new(fullPath, isDirectory, _rootPath, _statusSource)
        {
            Parent = this,
            // 子节点继承本节点的紧凑标志(根节点由 VM 按设置写入)。
            CompactFolders = CompactFolders,
        };

    /// <summary>树自动刷新的就地协调(与 LoadChildrenCoreAsync 的区别):**保留**磁盘上仍存在的
    /// 子节点对象——节点身份承载已加载状态、展开态、选区与行缓存,整表重建会把兄弟子树的这些
    /// 状态一并抹掉(一次无关变更让整棵子树"失忆",展开态全丢)。只有真正新增/消失的条目产生
    /// 子树变化;顺序(目录在前、文件在后、各自字母序)按磁盘实况重排。枚举失败时保持现状。</summary>
    public async Task ReconcileChildrenAsync()
    {
        if (!IsDirectory || !IsLoaded)
        {
            return; // 未加载目录保持惰性:下次展开时按磁盘实况枚举
        }

        var generation = Volatile.Read(ref _childrenLoadGeneration);
        if (_pendingLoad is not null)
        {
            await _pendingLoad; // 序列化:进行中的展开加载完成后按同一磁盘实况收敛
        }

        if (generation != Volatile.Read(ref _childrenLoadGeneration) || !IsLoaded)
        {
            return;
        }

        string[] directories;
        string[] files;
        try
        {
            (directories, files) = await EnumerateChildrenCoreAsync();
        }
        catch (Exception)
        {
            return; // 枚举失败:保留既有子树,下一次刷新重试
        }

        if (generation != Volatile.Read(ref _childrenLoadGeneration) || !IsLoaded)
        {
            return;
        }

        // 存活节点按全路径索引(同一目录下文件与目录不可能同名同路径)。
        var survivors = new Dictionary<string, WorkspaceNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in Children)
        {
            if (!child.IsPlaceholder)
            {
                survivors[child.Path] = child;
            }
        }

        var reconciled = new List<WorkspaceNode>(directories.Length + files.Length);
        foreach (var directory in directories)
        {
            reconciled.Add(ResolveOrCreate(directory, true));
        }

        foreach (var file in files)
        {
            reconciled.Add(ResolveOrCreate(file, false));
        }

        WorkspaceNode ResolveOrCreate(string fullPath, bool isDirectory)
        {
            if (survivors.TryGetValue(fullPath, out var keep))
            {
                survivors.Remove(fullPath);
                return keep;
            }

            return CreateChildNode(fullPath, isDirectory);
        }

        // survivors 剩余者 = 磁盘上已消失的条目(自然丢弃);顺序按磁盘实况重排。不要 Clear()
        // 再逐项 Add：一个文件变化不应让大目录的 CollectionView 收到 N 次重建通知。
        CollectionDiffer.Apply(Children, reconciled, ReferenceEqualityComparer.Instance);

        // V4: 子节点集合变化可能改变紧凑链形态与可见行数,重算自身并向父链传播。
        RecomputeVisibleRowCountChain();
    }

    /// <summary>Resets a directory node to its unloaded, collapsed placeholder state (explorer refresh).</summary>
    public void DropChildren()
    {
        if (!IsDirectory)
        {
            return;
        }

        // 刷新丢弃子树时展开态必须同步重置(RefreshAsync 契约:"Expansion state resets for the
        // refreshed subtree")。否则 IsExpanded 仍为 true 但只剩占位符——chevron 显示"已展开"却
        // 投影不出子行,用户第一次点击只是翻转空展开(delta=0,无可见变化),第二次才真正展开。
        if (IsExpanded)
        {
            IsExpanded = false;
        }

        Interlocked.Increment(ref _childrenLoadGeneration);
        IsLoaded = false;
        Children.Clear();
        Children.Add(new WorkspaceNode(string.Empty, false, _rootPath, _statusSource) { IsPlaceholder = true, Parent = this });
        // V4: 子树被丢弃 → 可见行数回到占位形态(目录自身一行),向父链传播。
        RecomputeVisibleRowCountChain();
    }

    /// <summary>Recomputes the git decoration from the current status source: files show a status
    /// letter, folders show the "changed" dot when any descendant changed.</summary>
    public void RefreshDecoration()
    {
        if (IsDirectory)
        {
            var status = _statusSource.HighestStatusUnder(RelativePath);
            HasChange = status is not null;
            StatusLetter = status?.ToString() ?? string.Empty;
        }
        else
        {
            var info = _statusSource.Lookup(RelativePath);
            StatusLetter = info?.Letter.ToString() ?? string.Empty;
            HasChange = false;
        }
    }

    private string ComputeRelativePath()
    {
        if (string.IsNullOrEmpty(_rootPath) || string.IsNullOrEmpty(Path))
        {
            return string.Empty;
        }

        var root = _rootPath.TrimEnd('\\', '/');
        var full = Path.TrimEnd('\\', '/');
        if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        // Git reports repository-relative paths with '/' separators on every platform; normalize so
        // tree decorations (keyed by git status paths) and diff requests match.
        if (full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return full[(root.Length + 1)..].Replace('\\', '/');
        }

        return full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
            ? full[(root.Length + 1)..]
            : full;
    }
}
