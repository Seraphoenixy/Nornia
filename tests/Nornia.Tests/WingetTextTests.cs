using Nornia.Package.Localization;
using System.Globalization;

namespace Nornia.Tests;

/// <summary>Verifies the package-manager message resources resolve in both supported languages.
/// Culture is passed explicitly so tests stay independent of the ambient UI culture.</summary>
public sealed class WingetTextTests
{
    [Fact]
    public void Messages_ResolveInEnglishAndSimplifiedChinese()
    {
        var english = WingetText.Get("Winget_RetryAfterFailure", CultureInfo.GetCultureInfo("en"));
        var chinese = WingetText.Get("Winget_RetryAfterFailure", CultureInfo.GetCultureInfo("zh-Hans"));

        Assert.StartsWith("Winget operation", english);
        Assert.StartsWith("Winget", chinese);
        Assert.Contains("重试", chinese);
        Assert.NotEqual(english, chinese);
    }

    [Fact]
    public void Format_AppliesArgumentsPerCulture()
    {
        var english = WingetText.Format("Repair_UsingProvider", CultureInfo.GetCultureInfo("en"), "Node.js", "winget");
        var chinese = WingetText.Format("Repair_UsingProvider", CultureInfo.GetCultureInfo("zh-Hans"), "Node.js", "winget");

        Assert.Contains("Node.js", english);
        Assert.Contains("winget", english);
        Assert.Contains("Node.js", chinese);
        Assert.Contains("winget", chinese);
    }

    [Fact]
    public void MissingKey_FallsBackToKeyItself()
    {
        Assert.Equal("No_Such_Key", WingetText.Get("No_Such_Key", CultureInfo.InvariantCulture));
    }
}