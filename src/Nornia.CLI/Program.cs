using Microsoft.Extensions.DependencyInjection;
using Nornia.CLI.Localization;
using Nornia.Composition;
using Nornia.Storage.Database;
using System.Globalization;

namespace Nornia.CLI;

/// <summary>Entry point: wires the shared composition root with the command set, applies the effective
/// culture from the system locale or <c>--lang</c>, initializes the database and dispatches.</summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        ServiceProvider? services = null;
        try
        {
            var invocation = CliArguments.Parse(args);
            CultureInfo.CurrentUICulture = invocation.Culture;

            services = new ServiceCollection()
                .AddNorniaServices()
                .AddNorniaCommands()
                .BuildServiceProvider();

            var database = services.GetRequiredService<NorniaDatabase>();
            await database.InitializeAsync(cancellation.Token);
            return await services.GetRequiredService<CommandDispatcher>()
                .DispatchAsync(invocation.Arguments, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(CliText.Get("Error_OperationCancelled"));
            return 130;
        }
        catch (CliUsageException ex)
        {
            // 退出码 2 = 未知命令或参数组合(见 docs/design.md 的退出码约定)。
            Console.Error.WriteLine(CliText.Format("Error_Prefix", ex.Message));
            return 2;
        }
        catch (Nornia.Core.Models.EnvironmentRepairException ex)
        {
            // 修复失败时把批次 id 显式交还用户,并给出可复制的回滚命令 —— 否则用户只看到一句失败信息,
            // 手里那个 correlation_id 无从使用。
            Console.Error.WriteLine(CliText.Format("Error_Prefix", ex.Message));
            Console.Error.WriteLine(CliText.Format("Env_FixRollbackHint", ex.CorrelationId.ToString("N")));
            return 1;
        }
        catch (Exception ex)
        {
            // 兜底所有领域异常。原先这里只接 ArgumentException/IOException/InvalidDataException/
            // InvalidOperationException,GitOperationException、SqliteException、
            // UnauthorizedAccessException、EnvironmentRepairException、FormatException 等一律漏网,
            // 结果是把堆栈直接喷给用户,进程还以 0xE0434352 退出。
            Console.Error.WriteLine(CliText.Format("Error_Prefix", ex.Message));
            if (WantsStackTrace())
            {
                Console.Error.WriteLine(ex.StackTrace);
            }

            return 1;
        }
        finally
        {
            services?.Dispose();
        }
    }

    /// <summary>完整堆栈会淹没正常使用场景的输出,只在显式开启时打印(设 NORNIA_CLI_STACKTRACE=1)。</summary>
    private static bool WantsStackTrace() =>
        Environment.GetEnvironmentVariable("NORNIA_CLI_STACKTRACE") is "1" or "true";
}