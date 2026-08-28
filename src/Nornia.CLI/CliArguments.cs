using Nornia.CLI.Localization;
using System.Globalization;

namespace Nornia.CLI;

/// <summary>Canonical CLI command/segment names. Commands and the dispatcher reference these constants
/// so help text, routing and validation can never drift apart.</summary>
public static class CliCommands
{
    public const string Runtime = "runtime";
    public const string Tool = "tool";
    public const string Package = "package";
    public const string Cache = "cache";
    public const string Environment = "env";
    public const string Project = "project";

    public const string List = "list";
    public const string Refresh = "refresh";
    public const string Install = "install";
    public const string Remove = "remove";
    public const string Search = "search";
    public const string Uninstall = "uninstall";
    public const string Upgrade = "upgrade";
    public const string Scan = "scan";
    public const string Clean = "clean";
    public const string Check = "check";
    public const string Plan = "plan";
    public const string Fix = "fix";
    public const string Init = "init";
    public const string Export = "export";
    public const string Import = "import";
    public const string Open = "open";
    public const string Help = "help";

    public const string ApplyOption = "--apply";
    public const string IdOption = "--id";
    public const string NameOption = "--name";
    public const string LanguageOption = "--lang";

    public const string RuntimeList = Runtime;
    public const string ToolList = Tool;
    public const string PackageList = Package;
}

/// <summary>Parsed invocation: the effective UI culture and the command arguments left after removing
/// the global <c>--lang</c> option.</summary>
public sealed record CliInvocation(CultureInfo Culture, IReadOnlyList<string> Arguments);

public static class CliArguments
{
    public static CliInvocation Parse(string[] args)
    {
        CultureInfo? culture = null;
        var remaining = new List<string>(args.Length);
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], CliCommands.LanguageOption, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                var language = args[++index];
                // ICU accepts arbitrary names as culture objects, so an explicit membership check is
                // required to reject typos like "--lang not-a-culture".
                if (!CultureInfo.GetCultures(CultureTypes.AllCultures).Any(candidate =>
                        string.Equals(candidate.Name, language, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ArgumentException(CliText.Format("Error_UnknownLanguage", language));
                }

                culture = new CultureInfo(language);
                continue;
            }

            if (string.Equals(args[index], CliCommands.LanguageOption, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Usage: Nornia {CliCommands.LanguageOption} <language-code>");
            }

            remaining.Add(args[index]);
        }

        return new CliInvocation(culture ?? CultureInfo.CurrentUICulture, remaining);
    }
}
