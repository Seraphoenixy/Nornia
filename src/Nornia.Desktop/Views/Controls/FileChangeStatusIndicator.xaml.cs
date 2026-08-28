using System.Windows;
using System.Windows.Controls;

namespace Nornia.Desktop.Views.Controls;

public enum SidebarFileStatusKind
{
    None,
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Conflict,
}

/// <summary>Canonical status presentation shared by Explorer and Source Control.</summary>
public sealed record SidebarFileStatusPresentation(
    SidebarFileStatusKind Kind,
    string Letter,
    string BrushKey,
    string ToolTip)
{
    public bool IsVisible => Kind != SidebarFileStatusKind.None;

    public static SidebarFileStatusPresentation FromLetter(string? value)
    {
        var letter = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return letter switch
        {
            "A" or "?" => new(SidebarFileStatusKind.Added, "A", "SuccessBrush", "新增或未跟踪文件"),
            "M" => new(SidebarFileStatusKind.Modified, "M", "ScmModifiedBrush", "已修改文件"),
            "D" => new(SidebarFileStatusKind.Deleted, "D", "ScmDeletedBrush", "已删除文件"),
            "R" => new(SidebarFileStatusKind.Renamed, "R", "InfoAccentBrush", "已重命名文件"),
            "C" => new(SidebarFileStatusKind.Copied, "C", "InfoAccentBrush", "已复制文件"),
            "T" => new(SidebarFileStatusKind.TypeChanged, "T", "ScmModifiedBrush", "文件类型已变化"),
            "U" => new(SidebarFileStatusKind.Conflict, "U", "ScmDeletedBrush", "存在合并冲突"),
            _ => new(SidebarFileStatusKind.None, string.Empty, string.Empty, string.Empty),
        };
    }
}

/// <summary>Renders a file status letter or a folder summary dot in one fixed slot.</summary>
public partial class FileChangeStatusIndicator : UserControl
{
    public static readonly DependencyProperty StatusLetterProperty = DependencyProperty.Register(
        nameof(StatusLetter), typeof(string), typeof(FileChangeStatusIndicator),
        new PropertyMetadata(string.Empty, OnStatusChanged));

    public static readonly DependencyProperty IsFolderSummaryProperty = DependencyProperty.Register(
        nameof(IsFolderSummary), typeof(bool), typeof(FileChangeStatusIndicator),
        new PropertyMetadata(false, OnStatusChanged));

    private static readonly DependencyPropertyKey StatusKindPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(StatusKind), typeof(SidebarFileStatusKind), typeof(FileChangeStatusIndicator), new PropertyMetadata(SidebarFileStatusKind.None));
    public static readonly DependencyProperty StatusKindProperty = StatusKindPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey DisplayLetterPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(DisplayLetter), typeof(string), typeof(FileChangeStatusIndicator), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty DisplayLetterProperty = DisplayLetterPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey StatusToolTipPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(StatusToolTip), typeof(string), typeof(FileChangeStatusIndicator), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty StatusToolTipProperty = StatusToolTipPropertyKey.DependencyProperty;

    public FileChangeStatusIndicator()
    {
        InitializeComponent();
        UpdateVisuals();
    }

    public string? StatusLetter { get => (string?)GetValue(StatusLetterProperty); set => SetValue(StatusLetterProperty, value); }
    public bool IsFolderSummary { get => (bool)GetValue(IsFolderSummaryProperty); set => SetValue(IsFolderSummaryProperty, value); }
    public SidebarFileStatusKind StatusKind { get => (SidebarFileStatusKind)GetValue(StatusKindProperty); private set => SetValue(StatusKindPropertyKey, value); }
    public string DisplayLetter { get => (string)GetValue(DisplayLetterProperty); private set => SetValue(DisplayLetterPropertyKey, value); }
    public string StatusToolTip { get => (string)GetValue(StatusToolTipProperty); private set => SetValue(StatusToolTipPropertyKey, value); }

    private static void OnStatusChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        ((FileChangeStatusIndicator)sender).UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        var presentation = SidebarFileStatusPresentation.FromLetter(StatusLetter);
        SetValue(StatusKindPropertyKey, presentation.Kind);
        SetValue(DisplayLetterPropertyKey, presentation.Letter);
        SetValue(StatusToolTipPropertyKey, presentation.ToolTip);

        if (Letter is null || Dot is null) return;
        Letter.Visibility = presentation.IsVisible && !IsFolderSummary ? Visibility.Visible : Visibility.Collapsed;
        Dot.Visibility = presentation.IsVisible && IsFolderSummary ? Visibility.Visible : Visibility.Collapsed;
    }
}
