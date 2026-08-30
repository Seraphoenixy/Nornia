using Nornia.Desktop.Code;
using Nornia.Desktop.Configuration;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>Preview-tab outline: built for C# / XML / JSON, hidden for plain text, filterable, and
/// jumps raise the go-to-line request.</summary>
public sealed class FilePreviewOutlineTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nornia-outline-{Guid.NewGuid():N}");

    public FilePreviewOutlineTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }

    private async Task<FilePreviewTab> OpenAsync(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        await File.WriteAllTextAsync(path, content);
        var tab = new FilePreviewTab(path, TextDocumentDecoder.Instance, CodeFileTypeRegistry.Instance);
        await tab.LoadAsync();
        return tab;
    }

    [Fact]
    public async Task CSharpFile_BuildsOutlineWithTypesAndMembers()
    {
        var tab = await OpenAsync("Calc.cs", """
            namespace Demo;

            public sealed class Calculator
            {
                public int Add(int a, int b) => a + b;

                public string Label { get; set; }
            }
            """);

        Assert.True(tab.HasOutline);
        Assert.Contains(tab.OutlineEntries, e => e.Kind == "namespace" && e.Name == "Demo");
        Assert.Contains(tab.OutlineEntries, e => e.Kind == "type" && e.Name == "Calculator");
        Assert.Contains(tab.OutlineEntries, e => e.Kind == "member" && e.Name == "Add");
        Assert.Contains(tab.OutlineEntries, e => e.Kind == "member" && e.Name == "Label");
    }

    [Fact]
    public async Task PlainText_HasNoOutline()
    {
        var tab = await OpenAsync("readme.txt", "just some words\nnothing structured");

        Assert.False(tab.HasOutline);
        Assert.Empty(tab.OutlineEntries);
    }

    [Fact]
    public async Task OutlineFilter_RestrictsSymbolList()
    {
        var tab = await OpenAsync("Big.cs", "class First { void A() { } void B() { } }\nclass Second { }");
        Assert.True(tab.HasOutline);

        tab.OutlineFilterText = "Second";

        Assert.Single(tab.FilteredOutlineEntries);
        Assert.Equal("Second", tab.FilteredOutlineEntries[0].Name);

        tab.OutlineFilterText = string.Empty;
        Assert.Equal(tab.OutlineEntries.Count, tab.FilteredOutlineEntries.Count);
    }

    [Fact]
    public async Task OutlineJump_RaisesGoToLineForEntry()
    {
        var tab = await OpenAsync("Jump.cs", "class Target\n{\n    void Hit() { }\n}\n");
        var jumps = new List<int>();
        tab.GoToLineRequested += (_, line) => jumps.Add(line);
        var entry = tab.OutlineEntries.First(e => e.Name == "Hit");

        tab.JumpToOutlineCommand.Execute(entry);

        Assert.Contains(3, jumps);
    }
}

/// <summary>Reading preferences: persisted options seed new previews and toggles save back.</summary>
public sealed class ReadingOptionsPersistenceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"nornia-options-{Guid.NewGuid():N}");

    public ReadingOptionsPersistenceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }

    private static EditorAreaViewModel CreateEditor(FakeSettingsService? settings) =>
        settings is null
            ? new(
                new FakeGitService(),
                new FakeUiLogService(),
                new FakeClipboardService(),
                CodeFileTypeRegistry.Instance,
                TextDocumentDecoder.Instance,
                CodeOutlineParser.Instance,
                TextSearchService.Instance,
                projectLauncher: null)
            : new(
                new FakeGitService(),
                new FakeUiLogService(),
                new FakeClipboardService(),
                CodeFileTypeRegistry.Instance,
                TextDocumentDecoder.Instance,
                CodeOutlineParser.Instance,
                TextSearchService.Instance,
                projectLauncher: new FakeProjectLauncher(),
                settings,
                new FakeProjectWorkspaceService(),
                new FakeApplicationStateStore());

    private static async Task ApplyReadingOptionsAsync(FakeSettingsService settings,
        bool wordWrap, double fontSize, bool showMinimap)
    {
        var baseline = await settings.GetSnapshotAsync(new());
        await settings.CommitAsync(SettingsTransaction.Set(baseline, SettingScope.User,
            BuiltInSettingsCatalog.EditorWordWrap, wordWrap ? "on" : "off"));
        await settings.CommitAsync(SettingsTransaction.Set(baseline, SettingScope.User,
            BuiltInSettingsCatalog.EditorFontSize, fontSize));
        await settings.CommitAsync(SettingsTransaction.Set(baseline, SettingScope.User,
            BuiltInSettingsCatalog.MinimapEnabled, showMinimap));
    }

    private async Task<string> WriteFileAsync(string content)
    {
        var path = Path.Combine(_tempDir, "sample.cs");
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    [Fact]
    public async Task OpenFileAsync_AppliesPersistedReadingOptions()
    {
        var settings = new FakeSettingsService();
        await ApplyReadingOptionsAsync(settings, wordWrap: true, fontSize: 16, showMinimap: true);
        var editor = CreateEditor(settings);
        var path = await WriteFileAsync("class A { }");

        await editor.OpenFileAsync(path);

        var tab = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));
        Assert.True(tab.WordWrap);
        Assert.Equal(16, tab.FontSize);
        Assert.True(tab.ShowMinimap);
    }

    [Fact]
    public async Task OpenFileAsync_ScalesEditorFontWithUiScale()
    {
        var settings = new FakeSettingsService();
        var baseline = await settings.GetSnapshotAsync(new());
        await settings.CommitAsync(SettingsTransaction.Set(baseline, SettingScope.User,
            BuiltInSettingsCatalog.UiScale, 1.3));
        var editor = CreateEditor(settings);
        var path = await WriteFileAsync("class A { }");

        await editor.OpenFileAsync(path);

        // 显示字号 = 作者字号(默认 14) × 界面缩放 1.3,使源码/Diff 阅读器跟随分辨率缩放。
        var tab = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));
        Assert.Equal(18.2, tab.FontSize, 10);
    }

    [Fact]
    public async Task TogglingOptions_PersistsBackToSettings()
    {
        var settings = new FakeSettingsService();
        var editor = CreateEditor(settings);
        var path = await WriteFileAsync("class A { }");
        await editor.OpenFileAsync(path);
        var tab = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));
        var languageId = tab.FileType.LanguageId;

        tab.ToggleWordWrapCommand.Execute(null);
        tab.ToggleShowMinimapCommand.Execute(null);
        tab.FontSize = 19;

        var persisted = false;
        // 预算 30 秒:并行测试负载下线程池饥饿偶发延长提交(此前 300×5ms 偶发 flake),
        // 2 核 CI 全量套件下实测会超过 10s;单次提交实际在毫秒级,大预算不影响测试灵敏度。
        for (var i = 0; i < 6000; i++)
        {
            var snapshot = await settings.GetSnapshotAsync(new SettingsContext { LanguageId = languageId });
            if (snapshot.Effective(BuiltInSettingsCatalog.EditorWordWrap) != "off"
                && snapshot.Effective(BuiltInSettingsCatalog.MinimapEnabled)
                && snapshot.Effective(BuiltInSettingsCatalog.EditorFontSize) == 19d)
            {
                persisted = true;
                break;
            }

            await Task.Delay(5);
        }

        Assert.True(persisted);
        var saved = await settings.GetSnapshotAsync(new SettingsContext { LanguageId = languageId });
        Assert.True(saved.Effective(BuiltInSettingsCatalog.EditorWordWrap) != "off");
        Assert.True(saved.Effective(BuiltInSettingsCatalog.MinimapEnabled));
        Assert.Equal(19d, saved.Effective(BuiltInSettingsCatalog.EditorFontSize));
    }

    [Fact]
    public async Task WithoutSettingsService_UsesDefaults()
    {
        var editor = CreateEditor(settings: null);
        var path = await WriteFileAsync("class A { }");

        await editor.OpenFileAsync(path);

        var tab = Assert.IsType<FilePreviewTab>(Assert.Single(editor.OpenTabs));
        Assert.False(tab.WordWrap);
        Assert.Equal(14, tab.FontSize);
    }
}
