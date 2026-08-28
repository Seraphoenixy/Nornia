using System.Windows;

namespace Nornia.Desktop.Services;

public interface IConfirmationService
{
    bool Confirm(string title, string message);
}

public sealed class ConfirmationService : IConfirmationService
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
}
