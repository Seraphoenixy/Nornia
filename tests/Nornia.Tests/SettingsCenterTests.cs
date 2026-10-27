using Nornia.Desktop.Commands;
using Nornia.Desktop.Code;
using Nornia.Desktop.Configuration;
using Nornia.Desktop.Views;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;
using NSubstitute;

namespace Nornia.Tests;

public sealed class SettingsCenterTests
{
    [Fact]
    public async Task ReadingLimits_DefaultToTheCapacityConstants()
    {
        var service = new FakeSettingsService();
        var baseline = await service.GetSnapshotAsync(new SettingsContext());

        var limits = SettingsOptionsMapper.ReadingLimits(baseline);
        Assert.Equal(ReadOnlyContentLimits.Default, limits);
    }

    [Fact]
    public async Task ReadingLimits_CarryCommittedThresholdOverrides()
    {
        var service = new FakeSettingsService();
        var context = new SettingsContext();
        var baseline = await service.GetSnapshotAsync(context);

        var commit = await service.CommitAsync(SettingsTransaction.Set(baseline, SettingScope.User,
            BuiltInSettingsCatalog.ReadingFullSourceMb, 2));
        Assert.True(commit.IsSuccess);

        var limits = SettingsOptionsMapper.ReadingLimits(await service.GetSnapshotAsync(context));
        Assert.Equal(2L * 1024 * 1024, limits.FullSourceBytes);
        // 未覆盖的阈值保持默认。
        Assert.Equal(ReadOnlyContentLimits.Default.WindowedSourceBytes, limits.WindowedSourceBytes);
        Assert.Equal(ReadOnlyContentLimits.Default.FullDiffLines, limits.FullDiffLines);
        Assert.Equal(ReadOnlyContentLimits.Default.WindowedDiffLines, limits.WindowedDiffLines);
    }

    [Fact]
    public async Task SettingsService_CommitsAndPersistsEditorFontSize()
    {
        var service = new FakeSettingsService();
        var context = new SettingsContext();

        var baseline = await service.GetSnapshotAsync(context);
        Assert.Equal(14d, baseline.Effective(BuiltInSettingsCatalog.EditorFontSize));

        var commit = await service.CommitAsync(SettingsTransaction.Set(baseline, SettingScope.User,
            BuiltInSettingsCatalog.EditorFontSize, 18d));
        Assert.True(commit.IsSuccess);

        var after = await service.GetSnapshotAsync(context);
        Assert.Equal(18d, after.Effective(BuiltInSettingsCatalog.EditorFontSize));
    }

    [Fact]
    public async Task GlobalSettingsSession_ReceivesUserChangesCommittedFromWorkspaceContext()
    {
        var service = new FakeSettingsService();
        await using var globalSession = await service.OpenSessionAsync(new(),
            [BuiltInSettingsCatalog.Theme.Id]);
        var changed = new TaskCompletionSource<SettingsChangeSet>(TaskCreationOptions.RunContinuationsAsynchronously);
        globalSession.Changed += (_, change) => changed.TrySetResult(change);

        var workspace = new SettingsContext(TestTempRoot.NewDirectory("settings-workspace"));
        var baseline = await service.GetSnapshotAsync(workspace);
        var commit = await service.CommitAsync(SettingsTransaction.Set(baseline, SettingScope.User,
            BuiltInSettingsCatalog.Theme, "Light"));

        Assert.True(commit.IsSuccess);
        var change = await changed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(change.Changes, item => item.Key == BuiltInSettingsCatalog.Theme.Id &&
            Equals(item.NewValue, "Light"));
    }

    [Fact]
    public async Task SettingsService_RejectsValuesOutsideAllowedRange()
    {
        var service = new FakeSettingsService();
        var context = new SettingsContext();
        var baseline = await service.GetSnapshotAsync(context);

        var commit = await service.CommitAsync(SettingsTransaction.Set(baseline, SettingScope.User,
            BuiltInSettingsCatalog.EditorFontSize, 99d));

        Assert.False(commit.IsSuccess);
        Assert.Equal(SettingsCommitStatus.ValidationFailed, commit.Status);
        Assert.True(commit.ValidationErrors!.ContainsKey(BuiltInSettingsCatalog.EditorFontSize.Id));
    }

    [Fact]
    public async Task SettingsEditorViewModel_LoadsCatalogItemsAndCategories()
    {
        var catalog = new BuiltInSettingsCatalog();
        var editor = new SettingsEditorViewModel(
            new FakeSettingsService(),
            catalog,
            new FakeProjectWorkspaceService(),
            Substitute.For<IKeybindingService>(),
            Substitute.For<ICommandRegistry>(),
            new FakeApplicationStateStore());

        await editor.InitializeAsync();

        Assert.Equal(catalog.Definitions.Count, editor.Items.Count);
        Assert.Equal("全部", editor.Categories[0].Name);
        // 固定且有意义的分类顺序:常用、工作台、编辑器、Diff、终端、工作区与源代码管理、外观、布局。
        Assert.Equal(new[] { "常用", "工作台", "编辑器", "Diff", "终端", "工作区与源代码管理", "外观", "布局" },
            editor.Categories.Skip(1).Select(category => category.Name));
        Assert.Equal(catalog.Definitions.Values.Select(item => item.Category).Distinct().Count() + 1,
            editor.Categories.Count);
        Assert.Contains(editor.Items, item => item.Id == BuiltInSettingsCatalog.Accent.Id);
        Assert.DoesNotContain(editor.Items, item => item.IsModified);
    }

    [Fact]
    public void ViewModel_ExposesEditorAsItsSidebar()
    {
        var editor = new SettingsEditorViewModel(
            new FakeSettingsService(),
            new BuiltInSettingsCatalog(),
            new FakeProjectWorkspaceService(),
            Substitute.For<IKeybindingService>(),
            Substitute.For<ICommandRegistry>(),
            new FakeApplicationStateStore());

        var viewModel = new SettingsViewModel(editor, new FakeUiLogService());

        Assert.Same(editor, viewModel.Editor);
        Assert.Same(editor, viewModel.Sidebar);
    }

    [Fact]
    public void Views_RegisterSettingsSidebarAndAutoSaveStatus()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        // 壳先行架构:页面侧栏由 MainWindow 在首帧后物化;视图类型与页 VM 的对应关系
        // 现在登记在 App.xaml 的隐式 DataTemplate 里(新增页面 = 加一个模板,不必改窗口代码)。
        var app = File.ReadAllText(Path.Combine(root, "src", "Nornia.Desktop", "App.xaml"));
        var sidebar = File.ReadAllText(Path.Combine(root, "src", "Nornia.Desktop", "Views", "SettingsSidebarView.xaml"));
        var view = File.ReadAllText(Path.Combine(root, "src", "Nornia.Desktop", "Views", "SettingsView.xaml"));

        Assert.Contains("<views:SettingsSidebarView />", app);
        Assert.Contains("{x:Type vm:SettingsEditorViewModel}", app);
        Assert.Contains("SearchText", sidebar);
        Assert.Contains("Editor.SaveStatus", view);
        Assert.DoesNotContain("SaveCommand", view);
        Assert.Contains("ResetCurrentCategoryCommand", view);
        Assert.Contains("ItemsSource=\"{Binding Editor.ShortcutView}\" BorderThickness=\"0\" Background=\"Transparent\"", view);
        Assert.Contains("VerticalAlignment=\"Center\" VerticalContentAlignment=\"Center\"", view);
    }

    [Fact]
    public void SettingsView_UsesAdaptiveCardLayouts()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var view = File.ReadAllText(Path.Combine(root, "src", "Nornia.Desktop", "Views", "SettingsView.xaml"));

        Assert.Contains("SettingsCardWideTemplate", view);
        Assert.Contains("<ColumnDefinition Width=\"340\"", view);
        Assert.Contains("SettingsMetadataTemplate", view);
        Assert.Contains("Grid.Column=\"1\" FontSize=\"{DynamicResource TypeBody}\"", view);
        Assert.Contains("SettingsCardCompactTemplate", view);
        Assert.Contains("Grid.Row=\"1\" Grid.ColumnSpan=\"2\"", view);
        Assert.Contains("SettingsCardContentStyle", view);
    }

    [Fact]
    public void SettingsView_UsesThemeAwareSelectionContainer()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var view = File.ReadAllText(Path.Combine(root, "src", "Nornia.Desktop", "Views", "SettingsView.xaml"));

        // The settings cards are hosted by a ListBox for virtualization. Its local layout style
        // must inherit the application ListBoxItem template, otherwise WPF shows the native
        // blue/white selection surface instead of the active theme's selection layer.
        Assert.Contains("<Style TargetType=\"ListBoxItem\" BasedOn=\"{StaticResource {x:Type ListBoxItem}}\">", view);
    }

    [Fact]
    public void SettingsView_TogglesCompactLayoutAtMeasuredWidth()
    {
        Assert.True(SettingsView.ShouldUseCompactLayout(SettingsView.CompactLayoutThreshold - 1));
        Assert.True(SettingsView.ShouldUseCompactLayout(0));
        Assert.False(SettingsView.ShouldUseCompactLayout(SettingsView.CompactLayoutThreshold));
        Assert.False(SettingsView.ShouldUseCompactLayout(SettingsView.CompactLayoutThreshold + 1));
    }
}
