using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nornia.Core.Interfaces;
using Nornia.Core.Collections;
using Nornia.Core.Models;
using Nornia.Desktop.Services;
using Nornia.Package.Services;
using Nornia.Runtime.Services;
using CoreRuntime = Nornia.Core.Models.Runtime;
using System.ComponentModel;
using System.Windows.Data;

namespace Nornia.Desktop.ViewModels;

public partial class ToolsViewModel(
    IRuntimeInventoryService inventoryService,
    IPackageProvider packageProvider,
    IPackageInventoryService packageInventoryService,
    IRuntimePackageResolver packageResolver,
    IConfirmationService confirmationService,
    IUiLogService logService,
    IClipboardService? clipboard = null,
    IUiPerformanceMetrics? performanceMetrics = null) : PageViewModel("开发工具", logService), INavigationTarget
{
    private readonly IClipboardService _clipboard = clipboard ?? NullClipboardService.Instance;

    public BulkObservableCollection<ManagedComponentItem> Tools { get; } = [];
    public ObservableCollection<ManagedComponentItem> SelectedTools { get; } = [];
    public ICollectionView FilteredTools => CollectionViewSource.GetDefaultView(Tools);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpgradeCommand))]
    private ManagedComponentItem? selectedTool;

    [ObservableProperty] private string filterText = string.Empty;
    public string SelectedToolSummary => SelectedTool is null ? "选择开发工具查看版本、路径和可用操作。" : $"{SelectedTool.Name} {SelectedTool.Version} · {SelectedTool.Architecture} · {SelectedTool.InstallPath}";

    protected override Task OnFirstActivatedAsync()
    {
        SelectedTools.CollectionChanged += (_, _) =>
        {
            RemoveCommand.NotifyCanExecuteChanged();
            UpgradeCommand.NotifyCanExecuteChanged();
            NotifyCopyCommands();
        };
        FilteredTools.Filter = MatchesFilter;
        return ScanAsync();
    }

    // ===== Copy commands (grid 复制选中 / 复制全部) =====

    private void NotifyCopyCommands()
    {
        CopySelectedToolsCommand.NotifyCanExecuteChanged();
        CopyAllToolsCommand.NotifyCanExecuteChanged();
    }

    private static string FormatTool(ManagedComponentItem tool) =>
        $"{tool.Name}\t{tool.Version}\t{tool.AvailableVersion}\t{tool.Architecture}\t{tool.InstallPath}";

    [RelayCommand(CanExecute = nameof(CanCopySelectedTools))]
    private void CopySelectedTools() =>
        _clipboard.SetText(string.Join(Environment.NewLine, SelectedTools.Select(FormatTool)));

    private bool CanCopySelectedTools() => SelectedTools.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCopyAllTools))]
    private void CopyAllTools() =>
        _clipboard.SetText(string.Join(Environment.NewLine, Tools.Select(FormatTool)));

    private bool CanCopyAllTools() => Tools.Count > 0;

    [RelayCommand]
    private Task ScanAsync() => RunAsync("扫描开发工具", async cancellationToken =>
    {
        SetPageLoading();
        try
        {
            await ReloadToolsAsync(cancellationToken);
            if (Tools.Count == 0)
            {
                SetPageEmpty("没有已扫描到的开发工具。点击「重新扫描」开始。");
            }
            else
            {
                SetPageReady();
            }
        }
        catch (Exception ex)
        {
            SetPageError($"扫描失败：{ex.Message}");
            throw;
        }
    }, "确认相关命令已加入 PATH，或查看 Problems。", canCancel: true);

    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private Task RemoveAsync() => RunAsync("移除开发工具", async cancellationToken =>
    {
        var tools = SelectedTools.Count > 0 ? SelectedTools.ToArray() : SelectedTool is not null ? [SelectedTool] : [];
        if (tools.Length == 0)
        {
            LogService.Write("INFO", "请先选择一个或多个开发工具。");
            return;
        }

        var selectedTools = tools.Where(t => EnvironmentComponentCatalog.Get(t.Name)?.CanManageWithWinget == true).ToArray();
        if (selectedTools.Length == 0)
        {
            LogService.Write("WARNING", "所选开发工具中没有可以通过 Winget 管理的项目。");
            return;
        }

        var summary = string.Join(Environment.NewLine, selectedTools.Take(12).Select(t => $"• {t.Name} {t.Version}"));
        if (selectedTools.Length > 12) summary += $"{Environment.NewLine}…以及 {selectedTools.Length - 12} 项";
        if (!confirmationService.Confirm(
                "确认移除开发工具",
                $"将卸载以下开发工具：{Environment.NewLine}{Environment.NewLine}{summary}{Environment.NewLine}{Environment.NewLine}此操作可能影响依赖项目，且不保证可自动回滚。选择&quot;否&quot;可安全取消。"))
        {
            LogService.Write("INFO", "用户取消了开发工具移除。");
            return;
        }

        foreach (var tool in selectedTools)
        {
            foreach (var package in packageResolver.ResolveMany(tool.Name, tool.Version))
            {
                await packageProvider.UninstallAsync(package.PackageId, null, OperationProgress, cancellationToken);
            }
        }
        await ReloadToolsAsync(cancellationToken);
    }, "确认工具未被占用，再查看 Problems。", canCancel: true);

    [RelayCommand(CanExecute = nameof(CanUpgradeSelected))]
    private Task UpgradeAsync() => RunAsync("升级开发工具", async cancellationToken =>
    {
        var tools = SelectedTools.Count > 0 ? SelectedTools.ToArray() : SelectedTool is not null ? [SelectedTool] : [];
        if (tools.Length == 0)
        {
            LogService.Write("INFO", "请先选择一个或多个开发工具。");
            return;
        }

        var selectedTools = tools.Where(t => t.HasUpdate && EnvironmentComponentCatalog.Get(t.Name)?.CanManageWithWinget == true).ToArray();
        if (selectedTools.Length == 0)
        {
            LogService.Write("WARNING", "所选开发工具中没有可用更新或无法通过 Winget 管理。");
            return;
        }

        foreach (var tool in selectedTools)
        {
            foreach (var package in packageResolver.ResolveMany(tool.Name, tool.Version))
            {
                await packageProvider.UpgradeAsync(package.PackageId, OperationProgress, cancellationToken);
            }
        }
        await ReloadToolsAsync(cancellationToken);
    }, "查看 Output 中的安装器诊断后重试。", canCancel: true);

    private async Task ReloadToolsAsync(CancellationToken cancellationToken = default)
    {
        var tools = await inventoryService.RefreshForcedAsync(cancellationToken);
        var packages = await packageInventoryService.RefreshAsync(OperationProgress, cancellationToken);
        var snapshot = tools
                     .Where(tool => EnvironmentComponentCatalog.Get(tool.Name)?.Category == EnvironmentComponentCategory.DevelopmentTool)
                     .OrderBy(tool => tool.Name).ThenByDescending(tool => tool.Version)
            .Select(tool => new ManagedComponentItem(tool, FindAvailableVersion(tool, packages)))
            .ToArray();
        using var performance = performanceMetrics?.Begin("tools.list.publish", snapshot.Length, "ui-batch");
        Tools.ReplaceRange(snapshot);
        NotifyCopyCommands();
    }
    partial void OnFilterTextChanged(string value) => FilteredTools.Refresh();
    partial void OnSelectedToolChanged(ManagedComponentItem? value)
    {
        OnPropertyChanged(nameof(SelectedToolSummary));
        OnPropertyChanged(nameof(RemoveButtonTooltip));
        OnPropertyChanged(nameof(UpgradeButtonTooltip));
        RemoveCommand.NotifyCanExecuteChanged();
        UpgradeCommand.NotifyCanExecuteChanged();
    }

    public string RemoveButtonTooltip
    {
        get
        {
            if (SelectedTool is null && SelectedTools.Count == 0) return "请先选择一个开发工具";
            if (SelectedTool is not null && EnvironmentComponentCatalog.Get(SelectedTool.Name)?.CanManageWithWinget != true)
                return $"当前开发工具 ({SelectedTool.Name}) 不支持 Winget 管理，无法移除";
            return "选择可由 Winget 管理的开发工具后可移除；执行前会确认";
        }
    }

    public string UpgradeButtonTooltip
    {
        get
        {
            if (SelectedTool is null && SelectedTools.Count == 0) return "请先选择一个开发工具";
            if (SelectedTool?.HasUpdate != true && !SelectedTools.Any(t => t.HasUpdate)) return "所选开发工具没有可用更新";
            if (SelectedTool is not null && EnvironmentComponentCatalog.Get(SelectedTool.Name)?.CanManageWithWinget != true)
                return $"当前开发工具 ({SelectedTool.Name}) 不支持 Winget 管理，无法升级";
            return "选择存在可用更新的开发工具后可升级";
        }
    }

    public void ApplyNavigationContext(NavigationContext? context)
    {
        if (context is NavigationContext.Scan) _ = ScanAsync();
    }
    private bool MatchesFilter(object item) => item is ManagedComponentItem tool
        && (string.IsNullOrWhiteSpace(FilterText) || tool.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase) || tool.Version.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

    private bool CanRemoveSelected() =>
        (SelectedTool is not null || SelectedTools.Count > 0)
        && (SelectedTool is not null ? EnvironmentComponentCatalog.Get(SelectedTool.Name)?.CanManageWithWinget == true : SelectedTools.Any(t => EnvironmentComponentCatalog.Get(t.Name)?.CanManageWithWinget == true));

    private bool CanUpgradeSelected() =>
        (SelectedTool?.HasUpdate == true || SelectedTools.Any(t => t.HasUpdate))
        && (SelectedTool is not null ? EnvironmentComponentCatalog.Get(SelectedTool.Name)?.CanManageWithWinget == true : SelectedTools.Any(t => t.HasUpdate && EnvironmentComponentCatalog.Get(t.Name)?.CanManageWithWinget == true));

    private string? FindAvailableVersion(CoreRuntime component, IReadOnlyList<PackageInfo> packages)
    {
        try
        {
            var ids = packageResolver.ResolveMany(component.Name, component.Version).Select(package => package.PackageId);
            return packages.FirstOrDefault(package => ids.Contains(package.Id, StringComparer.OrdinalIgnoreCase))?.AvailableVersion;
        }
        catch (ArgumentException) { return null; }
    }
}
