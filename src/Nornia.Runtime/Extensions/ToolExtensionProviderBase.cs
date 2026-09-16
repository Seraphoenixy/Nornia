using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using Nornia.Runtime.Services;

namespace Nornia.Runtime.Extensions;

/// <summary>扩展依赖适配器基类:持有进程执行器与生态元数据,并提供与
/// <see cref="CommandRuntimeProviderBase"/> 一致的 CLI 定位辅助(where.exe 首匹配 →
/// PATH 扫描 → App Paths 注册表)。生态 CLI 缺失时各命令抛
/// <see cref="InvalidOperationException"/> 说明缺哪个命令。</summary>
public abstract class ToolExtensionProviderBase(string ecosystemName, IProcessRunner processRunner) : IToolExtensionProvider
{
    public abstract ToolExtensionEcosystem Ecosystem { get; }
    public string EcosystemName { get; } = ecosystemName;

    protected IProcessRunner ProcessRunner { get; } = processRunner;

    public abstract Task<IReadOnlyList<ToolExtension>> ListInstalledAsync(IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default);
    public abstract Task<IReadOnlyList<ToolExtension>> ListOutdatedAsync(IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default);
    public abstract Task InstallAsync(string name, string? version = null, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default);
    public abstract Task UninstallAsync(string name, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default);
    public abstract Task UpgradeAsync(string name, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default);
    public abstract Task<IReadOnlyList<ToolExtensionDependency>> GetDependenciesAsync(string name, IProgress<ProcessOutput>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>定位生态 CLI 的绝对路径。返回 null 表示当前环境没有该命令。</summary>
    protected static async Task<string?> LocateExecutableAsync(IProcessRunner processRunner, string executable, CancellationToken cancellationToken = default)
    {
        var whereResult = await processRunner.RunAsync("where.exe", [executable], cancellationToken: cancellationToken);
        if (whereResult.IsSuccess)
        {
            var first = whereResult.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(first) && File.Exists(first))
            {
                return first;
            }
        }

        var fromPath = WindowsPathLocator.ResolveFromPath(executable);
        if (fromPath is not null) return fromPath;

        return WindowsPathLocator.ResolveWindowsAppPath(executable);
    }

    /// <summary>按子类给定的进程与参数运行并校验退出码;失败时抛带诊断的异常。</summary>
    protected async Task<ProcessResult> RunCheckedAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IProgress<ProcessOutput>? progress,
        CancellationToken cancellationToken,
        string description)
    {
        var result = await ProcessRunner.RunAsync(fileName, arguments, progress, cancellationToken);
        if (!result.IsSuccess)
        {
            var detail = (result.StandardError.Trim() is { Length: > 0 } error ? error : result.StandardOutput.Trim());
            throw new InvalidOperationException(
                $"{EcosystemName} {description}失败（exit {result.ExitCode}）：{Truncate(detail)}");
        }

        return result;
    }

    private static string Truncate(string value, int maxLen = 200) =>
        value.Length <= maxLen ? value : string.Concat(value.AsSpan(0, maxLen - 3), "...");
}
