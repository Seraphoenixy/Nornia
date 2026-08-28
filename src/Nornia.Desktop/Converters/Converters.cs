using Nornia.Core.Models;
using Nornia.Desktop.Code;
using Nornia.Desktop.Localization;
using Nornia.Desktop.Services;
using Nornia.Desktop.ViewModels;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Nornia.Desktop.Converters;

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? false : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? false : true;
}

/// <summary>Localized label for the accent preset combo (kept next to the theme label converter).</summary>
public sealed class AccentPresetToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        AccentPreset.Teal => "青色",
        AccentPreset.Iris => "鸢尾紫",
        AccentPreset.Custom => "自定义",
        _ => "跟随主题",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>字体下拉框项:绑定字体族名,本机未安装时显示「（未安装）」标记。</summary>
public sealed class FontAvailabilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string family && FontCatalog.IsInstalled(family) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps an outline depth (int) to a left indent for the symbol list rows.</summary>
public sealed class OutlineIndentConverter : IValueConverter
{
    private const double IndentPerDepth = 12;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new Thickness(Math.Max(0, value is int depth ? depth : 0) * IndentPerDepth, 0, 5, 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Returns Visible when the bound count (int) is 0, otherwise Collapsed.
/// Used to show an empty-state placeholder behind a list that has no items.
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ProjectAssetDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            ProjectPathStatus.Available => "正常",
            ProjectPathStatus.Missing => "路径缺失",
            EnvironmentHealthStatus.Unknown => "未检查",
            EnvironmentHealthStatus.Healthy => "通过",
            EnvironmentHealthStatus.NeedsAttention => "需处理",
            long timestamp => DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            _ => "—"
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps a status value (enum or string, case-insensitive) to a semantic theme brush:
/// Pass / Healthy / Installed / Available -> Success; Warning / NeedsAttention / UpdateAvailable / Review -> Warning;
/// Fail / Missing / Error -> Danger; everything else -> default text brush.
/// Resolves brushes from the application resource dictionary to keep a single source of truth.
/// </summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var token = value switch
        {
            Enum e => e.ToString(),
            string s => s,
            _ => string.Empty
        };

        var brushKey = token.Trim().ToLowerInvariant() switch
        {
            "pass" or "healthy" or "installed" or "available" or "ok" or "success" => "SuccessBrush",
            "warning" or "needsattention" or "updateavailable" or "review" => "WarningBrush",
            // errorForeground (#F85149 in Dark Modern) rather than the button-danger surface
            "fail" or "missing" or "error" => "ErrorTextBrush",
            _ => "TextBrush"
        };

        if (Application.Current.Resources[brushKey] is SolidColorBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Adds 1 to a zero-based integer (e.g. an <see cref="ItemsControl.AlternationIndex"/>),
/// rendered as a string. Used to show 1-based keyboard accelerator badges (1-8) on nav items.
/// </summary>
public sealed class AddOneConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i ? (i + 1).ToString(culture) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Renders a <see cref="GitChangeStatus"/> as its porcelain status letter (M/A/D/R/...).</summary>
public sealed class GitStatusLetterConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is GitChangeStatus status ? status.ToStatusLetter().ToString() : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a <see cref="StatusKind"/> to a foreground brush for the status-bar feedback (U2).
/// Colors are resolved from the active theme dictionary (StatusBar*Brush tokens) so the feedback
/// stays legible on the Dark (#181818), Light (blue #0066B8) and High Contrast status bars.</summary>
public sealed class StatusKindToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var brushKey = value is StatusKind kind ? kind switch
        {
            StatusKind.Success => "StatusBarSuccessBrush",
            StatusKind.Warning => "StatusBarWarningBrush",
            StatusKind.Error => "StatusBarErrorBrush",
            _ => "StatusBarForegroundBrush",
        } : "StatusBarForegroundBrush";

        return Application.Current.Resources[brushKey] is Brush brush ? brush : Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a <see cref="StatusKind"/> to a Codicon for the status-bar feedback (U2).</summary>
public sealed class StatusKindToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is StatusKind kind ? kind switch
        {
            StatusKind.Success => Codicons.Check,
            StatusKind.Warning => Codicons.Warning,
            StatusKind.Error => Codicons.Error,
            _ => string.Empty,
        } : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps project health / path status values to a Codicon for project list badges:
/// Healthy/Available -> CheckMark, NeedsAttention -> Warning, Unknown -> Help, Missing -> Cancel.</summary>
public sealed class HealthToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        EnvironmentHealthStatus.Healthy => Codicons.Check,
        EnvironmentHealthStatus.NeedsAttention => Codicons.Warning,
        EnvironmentHealthStatus.Unknown => Codicons.Info,
        ProjectPathStatus.Available => Codicons.Check,
        ProjectPathStatus.Missing => Codicons.Remove,
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapses when the bound string is null/empty/whitespace (used for optional empty-state descriptions, U5).</summary>
public sealed class StringEmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapses when the bound value is null (used to hide placeholder chrome).</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible only when the bound count is greater than zero (the inverse of
/// <see cref="CountToVisibilityConverter"/>; drives ↑/↓ sync badges and other "has items" chrome).</summary>
public sealed class NonZeroCountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Controls a section count badge that may reserve its right-edge slot even when the
/// count is zero. The first binding is the count and the second is the opt-in flag.</summary>
public sealed class BadgeCountVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = values.Length > 0 && values[0] is int value ? value : 0;
        var alwaysShow = values.Length > 1 && values[1] is true;
        return alwaysShow || count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>侧栏预建视图缓存:仅当 <paramref name="parameter"/>(x:Type)与当前页的
/// Sidebar 对象类型匹配时可见。页面切换只切换 Visibility,视觉树不再重建
/// (见 MainWindow.xaml 侧栏缓存区),滚动位置/展开状态得以保留。</summary>
public sealed class SidebarVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is Type type && type.IsInstanceOfType(value)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a repository-relative file path to its language icon glyph (VS Code resource
/// label). Unknown extensions fall back to the plain-document glyph.</summary>
public sealed class PathToIconGlyphConverter : IValueConverter
{
    private static readonly CodeFileTypeRegistry Registry = CodeFileTypeRegistry.Instance;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string path ? Registry.FromPath(path).IconGlyph : Codicons.File;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a repository-relative file path to the themed file-type icon brush (Seti-style
/// per-language-family hue). Resolves the <see cref="CodeFileType.IconColorToken"/> from the active
/// theme dictionary so the icon stays legible on Dark / Light / High Contrast; when no Application
/// is available (e.g. unit tests) it falls back to a transparent brush so callers keep rendering.</summary>
public sealed class PathToIconBrushConverter : IValueConverter
{
    private static readonly CodeFileTypeRegistry Registry = CodeFileTypeRegistry.Instance;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var token = value is string path ? Registry.FromPath(path).IconColorToken : "FileTypeIconGrayBrush";

        if (Application.Current is { } app &&
            app.Resources[token] is SolidColorBrush themed)
        {
            return themed;
        }

        // No Application host (unit tests) or token missing: transparent keeps the glyph visible
        // only via any surrounding chrome, never throwing.
        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a repository-relative file path to its language monogram (VS Code 风格语言缩写,
/// 如 cs→C#、js→JS、json→{}). Unrecognized extensions fall back to the "?" monogram.</summary>
public sealed class PathToIconMonogramConverter : IValueConverter
{
    private static readonly CodeFileTypeRegistry Registry = CodeFileTypeRegistry.Instance;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string path ? Registry.FromPath(path).IconMonogram : "TXT";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Resolves a <see cref="CodeFileType"/> (e.g. a preview tab's FileType) to the themed
/// badge brush via its <see cref="CodeFileType.IconColorToken"/>; same Application-host fallback as
/// <see cref="PathToIconBrushConverter"/>.</summary>
public sealed class FileTypeToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var token = value is CodeFileType type ? type.IconColorToken : "FileTypeIconGrayBrush";

        if (Application.Current is { } app &&
            app.Resources[token] is SolidColorBrush themed)
        {
            return themed;
        }

        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps compact Git diff tab states to the existing SCM semantic colors.</summary>
public sealed class DiffTabStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var token = value is DiffTabStatus status
            ? status switch
            {
                DiffTabStatus.Staged or DiffTabStatus.Untracked => "SuccessBrush",
                DiffTabStatus.Commit => "InfoAccentBrush",
                _ => "ScmModifiedBrush",
            }
            : "MutedTextBrush";
        return Application.Current?.TryFindResource(token) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Sidebar column width: 300px when the page has a sidebar, 0 when it does not (设置).</summary>
public sealed class SidebarWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? new GridLength(0) : new GridLength(300);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a <see cref="LogPanelLevelFilter"/> to a Chinese label for the panel filter ComboBox (U7).</summary>
public sealed class LogPanelLevelFilterToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is LogPanelLevelFilter filter ? filter switch
        {
            LogPanelLevelFilter.All => Loc.GetString("Level_All"),
            LogPanelLevelFilter.Info => Loc.GetString("Level_Info"),
            LogPanelLevelFilter.Warning => Loc.GetString("Level_Warning"),
            LogPanelLevelFilter.Error => Loc.GetString("Level_Error"),
            _ => string.Empty,
        } : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps an <see cref="AppTheme"/> to a Chinese label for the theme picker (A2).</summary>
public sealed class AppThemeToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AppTheme theme ? theme switch
        {
            AppTheme.Dark => "深色（默认）",
            AppTheme.Light => "浅色",
            AppTheme.HighContrast => "高对比",
            _ => string.Empty,
        } : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a log level to the PROBLEMS severity foreground (ERROR/WARNING, info falls back
/// to the default text brush). Resolves from the active theme dictionary.</summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var brushKey = value?.ToString()?.Trim().ToUpperInvariant() switch
        {
            "ERROR" => "ErrorTextBrush",
            "WARNING" => "WarningBrush",
            _ => "TextBrush"
        };

        return Application.Current.Resources[brushKey] is Brush brush ? brush : Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a log level to a Codicon severity glyph for PROBLEMS rows.</summary>
public sealed class LogLevelToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString()?.Trim().ToUpperInvariant() switch
        {
            "ERROR" => Codicons.Error,
            "WARNING" => Codicons.Warning,
            _ => string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>主状态栏"当前文档状态"段的可见性:选中标签是文档类标签时显示。
/// 无参数 → <see cref="FilePreviewTab"/> 或 <see cref="DiffTab"/>;<see cref="IValueConverter"/> 参数
/// "file" → 仅 FilePreviewTab;"diff" → 仅 DiffTab。null(无文档标签,如页面标签/空选择)→ Collapsed。</summary>
public sealed class DocumentTabVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (parameter as string) switch
        {
            "file" => value is FilePreviewTab ? Visibility.Visible : Visibility.Collapsed,
            "diff" => value is DiffTab ? Visibility.Visible : Visibility.Collapsed,
            _ => value is FilePreviewTab or DiffTab ? Visibility.Visible : Visibility.Collapsed,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
