using CommunityToolkit.Mvvm.ComponentModel;
using Nornia.Desktop.Localization;
using Nornia.Desktop.Services;
using Nornia.Core.Models;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Nornia.Desktop.ViewModels;

public abstract partial class PageViewModel(string title, IUiLogService logService) : ObservableObject
{
    private bool _activated;
    private readonly SemaphoreSlim _activationGate = new(1, 1);
    private CancellationTokenSource? _operationCancellation;
    private readonly IUiDispatcher _uiDispatcher = new WpfUiDispatcher();
    private OperationProgressSink? _operationProgress;
    private int _statusToken;

    public string Title { get; } = title;
    protected IUiLogService LogService { get; } = logService;

    /// <summary>VS Code-style secondary left sidebar content. Pages that have a sidebar return
    /// themselves (or a dedicated sidebar view model); null hides the sidebar column.</summary>
    public virtual object? Sidebar => null;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private StatusKind statusKind = StatusKind.Info;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelOperationCommand))]
    private OperationState? currentOperation;

    [ObservableProperty]
    private OperationResult? lastOperationResult;

    public bool HasOperation => CurrentOperation is not null;
    protected IProgress<ProcessOutput> OperationProgress
    {
        get
        {
            var operation = CurrentOperation;
            if (operation is null)
            {
                var logProgress = LogService.CreateProcessProgress(null);
                return new Progress<ProcessOutput>(logProgress.Report);
            }

            if (_operationProgress is null || !ReferenceEquals(_operationProgress.Operation, operation))
            {
                _operationProgress = new OperationProgressSink(
                    operation,
                    _uiDispatcher,
                    LogService.CreateProcessProgress(operation.CorrelationId),
                    ApplyOperationProgress);
            }

            return _operationProgress;
        }
    }

    public async Task ActivateAsync()
    {
        await _activationGate.WaitAsync();
        try
        {
            if (_activated)
            {
                return;
            }

            // Publish the one-time flag only after initialization succeeds. A transient
            // startup failure must leave the page retryable, while the gate prevents two
            // concurrent activation callers from running initialization twice.
            await OnFirstActivatedAsync();
            _activated = true;
        }
        finally
        {
            _activationGate.Release();
        }
    }

    protected virtual Task OnFirstActivatedAsync() => Task.CompletedTask;

    protected Task<bool> RunAsync(string operation, Func<Task> action, string recommendedNextStep = "") =>
        RunAsync(operation, _ => action(), recommendedNextStep, canCancel: false);

    protected async Task<bool> RunAsync(
        string operation,
        Func<CancellationToken, Task> action,
        string recommendedNextStep = "",
        bool canCancel = true,
        Func<string>? successMessageFactory = null)
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        _operationCancellation = new CancellationTokenSource();
        var correlationId = Guid.NewGuid();
        CurrentOperation = new OperationState(operation, correlationId, canCancel)
        {
            Detail = $"{operation}正在进行…",
            RecommendedNextStep = recommendedNextStep
        };
        _operationProgress = null;
        OnPropertyChanged(nameof(HasOperation));
        _statusToken++;
        StatusKind = StatusKind.Info;
        StatusMessage = $"{operation}…";
        // 过程信息降噪:开始时的“XXX…”不再写入 Output(状态栏仍即时反馈),只保留
        // 完成/取消/失败等终态,减少刷新、扫描等重复操作刷屏。
        try
        {
            await action(_operationCancellation.Token);
            await FlushOperationProgressAsync();
            StatusMessage = successMessageFactory?.Invoke() ?? $"{operation}完成";
            CurrentOperation.Phase = OperationPhase.Succeeded;
            CurrentOperation.Detail = StatusMessage;
            LastOperationResult = new(correlationId, operation, OperationPhase.Succeeded, StatusMessage, recommendedNextStep);
            LogService.Write("INFO", StatusMessage, correlationId);
            StatusKind = StatusKind.Success;
            ScheduleStatusClear();
            return true;
        }
        catch (OperationCanceledException) when (_operationCancellation.IsCancellationRequested)
        {
            await FlushOperationProgressAsync();
            StatusMessage = $"{operation}已取消";
            CurrentOperation.Phase = OperationPhase.Cancelled;
            CurrentOperation.Detail = StatusMessage;
            LastOperationResult = new(correlationId, operation, OperationPhase.Cancelled, StatusMessage, recommendedNextStep);
            LogService.Write("WARNING", StatusMessage, correlationId);
            StatusKind = StatusKind.Warning;
            ScheduleStatusClear();
            return false;
        }
        catch (Exception ex)
        {
            await FlushOperationProgressAsync();
            var shortMessage = ex.Message.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                ?? "未知错误";
            StatusMessage = $"{operation}失败：{shortMessage}";
            CurrentOperation.Phase = OperationPhase.Failed;
            CurrentOperation.Detail = StatusMessage;
            CurrentOperation.RecommendedNextStep = string.IsNullOrWhiteSpace(recommendedNextStep) ? "查看 Problems 和 Output 后重试。" : recommendedNextStep;
            LastOperationResult = new(correlationId, operation, OperationPhase.Failed, StatusMessage, CurrentOperation.RecommendedNextStep);
            LogService.WriteException("ERROR", StatusMessage, ex, correlationId);
            StatusKind = StatusKind.Error;
            ScheduleStatusClear();
            return false;
        }
        finally
        {
            if (CurrentOperation is not null) CurrentOperation.CanCancel = false;
            _operationCancellation.Dispose();
            _operationCancellation = null;
            IsBusy = false;
        }
    }

    private void ApplyOperationProgress(OperationState operation, double percentage)
    {
        if (!ReferenceEquals(CurrentOperation, operation) || operation.Phase != OperationPhase.Running)
        {
            return;
        }

        // stdout and stderr are drained concurrently. A late frame from the other stream must
        // not make the visible bar move backwards.
        if (operation.Progress is { } previous && percentage < previous)
        {
            return;
        }

        operation.Progress = percentage;
        operation.Detail = $"{operation.Title} {operation.ProgressDisplay}";
        StatusMessage = operation.Detail;
    }

    private async Task FlushOperationProgressAsync()
    {
        if (_operationProgress is null)
        {
            return;
        }

        try
        {
            // Progress reports from ProcessRunner arrive on background stream readers. The final
            // operation state can otherwise be published before the queued UI callback runs,
            // making the last download percentage disappear. Flush the latest value while the
            // operation is still Running, immediately before publishing its terminal state.
            await _operationProgress.FlushAsync();
        }
        catch (Exception exception)
        {
            LogService.Write("WARNING", $"刷新软件包下载进度失败：{exception.Message}");
        }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(CanCancelOperation))]
    private void CancelOperation() => _operationCancellation?.Cancel();

    private bool CanCancelOperation() => IsBusy && CurrentOperation?.CanCancel == true;

    // Clears the status-bar feedback a few seconds after a terminal state, unless a newer
    // operation has started (guarded by _statusToken). Resumes on the UI sync context.
    private void ScheduleStatusClear(int delayMs = 4000) => _ = ScheduleStatusClearAsync(delayMs);

    private async Task ScheduleStatusClearAsync(int delayMs)
    {
        try
        {
            var token = _statusToken;
            await Task.Delay(delayMs);
            if (token == _statusToken)
            {
                StatusMessage = string.Empty;
                StatusKind = StatusKind.Info;
            }
        }
        catch (Exception ex)
        {
            LogService.Write("WARNING", $"清理页面状态消息失败：{ex.Message}");
        }
    }

    partial void OnIsBusyChanged(bool value) => CancelOperationCommand.NotifyCanExecuteChanged();

    [ObservableProperty]
    private PageState pageState = PageState.Idle;

    protected void SetPageLoading() { PageState = PageState.Loading; }
    protected void SetPageReady() { PageState = PageState.Ready; }
    protected void SetPageError(string message) { PageState = PageState.Error; PageErrorMessage = message; }
    protected void SetPageEmpty(string message = "暂无数据") { PageState = PageState.Empty; PageEmptyMessage = message; }

    [ObservableProperty]
    private string pageErrorMessage = string.Empty;

    [ObservableProperty]
    private string pageEmptyMessage = "暂无数据";
}

public enum PageState
{
    Idle,
    Loading,
    Ready,
    Error,
    Empty
}

public enum OperationPhase
{
    Idle,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>Semantic kind of the transient status-bar feedback (U2): drives color + glyph.</summary>
public enum StatusKind
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>Receives process output without losing the last progress frame when the process exits
/// before a queued <see cref="Progress{T}"/> callback reaches the UI thread.</summary>
internal sealed class OperationProgressSink(OperationState operation, IUiDispatcher dispatcher,
    IProgress<ProcessOutput> logProgress, Action<OperationState, double> apply) : IProgress<ProcessOutput>
{
    private readonly object _gate = new();
    private double? _latestPercentage;

    public OperationState Operation { get; } = operation;

    public void Report(ProcessOutput value)
    {
        // Keep the existing output/log path for diagnostics. Percentage extraction is repeated
        // synchronously below so a fast process cannot finish before its final UI update is known.
        logProgress.Report(value);
        if (!OperationProgressParser.TryGetPercentage(value.Text, out var percentage))
        {
            return;
        }

        lock (_gate)
        {
            if (_latestPercentage is null || percentage > _latestPercentage.Value)
            {
                _latestPercentage = percentage;
            }
        }

        if (dispatcher.CheckAccess())
        {
            ApplyLatest();
            return;
        }

        try
        {
            dispatcher.BeginInvoke(ApplyLatest);
        }
        catch
        {
            // The dispatcher may be shutting down. FlushAsync will make one final best effort,
            // while the process operation itself still owns the original failure/cancellation.
        }
    }

    public Task FlushAsync() => dispatcher.InvokeAsync(ApplyLatest);

    private void ApplyLatest()
    {
        double? percentage;
        lock (_gate)
        {
            percentage = _latestPercentage;
        }

        if (percentage is { } value)
        {
            apply(Operation, value);
        }
    }
}

public partial class OperationState : ObservableObject
{
    public OperationState(string title, Guid correlationId, bool canCancel)
    {
        Title = title;
        CorrelationId = correlationId;
        CanCancel = canCancel;
    }

    public string Title { get; }
    public Guid CorrelationId { get; }
    public string LogReference => CorrelationId.ToString("N")[..8];
    public string LogReferenceDisplay => Loc.Format("Op_LogRef", LogReference);

    [ObservableProperty] private OperationPhase phase = OperationPhase.Running;
    [ObservableProperty] private string detail = string.Empty;
    /// <summary>Latest parsed process percentage in the 0-100 range; null means indeterminate.</summary>
    [ObservableProperty] private double? progress;
    [ObservableProperty] private bool canCancel;
    [ObservableProperty] private string recommendedNextStep = string.Empty;

    public bool HasProgress => Progress is not null;
    public string ProgressDisplay => Progress is { } value
        ? $"{value.ToString("0.#", CultureInfo.CurrentCulture)}%"
        : string.Empty;

    partial void OnProgressChanged(double? value)
    {
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(ProgressDisplay));
    }
}

/// <summary>Extracts percentages from package-manager output. In addition to human-readable
/// percentages, winget emits OSC 9;4 virtual-terminal progress frames. Package managers use both
/// integer and decimal percentages, and winget may emit several carriage-return updates in one
/// line, so the last valid match is the current progress value.</summary>
internal static class OperationProgressParser
{
    private static readonly Regex PercentagePattern = new(
        @"(?<!\d)(?<value>\d{1,3}(?:[.,]\d+)?)\s*%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WingetVirtualTerminalProgressPattern = new(
        @"\x1B\]9;4;(?<state>[124]);(?<value>\d{1,3})(?:\x07|\x1B\\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryGetPercentage(string? text, out double percentage)
    {
        percentage = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Some localized installers use the full-width percent sign. Normalize it before
        // applying the invariant numeric parser so the UI behaves the same in Chinese locales.
        var normalized = text.Replace('％', '%');
        var candidates = new List<(int Index, string Token)>();
        foreach (Match match in WingetVirtualTerminalProgressPattern.Matches(normalized))
        {
            candidates.Add((match.Index, match.Groups["value"].Value));
        }

        foreach (Match match in PercentagePattern.Matches(normalized))
        {
            candidates.Add((match.Index, match.Groups["value"].Value));
        }

        candidates.Sort((left, right) => right.Index.CompareTo(left.Index));
        foreach (var candidate in candidates)
        {
            var token = candidate.Token.Replace(',', '.');
            if (!double.TryParse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                || value is < 0 or > 100)
            {
                continue;
            }

            percentage = value;
            return true;
        }

        return false;
    }
}

public sealed record OperationResult(
    Guid CorrelationId,
    string Title,
    OperationPhase Phase,
    string Detail,
    string RecommendedNextStep);
