using System.Windows;
using System.Windows.Input;

namespace Nornia.Desktop.Views.Dialogs;

/// <summary>结果:用户是否确认 + 输入的主文本(标签名)+ 是否注释化 + 可选注释消息。</summary>
public sealed record TextPromptResult(bool Confirmed, string Text, bool Annotated = false, string? Message = null);

/// <summary>极简模态文本输入对话框(标签名 + 可选注释化消息)。全仓此前无通用文本输入控件,
/// 该对话框同时供标签创建复用。</summary>
public partial class TextPromptDialog : Window
{
    public TextPromptDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    public static TextPromptResult ShowPrompt(Window? owner, string title, string prompt, string initialText = "")
    {
        var dialog = new TextPromptDialog { Owner = owner, Title = title };
        dialog.PromptText.Text = prompt;
        dialog.InputBox.Text = initialText;
        var confirmed = dialog.ShowDialog() == true;
        return new TextPromptResult(confirmed, dialog.InputBox.Text.Trim(), dialog.AnnotateCheck.IsChecked == true, dialog.MessageBox.Text.Trim());
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        // Enter 直接确认;由于不是 AcceptsReturn,此处交由对话框完成。
        if (e.Key is Key.Enter or Key.Return)
        {
            DialogResult = true;
            e.Handled = true;
        }
    }

    private void AnnotateCheck_Checked(object sender, RoutedEventArgs e) =>
        MessageBox.Visibility = AnnotateCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
}