using System.Collections.Specialized;
using System.ComponentModel;
using ChatAgentBridge_Desktop.Inbox;

namespace ChatAgentBridge.Core.Tests;

public sealed class BatchObservableCollectionTests
{
    [Fact]
    public void ReplaceAll_emits_one_reset_and_keeps_supplied_instances()
    {
        var original = Enumerable.Range(0, 500).Select(_ => new object()).ToArray();
        var replacement = original.Take(5).ToArray();
        var collection = new BatchObservableCollection<object>();
        collection.ReplaceAll(original);
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        var propertyChanges = new List<string?>();
        collection.CollectionChanged += (_, args) => notifications.Add(args);
        ((INotifyPropertyChanged)collection).PropertyChanged += (_, args) => propertyChanges.Add(args.PropertyName);

        Assert.True(collection.ReplaceAll(replacement));

        Assert.Equal(5, collection.Count);
        Assert.All(replacement.Select((item, index) => (item, index)), pair =>
            Assert.Same(pair.item, collection[pair.index]));
        Assert.Equal(NotifyCollectionChangedAction.Reset, Assert.Single(notifications).Action);
        Assert.Equal(new[] { nameof(collection.Count), "Item[]" }, propertyChanges);
    }

    [Fact]
    public void ReplaceAll_same_references_is_a_noop()
    {
        var items = Enumerable.Range(0, 500).Select(_ => new object()).ToArray();
        var collection = new BatchObservableCollection<object>();
        collection.ReplaceAll(items);
        var collectionChanges = 0;
        var propertyChanges = 0;
        collection.CollectionChanged += (_, _) => collectionChanges++;
        ((INotifyPropertyChanged)collection).PropertyChanged += (_, _) => propertyChanges++;

        Assert.False(collection.ReplaceAll(items.ToArray()));

        Assert.Equal(0, collectionChanges);
        Assert.Equal(0, propertyChanges);
    }
}
