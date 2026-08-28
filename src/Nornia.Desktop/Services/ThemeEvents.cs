namespace Nornia.Desktop.Services;

/// <summary>
/// Broadcast notification when the active theme changes at runtime. Code-facing views (AvalonEdit
/// chrome, syntax-highlighting palette, git graph lanes, terminal palette) subscribe and re-resolve
/// their brushes instead of waiting for a restart — this closes the "already opened pages apply the
/// new theme only after restart" gap documented on <see cref="ThemeService"/>.
/// </summary>
public static class ThemeEvents
{
    public static event EventHandler<AppTheme>? ThemeChanged;

    public static void Raise(AppTheme theme) => ThemeChanged?.Invoke(null, theme);
}