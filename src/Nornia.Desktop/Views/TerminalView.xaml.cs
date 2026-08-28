using System.Windows.Controls;
using System.Windows.Input;
using Nornia.Desktop.ViewModels;

namespace Nornia.Desktop.Views;

public partial class TerminalView : UserControl
{
    public TerminalView()
    {
        InitializeComponent();
        // 面板视图(重新)进入可视化树时补一次聚焦:切回终端面板后无需点击即可打字。
        Loaded += (_, _) =>
        {
            if (DataContext is TerminalViewModel { HasSessions: true })
            {
                FocusSurface();
            }
        };
    }

    /// <summary>把键盘焦点交给交互式终端表面(唯一的命令输入入口):键盘事件直接写入当前
    /// ConPTY 会话,不再经过额外发送框。</summary>
    public void FocusSurface()
    {
        TerminalSurface.Focus();
        Keyboard.Focus(TerminalSurface);
    }
}
