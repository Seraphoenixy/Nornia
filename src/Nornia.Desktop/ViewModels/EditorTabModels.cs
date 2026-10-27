using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nornia.Core.Coalescing;
using Nornia.Core.Collections;
using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Desktop.Code;
using Nornia.Desktop.Configuration;
using Nornia.Desktop.Markdown;
using Nornia.Desktop.Services;
using Nornia.Project.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Nornia.Desktop.ViewModels;

/// <summary>编辑器折叠请求类型(视图把请求映射到 CodeDocumentView 的折叠操作)。</summary>
public enum EditorFoldRequest
{
    CollapseAll,
    ExpandAll,
    ToLevel,
}

/// <summary>One open tab in the shared editor group. Content is loaded lazily on first activation
/// and a file tab may evict its large payload while keeping its lightweight identity open.</summary>
public abstract partial class EditorTabItem : ObservableObject
{
    protected EditorTabItem(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(Name))
        {
            Name = path;
        }

        BreadcrumbText = TruncateBreadcrumb(path);
        BreadcrumbSegments = BuildBreadcrumbSegments(path);
    }

    public string Path { get; }
    public string Name { get; }

    /// <summary>Trailing path segments for the VS Code-style breadcrumb (ellipsized when deep).</summary>
    public string BreadcrumbText { get; }

    /// <summary>面包屑分段(驱动器/目录/文件;过深时以"…"折叠前段)。目录段点击可在资源管理器定位。</summary>
    public IReadOnlyList<BreadcrumbSegment> BreadcrumbSegments { get; }

    /// <summary>Identity used to deduplicate tabs (a file and a diff of that file are distinct).</summary>
    public abstract string TabKey { get; }

    public virtual string Glyph => Codicons.File;

    /// <summary>文件标签的语言元数据(语言缩写 Monogram / 色相);非文件标签为 null。</summary>
    public virtual CodeFileType? FileType => null;

    /// <summary>该标签是否为 diff 标签(驱动标签条上的暂存状态字母)。</summary>
    public virtual bool IsDiff => false;

    public virtual DiffTabStatus TabStatus => DiffTabStatus.Modified;
    public virtual string TabStatusMarker => string.Empty;
    public virtual string TabStatusToolTip => string.Empty;

    [ObservableProperty]
    private bool isActive;

    /// <summary>VS Code preview flag: file previews open as italic "preview" tabs and are replaced by
    /// the next file open until pinned (固定后即常驻)。</summary>
    [ObservableProperty]
    private bool isPreview;

    /// <summary>VS Code pinned flag: pinned tabs stay at the left end of the tab strip (固定标签;
    /// 会话级,不持久化,双击/右键菜单切换)。</summary>
    [ObservableProperty]
    private bool isPinned;

    /// <summary>运行时关闭标记:区分跨组移动期间的暂时离组与真正关闭,不参与布局持久化。</summary>
    [ObservableProperty]
    private bool isClosed;

    public abstract Task LoadAsync();

    /// <summary>Releases large payloads (text, diff rows, search matches) when the tab closes so the
    /// memory is reclaimed even if a stale reference lingers.</summary>
    public virtual void ReleaseResources()
    {
    }

    protected static string TruncateBreadcrumb(string path)
    {
        var segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 3)
        {
            return string.Join(" / ", segments);
        }

        return "… / " + string.Join(" / ", segments[^3..]);
    }

    /// <summary>把路径拆成面包屑分段:保留驱动器根,逐段累计完整路径(资源管理器定位用);
    /// 过深时以"…"折叠前段,只显示尾部 <paramref name="maxVisible"/> 段。</summary>
    private static IReadOnlyList<BreadcrumbSegment> BuildBreadcrumbSegments(string path, int maxVisible = 5)
    {
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return [new BreadcrumbSegment(path, null, false, false)];
        }

        var rows = new List<BreadcrumbSegment>();
        var full = string.Empty;
        for (var i = 0; i < parts.Length; i++)
        {
            full = i == 0 ? parts[i] : full + "\\" + parts[i];
            if (parts[i].EndsWith(':'))
            {
                full += "\\"; // 驱动器根 "D:\"
            }

            rows.Add(new BreadcrumbSegment(parts[i], full, i < parts.Length - 1, i != 0));
        }

        if (rows.Count <= maxVisible)
        {
            return rows;
        }

        var tail = rows[^maxVisible..];
        var result = new List<BreadcrumbSegment> { new("…", null, false, false) };
        result.Add(tail[0] with { ShowChevron = true });
        result.AddRange(tail[1..]);
        return result;
    }
}
