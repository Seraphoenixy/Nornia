using Nornia.CLI;
using Nornia.Core.Interfaces;
using Nornia.Package.Services;
using Nornia.Project.Services;

namespace Nornia.Tests;

/// <summary>CLI 命令测试的共享脚手架。命令只依赖 <see cref="CliServices"/> 里的一小组服务,
/// 每条用例真正用到的通常只有一个 —— 其余以 <c>null!</c> 占位,任何意外触达都会立刻 NRE 暴露。</summary>
internal static class CliTestHarness
{
    public static CliServices Services(
        IRuntimeInventoryService? runtimeInventory = null,
        IPackageProvider? packageProvider = null,
        IPackageInventoryService? packageInventory = null,
        CacheClassificationService? cacheClassification = null,
        IEnvironmentRepairPlanner? repairPlanner = null,
        IEnvironmentRepairExecutor? repairExecutor = null,
        IEnvironmentRollbackService? rollbackService = null) => new(
            runtimeInventory ?? null!,
            packageResolver: null!,
            packageProvider ?? null!,
            packageInventory ?? null!,
            cacheInventory: null!,
            cacheCleanup: null!,
            cacheClassification ?? null!,
            profileService: null!,
            checkEngine: null!,
            repairPlanner ?? null!,
            repairExecutor ?? null!,
            rollbackService ?? null!,
            projectCatalog: null!,
            projectLauncher: null!);

    /// <summary>同时重定向 stdout/stderr 执行命令,返回退出码与两路输出。
    /// 调用方必须处于 <c>ConsoleRedirection</c> 集合内(该集合禁用并行)。</summary>
    public static async Task<CliRun> RunAsync(Func<Task<int>> run)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var outWriter = new StringWriter();
        var errorWriter = new StringWriter();
        try
        {
            Console.SetOut(outWriter);
            Console.SetError(errorWriter);
            var exitCode = await run();
            return new CliRun(exitCode, outWriter.ToString(), errorWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}

internal sealed record CliRun(int ExitCode, string Output, string Error);
