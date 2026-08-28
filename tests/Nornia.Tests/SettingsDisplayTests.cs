using Nornia.Desktop.Commands;
using Nornia.Desktop.Configuration;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;
using NSubstitute;

namespace Nornia.Tests;

/// <summary>设置页展示层(仅显示):真实值/来源/继承关系的呈现、统一显示格式化、枚举中文标签与
/// 原始值提交分离、重置当前作用域行为。配置解析、保存与作用域优先级不受影响。</summary>
public sealed class SettingsDisplayTests
{
    private static SettingsEditorViewModel CreateEditor(ISettingsService? settings = null)
    {
        var editor = new SettingsEditorViewModel(
            settings ?? new FakeSettingsService(),
            new BuiltInSettingsCatalog(),
            new FakeProjectWorkspaceService(),
            Substitute.For<IKeybindingService>(),
            Substitute.For<ICommandRegistry>(),
            new FakeApplicationStateStore());
        return editor;
    }

    [Fact]
    public async Task UnconfiguredSetting_ShowsActualBuiltInValue_NotPlaceholderText()
    {
        var editor = CreateEditor();
        await editor.InitializeAsync();

        var fontSize = editor.Items.Single(item => item.Id == BuiltInSettingsCatalog.EditorFontSize.Id);
        // 未配置的作用域显示"未设置(继承)",不再出现"默认值 / 默认 → …"占位。
        Assert.Equal("未设置", fontSize.CurrentScopeValueText);
        Assert.Equal("14", fontSize.EffectiveValueText);
        Assert.Equal("14", fontSize.DefaultValueText);
        Assert.Equal("内置：14", fontSize.SourceChainText);
        Assert.Equal("内置", fontSize.SourceText);
        Assert.False(fontSize.IsModified);
    }

    [Fact]
    public async Task DisplayFormatting_TranslatesBooleansEnumsThemesAndCollections()
    {
        var editor = CreateEditor();
        await editor.InitializeAsync();

        // 布尔 → 启用/禁用(默认值本身)。
        var preview = editor.Items.Single(item => item.Id == BuiltInSettingsCatalog.EnablePreview.Id);
        Assert.Equal("启用", preview.EffectiveValueText);
        var folding = editor.Items.Single(item => item.Id == BuiltInSettingsCatalog.Folding.Id);
        Assert.Equal("启用", folding.EffectiveValueText);

        // off/on → 关闭/开启;换行枚举 → 中文含义。
        var wrap = editor.Items.Single(item => item.Id == BuiltInSettingsCatalog.EditorWordWrap.Id);
        Assert.Equal("关闭", wrap.EffectiveValueText);
        Assert.Equal(new[] { "关闭", "开启", "按列宽换行", "视口受限换行" }, wrap.EnumOptions.Select(option => option.Text));
        Assert.Equal(new[] { "off", "on", "wordWrapColumn", "bounded" }, wrap.EnumOptions.Select(option => option.Value).Cast<string>());
        var lineNumbers = editor.Items.Single(item => item.Id == BuiltInSettingsCatalog.LineNumbers.Id);
        Assert.Equal("开启", lineNumbers.EffectiveValueText);

        // 主题、强调色 → 中文含义。
        var theme = editor.Items.Single(item => item.Id == BuiltInSettingsCatalog.Theme.Id);
        Assert.Equal("深色", theme.EffectiveValueText);
        var accent = editor.Items.Single(item => item.Id == BuiltInSettingsCatalog.Accent.Id);
        Assert.Equal("主题默认色", accent.EffectiveValueText);
        Assert.Equal("主题默认色", accent.DefaultValueText);

        // 空数组 / 空对象 → "未配置"。
        var shells = editor.Items.Single(item => item.Id == BuiltInSettingsCatalog.TerminalCustomShells.Id);
        Assert.Equal("未配置", shells.EffectiveValueText);
        var exclude = editor.Items.Single(item => item.Id == BuiltInSettingsCatalog.FilesExclude.Id);
        Assert.Equal("未配置", exclude.EffectiveValueText);
    }

    [Fact]
    public void SourceChain_ShowsActualValuesWithCorrectPriority()
    {
        var editor = CreateEditor();
        var fontSize = editor.Items.Single(item => item.Id == BuiltInSettingsCatalog.EditorFontSize.Id);

        // 内置 14 → 用户 18 → 工作区 17 → 用户语言 20;当前作用域(用户 + 语言覆盖)取用户语言值。
        fontSize.Apply(new UntypedSettingValue(
            DefaultValue: 14d,
            UserValue: UntypedOptionalValue.Some(18d),
            WorkspaceValue: UntypedOptionalValue.Some(17d),
            UserLanguageValue: UntypedOptionalValue.Some(20d),
            WorkspaceLanguageValue: UntypedOptionalValue.None,
            EffectiveValue: 20d,
            Source: SettingValueSource.UserLanguage),
            SettingScope.User, languageOverride: true);

        Assert.Equal("20", fontSize.CurrentScopeValueText);
        Assert.Equal("20", fontSize.EffectiveValueText);
        Assert.Equal("内置：14 → 用户：18 → 工作区：17 → 用户语言：20", fontSize.SourceChainText);
        Assert.Equal("用户 · 语言覆盖", fontSize.SourceText);
        Assert.True(fontSize.IsModified);
    }

    [Fact]
    public async Task EnumCommit_PersistsRawValueWhileDisplayShowsChinese()
    {
        var settings = new FakeSettingsService();
        var editor = CreateEditor(settings);
        await editor.InitializeAsync();
        var wrap = editor.Items.Single(item => item.Id == BuiltInSettingsCatalog.EditorWordWrap.Id);

        // 选择中文标签"按列宽换行",提交后写回的是原始配置值 wordWrapColumn。
        wrap.SelectedEnum = wrap.EnumOptions.Single(option => Equals(option.Value, "wordWrapColumn"));
        await editor.FlushAsync();

        var snapshot = await settings.GetSnapshotAsync(new SettingsContext());
        Assert.Equal("wordWrapColumn", snapshot.Effective(BuiltInSettingsCatalog.EditorWordWrap));
        Assert.Equal("按列宽换行", wrap.EffectiveValueText);
    }

    [Fact]
    public async Task ResetSetting_ClearsCurrentScopeOnly_AndFallsBackToInherited()
    {
        var settings = new FakeSettingsService();
        var editor = CreateEditor(settings);
        await editor.InitializeAsync();
        var fontSize = editor.Items.Single(item => item.Id == BuiltInSettingsCatalog.EditorFontSize.Id);

        fontSize.ValueText = "18";
        await editor.FlushAsync();
        Assert.Equal("18", fontSize.CurrentScopeValueText);
        // 无语言上下文时,解析器把根节点同时作为用户值与用户语言值(既有解析语义),链如实显示。
        Assert.Equal("内置：14 → 用户：18 → 用户语言：18", fontSize.SourceChainText);

        await editor.ResetSettingCommand.ExecuteAsync(fontSize);

        // 重置只清除当前作用域(用户):生效值回落到内置值,显示"未设置(继承)"。
        Assert.Equal("未设置", fontSize.CurrentScopeValueText);
        Assert.Equal("14", fontSize.EffectiveValueText);
        Assert.Equal("内置：14", fontSize.SourceChainText);
        Assert.False(fontSize.IsModified);
        var snapshot = await settings.GetSnapshotAsync(new SettingsContext());
        Assert.Equal(14d, snapshot.Effective(BuiltInSettingsCatalog.EditorFontSize));
    }
}