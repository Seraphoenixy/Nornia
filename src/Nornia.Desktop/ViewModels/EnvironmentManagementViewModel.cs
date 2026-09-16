using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nornia.Desktop.Services;
using System.Collections.ObjectModel;

namespace Nornia.Desktop.ViewModels;

/// <summary>Top-level environment workspace. Its children stay lazy: selecting an item is the
/// only action that activates the corresponding existing page.</summary>
public partial class EnvironmentManagementViewModel : PageViewModel
{
    private readonly IClipboardService _clipboard;

    public EnvironmentManagementViewModel(
        DashboardViewModel dashboard, RuntimeViewModel runtime, ToolsViewModel tools,
        PackagesViewModel packages, CacheViewModel cache,
        IUiLogService logService,
        IClipboardService? clipboard = null) : base("环境管理", logService)
    {
        _clipboard = clipboard ?? NullClipboardService.Instance;
        SelectedSections.CollectionChanged += (_, _) => CopySelectedSectionsCommand.NotifyCanExecuteChanged();
        Items =
        [
            new WorkbenchSection(EnvironmentSection.Overview, "概览", Codicons.Dashboard, dashboard),
            new WorkbenchSection(EnvironmentSection.Runtime, "运行库", Codicons.ServerProcess, runtime),
            new WorkbenchSection(EnvironmentSection.Tools, "开发工具", Codicons.Tools, tools),
            new WorkbenchSection(EnvironmentSection.Packages, "软件包", Codicons.Package, packages),
            new WorkbenchSection(EnvironmentSection.Cache, "缓存管理", Codicons.Archive, cache)
        ];
        SelectedItem = Items[0];
    }

    public ObservableCollection<WorkbenchSection> Items { get; }
    public ObservableCollection<WorkbenchSection> SelectedSections { get; } = [];
    [ObservableProperty] private WorkbenchSection? selectedItem;
    public PageViewModel? CurrentPage => SelectedItem?.Page;

    /// <summary>Selects the section with the given stable kind (navigation / command palette):
    /// renders it in the environment page content area — it never opens a workbench tab.</summary>
    public void SelectSection(EnvironmentSection section)
    {
        var item = Items.FirstOrDefault(candidate => candidate.Kind == section);
        if (item is not null)
        {
            SelectedItem = item;
        }
    }

    /// <summary>Selects the section by its localized title (command palette rows).</summary>
    public void SelectSection(string title)
    {
        if (EnvironmentSectionInfo.SectionForTitle(title) is { } section)
        {
            SelectSection(section);
        }
    }

    /// <summary>The section page for a stable kind (used to apply navigation context).</summary>
    public PageViewModel? SectionPage(EnvironmentSection section) =>
        Items.FirstOrDefault(candidate => candidate.Kind == section)?.Page;

    /// <summary>The secondary left sidebar shows the section list (概览/运行库/开发工具/软件包/缓存管理).</summary>
    public override object? Sidebar => this;

    [RelayCommand(CanExecute = nameof(CanCopySelectedSections))]
    private void CopySelectedSections() =>
        _clipboard.SetText(string.Join(Environment.NewLine, SelectedSections.Select(section => section.Title)));

    private bool CanCopySelectedSections() => SelectedSections.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCopyAllSections))]
    private void CopyAllSections() =>
        _clipboard.SetText(string.Join(Environment.NewLine, Items.Select(section => section.Title)));

    private bool CanCopyAllSections() => Items.Count > 0;

    partial void OnSelectedItemChanged(WorkbenchSection? value)
    {
        OnPropertyChanged(nameof(CurrentPage));
        if (value is not null)
        {
            _ = ActivateSectionSafelyAsync(value.Page);
        }
    }

    private async Task ActivateSectionSafelyAsync(PageViewModel page)
    {
        try
        {
            await page.ActivateAsync();
        }
        catch (Exception ex)
        {
            LogService.WriteException("ERROR", $"环境页面“{page.Title}”初始化失败", ex);
        }
    }
}

/// <summary>One environment section: stable <see cref="Kind"/>, localized title, glyph and page.
/// Selection renders the page inside the environment view; it never opens a workbench tab.</summary>
public sealed record WorkbenchSection(EnvironmentSection Kind, string Title, string Glyph, PageViewModel Page);
