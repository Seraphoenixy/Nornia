using Nornia.Core;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Core.Collections;
using Serilog;
using Serilog.Events;
using System.Collections.ObjectModel;

namespace Nornia.Desktop.Services;

public interface IUiLogService
{
    ReadOnlyObservableCollection<UiLogEntry> Entries { get; }
    void Write(string level, string message, Guid? correlationId = null);
    void WriteException(string level, string message, Exception exception, Guid? correlationId = null);
    IProgress<ProcessOutput> CreateProcessProgress(Guid? correlationId = null);
    void Clear();
}

public sealed record UiLogEntry(DateTimeOffset Timestamp, string Level, string Message, Guid? CorrelationId = null)
{
    public string DisplayText => $"{Timestamp:HH:mm:ss} [{Level}] {(CorrelationId is null ? string.Empty : $"[{CorrelationId.Value.ToString("N")[..8]}] ")}{Message}";
}

public sealed class UiLogService : IUiLogService
{
    private readonly IUiDispatcher _dispatcher;
    private readonly BulkObservableCollection<UiLogEntry> _entries = [];

    public UiLogService()
        : this(new WpfUiDispatcher())
    {
    }

    public UiLogService(IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Entries = new ReadOnlyObservableCollection<UiLogEntry>(_entries);
    }

    public ReadOnlyObservableCollection<UiLogEntry> Entries { get; }

    // 应用日志只走两条路:Serilog 滚动文件(可追溯)与 Output 面板(内存,当前会话)。
    public void Write(string level, string message, Guid? correlationId = null)
    {
        Log.Write(ToLogLevel(level), "[{Level}] {Message}", level, message);
        AddEntryOrDispatch(level, message, correlationId);
    }

    public void WriteException(string level, string message, Exception exception, Guid? correlationId = null)
    {
        var displayMessage = ExceptionDiagnosticFormatter.Format(message, exception);
        Log.Write(ToLogLevel(level), exception, "[{Level}] {Message}", level, message);
        AddEntryOrDispatch(level, displayMessage, correlationId);
    }

    private void AddEntryOrDispatch(string level, string message, Guid? correlationId)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => AddEntry(level, message, correlationId));
            return;
        }

        AddEntry(level, message, correlationId);
    }

    public IProgress<ProcessOutput> CreateProcessProgress(Guid? correlationId = null) =>
        // 子进程输出的逐行标准输出(如 winget list 每包一行、缓存扫描每候选一行)是过程噪声:
        // 只把错误行写入日志,普通 stdout 不再逐行刷屏(操作终态由 RunAsync 汇总行承载)。
        new Progress<ProcessOutput>(output =>
        {
            if (output.IsError)
            {
                Write("ERROR", output.Text, correlationId);
            }
        });

    public void Clear()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(_entries.Clear);
            return;
        }

        _entries.Clear();
    }

    private void AddEntry(string level, string message, Guid? correlationId)
    {
        _entries.Add(new UiLogEntry(DateTimeOffset.Now, level.ToUpperInvariant(), message, correlationId));
        var overflow = _entries.Count - NorniaSettings.UiLogMaximumEntries;
        if (overflow > 0)
        {
            // Removing one item at a time shifts the whole list and emits one notification per
            // item. Trim the ring in one logical update when a burst crosses the cap.
            _entries.RemoveFirst(overflow);
        }
    }

    private static LogEventLevel ToLogLevel(string level) => level.ToUpperInvariant() switch
    {
        "ERROR" => LogEventLevel.Error,
        "WARNING" or "WARN" => LogEventLevel.Warning,
        "DEBUG" => LogEventLevel.Debug,
        "VERBOSE" => LogEventLevel.Verbose,
        _ => LogEventLevel.Information
    };
}

internal static class ExceptionDiagnosticFormatter
{
    public static string Format(string message, Exception exception)
    {
        if (exception is EnvironmentRepairException repair)
        {
            var lines = new List<string> { message, $"失败步骤：{repair.Failures.Count}（correlation_id={repair.CorrelationId:N}）" };
            lines.AddRange(repair.Failures.Select(failure =>
                $"  [{failure.Sequence}/{failure.Total}] {failure.Component} / {failure.PackageId}：{failure.FailureReason}"));
            return string.Join(Environment.NewLine, lines);
        }

        var details = new List<string>
        {
            message,
            $"异常类型：{exception.GetType().FullName}",
            $"原因：{exception.Message}"
        };
        if (exception.InnerException is not null)
        {
            details.Add($"内部原因：{exception.InnerException.Message}");
        }
        details.Add("建议：查看 Output 中的完整上下文后重试。");
        return string.Join(Environment.NewLine, details);
    }
}
