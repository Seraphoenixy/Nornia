using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Package.Providers;
using Nornia.Package.Services;

namespace Nornia.Tests;

public sealed class TrackedEnvironmentRepairExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_ContinuesAfterFailureAndThrowsStructuredBatchError()
    {
        var provider = new FailingPackageProvider();
        var repairLogs = new RecordingRepairLogRepository();
        var executor = new TrackedEnvironmentRepairExecutor(
            provider,
            new PackageInventoryService(provider, new EmptyPackageRepository()),
            new EmptyRuntimeInventory(),
            repairLogs);
        var progress = new RecordingProgress();
        var operations = new RuntimePackageOperation[]
        {
            new("Node.js", "22", "Fail.Package", "22"),
            new("Python", "3.13", "Success.Package", "3.13")
        };

        var exception = await Assert.ThrowsAsync<EnvironmentRepairException>(() =>
            executor.ExecuteAsync(operations, progress));

        Assert.Equal(2, provider.Installs.Count);
        var failure = Assert.Single(exception.Failures);
        Assert.Equal("Node.js", failure.Component);
        Assert.Equal("Fail.Package", failure.PackageId);
        Assert.Equal(WingetExitCodes.InstallerFailed, failure.ExitCode);
        Assert.Contains("stderr line 1", failure.DiagnosticOutput);
        Assert.Contains("provider detail", failure.DiagnosticOutput);
        Assert.Equal(2, repairLogs.Entries.Count);
        Assert.Contains(progress.Outputs, output => output.IsError && output.Text.Contains("Node.js") && output.Text.Contains("Fail.Package"));
        // 只断言十六进制退出码本身:文案随 CI/本机的 UI 文化走不同资源(en-US → "Exit code:",
        // zh-Hans → "退出码:"),断言本地化标签会在英文 runner 上稳定误报。
        Assert.Contains(progress.Outputs, output => output.IsError && output.Text.Contains("0x8A15010C"));
    }

    private sealed class FailingPackageProvider : IPackageProvider
    {
        public string Name => "winget";
        public List<string> Installs { get; } = [];

        public Task<IReadOnlyList<PackageInfo>> SearchAsync(string query, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PackageInfo>>([]);
        public Task<IReadOnlyList<PackageInfo>> ListInstalledAsync(IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PackageInfo>>([]);

        public Task InstallAsync(string packageId, string? version = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default)
        {
            Installs.Add(packageId);
            if (packageId == "Fail.Package")
            {
                progress?.Report(new ProcessOutput("stderr line 1", true));
                progress?.Report(new ProcessOutput("stderr line 2", true));
                throw new WingetException("install", WingetExitCodes.InstallerFailed, "provider detail");
            }

            return Task.CompletedTask;
        }

        public Task UninstallAsync(string packageId, string? packageName = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpgradeAsync(string packageId, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmptyPackageRepository : IPackageRepository
    {
        public Task<IReadOnlyList<PackageInfo>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PackageInfo>>([]);
        public Task ReplaceSnapshotAsync(IReadOnlyCollection<PackageInfo> packages, long scannedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmptyRuntimeInventory : IRuntimeInventoryService
    {
        public Task<IReadOnlyList<Nornia.Core.Models.Runtime>> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Nornia.Core.Models.Runtime>>([]);
        public Task<IReadOnlyList<Nornia.Core.Models.Runtime>> RefreshForcedAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Nornia.Core.Models.Runtime>>([]);
        public Task<IReadOnlyList<Nornia.Core.Models.Runtime>> GetPersistedAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Nornia.Core.Models.Runtime>>([]);
    }

    private sealed class RecordingRepairLogRepository : IEnvironmentRepairLogRepository
    {
        public List<EnvironmentRepairLogEntry> Entries { get; } = [];
        public Task AppendAsync(EnvironmentRepairLogEntry entry, CancellationToken cancellationToken = default) { Entries.Add(entry); return Task.CompletedTask; }
        public Task<IReadOnlyList<EnvironmentRepairLogEntry>> GetByCorrelationAsync(Guid correlationId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<EnvironmentRepairLogEntry>>([]);
    }

    private sealed class RecordingProgress : IProgress<ProcessOutput>
    {
        public List<ProcessOutput> Outputs { get; } = [];
        public void Report(ProcessOutput value) => Outputs.Add(value);
    }
}
