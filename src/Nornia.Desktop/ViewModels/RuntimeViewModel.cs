using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CoreRuntime = Nornia.Core.Models.Runtime;
using Nornia.Core.Interfaces;
using Nornia.Core.Collections;
using Nornia.Core.Models;
using Nornia.Core.Services;
using Nornia.Desktop.Services;
using Nornia.Package.Providers;
using Nornia.Package.Services;
using Nornia.Runtime.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;

namespace Nornia.Desktop.ViewModels;

public partial class RuntimeViewModel(
    IRuntimeInventoryService inventoryService,
    IPackageProvider packageProvider,
    IPackageInventoryService packageInventoryService,
    IRuntimePackageResolver packageResolver,
    IConfirmationService confirmationService,
    IUiLogService logService,
    IClipboardService? clipboard = null,
    IUiPerformanceMetrics? performanceMetrics = null) : PageViewModel("运行库", logService), INavigationTarget
{
    private readonly IClipboardService _clipboard = clipboard ?? NullClipboardService.Instance;

    public BulkObservableCollection<ManagedComponentItem> Runtimes { get; } = [];
    public ObservableCollection<ManagedComponentItem> SelectedRuntimes { get; } = [];
    public ICollectionView FilteredRuntimes => CollectionViewSource.GetDefaultView(Runtimes);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpgradeCommand))]
    private ManagedComponentItem? selectedRuntime;

    [ObservableProperty] private string filterText = string.Empty;
    public string SelectedRuntimeSummary => SelectedRuntime is null ? "选择 Runtime 查看版本、路径和可用操作。" : $"{SelectedRuntime.Name} {SelectedRuntime.Version} · {SelectedRuntime.Architecture} · {SelectedRuntime.InstallPath}";

    protected override Task OnFirstActivatedAsync()
    {
        SelectedRuntimes.CollectionChanged += (_, _) =>
        {
            RemoveCommand.NotifyCanExecuteChanged();
            UpgradeCommand.NotifyCanExecuteChanged();
            NotifyCopyCommands();
        };
        FilteredRuntimes.Filter = MatchesFilter;
        return ScanAsync();
    }

    // ===== Copy commands (grid 复制选中 / 复制全部) =====

    private void NotifyCopyCommands()
    {
        CopySelectedRuntimesCommand.NotifyCanExecuteChanged();
        CopyAllRuntimesCommand.NotifyCanExecuteChanged();
    }

    private static string FormatRuntime(ManagedComponentItem runtime) =>
        $"{runtime.Name}\t{runtime.Version}\t{runtime.AvailableVersion}\t{runtime.Architecture}\t{runtime.InstallPath}";

    [RelayCommand(CanExecute = nameof(CanCopySelectedRuntimes))]
    private void CopySelectedRuntimes() =>
        _clipboard.SetText(string.Join(Environment.NewLine, SelectedRuntimes.Select(FormatRuntime)));

    private bool CanCopySelectedRuntimes() => SelectedRuntimes.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCopyAllRuntimes))]
    private void CopyAllRuntimes() =>
        _clipboard.SetText(string.Join(Environment.NewLine, Runtimes.Select(FormatRuntime)));

    private bool CanCopyAllRuntimes() => Runtimes.Count > 0;

    [RelayCommand]
    private Task ScanAsync() => RunAsync("扫描 Runtime", async cancellationToken =>
    {
        SetPageLoading();
        try
        {
            await ReloadRuntimesAsync(cancellationToken);
            if (Runtimes.Count == 0)
            {
                SetPageEmpty("没有已扫描到的 Runtime。点击「重新扫描」开始。");
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
    private Task RemoveAsync() => RunAsync("移除 Runtime", async cancellationToken =>
    {
        var runtimes = SelectedRuntimes.Count > 0 ? SelectedRuntimes.ToArray() : SelectedRuntime is not null ? [SelectedRuntime] : [];
        if (runtimes.Length == 0)
        {
            LogService.Write("INFO", "请先选择一个或多个 Runtime。");
            return;
        }

        var selectedRuntimes = runtimes.Where(r => EnvironmentComponentCatalog.Get(r.Name)?.CanManageWithWinget == true).ToArray();
        if (selectedRuntimes.Length == 0)
        {
            LogService.Write("WARNING", "所选 Runtime 中没有可以通过 Winget 管理的项目。");
            return;
        }

        var summary = string.Join(Environment.NewLine, selectedRuntimes.Take(12).Select(r => $"• {r.Name} {r.Version}"));
        if (selectedRuntimes.Length > 12) summary += $"{Environment.NewLine}…以及 {selectedRuntimes.Length - 12} 项";
        if (!confirmationService.Confirm(
                "确认移除 Runtime",
                $"将卸载以下 Runtime：{Environment.NewLine}{Environment.NewLine}{summary}{Environment.NewLine}{Environment.NewLine}此操作可能影响依赖项目，且不保证可自动回滚。选择&quot;否&quot;可安全取消。"))
        {
            LogService.Write("INFO", "用户取消了 Runtime 移除。");
            return;
        }

        foreach (var runtime in selectedRuntimes)
        {
            foreach (var package in packageResolver.ResolveMany(runtime.Name, runtime.Version))
            {
                try
                {
                    await packageProvider.UninstallAsync(package.PackageId, null, OperationProgress, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // Preserve the concrete package id in the operation diagnostic. Without
                    // this context a failed multi-package Runtime removal only reports the
                    // generic "移除 Runtime 失败" message.
                    throw new InvalidOperationException(
                        $"卸载 Runtime 包 {package.PackageId} 失败。", exception);
                }
            }
        }
        await ReloadRuntimesAsync(cancellationToken);
    }, "确认 Runtime 未被占用，再查看 Problems。", canCancel: true);

    [RelayCommand(CanExecute = nameof(CanUpgradeSelected))]
    private Task UpgradeAsync() => RunAsync("升级 Runtime", async cancellationToken =>
    {
        var runtimes = SelectedRuntimes.Count > 0 ? SelectedRuntimes.ToArray() : SelectedRuntime is not null ? [SelectedRuntime] : [];
        if (runtimes.Length == 0)
        {
            LogService.Write("INFO", "请先选择一个或多个 Runtime。");
            return;
        }

        var selectedRuntimes = runtimes.Where(r => r.HasUpdate && EnvironmentComponentCatalog.Get(r.Name)?.CanManageWithWinget == true).ToArray();
        if (selectedRuntimes.Length == 0)
        {
            LogService.Write("WARNING", "所选 Runtime 中没有可用更新或无法通过 Winget 管理。");
            return;
        }

        foreach (var runtime in selectedRuntimes)
        {
            var packages = SelectUpgradePackages(
                runtime.Name,
                runtime.Architecture,
                packageResolver.ResolveMany(runtime.Name, runtime.Version));
            foreach (var package in packages)
            {
                try
                {
                    await packageProvider.UpgradeAsync(package.PackageId, OperationProgress, cancellationToken);
                }
                catch (WingetException exception) when (WingetExitCodes.IsUpdateNotApplicable(exception.ExitCode))
                {
                    // The package snapshot can advertise an update that is no longer
                    // applicable to this architecture/install scope. Do not let (for
                    // example) an up-to-date x86 package block the selected x64 one.
                    LogService.Write("WARNING",
                        $"跳过 {package.PackageId}：WinGet 没有适用的更新（{WingetExitCodes.Format(exception.ExitCode)}）。");
                }
            }
        }
        await ReloadRuntimesAsync(cancellationToken);
    }, "查看 Output 中的安装器诊断后重试。", canCancel: true);

    private async Task ReloadRuntimesAsync(CancellationToken cancellationToken = default)
    {
        var runtimes = await inventoryService.RefreshForcedAsync(cancellationToken);
        var packages = await packageInventoryService.RefreshAsync(OperationProgress, cancellationToken);
        var snapshot = runtimes
                     .Where(runtime => EnvironmentComponentCatalog.Get(runtime.Name)?.Category == EnvironmentComponentCategory.Runtime)
                     .OrderBy(runtime => runtime.Name).ThenByDescending(runtime => runtime.Version)
            .Select(runtime => new ManagedComponentItem(runtime, FindAvailableVersion(runtime, packages)))
            .ToArray();
        using var performance = performanceMetrics?.Begin("runtime.list.publish", snapshot.Length, "ui-batch");
        Runtimes.ReplaceRange(snapshot);
        FilteredRuntimes.Refresh();
        NotifyCopyCommands();
    }
    partial void OnFilterTextChanged(string value) => FilteredRuntimes.Refresh();
    partial void OnSelectedRuntimeChanged(ManagedComponentItem? value)
    {
        OnPropertyChanged(nameof(SelectedRuntimeSummary));
        OnPropertyChanged(nameof(RemoveButtonTooltip));
        OnPropertyChanged(nameof(UpgradeButtonTooltip));
        RemoveCommand.NotifyCanExecuteChanged();
        UpgradeCommand.NotifyCanExecuteChanged();
    }

    public string RemoveButtonTooltip
    {
        get
        {
            if (SelectedRuntime is null && SelectedRuntimes.Count == 0) return "请先选择一个 Runtime";
            if (SelectedRuntime is not null && EnvironmentComponentCatalog.Get(SelectedRuntime.Name)?.CanManageWithWinget != true)
                return $"当前 Runtime ({SelectedRuntime.Name}) 不支持 Winget 管理，无法移除";
            return "选择可由 Winget 管理的 Runtime 后可移除；执行前会确认";
        }
    }

    public string UpgradeButtonTooltip
    {
        get
        {
            if (SelectedRuntime is null && SelectedRuntimes.Count == 0) return "请先选择一个 Runtime";
            if (SelectedRuntime?.HasUpdate != true && !SelectedRuntimes.Any(r => r.HasUpdate)) return "所选 Runtime 没有可用更新";
            if (SelectedRuntime is not null && EnvironmentComponentCatalog.Get(SelectedRuntime.Name)?.CanManageWithWinget != true)
                return $"当前 Runtime ({SelectedRuntime.Name}) 不支持 Winget 管理，无法升级";
            return "选择存在可用更新的 Runtime 后可升级";
        }
    }

    public void ApplyNavigationContext(NavigationContext? context)
    {
        if (context is NavigationContext.Scan) _ = ScanAsync();
    }

    private bool MatchesFilter(object item) => item is ManagedComponentItem runtime
        && (string.IsNullOrWhiteSpace(FilterText) || runtime.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase) || runtime.Version.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

    private bool CanRemoveSelected() =>
        (SelectedRuntime is not null || SelectedRuntimes.Count > 0)
        && (SelectedRuntime is not null ? EnvironmentComponentCatalog.Get(SelectedRuntime.Name)?.CanManageWithWinget == true : SelectedRuntimes.Any(r => EnvironmentComponentCatalog.Get(r.Name)?.CanManageWithWinget == true));

    private bool CanUpgradeSelected() =>
        (SelectedRuntime?.HasUpdate == true || SelectedRuntimes.Any(r => r.HasUpdate))
        && (SelectedRuntime is not null ? EnvironmentComponentCatalog.Get(SelectedRuntime.Name)?.CanManageWithWinget == true : SelectedRuntimes.Any(r => r.HasUpdate && EnvironmentComponentCatalog.Get(r.Name)?.CanManageWithWinget == true));

    private string? FindAvailableVersion(CoreRuntime component, IReadOnlyList<PackageInfo> packages)
    {
        try
        {
            var resolved = packageResolver.ResolveMany(component.Name, component.Version);
            return SelectAvailableVersion(component, packages, resolved);
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Limits Visual C++ upgrades to the architecture represented by the selected runtime.
    /// The mapping intentionally includes x86 for repair/install scenarios, but an upgrade of one
    /// runtime row must not attempt the other architecture first.</summary>
    internal static IReadOnlyList<RuntimePackage> SelectUpgradePackages(
        string runtimeName,
        string architecture,
        IReadOnlyList<RuntimePackage> packages)
    {
        if (!IsVisualCppRuntime(runtimeName)) return packages;

        var suffix = $".{architecture.Trim().ToLowerInvariant()}";
        var matching = packages
            .Where(package => package.PackageId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matching.Length > 0 ? matching : packages;
    }

    /// <summary>Matches the available version to the same architecture as a Visual C++ runtime
    /// row. Other runtimes retain the existing package-id matching behavior.</summary>
    internal static string? SelectAvailableVersion(
        CoreRuntime component,
        IReadOnlyList<PackageInfo> packages,
        IReadOnlyList<RuntimePackage> resolvedPackages)
    {
        var ids = resolvedPackages.Select(package => package.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = packages.Where(package => ids.Contains(package.Id));
        if (IsVisualCppRuntime(component.Name))
        {
            return matches.FirstOrDefault(package =>
                string.Equals(package.Architecture, component.Architecture, StringComparison.OrdinalIgnoreCase))?.AvailableVersion;
        }

        return matches.FirstOrDefault()?.AvailableVersion;
    }

    private static bool IsVisualCppRuntime(string runtimeName) =>
        string.Equals(EnvironmentComponentCatalog.Get(runtimeName)?.Id,
            "visual-cpp-redistributable", StringComparison.OrdinalIgnoreCase);
}

public sealed record ManagedComponentItem(CoreRuntime Component, string? AvailableVersion)
{
    public string Name => Component.Name;
    public string Version => Component.Version;
    public string InstallPath => Component.InstallPath;
    public string Architecture => Component.Architecture;
    public RuntimeStatus Status => Component.Status;
    public bool HasUpdate => !string.IsNullOrWhiteSpace(AvailableVersion);
}
