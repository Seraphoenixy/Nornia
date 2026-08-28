namespace Nornia.Desktop.Services;

public abstract record NavigationContext
{
    public record Scan : NavigationContext;
    public record Refresh : NavigationContext;
    public record Check : NavigationContext;
    public record Repair : NavigationContext;
    public record New : NavigationContext;
    public record Updates : NavigationContext;
    public record Open : NavigationContext;
    public record OpenInExplorer(string Path) : NavigationContext;
    public record OpenProject(string ProjectPath) : NavigationContext;
    public record CheckProject(string ProjectPath) : NavigationContext;
    public record RepairProject(string ProjectPath) : NavigationContext;
    public record NewProject(string ProjectPath) : NavigationContext;
    public record History : NavigationContext;
    public record CacheHighConfidence : NavigationContext;
    public record CacheByPackage(string PackageId, string PackageName, string Provider) : NavigationContext;

    public static NavigationContext? Parse(string? context)
    {
        if (string.IsNullOrWhiteSpace(context)) return null;

        var parts = context.Split(':', 2);
        var action = parts[0].Trim().ToLowerInvariant();
        var param = parts.Length > 1 ? parts[1] : null;

        return action switch
        {
            "scan" => new Scan(),
            "refresh" => new Refresh(),
            "check" => ParseProjectContext<Check>(param),
            "repair" => ParseProjectContext<Repair>(param),
            "new" => ParseProjectContext<New>(param),
            "updates" => new Updates(),
            "open" => string.IsNullOrWhiteSpace(param) ? new Open() : new OpenInExplorer(param),
            "history" => new History(),
            "cache" => ParseCacheContext(param),
            _ => null
        };
    }

    private static NavigationContext? ParseProjectContext<T>(string? param) where T : NavigationContext
    {
        if (string.IsNullOrWhiteSpace(param)) return new Check();

        const string projectKey = "project=";
        if (param.StartsWith(projectKey, StringComparison.OrdinalIgnoreCase))
        {
            var projectPath = param[projectKey.Length..];
            return typeof(T).Name switch
            {
                nameof(Check) => new CheckProject(projectPath),
                nameof(Repair) => new RepairProject(projectPath),
                nameof(New) => new NewProject(projectPath),
                _ => null
            };
        }

        return new Check();
    }

    private static NavigationContext? ParseCacheContext(string? param)
    {
        if (string.IsNullOrWhiteSpace(param)) return null;
        if (string.Equals(param, "high-confidence", StringComparison.OrdinalIgnoreCase)) return new CacheHighConfidence();
        return null;
    }
}

public sealed record NavigationRequest(string Destination, NavigationContext? Context)
{
    public NavigationRequest(string destination, string? contextString) 
        : this(destination, NavigationContext.Parse(contextString)) { }
}
