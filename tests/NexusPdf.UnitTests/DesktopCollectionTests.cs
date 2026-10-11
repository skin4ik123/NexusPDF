using System.Collections.Specialized;
using NexusPdf.App.Desktop.ViewModels;

namespace NexusPdf.UnitTests;

public sealed class DesktopCollectionTests
{
    [Fact]
    public void Editing_One_Of_300_Pages_Does_Not_Reset_Other_Containers()
    {
        var pages = Enumerable.Range(0, 300).Select(_ => new object()).ToArray();
        var collection = new BulkObservableCollection<object>();
        collection.ReplaceAll(pages);
        var events = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => events.Add(e.Action);
        var edited = pages.ToArray();
        edited[155] = new object();
        collection.Reconcile(edited);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Replace }, events);
        Assert.Same(pages[154], collection[154]);
        Assert.Same(pages[156], collection[156]);
    }

    [Fact]
    public void Moving_Inserting_And_Deleting_Pages_Keeps_The_Desired_Order()
    {
        var collection = new BulkObservableCollection<string> { "A", "B", "C", "D" };
        var actions = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => actions.Add(e.Action);
        collection.Reconcile(new[] { "C", "A", "E", "D" });
        Assert.Equal(new[] { "C", "A", "E", "D" }, collection);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        collection.Reconcile(new[] { "E", "A" });
        Assert.Equal(new[] { "E", "A" }, collection);
    }

    [Fact]
    public void Unchanged_Pages_Produce_No_Notifications()
    {
        var collection = new BulkObservableCollection<int> { 1, 2, 3 };
        var notifications = 0;
        collection.CollectionChanged += (_, _) => notifications++;
        Assert.False(collection.Reconcile(new[] { 1, 2, 3 }));
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void Huge_Rearrangements_Are_Still_One_Batch()
    {
        var collection = new BulkObservableCollection<int>();
        collection.ReplaceAll(Enumerable.Range(0, 1000));
        var actions = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => actions.Add(e.Action);
        var desired = Enumerable.Range(0, 1000).Reverse().ToArray();
        collection.Reconcile(desired);
        Assert.Equal(desired, collection);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, actions);
    }

    [Fact]
    public void Repeated_Pages_Are_Not_Lost()
    {
        var collection = new BulkObservableCollection<int> { 1, 1, 2, 3 };
        collection.Reconcile(new[] { 2, 1, 3, 1, 1 });
        Assert.Equal(new[] { 2, 1, 3, 1, 1 }, collection);
    }
}
