using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nornia.Desktop.Commands;
using Nornia.Storage;

namespace Nornia.Desktop.Configuration;

/// <summary>One-shot, read-only v2 importer. Legacy DTOs never enter the runtime container.</summary>
public sealed class LegacySettingsMigrator(ISettingsService settings, IApplicationStateStore stateStore,
    IKeybindingService keybindings)
{
    private const int MigrationVersion = 3;
    // 旧 v2 文件与数据目录同根(Roaming\Nornia)。
    private static readonly string LegacyPath = Path.Combine(NorniaPaths.DataDirectory, "settings.json");

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        var state = await stateStore.LoadAsync(cancellationToken);
        if (state.SettingsMigrationVersion >= MigrationVersion) return;
        try { await keybindings.ReloadAsync(cancellationToken); }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return;
        }

        LegacyV2? old;
        try { old = await ReadLegacyAsync(cancellationToken); }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (old is not null)
        {
            var baseline = await settings.GetSnapshotAsync(new(), cancellationToken);
            var operations = new List<SettingOperation>
            {
                Set(BuiltInSettingsCatalog.ExternalEditor.Id, old.General.EditorCommand),
                Set(BuiltInSettingsCatalog.RestoreLastWorkspace.Id, old.General.RestoreLastWorkspace),
                Set(BuiltInSettingsCatalog.EnablePreview.Id, old.General.EnablePreviewTabs),
                Set(BuiltInSettingsCatalog.EditorLimitEnabled.Id, true),
                Set(BuiltInSettingsCatalog.EditorLimitValue.Id, old.General.MaxOpenTabs),
                Set(BuiltInSettingsCatalog.Theme.Id, EnumName(old.Theme, ["Dark", "Light", "HighContrast"], "Dark")),
                Set(BuiltInSettingsCatalog.Accent.Id, old.AccentColor ?? EnumName(old.Accent,
                    ["Default", "Teal", "Iris", "Custom"], "Default")),
                Set(BuiltInSettingsCatalog.UiScale.Id, old.UiFontScale),
                Set(BuiltInSettingsCatalog.EditorFontSize.Id, old.Reading.FontSize),
                Set(BuiltInSettingsCatalog.EditorWordWrap.Id, old.Reading.WordWrap ? "on" : "off"),
                Set(BuiltInSettingsCatalog.MinimapEnabled.Id, old.Reading.ShowMinimap),
                Set(BuiltInSettingsCatalog.LineNumbers.Id, old.Reading.ShowLineNumbers ? "on" : "off"),
                Set(BuiltInSettingsCatalog.IndentationGuides.Id, old.Reading.ShowIndentGuides),
                Set(BuiltInSettingsCatalog.Folding.Id, old.Reading.ShowFoldingControls),
                Set(BuiltInSettingsCatalog.DiffSideBySide.Id,
                    EnumName(old.Diff.DefaultLayout, ["Inline", "SideBySide"], "Inline") == "SideBySide"),
                Set(BuiltInSettingsCatalog.DiffHideUnchanged.Id, old.Diff.CollapseUnchangedContext),
                Set(BuiltInSettingsCatalog.DiffIntraline.Id, old.Diff.ShowIntralineChanges),
                Set(BuiltInSettingsCatalog.DiffOverview.Id, old.Diff.ShowOverviewRuler),
                Set(BuiltInSettingsCatalog.DiffSyncScroll.Id, old.Diff.SynchronizeScrolling),
                Set(BuiltInSettingsCatalog.TerminalFontSize.Id, old.Terminal.FontSize),
                Set(BuiltInSettingsCatalog.TerminalSidebarVisible.Id, old.Terminal.ShowSessionSidebar),
                Set(BuiltInSettingsCatalog.TerminalSidebarWidth.Id, old.Terminal.SessionSidebarWidth),
                Set(BuiltInSettingsCatalog.GitAutoRefresh.Id, old.Workspace.EnableScmAutoRefresh),
                Set(BuiltInSettingsCatalog.ExplorerTreeGuides.Id, old.Workspace.ShowTreeGuides),
                Set(BuiltInSettingsCatalog.PanelStartExpanded.Id, old.Layout.StartWithPanelExpanded),
                Set(BuiltInSettingsCatalog.SidebarDefaultWidth.Id, old.Layout.SidebarWidth),
                Set(BuiltInSettingsCatalog.PanelDefaultHeight.Id, old.Layout.PanelHeight),
            };
            var result = await settings.CommitAsync(new(baseline, SettingScope.User, operations), cancellationToken);
            if (!result.IsSuccess) return;

            var backup = Path.Combine(Path.GetDirectoryName(LegacyPath)!,
                $"settings.v2.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.backup.json");
            if (!File.Exists(backup)) File.Copy(LegacyPath, backup);

            await stateStore.CommitAsync(new([
                new(ApplicationStateField.WindowBounds, old.WindowBounds),
                new(ApplicationStateField.SidebarWidth, old.SidebarWidth ?? old.Layout.SidebarWidth),
                new(ApplicationStateField.PanelHeight, old.PanelHeight ?? old.Layout.PanelHeight),
                new(ApplicationStateField.ScmChangesHeight, old.ScmChangesHeight),
                new(ApplicationStateField.ScmGraphHeight, old.ScmGraphHeight),
                new(ApplicationStateField.LastWorkspace, old.WorkspacePath),
                new(ApplicationStateField.PanelVisible, old.Layout.StartWithPanelExpanded),
            ]), cancellationToken);
        }

        await stateStore.CommitAsync(new([
            new(ApplicationStateField.SettingsMigrationVersion, MigrationVersion),
        ]), cancellationToken);
    }

    private static async Task<LegacyV2?> ReadLegacyAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(LegacyPath)) return null;
        await using var stream = new FileStream(LegacyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            16 * 1024, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<LegacyV2>(stream, cancellationToken: cancellationToken);
    }

    private static SettingOperation Set<T>(string key, T value) => new(key, JsonValue.Create(value), false);

    private static string EnumName(JsonElement value, IReadOnlyList<string> names, string fallback)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var index) && index >= 0 && index < names.Count)
            return names[index];
        return fallback;
    }

    private sealed record LegacyV2
    {
        public JsonElement Theme { get; init; }
        public JsonElement Accent { get; init; }
        public string? AccentColor { get; init; }
        public double UiFontScale { get; init; } = 1;
        public string? WorkspacePath { get; init; }
        public WindowBoundsInfo? WindowBounds { get; init; }
        public double? SidebarWidth { get; init; }
        public double? PanelHeight { get; init; }
        public double? ScmChangesHeight { get; init; }
        public double? ScmGraphHeight { get; init; }
        public LegacyGeneral General { get; init; } = new();
        public LegacyReading Reading { get; init; } = new();
        public LegacyDiff Diff { get; init; } = new();
        public LegacyTerminal Terminal { get; init; } = new();
        public LegacyWorkspace Workspace { get; init; } = new();
        public LegacyLayout Layout { get; init; } = new();
    }
    private sealed record LegacyGeneral(string EditorCommand = "code", bool RestoreLastWorkspace = true,
        bool EnablePreviewTabs = true, int MaxOpenTabs = 30);
    private sealed record LegacyReading(bool WordWrap = false,
        bool ShowMinimap = false, double FontSize = 14, bool ShowLineNumbers = true,
        bool ShowIndentGuides = true, bool ShowFoldingControls = true);
    private sealed record LegacyDiff(JsonElement DefaultLayout = default, bool CollapseUnchangedContext = true,
        bool ShowIntralineChanges = true, bool ShowOverviewRuler = true, bool SynchronizeScrolling = true);
    private sealed record LegacyTerminal(double FontSize = 13, bool ShowSessionSidebar = true,
        double SessionSidebarWidth = 190);
    private sealed record LegacyWorkspace(bool ShowTreeGuides = true, bool EnableScmAutoRefresh = true);
    private sealed record LegacyLayout(double SidebarWidth = 300, double PanelHeight = 240,
        bool StartWithPanelExpanded = false);
}
