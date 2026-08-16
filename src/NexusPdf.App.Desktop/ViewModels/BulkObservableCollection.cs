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
