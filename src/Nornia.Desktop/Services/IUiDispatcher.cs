using System.Windows;

namespace Nornia.Desktop.Services;

/// <summary>Abstraction over the UI-thread dispatcher so <see cref="UiLogService"/> can be tested
/// without a running WPF application.</summary>
public interface IUiDispatcher
{
    bool CheckAccess();
    void BeginInvoke(Action action);

    /// <summary>Runs <paramref name="action"/> on the UI thread and returns a task that completes when
    /// the action has finished. When no WPF application is running the action executes inline.</summary>
    Task InvokeAsync(Action action);
}

/// <summary>Default dispatcher backed by the WPF application dispatcher; degrades to direct
/// invocation when no application is running (e.g. under unit tests).</summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Application.Current?.Dispatcher.CheckAccess() ?? true;

    public void BeginInvoke(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }

    public Task InvokeAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}