using Nornia.CLI;
using Nornia.CLI.Commands;

namespace Nornia.Tests;

/// <summary>CLI 命令的参数契约。这些用例不碰任何真实服务 —— 参数校验必须发生在服务调用之前,
/// 否则一个拼错的选项会先触发磁盘扫描/进程启动,再报错。
/// 覆盖 §1.4 引入的 <see cref="CliUsageException"/>(退出码 2)契约。
/// 命令实例经反射构造,因此新增命令若忘了 <c>CliServices</c> 构造函数形态,这里会立刻发现。</summary>
public sealed class CliCommandContractTests
{
    [Theory]
    [InlineData(typeof(RuntimeInstallCommand))]
    [InlineData(typeof(RuntimeRemoveCommand))]
    [InlineData(typeof(ToolInstallCommand))]
    [InlineData(typeof(ToolRemoveCommand))]
    public async Task NameAndVersionCommands_RejectMissingArguments(Type commandType)
    {
        var command = Create(commandType);

        await Assert.ThrowsAsync<CliUsageException>(() => command.ExecuteAsync([], CancellationToken.None));
        await Assert.ThrowsAsync<CliUsageException>(() => command.ExecuteAsync(["only-one"], CancellationToken.None));
    }

    [Theory]
    [InlineData(typeof(PackageSearchCommand))]
    [InlineData(typeof(PackageInstallCommand))]
    [InlineData(typeof(PackageUninstallCommand))]
    [InlineData(typeof(PackageUpgradeCommand))]
    [InlineData(typeof(ProjectExportCommand))]
    [InlineData(typeof(ProjectImportCommand))]
    [InlineData(typeof(EnvRollbackCommand))]
    public async Task ArgumentTakingCommands_RejectNoArguments(Type commandType)
    {
        var command = Create(commandType);

        await Assert.ThrowsAsync<CliUsageException>(() => command.ExecuteAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task PackageCacheClean_RequiresAnIdAndRejectsUnknownOptions()
    {
        var command = new PackageCacheCleanCommand(CliTestHarness.Services());

        // 没有 --id 时是用法错误。
        await Assert.ThrowsAsync<CliUsageException>(() => command.ExecuteAsync([], CancellationToken.None));
        // 未知选项必须报错而不是被静默吞掉。
        await Assert.ThrowsAsync<CliUsageException>(() => command.ExecuteAsync(["--nope"], CancellationToken.None));
    }

    [Fact]
    public async Task EnvFix_RejectsUnknownOptionsAndExtraPaths()
    {
        var command = new EnvFixCommand(CliTestHarness.Services());

        // 旧实现会把 --force 静默忽略、把第二个路径当作新的路径覆盖 —— 都改成用法错误。
        await Assert.ThrowsAsync<CliUsageException>(() => command.ExecuteAsync(["--force"], CancellationToken.None));
        await Assert.ThrowsAsync<CliUsageException>(() => command.ExecuteAsync([@"C:\a", @"C:\b"], CancellationToken.None));
    }

    [Fact]
    public async Task ProjectInit_RejectsUnknownOptions()
    {
        var command = new ProjectInitCommand(CliTestHarness.Services());

        await Assert.ThrowsAsync<CliUsageException>(() => command.ExecuteAsync(["--unknown"], CancellationToken.None));
    }

    private static ICommand Create(Type commandType) =>
        (ICommand)Activator.CreateInstance(commandType, CliTestHarness.Services())!;
}
