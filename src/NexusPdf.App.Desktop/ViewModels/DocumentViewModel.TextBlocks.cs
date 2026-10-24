using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.Application;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Abstractions.TextBlocks;

namespace NexusPdf.App.Desktop.ViewModels;

/// <summary>
/// Инструмент «Текст»: блоки создаются щелчком (растут вправо) или рамкой
/// (перенос по словам), правятся на странице и после сохранения — двойным
/// щелчком. Каждое завершение ввода — один шаг отмены документа.
/// </summary>
public sealed partial class DocumentViewModel
{
    /// <summary>Блок в работе: где он, с чего начинался и чем станет при завершении.</summary>
    /// <param name="Original">Правленый черновик (объект из списка страницы); null — новый или сохранённый блок.</param>
    /// <param name="StoredId">Id блока из файла; null — блок ещё не сохранялся.</param>
    /// <param name="StartBlock">Блок в начале ввода (в рамке текущего показа) — для «ничего не менялось».</param>
    /// <param name="CoversOriginal">Под редактором нарисован прежний вид блока, и его надо закрыть.</param>
    /// <param name="ParagraphObjects">Правится абзац существующего текста: его объекты снимет запись.</param>
    /// <param name="FontNote">Строка о замене шрифта документа, которого нет в системе.</param>
    /// <param name="CoverPt">Сколько закрыть подложкой (пункты от угла рамки); null — рамку блока.</param>
    public sealed record TextBlockEdit(
        PageViewModel Page, TextBlockEditorModel Model, TextBlockOverlay? Original, Guid? StoredId,
        TextBlockOverlay StartBlock, bool CoversOriginal,
        IReadOnlyList<ParagraphObjectRef>? ParagraphObjects = null, string? FontNote = null,
        (double Width, double Height)? CoverPt = null);

    /// <summary>
    /// Чем кончилась попытка открыть для правки то, что под точкой: Refused —
    /// абзац не берётся, дальше правка строки; Blocked — не править ничем
    /// (под вымаркой), причина в строке состояния.
    /// </summary>
    private enum EditResult { None, Started, Refused, Blocked }

    [ObservableProperty]
    private bool _isTextTool;

    /// <summary>Оформление нового блока (из настроек). Ставит MainViewModel.</summary>
    public Func<(TextStyle Style, TextAlign Align, double LineSpacing)>? TextDefaults { get; set; }

    /// <summary>Запомнить оформление последнего блока как умолчание. Ставит MainViewModel.</summary>
    public Action<TextStyle, TextAlign, double>? RememberTextDefaults { get; set; }

    public TextBlockEdit? ActiveTextEdit { get; private set; }

    public TextBlockEditorModel? ActiveTextBlock => ActiveTextEdit?.Model;

    /// <summary>Редактор надо поставить на страницу — это делает представление.</summary>
    public event EventHandler<TextBlockEdit>? TextBlockEditStarted;

    /// <summary>Ввод закончен — редактор убирается со страницы.</summary>
    public event EventHandler? TextBlockEditEnded;

    /// <summary>Шрифты блоков — те же, которыми их пишет движок.</summary>
    private static IFontProvider BlockFonts => FontCatalog.System;

    /// <summary>
    /// Каталог строится в фоне: первое сканирование шрифтов системы заметно по
    /// времени и не должно замораживать окно на щелчке.
    /// </summary>
    private static Task EnsureBlockFontsAsync() => Task.Run(() => _ = FontCatalog.System.Families);

    public void ToggleTextTool()
    {
        if (IsTextTool)
        {
            CommitTextBlock();
            ArmedTextMark = null;
            ClearTextHover();
            IsTextTool = false;
            StatusText = Loc.Get("Ready");
            return;
        }
        if (IsBusy) return;
        CancelPlacement();
        StopMeasurement();
        if (IsDrawing)
            SelectDrawTool(DrawTool.None);
        if (IsFormMode)
            _ = ExitFormModeAsync();
        ClearObjectSelection();
        ClearTextSelection();
        IsTextTool = true;
        StatusText = Loc.Get("TextToolHint");
        // Список шрифтов сканируется заранее, пока человек выбирает место;
        // заодно — для каких отметок ✓ ✗ ● есть шрифт.
        _ = RefreshTextMarksAsync();
    }

    /// <summary>Щелчок инструментом: блок без переноса, растёт вправо при наборе.</summary>
    public async Task BeginTextBlockAtAsync(PageViewModel page, double xPt, double yPt)
    {
        await EnsureBlockFontsAsync();
        var (style, align, spacing) = Defaults();
        var lineHeight = style.FontSizePt * 1.2;
        var block = NewBlock(
            xPt - TextBlockLayout.PaddingPt, yPt - TextBlockLayout.PaddingPt - lineHeight / 2,
            2 * TextBlockLayout.PaddingPt + 1, 2 * TextBlockLayout.PaddingPt + lineHeight,
            TextBlockWidthMode.AutoWidth, style, align, spacing, page);
        StartTextEdit(page, block, null, null, coversOriginal: false);
    }

    /// <summary>Протянутая рамка: ширина задана, текст переносится по словам, высота растёт сама.</summary>
    public async Task BeginTextBlockInRectAsync(PageViewModel page, OverlayBox rect)
    {
        await EnsureBlockFontsAsync();
        var (style, align, spacing) = Defaults();
        var box = OverlayGeometry.Normalize(rect);
        var block = NewBlock(box.XPt, box.YPt, box.WidthPt, box.HeightPt,
            TextBlockWidthMode.Fixed, style, align, spacing, page);
        StartTextEdit(page, block, null, null, coversOriginal: false);
    }

    /// <summary>
    /// Снова писать в блоке под точкой: черновик этой сессии или блок из файла.
    /// false — блока здесь нет (или он изменён другой программой — тогда
    /// объяснение уже в строке состояния).
    /// </summary>
    public async Task<bool> EditTextBlockAsync(PageViewModel page, double xPt, double yPt) =>
        await EditAtAsync(page, xPt, yPt) is EditResult.Started or EditResult.Blocked;

    /// <summary>
    /// Блок под точкой (черновик, замена, блок из файла), иначе абзац
    /// существующего текста. Абзац, который взять нельзя, — Refused, причина
    /// уже в строке состояния.
    /// </summary>
    private async Task<EditResult> EditAtAsync(PageViewModel page, double xPt, double yPt)
    {
        if (IsBusy) return EditResult.None;
        await EnsureBlockFontsAsync();
        var tolerance = 2 / Math.Max(Zoom, 0.1);

        var overlays = page.PageRef.OverlayList;
        for (var i = overlays.Count - 1; i >= 0; i--)
        {
            switch (ToDisplayFrame(overlays[i], page))
            {
                case TextBlockOverlay draft when FrameContains(draft, xPt, yPt, tolerance):
                    StartTextEdit(page, draft, (TextBlockOverlay)overlays[i], null, coversOriginal: true, xPt, yPt);
                    return EditResult.Started;
                case TextBlockReplacement { NewBlock: { } pending } replacement when FrameContains(pending, xPt, yPt, tolerance):
                    if (Blocked(page, pending)) return EditResult.Blocked;
                    StartTextEdit(page, pending, null, replacement.BlockId, coversOriginal: true, xPt, yPt);
                    return EditResult.Started;
                case TextParagraphReplacement { NewBlock: { } pendingParagraph } paragraphReplacement
                    when FrameContains(pendingParagraph, xPt, yPt, tolerance):
                    if (Blocked(page, pendingParagraph)) return EditResult.Blocked;
                    StartTextEdit(page, pendingParagraph, null, null, coversOriginal: true, xPt, yPt,
                        paragraphReplacement.Objects);
                    return EditResult.Started;
            }
        }

        await EnsureStoredTextBlocksAsync(page);
        foreach (var stored in StoredBlocksOf(page))
        {
            if (stored.Block is not { } block)
            {
                // Блок, который поменяла другая программа, правится строкой —
                // но человек должен знать, почему не открылся редактор блока.
                if (Contains(stored.BoundsPt, xPt, yPt))
                    StatusText = Loc.Get("TextBlockChangedElsewhere");
                continue;
            }
            if (HasPendingReplacement(page, stored.Id)) continue;
            var displayed = (TextBlockOverlay)ToDisplayFrame(block, page);
            if (!FrameContains(displayed, xPt, yPt, tolerance)) continue;
            if (Blocked(page, displayed)) return EditResult.Blocked;
            StartTextEdit(page, displayed, null, stored.Id, coversOriginal: true, xPt, yPt);
            return EditResult.Started;
        }

        await EnsurePageTextAsync(page);
        if (ParagraphAt(page, xPt, yPt) is not { } paragraph)
            return EditResult.None;
        if (paragraph.Block is not { } paragraphBlock)
        {
            StatusText = Loc.Get(paragraph.RefusedKey ?? "TextParagraphOverlap");
            return EditResult.Refused;
        }
        if (UnderRedaction(page, paragraph.BoundsPt) || Blocked(page, paragraphBlock))
            return EditResult.Blocked;
        // Абзац становится блоком в памяти; документ изменится, только если
        // что-то поменяли. Подложка закрывает прежний вид целиком.
        var taken = paragraphBlock with { PlacedRotation = page.PageRef.RotationOffset };
        var bounds = paragraph.BoundsPt;
        (double, double)? cover = taken.RotationDegrees == 0
            ? (Math.Max(taken.WidthPt, bounds.XPt + bounds.WidthPt - taken.XPt + 1),
               Math.Max(taken.HeightPt, bounds.YPt + bounds.HeightPt - taken.YPt + 1))
            : null;
        var note = paragraph.SubstitutedFonts.Count == 0
            ? null
            : string.Join("; ", paragraph.SubstitutedFonts.Select(f =>
                string.Format(CultureInfo.CurrentCulture, Loc.Get("TextFontSubstituted"), f.Document, f.Written)));
        StartTextEdit(page, taken, null, null, coversOriginal: true, xPt, yPt,
            paragraph.Objects.Select(o => new ParagraphObjectRef(o.Path, o.Fingerprint))
                .Concat(paragraph.Decorations.Select(d => new ParagraphObjectRef(d.Path, d.Fingerprint))).ToList(),
            note, cover);
        return EditResult.Started;
    }

    /// <summary>Закончить ввод: набранное уходит в документ одним шагом отмены.</summary>
    public void CommitTextBlock()
    {
        if (ActiveTextEdit is not { } edit) return;
        ActiveTextEdit = null;
        _activeFormTarget = null;
        edit.Model.Changed -= OnActiveTextChanged;
        OnPropertyChanged(nameof(ActiveTextBlock));
        OnPropertyChanged(nameof(ShowsTextBlockProperties));
        OnPropertyChanged(nameof(ShowsPageProperties));
        TextBlockEditEnded?.Invoke(this, EventArgs.Empty);

        OnPropertyChanged(nameof(TextFontNote));
        OnPropertyChanged(nameof(HasTextFontNote));
        var edited = edit.Model.Block with { PlacedRotation = edit.Page.PageRef.RotationOffset };
        if (!TextBlockSessionService.SameContent(edit.StartBlock, edited))
        {
            // Оформление по умолчанию меняет только «Сделать оформлением по
            // умолчанию»: иначе правка чужого блока делала бы его стиль своим.
            if (edit.ParagraphObjects is { } objects)
                TextBlockSessionService.CommitParagraph(Document.Session, edit.Page.LogicalIndex, objects, edited);
            else
                TextBlockSessionService.Commit(Document.Session, edit.Page.LogicalIndex, edit.Original, edit.StoredId, edited);
        }
        if (StatusText == Loc.Get("TextBlockTyping"))
            StatusText = IsTextTool ? Loc.Get("TextToolHint") : Loc.Get("Ready");
    }

    private void StartTextEdit(
        PageViewModel page, TextBlockOverlay block, TextBlockOverlay? original, Guid? storedId,
        bool coversOriginal, double? xPt = null, double? yPt = null,
        IReadOnlyList<ParagraphObjectRef>? paragraphObjects = null, string? fontNote = null,
        (double Width, double Height)? cover = null)
    {
        CommitTextBlock();
        // Завершённый ввод меняет страницу, и её модель в списке заменяется
        // новой: редактор ставится на ту, что сейчас на экране.
        page = CurrentPage(page);
        ClearObjectSelection();
        ClearTextSelection();
        var (style, _, _) = Defaults();
        var model = new TextBlockEditorModel(block, BlockFonts, block.Paragraphs.FirstOrDefault()?.Runs.FirstOrDefault()?.Style ?? style);
        if (xPt is { } x && yPt is { } y)
        {
            var (lx, ly) = ToBlockLocal(block, x, y);
            model.PlaceCaret(lx, ly, extend: false);
        }
        else
        {
            model.MoveCaret(CaretMove.BlockEnd, extend: false);
        }
        var edit = new TextBlockEdit(page, model, original, storedId, model.Block, coversOriginal,
            paragraphObjects, fontNote, cover);
        ActiveTextEdit = edit;
        model.Changed += OnActiveTextChanged;
        OnPropertyChanged(nameof(ActiveTextBlock));
        OnPropertyChanged(nameof(ShowsTextBlockProperties));
        OnPropertyChanged(nameof(ShowsPageProperties));
        OnPropertyChanged(nameof(TextFontNote));
        OnPropertyChanged(nameof(HasTextFontNote));
        RefreshTextStyle();
        // Блок открыт двойным щелчком без инструмента — кнопкам отметок в
        // панели тоже нужно знать, для каких знаков есть шрифт.
        if (AvailableTextMarks.Count == 0)
            _ = RefreshTextMarksAsync();
        StatusText = Loc.Get("TextBlockTyping");
        TextBlockEditStarted?.Invoke(this, edit);
    }

    /// <summary>Блок под вымаркой, ждущей записи, не открывается — почему, сказано в строке состояния.</summary>
    private bool Blocked(PageViewModel page, TextBlockOverlay block)
    {
        if (!UnderRedaction(page, Footprint(block))) return false;
        StatusText = Loc.Get("TextUnderRedaction");
        return true;
    }

    /// <summary>Модель страницы, которая сейчас в списке (после правки прежняя заменяется).</summary>
    private PageViewModel CurrentPage(PageViewModel page) =>
        Pages.FirstOrDefault(p => p.LogicalIndex == page.LogicalIndex) ?? page;

    private (TextStyle Style, TextAlign Align, double LineSpacing) Defaults() =>
        TextDefaults?.Invoke() ?? (new TextStyle(FontCatalog.DefaultFamily, 11), TextAlign.Left, 1.15);

    private static TextBlockOverlay NewBlock(
        double x, double y, double width, double height, TextBlockWidthMode mode,
        TextStyle style, TextAlign align, double spacing, PageViewModel page) =>
        new(x, y, width, height, 0, mode, AutoHeight: true, ShrinkToFit: false,
            [new TextParagraph([new TextRun("", style)], align, spacing)], Guid.NewGuid())
        {
            PlacedRotation = page.PageRef.RotationOffset,
        };

    // ----- Геометрия повёрнутой рамки -----

    /// <summary>Точка страницы в осях блока (пункты от левого верхнего угла рамки до поворота).</summary>
    internal static (double X, double Y) ToBlockLocal(TextBlockOverlay block, double xPt, double yPt)
    {
        var (cx, cy) = (block.XPt + block.WidthPt / 2, block.YPt + block.HeightPt / 2);
        var angle = -block.RotationDegrees * Math.PI / 180;
        var (dx, dy) = (xPt - cx, yPt - cy);
        return (cx + dx * Math.Cos(angle) - dy * Math.Sin(angle) - block.XPt,
                cy + dx * Math.Sin(angle) + dy * Math.Cos(angle) - block.YPt);
    }

    internal static bool FrameContains(TextBlockOverlay block, double xPt, double yPt, double tolerance)
    {
        var (lx, ly) = ToBlockLocal(block, xPt, yPt);
        return lx >= -tolerance && ly >= -tolerance &&
               lx <= block.WidthPt + tolerance && ly <= block.HeightPt + tolerance;
    }

    private static bool Contains(OverlayBox box, double x, double y) => box.Contains(x, y);

    // ----- Блоки из файла -----

    // Чтение — задачей: пока оно идёт, второй вызов ждёт его, а не видит
    // пустой список. Иначе двойной щелчок сразу после наведения открывал
    // правку строки вместо блока.
    private readonly Dictionary<(IPdfDocumentHandle Handle, int Page, int Turns), Task<IReadOnlyList<StoredTextBlock>>> _storedBlocks = new();

    /// <summary>Прочитать блоки страницы из файла (один раз на страницу; повторный вызов ждёт того же чтения).</summary>
    public Task EnsureStoredTextBlocksAsync(PageViewModel page)
    {
        if (!Document.Handles.TryGetValue(page.PageRef.SourceId, out var handle)) return Task.CompletedTask;
        // Блоки читаются в исходной рамке страницы: на экран их переносит
        // тот же пересчёт, что и черновики (PlacedRotation = 0).
        var key = (handle, page.PageRef.SourcePageIndex, 0);
        if (!_storedBlocks.TryGetValue(key, out var task))
            _storedBlocks[key] = task = ReadStoredBlocksAsync(handle, page);
        return task;
    }

    private static async Task<IReadOnlyList<StoredTextBlock>> ReadStoredBlocksAsync(IPdfDocumentHandle handle, PageViewModel page)
    {
        try
        {
            return await handle.GetTextBlocksAsync(page.PageRef.SourcePageIndex, 0, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Serilog.Log.Debug(ex, "Блоки страницы {Page} не прочитались", page.LogicalIndex + 1);
            return [];
        }
    }

    /// <summary>Прочитанные блоки страницы (без ожидания: пока чтение идёт — пусто).</summary>
    private IReadOnlyList<StoredTextBlock> StoredBlocksOf(PageViewModel page) =>
        Document.Handles.TryGetValue(page.PageRef.SourceId, out var handle) &&
        _storedBlocks.TryGetValue((handle, page.PageRef.SourcePageIndex, 0), out var task) &&
        task.IsCompletedSuccessfully
            ? task.Result
            : [];

    private static bool HasPendingReplacement(PageViewModel page, Guid id) =>
        page.PageRef.OverlayList.OfType<TextBlockReplacement>().Any(r => r.BlockId == id);

    /// <summary>Целый блок из файла под точкой, ещё не заменённый в этой сессии.</summary>
    private StoredTextBlock? StoredBlockAt(PageViewModel page, double xPt, double yPt, double tolerance)
    {
        foreach (var stored in StoredBlocksOf(page))
        {
            if (stored.Block is not { } block || HasPendingReplacement(page, stored.Id)) continue;
            if (FrameContains((TextBlockOverlay)ToDisplayFrame(block, page), xPt, yPt, tolerance))
                return stored;
        }
        return null;
    }

    // ----- Оформление блока в работе: панель над блоком и правая колонка -----

    private const double PtPerMm = 72 / 25.4;
    private bool _refreshingTextStyle;

    private static readonly string[] TextStyleProperties =
    [
        nameof(TextFamily), nameof(TextSizeText), nameof(TextBold), nameof(TextItalic), nameof(TextUnderline),
        nameof(TextStrike), nameof(TextColorArgb), nameof(TextColorHex), nameof(TextSuperscript), nameof(TextSubscript),
        nameof(TextLetterSpacingText), nameof(TextAlignValue), nameof(TextLineSpacingText), nameof(TextAutoHeight),
        nameof(TextShrinkToFit), nameof(TextRotationText), nameof(TextBoxXMm), nameof(TextBoxYMm), nameof(TextBoxWMm),
        nameof(TextBoxHMm), nameof(TextOverflows),
    ];

    /// <summary>Набор в блоке идёт — показывать раздел «Текст» в правой колонке и панель над блоком.</summary>
    public bool ShowsTextBlockProperties => ActiveTextBlock != null;

    /// <summary>«Шрифт документа «X» не установлен — пишется «Y»» — строка в панели над блоком.</summary>
    public string? TextFontNote => ActiveTextEdit?.FontNote;

    public bool HasTextFontNote => TextFontNote != null;

    /// <summary>Просьба вернуть клавиатуру в редактор после щелчка по панели.</summary>
    public event EventHandler? TextEditorFocusRequested;

    private TextStyle CaretStyle => ActiveTextBlock?.StyleAtCaret ?? Defaults().Style;

    private TextParagraph? CaretParagraph =>
        ActiveTextBlock is { } m && m.Caret.Paragraph < m.Block.Paragraphs.Count ? m.Block.Paragraphs[m.Caret.Paragraph] : null;

    public string TextFamily => CaretStyle.FontFamily;

    public string TextSizeText
    {
        get => CaretStyle.FontSizePt.ToString("0.##", CultureInfo.CurrentCulture);
        set
        {
            if (NexusPdf.Ux.TextBlockStyleCommands.TryParseSize(value, out var pt))
                ApplyTextStyle(s => s with { FontSizePt = pt });
            else
                RefreshTextStyle();
        }
    }

    public bool TextBold { get => CaretStyle.Bold; set => ApplyTextStyle(s => s with { Bold = value }); }
    public bool TextItalic { get => CaretStyle.Italic; set => ApplyTextStyle(s => s with { Italic = value }); }
    public bool TextUnderline { get => CaretStyle.Underline; set => ApplyTextStyle(s => s with { Underline = value }); }
    public bool TextStrike { get => CaretStyle.Strike; set => ApplyTextStyle(s => s with { Strike = value }); }

    public uint TextColorArgb => CaretStyle.ColorArgb;

    public string TextColorHex
    {
        get => NexusPdf.Ux.TextBlockStyleCommands.FormatColor(CaretStyle.ColorArgb);
        set
        {
            if (NexusPdf.Ux.TextBlockStyleCommands.TryParseColor(value, out var argb))
                SetTextColor(argb);
            else
                RefreshTextStyle();
        }
    }

    public bool TextSuperscript
    {
        get => CaretStyle.Baseline == BaselineShift.Superscript;
        set => ApplyTextStyle(s => s with { Baseline = value ? BaselineShift.Superscript : BaselineShift.None });
    }

    public bool TextSubscript
    {
        get => CaretStyle.Baseline == BaselineShift.Subscript;
        set => ApplyTextStyle(s => s with { Baseline = value ? BaselineShift.Subscript : BaselineShift.None });
    }

    /// <summary>Межбуквенный интервал, % размера (−50…500).</summary>
    public string TextLetterSpacingText
    {
        get => CaretStyle.LetterSpacingPercent.ToString("0.#", CultureInfo.CurrentCulture);
        set
        {
            if (TryParseNumber(value, -50, 500, out var percent))
                ApplyTextStyle(s => s with { LetterSpacingPercent = percent });
            else
                RefreshTextStyle();
        }
    }

    public TextAlign TextAlignValue => CaretParagraph?.Align ?? Defaults().Align;

    public string TextLineSpacingText
    {
        get => (CaretParagraph?.LineSpacing ?? Defaults().LineSpacing).ToString("0.##", CultureInfo.CurrentCulture);
        set
        {
            if (TryParseNumber(value, 0.5, 5, out var spacing) && ActiveTextBlock is { } m && !_refreshingTextStyle)
            {
                m.SetLineSpacing(spacing);
                TextEditorFocusRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                RefreshTextStyle();
            }
        }
    }

    public bool TextAutoHeight
    {
        get => ActiveTextBlock?.Block.AutoHeight ?? true;
        set => SetTextFrame(b => b with { AutoHeight = value });
    }

    public bool TextShrinkToFit
    {
        get => ActiveTextBlock?.Block.ShrinkToFit ?? false;
        set => SetTextFrame(b => b with { ShrinkToFit = value });
    }

    /// <summary>Текст не помещается в рамку заданной высоты (виден знак «+» у рамки).</summary>
    public bool TextOverflows => ActiveTextBlock?.Layout.Overflows ?? false;

    public string TextRotationText
    {
        get => (ActiveTextBlock?.Block.RotationDegrees ?? 0).ToString("0.#", CultureInfo.CurrentCulture);
        set
        {
            if (TryParseNumber(value, -360, 360, out var degrees))
                SetTextFrame(b => b with { RotationDegrees = OverlayGeometry.NormalizeDegrees(degrees) });
            else
                RefreshTextStyle();
        }
    }

    public double TextBoxXMm
    {
        get => (ActiveTextBlock?.Block.XPt ?? 0) / PtPerMm;
        set => SetTextFrame(b => b with { XPt = value * PtPerMm });
    }

    public double TextBoxYMm
    {
        get => (ActiveTextBlock?.Block.YPt ?? 0) / PtPerMm;
        set => SetTextFrame(b => b with { YPt = value * PtPerMm });
    }

    /// <summary>Заданная ширина делает блок блоком с переносом.</summary>
    public double TextBoxWMm
    {
        get => (ActiveTextBlock?.Block.WidthPt ?? 0) / PtPerMm;
        set => SetTextFrame(b => b with
        {
            WidthPt = Math.Max(OverlayGeometry.MinimumSizePt, value * PtPerMm), WidthMode = TextBlockWidthMode.Fixed,
        });
    }

    /// <summary>Заданная высота выключает «Высота по тексту».</summary>
    public double TextBoxHMm
    {
        get => (ActiveTextBlock?.Block.HeightPt ?? 0) / PtPerMm;
        set => SetTextFrame(b => b with { HeightPt = Math.Max(OverlayGeometry.MinimumSizePt, value * PtPerMm), AutoHeight = false });
    }

    public void SetTextFamily(string family)
    {
        if (string.IsNullOrWhiteSpace(family)) return;
        ApplyTextStyle(s => s with { FontFamily = family });
    }

    public void SetTextColor(uint argb) => ApplyTextStyle(s => s with { ColorArgb = argb });

    [RelayCommand]
    private void TextSizeStep(string? direction)
    {
        var step = direction == "-" ? -1 : 1;
        var size = NexusPdf.Ux.TextBlockStyleCommands.NextSize(CaretStyle.FontSizePt, step);
        ApplyTextStyle(s => s with { FontSizePt = size });
    }

    [RelayCommand]
    private void SetTextAlign(string? align)
    {
        if (ActiveTextBlock is not { } m || !Enum.TryParse<TextAlign>(align, out var value)) return;
        m.SetAlign(value);
        TextEditorFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Оформление под курсором становится оформлением новых блоков.</summary>
    [RelayCommand]
    private void UseTextStyleAsDefault()
    {
        if (ActiveTextBlock == null) return;
        RememberTextDefaults?.Invoke(CaretStyle, TextAlignValue, CaretParagraph?.LineSpacing ?? 1.15);
        StatusText = Loc.Get("TextStyleSavedAsDefault");
        TextEditorFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyTextStyle(Func<TextStyle, TextStyle> change)
    {
        if (_refreshingTextStyle || ActiveTextBlock is not { } model) return;
        model.ApplyStyle(change);
        TextEditorFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SetTextFrame(Func<TextBlockOverlay, TextBlockOverlay> change)
    {
        if (_refreshingTextStyle || ActiveTextBlock is not { } model) return;
        var b = change(model.Block);
        if (!double.IsFinite(b.XPt) || !double.IsFinite(b.YPt) || !double.IsFinite(b.WidthPt) || !double.IsFinite(b.HeightPt))
        {
            RefreshTextStyle();
            return;
        }
        model.SetFrame(b.XPt, b.YPt, b.WidthPt, b.HeightPt, b.WidthMode, b.AutoHeight, b.ShrinkToFit, b.RotationDegrees);
    }

    private void OnActiveTextChanged(object? sender, EventArgs e) => RefreshTextStyle();

    private void RefreshTextStyle()
    {
        _refreshingTextStyle = true;
        try
        {
            foreach (var name in TextStyleProperties)
                OnPropertyChanged(name);
        }
        finally
        {
            _refreshingTextStyle = false;
        }
    }

    private static bool TryParseNumber(string? text, double min, double max, out double value)
    {
        var trimmed = (text ?? "").Trim().TrimEnd('%', '°').Trim();
        if ((double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
             double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
            double.IsFinite(value) && value >= min && value <= max)
            return true;
        value = 0;
        return false;
    }
}
