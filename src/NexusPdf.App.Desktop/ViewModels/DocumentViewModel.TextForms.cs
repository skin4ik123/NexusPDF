using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.Application;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Abstractions.TextBlocks;

namespace NexusPdf.App.Desktop.ViewModels;

/// <summary>Что подсвечено под курсором инструмента «Текст».</summary>
public enum TextHoverKind { None, Block, Paragraph, Cell, Line }

/// <summary>
/// Инструмент «Текст» в бланке: щелчок в пустую клетку — блок ровно в
/// клетке, над строкой-подчёркиванием или пропуском «____» — текст на линии;
/// Tab — следующая клетка той же таблицы; отметки ✓ ✗ ● — по центру места.
/// Клетки и линии ищутся по описанию страницы, прочитанному один раз.
/// </summary>
public sealed partial class DocumentViewModel
{
    /// <summary>Описание страницы, места бланка и абзацы чужого текста (без наших целых блоков).</summary>
    private sealed record PageText(PageTextLayout Layout, FormTargetIndex Forms, IReadOnlyList<DetectedParagraph> Paragraphs);

    private readonly Dictionary<(IPdfDocumentHandle Handle, int Page, int Turns), Task<PageText?>> _pageTexts = new();

    /// <summary>Место бланка, в котором идёт ввод: от него Tab ищет следующую клетку.</summary>
    private FormTarget? _activeFormTarget;

    private PageViewModel? _hoverPage;

    /// <summary>Отметка, которая встанет следующим щелчком инструмента; null — обычный ввод.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArmedCheck))]
    [NotifyPropertyChangedFor(nameof(ArmedCross))]
    [NotifyPropertyChangedFor(nameof(ArmedDot))]
    private string? _armedTextMark;

    public bool ArmedCheck => ArmedTextMark == TextMarks[0];
    public bool ArmedCross => ArmedTextMark == TextMarks[1];
    public bool ArmedDot => ArmedTextMark == TextMarks[2];

    public string TextMarkCheckTip => MarkTip(TextMarks[0], "TextMarkCheck");
    public string TextMarkCrossTip => MarkTip(TextMarks[1], "TextMarkCross");
    public string TextMarkDotTip => MarkTip(TextMarks[2], "TextMarkDot");

    private string MarkTip(string mark, string nameKey) =>
        AvailableTextMarks.Contains(mark) ? Loc.Get(nameKey) : Loc.Get(nameKey) + " — " + Loc.Get("TextMarkNoFont");

    /// <summary>Есть ли шрифт для отметки — иначе её кнопка выключена (с причиной в подсказке).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ArmTextMarkCommand))]
    [NotifyCanExecuteChangedFor(nameof(InsertTextMarkCommand))]
    [NotifyPropertyChangedFor(nameof(TextMarkCheckTip))]
    [NotifyPropertyChangedFor(nameof(TextMarkCrossTip))]
    [NotifyPropertyChangedFor(nameof(TextMarkDotTip))]
    private IReadOnlySet<string> _availableTextMarks = new HashSet<string>();

    public static readonly IReadOnlyList<string> TextMarks = ["✓", "✗", "●"];

    /// <summary>Прочитать описание страницы (текст, линии, клетки, абзацы) — один раз на страницу и поворот.</summary>
    public Task EnsurePageTextAsync(PageViewModel page) => PageTextAsync(page);

    private Task<PageText?> PageTextAsync(PageViewModel page)
    {
        if (!Document.Handles.TryGetValue(page.PageRef.SourceId, out var handle))
            return Task.FromResult<PageText?>(null);
        var key = (handle, page.PageRef.SourcePageIndex, page.PageRef.RotationOffset);
        if (!_pageTexts.TryGetValue(key, out var task))
            _pageTexts[key] = task = ReadPageTextAsync(page, handle);
        return task;
    }

    private async Task<PageText?> ReadPageTextAsync(PageViewModel page, IPdfDocumentHandle handle)
    {
        try
        {
            var layout = await handle.GetPageTextLayoutAsync(
                page.PageRef.SourcePageIndex, page.PageRef.RotationOffset, CancellationToken.None);
            await EnsureStoredTextBlocksAsync(page);
            await EnsureBlockFontsAsync();
            // Объекты наших целых блоков правятся как блоки, абзацами их не собирать.
            var own = StoredBlocksOf(page).Where(b => b.Block != null)
                .Select(b => (TextBlockOverlay)ToDisplayFrame(b.Block!, page)).ToList();
            var foreign = layout with
            {
                Objects = layout.Objects.Where(o => !own.Any(b => FrameContains(b,
                    o.BoundsPt.XPt + o.BoundsPt.WidthPt / 2, o.BoundsPt.YPt + o.BoundsPt.HeightPt / 2, 1))).ToList(),
            };
            var size = Defaults().Style.FontSizePt;
            return await Task.Run(() =>
            {
                var forms = FormTargetIndex.Build(layout, size);
                return new PageText(layout, forms, ParagraphBuilder.Build(foreign, forms, BlockFonts));
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Без описания страницы остаются блоки и правка строки — а не
            // страница, на которой не открывается ничего.
            Serilog.Log.Warning(ex, "Описание страницы {Page} не прочиталось", page.LogicalIndex + 1);
            return null;
        }
    }

    /// <summary>Описание страницы, если оно уже прочитано (для наведения — без ожидания).</summary>
    private PageText? PageTextOf(PageViewModel page) =>
        Document.Handles.TryGetValue(page.PageRef.SourceId, out var handle) &&
        _pageTexts.TryGetValue((handle, page.PageRef.SourcePageIndex, page.PageRef.RotationOffset), out var task) &&
        task.IsCompletedSuccessfully
            ? task.Result
            : null;

    /// <summary>
    /// Абзац чужого текста под точкой — наименьший из накрывающих её. Абзацы,
    /// замена которых уже ждёт записи, правятся своей заменой.
    /// </summary>
    private DetectedParagraph? ParagraphAt(PageViewModel page, double xPt, double yPt)
    {
        if (PageTextOf(page) is not { } text) return null;
        var pending = page.PageRef.OverlayList.OfType<TextParagraphReplacement>()
            .SelectMany(r => r.Objects).Select(o => o.Fingerprint).ToHashSet(StringComparer.Ordinal);
        // Строка, правка которой уже ждёт записи, абзацем не берётся: две правки
        // одного объекта противоречили бы друг другу.
        var lineEdits = page.PageRef.OverlayList.OfType<TextObjectReplacement>().Select(r => r.ObjectPath).ToList();
        return text.Paragraphs
            .Where(p => p.BoundsPt.Contains(xPt, yPt) || Near(p.BoundsPt, xPt, yPt, 1))
            .Where(p => !p.Objects.Any(o => pending.Contains(o.Fingerprint)))
            .Where(p => !p.Objects.Any(o => lineEdits.Any(path => path.SequenceEqual(o.Path))))
            .OrderBy(p => p.BoundsPt.WidthPt * p.BoundsPt.HeightPt)
            .FirstOrDefault();
    }

    /// <summary>
    /// Лежит ли место под вымаркой, ждущей записи. Такой абзац или блок не
    /// открывается: его текст содержит вымаранные слова, а после переноса строк
    /// они вышли бы из-под чёрного.
    /// </summary>
    private bool UnderRedaction(PageViewModel page, OverlayBox box) =>
        page.PageRef.OverlayList.OfType<RedactionDraft>()
            .Select(r => (RedactionDraft)ToDisplayFrame(r, page))
            .Any(r => r.XPt < box.XPt + box.WidthPt && box.XPt < r.XPt + r.WidthPt &&
                      r.YPt < box.YPt + box.HeightPt && box.YPt < r.YPt + r.HeightPt);

    /// <summary>Описанная рамка блока на странице (у повёрнутого — по углам).</summary>
    private static OverlayBox Footprint(TextBlockOverlay block)
    {
        if (block.RotationDegrees == 0) return Frame(block);
        var a = block.RotationDegrees * Math.PI / 180;
        var (cx, cy) = (block.XPt + block.WidthPt / 2, block.YPt + block.HeightPt / 2);
        var (hw, hh) = (block.WidthPt / 2, block.HeightPt / 2);
        var xs = new[] { -1.0, 1.0 }.SelectMany(sx => new[] { -1.0, 1.0 }.Select(sy => cx + sx * hw * Math.Cos(a) - sy * hh * Math.Sin(a))).ToList();
        var ys = new[] { -1.0, 1.0 }.SelectMany(sx => new[] { -1.0, 1.0 }.Select(sy => cy + sx * hw * Math.Sin(a) + sy * hh * Math.Cos(a))).ToList();
        return new OverlayBox(xs.Min(), ys.Min(), xs.Max() - xs.Min(), ys.Max() - ys.Min());
    }

    private static bool Near(OverlayBox box, double x, double y, double margin) =>
        x >= box.XPt - margin && x <= box.XPt + box.WidthPt + margin &&
        y >= box.YPt - margin && y <= box.YPt + box.HeightPt + margin;

    /// <summary>Место бланка под точкой (отображаемые пункты) или null, если страница ещё не прочитана.</summary>
    private FormTarget? FormTargetAt(PageViewModel page, double xPt, double yPt) =>
        PageTextOf(page)?.Forms.At(xPt, yPt);

    // ----- Наведение -----

    /// <summary>
    /// Подсветить цель под курсором — по порядку: блок, клетка, строка. Без
    /// вызовов движка: страница читается в фоне, до этого подсветки нет.
    /// </summary>
    public void UpdateTextHover(PageViewModel page, double xPt, double yPt)
    {
        _ = EnsurePageTextAsync(page);
        var hover = IsTextTool && ActiveTextEdit == null ? TextHoverAt(page, xPt, yPt) : null;
        if (_hoverPage != null && !ReferenceEquals(_hoverPage, page))
            _hoverPage.SetTextHover(TextHoverKind.None, null, 0);
        _hoverPage = page;
        if (hover is { } h)
            page.SetTextHover(h.Kind, h.Rect, h.Angle);
        else
            page.SetTextHover(TextHoverKind.None, null, 0);
    }

    public void ClearTextHover()
    {
        _hoverPage?.SetTextHover(TextHoverKind.None, null, 0);
        _hoverPage = null;
    }

    private (TextHoverKind Kind, OverlayBox Rect, double Angle)? TextHoverAt(PageViewModel page, double xPt, double yPt)
    {
        var tolerance = 2 / Math.Max(Zoom, 0.1);
        if (BlockAt(page, xPt, yPt, tolerance) is { } block)
            return (TextHoverKind.Block, Frame(block), block.RotationDegrees);
        // Пропуск «____» внутри строки — место для ввода, а не правка этой строки.
        var target = FormTargetAt(page, xPt, yPt);
        if (target is not { Kind: FormTargetKind.TextBlank } && ParagraphAt(page, xPt, yPt) is { } paragraph)
            return (TextHoverKind.Paragraph, paragraph.BoundsPt, 0);
        if (target == null)
            return null;
        // Клетка, в которой уже есть блок, правится этим блоком.
        if (BlockInside(page, target.CellPt) is { } filled)
            return (TextHoverKind.Block, Frame(filled), filled.RotationDegrees);
        return (target.Kind is FormTargetKind.Cell or FormTargetKind.CaptionCell ? TextHoverKind.Cell : TextHoverKind.Line,
            target.AreaPt, 0);
    }

    private static OverlayBox Frame(TextBlockOverlay block) => new(block.XPt, block.YPt, block.WidthPt, block.HeightPt);

    /// <summary>Блок страницы (черновик или из файла) под точкой, в рамке показа.</summary>
    private TextBlockOverlay? BlockAt(PageViewModel page, double xPt, double yPt, double tolerance) =>
        DisplayedBlocks(page).FirstOrDefault(b => FrameContains(b, xPt, yPt, tolerance));

    /// <summary>Блок, центр которого лежит в клетке.</summary>
    private TextBlockOverlay? BlockInside(PageViewModel page, OverlayBox cell) =>
        DisplayedBlocks(page).FirstOrDefault(b => cell.Contains(b.XPt + b.WidthPt / 2, b.YPt + b.HeightPt / 2));

    /// <summary>Черновики (сверху — последний) и целые блоки из файла, ещё не заменённые.</summary>
    private IEnumerable<TextBlockOverlay> DisplayedBlocks(PageViewModel page)
    {
        var overlays = page.PageRef.OverlayList;
        for (var i = overlays.Count - 1; i >= 0; i--)
        {
            switch (ToDisplayFrame(overlays[i], page))
            {
                case TextBlockOverlay draft:
                    yield return draft;
                    break;
                case TextBlockReplacement { NewBlock: { } pending }:
                    yield return pending;
                    break;
                case TextParagraphReplacement { NewBlock: { } paragraph }:
                    yield return paragraph;
                    break;
            }
        }
        foreach (var stored in StoredBlocksOf(page))
        {
            if (stored.Block is { } block && !HasPendingReplacement(page, stored.Id))
                yield return (TextBlockOverlay)ToDisplayFrame(block, page);
        }
    }

    // ----- Щелчок инструментом -----

    /// <summary>
    /// Щелчок инструментом «Текст»: отметка, если она выбрана; иначе блок под
    /// точкой; иначе место бланка (клетка, строка, пропуск); иначе надпись.
    /// </summary>
    public async Task TextToolClickAsync(PageViewModel page, double xPt, double yPt)
    {
        ClearTextHover();
        if (ArmedTextMark is { Length: 1 } mark)
        {
            await PlaceTextMarkAsync(page, xPt, yPt, mark[0]);
            return;
        }
        await EnsurePageTextAsync(page);
        var target = FormTargetAt(page, xPt, yPt);
        if (target is not { Kind: FormTargetKind.TextBlank })
        {
            switch (await EditAtAsync(page, xPt, yPt))
            {
                case EditResult.Started:
                case EditResult.Blocked:
                    return;
                case EditResult.Refused:
                    // Абзац взять нельзя — нынешняя правка строки; почему — в строке состояния.
                    RequestInlineEdit(page, xPt, yPt, isExplicit: true);
                    return;
            }
        }
        if (target != null)
        {
            if (BlockInside(page, target.CellPt) is { } filled)
            {
                await EditTextBlockAsync(page, filled.XPt + filled.WidthPt / 2, filled.YPt + filled.HeightPt / 2);
                return;
            }
            await BeginTextBlockInTargetAsync(page, target);
            return;
        }
        await BeginTextBlockAtAsync(page, xPt, yPt);
    }

    /// <summary>Новый блок в клетке, на строке или в пропуске бланка.</summary>
    public async Task BeginTextBlockInTargetAsync(PageViewModel page, FormTarget target)
    {
        await EnsureBlockFontsAsync();
        var (style, align, spacing) = Defaults();
        var block = FormFill.BlockFor(target, style, align, spacing) with { PlacedRotation = page.PageRef.RotationOffset };
        StartTextEdit(page, block, null, null, coversOriginal: false);
        _activeFormTarget = target;
    }

    /// <summary>
    /// Tab в блоке-клетке: ввод закончен, новый блок — в следующей (Shift —
    /// в предыдущей) пустой клетке той же таблицы; клетка, где блок уже есть,
    /// открывается для правки. false — следующей клетки нет: Tab — обычный.
    /// </summary>
    public async Task<bool> MoveToNextCellAsync(bool backwards)
    {
        if (ActiveTextEdit is not { } edit || _activeFormTarget is not { } from ||
            PageTextOf(edit.Page)?.Forms is not { } forms ||
            forms.Next(from, backwards) is not { } next)
            return false;
        var page = edit.Page;
        CommitTextBlock();
        if (BlockInside(page, next.CellPt) is { } filled &&
            await EditTextBlockAsync(page, filled.XPt + filled.WidthPt / 2, filled.YPt + filled.HeightPt / 2))
        {
            _activeFormTarget = next;
            return true;
        }
        await BeginTextBlockInTargetAsync(page, next);
        return true;
    }

    // ----- Отметки ✓ ✗ ● -----

    /// <summary>Какие отметки можно нарисовать хоть каким-то шрифтом (каталог сканируется в фоне).</summary>
    private async Task RefreshTextMarksAsync()
    {
        var style = Defaults().Style;
        var available = await Task.Run(() =>
            TextMarks.Where(m => FormFill.MarkStyle(BlockFonts, style, m[0]) != null).ToHashSet());
        AvailableTextMarks = available;
    }

    private bool CanUseTextMark(string? mark) => mark != null && AvailableTextMarks.Contains(mark);

    /// <summary>Кнопка отметки рядом с инструментом: следующий щелчок ставит её (повторное нажатие — отмена).</summary>
    [RelayCommand(CanExecute = nameof(CanUseTextMark))]
    private void ArmTextMark(string? mark)
    {
        if (mark == null) return;
        if (!IsTextTool) ToggleTextTool();
        CommitTextBlock();
        ArmedTextMark = ArmedTextMark == mark ? null : mark;
        StatusText = Loc.Get(ArmedTextMark == null ? "TextToolHint" : "TextMarkHint");
    }

    /// <summary>Кнопка отметки в панели над блоком: знак — в текст у курсора.</summary>
    [RelayCommand(CanExecute = nameof(CanUseTextMark))]
    private void InsertTextMark(string? mark)
    {
        if (mark is not { Length: 1 } || ActiveTextBlock is not { } model) return;
        var caret = model.StyleAtCaret;
        if (FormFill.MarkStyle(BlockFonts, caret, mark[0]) is not { } style)
        {
            StatusText = Loc.Get("TextMarkNoFont");
            return;
        }
        if (style == caret)
        {
            model.Insert(mark);
        }
        else
        {
            // Знак — своим шрифтом, дальше набор продолжается прежним.
            model.ApplyStyle(_ => style);
            model.Insert(mark);
            model.ApplyStyle(_ => caret);
        }
        TextEditorFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task PlaceTextMarkAsync(PageViewModel page, double xPt, double yPt, char mark)
    {
        await EnsureBlockFontsAsync();
        await EnsurePageTextAsync(page);
        ArmedTextMark = null;
        if (FormFill.MarkStyle(BlockFonts, Defaults().Style, mark) is not { } style)
        {
            StatusText = Loc.Get("TextMarkNoFont");
            return;
        }
        var target = FormTargetAt(page, xPt, yPt);
        var draft = FormFill.MarkFor(target, xPt, yPt, mark, style);
        // Рамка по вёрстке — та же, что дал бы редактор (у надписи ширина по тексту).
        var block = new TextBlockEditorModel(draft, BlockFonts, style).Block with
        {
            PlacedRotation = page.PageRef.RotationOffset,
        };
        TextBlockSessionService.Commit(Document.Session, page.LogicalIndex, null, null, block);
        StatusText = Loc.Get("TextToolHint");
    }
}
