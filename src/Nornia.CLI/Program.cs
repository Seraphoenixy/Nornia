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
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or InvalidOperationException)
        {
            Console.Error.WriteLine(CliText.Format("Error_Prefix", ex.Message));
            return 1;
        }
        finally
        {
            services?.Dispose();
        }
    }
}