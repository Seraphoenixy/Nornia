using Nornia.Project.Models;

namespace Nornia.Tests;

/// <summary>版本模型(<see cref="VersionValue"/> / <see cref="VersionComparator"/> /
/// <see cref="VersionConstraintParser"/>)此前是全项目零覆盖的核心类型,而 §1.2 的排序 Bug
/// 正出在它的调用侧。这里把「数值序比较」「预发布语义」「非法输入」三类行为固定下来。</summary>
public sealed class VersionConstraintTests
{
    [Theory]
    [InlineData("9.0.1", "10.0.0", -1)]
    [InlineData("10.0.0", "9.0.1", 1)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("1.2.1", "1.2", 1)]
    [InlineData("2.0", "10.0", -1)]
    public void CompareTo_UsesNumericOrderingNotLexicographic(string left, string right, int expectedSign)
    {
        Assert.True(VersionValue.TryParse(left, out var a));
        Assert.True(VersionValue.TryParse(right, out var b));

        // 字符串序会给出 "9.0.1" > "10.0.0",这正是 §1.2 的 Bug 根源。
        Assert.Equal(expectedSign, Math.Sign(a.CompareTo(b)));
    }

    [Fact]
    public void CompareTo_TreatsReleaseAsNewerThanPrerelease()
    {
        Assert.True(VersionValue.TryParse("1.0.0", out var release));
        Assert.True(VersionValue.TryParse("1.0.0-beta", out var prerelease));

        Assert.True(release.CompareTo(prerelease) > 0);
        Assert.True(prerelease.CompareTo(release) < 0);
    }

    [Fact]
    public void CompareTo_OrdersPrereleaseIdentifiersLexicographically()
    {
        // 已知简化:预发布标识符按序数比较,而非 SemVer 的数值分段比较。
        // 本项目的版本域(运行时/工具版本)里预发布极少,这里固定当前行为以免无意识漂移。
        Assert.True(VersionValue.TryParse("1.0.0-alpha", out var alpha));
        Assert.True(VersionValue.TryParse("1.0.0-beta", out var beta));

        Assert.True(alpha.CompareTo(beta) < 0);
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3-beta.1", "1.2.3-beta.1")]
    [InlineData("  10.0.100  ", "10.0.100")]
    public void TryParse_NormalizesPrefixAndWhitespace(string input, string canonical)
    {
        Assert.True(VersionValue.TryParse(input, out var value));
        Assert.Equal(canonical, value.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.")]
    [InlineData("-beta")]
    [InlineData("1..2")]
    public void TryParse_RejectsMalformedInput(string input)
    {
        Assert.False(VersionValue.TryParse(input, out _));
    }

    [Fact]
    public void TryParseInstalled_ToleratesNonNumericRuntimeSuffix()
    {
        // 已安装版本常带发行标记(如 "3.13.0b4"),必须以数字前缀为准而不是整体判定失败。
        Assert.True(VersionValue.TryParseInstalled("3.13.0b4", out var value));
        Assert.Equal("3.13", value.ToString());

        // 但严格解析(用于约束声明)不接受这种写法。
        Assert.False(VersionValue.TryParse("3.13.0b4", out _));
    }

    [Fact]
    public void PrefixConstraint_MatchesSameMajorMinorOnly()
    {
        Assert.True(VersionConstraintParser.TryParse("10.0", out var constraint));

        Assert.True(constraint!.IsSatisfiedBy("10.0.100"));
        Assert.False(constraint.IsSatisfiedBy("10.1.0"));
        Assert.False(constraint.IsSatisfiedBy("9.0.0"));
        Assert.Equal("10.0", constraint.GetRepairTargetVersion());
    }

    [Fact]
    public void RangeConstraint_RequiresEveryComparator()
    {
        Assert.True(VersionConstraintParser.TryParse(">=10.0.0 <11.0.0", out var constraint));

        Assert.True(constraint!.IsSatisfiedBy("10.5.0"));
        Assert.False(constraint.IsSatisfiedBy("9.0.0"));
        Assert.False(constraint.IsSatisfiedBy("11.0.0"));
        Assert.False(constraint.IsSatisfiedBy("12.0.0"));
        // 修复目标取 >= 那一侧的版本。
        Assert.Equal("10.0.0", constraint.GetRepairTargetVersion());
    }

    [Fact]
    public void Constraint_RejectsPrereleaseUnlessTheConstraintAllowsIt()
    {
        Assert.True(VersionConstraintParser.TryParse("1.0", out var stable));
        Assert.False(stable!.AllowsPrerelease);
        Assert.False(stable.IsSatisfiedBy("1.0.0-beta"));

        Assert.True(VersionConstraintParser.TryParse("1.0.0-beta", out var prerelease));
        Assert.True(prerelease!.AllowsPrerelease);
        Assert.True(prerelease.IsSatisfiedBy("1.0.0-beta"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(">=")]
    [InlineData(">=abc")]
    [InlineData("=>1.0")]
    public void ConstraintParser_RejectsMalformedConstraints(string input)
    {
        Assert.False(VersionConstraintParser.TryParse(input, out _));
    }

    [Fact]
    public void ComparatorParser_RecognizesEveryOperator()
    {
        Assert.True(VersionComparator.TryParse(">=1.0", out var ge));
        Assert.Equal(VersionComparisonOperator.GreaterThanOrEqual, ge.Operator);
        Assert.True(VersionComparator.TryParse("<2.0", out var lt));
        Assert.Equal(VersionComparisonOperator.LessThan, lt.Operator);
        Assert.True(VersionComparator.TryParse(">1.0", out var gt));
        Assert.Equal(VersionComparisonOperator.GreaterThan, gt.Operator);
        Assert.True(VersionComparator.TryParse("<=2.0", out var le));
        Assert.Equal(VersionComparisonOperator.LessThanOrEqual, le.Operator);
    }
}
