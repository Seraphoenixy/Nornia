using Nornia.Desktop.Services;
using System.Windows.Media;

namespace Nornia.Tests;

/// <summary>Pure accent-resolving logic: preset mapping, hex parsing, shading and the null-safe
/// apply path (no WPF Application in unit tests).</summary>
public sealed class ThemeFactoryTests
{
    [Fact]
    public void ResolveAccent_DefaultPresetReturnsNull()
    {
        Assert.Null(ThemeFactory.ResolveAccent("Default"));
    }

    [Theory]
    [InlineData("Teal")]
    [InlineData("Iris")]
    public void ResolveAccent_PresetsReturnColor(string accent)
    {
        var color = ThemeFactory.ResolveAccent(accent);
        Assert.NotNull(color);
        Assert.NotEqual(Colors.Transparent, color.Value);
    }

    [Theory]
    [InlineData("#7C5CFF", 0x7C, 0x5C, 0xFF)]
    [InlineData("00B294", 0x00, 0xB2, 0x94)]
    public void TryParseHex_ParsesRgb(string text, byte r, byte g, byte b)
    {
        Assert.True(ThemeFactory.TryParseHex(text, out var color));
        Assert.Equal(Color.FromRgb(r, g, b), color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12")]
    [InlineData("not-a-color")]
    [InlineData("1234567")]
    public void TryParseHex_RejectsMalformedInput(string text)
    {
        Assert.False(ThemeFactory.TryParseHex(text, out _));
    }

    [Fact]
    public void Darken_ScalesEachChannel()
    {
        var darkened = ThemeFactory.Darken(Color.FromRgb(0x80, 0x40, 0x20), 0.5);
        Assert.Equal(Color.FromRgb(0x40, 0x20, 0x10), darkened);
    }

    [Fact]
    public void ApplyAccent_WithoutWpfApplicationIsNoOp()
    {
        // Unit tests run without Application.Current; applying must not throw and simply return.
        ThemeFactory.ApplyAccent("Iris");
    }

    [Fact]
    public void AccentFamilyKeys_OnlyTargetAccentBrushes()
    {
        // The overlay must never redefine text/surface brushes (contrast containment).
        Assert.All(ThemeFactory.AccentFamilyKeys, key => Assert.NotEqual("TextBrush", key));
        Assert.Contains("ButtonBrush", ThemeFactory.AccentFamilyKeys);
        Assert.Contains("TabActiveBorderTopBrush", ThemeFactory.AccentFamilyKeys);
    }
}