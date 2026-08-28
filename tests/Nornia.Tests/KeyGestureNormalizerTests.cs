using Nornia.Desktop.Commands;

namespace Nornia.Tests;

/// <summary>Split gestures (Ctrl+\ / Ctrl+Shift+\) 与既有链式按键 (Ctrl+K Ctrl+S) 的归一化。</summary>
public sealed class KeyGestureNormalizerTests
{
    [Theory]
    [InlineData("ctrl+\\", "ctrl+\\")]
    [InlineData("ctrl+shift+\\", "ctrl+shift+\\")]
    [InlineData("ctrl+k ctrl+s", "ctrl+k ctrl+s")]
    [InlineData("ctrl+b", "ctrl+b")]
    [InlineData("ctrl+oembackslash", "ctrl+\\")]
    [InlineData("ctrl+oempipe", "ctrl+\\")]
    [InlineData("ctrl" + "+" + "PAGEDOWN", "ctrl+pagedown")]
    public void Normalize_ProducesStableGestures(string gesture, string expected)
    {
        Assert.Equal(expected, KeyGestureNormalizer.Normalize(gesture));
    }

    [Fact]
    public void NormalizeStroke_MapsOemBackslashAndOemPipeToBackslash()
    {
        Assert.Equal("ctrl+\\", KeyGestureNormalizer.NormalizeStroke("ctrl+oembackslash"));
        Assert.Equal("ctrl+shift+\\", KeyGestureNormalizer.NormalizeStroke("ctrl+shift+oempipe"));
    }
}