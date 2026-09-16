using System.Windows;
using System.Windows.Controls;
using Nornia.Desktop.ViewModels;

namespace Nornia.Desktop.Views;

public partial class ToolsView : UserControl
{
    public ToolsView() => InitializeComponent();

    /// <summary>依赖树节点展开时懒加载其直接依赖(薄转发,无业务逻辑)。</summary>
    private async void DependencyTreeItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem { DataContext: ToolExtensionDependencyNode node }
            && DataContext is ToolsViewModel viewModel)
        {
            await viewModel.LoadDependencyNodeAsync(node);
        }
    }
}
