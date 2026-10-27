namespace Nornia.Desktop.Services;

/// <summary>信息提示框的抽象，让 ViewModel 不必直接调用 <c>System.Windows.MessageBox</c>
/// （那会让 ViewModel 依赖 WPF 窗口基础设施，且无法在无 UI 宿主的测试里验证调用）。</summary>
public interface IMessageService
{
    void ShowInformation(string title, string message);
}

public sealed class MessageService : IMessageService
{
    public void ShowInformation(string title, string message) =>
        System.Windows.MessageBox.Show(
            message,
            title,
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
}
