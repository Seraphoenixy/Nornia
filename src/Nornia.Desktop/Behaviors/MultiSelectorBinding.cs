using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Nornia.Desktop.Behaviors;

/// <summary>
/// 将 <see cref="MultiSelector"/>（如 <see cref="DataGrid"/>）或 <see cref="ListBox"/> 的多选集合同步
/// 到 ViewModel 中的一个 <see cref="IList"/> 绑定属性，从而避免在代码后台订阅 SelectionChanged 事件。
/// </summary>
public static class MultiSelectorBinding
{
    public static readonly DependencyProperty SelectedItemsProperty =
        DependencyProperty.RegisterAttached(
            "SelectedItems",
            typeof(IEnumerable),
            typeof(MultiSelectorBinding),
            new PropertyMetadata(null, OnSelectedItemsChanged));

    public static IEnumerable GetSelectedItems(DependencyObject element) =>
        (IEnumerable)element.GetValue(SelectedItemsProperty);

    public static void SetSelectedItems(DependencyObject element, IEnumerable value) =>
        element.SetValue(SelectedItemsProperty, value);

    // 防止在还原选中项到控件时再次触发 SelectionChanged → 无限递归。
    private static readonly DependencyProperty IsSyncingProperty =
        DependencyProperty.RegisterAttached(
            "IsSyncing",
            typeof(bool),
            typeof(MultiSelectorBinding),
            new PropertyMetadata(false));

    private static void OnSelectedItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Selector selector)
        {
            return;
        }

        selector.SelectionChanged -= OnSelectionChanged;
        if (e.NewValue is not null)
        {
            selector.SelectionChanged += OnSelectionChanged;
        }
    }

    private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not Selector selector || selector.GetValue(IsSyncingProperty) is true)
        {
            return;
        }

        // The bound target is the value of the attached SelectedItems property (the ViewModel's
        // collection), NOT the control's own SelectedItems. Writing into the control's own
        // SelectedItems would clear the actual selection (leaving the bound SelectedItem null) and
        // never populate the ViewModel, so the batch tool commands would stay disabled.
        if (GetSelectedItems(selector) is not IList target)
        {
            return;
        }

        var selectedItems = GetSelectorSelectedItems(selector);
        if (selectedItems is null)
        {
            return;
        }

        try
        {
            selector.SetValue(IsSyncingProperty, true);
            target.Clear();
            foreach (var item in selectedItems)
            {
                target.Add(item);
            }
        }
        finally
        {
            selector.SetValue(IsSyncingProperty, false);
        }
    }

    private static IList? GetSelectorSelectedItems(Selector selector) => selector switch
    {
        ListBox listBox => listBox.SelectedItems,
        MultiSelector multiSelector => multiSelector.SelectedItems,
        _ => null
    };
}
