using Nornia.Desktop.Code;
using Nornia.Desktop.ViewModels;

namespace Nornia.Tests;

/// <summary>Covers <see cref="PinyinInitials"/> (GBK 区位边界算法) and its QuickInput 集成:
/// 中文命令标题对拉丁缩写查询("dkxml" → 打开项目目录)的匹配档位。</summary>
public sealed class PinyinInitialsTests
{
    [Theory]
    [InlineData("打开项目目录", "dkxmml")] // 项目目录 = 项x 目m 目m 录l(两个"目")
    [InlineData("新建终端", "xjzd")]
    [InlineData("切换侧栏", "qhcl")]
    [InlineData("转到行", "zdx")] // 行(xíng)按 GB2312 排在 x 段
    [InlineData("源代码管理", "ydmgl")] // g/h 边界修正:管(guǎn)必须落在 g 段
    public void GetInitials_MapsCommonShellTitles(string title, string expected) =>
        Assert.Equal(expected, PinyinInitials.GetInitials(title));

    [Fact]
    public void GetInitials_KeepsAsciiLettersDigits_SkipsOtherSymbols()
    {
        Assert.Equal("ctrlp", PinyinInitials.GetInitials("Ctrl+P"));
        Assert.Equal("f12zd", PinyinInitials.GetInitials("F12 转到"));
        Assert.Equal(string.Empty, PinyinInitials.GetInitials("（）·—"));
        Assert.Equal(string.Empty, PinyinInitials.GetInitials(""));
    }

    [Fact]
    public void IsAbbreviationQuery_RequiresAsciiLettersOrDigitsOnly()
    {
        Assert.True(PinyinInitials.IsAbbreviationQuery("dkxml"));
        Assert.True(PinyinInitials.IsAbbreviationQuery("F12"));
        Assert.False(PinyinInitials.IsAbbreviationQuery("dk xml"));
        Assert.False(PinyinInitials.IsAbbreviationQuery("打开"));
        Assert.False(PinyinInitials.IsAbbreviationQuery(string.Empty));
    }

    [Fact]
    public void QuickInput_AbbreviationQueryMatchesChineseTitles()
    {
        var vm = new QuickInputViewModel();
        vm.Open("palette",
        [
            new QuickPickItem("打开项目目录", "文件", "", () => { }),
            new QuickPickItem("新建终端", "终端", "", () => { }),
            new QuickPickItem("打开设置", "首选项", "", () => { }),
        ]);

        vm.FilterText = "dkxmm";

        var titles = vm.View.Cast<QuickPickItem>().Select(item => item.Title).ToArray();
        Assert.Equal("打开项目目录", titles[0]); // 首字母前缀命中,排最前
        Assert.DoesNotContain("新建终端", titles);
    }

    [Fact]
    public void QuickInput_TitleSubstringStillOutranksInitials()
    {
        var vm = new QuickInputViewModel();
        vm.Open("palette",
        [
            new QuickPickItem("dkxml 是缩写", "", "", () => { }), // 标题直接含子串(100k 档)
            new QuickPickItem("打开项目目录", "", "", () => { }), // 首字母档(90k)
        ]);

        vm.FilterText = "dkxml";

        Assert.Equal("dkxml 是缩写", vm.View.Cast<QuickPickItem>().First().Title);
    }
}
