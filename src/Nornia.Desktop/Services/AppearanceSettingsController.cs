using Nornia.Desktop.Configuration;

namespace Nornia.Desktop.Services;

/// <summary>Applies the user-scoped appearance snapshot, including external JSONC edits.</summary>
public sealed class AppearanceSettingsController(ISettingsService settings) : IAsyncDisposable, IDisposable
{
    private ISettingsSession? _session;
    private static readonly RevisionGate RevisionGate = new();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _session = await settings.OpenSessionAsync(new(),
        [
            BuiltInSettingsCatalog.Theme.Id,
            BuiltInSettingsCatalog.Accent.Id,
            BuiltInSettingsCatalog.UiScale.Id,
        ], cancellationToken);
        _session.Changed += OnChanged;
        Apply(_session.Current!);
    }

    private static void OnChanged(object? sender, SettingsChangeSet change)
    {
        if (sender is ISettingsSession { Current: { } snapshot }) Apply(snapshot);
    }

    private static void Apply(SettingsSnapshot snapshot)
    {
        if (!RevisionGate.TryAccept(snapshot)) return;
        void Update()
        {
            var themeName = snapshot.Effective(BuiltInSettingsCatalog.Theme);
            ThemeService.Apply(Enum.TryParse<AppTheme>(themeName, true, out var theme) ? theme : AppTheme.Dark);
            ThemeFactory.ApplyAccent(snapshot.Effective(BuiltInSettingsCatalog.Accent));
            UiFontService.Apply(snapshot.Effective(BuiltInSettingsCatalog.UiScale));
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Update(); else dispatcher.BeginInvoke(Update);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is null) return;
        _session.Changed -= OnChanged;
        await _session.DisposeAsync();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
