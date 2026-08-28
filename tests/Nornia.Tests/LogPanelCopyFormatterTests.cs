using Nornia.Desktop.Services;

namespace Nornia.Tests;

public sealed class LogPanelCopyFormatterTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 8, 21, 12, 34, 56, TimeSpan.FromHours(8));
    private static readonly Guid CorrelationId = Guid.Parse("abcdef01-1234-5678-9abc-def012345678");

    [Fact]
    public void FormatRows_SingleEntryKeepsVisiblePrefix()
    {
        var entry = new UiLogEntry(Timestamp, "ERROR", "boom", CorrelationId);

        Assert.Equal("12:34:56 [ERROR] [abcdef01] boom", LogPanelCopyFormatter.FormatRows([entry]));
    }

    [Fact]
    public void FormatMessages_OmitsTimestampLevelAndCorrelationPrefix()
    {
        var entry = new UiLogEntry(Timestamp, "WARNING", "watch-out", CorrelationId);

        Assert.Equal("watch-out", LogPanelCopyFormatter.FormatMessages([entry]));
    }

    [Fact]
    public void FormatRows_MultipleEntriesJoinInOrderWithoutTrailingNewline()
    {
        var first = new UiLogEntry(Timestamp, "ERROR", "boom");
        var second = new UiLogEntry(Timestamp, "INFO", "done");

        var text = LogPanelCopyFormatter.FormatRows([first, second]);

        Assert.Equal(
            $"12:34:56 [ERROR] boom{Environment.NewLine}12:34:56 [INFO] done",
            text);
    }

    [Fact]
    public void EmptyEntriesReturnEmptyString()
    {
        Assert.Equal(string.Empty, LogPanelCopyFormatter.FormatRows([]));
        Assert.Equal(string.Empty, LogPanelCopyFormatter.FormatMessages([]));
    }
}
