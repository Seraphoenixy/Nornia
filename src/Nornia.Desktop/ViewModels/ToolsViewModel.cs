using System.Collections.ObjectModel;
using System.Windows;
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
    IToolExtensionInventoryService extensionService,
    IClipboardService? clipboard = null,
    IUiPerformanceMetrics? performanceMetrics = null,
    IInventoryScanStateRepository? scanStateRepository = null) : PageViewModel("开发工具", logService), INavigationTarget
{
    private readonly IClipboardService _clipboard = clipboard ?? NullClipboardService.Instance;

    public BulkObservableCollection<ManagedComponentItem> Tools { get; } = [];
    public ObservableCollection<ManagedComponentItem> SelectedTools { get; } = [];
    public ICollectionView FilteredTools => CollectionViewSource.GetDefaultView(Tools);

    /// <summary>页头新鲜度提示:快照何时扫描,由 scan_state 提供(空表示不可用/未注册仓库)。</summary>
    [ObservableProperty] private string lastScanDisplay = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpgradeCommand))]
    private ManagedComponentItem? selectedTool;

    [ObservableProperty] private string filterText = string.Empty;
    public string SelectedToolSummary => SelectedTool is null ? "选择开发工具查看版本、路径和可用操作。" : $"{SelectedTool.Name} {SelectedTool.Version} · {SelectedTool.Architecture} · {SelectedTool.InstallPath}";

    // ===== 扩展依赖子面板 =====

    public BulkObservableCollection<ToolExtension> Extensions { get; } = [];
    public ObservableCollection<ToolExtension> SelectedExtensions { get; } = [];
    public ICollectionView FilteredExtensions => CollectionViewSource.GetDefaultView(Extensions);

    /// <summary>当前面板绑定的生态;选中工具无生态时为 null。</summary>
    private ToolExtensionEcosystem? _activeEcosystem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshExtensionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallExtensionCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallExtensionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpgradeExtensionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpgradeAllExtensionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyAllExtensionsCommand))]
    private bool extensionPanelVisible;

    public Visibility ExtensionPanelVisibility => ExtensionPanelVisible ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty] private string extensionPanelTitle = string.Empty;

    [ObservableProperty] private string extensionFilterText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallExtensionCommand))]
    private string extensionSearchName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallExtensionCommand))]
    private string extensionSearchVersion = string.Empty;

    [ObservableProperty] private string extensionErrorText = string.Empty;
    public Visibility ExtensionErrorVisibility => string.IsNullOrEmpty(ExtensionErrorText) ? Visibility.Collapsed : Visibility.Visible;

    public bool HasExtensionUpdates => Extensions.Any(extension => extension.HasUpdate);

    // ===== 扩展依赖关系树 =====

    /// <summary>扩展列表的单选(驱动依赖树联动);与多选 SelectedExtensions 并存。</summary>
    [ObservableProperty] private ToolExtension? selectedExtension;

    /// <summary>依赖树根节点(选中包的直接依赖,展开时逐节点懒加载)。</summary>
    public BulkObservableCollection<ToolExtensionDependencyNode> DependencyRoots { get; } = [];

    [ObservableProperty] private string dependencyPanelTitle = "依赖关系";

    [ObservableProperty] private string dependencyErrorText = string.Empty;
    public Visibility DependencyErrorVisibility => string.IsNullOrEmpty(DependencyErrorText) ? Visibility.Collapsed : Visibility.Visible;

    protected override async Task OnFirstActivatedAsync()
    {
        SelectedTools.CollectionChanged += (_, _) =>
        {
            RemoveCommand.NotifyCanExecuteChanged();
            UpgradeCommand.NotifyCanExecuteChanged();
            NotifyCopyCommands();
        };
        SelectedExtensions.CollectionChanged += (_, _) =>
        {
            UninstallExtensionsCommand.NotifyCanExecuteChanged();
            UpgradeExtensionsCommand.NotifyCanExecuteChanged();
            CopySelectedExtensionsCommand.NotifyCanExecuteChanged();
        };
        FilteredTools.Filter = MatchesFilter;
        FilteredExtensions.Filter = MatchesExtensionFilter;
        // 快照优先:先用持久化清单立即渲染首屏,再走 TTL 门控刷新;快照足够新且环境指纹
        // 未变时,门控刷新直接返回库内数据,不派生任何进程。
        await SeedPersistedAsync();
        await LoadFromInventoryAsync();
    }

    /// <summary>从持久化快照立即填充列表,失败静默(门控刷新会落回全扫兜底)。</summary>
    private async Task SeedPersistedAsync()
    {
        try
        {
            var tools = await inventoryService.GetPersistedAsync();
            var packages = await packageInventoryService.GetPersistedAsync();
            PublishTools(tools, packages);
            LastScanDisplay = await InventoryScanAgeText.LoadAsync(
                scanStateRepository, InventoryScanAgeText.RuntimeScanKind);
        }
        catch
        {
            // 持久化暂不可用(如表未建好):交给下面的门控刷新处理。
        }
    }

    // ===== Copy commands (grid 复制选中 / 复制全部) =====

    private void NotifyCopyCommands()
    {
        CopySelectedToolsCommand.NotifyCanExecuteChanged();
        CopyAllToolsCommand.NotifyCanExecuteChanged();
        CopySelectedExtensionsCommand.NotifyCanExecuteChanged();
        CopyAllExtensionsCommand.NotifyCanExecuteChanged();
    }

    private static string FormatTool(ManagedComponentItem tool) =>
        $"{tool.Name}\t{tool.Version}\t{tool.AvailableVersion}\t{tool.Architecture}\t{tool.InstallPath}";

    private static string FormatExtension(ToolExtension extension) =>
        $"{extension.Name}\t{extension.Version}\t{extension.AvailableVersion}";

    [RelayCommand(CanExecute = nameof(CanCopySelectedTools))]
    private void CopySelectedTools() =>
        _clipboard.SetText(string.Join(Environment.NewLine, SelectedTools.Select(FormatTool)));

    private bool CanCopySelectedTools() => SelectedTools.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCopyAllTools))]
    private void CopyAllTools() =>
        _clipboard.SetText(string.Join(Environment.NewLine, Tools.Select(FormatTool)));

    private bool CanCopyAllTools() => Tools.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCopySelectedExtensions))]
    private void CopySelectedExtensions() =>
        _clipboard.SetText(string.Join(Environment.NewLine, SelectedExtensions.Select(FormatExtension)));

    private bool CanCopySelectedExtensions() => SelectedExtensions.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCopyAllExtensions))]
    private void CopyAllExtensions() =>
        _clipboard.SetText(string.Join(Environment.NewLine, Extensions.Select(FormatExtension)));

    private bool CanCopyAllExtensions() => Extensions.Count > 0;

    /// <summary>首激活的门控加载:快照新鲜时直接返回库内数据(零进程派生),否则全量扫描。</summary>
    private Task LoadFromInventoryAsync() => RunAsync("读取开发工具", async cancellationToken =>
    {
        SetPageLoading();
        try
        {
            await ReloadToolsAsync(forceRescan: false, cancellationToken);
            PublishListState();
        }
        catch (Exception ex)
        {
            SetPageError($"读取失败：{ex.Message}");
            throw;
        }
    }, "确认相关命令已加入 PATH，或查看 Problems。", canCancel: true);

    /// <summary>用户显式发起的扫描:永远强制重扫并更新持久化快照与 scan_state。</summary>
    [RelayCommand]
    private Task ScanAsync() => RunAsync("扫描开发工具", async cancellationToken =>
    {
        SetPageLoading();
        try
        {
            await ReloadToolsAsync(forceRescan: true, cancellationToken);
            PublishListState();
        }
        catch (Exception ex)
        {
            SetPageError($"扫描失败：{ex.Message}");
            throw;
        }
    }, "确认相关命令已加入 PATH，或查看 Problems。", canCancel: true);

    private void PublishListState()
    {
        if (Tools.Count == 0)
        {
            SetPageEmpty("没有已扫描到的开发工具。点击「重新扫描」开始。");
        }
        else
        {
            SetPageReady();
        }
    }

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
        await ReloadToolsAsync(forceRescan: true, cancellationToken);
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
        await ReloadToolsAsync(forceRescan: true, cancellationToken);
    }, "查看 Output 中的安装器诊断后重试。", canCancel: true);

    /// <summary>重载开发工具列表。forceRescan=true 用于显式扫描与移除/升级等变更后的重载
    /// (两个清单都绕过合并缓存与持久化快照 TTL);false 用于页面首激活的门控加载。</summary>
    private async Task ReloadToolsAsync(bool forceRescan, CancellationToken cancellationToken = default)
    {
        var tools = forceRescan
            ? await inventoryService.RefreshForcedAsync(cancellationToken)
            : await inventoryService.RefreshAsync(cancellationToken);
        var packages = forceRescan
            ? await packageInventoryService.RefreshForcedAsync(OperationProgress, cancellationToken)
            : await packageInventoryService.RefreshAsync(OperationProgress, cancellationToken);
        PublishTools(tools, packages);
        LastScanDisplay = await InventoryScanAgeText.LoadAsync(
            scanStateRepository, InventoryScanAgeText.RuntimeScanKind, cancellationToken);
    }

    private void PublishTools(IReadOnlyList<CoreRuntime> tools, IReadOnlyList<PackageInfo> packages)
    {
        var snapshot = tools
                     .Where(tool => EnvironmentComponentCatalog.Get(tool.Name)?.Category == EnvironmentComponentCategory.DevelopmentTool)
                     .OrderBy(tool => tool.Name).ThenByDescending(tool => tool.Version)
            .Select(tool => new ManagedComponentItem(tool, FindAvailableVersion(tool, packages)))
            .ToArray();
        using var performance = performanceMetrics?.Begin("tools.list.publish", snapshot.Length, "ui-batch");
        var selectedItems = SelectedTools.ToArray();
        var selectedItem = SelectedTool;
        Tools.ReplaceRange(snapshot);
        ReselectPublishedItems(snapshot, selectedItems, selectedItem);
        FilteredTools.Refresh();
        NotifyCopyCommands();
    }

    private void ReselectPublishedItems(
        IReadOnlyList<ManagedComponentItem> snapshot,
        IReadOnlyList<ManagedComponentItem> selectedItems,
        ManagedComponentItem? selectedItem)
    {
        var selected = selectedItems
            .Select(item => FindReplacement(snapshot, item))
            .Where(item => item is not null)
            .Cast<ManagedComponentItem>()
            .Distinct()
            .ToArray();
        SelectedTools.Clear();
        foreach (var item in selected)
        {
            SelectedTools.Add(item);
        }

        // Keep the detail form attached to the freshly scanned row when its stable location is
        // unchanged. If the row disappeared, clear it instead of displaying stale data.
        SelectedTool = selectedItem is null
            ? null
            : FindReplacement(snapshot, selectedItem);
    }

    private static string GetSelectionKey(ManagedComponentItem item) =>
        $"{item.Name}\u001F{item.Architecture}\u001F{item.InstallPath}";

    private static ManagedComponentItem? FindReplacement(
        IReadOnlyList<ManagedComponentItem> snapshot,
        ManagedComponentItem previous)
    {
        var exact = snapshot.FirstOrDefault(item => string.Equals(GetSelectionKey(item), GetSelectionKey(previous), StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        if (!string.IsNullOrWhiteSpace(previous.AvailableVersion))
        {
            var target = snapshot.FirstOrDefault(item =>
                string.Equals(item.Name, previous.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Architecture, previous.Architecture, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Version, previous.AvailableVersion, StringComparison.OrdinalIgnoreCase));
            if (target is not null) return target;
        }

        var candidates = snapshot.Where(item =>
                string.Equals(item.Name, previous.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Architecture, previous.Architecture, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }
    partial void OnFilterTextChanged(string value) => FilteredTools.Refresh();
    partial void OnSelectedToolChanged(ManagedComponentItem? value)
    {
        OnPropertyChanged(nameof(SelectedToolSummary));
        OnPropertyChanged(nameof(RemoveButtonTooltip));
        OnPropertyChanged(nameof(UpgradeButtonTooltip));
        RemoveCommand.NotifyCanExecuteChanged();
        UpgradeCommand.NotifyCanExecuteChanged();
        _ = LoadExtensionsForSelectionAsync(value);
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

    // ===== 扩展依赖子面板 =====

    /// <summary>选中工具变化时联动加载其扩展依赖:无生态(Java/Git 等)则隐藏面板。
    /// 列表刷新会经 ReselectPublishedItems 再次触发本方法,因此加载走 30 秒冷却缓存
    /// (避免派生进程),并以面板生态守卫防止乱序结果覆盖。</summary>
    private async Task LoadExtensionsForSelectionAsync(ManagedComponentItem? tool)
    {
        var componentId = tool is null ? null : EnvironmentComponentCatalog.Get(tool.Name)?.Id;
        var ecosystem = componentId is null ? null : extensionService.GetEcosystemForComponent(componentId);
        if (ecosystem is null)
        {
            _activeEcosystem = null;
            ExtensionPanelVisible = false;
            ExtensionPanelTitle = string.Empty;
            ExtensionErrorText = string.Empty;
            Extensions.ReplaceRange([]);
            SelectedExtensions.Clear();
            ClearDependencyTree();
            return;
        }

        var panelEcosystem = ecosystem.Value;
        var provider = extensionService.GetProvider(panelEcosystem);
        // 立即占用面板生态:后续扫描完成时若用户已切换工具,则丢弃该次结果,
        // 防止慢扫描覆盖新面板的数据。
        _activeEcosystem = panelEcosystem;
        ExtensionPanelTitle = $"{tool!.Name} 的扩展依赖（{provider.EcosystemName}）";
        ExtensionPanelVisible = true;
        try
        {
            var list = await extensionService.RefreshAsync(panelEcosystem, force: false, cancellationToken: CancellationToken.None);
            if (_activeEcosystem == panelEcosystem)
            {
                PublishExtensions(list);
            }
        }
        catch (Exception ex)
        {
            if (_activeEcosystem == panelEcosystem)
            {
                LogService.WriteException("ERROR", $"读取 {provider.EcosystemName} 扩展依赖失败", ex);
                SetExtensionPanelError($"读取失败：{ex.Message}");
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefreshExtensions))]
    private Task RefreshExtensionsAsync() => RunAsync("重新加载扩展依赖", async cancellationToken =>
    {
        if (_activeEcosystem is null) return;
        var list = await extensionService.RefreshAsync(_activeEcosystem.Value, force: true, OperationProgress, cancellationToken);
        PublishExtensions(list);
    }, "确认对应 CLI 已加入 PATH，或查看 Problems。", canCancel: true);

    private bool CanRefreshExtensions() => ExtensionPanelVisible && _activeEcosystem is not null;

    [RelayCommand(CanExecute = nameof(CanInstallExtension))]
    private Task InstallExtensionAsync() => RunAsync("安装扩展依赖", async cancellationToken =>
    {
        if (_activeEcosystem is null) return;
        var name = ExtensionSearchName.Trim();
        var version = string.IsNullOrWhiteSpace(ExtensionSearchVersion) ? null : ExtensionSearchVersion.Trim();
        await extensionService.GetProvider(_activeEcosystem.Value).InstallAsync(name, version, OperationProgress, cancellationToken);
        ExtensionSearchName = string.Empty;
        ExtensionSearchVersion = string.Empty;
        await ReloadExtensionsForcedAsync(cancellationToken);
    }, "查看 Output 中的安装器诊断后重试。", canCancel: true);

    private bool CanInstallExtension() =>
        ExtensionPanelVisible && _activeEcosystem is not null && !string.IsNullOrWhiteSpace(ExtensionSearchName);

    [RelayCommand(CanExecute = nameof(CanUninstallExtensions))]
    private Task UninstallExtensionsAsync() => RunAsync("卸载扩展依赖", async cancellationToken =>
    {
        if (_activeEcosystem is null) return;
        var selected = SelectedExtensions.ToArray();
        if (selected.Length == 0)
        {
            LogService.Write("INFO", "请先选择一个或多个扩展依赖。");
            return;
        }

        var summary = string.Join(Environment.NewLine, selected.Take(12).Select(e => $"• {e.Name} {e.Version}"));
        if (selected.Length > 12) summary += $"{Environment.NewLine}…以及 {selected.Length - 12} 项";
        if (!confirmationService.Confirm("确认卸载扩展依赖",
                $"将卸载以下扩展依赖：{Environment.NewLine}{Environment.NewLine}{summary}{Environment.NewLine}{Environment.NewLine}此操作可能影响使用它们的项目。选择&quot;否&quot;可安全取消。"))
        {
            LogService.Write("INFO", "用户取消了扩展依赖卸载。");
            return;
        }

        var provider = extensionService.GetProvider(_activeEcosystem.Value);
        foreach (var extension in selected)
        {
            await provider.UninstallAsync(extension.Name, OperationProgress, cancellationToken);
        }
        await ReloadExtensionsForcedAsync(cancellationToken);
    }, "确认依赖未被占用，再查看 Problems。", canCancel: true);

    private bool CanUninstallExtensions() => ExtensionPanelVisible && _activeEcosystem is not null && SelectedExtensions.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUpgradeExtensions))]
    private Task UpgradeExtensionsAsync() => RunAsync("升级扩展依赖", async cancellationToken =>
    {
        if (_activeEcosystem is null) return;
        var selected = SelectedExtensions.Where(e => e.HasUpdate).ToArray();
        if (selected.Length == 0)
        {
            LogService.Write("INFO", "所选扩展依赖中没有存在可用更新的项目。");
            return;
        }

        var provider = extensionService.GetProvider(_activeEcosystem.Value);
        foreach (var extension in selected)
        {
            await provider.UpgradeAsync(extension.Name, OperationProgress, cancellationToken);
        }
        await ReloadExtensionsForcedAsync(cancellationToken);
    }, "查看 Output 中的安装器诊断后重试。", canCancel: true);

    private bool CanUpgradeExtensions() =>
        ExtensionPanelVisible && _activeEcosystem is not null && SelectedExtensions.Any(e => e.HasUpdate);

    [RelayCommand(CanExecute = nameof(CanUpgradeAllExtensions))]
    private Task UpgradeAllExtensionsAsync() => RunAsync("升级全部扩展依赖", async cancellationToken =>
    {
        if (_activeEcosystem is null) return;
        var updatable = Extensions.Where(e => e.HasUpdate).ToArray();
        if (updatable.Length == 0)
        {
            LogService.Write("INFO", "没有存在可用更新的扩展依赖。");
            return;
        }

        var provider = extensionService.GetProvider(_activeEcosystem.Value);
        foreach (var extension in updatable)
        {
            await provider.UpgradeAsync(extension.Name, OperationProgress, cancellationToken);
        }
        await ReloadExtensionsForcedAsync(cancellationToken);
    }, "查看 Output 中的安装器诊断后重试。", canCancel: true);

    private bool CanUpgradeAllExtensions() =>
        ExtensionPanelVisible && _activeEcosystem is not null && Extensions.Any(e => e.HasUpdate);

    private async Task ReloadExtensionsForcedAsync(CancellationToken cancellationToken)
    {
        if (_activeEcosystem is null) return;
        var list = await extensionService.RefreshAsync(_activeEcosystem.Value, force: true, OperationProgress, cancellationToken);
        PublishExtensions(list);
    }

    private void PublishExtensions(IReadOnlyList<ToolExtension> list)
    {
        Extensions.ReplaceRange(list);
        FilteredExtensions.Refresh();
        ExtensionErrorText = string.Empty;
        OnPropertyChanged(nameof(HasExtensionUpdates));
        NotifyCopyCommands();
        SyncDependencyTree();
    }

    private void SetExtensionPanelError(string message)
    {
        ExtensionErrorText = message;
        Extensions.ReplaceRange([]);
        SelectedExtensions.Clear();
        OnPropertyChanged(nameof(HasExtensionUpdates));
        NotifyCopyCommands();
    }

    partial void OnExtensionFilterTextChanged(string value) => FilteredExtensions.Refresh();
    partial void OnExtensionPanelVisibleChanged(bool value) => OnPropertyChanged(nameof(ExtensionPanelVisibility));
    partial void OnExtensionErrorTextChanged(string value) => OnPropertyChanged(nameof(ExtensionErrorVisibility));

    private bool MatchesExtensionFilter(object item) => item is ToolExtension extension
        && (string.IsNullOrWhiteSpace(ExtensionFilterText) || extension.Name.Contains(ExtensionFilterText, StringComparison.OrdinalIgnoreCase));

    // ===== 依赖树加载(懒加载,逐节点) =====

    partial void OnSelectedExtensionChanged(ToolExtension? value)
    {
        DependencyRoots.Clear();
        DependencyErrorText = string.Empty;
        if (value is null)
        {
            DependencyPanelTitle = "依赖关系";
            return;
        }

        DependencyPanelTitle = $"{value.Name} 的依赖";
        _ = LoadRootDependenciesAsync(value.Name);
    }

    /// <summary>加载选中包的直接依赖为树根。异步完成时校验生态守卫与选中未变,
    /// 防止快速切换工具/包时慢查询覆盖新面板数据。</summary>
    private async Task LoadRootDependenciesAsync(string name)
    {
        var ecosystem = _activeEcosystem;
        if (ecosystem is null)
        {
            return;
        }

        try
        {
            var dependencies = await extensionService.GetProvider(ecosystem.Value).GetDependenciesAsync(name);
            if (SelectedExtension?.Name == name && _activeEcosystem == ecosystem)
            {
                PublishDependencyRoots(dependencies);
            }
        }
        catch (Exception ex)
        {
            if (SelectedExtension?.Name == name && _activeEcosystem == ecosystem)
            {
                LogService.WriteException("ERROR", $"读取 {name} 依赖失败", ex);
                DependencyErrorText = $"读取失败：{ex.Message}";
            }
        }
    }

    /// <summary>展开一个依赖节点时懒加载其直接依赖(XAML 的 TreeViewItem.Expanded 转发调用)。</summary>
    public async Task LoadDependencyNodeAsync(ToolExtensionDependencyNode node)
    {
        if (node.IsLoading || node.IsLoaded || _activeEcosystem is not { } ecosystem)
        {
            return;
        }

        node.IsLoading = true;
        try
        {
            var dependencies = await extensionService.GetProvider(ecosystem).GetDependenciesAsync(node.Name);
            if (_activeEcosystem == ecosystem)
            {
                node.PublishChildren(dependencies);
            }
        }
        catch (Exception ex)
        {
            LogService.WriteException("ERROR", $"读取 {node.Name} 依赖失败", ex);
            DependencyErrorText = $"读取 {node.Name} 失败：{ex.Message}";
        }
        finally
        {
            node.IsLoading = false;
        }
    }

    private void PublishDependencyRoots(IReadOnlyList<ToolExtensionDependency> dependencies)
    {
        DependencyRoots.ReplaceRange(dependencies.Select(dependency => new ToolExtensionDependencyNode(dependency)));
        DependencyErrorText = string.Empty;
    }

    /// <summary>列表刷新后若选中的包已不存在(卸载/重命名),清空选中与依赖树。</summary>
    private void SyncDependencyTree()
    {
        if (SelectedExtension is not null && !Extensions.Contains(SelectedExtension))
        {
            SelectedExtension = null;
        }
    }

    /// <summary>清空依赖树(切换工具/生态无依赖/刷新时);置空单选会经
    /// OnSelectedExtensionChanged 复位标题。</summary>
    private void ClearDependencyTree()
    {
        DependencyRoots.Clear();
        DependencyErrorText = string.Empty;
        SelectedExtension = null;
    }

    partial void OnDependencyErrorTextChanged(string value) => OnPropertyChanged(nameof(DependencyErrorVisibility));
}
