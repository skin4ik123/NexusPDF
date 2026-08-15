using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.App.Desktop.ViewModels;

/// <summary>
/// Узел оглавления. Целевая страница хранится ЛОГИЧЕСКОЙ: если страницу
/// удалили или переставили в режиме систематизации, закладка ведёт туда, где
/// страница оказалась, а для удалённой честно показывает «страницы больше нет».
/// </summary>
public sealed partial class BookmarkViewModel : ObservableObject
{
    public BookmarkViewModel(PdfBookmark source, Func<int, int> mapSourcePage)
    {
        Title = source.Title;
        SourcePageIndex = source.TargetPageIndex;
        Children = new ObservableCollection<BookmarkViewModel>(
            source.Children.Select(c => new BookmarkViewModel(c, mapSourcePage)));
        Remap(mapSourcePage);
    }

    public string Title { get; }

    /// <summary>Страница в ИСХОДНОМ документе (-1 — цель не определена в PDF).</summary>
    public int SourcePageIndex { get; }

    public ObservableCollection<BookmarkViewModel> Children { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanNavigate))]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private int _logicalPageIndex = -1;

    /// <summary>
    /// Что произносит экранный диктор. Без него WPF читает имя ТИПА узла:
    /// заголовок закладки лежит в шаблоне, а не в тексте элемента дерева.
    /// </summary>
    public string AccessibleName => LogicalPageIndex >= 0
        ? Localization.Loc.F("A11yBookmarkItem", Title, LogicalPageIndex + 1)
        : Localization.Loc.F("A11yBookmarkHeading", Title);

    /// <summary>Узел ведёт на существующую страницу; иначе он только заголовок раздела.</summary>
    public bool CanNavigate => LogicalPageIndex >= 0;

    public string PageLabel => LogicalPageIndex >= 0 ? (LogicalPageIndex + 1).ToString() : "";

    /// <summary>Верхние узлы раскрыты, вложенные — свёрнуты: оглавление не должно занимать весь экран.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Пересчёт целевых страниц после изменения состава документа.</summary>
    public void Remap(Func<int, int> mapSourcePage)
    {
        LogicalPageIndex = SourcePageIndex >= 0 ? mapSourcePage(SourcePageIndex) : -1;
        foreach (var child in Children)
            child.Remap(mapSourcePage);
    }
}
