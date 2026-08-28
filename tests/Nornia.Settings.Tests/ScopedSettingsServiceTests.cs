using Nornia.Desktop.Commands;
using Nornia.Desktop.Configuration;
using System.Text.Json.Nodes;
using System.IO;
using Xunit;

namespace Nornia.Settings.Tests;

public sealed class ScopedSettingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nornia-settings-{Guid.NewGuid():N}");
    public ScopedSettingsServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task EffectiveValue_UsesUserWorkspaceAndLanguagePrecedence()
    {
        using var service = CreateService();
        var workspace = Path.Combine(_root, "repo");
        Directory.CreateDirectory(workspace);
        var context = new SettingsContext(workspace, "csharp");
        var snapshot = await service.GetSnapshotAsync(context);
        snapshot = AssertSuccess(await service.CommitAsync(SettingsTransaction.Set(snapshot, SettingScope.User,
            BuiltInSettingsCatalog.EditorFontSize, 15d)));
        snapshot = AssertSuccess(await service.CommitAsync(SettingsTransaction.Set(snapshot, SettingScope.Workspace,
            BuiltInSettingsCatalog.EditorFontSize, 16d)));
        snapshot = AssertSuccess(await service.CommitAsync(new(snapshot, SettingScope.User,
            [new(BuiltInSettingsCatalog.EditorFontSize.Id, JsonValue.Create(17d), LanguageId: "csharp")])));
        snapshot = AssertSuccess(await service.CommitAsync(new(snapshot, SettingScope.Workspace,
            [new(BuiltInSettingsCatalog.EditorFontSize.Id, JsonValue.Create(18d), LanguageId: "csharp")])));

        var value = snapshot.Get(BuiltInSettingsCatalog.EditorFontSize);
        Assert.Equal(18d, value.EffectiveValue);
        Assert.Equal(SettingValueSource.WorkspaceLanguage, value.Source);
    }

    [Fact]
    public async Task Commit_PreservesCommentsUnknownKeysAndTrailingComma()
    {
        var userPath = Path.Combine(_root, "settings.jsonc");
        await File.WriteAllTextAsync(userPath, "{\n  // keep me\n  \"extension.unknown\": { \"x\": 1 },\n  \"editor.fontSize\": 12,\n}\n");
        using var service = new ScopedSettingsService(new(), userPath);
        var baseline = await service.GetSnapshotAsync(new());
        var result = await service.CommitAsync(SettingsTransaction.Set(baseline, SettingScope.User,
            BuiltInSettingsCatalog.EditorFontSize, 14d));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        var text = await File.ReadAllTextAsync(userPath);
        Assert.Contains("// keep me", text);
        Assert.Contains("extension.unknown", text);
        Assert.Contains("\"editor.fontSize\": 14", text);
    }

    [Fact]
    public async Task DocumentStore_SerializesConcurrentPatchesWithoutLostUpdates()
    {
        var path = Path.Combine(_root, "settings.jsonc");
        await File.WriteAllTextAsync(path, "{ \"editor.fontSize\": 12 }");
        using var store = new SettingsDocumentStore();

        await Task.WhenAll(
            store.PatchAsync(path, [new(BuiltInSettingsCatalog.EditorFontSize.Id, JsonValue.Create(14d))]),
            store.PatchAsync(path, [new(BuiltInSettingsCatalog.EditorWordWrap.Id, JsonValue.Create("on"))]));

        var final = await store.ReadAsync(path);
        Assert.Equal(14d, final.ParsedRoot[BuiltInSettingsCatalog.EditorFontSize.Id]!.GetValue<double>());
        Assert.Equal("on", final.ParsedRoot[BuiltInSettingsCatalog.EditorWordWrap.Id]!.GetValue<string>());
    }

    [Fact]
    public async Task DocumentStore_CachesUnchangedFilesAndInvalidatesOnWrite()
    {
        var path = Path.Combine(_root, "settings.jsonc");
        await File.WriteAllTextAsync(path, "{ \"editor.fontSize\": 12 }");
        using var store = new SettingsDocumentStore();

        var first = await store.ReadAsync(path);
        var second = await store.ReadAsync(path);
        // S5: unchanged mtime/length → the same shared snapshot is returned (no re-read/re-parse).
        Assert.Same(first, second);

        // An external change (different length) misses the cache and yields fresh content.
        await File.WriteAllTextAsync(path, "{ \"editor.fontSize\": 14, \"a\": 1 }");
        var third = await store.ReadAsync(path);
        Assert.NotSame(first, third);
        Assert.Equal(14d, third.ParsedRoot[BuiltInSettingsCatalog.EditorFontSize.Id]!.GetValue<double>());

        // A commit through the store must never observe its own pre-write document.
        var patched = await store.PatchAsync(path, [new(BuiltInSettingsCatalog.EditorFontSize.Id, JsonValue.Create(16d))]);
        Assert.Equal(16d, patched.ParsedRoot[BuiltInSettingsCatalog.EditorFontSize.Id]!.GetValue<double>());
    }

    [Fact]
    public async Task Commit_DetectsSameFieldConflictButMergesOtherFields()
    {
        var userPath = Path.Combine(_root, "settings.jsonc");
        await File.WriteAllTextAsync(userPath, "{ \"editor.fontSize\": 12 }");
        using var service = new ScopedSettingsService(new(), userPath);
        var baseline = await service.GetSnapshotAsync(new());
        await File.WriteAllTextAsync(userPath, "{ \"editor.fontSize\": 13, \"unknown\": true }");
        var conflict = await service.CommitAsync(SettingsTransaction.Set(baseline, SettingScope.User,
            BuiltInSettingsCatalog.EditorFontSize, 14d));
        Assert.Equal(SettingsCommitStatus.Conflict, conflict.Status);

        baseline = await service.GetSnapshotAsync(new());
        await File.WriteAllTextAsync(userPath, "{ \"editor.fontSize\": 13, \"unknown\": false }");
        var merged = await service.CommitAsync(SettingsTransaction.Set(baseline, SettingScope.User,
            BuiltInSettingsCatalog.MinimapEnabled, true));
        Assert.True(merged.IsSuccess);
        Assert.Contains("\"unknown\": false", await File.ReadAllTextAsync(userPath));
    }

    [Fact]
    public async Task MalformedJsonc_ReportsDiagnosticAndNeverOverwrites()
    {
        var userPath = Path.Combine(_root, "settings.jsonc");
        const string malformed = "{ \"editor.fontSize\": }";
        await File.WriteAllTextAsync(userPath, malformed);
        using var service = new ScopedSettingsService(new(), userPath);
        var baseline = await service.GetSnapshotAsync(new());
        Assert.NotEmpty(baseline.Diagnostics);
        var result = await service.CommitAsync(SettingsTransaction.Set(baseline, SettingScope.User,
            BuiltInSettingsCatalog.EditorFontSize, 14d));
        Assert.Equal(SettingsCommitStatus.FileError, result.Status);
        Assert.Equal(malformed, await File.ReadAllTextAsync(userPath));
    }

    [Fact]
    public async Task MalformedReload_KeepsLastValidEffectiveSnapshot()
    {
        var userPath = Path.Combine(_root, "settings.jsonc");
        await File.WriteAllTextAsync(userPath, "{ \"editor.fontSize\": 19 }");
        using var service = new ScopedSettingsService(new(), userPath);
        Assert.Equal(19d, (await service.GetSnapshotAsync(new())).Effective(BuiltInSettingsCatalog.EditorFontSize));
        await File.WriteAllTextAsync(userPath, "{ \"editor.fontSize\": }");
        var fallback = await service.GetSnapshotAsync(new());
        Assert.Equal(19d, fallback.Effective(BuiltInSettingsCatalog.EditorFontSize));
        Assert.NotEmpty(fallback.Diagnostics);
    }

    [Fact]
    public async Task ApplicationState_IsStoredSeparatelyFromSettings()
    {
        var path = Path.Combine(_root, "state.json");
        using var store = new ApplicationStateStore(path);
        await store.CommitAsync(new ApplicationStateTransaction([
            new(ApplicationStateField.SidebarWidth, 412d),
            new(ApplicationStateField.LastWorkspace, "C:\\repo"),
        ]));
        await store.FlushAsync(); // 写盘现经去抖合批;断言磁盘内容前先显式落盘
        var state = await store.LoadAsync();
        Assert.Equal(412, state.SidebarWidth);
        Assert.Equal("C:\\repo", state.LastWorkspace);
        Assert.DoesNotContain("editor.fontSize", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task UnsupportedStandardValue_IsPreservedAndFallsBackWithDiagnostic()
    {
        var userPath = Path.Combine(_root, "settings.jsonc");
        await File.WriteAllTextAsync(userPath, "{ \"editor.wordWrap\": \"future-mode\" }");
        using var service = new ScopedSettingsService(new(), userPath);
        var snapshot = await service.GetSnapshotAsync(new());
        Assert.Equal("off", snapshot.Effective(BuiltInSettingsCatalog.EditorWordWrap));
        Assert.Contains(snapshot.Diagnostics, item => item.Message.Contains("editor.wordWrap"));
        Assert.Contains("future-mode", await File.ReadAllTextAsync(userPath));
    }

    private ScopedSettingsService CreateService() => new(new(), Path.Combine(_root, "settings.jsonc"));
    private static SettingsSnapshot AssertSuccess(SettingsCommitResult result)
    {
        Assert.True(result.IsSuccess, result.ErrorMessage);
        return Assert.IsType<SettingsSnapshot>(result.Snapshot);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

public sealed class CommandAndKeybindingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nornia-keybindings-{Guid.NewGuid():N}");

    [Fact]
    public void WhenExpression_SupportsBooleanComparisonRegexAndParentheses()
    {
        var context = new ContextKeyService();
        context.Set("editorTextFocus", true);
        context.Set("activeEditor", "diff-inline");
        Assert.True(context.Matches("editorTextFocus && (activeEditor =~ /diff/ || terminalFocus)"));
        Assert.True(context.Matches("!terminalFocus && activeEditor != code"));
    }

    [Fact]
    public async Task UserBindingOverridesDefaultAndChordDispatchesOnce()
    {
        Directory.CreateDirectory(_root);
        var registry = new CommandRegistry();
        var context = new ContextKeyService();
        var count = 0;
        registry.Register(new("test.run", "Run", "Test", "", [new("ctrl+r", "test.run", IsDefault: true)],
            (_) => { count++; return Task.CompletedTask; }));
        var service = new KeybindingService(registry, context, Path.Combine(_root, "keybindings.json"));
        await service.SaveUserBindingsAsync([new("ctrl+r", "-test.run"), new("ctrl+k ctrl+r", "test.run")]);

        Assert.False(await service.DispatchAsync("ctrl+r"));
        Assert.True(await service.DispatchAsync("ctrl+k"));
        Assert.True(await service.DispatchAsync("ctrl+r"));
        Assert.Equal(1, count);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
