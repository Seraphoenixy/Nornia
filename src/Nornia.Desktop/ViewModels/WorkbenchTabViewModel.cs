using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nornia.Desktop.Code;
using Nornia.Desktop.Services;

namespace Nornia.Desktop.ViewModels;

/// <summary>One tab in the unified VS Code-style workbench strip: a top-level page, an environment
/// section or a file/diff tab owned by the shared editor area.</summary>
public abstract partial class WorkbenchTabViewModel : ObservableObject
{
    protected WorkbenchTabViewModel(string tabKey, string title, string glyph, string? context, object content)
    {
        TabKey = tabKey;
        Title = title;
        Glyph = glyph;
        Context = context;
        Content = content;
    }

    /// <summary>Stable identity used to deduplicate tabs (e.g. "page:软件包", "section:运行库",
    /// "editor:file:D:\repo\src\A.cs").</summary>
    public string TabKey { get; }

    public string Title { get; }

    public string Glyph { get; }

    /// <summary>The activity-bar title this tab belongs to (page/section tabs) or null for file/diff tabs.</summary>
    public string? Context { get; }

    /// <summary>Full context tooltip for the tab (falls back to the visible title).</summary>
    public virtual string ToolTipText => Title;

    /// <summary>The view-model to render in the content area when this tab is active.</summary>
    public object Content { get; }

    [ObservableProperty]
    private bool isActive;

    /// <summary>Pinned tabs survive "关闭其他"/"关闭右侧"/"关闭未修改" (VS Code pin), are exempt
    /// from preview-slot replacement and the tab-limit eviction; pinning a preview promotes it to a
    /// regular tab (固定预览即常驻). Unpinning does not demote. Pin is session-only.</summary>
    [ObservableProperty]
    private bool isPinned;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler? CloseRequested;
}

/// <summary>Tab connected to a file/diff tab owned by the shared editor area.</summary>
public sealed class EditorWorkbenchTab : WorkbenchTabViewModel
{
    public EditorWorkbenchTab(EditorAreaViewModel editor, EditorTabItem editorTab)
        : base($"editor:{editorTab.TabKey}", editorTab.Name, editorTab.Glyph, null, editorTab)
    {
        EditorTab = editorTab;
        IsActive = ReferenceEquals(editor.SelectedTab, editorTab);
        editor.SelectedTabChanged += (_, _) => IsActive = ReferenceEquals(editor.SelectedTab, editorTab);
        // Mirror the editor tab's preview flag (斜体预览标签 → 固定后变常规).
        editorTab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorTabItem.IsPreview))
            {
                OnPropertyChanged(nameof(IsPreview));
            }
        };
    }

    /// <summary>The underlying shared-editor tab this workbench tab mirrors.</summary>
    public EditorTabItem EditorTab { get; }

    /// <summary>VS Code preview flag carried from the editor tab (斜体标题)。</summary>
    public bool IsPreview => EditorTab.IsPreview;

    /// <summary>Forwards file-language metadata to the workbench template. Diff tabs resolve the
    /// same type from their path so their tab badge uses the same language color as Explorer.</summary>
    public CodeFileType? FileType => EditorTab switch
    {
        FilePreviewTab preview => preview.FileType,
        DiffTab diff => CodeFileTypeRegistry.Instance.FromPath(diff.Path),
        _ => null,
    };

    public bool IsDiff => EditorTab is DiffTab;

    public string? DiffStatusMarker => (EditorTab as DiffTab)?.TabStatusMarker;

    public string? DiffStatusToolTip => (EditorTab as DiffTab)?.TabStatusToolTip;

    public DiffTabStatus? DiffStatus => (EditorTab as DiffTab)?.TabStatus;

    public override string ToolTipText => EditorTab.Path;
}

/// <summary>内容页(环境管理 / 项目管理 / 设置)标签:普通可关闭标签,导航可重开。
/// 实例由 Workbench 预建,首次导航才加入条带。</summary>
public sealed class PageWorkbenchTab : WorkbenchTabViewModel
{
    public PageWorkbenchTab(PageViewModel page, string glyph)
        : base($"page:{page.Title}", page.Title, glyph, page.Title, page)
    {
    }
}

/// <summary>Drag-reorder payload for the workbench tab strip (from/to strip indices).</summary>
public sealed record MoveTabArgs(int FromIndex, int ToIndex);

/// <summary>The five environment-management sections
/// (概览 / 运行库 / 开发工具 / 软件包 / 缓存管理).</summary>
public enum EnvironmentSection
{
    Overview,
    Runtime,
    Tools,
    Packages,
    Cache
}

/// <summary>Stable display metadata for <see cref="EnvironmentSection"/> values.</summary>
public static class EnvironmentSectionInfo
{
    public static (string Title, string Glyph) SectionInfo(EnvironmentSection section) => section switch
    {
        EnvironmentSection.Overview => ("概览", Codicons.Dashboard),
        EnvironmentSection.Runtime => ("运行库", Codicons.ServerProcess),
        EnvironmentSection.Tools => ("开发工具", Codicons.Tools),
        EnvironmentSection.Packages => ("软件包", Codicons.Package),
        EnvironmentSection.Cache => ("缓存管理", Codicons.Archive),
        _ => ("概览", Codicons.Dashboard),
    };

    /// <summary>Maps a navigation destination to the environment section it should activate, or null
    /// when the destination is not an environment section.</summary>
    public static EnvironmentSection? SectionForDestination(string destination) => destination switch
    {
        NavigationTargets.Dashboard => EnvironmentSection.Overview,
        NavigationTargets.Runtime => EnvironmentSection.Runtime,
        NavigationTargets.Tools => EnvironmentTargetTools(),
        NavigationTargets.Packages => EnvironmentSection.Packages,
        NavigationTargets.Cache => EnvironmentSection.Cache,
        _ => null,
    };

    private static EnvironmentSection? EnvironmentTargetTools() => EnvironmentSection.Tools;

    /// <summary>Maps an environment section title (概览/运行库/…) to its enum, or null when unknown.</summary>
    public static EnvironmentSection? SectionForTitle(string title) => title switch
    {
        "概览" => EnvironmentSection.Overview,
        "运行库" => EnvironmentSection.Runtime,
        "开发工具" => EnvironmentSection.Tools,
        "软件包" => EnvironmentSection.Packages,
        "缓存管理" => EnvironmentSection.Cache,
        _ => null,
    };
}
