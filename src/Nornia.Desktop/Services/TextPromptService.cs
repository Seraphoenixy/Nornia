namespace Nornia.Desktop.Services;

/// <summary>模态文本输入的抽象。ViewModel 只依赖它，不再直接引用 <c>Views.Dialogs.TextPromptDialog</c>：
/// 原先 <c>GitViewModel</c> 用完全限定名 + <c>Application.Current.MainWindow</c> 直接弹窗，
/// 绕过了 grep 审查，也让「创建标签」这条纯业务命令脱离不了 STA 宿主（无法用替身单测）。
/// 与既有的 <see cref="IConfirmationService"/>、<see cref="IFolderPickerService"/> 同一模式。</summary>
public interface ITextPromptService
{
    TextPromptOutcome Prompt(string title, string message, string initialText = "");
}

/// <summary>对话框结果。刻意不复用 <c>Views.Dialogs.TextPromptResult</c> —— Services 层不应向 Views 反向依赖。</summary>
public sealed record TextPromptOutcome(bool Confirmed, string Text, bool Annotated = false, string? Message = null);

/// <summary>生产实现：把抽象转接到真实的 WPF 对话框（宿主可能不存在，此时对话框自行决定 owner）。</summary>
public sealed class TextPromptService : ITextPromptService
{
    public TextPromptOutcome Prompt(string title, string message, string initialText = "")
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        var result = Views.Dialogs.TextPromptDialog.ShowPrompt(owner, title, message, initialText);
        return new TextPromptOutcome(result.Confirmed, result.Text, result.Annotated, result.Message);
    }
}
