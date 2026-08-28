using Nornia.Core;
using Nornia.Desktop.Services;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

public sealed class UiLogServiceTests
{
    [Fact]
    public void Write_WhenCallerOwnsDispatcher_AddsEntryImmediately()
    {
        var service = new UiLogService(new FakeUiDispatcher { AccessGranted = true });

        service.Write("INFO", "hello");

        var entry = Assert.Single(service.Entries);
        Assert.Equal("INFO", entry.Level);
        Assert.Equal("hello", entry.Message);
    }

    [Fact]
    public void Write_FromForeignThread_MarshalsThroughDispatcher()
    {
        var dispatcher = new FakeUiDispatcher { AccessGranted = false };
        var service = new UiLogService(dispatcher);

        service.Write("WARNING", "cross-thread");

        Assert.Empty(service.Entries);
        var marshaled = Assert.Single(dispatcher.Invoked);
        marshaled();
        Assert.Equal("WARNING", Assert.Single(service.Entries).Level);
    }

    [Fact]
    public void AddEntry_TrimsOldestEntriesAtMaximum()
    {
        var service = new UiLogService(new FakeUiDispatcher { AccessGranted = true });

        for (var index = 0; index < NorniaSettings.UiLogMaximumEntries + 10; index++)
        {
            service.Write("INFO", $"entry-{index}");
        }

        Assert.Equal(NorniaSettings.UiLogMaximumEntries, service.Entries.Count);
        Assert.Equal("entry-10", service.Entries[0].Message);
        Assert.Equal($"entry-{NorniaSettings.UiLogMaximumEntries + 9}", service.Entries[^1].Message);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var service = new UiLogService(new FakeUiDispatcher { AccessGranted = true });
        service.Write("INFO", "a");
        service.Write("INFO", "b");

        service.Clear();

        Assert.Empty(service.Entries);
    }

    [Fact]
    public void WriteException_IncludesExceptionContextInUiEntry()
    {
        var service = new UiLogService(new FakeUiDispatcher { AccessGranted = true });

        service.WriteException("ERROR", "检查环境失败", new InvalidOperationException("包管理器不可用"));

        var message = Assert.Single(service.Entries).Message;
        Assert.Contains("异常类型", message);
        Assert.Contains("InvalidOperationException", message);
        Assert.Contains("包管理器不可用", message);
        Assert.Contains("建议", message);
    }
}
