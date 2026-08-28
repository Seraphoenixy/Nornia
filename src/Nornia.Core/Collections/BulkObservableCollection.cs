using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Nornia.Core.Collections;

/// <summary>
/// Observable collection that can publish a snapshot with one Reset notification.
/// The item list is built before this method is called, so large UI lists do not cause one
/// layout/filter pass per item.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var snapshot = items as IReadOnlyList<T> ?? items.ToArray();
        if (this.SequenceEqual(snapshot))
        {
            return;
        }

        Items.Clear();
        foreach (var item in snapshot)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var additions = items as IReadOnlyList<T> ?? items.ToArray();
        if (additions.Count == 0)
        {
            return;
        }

        foreach (var item in additions)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void RemoveFirst(int count)
    {
        if (count <= 0 || Count == 0)
        {
            return;
        }

        var removeCount = Math.Min(count, Count);
        if (Items is List<T> list)
        {
            list.RemoveRange(0, removeCount);
        }
        else
        {
            for (var i = 0; i < removeCount; i++)
            {
                Items.RemoveAt(0);
            }
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
