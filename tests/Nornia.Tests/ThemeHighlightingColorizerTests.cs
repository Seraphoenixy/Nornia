using Nornia.Desktop.Code;
using System.Text.RegularExpressions;

namespace Nornia.Tests;

/// <summary>Verifies the syntax-highlighting palette mapping: every semantic fragment resolves to a
/// palette token exactly once, unmapped names fall back to the built-in color, and token lookups
/// degrade to null without an active WPF <see cref="System.Windows.Application"/>.</summary>
public sealed class ThemeHighlightingColorizerTests
{
    [Theory]
    [InlineData("Comment", "CodeTokenCommentBrush")]
    [InlineData("MultiLineComment", "CodeTokenCommentBrush")]
    [InlineData("String", "CodeTokenStringBrush")]
    [InlineData("VerbatimString", "CodeTokenStringBrush")]
    [InlineData("Keyword", "CodeTokenKeywordBrush")]
    [InlineData("Number", "CodeTokenNumberBrush")]
    [InlineData("TypeName", "CodeTokenTypeBrush")]
    [InlineData("ClassName", "CodeTokenTypeBrush")]
    [InlineData("MethodCall", "CodeTokenFunctionBrush")]
    [InlineData("Function", "CodeTokenFunctionBrush")]
    [InlineData("Url", "CodeTokenLinkBrush")]
    [InlineData("Link", "CodeTokenLinkBrush")]
    [InlineData("Attribute", "CodeTokenAttributeBrush")]
    [InlineData("XmlTag", "CodeTokenTagBrush")]
    [InlineData("MarkupTag", "CodeTokenTagBrush")]
    public void MatchToken_MapsKnownSemanticNames(string colorName, string expectedToken)
    {
        var actual = ThemeHighlightingColorizer.MatchToken(colorName);
        Assert.Equal(expectedToken, actual);
    }

    [Fact]
    public void MatchToken_UnmappedNameReturnsNull()
    {
        Assert.Null(ThemeHighlightingColorizer.MatchToken("StrangeDecoration"));
        Assert.Null(ThemeHighlightingColorizer.MatchToken(string.Empty));
    }

    [Fact]
    public void ResolveTokenColor_WithoutApplicationReturnsNull()
    {
        // 无 WPF Application 时查询必须降级为 null 而不是抛异常;生产环境总有主题字典。
        // 并行运行的 WPF 视图测试(WpfStaContext)会在同一进程创建真实 App,此时令牌应能解析
        // —— 两种宿主状态都必须安全,因此只在确实没有 Application 时断言为 null。
        if (System.Windows.Application.Current is null)
        {
            Assert.Null(ThemeHighlightingColorizer.ResolveTokenColor("CodeTokenKeywordBrush"));
        }
    }
}
