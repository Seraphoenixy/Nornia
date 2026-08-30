using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Nornia.Desktop.Commands;
using Nornia.Desktop.Configuration;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;
using NSubstitute;

namespace Nornia.Tests;

/// <summary>
/// Guards the configurable mono fonts: the Settings page exposes both the code reader and the
/// terminal font as dropdown (enumeration-style) editors backed by the curated supported-font
/// catalog, the persisted values flow into the code/diff readers and the terminal surface, and
/// the views are actually wired to those properties. TerminalViewModel 的设置应用会 marshal
/// 到共享 STA Dispatcher,测试需要泵队列——因此整个类放入非并行集合,独占阶段内泵不会
/// 干扰其它 STA 测试。
/// </summary>
[Collection("WpfStaSequential")]
public sealed class FontConfigurationTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();

    [Fact]
    public void FontCatalog_ExposesSupportedMonoFamiliesAndDefaults()
    {
        Assert.True(FontCatalog.SupportedMonoFamilies.Count >= 10, "支持的字体列表应包含常用等宽候选");
        Assert.Equal(FontCatalog.SupportedMonoFamilies, FontCatalog.SupportedMonoFamilies.Distinct(StringComparer.Ordinal));
        Assert.All(FontCatalog.SupportedMonoFamilies, family => Assert.False(string.IsNullOrWhiteSpace(family)));
        Assert.Contains(FontCatalog.DefaultEditorFamily, FontCatalog.SupportedMonoFamilies);
        Assert.Contains(FontCatalog.DefaultTerminalFamily, FontCatalog.SupportedMonoFamilies);
        Assert.Equal("Cascadia Code", FontCatalog.DefaultEditorFamily);
        // 渲染令牌:所选字体后补通用后备,空值回退默认,绝不产生空字体族。
        Assert.StartsWith("Consolas, ", FontCatalog.ComposeRenderingFamily("Consolas"));
        Assert.StartsWith(FontCatalog.DefaultEditorFamily, FontCatalog.ComposeRenderingFamily(null));
        Assert.StartsWith(FontCatalog.DefaultEditorFamily, FontCatalog.ComposeRenderingFamily("  "));
    }

    [Fact]
    public void FontCatalog_DetectsInstalledFamiliesWithoutThrowing()
    {
        // Windows 全系自带 Consolas / Courier New;Windows Terminal(Win11 默认)带 Cascadia Mono。
        // 至少识别到其中一个即证明系统字体枚举可用;无字体服务的宿主也不得抛异常。
        Assert.True(FontCatalog.IsInstalled("Consolas") || FontCatalog.IsInstalled("Courier New") ||
            FontCatalog.IsInstalled("Cascadia Mono"), "系统字体枚举应当识别 Windows 内置等宽字体");
        Assert.False(FontCatalog.IsInstalled("Nornia-Surely-Not-A-Real-Font-098"));
        Assert.False(FontCatalog.IsInstalled(string.Empty));
        Assert.False(FontCatalog.IsInstalled(null!));
        // InstalledSupportedFamilies ⊂ SupportedMonoFamilies 且去重。
        Assert.All(FontCatalog.InstalledSupportedFamilies, family =>
            Assert.Contains(family, FontCatalog.SupportedMonoFamilies));
        Assert.Equal(FontCatalog.InstalledSupportedFamilies.Count,
            FontCatalog.InstalledSupportedFamilies.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void FontSettings_AreDropdownEditorsOverTheSupportedList()
    {
        var catalog = new BuiltInSettingsCatalog();
        Assert.True(catalog.TryGet(BuiltInSettingsCatalog.EditorFontFamily.Id, out var editor));
        Assert.Equal(SettingEditorKind.Font, editor.EditorKind);
        Assert.Equal(FontCatalog.DefaultEditorFamily, editor.DefaultValue);
        Assert.Equal(FontCatalog.SupportedMonoFamilies, ((SettingDefinition<string>)editor).EnumValues);

        Assert.True(catalog.TryGet(BuiltInSettingsCatalog.TerminalFontFamily.Id, out var terminal));
        Assert.Equal(SettingEditorKind.Font, terminal.EditorKind);
        Assert.Equal(FontCatalog.DefaultTerminalFamily, terminal.DefaultValue);
        Assert.Equal(FontCatalog.SupportedMonoFamilies, ((SettingDefinition<string>)terminal).EnumValues);

        // 终端字体从自由文本编辑器迁移为下拉框:值必须取自支持列表。
        Assert.NotEqual(SettingEditorKind.String, terminal.EditorKind);
    }

    [Fact]
    public async Task SettingsService_CommitsAndPersistsEditorFontFamily()
    {
        var service = new FakeSettingsService();
        var context = new SettingsContext();

        var baseline = await service.GetSnapshotAsync(context);
        Assert.Equal(FontCatalog.DefaultEditorFamily, baseline.Effective(BuiltInSettingsCatalog.EditorFontFamily));

        var commit = await service.CommitAsync(SettingsTransaction.Set(baseline, SettingScope.User,
            BuiltInSettingsCatalog.EditorFontFamily, "JetBrains Mono"));
        Assert.True(commit.IsSuccess);

        var after = await service.GetSnapshotAsync(context);
        Assert.Equal("JetBrains Mono", after.Effective(BuiltInSettingsCatalog.EditorFontFamily));
    }

    [Fact]
    public async Task SettingsService_RejectsFontOutsideSupportedList()
    {
        var service = new FakeSettingsService();
        var context = new SettingsContext();
        var baseline = await service.GetSnapshotAsync(context);

        foreach (var invalid in new[] { "Comic Sans MS", "", "Cascadia Code, Consolas" })
        {
            var commit = await service.CommitAsync(SettingsTransaction.Set(baseline, SettingScope.User,
                BuiltInSettingsCatalog.EditorFontFamily, invalid));
            Assert.False(commit.IsSuccess);
            Assert.Equal(SettingsCommitStatus.ValidationFailed, commit.Status);
            Assert.True(commit.ValidationErrors!.ContainsKey(BuiltInSettingsCatalog.EditorFontFamily.Id));
        }
    }

    [Fact]
    public async Task SettingsMapper_CarriesReaderFontFamilyFromSnapshot()
    {
        var service = new FakeSettingsService();
        var context = new SettingsContext();
        var baseline = await service.GetSnapshotAsync(context);

        var mapped = SettingsOptionsMapper.CodeReading(baseline);
        Assert.Equal(FontCatalog.DefaultEditorFamily, mapped.FontFamily);

        await service.CommitAsync(SettingsTransaction.Set(baseline, SettingScope.User,
            BuiltInSettingsCatalog.EditorFontFamily, "Fira Code"));
        var changed = await service.GetSnapshotAsync(context);
        Assert.Equal("Fira Code", SettingsOptionsMapper.CodeReading(changed).FontFamily);
    }

    [Fact]
    public async Task TerminalViewModel_AppliesTerminalFontFromSettings()
    {
        var settingsService = new FakeSettingsService();
        var baseline = await settingsService.GetSnapshotAsync(new SettingsContext());
        await settingsService.CommitAsync(SettingsTransaction.Set(baseline, SettingScope.User,
            BuiltInSettingsCatalog.TerminalFontFamily, "Consolas"));

        var terminal = new TerminalViewModel(Substitute.For<ITerminalService>(), settingsService,
            new FakeProjectWorkspaceService(), new FakeUiLogService(), new FakeClipboardService());
        Assert.Equal(FontCatalog.DefaultTerminalFamily, terminal.FontFamily);

        await terminal.ActivateAsync();
        // 共享测试宿主中可能已存在 Application( STA 测试):ApplyTerminalSettings 会把应用
        // BeginInvoke 到共享 STA 线程的 Dispatcher——该队列只在有人调用 WpfStaContext.PumpQueue
        // 时才被泵,本测试必须自己泵,否则应用永远落地不了(并行下靠其它 STA 测试泵过属
        // 侥幸)。60s 预算覆盖 2 核 CI 全量套件满负载。
        var applied = false;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (terminal.FontFamily == "Consolas")
            {
                applied = true;
                break;
            }

            WpfStaContext.Run(WpfStaContext.PumpQueue);
            await Task.Delay(10);
        }

        Assert.True(applied, "终端字体设置未在会话激活后应用。");
    }

    [Fact]
    public async Task SettingsEditorViewModel_FontEditorSelectsAndValidates()
    {
        var catalog = new BuiltInSettingsCatalog();
        var editor = new SettingsEditorViewModel(
            new FakeSettingsService(), catalog, new FakeProjectWorkspaceService(),
            Substitute.For<IKeybindingService>(), Substitute.For<ICommandRegistry>(),
            new FakeApplicationStateStore());
        await editor.InitializeAsync();

        var item = editor.Items.Single(entry => entry.Id == BuiltInSettingsCatalog.EditorFontFamily.Id);
        Assert.True(item.IsFontEditor);
        Assert.False(item.IsEnumeration);
        Assert.False(item.IsTextEditor);
        // 显示层选项:Text 与 Value 均为字体族名称(字体名本身即展示文本);提交用原始 Value。
        Assert.Equal(FontCatalog.SupportedMonoFamilies, item.EnumOptions.Select(option => option.Value).Cast<string>());
        Assert.Equal(FontCatalog.DefaultEditorFamily, Assert.IsType<SettingDisplayOption>(item.SelectedEnum).Value);

        item.SelectedEnum = item.EnumOptions.Single(option => Equals(option.Value, "JetBrains Mono"));
        Assert.True(item.TryCreateOperation(null, out var operation));
        Assert.Equal("JetBrains Mono", operation.Value?.GetValue<string>());

        item.SelectedEnum = "Comic Sans MS";
        Assert.False(item.TryCreateOperation(null, out _));
        Assert.NotEmpty(item.ErrorText);
    }

    [Fact]
    public void FontWiring_IsBoundAcrossViewsAndSettings()
    {
        var codeEditor = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/CodeDocumentView.xaml"));
        var diff = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/DiffDocumentView.xaml"));
        var terminalView = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/TerminalView.xaml"));
        var terminalSurface = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/Controls/TerminalSurfaceControl.cs"));
        var settingsView = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/Views/SettingsView.xaml"));
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/App.xaml"));
        var tabModel = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/ViewModels/EditorAreaViewModel.cs"));
        var terminalModel = File.ReadAllText(Path.Combine(RepoRoot, "src/Nornia.Desktop/ViewModels/TerminalViewModel.cs"));

        // 阅读器:FilePreviewTab/DiffTab 携带 FontFamily,内部阅读器绑定到该属性(不再是单一静态令牌)。
        Assert.Contains("FontFamily = options.FontFamily", tabModel);
        Assert.Contains("tab.FontFamily = code.FontFamily", tabModel);
        Assert.Contains("FontFamily=\"{Binding FontFamily}\"", codeEditor);
        Assert.Equal(3, diff.Split("FontFamily=\"{Binding FontFamily}\"").Length - 1);
        // 终端:表面新增 TerminalFontFamily 依赖属性并由 TerminalViewModel 喂值。
        Assert.Contains("TerminalFontFamily=\"{Binding FontFamily}\"", terminalView);
        Assert.Contains("TerminalFontFamilyProperty", terminalSurface);
        Assert.Contains("TerminalFontFamily.Id", terminalModel);
        Assert.Contains("FontFamily = string.IsNullOrWhiteSpace(family)", terminalModel);
        // 设置页:字体下拉框(带预览 + 未安装标记)通过 Font 编辑器渲染。
        Assert.Contains("IsFontEditor", settingsView);
        Assert.Contains("FontAvailabilityConverter", settingsView);
        Assert.Contains("FontAvailabilityConverter", app);
        Assert.Contains("（未安装）", settingsView);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string ResolveRepoRoot([CallerFilePath] string? sourcePath = null)
    {
        var testsDir = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException("无法定位测试源文件路径。");
        return Path.GetFullPath(Path.Combine(testsDir, "..", ".."));
    }
}