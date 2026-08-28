using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Nornia.Desktop.Behaviors;

/// <summary>
/// VS Code-style right-click selection for list-like controls (<see cref="ListBox"/>,
/// <see cref="DataGrid"/>). Right-clicking an unselected row selects it first; right-clicking
/// inside an existing multi-selection leaves the selection set intact, so Shift/Ctrl multi-selection
/// survives a context-menu invocation.
/// </summary>
public static class ListSelectionBehavior
{
    public static readonly DependencyProperty SelectOnRightClickProperty = DependencyProperty.RegisterAttached(
        "SelectOnRightClick",
        typeof(bool),
        typeof(ListSelectionBehavior),
        new PropertyMetadata(false, OnSelectOnRightClickChanged));

    public static bool GetSelectOnRightClick(DependencyObject element) =>
        (bool)element.GetValue(SelectOnRightClickProperty);

    public static void SetSelectOnRightClick(DependencyObject element, bool value) =>
        element.SetValue(SelectOnRightClickProperty, value);

    private static void OnSelectOnRightClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.PreviewMouseRightButtonDown -= OnPreviewMouseRightButtonDown;
        if (e.NewValue is true)
        {
            element.PreviewMouseRightButtonDown += OnPreviewMouseRightButtonDown;
        }
    }

    private static void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        switch (sender)
        {
            case ListBox listBox when FindAncestor<ListBoxItem>(source) is { IsSelected: false } item:
                if (listBox.SelectionMode == SelectionMode.Single)
                {
                    // Single-select lists (e.g. the editor tab strip) must not touch SelectedItems —
                    // WPF throws "只能在多选模式中更改 SelectedItems 集合". Selecting the data item
                    // through SelectedItem gives the same right-click-select semantics legally.
                    listBox.SelectedItem = item.DataContext;
                }
                else
                {
                    listBox.SelectedItems.Clear();
                    item.IsSelected = true;
                }

                e.Handled = true;
                break;
            case DataGrid dataGrid when FindAncestor<DataGridRow>(source) is { IsSelected: false } row:
                if (dataGrid.SelectionMode == DataGridSelectionMode.Single)
                {
                    dataGrid.SelectedItem = row.DataContext;
                }
                else
                {
                    dataGrid.SelectedItems.Clear();
                    row.IsSelected = true;
                }

                e.Handled = true;
                break;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }

        return null;
    }
}