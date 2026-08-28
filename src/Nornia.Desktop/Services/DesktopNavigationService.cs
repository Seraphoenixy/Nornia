namespace Nornia.Desktop.Services;

public interface IDesktopNavigationService
{
    event EventHandler<NavigationRequest>? NavigationRequested;
    void Navigate(string destination, NavigationContext? context = null);
    void Navigate(string destination, string? contextString) => Navigate(destination, NavigationContext.Parse(contextString));
}

public sealed class DesktopNavigationService : IDesktopNavigationService
{
    public event EventHandler<NavigationRequest>? NavigationRequested;
    public void Navigate(string destination, NavigationContext? context = null) =>
        NavigationRequested?.Invoke(this, new NavigationRequest(destination, context));
}

public interface INavigationTarget
{
    void ApplyNavigationContext(NavigationContext? context);
}

/// <summary>Canonical navigation destinations. Pages and task planners reference these constants so a
/// destination is defined once and never re-typed as a raw string.</summary>
public static class NavigationTargets
{
    public const string Dashboard = "Dashboard";
    public const string Runtime = "Runtime";
    public const string Tools = "Tools";
    public const string Packages = "Packages";
    public const string Cache = "Cache";
    public const string Projects = "Projects";
    public const string Explorer = "Explorer";
    public const string Search = "Search";
    public const string Git = "Git";
    public const string Settings = "Settings";
}
