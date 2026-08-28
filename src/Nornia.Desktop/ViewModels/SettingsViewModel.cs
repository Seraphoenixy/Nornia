using Nornia.Desktop.Services;

namespace Nornia.Desktop.ViewModels;

/// <summary>Workbench adapter for the catalog-driven settings editor.</summary>
public sealed class SettingsViewModel(SettingsEditorViewModel editor, IUiLogService logService)
    : PageViewModel("设置", logService), INavigationTarget
{
    public SettingsEditorViewModel Editor { get; } = editor;
    public override object Sidebar => Editor;

    protected override Task OnFirstActivatedAsync() => Editor.InitializeAsync();

    public void ApplyNavigationContext(NavigationContext? context)
    {
        if (context is NavigationContext.Open) Editor.ShowSettingsCommand.Execute(null);
    }
}
