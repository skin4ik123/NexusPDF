using CommunityToolkit.Mvvm.ComponentModel;
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
    public sealed record TextBlockEdit(
        PageViewModel Page, TextBlockEditorModel Model, TextBlockOverlay? Original, Guid? StoredId,
        TextBlockOverlay StartBlock, bool CoversOriginal);

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

    public void ToggleTextTool()
    {
        if (IsTextTool)
        {
            CommitTextBlock();
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
        // Список шрифтов сканируется заранее, пока человек выбирает место.
        _ = Task.Run(() => _ = FontCatalog.System.Families);
    }

    /// <summary>Щелчок инструментом: блок без переноса, растёт вправо при наборе.</summary>
    public Task BeginTextBlockAtAsync(PageViewModel page, double xPt, double yPt)
    {
        var (style, align, spacing) = Defaults();
        var lineHeight = style.FontSizePt * 1.2;
        var block = NewBlock(
            xPt - TextBlockLayout.PaddingPt, yPt - TextBlockLayout.PaddingPt - lineHeight / 2,
            2 * TextBlockLayout.PaddingPt + 1, 2 * TextBlockLayout.PaddingPt + lineHeight,
            TextBlockWidthMode.AutoWidth, style, align, spacing, page);
        StartTextEdit(page, block, null, null, coversOriginal: false);
        return Task.CompletedTask;
    }

    /// <summary>Протянутая рамка: ширина задана, текст переносится по словам, высота растёт сама.</summary>
    public Task BeginTextBlockInRectAsync(PageViewModel page, OverlayBox rect)
    {
        var (style, align, spacing) = Defaults();
        var box = OverlayGeometry.Normalize(rect);
        var block = NewBlock(box.XPt, box.YPt, box.WidthPt, box.HeightPt,
            TextBlockWidthMode.Fixed, style, align, spacing, page);
        StartTextEdit(page, block, null, null, coversOriginal: false);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Снова писать в блоке под точкой: черновик этой сессии или блок из файла.
    /// false — блока здесь нет (или он изменён другой программой — тогда
    /// объяснение уже в строке состояния).
    /// </summary>
    public async Task<bool> EditTextBlockAsync(PageViewModel page, double xPt, double yPt)
    {
        if (IsBusy) return false;
        var tolerance = 2 / Math.Max(Zoom, 0.1);

        var overlays = page.PageRef.OverlayList;
        for (var i = overlays.Count - 1; i >= 0; i--)
        {
            switch (ToDisplayFrame(overlays[i], page))
            {
                case TextBlockOverlay draft when FrameContains(draft, xPt, yPt, tolerance):
                    StartTextEdit(page, draft, (TextBlockOverlay)overlays[i], null, coversOriginal: true, xPt, yPt);
                    return true;
                case TextBlockReplacement { NewBlock: { } pending } replacement when FrameContains(pending, xPt, yPt, tolerance):
                    StartTextEdit(page, pending, null, replacement.BlockId, coversOriginal: true, xPt, yPt);
                    return true;
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
            StartTextEdit(page, displayed, null, stored.Id, coversOriginal: true, xPt, yPt);
            return true;
        }
        return false;
    }

    /// <summary>Закончить ввод: набранное уходит в документ одним шагом отмены.</summary>
    public void CommitTextBlock()
    {
        if (ActiveTextEdit is not { } edit) return;
        ActiveTextEdit = null;
        OnPropertyChanged(nameof(ActiveTextBlock));
        TextBlockEditEnded?.Invoke(this, EventArgs.Empty);

        var edited = edit.Model.Block with { PlacedRotation = edit.Page.PageRef.RotationOffset };
        if (!TextBlockSessionService.SameContent(edit.StartBlock, edited))
        {
            TextBlockSessionService.Commit(Document.Session, edit.Page.LogicalIndex, edit.Original, edit.StoredId, edited);
            if (edited.Paragraphs.LastOrDefault(p => p.Text.Length > 0) is { } last)
                RememberTextDefaults?.Invoke(last.Runs[^1].Style, last.Align, last.LineSpacing);
        }
        if (StatusText == Loc.Get("TextBlockTyping"))
            StatusText = IsTextTool ? Loc.Get("TextToolHint") : Loc.Get("Ready");
    }

    private void StartTextEdit(
        PageViewModel page, TextBlockOverlay block, TextBlockOverlay? original, Guid? storedId,
        bool coversOriginal, double? xPt = null, double? yPt = null)
    {
        CommitTextBlock();
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
        var edit = new TextBlockEdit(page, model, original, storedId, model.Block, coversOriginal);
        ActiveTextEdit = edit;
        OnPropertyChanged(nameof(ActiveTextBlock));
        StatusText = Loc.Get("TextBlockTyping");
        TextBlockEditStarted?.Invoke(this, edit);
    }

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

    private readonly Dictionary<(IPdfDocumentHandle Handle, int Page, int Turns), IReadOnlyList<StoredTextBlock>> _storedBlocks = new();

    /// <summary>Прочитать блоки страницы из файла (один раз на страницу и поворот).</summary>
    public async Task EnsureStoredTextBlocksAsync(PageViewModel page)
    {
        if (!Document.Handles.TryGetValue(page.PageRef.SourceId, out var handle)) return;
        // Блоки читаются в исходной рамке страницы: на экран их переносит
        // тот же пересчёт, что и черновики (PlacedRotation = 0).
        var key = (handle, page.PageRef.SourcePageIndex, 0);
        if (_storedBlocks.ContainsKey(key)) return;
        _storedBlocks[key] = [];
        try
        {
            _storedBlocks[key] = await handle.GetTextBlocksAsync(page.PageRef.SourcePageIndex, 0, CancellationToken.None);
        }
        catch (Exception ex) when (ex is PdfEngineException or InvalidOperationException or ObjectDisposedException)
        {
            Serilog.Log.Debug(ex, "Блоки страницы {Page} не прочитались", page.LogicalIndex + 1);
        }
    }

    private IReadOnlyList<StoredTextBlock> StoredBlocksOf(PageViewModel page) =>
        Document.Handles.TryGetValue(page.PageRef.SourceId, out var handle) &&
        _storedBlocks.TryGetValue((handle, page.PageRef.SourcePageIndex, 0), out var blocks)
            ? blocks
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
}
