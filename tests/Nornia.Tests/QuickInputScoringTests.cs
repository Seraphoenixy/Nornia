using Nornia.Desktop.ViewModels;

namespace Nornia.Tests;

/// <summary>Covers the fuzzy scoring + resident filter behavior of the rewritten QuickInput
/// (VS Code fuzzyScorer-inspired): match priority bands, word-start preference, focus handling
/// and stable ordering.</summary>
public sealed class QuickInputScoringTests
{
    private static QuickInputViewModel Create(params (string Title, string Detail)[] items)
    {
        var vm = new QuickInputViewModel();
        vm.Open("palette", items.Select(item => new QuickPickItem(item.Title, item.Detail, "", () => { })));
        return vm;
    }

    private static List<string> VisibleTitles(QuickInputViewModel vm) =>
        vm.View.Cast<QuickPickItem>().Select(item => item.Title).ToList();

    [Fact]
    public void DetailSubstring_RanksBelowTitleSubstring()
    {
        var vm = Create(("别的命令", "含关键词"), ("关键词命令", "无关"));

        vm.FilterText = "关键词";

        // "关键词命令" 标题含子串(100k 档)排在 "别的命令"(详情含子串,50k 档)之前。
        Assert.Equal(["关键词命令", "别的命令"], VisibleTitles(vm));
    }

    [Fact]
    public void FuzzyMatchPrefersWordStartAndSmallerGaps()
    {
        // 后两者均不含连续子串 "open"(只按模糊子序列打分);"open project" 是连续子串:
        var vm = Create(("open project", ""), ("o_p_e_n", ""), ("xo pen x", ""));

        vm.FilterText = "open";

        // "o_p_e_n" 每字符都在词首(下划线分隔);"xo pen x" 首字符不在词首、间隙更多 → 殿后。
        Assert.Equal(["open project", "o_p_e_n", "xo pen x"], VisibleTitles(vm));
    }

    [Fact]
    public void MoveSelection_IsPositionalAndWraps()
    {
        var vm = Create(("alpha", ""), ("beta", ""), ("gamma", ""));

        vm.MoveSelection(1);
        vm.MoveSelection(1);
        vm.MoveSelection(1);

        Assert.Equal("alpha", vm.SelectedItem?.Title); // wrapped back to first
    }

    [Fact]
    public void FilteringKeepsFocusWhenSelectionSurvives()
    {
        var vm = Create(("alpha", ""), ("beta", ""), ("gamma", ""));
        vm.MoveSelection(2); // gamma selected
        vm.FilterText = "a"; // gamma survives (fuzzy 'a' at index 1), beta drops out

        Assert.Equal("gamma", vm.SelectedItem?.Title);
    }

    [Fact]
    public void FilteringResetsToFirstWhenSelectionDropsOut()
    {
        var vm = Create(("alpha", ""), ("beta", ""), ("gamma", ""));
        vm.MoveSelection(2); // gamma selected
        vm.FilterText = "b"; // only beta matches; gamma drops out

        Assert.Equal("beta", vm.SelectedItem?.Title);
    }

    [Fact]
    public void EqualScoresKeepOriginalOrder()
    {
        var vm = Create(("a one", ""), ("a two", ""), ("a three", ""));

        vm.FilterText = "a";

        // 三者同为标题位置 0 的子串命中(同分):稳定排序必须保持原始顺序。
        Assert.Equal(["a one", "a two", "a three"], VisibleTitles(vm));
    }
}
