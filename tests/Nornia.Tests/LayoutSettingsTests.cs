using Nornia.Desktop.Configuration;

namespace Nornia.Tests;

/// <summary>Layout persistence through the NEW ApplicationStateStore: window placement and pane
/// dimensions must round-trip through the transactional state file, missing files fall back to
/// defaults, and a layout reset clears only the layout fields.</summary>
public sealed class LayoutSettingsTests
{
    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"nornia-layout-{name}-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task ApplicationStateStore_RoundTripsLayoutFields()
    {
        var path = TempPath("roundtrip");
        try
        {
            var layout = new ApplicationState(
                WindowBounds: new WindowBoundsInfo(40, 30, 1280, 800, Maximized: true),
                SidebarWidth: 340,
                PanelHeight: 260,
                ScmChangesHeight: 320,
                ScmGraphHeight: 240);
            using (var store = new ApplicationStateStore(path))
            {
                await store.CommitAsync(new(new[]
                {
                    new ApplicationStateOperation(ApplicationStateField.WindowBounds, layout.WindowBounds),
                    new ApplicationStateOperation(ApplicationStateField.SidebarWidth, layout.SidebarWidth),
                    new ApplicationStateOperation(ApplicationStateField.PanelHeight, layout.PanelHeight),
                    new ApplicationStateOperation(ApplicationStateField.ScmChangesHeight, layout.ScmChangesHeight),
                    new ApplicationStateOperation(ApplicationStateField.ScmGraphHeight, layout.ScmGraphHeight),
                    new ApplicationStateOperation(ApplicationStateField.SearchResultsViewMode, "List"),
                    new ApplicationStateOperation(ApplicationStateField.ScmLayout, "Tree"),
                }));
            }

            using var reloaded = new ApplicationStateStore(path);
            var restored = await reloaded.LoadAsync();

            Assert.NotNull(restored);
            Assert.Equal(layout.WindowBounds, restored.WindowBounds);
            Assert.Equal(340d, restored.SidebarWidth);
            Assert.Equal(260d, restored.PanelHeight);
            Assert.Equal(320d, restored.ScmChangesHeight);
            Assert.Equal(240d, restored.ScmGraphHeight);
            Assert.Equal("List", restored.SearchResultsViewMode);
            Assert.Equal("Tree", restored.ScmLayout);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task LoadAsync_WhenNoStateFileExistsReturnsDefaults()
    {
        var path = TempPath("missing");
        try
        {
            using var store = new ApplicationStateStore(path);
            var state = await store.LoadAsync();

            Assert.NotNull(state);
            Assert.Null(state.WindowBounds);
            Assert.Null(state.SidebarWidth);
            Assert.Null(state.PanelHeight);
            Assert.Null(state.ScmChangesHeight);
            Assert.Null(state.ScmGraphHeight);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ResetLayoutAsync_ClearsLayoutButKeepsOtherFields()
    {
        var path = TempPath("reset");
        try
        {
            using (var store = new ApplicationStateStore(path))
            {
                await store.CommitAsync(new(new[]
                {
                    new ApplicationStateOperation(ApplicationStateField.SidebarWidth, 420d),
                    new ApplicationStateOperation(ApplicationStateField.PanelHeight, 300d),
                    new ApplicationStateOperation(ApplicationStateField.SearchResultsViewMode, "List"),
                    new ApplicationStateOperation(ApplicationStateField.ScmLayout, "Tree"),
                    new ApplicationStateOperation(ApplicationStateField.LastWorkspace, @"C:\ws"),
                }));
                await store.ResetLayoutAsync();
            }

            using var reloaded = new ApplicationStateStore(path);
            var state = await reloaded.LoadAsync();

            Assert.Null(state.WindowBounds);
            Assert.Null(state.SidebarWidth);
            Assert.Null(state.PanelHeight);
            Assert.Null(state.ScmChangesHeight);
            Assert.Null(state.ScmGraphHeight);
            Assert.Null(state.SearchResultsViewMode);
            Assert.Null(state.ScmLayout);
            Assert.Equal(@"C:\ws", state.LastWorkspace);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task PanelMaximized_RoundTripsThroughStore()
    {
        var path = TempPath("maximized");
        try
        {
            using (var store = new ApplicationStateStore(path))
            {
                await store.CommitAsync(new(new[]
                {
                    new ApplicationStateOperation(ApplicationStateField.PanelMaximized, true),
                    new ApplicationStateOperation(ApplicationStateField.PanelHeight, 300d),
                }));
            }

            using var reloaded = new ApplicationStateStore(path);
            var state = await reloaded.LoadAsync();

            Assert.True(state.PanelMaximized);
            Assert.Equal(300d, state.PanelHeight);
            Assert.Equal(ApplicationState.CurrentSchemaVersion, state.SchemaVersion);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task WorkspaceEditorLayout_RoundTripsThroughStore()
    {
        var path = TempPath("editorlayout");
        try
        {
            var layout = new EditorLayoutState(
                Orientation: "vertical",
                Children:
                [
                    new EditorLayoutState(GroupId: "g1",
                        Tabs: [new EditorTabState("file:C:\\a.txt", IsPreview: true)],
                        ActiveTabKey: "file:C:\\a.txt",
                        Mru: ["file:C:\\a.txt"]),
                    new EditorLayoutState(GroupId: "g2",
                        Tabs: [new EditorTabState("diff:src/A.cs", RepositoryPath: @"C:\repo",
                            DiffPath: "src/A.cs", IsStaged: true)]),
                ],
                Weights: [0.4, 0.6],
                ActiveGroupId: "g2",
                GroupMru: ["g2", "g1"]);
            using (var store = new ApplicationStateStore(path))
            {
                await store.CommitAsync(new(new[]
                {
                    new ApplicationStateOperation(ApplicationStateField.WorkspaceEditorLayout, layout, @"C:\ws"),
                }));
            }

            using var reloaded = new ApplicationStateStore(path);
            var state = await reloaded.LoadAsync();
            var restored = Assert.Single(state.Workspaces!.Values).EditorLayout;

            Assert.NotNull(restored);
            Assert.Equal("vertical", restored!.Orientation);
            Assert.Equal(2, restored.Children!.Count);
            Assert.Equal(0.4, restored.Weights![0], 6);
            Assert.True(restored.Children![0].Tabs![0].IsPreview);
            Assert.Equal(["file:C:\\a.txt"], restored.Children![0].Mru);
            Assert.True(restored.Children[1].Tabs![0].IsStaged);
            Assert.Equal("g2", restored.ActiveGroupId);
            Assert.Equal(["g2", "g1"], restored.GroupMru);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
