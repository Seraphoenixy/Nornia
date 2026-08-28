using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using Nornia.Desktop.Services;

namespace Nornia.Tests;

/// <summary>
/// Guards the global UI font scale: token values multiply + round correctly, the scale is clamped
/// to [0.85, 1.3], applying 100% restores the App.xaml author values (idempotent), the rewritten
/// key set covers every scalable token, and the base table stays in sync with App.xaml.
/// </summary>
public sealed class UiFontServiceTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();

    // Only content-bearing measurements participate in UI font scaling. Frame metrics such as
    // SizeWorkbenchSplitter deliberately stay fixed so drag targets and seams do not drift.
    private static readonly Regex TokenDefinitionRegex = new(
        @"<sys:Double x:Key=""(Type\w+|Icon\w+|Size(?:RowSm|RowCompact|TableRow|RowMd|ControlMd|StatusBar|TitleBar|Activity|TabBar|CommitBox))"">([0-9.]+)</sys:Double>",
        RegexOptions.Compiled);

    [Fact]
    public void ApplyTo_MultipliesAndRoundsEveryToken()
    {
        var resources = new ResourceDictionary();
        UiFontService.ApplyTo(resources, 1.3);

        Assert.Equal(16.9, (double)resources["TypeBody"], 10);      // 13 * 1.3 — VS Code workbench 13
        Assert.Equal(33.8, (double)resources["TypeHero"], 10);      // 26 * 1.3
        Assert.Equal(11.7, (double)resources["IconChevron"], 10);   // 9 * 1.3
        Assert.Equal(15.6, (double)resources["IconInline"], 10);    // 12 * 1.3
        Assert.Equal(31.2, (double)resources["IconActivity"], 10);  // 24 * 1.3 — VS Code activity icon
        Assert.Equal(20.8, (double)resources["IconActivityCompact"], 10); // 16 * 1.3
        Assert.Equal(62.4, (double)resources["SizeActivity"], 10);  // 48 * 1.3
    }

    [Fact]
    public void ApplyTo_ClampsScaleToSupportedBounds()
    {
        var below = new ResourceDictionary();
        UiFontService.ApplyTo(below, 0.5);
        var atMin = new ResourceDictionary();
        UiFontService.ApplyTo(atMin, UiFontService.MinScale);
        foreach (var (key, _) in UiFontService.BaseValues)
        {
            Assert.Equal((double)atMin[key], (double)below[key], 10);
        }

        var above = new ResourceDictionary();
        UiFontService.ApplyTo(above, 2.0);
        var atMax = new ResourceDictionary();
        UiFontService.ApplyTo(atMax, UiFontService.MaxScale);
        foreach (var (key, _) in UiFontService.BaseValues)
        {
            Assert.Equal((double)atMax[key], (double)above[key], 10);
        }

        Assert.Equal(16.9, (double)atMax["TypeBody"], 10); // clamp keeps 13 * 1.3, not 13 * 2
    }

    [Fact]
    public void ApplyTo_DefaultScaleRestoresAuthorValues()
    {
        var resources = new ResourceDictionary();
        UiFontService.ApplyTo(resources, UiFontService.DefaultScale);
        foreach (var (key, baseValue) in UiFontService.BaseValues)
        {
            Assert.Equal(baseValue, (double)resources[key], 10);
        }
    }

    [Fact]
    public void ApplyTo_RewritesExactlyTheScalableTokenKeys()
    {
        var resources = new ResourceDictionary();
        UiFontService.ApplyTo(resources, 1.1);
        Assert.Equal(UiFontService.BaseValues.Keys.OrderBy(k => k),
            resources.Keys.Cast<string>().OrderBy(k => k));
    }

    [Fact]
    public void BaseValues_StayInSyncWithAppXamlAuthorValues()
    {
        // 基值表必须与 App.xaml 的作者值一致(缩放 1.0 时不得产生任何视觉变化)。
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var definitions = TokenDefinitionRegex.Matches(app)
            .ToDictionary(m => m.Groups[1].Value, m => double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(UiFontService.BaseValues.Count, definitions.Count);
        foreach (var (key, value) in UiFontService.BaseValues)
        {
            Assert.True(definitions.TryGetValue(key, out var appValue), $"App.xaml 缺少可缩放令牌 {key}");
            Assert.True(appValue == value, $"令牌 {key} 基值不一致：UiFontService={value}，App.xaml={appValue}");
        }

        // 收敛后的角色表:小字阶全部并入 TypeBody(13 — VS Code 13),行级图标并入 IconInline(12);
        // 旧的多档令牌不得复活。
        Assert.Equal(13, UiFontService.BaseValues["TypeBody"]);
        Assert.Equal(12, UiFontService.BaseValues["IconInline"]);
        foreach (var removed in new[] { "TypeMicro", "TypeCaption", "TypeLg", "TypeSection", "IconSm", "IconGlyph", "IconMd", "IconLg", "IconXl" })
        {
            Assert.False(UiFontService.BaseValues.ContainsKey(removed), $"已收敛的令牌 {removed} 不得复活");
        }
    }

    [Fact]
    public void Apply_NeverThrowsRegardlessOfApplicationState()
    {
        // 启动前(无 Application)必须静默返回;若测试宿主里恰好已有 Application 实例
        // (STA 诊断测试共享进程),Apply 则正常改写令牌——两种情况都不得抛异常。
        var exception = Record.Exception(() => UiFontService.Apply(1.2));
        Assert.Null(exception);
    }

    [Fact]
    public void HeightTokens_AlwaysAccommodateTextAtEveryScale()
    {
        // 行/条/控件高度令牌必须始终容纳正文:在 85% / 100% / 130% 三个档位下,行高都 ≥
        // 正文行高需求(13px × 1.35 行高 ≈ 17.6),否则缩放后出现纵向裁切或字行叠压。
        var body = UiFontService.BaseValues["TypeBody"];
        foreach (var token in new[] { "SizeRowSm", "SizeRowCompact", "SizeTableRow", "SizeStatusBar", "SizeControlMd", "SizeRowMd" })
        {
            foreach (var scale in new[] { UiFontService.MinScale, UiFontService.DefaultScale, UiFontService.MaxScale })
            {
                var height = UiFontService.BaseValues[token] * scale;
                var lineRequirement = body * scale * 1.35;
                Assert.True(height >= lineRequirement,
                    $"{token} × {scale} = {height:F1} 不足以容纳正文行高 {lineRequirement:F1}");
            }
        }

        // VS Code 默认桌面密度基线:标题栏/标签栏 35px、活动栏 48px;
        // 状态栏 28px(合并文档状态段后加高,原 22)
        Assert.Equal(35, UiFontService.BaseValues["SizeTitleBar"]);
        Assert.Equal(35, UiFontService.BaseValues["SizeTabBar"]);
        Assert.Equal(28, UiFontService.BaseValues["SizeStatusBar"]);
        Assert.Equal(22, UiFontService.BaseValues["SizeRowSm"]);
        Assert.Equal(22, UiFontService.BaseValues["SizeTableRow"]);
        Assert.Equal(48, UiFontService.BaseValues["SizeActivity"]);
        Assert.Equal(24, UiFontService.BaseValues["IconActivity"]);
    }

    [Fact]
    public void SettingsPage_WiresLivePreviewAndPersistence()
    {
        var view = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/SettingsView.xaml"));
        // Settings is catalog-driven: the UI scale is edited through the same generic setting
        // editor as the other appearance options, while AppearanceSettingsController applies it
        // immediately when the scoped setting session changes.
        Assert.Contains("ItemsSource=\"{Binding Editor.View}\"", view);

        var catalog = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Configuration/BuiltInSettingsCatalog.cs"));
        Assert.Contains("UiScale", catalog);
        Assert.Contains("界面缩放", catalog);
        var controller = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Services/AppearanceSettingsController.cs"));
        Assert.Contains("UiFontService.Apply", controller);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string ResolveRepoRoot([CallerFilePath] string? sourcePath = null)
    {
        var testsDir = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException("无法定位测试源文件路径。");
        return Path.GetFullPath(Path.Combine(testsDir, "..", ".."));
    }
}
