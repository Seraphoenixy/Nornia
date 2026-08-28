using System.IO;

namespace Nornia.Desktop.Services;

/// <summary>Watches a git working tree for file changes so the source-control view can refresh
/// silently (VS Code SCM auto-refresh). Implementations raise <see cref="ChangesDetected"/> once
/// per burst of changes, already debounced and marshaled to the UI thread.</summary>
public sealed class GitRepositoryChangesDetectedEventArgs(
    string repositoryPath,
    IReadOnlySet<string> changedPaths,
    bool indexChanged = false,
    bool headOrRefsChanged = false,
    bool isUnknown = false) : EventArgs
{
    public string RepositoryPath { get; } = repositoryPath;
    public IReadOnlySet<string> ChangedPaths { get; } = changedPaths
        .Select(NormalizeRelativePath)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    public bool IndexChanged { get; } = indexChanged;
    public bool HeadOrRefsChanged { get; } = headOrRefsChanged;
    public bool IsUnknown { get; } = isUnknown;

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');
}

public interface IGitRepositoryWatcher : IDisposable
{
    /// <summary>Raised (on the UI thread) after a debounced burst of working-tree changes.</summary>
    event EventHandler<GitRepositoryChangesDetectedEventArgs>? ChangesDetected;

    /// <summary>Starts watching <paramref name="repositoryPath"/> (idempotent; a missing or empty
    /// path is a no-op). Re-attaching keeps the current watcher and never suppresses an
    /// external change.</summary>
    void Attach(string repositoryPath);

    /// <summary>Stops watching and releases the file-system watcher (idempotent).</summary>
    void Detach();

    /// <summary>Arms the self-trigger suppression window: <c>.git\index</c> events are ignored for
    /// a short duration. Called when a git read/write pass starts or finishes, because git may
    /// write back the index stat cache and would otherwise trigger a refresh loop.</summary>
    void BeginSuppressionWindow();

    /// <summary>Suppresses watcher notifications produced by one local Git mutation until the
    /// caller has published its explicit status snapshot. The optional paths identify working-tree
    /// files the mutation is expected to touch; an empty collection means index-only, and
    /// <see langword="null"/> means every working-tree path is covered.</summary>
    IDisposable BeginOperationSuppression(IReadOnlyCollection<string>? workingTreePaths = null);
}

/// <summary>No-op watcher used by the legacy <see cref="Nornia.Desktop.ViewModels.GitViewModel"/>
/// constructor overload (mirrors <see cref="NullClipboardService"/>): never raises events.</summary>
internal sealed class NullGitRepositoryWatcher : IGitRepositoryWatcher
{
    public static readonly NullGitRepositoryWatcher Instance = new();

    private NullGitRepositoryWatcher()
    {
    }

    public event EventHandler<GitRepositoryChangesDetectedEventArgs>? ChangesDetected
    {
        add
        {
        }
        remove
        {
        }
    }

    public void Attach(string repositoryPath)
    {
    }

    public void Detach()
    {
    }

    public void BeginSuppressionWindow()
    {
    }

    public IDisposable BeginOperationSuppression(IReadOnlyCollection<string>? workingTreePaths = null) => NoopLease.Instance;

    public void Dispose()
    {
    }

    private sealed class NoopLease : IDisposable
    {
        public static readonly NoopLease Instance = new();
        public void Dispose()
        {
        }
    }
}

/// <summary>Recursive <see cref="FileSystemWatcher"/> over the repository working tree with
/// short trailing debounce (each new event resets a timer) and UI-thread marshaling, VS Code-style.
/// Everything under <c>.git</c> is ignored except the index and the metadata that can change the
/// visible repository state (HEAD, refs and packed refs), so external <c>git add</c>, commits and
/// branch switches are still picked up. During a suppression window
/// <c>.git\index</c> events are dropped — the application's own git writes (and the index stat
/// cache write-back of <c>git status</c>) must not feed back into refreshes.</summary>
public sealed class GitRepositoryWatcher : IGitRepositoryWatcher
{
    // One second makes normal save → SCM feedback feel broken. Keep enough room to collapse the
    // Created/Changed pair emitted by Windows while refreshing the status within one UI beat.
    internal static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromMilliseconds(200);
    internal static readonly TimeSpan DefaultSuppressionWindow = TimeSpan.FromMilliseconds(250);
    private const int InternalBufferSize = 64 * 1024;

    private readonly IUiDispatcher _dispatcher;
    private readonly TimeSpan _debounceInterval;
    private readonly TimeSpan _suppressionWindow;
    private readonly object _gate = new();

    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private DateTime _suppressUntilUtc = DateTime.MinValue;
    private int _operationSuppressionDepth;
    private bool _suppressAllWorkingTreePaths;
    private readonly HashSet<string> _suppressedWorkingTreePaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _repositoryPath;
    private readonly HashSet<string> _pendingChangedPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _pendingIndexChanged;
    private bool _pendingHeadOrRefsChanged;

    public GitRepositoryWatcher() : this(new WpfUiDispatcher(), DefaultDebounceInterval, DefaultSuppressionWindow)
    {
    }

    /// <summary>Test seam: shorter debounce / suppression durations keep the watcher tests fast.</summary>
    internal GitRepositoryWatcher(IUiDispatcher dispatcher, TimeSpan debounceInterval, TimeSpan suppressionWindow)
    {
        _dispatcher = dispatcher;
        _debounceInterval = debounceInterval;
        _suppressionWindow = suppressionWindow;
    }

    public event EventHandler<GitRepositoryChangesDetectedEventArgs>? ChangesDetected;

    public void Attach(string repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            return;
        }

        lock (_gate)
        {
            if (string.Equals(_repositoryPath, repositoryPath, StringComparison.OrdinalIgnoreCase) && _watcher is not null)
            {
                // Idempotent re-attach: do not create an artificial blind window. The caller
                // explicitly opens a brief suppression window only while it performs a git read
                // or write pass; external changes immediately after a refresh must be observed.
                return;
            }

            DetachNoLock();
            _repositoryPath = repositoryPath;
            var watcher = new FileSystemWatcher(repositoryPath)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = InternalBufferSize,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
            };
            watcher.Changed += OnFileSystemEvent;
            watcher.Created += OnFileSystemEvent;
            watcher.Deleted += OnFileSystemEvent;
            watcher.Renamed += OnFileSystemEvent;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
    }

    public void Detach()
    {
        lock (_gate)
        {
            DetachNoLock();
        }
    }

    public void BeginSuppressionWindow()
    {
        lock (_gate)
        {
            ArmSuppressionNoLock();
        }
    }

    public IDisposable BeginOperationSuppression(IReadOnlyCollection<string>? workingTreePaths = null)
    {
        lock (_gate)
        {
            _operationSuppressionDepth++;
            if (workingTreePaths is null)
            {
                _suppressAllWorkingTreePaths = true;
            }
            else
            {
                foreach (var path in workingTreePaths)
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        _suppressedWorkingTreePaths.Add(NormalizeRelativePath(path));
                    }
                }
            }
        }

        return new OperationSuppressionLease(this);
    }

    /// <summary>Filters a filesystem event path; internal so the unit tests exercise the exact
    /// production logic (including the suppression window) without a real watcher.</summary>
    internal bool ShouldReactTo(string fullPath)
    {
        string repositoryPath;
        DateTime suppressUntilUtc;
        bool suppressOperationIndex;
        bool suppressAllWorkingTreePaths;
        HashSet<string>? suppressedWorkingTreePaths;
        lock (_gate)
        {
            if (_repositoryPath is null)
            {
                return false;
            }

            repositoryPath = _repositoryPath;
            suppressUntilUtc = _suppressUntilUtc;
            suppressOperationIndex = _operationSuppressionDepth > 0;
            suppressAllWorkingTreePaths = _suppressAllWorkingTreePaths;
            suppressedWorkingTreePaths = _operationSuppressionDepth > 0
                ? new HashSet<string>(_suppressedWorkingTreePaths, StringComparer.OrdinalIgnoreCase)
                : null;
        }

        // The suppression window covers .git\index only: git itself writes the index back during
        // the app's own operations (and git status refreshes the stat cache), while an external
        // HEAD switch should still be picked up immediately.
        if ((DateTime.UtcNow < suppressUntilUtc || suppressOperationIndex) && IsGitIndexPath(fullPath, repositoryPath))
        {
            return false;
        }

        var relative = RelativeToRepository(fullPath, repositoryPath);
        if (relative is not null && !relative.StartsWith(".git\\", StringComparison.OrdinalIgnoreCase) &&
            (suppressAllWorkingTreePaths || suppressedWorkingTreePaths?.Contains(NormalizeRelativePath(relative)) == true))
        {
            // The explicit status read at the end of the operation is authoritative for these
            // paths, so this is not a timed blind spot: an external write before that read is
            // included in the same snapshot; a later write is observed after the lease ends.
            return false;
        }

        return IsRelevantPath(fullPath, repositoryPath);
    }

    internal static bool IsRelevantPath(string fullPath, string repositoryPath)
    {
        var relative = RelativeToRepository(fullPath, repositoryPath);
        if (relative is null)
        {
            return false;
        }

        // Ignore object/lock/log churn, but retain the metadata that affects status or the graph.
        // A commit normally modifies refs\heads\<branch>, not HEAD itself, so filtering refs made
        // the graph stale until a manual refresh.
        if (relative.StartsWith(".git\\", StringComparison.OrdinalIgnoreCase))
        {
            return IsRelevantGitMetadata(relative);
        }

        return true;
    }

    /// <summary>Debounced, filtered notification path — internal for the same reason as
    /// <see cref="ShouldReactTo"/>: tests drive the real pipeline minus the OS watcher.</summary>
    internal void HandleFileSystemChange(string fullPath)
    {
        if (!ShouldReactTo(fullPath))
        {
            return;
        }

        lock (_gate)
        {
            if (_repositoryPath is null)
            {
                return;
            }

            var relative = RelativeToRepository(fullPath, _repositoryPath);
            if (relative is null)
            {
                return;
            }

            if (IsGitIndexPath(fullPath, _repositoryPath))
            {
                _pendingIndexChanged = true;
            }
            else if (relative.StartsWith(".git\\", StringComparison.OrdinalIgnoreCase))
            {
                _pendingHeadOrRefsChanged = true;
            }
            else
            {
                _pendingChangedPaths.Add(relative.Replace('\\', '/'));
            }
        }

        ScheduleDebouncedNotification();
    }

    /// <summary>Watcher error (buffer overflow, directory removed): raise one change notification
    /// and stop watching — the view model's state check then shows the degraded state, and a later
    /// successful refresh re-attaches once the directory is reachable again.</summary>
    internal void HandleWatcherError()
    {
        string? repositoryPath;
        lock (_gate)
        {
            repositoryPath = _repositoryPath;
        }

        Detach();
        if (repositoryPath is not null)
        {
            var args = new GitRepositoryChangesDetectedEventArgs(repositoryPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase), isUnknown: true);
            _dispatcher.BeginInvoke(() => ChangesDetected?.Invoke(this, args));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            DetachNoLock();
        }
    }

    private static string? RelativeToRepository(string fullPath, string repositoryPath)
    {
        string normalized;
        string root;
        try
        {
            // Normalize first so ".." segments cannot sneak out of the working tree.
            normalized = Path.GetFullPath(fullPath);
            root = Path.GetFullPath(repositoryPath).TrimEnd('\\', '/') + "\\";
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        if (normalized.Length <= root.Length ||
            !normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return normalized[root.Length..];
    }

    private static bool IsGitIndexPath(string fullPath, string repositoryPath) =>
        string.Equals(RelativeToRepository(fullPath, repositoryPath), ".git\\index", StringComparison.OrdinalIgnoreCase);

    private static bool IsRelevantGitMetadata(string relative) =>
        string.Equals(relative, ".git\\index", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(relative, ".git\\HEAD", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(relative, ".git\\packed-refs", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(relative, ".git\\FETCH_HEAD", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(relative, ".git\\ORIG_HEAD", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(relative, ".git\\MERGE_HEAD", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(relative, ".git\\CHERRY_PICK_HEAD", StringComparison.OrdinalIgnoreCase) ||
        relative.StartsWith(".git\\refs\\", StringComparison.OrdinalIgnoreCase) ||
        relative.StartsWith(".git\\rebase-apply\\", StringComparison.OrdinalIgnoreCase) ||
        relative.StartsWith(".git\\rebase-merge\\", StringComparison.OrdinalIgnoreCase);

    private void ArmSuppressionNoLock() => _suppressUntilUtc = DateTime.UtcNow + _suppressionWindow;

    private void EndOperationSuppression()
    {
        lock (_gate)
        {
            if (_operationSuppressionDepth == 0)
            {
                return;
            }

            _operationSuppressionDepth--;
            if (_operationSuppressionDepth == 0)
            {
                _suppressAllWorkingTreePaths = false;
                _suppressedWorkingTreePaths.Clear();
                // Status may update Git's stat cache just as the lease ends.
                ArmSuppressionNoLock();
            }
        }
    }

    private sealed class OperationSuppressionLease(GitRepositoryWatcher owner) : IDisposable
    {
        private GitRepositoryWatcher? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndOperationSuppression();
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        HandleFileSystemChange(e.FullPath);
        if (e is RenamedEventArgs renamed)
        {
            HandleFileSystemChange(renamed.OldFullPath);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e) => HandleWatcherError();

    private void ScheduleDebouncedNotification()
    {
        lock (_gate)
        {
            // Trailing debounce: every new event restarts the window, so a burst collapses into
            // a single notification after the edits settle.
            if (_debounceTimer is null)
            {
                _debounceTimer = new Timer(_ => RaiseChangesDetected(), null, _debounceInterval, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _debounceTimer.Change(_debounceInterval, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void RaiseChangesDetected()
    {
        GitRepositoryChangesDetectedEventArgs? args = null;
        lock (_gate)
        {
            if (_repositoryPath is not null &&
                (_pendingChangedPaths.Count > 0 || _pendingIndexChanged || _pendingHeadOrRefsChanged))
            {
                args = new GitRepositoryChangesDetectedEventArgs(
                    _repositoryPath,
                    new HashSet<string>(_pendingChangedPaths, StringComparer.OrdinalIgnoreCase),
                    _pendingIndexChanged,
                    _pendingHeadOrRefsChanged);
            }

            _pendingChangedPaths.Clear();
            _pendingIndexChanged = false;
            _pendingHeadOrRefsChanged = false;
        }

        if (args is not null)
        {
            _dispatcher.BeginInvoke(() => ChangesDetected?.Invoke(this, args));
        }
    }

    private void DetachNoLock()
    {
        if (_watcher is not null)
        {
            var watcher = _watcher;
            _watcher = null;
            watcher.EnableRaisingEvents = false;
            // FileSystemWatcher teardown can block for seconds while the OS drains a flooded
            // event buffer (a build churning thousands of files in the tree). Detach is on the
            // shutdown path, where blocking would eat the cleanup budget, so release the native
            // watch handle off this thread; the OS reclaims it at process exit either way.
            Task.Run(() =>
            {
                try { watcher.Dispose(); }
                catch (Exception) { /* best-effort; the process owns the handle until exit */ }
            });
        }

        _debounceTimer?.Dispose();
        _debounceTimer = null;
        _pendingChangedPaths.Clear();
        _pendingIndexChanged = false;
        _pendingHeadOrRefsChanged = false;
        _operationSuppressionDepth = 0;
        _suppressAllWorkingTreePaths = false;
        _suppressedWorkingTreePaths.Clear();
        _repositoryPath = null;
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/').TrimStart('/');
}
