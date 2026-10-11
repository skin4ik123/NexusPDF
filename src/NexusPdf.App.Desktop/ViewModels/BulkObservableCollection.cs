using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace NexusPdf.App.Desktop.ViewModels;

/// <summary>
/// Коллекция, которую можно заменить целиком ОДНИМ уведомлением.
///
/// Обычный ObservableCollection сообщает о каждой добавленной строке
/// отдельно, и на документе в 333 страницы список страниц, панель миниатюр и
/// организатор пересчитывали раскладку по триста раз каждый. Пока это
/// происходило, поток интерфейса был занят, и уже нарисованная страница не
/// могла появиться на экране — со стороны это выглядело как «страницы очень
/// долго рендерятся», хотя сам рендер занимал сорок миллисекунд.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _silent;

    /// <summary>Preserve item containers for small edits; batch large rearrangements.</summary>
    public bool Reconcile(IReadOnlyList<T> items, int maxChanges = 64)
    {
        var working = this.ToList();
        var desired = new HashSet<T>(items);
        var changes = new List<(NotifyCollectionChangedAction Action, int Index, int OldIndex, T Item)>();
        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < items.Count; i++)
        {
            if (i < working.Count && comparer.Equals(working[i], items[i])) continue;
            var from = working.IndexOf(items[i], Math.Min(i + 1, working.Count));
            if (from >= 0)
            {
                working.RemoveAt(from);
                working.Insert(i, items[i]);
                changes.Add((NotifyCollectionChangedAction.Move, i, from, items[i]));
            }
            else if (i < working.Count && !desired.Contains(working[i]))
            {
                working[i] = items[i];
                changes.Add((NotifyCollectionChangedAction.Replace, i, -1, items[i]));
            }
            else
            {
                working.Insert(i, items[i]);
                changes.Add((NotifyCollectionChangedAction.Add, i, -1, items[i]));
            }
            if (changes.Count > maxChanges)
            {
                ReplaceAll(items);
                return true;
            }
        }
        for (var i = working.Count - 1; i >= items.Count; i--)
        {
            changes.Add((NotifyCollectionChangedAction.Remove, i, -1, working[i]));
            if (changes.Count > maxChanges)
            {
                ReplaceAll(items);
                return true;
            }
        }
        foreach (var change in changes)
        {
            switch (change.Action)
            {
                case NotifyCollectionChangedAction.Move: Move(change.OldIndex, change.Index); break;
                case NotifyCollectionChangedAction.Replace: this[change.Index] = change.Item; break;
                case NotifyCollectionChangedAction.Add: Insert(change.Index, change.Item); break;
                case NotifyCollectionChangedAction.Remove: RemoveAt(change.Index); break;
            }
        }
        return changes.Count > 0;
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_silent) base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_silent) base.OnPropertyChanged(e);
    }

    /// <summary>Заменить содержимое целиком: одно уведомление на всю операцию.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        _silent = true;
        try
        {
            Clear();
            foreach (var item in items) Add(item);
        }
        finally
        {
            _silent = false;
        }

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
