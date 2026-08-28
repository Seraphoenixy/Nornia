using Nornia.CLI.Localization;

namespace Nornia.CLI.Commands;

/// <summary>Generates help from the registered command set instead of duplicating a hand-maintained
/// text block, so command names, usages and summaries can never drift apart.</summary>
public sealed class HelpCommand(Func<IReadOnlyList<ICommand>> commandFactory) : ICommand
{
    public string Name => CliCommands.Help;
    public IReadOnlyList<string> Aliases => ["--help", "-h"];
    public string Summary => CliText.Get("Summary_Help");
    public string Usage => $"Nornia {Name}";

    public Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        Console.WriteLine(CliText.Get("Help_Title"));
        Console.WriteLine();
        Console.WriteLine(CliText.Get("Help_Commands"));
        foreach (var command in commandFactory()
                     .OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase))
        {
            var usage = command.Usage.Replace("Nornia ", string.Empty, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  {usage}{Environment.NewLine}      {command.Summary}");
        }

        return Task.FromResult(0);
    }
}

