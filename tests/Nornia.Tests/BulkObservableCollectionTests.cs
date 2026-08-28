using System.Collections.Specialized;
using Nornia.Core.Collections;

namespace Nornia.Tests;

public sealed class BulkObservableCollectionTests
{
    [Fact]
    public void ReplaceRange_PublishesOneResetForLargeSnapshot()
    {
        var collection = new BulkObservableCollection<int>();
        var notifications = 0;
        NotifyCollectionChangedAction? action = null;
        collection.CollectionChanged += (_, args) => { notifications++; action = args.Action; };

        collection.ReplaceRange(Enumerable.Range(0, 100_000));

        Assert.Equal(100_000, collection.Count);
        Assert.Equal(1, notifications);
        Assert.Equal(NotifyCollectionChangedAction.Reset, action);
    }

    [Fact]
    public void ReplaceRange_SkipsNotificationWhenSnapshotIsUnchanged()
    {
        var collection = new BulkObservableCollection<int>();
        collection.ReplaceRange([1, 2, 3]);
        var notifications = 0;
        collection.CollectionChanged += (_, _) => notifications++;

        collection.ReplaceRange([1, 2, 3]);

        Assert.Equal(0, notifications);
    }

    [Fact]
    public void RemoveFirst_TrimsAsOneLogicalUpdate()
    {
        var collection = new BulkObservableCollection<int>();
        collection.ReplaceRange(Enumerable.Range(0, 100));
        var notifications = 0;
        collection.CollectionChanged += (_, args) => { if (args.Action == NotifyCollectionChangedAction.Reset) notifications++; };

        collection.RemoveFirst(40);

        Assert.Equal(60, collection.Count);
        Assert.Equal(40, collection[0]);
        Assert.Equal(1, notifications);
    }
}
