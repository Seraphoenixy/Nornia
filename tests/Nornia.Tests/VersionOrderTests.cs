using Nornia.Core.Models;
using CoreRuntime = Nornia.Core.Models.Runtime;

namespace Nornia.Tests;

/// <summary>版本号排序。这些用例锁住的是真实事故:字典序下 "9.0.1" 大于 "10.0.0",
/// 「取最新已安装版本」会选中错误的实例,进而把修复计划的目标版本算错。</summary>
public sealed class VersionOrderTests
{
    [Theory]
    [InlineData("9.0.1", "10.0.0", -1)]
    [InlineData("10.0.0", "9.0.1", 1)]
    [InlineData("3.9.1", "3.13.2", -1)]
    [InlineData("22.0.0", "22.11.0", -1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    public void Compare_UsesNumericOrderingNotLexicographic(string left, string right, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(VersionOrder.Compare(left, right)));
    }

    [Fact]
    public void Compare_RejectsTheLexicographicResultItReplaces()
    {
        // 直接对比:字典序会给出相反答案,数值序才是正确的。
        Assert.True(string.CompareOrdinal("9.0.1", "10.0.0") > 0);
        Assert.True(VersionOrder.Compare("9.0.1", "10.0.0") < 0);
    }

    [Fact]
    public void Compare_PrereleaseRanksBelowStable()
    {
        Assert.True(VersionOrder.Compare("10.0.100-preview.1", "10.0.100") < 0);
        Assert.True(VersionOrder.Compare("10.0.100", "10.0.100-preview.1") > 0);
    }

    [Fact]
    public void Compare_UnparsablePlaceholdersRankLast()
    {
        // "unknown" 之类的占位值不得篡夺「最新版本」的位置 —— 它们同样会流进回滚的 previous_version。
        Assert.True(VersionOrder.Compare("unknown", "1.0.0") < 0);
        Assert.True(VersionOrder.Compare("1.0.0", "unknown") > 0);
        Assert.True(VersionOrder.Compare(null, "1.0.0") < 0);
    }

    [Fact]
    public void Compare_ToleratesVPrefixAndRuntimeSuffix()
    {
        Assert.True(VersionOrder.Compare("v22.0.0", "v23.0.0") < 0);
        Assert.Equal(0, VersionOrder.Compare("2.50.1.windows.1", "2.50.1"));
        Assert.Equal(0, VersionOrder.Compare("v20.10.0", "20.10.0"));
    }

    [Fact]
    public void MaxByVersion_PicksNewestAcrossMajorBoundary()
    {
        IReadOnlyList<CoreRuntime> runtimes =
        [
            CreateRuntime("9.0.1"),
            CreateRuntime("10.0.302"),
            CreateRuntime("8.0.100")
        ];

        Assert.Equal("10.0.302", runtimes.MaxByVersion(runtime => runtime.Version)!.Version);
    }

    [Fact]
    public void MaxByVersion_PrefersStableOverPrerelease()
    {
        IReadOnlyList<CoreRuntime> runtimes =
        [
            CreateRuntime("10.0.100-preview.1"),
            CreateRuntime("10.0.100")
        ];

        Assert.Equal("10.0.100", runtimes.MaxByVersion(runtime => runtime.Version)!.Version);
    }

    [Fact]
    public void MaxByVersion_EmptySequenceReturnsDefault()
    {
        Assert.Null(Array.Empty<CoreRuntime>().MaxByVersion(runtime => runtime.Version));
    }

    private static CoreRuntime CreateRuntime(string version) => new(
        Guid.NewGuid(),
        "Visual C++ Redistributable",
        version,
        @"C:\Windows\System32",
        "x64",
        "registry",
        0,
        RuntimeStatus.Installed);
}
