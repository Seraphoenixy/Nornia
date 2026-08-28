using System.IO;

namespace Nornia.Desktop.Services;

/// <summary>Arguments for <see cref="IFileContentWatcher.FileChanged"/>.</summary>
public sealed class FileChangedEventArgs : EventArgs
{
    public FileChangedEventArgs(string fullPath) => FullPath = fullPath;

    /// <summary>Full path of the file that changed on disk.</summary>
    public string FullPath { get; }
}

/// <summary>Watches individual open files for external changes (saves, atomic replaces, creation,
/// deletion, renames) and raises <see cref="FileChanged"/> on the UI thread, once per debounced
/// burst. File-system watchers are shared per directory: only paths registered via
/// <see cref="Watch"/> are ever reported, so churn in the same directory from unrelated files is
/// ignored.</summary>
public interface IFileContentWatcher : IDisposable
{
    /// <summary>Raised (on the UI thread) after a watched file's debounced change burst. The path is
    /// one registered with <see cref="Watch"/> (still registered at raise time).</summary>
    event EventHandler<FileChangedEventArgs>? FileChanged;

    /// <summary>Starts reporting changes for <paramref name="path"/> (idempotent; a missing file or
    /// directory is still watched — the nearest existing ancestor directory is used — so a later
    /// creation is reported).</summary>
    void Watch(string path);

    /// <summary>Stops reporting changes for <paramref name="path"/> (idempotent). The shared
    /// per-directory watcher is released when no registered file remains in that directory.</summary>
    void Unwatch(string path);
}

/// <summary>No-op watcher for the legacy <see cref="Nornia.Desktop.ViewModels.EditorAreaViewModel"/>
/// constructor overloads that do not receive a watcher (mirrors <see cref="NullClipboardService"/>);
/// never raises events.</summary>
internal sealed class NullFileContentWatcher : IFileContentWatcher
{
    public static readonly NullFileContentWatcher Instance = new();

    private NullFileContentWatcher()
    {
    }

    public event EventHandler<FileChangedEventArgs>? FileChanged
    {
        add
        {
        }
        remove
        {
        }
    }

    public void Watch(string path)
    {
    }

    public void Unwatch(string path)
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>Directory-shared <see cref="FileSystemWatcher"/> over each watched file's directory with
/// a per-file trailing debounce (each event resets the timer) and UI-thread marshaling. Handles
/// Changed / Created / Deleted / Renamed; a rename is reported for the old and the new name, so an
/// atomic save (temp → target via File.Move / File.Replace) reloads the target.</summary>
public sealed class FileContentWatcher : IFileContentWatcher
{
    internal static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromMilliseconds(200);
    private const int InternalBufferSize = 64 * 1024;

    private readonly IUiDispatcher _dispatcher;
    private readonly TimeSpan _debounceInterval;
    private readonly object _gate = new();

    /// <summary>Registered target files: normalized full path → per-file debounce state.</summary>
    private readonly Dictionary<string, WatchedFile> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Shared directory watchers: watch root (nearest existing ancestor) → watcher +
    /// the registered files under it.</summary>
    private readonly Dictionary<string, DirectoryWatch> _directories = new(StringComparer.OrdinalIgnoreCase);

    public FileContentWatcher() : this(new WpfUiDispatcher(), DefaultDebounceInterval)
    {
    }

    /// <summary>Test seam: inject a dispatcher and a shorter/trailing debounce window.</summary>
    internal FileContentWatcher(IUiDispatcher dispatcher, TimeSpan debounceInterval)
    {
        _dispatcher = dispatcher;
        _debounceInterval = debounceInterval;
    }

    public event EventHandler<FileChangedEventArgs>? FileChanged;

    public void Watch(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        lock (_gate)
        {
            if (_files.ContainsKey(fullPath))
            {
                return;
            }

            // A registered file may live in a directory that vanished (deleted folder); watch the
            // nearest existing ancestor so a later recreation is still observed.
            var directory = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                var parent = Path.GetDirectoryName(directory);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, directory, StringComparison.Ordinal)) break;
                directory = parent;
            }

            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }

            var file = new WatchedFile(fullPath, directory, _debounceInterval, OnDebounceElapsed);
            _files[fullPath] = file;
            if (!_directories.TryGetValue(directory, out var directoryWatch))
            {
                directoryWatch = new DirectoryWatch(EnsureWatcher(directory));
                _directories[directory] = directoryWatch;
            }

            directoryWatch.Files.Add(fullPath);
        }
    }

    public void Unwatch(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        lock (_gate)
        {
            if (!_files.Remove(fullPath, out var file))
            {
                return;
            }

            file.Dispose(); // cancels any pending debounce
            if (!_directories.TryGetValue(file.WatchRoot, out var directoryWatch))
            {
                return;
            }

            directoryWatch.Files.Remove(fullPath);
            if (directoryWatch.Files.Count == 0)
            {
                directoryWatch.Dispose();
                _directories.Remove(file.WatchRoot);
            }
        }
    }

    public void Dispose()
    {
        List<DirectoryWatch> directories;
        lock (_gate)
        {
            foreach (var file in _files.Values)
            {
                file.Dispose();
            }

            directories = [.. _directories.Values];
            _files.Clear();
            _directories.Clear();
        }

        // FileSystemWatcher teardown can block for seconds while the OS drains a flooded event
        // buffer. This runs on the shutdown path, so stop event generation synchronously and
        // release each native watch handle off-thread; the OS reclaims the handles at process
        // exit either way.
        foreach (var directory in directories)
        {
            var watcher = directory.Watcher;
            try
            {
                watcher.EnableRaisingEvents = false;
            }
            catch (Exception) { /* already torn down */ }
            Task.Run(() =>
            {
                try { watcher.Dispose(); }
                catch (Exception) { /* best-effort; the process owns the handle until exit */ }
            });
        }
    }

    /// <summary>Entry point shared by every FileSystemWatcher handler; internal for the deterministic
    /// watcher tests (the real watcher drives the exact same path). Only registered target files are
    /// reported; everything else in the directory is ignored.</summary>
    internal void HandleFileSystemEvent(string fullPath)
    {
        WatchedFile? file;
        lock (_gate)
        {
            if (!_files.TryGetValue(fullPath, out file))
            {
                return;
            }
        }

        file.Arm();
    }

    private FileSystemWatcher EnsureWatcher(string directory)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            IncludeSubdirectories = true,
            InternalBufferSize = InternalBufferSize,
            EnableRaisingEvents = true,
        };
        watcher.Changed += (_, args) => HandleFileSystemEvent(args.FullPath);
        watcher.Created += (_, args) => HandleFileSystemEvent(args.FullPath);
        watcher.Deleted += (_, args) => HandleFileSystemEvent(args.FullPath);
        // An atomic save (File.Move temp→target / File.Replace) renames the temp onto the target:
        // report both the old and the new name so a moved-away target is still reloaded.
        watcher.Renamed += (_, args) =>
        {
            HandleFileSystemEvent(args.OldFullPath);
            HandleFileSystemEvent(args.FullPath);
        };
        return watcher;
    }

    private void OnDebounceElapsed(WatchedFile file)
    {
        // Fire on the UI thread; the pending raise is dropped if the file was unwatched after the
        // debounce window closed (e.g. the editor tab was closed while the timer was pending).
        _dispatcher.BeginInvoke(() =>
        {
            bool stillWatched;
            lock (_gate)
            {
                stillWatched = _files.TryGetValue(file.FullPath, out var current) && ReferenceEquals(current, file);
            }

            if (stillWatched)
            {
                FileChanged?.Invoke(this, new FileChangedEventArgs(file.FullPath));
            }
        });
    }

    /// <summary>Per-file trailing debounce: every file-system event re-arms the timer so a burst of
    /// events (Created + Changed emitted by a save) collapses into a single UI-thread raise.</summary>
    private sealed class WatchedFile : IDisposable
    {
        private readonly Timer _timer;
        private readonly TimeSpan _interval;

        public WatchedFile(string fullPath, string watchRoot, TimeSpan interval, Action<WatchedFile> onElapsed)
        {
            FullPath = fullPath;
            WatchRoot = watchRoot;
            _interval = interval;
            _timer = new Timer(_ => onElapsed(this), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public string FullPath { get; }

        public string WatchRoot { get; }

        public void Arm()
        {
            try
            {
                _timer.Change(_interval, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Unwatch raced with an in-flight event; the raise is dropped anyway.
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }

    private sealed class DirectoryWatch : IDisposable
    {
        public DirectoryWatch(FileSystemWatcher watcher) => Watcher = watcher;

        public FileSystemWatcher Watcher { get; }

        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Dispose() => Watcher.Dispose();
    }
}