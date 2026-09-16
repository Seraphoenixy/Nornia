using Nornia.Core.Models;
using Nornia.Project.Models;

namespace Nornia.CLI;

/// <summary>Tabular console output shared by several commands.</summary>
public static class CliOutput
{
    public static void PrintPackages(IEnumerable<PackageInfo> packages)
    {
        Console.WriteLine("ID\tNAME\tVERSION\tARCHITECTURE\tAVAILABLE");
        foreach (var package in packages)
        {
            Console.WriteLine($"{package.Id}\t{package.Name}\t{package.Version}\t{package.Architecture}\t{package.AvailableVersion ?? "-"}");
        }
    }

    public static void PrintCaches(IEnumerable<CacheCandidate> caches)
    {
        Console.WriteLine("ID\tCONFIDENCE\tCATEGORY\tTYPE\tSOURCE\tUSERDIR\tSIZE\tPATH\tREASON");
        foreach (var cache in caches)
        {
            Console.WriteLine($"{cache.Id}\t{cache.Confidence}\t{cache.CategoryDisplayName}\t{cache.CacheTypeDisplay}\t{cache.Source}\t{cache.UserDirectory}\t{cache.SizeBytes}\t{cache.Path}\t{cache.ClassificationReason ?? cache.Reason}");
        }
    }

    public static void PrintRepairPlan(EnvironmentRepairPlan plan)
    {
        if (plan.Actions.Count == 0)
        {
            Console.WriteLine(Nornia.CLI.Localization.CliText.Get("Plan_NoRequirements"));
            return;
        }

        Console.WriteLine("COMPONENT\tREQUIRED\tACTION\tDETAIL");
        foreach (var action in plan.Actions)
        {
            var detail = action.Operation is null
                ? action.Message
                : $"{action.Operation.PackageId} {action.Operation.PackageVersion ?? action.Operation.TargetVersion}";
            Console.WriteLine($"{action.Component}\t{action.RequiredVersion}\t{action.Disposition}\t{detail}");
        }
    }
}
