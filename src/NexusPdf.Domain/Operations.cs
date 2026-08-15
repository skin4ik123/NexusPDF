namespace NexusPdf.Domain;

/// <summary>Обратимая операция над логической структурой документа.</summary>
public interface IDocumentOperation
{
    /// <summary>Название для журнала операций и меню «Отменить …».</summary>
    string Name { get; }

    void Apply(DocumentModel model);
    void Revert(DocumentModel model);
}

/// <summary>
/// База: перед применением снимает снимок порядка страниц, чем гарантирует
/// байт-в-байт точный откат независимо от сложности операции.
/// Список PageRef — лёгкие ссылки, поэтому снимок дёшев даже на тысячах страниц.
/// </summary>
public abstract class DocumentOperationBase : IDocumentOperation
{
    private List<PageRef>? _before;

    public abstract string Name { get; }

    public void Apply(DocumentModel model)
    {
        _before = new List<PageRef>(model.Pages);
        ApplyCore(model);
    }

    public void Revert(DocumentModel model)
    {
        if (_before is null)
            throw new InvalidOperationException("Revert вызван до Apply.");
        model.Pages.Clear();
        model.Pages.AddRange(_before);
    }

    protected abstract void ApplyCore(DocumentModel model);

    protected static int[] ValidateIndices(DocumentModel model, IReadOnlyList<int> indices)
    {
        var result = indices.Distinct().OrderBy(i => i).ToArray();
        if (result.Length == 0)
            throw new ArgumentException("Не выбрано ни одной страницы.");
        if (result[0] < 0 || result[^1] >= model.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(indices), "Номер страницы вне документа.");
        return result;
    }
}

public sealed class RotatePagesOperation : DocumentOperationBase
{
    private readonly IReadOnlyList<int> _indices;
    private readonly int _quarterTurns;

    public RotatePagesOperation(IReadOnlyList<int> indices, int quarterTurns)
    {
        _indices = indices;
        _quarterTurns = quarterTurns;
    }

    public override string Name => "Поворот страниц";

    protected override void ApplyCore(DocumentModel model)
    {
        foreach (var i in ValidateIndices(model, _indices))
            model.Pages[i] = model.Pages[i].Rotated(_quarterTurns);
    }
}

public sealed class DeletePagesOperation : DocumentOperationBase
{
    private readonly IReadOnlyList<int> _indices;

    public DeletePagesOperation(IReadOnlyList<int> indices) => _indices = indices;

    public override string Name => "Удаление страниц";

    protected override void ApplyCore(DocumentModel model)
    {
        var sorted = ValidateIndices(model, _indices);
        if (sorted.Length == model.Pages.Count)
            throw new InvalidOperationException("Нельзя удалить все страницы документа.");
        for (var k = sorted.Length - 1; k >= 0; k--)
            model.Pages.RemoveAt(sorted[k]);
    }
}

public sealed class InsertPagesOperation : DocumentOperationBase
{
    private readonly int _insertIndex;
    private readonly IReadOnlyList<PageRef> _pages;

    public InsertPagesOperation(int insertIndex, IReadOnlyList<PageRef> pages)
    {
        _insertIndex = insertIndex;
        _pages = pages;
    }

    public override string Name => "Вставка страниц";

    protected override void ApplyCore(DocumentModel model)
    {
        if (_insertIndex < 0 || _insertIndex > model.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(_insertIndex));
        model.Pages.InsertRange(_insertIndex, _pages);
    }
}

/// <summary>Перемещение выбранных страниц к позиции вставки (позиция считается до изъятия выбранных).</summary>
public sealed class MovePagesOperation : DocumentOperationBase
{
    private readonly IReadOnlyList<int> _indices;
    private readonly int _insertIndex;

    public MovePagesOperation(IReadOnlyList<int> indices, int insertIndex)
    {
        _indices = indices;
        _insertIndex = insertIndex;
    }

    public override string Name => "Перемещение страниц";

    protected override void ApplyCore(DocumentModel model)
    {
        var sorted = ValidateIndices(model, _indices);
        if (_insertIndex < 0 || _insertIndex > model.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(_insertIndex));

        var moved = sorted.Select(i => model.Pages[i]).ToList();
        var adjustedInsert = _insertIndex - sorted.Count(i => i < _insertIndex);
        for (var k = sorted.Length - 1; k >= 0; k--)
            model.Pages.RemoveAt(sorted[k]);
        model.Pages.InsertRange(adjustedInsert, moved);
    }
}

/// <summary>Добавление нового контента (текст/изображение) на одну страницу.</summary>
public sealed class AddOverlayOperation : DocumentOperationBase
{
    private readonly int _pageIndex;
    private readonly Pdf.Abstractions.PageOverlay _overlay;

    public AddOverlayOperation(int pageIndex, Pdf.Abstractions.PageOverlay overlay)
    {
        _pageIndex = pageIndex;
        _overlay = overlay;
    }

    public override string Name => "Добавление содержимого";

    protected override void ApplyCore(DocumentModel model)
    {
        ValidateIndices(model, new[] { _pageIndex });
        model.Pages[_pageIndex] = model.Pages[_pageIndex].WithOverlay(_overlay);
    }
}

/// <summary>
/// Замена одного оверлея страницы другим — правка уже добавленного содержимого
/// (например строки распознанного текста) без потери остальных правок.
/// </summary>
public sealed class ReplaceOverlayOperation : DocumentOperationBase
{
    private readonly int _pageIndex;
    private readonly Pdf.Abstractions.PageOverlay _old;
    private readonly Pdf.Abstractions.PageOverlay _new;

    public ReplaceOverlayOperation(
        int pageIndex, Pdf.Abstractions.PageOverlay oldOverlay, Pdf.Abstractions.PageOverlay newOverlay)
    {
        _pageIndex = pageIndex;
        _old = oldOverlay;
        _new = newOverlay;
    }

    public override string Name => "Правка содержимого";

    protected override void ApplyCore(DocumentModel model)
    {
        ValidateIndices(model, new[] { _pageIndex });
        var page = model.Pages[_pageIndex];
        var index = -1;
        for (var i = 0; i < page.OverlayList.Count; i++)
        {
            if (ReferenceEquals(page.OverlayList[i], _old)) { index = i; break; }
        }
        if (index < 0)
            throw new InvalidOperationException("Правимое содержимое больше не найдено на странице.");

        var overlays = page.OverlayList.ToList();
        overlays[index] = _new;
        model.Pages[_pageIndex] = page.WithOverlays(overlays);
    }
}

/// <summary>
/// Перемещение наложенного объекта по порядку отрисовки — «на передний план» и
/// «на задний план». Порядок в списке и есть порядок рисования, поэтому
/// операция просто переставляет элемент.
/// </summary>
public sealed class ReorderOverlayOperation : DocumentOperationBase
{
    private readonly int _pageIndex;
    private readonly int _fromIndex;
    private readonly int _toIndex;

    public ReorderOverlayOperation(int pageIndex, int fromIndex, int toIndex)
    {
        _pageIndex = pageIndex;
        _fromIndex = fromIndex;
        _toIndex = toIndex;
    }

    public override string Name => "Порядок содержимого";

    protected override void ApplyCore(DocumentModel model)
    {
        ValidateIndices(model, new[] { _pageIndex });
        var page = model.Pages[_pageIndex];
        var list = page.OverlayList.ToList();
        if (_fromIndex < 0 || _fromIndex >= list.Count)
            throw new ArgumentOutOfRangeException(nameof(_fromIndex));

        var target = Math.Clamp(_toIndex, 0, list.Count - 1);
        if (target == _fromIndex)
            return;

        var item = list[_fromIndex];
        list.RemoveAt(_fromIndex);
        list.Insert(target, item);
        model.Pages[_pageIndex] = page.WithOverlays(list);
    }
}

/// <summary>Пакетное добавление контента (колонтитулы, номера, водяной знак) — одна операция Undo.</summary>
public sealed class AddOverlaysOperation : DocumentOperationBase
{
    private readonly IReadOnlyList<(int PageIndex, Pdf.Abstractions.PageOverlay Overlay)> _items;
    private readonly string _name;

    public AddOverlaysOperation(
        IReadOnlyList<(int PageIndex, Pdf.Abstractions.PageOverlay Overlay)> items,
        string name = "Оформление страниц")
    {
        _items = items;
        _name = name;
    }

    public override string Name => _name;

    protected override void ApplyCore(DocumentModel model)
    {
        ValidateIndices(model, _items.Select(i => i.PageIndex).Distinct().ToArray());
        foreach (var (pageIndex, overlay) in _items)
            model.Pages[pageIndex] = model.Pages[pageIndex].WithOverlay(overlay);
    }
}

/// <summary>Удаление одного черновика (аннотации/оверлея) со страницы — из панели комментариев.</summary>
public sealed class RemoveOverlayAtOperation : DocumentOperationBase
{
    private readonly int _pageIndex;
    private readonly int _overlayIndex;

    public RemoveOverlayAtOperation(int pageIndex, int overlayIndex)
    {
        _pageIndex = pageIndex;
        _overlayIndex = overlayIndex;
    }

    public override string Name => "Удаление комментария";

    protected override void ApplyCore(DocumentModel model)
    {
        ValidateIndices(model, new[] { _pageIndex });
        var page = model.Pages[_pageIndex];
        if (_overlayIndex < 0 || _overlayIndex >= page.OverlayList.Count)
            throw new ArgumentOutOfRangeException(nameof(_overlayIndex));
        var list = new List<Pdf.Abstractions.PageOverlay>(page.OverlayList);
        list.RemoveAt(_overlayIndex);
        model.Pages[_pageIndex] = page with { Overlays = list.Count == 0 ? null : list };
    }
}

/// <summary>
/// Пометка СУЩЕСТВУЮЩЕЙ аннотации исходного файла к удалению при сохранении.
/// Исходный файл не мутируется: индекс аннотации исходной страницы стабилен
/// до сохранения, Ctrl+Z снимает пометку.
/// </summary>
public sealed class RemoveExistingAnnotationOperation : DocumentOperationBase
{
    private readonly int _pageIndex;
    private readonly int _annotIndex;

    public RemoveExistingAnnotationOperation(int pageIndex, int annotIndex)
    {
        _pageIndex = pageIndex;
        _annotIndex = annotIndex;
    }

    public override string Name => "Удаление аннотации";

    protected override void ApplyCore(DocumentModel model)
    {
        ValidateIndices(model, new[] { _pageIndex });
        if (_annotIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(_annotIndex));
        model.Pages[_pageIndex] = model.Pages[_pageIndex].WithRemovedAnnotation(_annotIndex);
    }
}

/// <summary>Удаление всего наложенного контента с выбранных страниц.</summary>
public sealed class RemoveOverlaysOperation : DocumentOperationBase
{
    private readonly IReadOnlyList<int> _indices;

    public RemoveOverlaysOperation(IReadOnlyList<int> indices) => _indices = indices;

    public override string Name => "Удаление наложенного содержимого";

    protected override void ApplyCore(DocumentModel model)
    {
        foreach (var i in ValidateIndices(model, _indices))
            model.Pages[i] = model.Pages[i].WithoutOverlays();
    }
}

public sealed class DuplicatePagesOperation : DocumentOperationBase
{
    private readonly IReadOnlyList<int> _indices;

    public DuplicatePagesOperation(IReadOnlyList<int> indices) => _indices = indices;

    public override string Name => "Дублирование страниц";

    protected override void ApplyCore(DocumentModel model)
    {
        var sorted = ValidateIndices(model, _indices);
        // Вставляем копию сразу после каждой исходной страницы, идя с конца.
        for (var k = sorted.Length - 1; k >= 0; k--)
            model.Pages.Insert(sorted[k] + 1, model.Pages[sorted[k]]);
    }
}
