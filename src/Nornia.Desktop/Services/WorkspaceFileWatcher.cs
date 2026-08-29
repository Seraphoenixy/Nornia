using System.IO;

namespace Nornia.Desktop.Services;

/// <summary>Arguments for <see cref="IWorkspaceFileWatcher.FilesChanged"/>: the workspace root the
/// watcher is attached to plus the full paths that changed structure (created / deleted / renamed —
/// a rename is reported for both the old and the new name) during one debounced window.</summary>
public sealed class WorkspaceFilesChangedEventArgs : EventArgs
{
    public WorkspaceFilesChangedEventArgs(string rootPath, IReadOnlySet<string> changedPaths)
    {
        RootPath = rootPath;
        ChangedPaths = changedPaths;
    }

    /// <summary>Workspace root the notification was raised for.</summary>
    public string RootPath { get; }

    /// <summary>Full paths of the created / deleted / renamed files and directories.</summary>
    public IReadOnlySet<string> ChangedPaths { get; }
}

/// <summary>Structural watcher over the workspace root and its currently loaded directories.
/// Content-only churn (saves: LastWrite / Size) is intentionally not watched — only creation,
/// deletion and rename change the tree.</summary>
public interface IWorkspaceFileWatcher : IDisposable
{
    /// <summary>Raised (on the UI thread) after a debounced burst of structural changes.</summary>
    event EventHandler<WorkspaceFilesChangedEventArgs>? FilesChanged;

    /// <summary>Starts watching <paramref name="rootPath"/> (idempotent; switching roots detaches
    /// the previous one). A missing directory leaves no watch attached.</summary>
    void Attach(string rootPath);

    /// <summary>Replaces the set of loaded directories whose immediate children should be watched.
    /// Callers must include the workspace root; implementations may ignore missing directories.</summary>
    void UpdateDirectories(IEnumerable<string> directories);

    /// <summary>Stops watching (idempotent).</summary>
    void Detach();
}

/// <summary>No-op watcher for the legacy <see cref="Nornia.Desktop.ViewModels.WorkspaceViewModel"/>
/// constructor calls that do not receive one (mirrors <see cref="NullFileContentWatcher"/>);
/// never raises events.</summary>
internal sealed class NullWorkspaceFileWatcher : IWorkspaceFileWatcher
{
    public static readonly NullWorkspaceFileWatcher Instance = new();

    private NullWorkspaceFileWatcher()
    {
    }

    public event EventHandler<WorkspaceFilesChangedEventArgs>? FilesChanged
    {
        add
        {
        }
        remove
        {
        }
    }

    public void Attach(string rootPath)
    {
    }

    public void Detach()
    {
    }

    public void UpdateDirectories(IEnumerable<string> directories)
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>One non-recursive <see cref="FileSystemWatcher"/> per loaded explorer directory, with a
/// short trailing debounce and UI-thread marshaling. Watching only visible/lazy-loaded directories
/// prevents build output and dependency trees from flooding a root-recursive watcher buffer.</summary>
public sealed class WorkspaceFileWatcher : IWorkspaceFileWatcher
{
    internal static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromMilliseconds(200);
    private const int InternalBufferSize = 64 * 1024;

    private readonly IUiDispatcher _dispatcher;
    private readonly TimeSpan _debounceInterval;
    private readonly object _gate = new();

    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _debounceTimer;
    private string? _rootPath;
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private long _generation;

    public WorkspaceFileWatcher() : this(new WpfUiDispatcher(), DefaultDebounceInterval)
    {
    }

    /// <summary>Test seam: inject a dispatcher and a shorter debounce window.</summary>
    internal WorkspaceFileWatcher(IUiDispatcher dispatcher, TimeSpan debounceInterval)
    {
        _dispatcher = dispatcher;
        _debounceInterval = debounceInterval;
    }

    public event EventHandler<WorkspaceFilesChangedEventArgs>? FilesChanged;

    public void Attach(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return;
        }

        rootPath = Path.GetFullPath(rootPath);

        lock (_gate)
        {
            if (string.Equals(_rootPath, rootPath, StringComparison.OrdinalIgnoreCase) && _watchers.Count > 0)
            {
                return;
            }

            DetachNoLock();
            if (!Directory.Exists(rootPath))
            {
                return;
            }

            _rootPath = rootPath;
            AddWatcherNoLock(rootPath);
        }
    }

    public void UpdateDirectories(IEnumerable<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        lock (_gate)
        {
            if (_rootPath is null)
            {
                return;
            }

            var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _rootPath };
            foreach (var directory in directories)
            {
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    requested.Add(Path.GetFullPath(directory));
                }
            }

            foreach (var stale in _watchers.Keys.Where(path => !requested.Contains(path)).ToArray())
            {
                DisposeWatcherNoLock(stale);
            }

            foreach (var directory in requested)
            {
                if (!_watchers.ContainsKey(directory))
                {
                    AddWatcherNoLock(directory);
                }
            }
        }
    }

    public void Detach()
    {
        lock (_gate)
        {
            DetachNoLock();
        }
    }

    /// <summary>Entry point shared by every FileSystemWatcher handler; internal so the unit tests
    /// exercise the exact production path without a real watcher. The path is collapsed into the
    /// pending set and the debounce timer is (re)armed; one raise per quiet window.</summary>
    internal void HandleFileSystemEvent(string fullPath)
    {
        lock (_gate)
        {
            if (_watchers.Count == 0)
            {
                return;
            }

            _pendingPaths.Add(fullPath);
            _debounceTimer ??= new Timer(_ => OnDebounceElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            try
            {
                _debounceTimer.Change(_debounceInterval, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Detached while the event was in flight; nothing to raise.
            }
        }
    }

    private void OnRenamedEvent(object sender, RenamedEventArgs e)
    {
        // A rename is both a deletion (old name) and a creation (new name) for the tree: report
        // both so a moved directory's parent and a renamed file's row are both refreshed.
        HandleFileSystemEventFromWatcher(sender, e.OldFullPath);
        HandleFileSystemEventFromWatcher(sender, e.FullPath);
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e) => HandleFileSystemEventFromWatcher(sender, e.FullPath);

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        lock (_gate)
        {
            if (sender is not FileSystemWatcher watcher || !_watchers.Values.Contains(watcher))
            {
                return;
            }

            // Recreate just the failed handle. Unlike a recursive root watcher, a burst in an
            // ignored subtree cannot poison every directory's buffer; this is a final recovery
            // path for transient handle/root failures.
            var directory = _watchers.First(pair => ReferenceEquals(pair.Value, watcher)).Key;
            DisposeWatcherNoLock(directory);
            if (Directory.Exists(directory) && _rootPath is not null)
            {
                AddWatcherNoLock(directory);
            }
        }
    }

    private void OnDebounceElapsed()
    {
        string? root;
        long generation;
        HashSet<string> paths;
        lock (_gate)
        {
            if (_watchers.Count == 0)
            {
                return;
            }

            root = _rootPath;
            generation = _generation;
            paths = new HashSet<string>(_pendingPaths, StringComparer.OrdinalIgnoreCase);
            _pendingPaths.Clear();
        }

        _dispatcher.BeginInvoke(() =>
        {
            lock (_gate)
            {
                if (paths.Count == 0 || root is null || generation != _generation || _rootPath is null)
                {
                    return;
                }
            }

            FilesChanged?.Invoke(this, new WorkspaceFilesChangedEventArgs(root, paths));
        });
    }

    private void DetachNoLock()
    {
        _generation++;
        _debounceTimer?.Dispose();
        _debounceTimer = null;
        _pendingPaths.Clear();
        foreach (var directory in _watchers.Keys.ToArray())
        {
            DisposeWatcherNoLock(directory);
        }

        _rootPath = null;
    }

    private void HandleFileSystemEventFromWatcher(object sender, string fullPath)
    {
        lock (_gate)
        {
            if (sender is not FileSystemWatcher watcher || !_watchers.Values.Contains(watcher))
            {
                return;
            }

            _pendingPaths.Add(fullPath);
            _debounceTimer ??= new Timer(_ => OnDebounceElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            try
            {
                _debounceTimer.Change(_debounceInterval, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Detached while an OS callback was in flight.
            }
        }
    }

    private void AddWatcherNoLock(string directory)
    {
        FileSystemWatcher watcher;
        try
        {
            watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                InternalBufferSize = InternalBufferSize,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Directory removal can race a tree reconciliation. The next refresh/expand retries.
            return;
        }

        watcher.Created += OnFileSystemEvent;
        watcher.Deleted += OnFileSystemEvent;
        watcher.Renamed += OnRenamedEvent;
        watcher.Error += OnWatcherError;
        watcher.EnableRaisingEvents = true;
        _watchers.Add(directory, watcher);
    }

    private void DisposeWatcherNoLock(string directory)
    {
        if (!_watchers.Remove(directory, out var watcher))
        {
            return;
        }

        watcher.EnableRaisingEvents = false;
        watcher.Created -= OnFileSystemEvent;
        watcher.Deleted -= OnFileSystemEvent;
        watcher.Renamed -= OnRenamedEvent;
        watcher.Error -= OnWatcherError;
        Task.Run(() =>
        {
            try
            {
                watcher.Dispose();
            }
            catch (Exception)
            {
                // Best-effort native-handle release during teardown.
            }
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            DetachNoLock();
        }
    }
}
