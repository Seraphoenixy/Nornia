using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using Nornia.Tests.Fakes;

namespace Nornia.Tests;

/// <summary>VS Code preview semantics for the editor area: file previews open as italic preview
/// tabs, are replaced by the next file open, and survive once promoted. Lightweight (no workbench).</summary>
public sealed class EditorPreviewSemanticsTests
{
    private static EditorAreaViewModel CreateEditor(out FakeUiLogService logs)
    {
        logs = new FakeUiLogService();
        return new EditorAreaViewModel(new FakeGitService(), logs);
    }

    private static string TempFile(string name, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "nornia-preview-" + Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task FilePreview_OpensAsPreviewTab()
    {
        var editor = CreateEditor(out _);
        var a = TempFile("a.cs", "class A {}");
        try
        {
            await editor.OpenFileAsync(a);
            var tab = Assert.IsType<FilePreviewTab>(editor.SelectedTab);
            Assert.True(tab.IsPreview);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(a)!, recursive: true);
        }
    }

    [Fact]
    public async Task FilePreview_IsReplacedByNextFileOpen()
    {
        var editor = CreateEditor(out _);
        var dir = Path.Combine(Path.GetTempPath(), "nornia-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.cs");
            var b = Path.Combine(dir, "b.cs");
            File.WriteAllText(a, "class A {}");
            File.WriteAllText(b, "class B {}");

            await editor.OpenFileAsync(a);
            await editor.OpenFileAsync(b);

            Assert.Single(editor.OpenTabs); // VS Code: 新文件替换当前预览标签
            Assert.Equal("b.cs", editor.SelectedTab?.Name);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task FilePreview_RegularTabSurvivesNextOpen()
    {
        var editor = CreateEditor(out _);
        var dir = Path.Combine(Path.GetTempPath(), "nornia-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.cs");
            var b = Path.Combine(dir, "b.cs");
            File.WriteAllText(a, "class A {}");
            File.WriteAllText(b, "class B {}");

            await editor.OpenFileAsync(a);
            editor.SelectedTab!.IsPreview = false; // 取消预览态
            await editor.OpenFileAsync(b);

            Assert.Equal(2, editor.OpenTabs.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReopeningSameFile_ActivatesExistingTab()
    {
        var editor = CreateEditor(out _);
        var dir = Path.Combine(Path.GetTempPath(), "nornia-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.cs");
            File.WriteAllText(a, "class A {}");

            await editor.OpenFileAsync(a);
            var first = editor.SelectedTab;
            await editor.OpenFileAsync(a);

            Assert.Single(editor.OpenTabs);
            Assert.Same(first, editor.SelectedTab);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>Phase-3 workbench tab-strip capabilities riding the shared MainViewModel fixture:
/// preview italic mirroring, pin survival, close-right / close-unpinned, drag reorder.</summary>
public sealed class WorkbenchTabCapabilitiesTests
{
    private sealed record FixtureWithDir(WorkbenchMainViewModelTests.Fixture Fixture, string Dir);

    private static async Task<FixtureWithDir> CreateWithOpenFiles(params string[] names)
    {
        var fixture = WorkbenchMainViewModelTests.Create();
        var dir = Path.Combine(Path.GetTempPath(), "nornia-tabs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var name in names)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, $"// {name}");
            await fixture.Editor.OpenFileAsync(path);
            // 取消预览态,避免下一个文件打开替换掉它(测试需要同时存在多个标签)。
            fixture.Editor.SelectedTab!.IsPreview = false;
        }

        return new FixtureWithDir(fixture, dir);
    }

    private static void Cleanup(FixtureWithDir fixture) => Directory.Delete(fixture.Dir, recursive: true);

    [Fact]
    public async Task EditorPreview_IsMirroredByWorkbenchTab()
    {
        var fixture = WorkbenchMainViewModelTests.Create();
        var dir = Path.Combine(Path.GetTempPath(), "nornia-tabs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "a.cs");
            File.WriteAllText(path, "// a.cs");
            await fixture.Editor.OpenFileAsync(path);

            var workbenchTab = fixture.Main.Workbench.Tabs.OfType<EditorWorkbenchTab>().Single();
            Assert.True(workbenchTab.IsPreview);

            // 固定 → 工作台标签镜像为非预览(斜体取消)
            fixture.Main.Workbench.TogglePinCommand.Execute(workbenchTab);
            Assert.True(workbenchTab.IsPinned);
            Assert.False(workbenchTab.IsPreview);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task PinnedPreview_SurvivesNextFileOpen()
    {
        // 回归:固定预览即常驻(固定具备实际保留语义)——斜体消失,且不再被下一次文件打开的
        // 预览槽替换;取消固定不会降级回预览。
        var fixture = WorkbenchMainViewModelTests.Create();
        var dir = Path.Combine(Path.GetTempPath(), "nornia-tabs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.cs");
            var b = Path.Combine(dir, "b.cs");
            File.WriteAllText(a, "// a.cs");
            File.WriteAllText(b, "// b.cs");

            await fixture.Editor.OpenFileAsync(a);
            var tabA = fixture.Main.Workbench.Tabs.OfType<EditorWorkbenchTab>().Single();
            Assert.True(tabA.IsPreview);

            fixture.Main.Workbench.TogglePinCommand.Execute(tabA);
            Assert.True(tabA.IsPinned);
            Assert.False(tabA.IsPreview); // 固定后预览态取消

            await fixture.Editor.OpenFileAsync(b);

            // 固定的 a.cs 与新预览 b.cs 共存,未被预览槽替换。
            var titles = fixture.Main.Workbench.Tabs.OfType<EditorWorkbenchTab>().Select(tab => tab.Title).ToArray();
            Assert.Contains("a.cs", titles);
            Assert.Contains("b.cs", titles);
            Assert.True(tabA.IsPinned);
            Assert.False(tabA.IsPreview);

            // 取消固定不降级:仍是常驻标签。
            fixture.Main.Workbench.TogglePinCommand.Execute(tabA);
            Assert.False(tabA.IsPinned);
            Assert.False(tabA.IsPreview);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task PinnedTab_SurvivesCloseOthers()
    {
        var fixture = await CreateWithOpenFiles("a.cs", "b.cs", "c.cs");
        try
        {
            var tabs = fixture.Fixture.Main.Workbench.Tabs.OfType<EditorWorkbenchTab>().ToArray();
            Assert.Equal(3, tabs.Length);
            var pinned = tabs[0];
            fixture.Fixture.Main.Workbench.TogglePinCommand.Execute(pinned);
            fixture.Fixture.Main.Workbench.CloseOtherTabsCommand.Execute(tabs[^1]); // keep c.cs

            // 剩余: 固定 a + keep c(启动不再自动打开页面标签,不参与编辑器断言)
            Assert.Contains(fixture.Fixture.Main.Workbench.Tabs, t => ReferenceEquals(t, pinned));
            Assert.Equal(tabs[^1], fixture.Fixture.Main.Workbench.SelectedTab);
            Assert.Equal(2, fixture.Fixture.Main.Workbench.Tabs.OfType<EditorWorkbenchTab>().Count());
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    [Fact]
    public async Task FileTabCloseCommand_ClosesTheExactUnderlyingFile()
    {
        var fixture = await CreateWithOpenFiles("a.cs", "b.cs", "c.cs");
        try
        {
            var tabs = fixture.Fixture.Main.Workbench.Tabs.OfType<EditorWorkbenchTab>().ToArray();
            var target = tabs[0];

            fixture.Fixture.Main.Workbench.CloseTabCommand.Execute(target);

            Assert.DoesNotContain(target, fixture.Fixture.Main.Workbench.Tabs);
            Assert.DoesNotContain(target.EditorTab, fixture.Fixture.Editor.OpenTabs);
            Assert.Contains(fixture.Fixture.Main.Workbench.Tabs.OfType<EditorWorkbenchTab>(),
                tab => tab.Title == "b.cs");
            Assert.Contains(fixture.Fixture.Main.Workbench.Tabs.OfType<EditorWorkbenchTab>(),
                tab => tab.Title == "c.cs");
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    [Fact]
    public async Task ClosingSelectedFileTab_SelectsAnotherOpenTab()
    {
        var fixture = await CreateWithOpenFiles("a.cs", "b.cs", "c.cs");
        try
        {
            var workbench = fixture.Fixture.Main.Workbench;
            var selected = Assert.IsType<EditorWorkbenchTab>(workbench.SelectedTab);

            workbench.CloseTabCommand.Execute(selected);

            Assert.NotEmpty(workbench.Tabs);
            Assert.NotNull(workbench.SelectedTab);
            Assert.NotSame(selected, workbench.SelectedTab);
            Assert.Contains(workbench.SelectedTab, workbench.Tabs);
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    [Fact]
    public async Task EachFileTabCloseCommand_ClosesItsOwnTab()
    {
        var fixture = await CreateWithOpenFiles("a.cs", "b.cs", "c.cs");
        try
        {
            var tabs = fixture.Fixture.Main.Workbench.Tabs.OfType<EditorWorkbenchTab>().ToArray();

            tabs[0].CloseCommand.Execute(null);
            Assert.DoesNotContain(tabs[0], fixture.Fixture.Main.Workbench.Tabs);
            Assert.Contains(tabs[1], fixture.Fixture.Main.Workbench.Tabs);

            tabs[1].CloseCommand.Execute(null);
            Assert.DoesNotContain(tabs[1], fixture.Fixture.Main.Workbench.Tabs);
            Assert.Contains(tabs[2], fixture.Fixture.Main.Workbench.Tabs);
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    [Fact]
    public async Task CloseAllTabsCommand_ClosesPagesAndFilesTogether()
    {
        var fixture = await CreateWithOpenFiles("a.cs", "b.cs");
        try
        {
            fixture.Fixture.Main.Workbench.CloseAllTabsCommand.Execute(null);

            Assert.Empty(fixture.Fixture.Editor.OpenTabs);
            Assert.Empty(fixture.Fixture.Main.Workbench.Tabs);
            Assert.Empty(fixture.Fixture.Main.Workbench.PageTabs);
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    [Fact]
    public async Task CloseTabsToTheRight_ClosesFollowingTabs()
    {
        var fixture = await CreateWithOpenFiles("a.cs", "b.cs", "c.cs", "d.cs");
        try
        {
            var tabs = fixture.Fixture.Main.Workbench.Tabs.OfType<EditorWorkbenchTab>().ToArray();
            fixture.Fixture.Main.Workbench.CloseTabsToTheRightCommand.Execute(tabs[0]); // 锚点 a

            Assert.Single(fixture.Fixture.Main.Workbench.Tabs.OfType<EditorWorkbenchTab>());
            Assert.Equal("a.cs", fixture.Fixture.Main.Workbench.Tabs.OfType<EditorWorkbenchTab>().Single().Title);
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    [Fact]
    public async Task CloseUnpinned_KeepsOnlyPinnedTabs()
    {
        var fixture = await CreateWithOpenFiles("a.cs", "b.cs", "c.cs");
        try
        {
            var tabs = fixture.Fixture.Main.Workbench.Tabs.OfType<EditorWorkbenchTab>().ToArray();
            fixture.Fixture.Main.Workbench.TogglePinCommand.Execute(tabs[1]); // 固定 b
            fixture.Fixture.Main.Workbench.CloseUnpinnedCommand.Execute(null);

            var remaining = fixture.Fixture.Main.Workbench.Tabs.OfType<EditorWorkbenchTab>().ToList();
            Assert.Single(remaining);
            Assert.Same(tabs[1], remaining[0]);
        }
        finally
        {
            Cleanup(fixture);
        }
    }

    [Fact]
    public async Task MoveTab_ReordersUnifiedStripWithoutChangingEditorGroupOrder()
    {
        var fixture = await CreateWithOpenFiles("a.cs", "b.cs", "c.cs");
        try
        {
            // 启动不再自动打开页面标签:条带 = [a, b, c]
            var fullTabs = fixture.Fixture.Main.Workbench.Tabs.ToList();
            Assert.Equal(3, fullTabs.Count);
            Assert.Equal(3, fullTabs.OfType<EditorWorkbenchTab>().Count());

            fixture.Fixture.Main.Workbench.MoveTabCommand.Execute(new MoveTabArgs(2, 0)); // c → 条带第1位

            Assert.Equal(new[] { "c.cs", "a.cs", "b.cs" },
                fixture.Fixture.Main.Workbench.Tabs.OfType<EditorWorkbenchTab>().Select(t => t.Title));
            Assert.Equal(new[] { "a.cs", "b.cs", "c.cs" },
                fixture.Fixture.Editor.OpenTabs.Select(t => t.Name));
            Assert.Equal(new[] { "c.cs", "a.cs", "b.cs" },
                fixture.Fixture.Main.Workbench.EditorTabs.Select(t => t.Title));
        }
        finally
        {
            Cleanup(fixture);
        }
    }
}
