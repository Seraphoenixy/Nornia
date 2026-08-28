using Nornia.Desktop.Commands;
using Nornia.Desktop.Configuration;
using Nornia.Desktop.Views;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;
using NSubstitute;

namespace Nornia.Tests;

public sealed class SettingsCenterTests
{
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
        // 壳先行架构:页面侧栏(含 SettingsSidebarView)由 MainWindow 代码在首帧后物化,
        // 注册点从 XAML 迁移到 code-behind。
        var mainCode = File.ReadAllText(Path.Combine(root, "src", "Nornia.Desktop", "Views", "MainWindow.xaml.cs"));
        var sidebar = File.ReadAllText(Path.Combine(root, "src", "Nornia.Desktop", "Views", "SettingsSidebarView.xaml"));
        var view = File.ReadAllText(Path.Combine(root, "src", "Nornia.Desktop", "Views", "SettingsView.xaml"));

        Assert.Contains("SettingsSidebarView", mainCode);
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
    public void SettingsView_TogglesCompactLayoutAtMeasuredWidth()
    {
        Assert.True(SettingsView.ShouldUseCompactLayout(SettingsView.CompactLayoutThreshold - 1));
        Assert.True(SettingsView.ShouldUseCompactLayout(0));
        Assert.False(SettingsView.ShouldUseCompactLayout(SettingsView.CompactLayoutThreshold));
        Assert.False(SettingsView.ShouldUseCompactLayout(SettingsView.CompactLayoutThreshold + 1));
    }
}
