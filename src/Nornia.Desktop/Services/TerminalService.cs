using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Nornia.Desktop.Native;
using Nornia.Desktop.Terminal;

namespace Nornia.Desktop.Services;

public sealed record ShellProfile(string Id, string Name, string Executable, string Arguments = "");

public enum TerminalSessionState { Starting, Running, Exited, Failed }

/// <summary>Owns lightweight, redirected console sessions.  The service deliberately creates no
/// browser or WebView process; every session is a child of the desktop process and is torn down on
/// disposal.  The stream contract also keeps the UI independent from a future ConPTY renderer.</summary>
public interface ITerminalService : IAsyncDisposable
{
    IReadOnlyList<ShellProfile> DiscoverProfiles();
    Task<TerminalSession> StartAsync(ShellProfile profile, string workingDirectory, CancellationToken cancellationToken = default);
    Task StopAsync(TerminalSession session);
}

public sealed class TerminalSession : IAsyncDisposable
{
    private readonly Process? _process;
    // T4: 环形 char[] 输出缓冲(替代 StringBuilder + Remove(0,n) 的 O(剩余) memmove):
    // 追加 O(1),截断只推进首指针;语义与旧实现一致(保留最近 MaximumCharacters 字符)。
    private readonly char[] _buffer = new char[MaximumCharacters];
    private int _bufferStart;
    private int _bufferLength;
    private readonly SemaphoreSlim _inputLock = new(1, 1);
    private const int MaximumCharacters = 200_000;
    private static readonly TimeSpan OutputPublishInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(1.5);
    private int _disposed;
    private Timer? _outputPublishTimer;
    private ConPty.Instance? _pty;
    private TerminalScreen? _screen;
    private readonly IUiLogService? _log;

    internal TerminalSession(ShellProfile profile, string workingDirectory, Process? process, IUiLogService? log = null)
    {
        Id = Guid.NewGuid();
        Profile = profile;
        WorkingDirectory = workingDirectory;
        _process = process;
        _log = log;
        State = TerminalSessionState.Running;
    }

    public Guid Id { get; }
    public ShellProfile Profile { get; }
    public string WorkingDirectory { get; }
    public TerminalSessionState State { get; internal set; }

    /// <summary>Actionable start failure. A failed session intentionally has no pseudo-terminal
    /// fallback because redirected stdio cannot faithfully implement interactive shell editing.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Interactive screen model when the session runs on ConPTY; null in redirected mode.</summary>
    public TerminalScreen? Screen => _screen;

    /// <summary>ConPTY 启动失败回退到重定向模式的原因(重定向会话无屏幕回显,必须可见化)。</summary>
    public string? StartupWarning { get; internal set; }

    public bool IsInteractive => _pty is not null;

    public bool CanAcceptInput => State == TerminalSessionState.Running && IsInteractive;

    internal static TerminalSession CreateFailed(ShellProfile profile, string workingDirectory, string reason, IUiLogService? log = null)
    {
        var session = new TerminalSession(profile, workingDirectory, null, log);
        session.State = TerminalSessionState.Failed;
        session.FailureReason = reason;
        session.StartupWarning = reason;
        return session;
    }

    /// <summary>Plain text of the session output (screen + scrollback in ConPTY mode, the captured
    /// buffer in redirected mode) for copy operations.</summary>
    public string Output => _screen is not null ? _screen.ToPlainText() : BufferText();

    public event EventHandler? OutputChanged;
    public event EventHandler? Exited;

    /// <summary>Attaches an active pseudo console (host streams). The screen is created with a
    /// theme-token palette for the base 16 ANSI colors and the standard table beyond.</summary>
    internal void AttachPseudoConsole(ConPty.Instance pty, int columns, int rows)
    {
        _pty = pty;
        _screen = CreateScreen(columns, rows);
    }

    internal void AttachFallbackScreen(int columns, int rows)
    {
        _screen = CreateScreen(columns, rows);
    }

    private static TerminalScreen CreateScreen(int columns, int rows) => new(columns, rows, 2000, index =>
    {
        if (index >= 0 && index < 16 && Application.Current?.TryFindResource(AnsiParser.BaseColorToken(index)) is SolidColorBrush brush)
        {
            return unchecked((int)0xFF000000) | (brush.Color.R << 16) | (brush.Color.G << 8) | brush.Color.B;
        }

        return AnsiPalette.ToArgb(index);
    });

    /// <summary>Propagates a terminal size change to the model and the pseudo console.</summary>
    public void Resize(int columns, int rows)
    {
        _screen?.Resize(columns, rows);
        _pty?.Resize(columns, rows);
    }

    /// <summary>Fire-and-forget convenience for keyboard input (control keys, paste).</summary>
    public void WriteTextAsync(string text) => _ = WriteAsync(text);

    internal async Task PumpAsync(StreamReader reader)
    {
        var chars = new char[2048];
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(chars.AsMemory());
                if (count == 0) break;
                var text = new string(chars, 0, count);
                lock (_buffer)
                {
                    AppendToBuffer(text);
                }

                // 嵌入式回退模式同样把输出喂给屏幕模型, 批量提交避免每字符触发渲染导致卡顿
                if (_screen is not null)
                {
                    try
                    {
                        _screen.FeedTextBatch(text);
                    }
                    catch (Exception exception)
                    {
                        // 与 PumpPtyAsync 同策略:单块解析失败不得杀死输出泵(终端冻结)。
                        _log?.Write("WARNING", $"终端屏幕解析单块失败(已跳过该块渲染): {exception.Message}");
                    }
                }

                ScheduleOutputChanged();
            }
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>T4: 环形缓冲追加(空间不足时从最旧字符开始截断)。</summary>
    private void AppendToBuffer(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (text.Length >= _buffer.Length)
        {
            // 整块超过容量:只保留最尾段。
            text.AsSpan(text.Length - _buffer.Length).CopyTo(_buffer);
            _bufferStart = 0;
            _bufferLength = _buffer.Length;
            return;
        }

        var writeStart = (_bufferStart + _bufferLength) % _buffer.Length;
        var first = Math.Min(text.Length, _buffer.Length - writeStart);
        text.AsSpan(0, first).CopyTo(_buffer.AsSpan(writeStart, first));
        if (text.Length > first)
        {
            text.AsSpan(first).CopyTo(_buffer);
        }

        _bufferLength += text.Length;
        if (_bufferLength > _buffer.Length)
        {
            var drop = _bufferLength - _buffer.Length;
            _bufferStart = (_bufferStart + drop) % _buffer.Length;
            _bufferLength = _buffer.Length;
        }
    }

    private string BufferText()
    {
        if (_bufferLength == 0)
        {
            return string.Empty;
        }

        var text = new char[_bufferLength];
        var first = Math.Min(_bufferLength, _buffer.Length - _bufferStart);
        Array.Copy(_buffer, _bufferStart, text, 0, first);
        if (_bufferLength > first)
        {
            Array.Copy(_buffer, 0, text, first, _bufferLength - first);
        }

        return new string(text);
    }

    /// <summary>Pumps pseudo-console output into the screen model (decoded as UTF-8).
    /// <paramref name="onFirstChunk"/> fires once when the first output chunk arrives — the service
    /// uses it as the "ConPTY 可用性判定"(a silently-dead pty never delivers a chunk).</summary>
    internal async Task PumpPtyAsync(StreamReader reader, Action? onFirstChunk = null)
    {
        var chars = new char[4096];
        // T4: 10ms 时间预算内可连续解析整块;超预算让出一次(UI 线程有呼吸空间,xterm.js
        // WriteBuffer 时间预算切片思路)。预算只影响解析节奏,不丢任何输出。
        var budget = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(chars.AsMemory());
                if (count == 0) break;
                var text = new string(chars, 0, count);
                if (_screen is not null)
                {
                    try
                    {
                        _screen.FeedTextBatch(text);
                    }
                    catch (Exception exception)
                    {
                        // 解析单块失败只丢这一块的屏显渲染,绝不能让泵任务死亡——泵一死
                        // 终端就永久冻结(输入仍发给 shell,但一切回显/历史召回都不可见,
                        // 表现为"方向键不能回到历史"),且再无任何错误可见。
                        _log?.Write("WARNING", $"终端屏幕解析单块失败(已跳过该块渲染): {exception.Message}");
                    }
                }
                else
                {
                    _screen?.FeedText(text);
                }

                onFirstChunk?.Invoke();
                if (budget.ElapsedMilliseconds >= 10)
                {
                    budget.Restart();
                    await Task.Yield();
                }
            }
        }
        catch (ObjectDisposedException) { }
    }

    internal void MarkExited()
    {
        State = TerminalSessionState.Exited;
        Exited?.Invoke(this, EventArgs.Empty);
    }

    public async Task WriteAsync(string text)
    {
        if (!CanAcceptInput || string.IsNullOrEmpty(text)) return;
        await _inputLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!CanAcceptInput || _pty is null) return;
            await _pty.Input.WriteAsync(Encoding.UTF8.GetBytes(text)).ConfigureAwait(false);
            await _pty.Input.FlushAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        finally
        {
            _inputLock.Release();
        }
    }

    public async Task WriteBytesAsync(byte[] bytes)
    {
        await WriteAsync(Encoding.UTF8.GetString(bytes));
    }

    public void Clear()
    {
        lock (_buffer)
        {
            _bufferStart = 0;
            _bufferLength = 0;
        }

        _screen?.Clear();
        PublishOutputChangedNow();
    }

    /// <summary>Coalesces redirected process chunks into roughly 50 UI notifications per second.
    /// The buffer and terminal screen continue receiving every chunk; only the repaint signal is
    /// rate limited.</summary>
    private void ScheduleOutputChanged()
    {
        if (OutputChanged is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var timer = _outputPublishTimer ??= new Timer(
            static state => ((TerminalSession)state!).PublishOutputChanged(), this,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        timer.Change(OutputPublishInterval, Timeout.InfiniteTimeSpan);
    }

    private void PublishOutputChanged()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            OutputChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void PublishOutputChangedNow()
    {
        _outputPublishTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        PublishOutputChanged();
    }

    public async ValueTask DisposeAsync()
    {
        // Dispose can be reached both from the terminal tab and from the application shutdown
        // path. Make it idempotent so those paths cannot race while tearing down the same PTY.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _outputPublishTimer?.Dispose();
        _outputPublishTimer = null;

        _pty?.Dispose();
        _pty = null;
        var process = _process;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }

                    // A shell can leave a child attached to the pseudo console and never signal
                    // Process.Exited promptly. Never let that hold the WPF shutdown watchdog.
                    using var timeout = new CancellationTokenSource(ProcessExitTimeout);
                    try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            finally
            {
                process.Dispose();
            }
        }


    }
}

public sealed class TerminalService : ITerminalService
{
    private const int DefaultColumns = 120;
    private const int DefaultRows = 30;

    /// <summary>ConPTY 优先:交互式真实终端(回显/光标/提示符由外壳驱动)。设为 false 可整体禁用。
    /// 在个别环境(如无 ConHost/ConDrv 的受限会话)ConPTY 会“启动成功但零输出”——用首字节
    /// 判定(<see cref="PtyVerdictTimeout"/>)自动回退到带屏的重定向模式,保证嵌入式外观兜底。</summary>
    private const bool TryConPtyFirst = true;

    /// <summary>ConPTY 会话“可用性判定”窗口:首字节在这个时间内到达视为交互式可用。</summary>
    private static readonly TimeSpan PtyVerdictTimeout = TimeSpan.FromSeconds(2);

    private readonly List<TerminalSession> _sessions = [];
    private readonly IUiLogService? _log;
    private int _disposed;

    public TerminalService(IUiLogService? log = null)
    {
        _log = log;
    }

    public IReadOnlyList<ShellProfile> DiscoverProfiles()
    {
        var profiles = new List<ShellProfile>();
        AddIfFound(profiles, "pwsh", "PowerShell 7", "pwsh.exe", "-NoLogo");
        AddIfFound(profiles, "powershell", "Windows PowerShell", "powershell.exe", "-NoLogo");
        AddIfFound(profiles, "cmd", "命令提示符", "cmd.exe");
        AddIfFound(profiles, "wsl", "WSL", "wsl.exe");
        var gitBash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (File.Exists(gitBash)) profiles.Add(new ShellProfile("git-bash", "Git Bash", gitBash, "--login -i"));
        return profiles.Count > 0 ? profiles : [new ShellProfile("cmd", "命令提示符", "cmd.exe")];
    }

    public async Task<TerminalSession> StartAsync(ShellProfile profile, string workingDirectory, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var workDir = Directory.Exists(workingDirectory) ? workingDirectory : Environment.CurrentDirectory;

        // A redirected stdin/stdout process is not an interactive terminal: it cannot correctly
        // support PSReadLine, cursor keys, or full-screen applications. Like VS Code, use ConPTY
        // for supported Windows versions and expose an actionable failure otherwise.
        string ptyError = string.Empty;
        if (TryConPtyFirst && ConPty.TryStartAttached(profile.Executable, profile.Arguments, workDir, DefaultColumns, DefaultRows, out var pty, out var attachedProcess, out ptyError))
        {
            var session = new TerminalSession(profile, workDir, attachedProcess!, _log);
            session.AttachPseudoConsole(pty!, DefaultColumns, DefaultRows);
            _sessions.Add(session);
            var output = pty!.Output;
            var firstOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(async () =>
            {
                try
                {
                    using var reader = new StreamReader(output, Encoding.UTF8, true, 4096, leaveOpen: false);
                    await session.PumpPtyAsync(reader, () => firstOutput.TrySetResult());
                }
                catch (Exception exception)
                {
                    // 泵任务必须被观察:否则退出期的 IO 异常会以 UnobservedTaskException 形式
                    // 落到终结器线程。正常退出路径(reader 结束/Disposed)已在泵内吞掉。
                    _log?.Write("WARNING", $"终端输出泵结束: {exception.Message}");
                }
            });
            try { attachedProcess!.EnableRaisingEvents = true; } catch (InvalidOperationException) { session.MarkExited(); }
            attachedProcess!.Exited += (_, _) => session.MarkExited();
            if (attachedProcess!.HasExited) session.MarkExited();

            // 可用性判定:首字节超时判定为伪控制台失效(常见于受限/虚拟化环境,子进程逃逸到
            // 父控制台表现为零输出),丢弃该会话并落到重定向分支,而不是让用户面对一个僵死壳。
            var verdict = await Task.WhenAny(firstOutput.Task, Task.Delay(PtyVerdictTimeout, cancellationToken));
            if (verdict == firstOutput.Task && session.State == TerminalSessionState.Running)
            {
                return session;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                await session.DisposeAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }

            var reason = session.State == TerminalSessionState.Exited
                ? "Shell 在建立伪终端后立即退出。"
                : $"ConPTY 在 {PtyVerdictTimeout.TotalSeconds:0} 秒内没有收到输出。";
            _log?.Write("WARNING", $"{profile.Name} {reason}");
            _sessions.Remove(session);
            await session.DisposeAsync();
            return CreateFailedSession(profile, workDir, reason);
        }

        var unavailableReason = string.IsNullOrWhiteSpace(ptyError)
            ? "此系统无法创建 ConPTY 伪终端。"
            : $"ConPTY 不可用：{ptyError}";
        _log?.Write("WARNING", $"{profile.Name} {unavailableReason}");
        return CreateFailedSession(profile, workDir, unavailableReason);
    }

    public async Task StopAsync(TerminalSession session)
    {
        _sessions.Remove(session);
        await session.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        var sessions = _sessions.ToArray();
        _sessions.Clear();

        // Closing several terminals serially multiplied each process wait and was the main
        // reason the application's five-second shutdown watchdog fired. Dispose independently
        // so one broken shell cannot delay the remaining sessions.
        await Task.WhenAll(sessions.Select(async session =>
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _log?.Write("WARNING", $"终端会话释放失败：{exception.Message}");
            }
        })).ConfigureAwait(false);
    }

    private static void AddIfFound(List<ShellProfile> profiles, string id, string name, string executable, string arguments = "")
    {
        var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, executable)).FirstOrDefault(File.Exists);
        if (path is not null) profiles.Add(new ShellProfile(id, name, path, arguments));
    }

    private TerminalSession CreateFailedSession(ShellProfile profile, string workingDirectory, string reason)
    {
        var session = TerminalSession.CreateFailed(profile, workingDirectory, reason, _log);
        _sessions.Add(session);
        return session;
    }
}
