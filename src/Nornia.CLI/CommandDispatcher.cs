using Nornia.CLI.Commands;
using Nornia.CLI.Localization;

namespace Nornia.CLI;

/// <summary>A routable CLI command. <see cref="Name"/> (and <see cref="Aliases"/>) are whitespace-separated
/// route segments matched against the head of the argument list; the remaining arguments are passed to
/// <see cref="ExecuteAsync"/>.</summary>
public interface ICommand
{
    string Name { get; }

    IReadOnlyList<string> Aliases { get; }

    /// <summary>One-line description shown in help output.</summary>
    string Summary { get; }

    /// <summary>Usage line shown in help output.</summary>
    string Usage { get; }

    Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken);
}

/// <summary>Routes argument lists to the command whose route segments match the head of the input.
/// The longest matching route wins, so <c>runtime list</c> is preferred over an ambiguous <c>runtime</c>
/// prefix. Unknown or empty input falls through to help.</summary>
public sealed class CommandDispatcher(IEnumerable<ICommand> commands)
{
    private readonly IReadOnlyList<ICommand> _commands = commands.ToArray();

    public IReadOnlyList<ICommand> Commands => _commands;

    public async Task<int> DispatchAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0 || args[0] is CliCommands.Help or "--help" or "-h")
        {
            return await PrintHelpAsync();
        }

        var segments = args.Select(segment => segment.ToLowerInvariant()).ToArray();
        ICommand? match = null;
        var matchLength = 0;
        foreach (var command in _commands)
        {
            foreach (var route in Routes(command))
            {
                var routeLength = route.Count;
                if (routeLength <= matchLength || routeLength > segments.Length || !IsPrefix(route, segments))
                {
                    continue;
                }

                match = command;
                matchLength = routeLength;
            }
        }

        if (match is null)
        {
            Console.Error.WriteLine(CliText.Format("Error_UnknownCommand", string.Join(' ', args)));
            await PrintHelpAsync();
            return 2;
        }

        return await match.ExecuteAsync(args.Skip(matchLength).ToArray(), cancellationToken);
    }

    private async Task<int> PrintHelpAsync()
    {
        var help = _commands.OfType<HelpCommand>().FirstOrDefault();
        if (help is not null)
        {
            return await help.ExecuteAsync([], CancellationToken.None);
        }

        Console.WriteLine(CliText.Get("Help_Title"));
        return 0;
    }

    private static IEnumerable<IReadOnlyList<string>> Routes(ICommand command)
    {
        yield return Split(command.Name);
        foreach (var alias in command.Aliases)
        {
            yield return Split(alias);
        }
    }

    private static IReadOnlyList<string> Split(string name) =>
        name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsPrefix(IReadOnlyList<string> route, IReadOnlyList<string> args)
    {
        for (var index = 0; index < route.Count; index++)
        {
            if (!string.Equals(route[index], args[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
