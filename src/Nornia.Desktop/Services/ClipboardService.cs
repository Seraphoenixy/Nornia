using System.Windows;

namespace Nornia.Desktop.Services;

/// <summary>Writes text to the system clipboard (abstracted so view-model copy commands stay testable).</summary>
public interface IClipboardService
{
    void SetText(string text);
}

public sealed class WpfClipboardService : IClipboardService
{
    public void SetText(string text) => Clipboard.SetText(text);
}

/// <summary>No-op clipboard used by legacy constructor overloads so copy commands stay safe in
/// test harnesses that do not resolve the clipboard service.</summary>
internal sealed class NullClipboardService : IClipboardService
{
    public static readonly NullClipboardService Instance = new();

    private NullClipboardService()
    {
    }

    public void SetText(string text)
    {
    }
}

/// <summary>Builds the clipboard payloads for the OUTPUT/PROBLEMS panels. "Copy all" reuses
/// <see cref="FormatRows"/> with the panel's full entry list, matching VS Code's Copy All.</summary>
public static class LogPanelCopyFormatter
{
    public static string FormatRows(IEnumerable<UiLogEntry> entries) =>
        string.Join(Environment.NewLine, entries.Select(entry => entry.DisplayText));

    public static string FormatMessages(IEnumerable<UiLogEntry> entries) =>
        string.Join(Environment.NewLine, entries.Select(entry => entry.Message));
}
