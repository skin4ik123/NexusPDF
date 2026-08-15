using System.Collections;
using System.Windows;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.ViewModels;
using NexusPdf.App.Desktop.Views;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Ux;

namespace NexusPdf.App.Desktop.Services.Ux;

/// <summary>На чём выполняется команда: документ, страницы, ссылка, закладка.</summary>
public sealed record UxTarget
{
    public required SelectionContext Context { get; init; }
    public DocumentViewModel? Document { get; init; }

    /// <summary>Страницы, к которым относится команда (миниатюры или страница под курсором).</summary>
    public IReadOnlyList<PageViewModel> Pages { get; init; } = Array.Empty<PageViewModel>();

    public PdfPageLink? Link { get; init; }
    public BookmarkViewModel? Bookmark { get; init; }
}

/// <summary>
/// Мост между реестром команд и реальными действиями программы.
///
/// Здесь ОДНА точка исполнения: панель, контекстное меню, палитра и горячая
/// клавиша зовут <see cref="Invoke"/> с одним и тем же идентификатором, поэтому
/// разойтись в поведении они физически не могут.
///
/// Конструктор проверяет, что у КАЖДОЙ команды каталога есть обработчик, и
/// падает, если его нет: пункт меню, за которым ничего не стоит, — обман, и
/// узнать о нём нужно на запуске, а не от пользователя.
/// </summary>
public sealed class UxCommandHub
{
    private readonly MainViewModel _main;
    private readonly AppServices _services;
    private readonly Dictionary<string, Action<UxTarget>> _handlers;

    public CommandRegistry Registry { get; } = AppCommands.Build();
    public ContextMenuComposer Menus { get; }

    public UxCommandHub(MainViewModel main, AppServices services)
    {
        _main = main;
        _services = services;
        Menus = new ContextMenuComposer(Registry);
        _handlers = BuildHandlers();

        var orphans = Registry.All
            .Where(c => !_handlers.ContainsKey(c.Id))
            .Select(c => c.Id)
            .ToList();
        if (orphans.Count > 0)
            throw new InvalidOperationException(
                "Команды каталога без обработчика: " + string.Join(", ", orphans) +
                ". Команда попадает в каталог вместе с реализацией, а не раньше.");
    }

    private Window? Owner => _main.OwnerWindow;

    /// <summary>Документ, к которому относятся команды без явной цели.</summary>
    public DocumentViewModel? ActiveDocument => _main.ActiveDocument;

    // ----- Состояние -----

    /// <summary>
    /// Слепок текущего состояния для проверки доступности и сборки меню.
    /// Один экземпляр на весь показ меню: панель, пункт и сама команда обязаны
    /// смотреть в одно и то же состояние.
    /// </summary>
    public SelectionContext Snapshot(
        SelectionKind kind = SelectionKind.Nothing,
        IReadOnlyList<PageViewModel>? pages = null)
    {
        var doc = _main.ActiveDocument;
        if (doc == null)
            return new SelectionContext
            {
                HasDocument = false,
                HasQpdf = _main.HasPdfTools,
                HasOcr = _main.HasOcr,
                HasImageEditor = ExternalImageEditor.IsEditorAvailable(),
            };

        var selectedPages = pages ?? Array.Empty<PageViewModel>();
        return new SelectionContext
        {
            HasDocument = true,
            Kind = kind,
            Mode = doc.IsOrganizeMode ? WorkMode.Pages
                : doc.IsFormMode ? WorkMode.Forms
                : WorkMode.View,
            SelectedCount = kind == SelectionKind.Page ? selectedPages.Count : 0,
            SelectedPageNumbers = selectedPages.Select(p => p.PageNumber).ToList(),
            CurrentPageNumber = doc.CurrentPageNumber,
            PageCount = doc.PageCount,
            HasTextSelection = doc.HasSelection,
            HasSelectedObject = doc.HasObjectSelection,
            HasUnsavedChanges = doc.IsDirty,
            CanUndo = doc.CanUndo,
            CanRedo = doc.CanRedo,
            IsSigned = doc.HasSignatures,
            AllowsPrinting = doc.AllowsPrinting,
            IsBusy = doc.IsBusy,
            HasQpdf = _main.HasPdfTools,
            HasOcr = _main.HasOcr,
            HasImageEditor = ExternalImageEditor.IsEditorAvailable(),
        };
    }

    // ----- Названия -----

    /// <summary>Название пункта с учётом числа выделенных объектов и многоточия у диалогов.</summary>
    public static string Title(CommandDescriptor command, SelectionContext context)
    {
        var title = ContextMenuComposer.Title(command, context, Loc.Get,
            (key, _) => Loc.F(key, CountPhrase(context)));
        // Многоточие означает «спросит и только потом сделает». Ставится
        // здесь, а не в словаре, чтобы не забыть его в одной из точек.
        return command.OpensDialog && !title.EndsWith('…') ? title + "…" : title;
    }

    /// <summary>«12 страниц» с правильным окончанием — в заголовке пункта меню.</summary>
    private static string CountPhrase(SelectionContext context)
    {
        var noun = context.Kind switch
        {
            SelectionKind.Page => "UxNounPage",
            SelectionKind.Annotation => "UxNounComment",
            _ => "UxNounObject",
        };
        return $"{context.SelectedCount} {Loc.Get(noun + PluralSuffix(context.SelectedCount))}";
    }

    /// <summary>Русские окончания: 1 страница, 2 страницы, 5 страниц, 11 страниц.</summary>
    private static string PluralSuffix(int count)
    {
        var mod100 = count % 100;
        if (mod100 is >= 11 and <= 14) return "5";
        return (count % 10) switch { 1 => "1", 2 or 3 or 4 => "2", _ => "5" };
    }

    public static string Reason(string? reasonKey) =>
        string.IsNullOrEmpty(reasonKey) ? "" : Loc.Get(reasonKey);

    // ----- Исполнение -----

    public void Invoke(string commandId, UxTarget target)
    {
        if (!_handlers.TryGetValue(commandId, out var handler))
            throw new InvalidOperationException($"Команда «{commandId}» не имеет обработчика.");

        var availability = Registry.Require(commandId).Evaluate(target.Context);
        if (!availability.IsAvailable)
        {
            // Недоступную команду можно позвать из горячей клавиши или палитры:
            // молча проглатывать нельзя — пользователь должен узнать причину.
            if (_main.ActiveDocument is { } doc)
                doc.StatusText = Reason(availability.ReasonKey);
            return;
        }

        try
        {
            handler(target);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Ошибка выполнения команды {Command}", commandId);
            ErrorDialog.Show(Owner, Loc.Get("ErrorTitle"), ex.Message, ex.ToString());
        }
    }

    /// <summary>Страницы, к которым относится команда: выбранные либо текущая.</summary>
    private IList PagesOf(UxTarget target)
    {
        if (target.Pages.Count > 0)
            return target.Pages.ToList();
        if (target.Document is not { } doc || doc.Pages.Count == 0)
            return Array.Empty<PageViewModel>();
        var index = Math.Clamp(doc.CurrentPageNumber - 1, 0, doc.Pages.Count - 1);
        return new List<PageViewModel> { doc.Pages[index] };
    }

    private IReadOnlyList<int> PageIndicesOf(UxTarget target) =>
        PagesOf(target).Cast<PageViewModel>().Select(p => p.LogicalIndex).OrderBy(i => i).ToList();

    private Dictionary<string, Action<UxTarget>> BuildHandlers() => new(StringComparer.Ordinal)
    {
        // Файл
        [CommandIds.Open] = _ => _main.OpenCommand.Execute(null),
        [CommandIds.Save] = _ => _main.SaveCommand.Execute(null),
        [CommandIds.SaveAs] = _ => _main.SaveAsCommand.Execute(null),
        [CommandIds.CloseTab] = t => _main.CloseTabCommand.Execute(t.Document),
        [CommandIds.DocumentProperties] = _ => _main.ShowPropertiesCommand.Execute(null),

        // Правка
        [CommandIds.Undo] = _ => _main.UndoActiveCommand.Execute(null),
        [CommandIds.Redo] = _ => _main.RedoActiveCommand.Execute(null),
        [CommandIds.Copy] = t => t.Document?.CopySelectionCommand.Execute(null),
        [CommandIds.SelectAllOnPage] = t => t.Document?.SelectAllOnPageCommand.Execute(null),
        [CommandIds.Duplicate] = t => t.Document?.DuplicateSelectedCommand.Execute(PagesOf(t)),

        // Выбранный объект
        [CommandIds.Delete] = t => t.Document?.DeleteSelectedObject(),
        [CommandIds.DuplicateObject] = t => t.Document?.DuplicateSelectedObject(),
        [CommandIds.BringForward] = t => t.Document?.MoveSelectedObjectInOrder(forward: true),
        [CommandIds.SendBackward] = t => t.Document?.MoveSelectedObjectInOrder(forward: false),
        [CommandIds.ObjectProperties] = t =>
        {
            if (t.Document is not { } doc) return;
            InfoDialog.Show(Owner, Loc.Get("UxObjectProperties"), doc.DescribeSelectedObject());
        },

        // Просмотр
        [CommandIds.ZoomIn] = _ => _main.ZoomInActiveCommand.Execute(null),
        [CommandIds.ZoomOut] = _ => _main.ZoomOutActiveCommand.Execute(null),
        [CommandIds.ZoomActual] = _ => _main.ZoomActualActiveCommand.Execute(null),
        [CommandIds.FitWidth] = _ => _main.FitWidthActiveCommand.Execute(null),
        [CommandIds.FitPage] = _ => _main.FitPageActiveCommand.Execute(null),
        [CommandIds.Find] = _ => _main.ToggleFindActiveCommand.Execute(null),
        [CommandIds.FindSelection] = t => _ = t.Document?.FindSelectedTextAsync(),
        [CommandIds.ToggleOrganize] = _ => _main.ToggleOrganizeCommand.Execute(null),
        [CommandIds.ToggleOutline] = t => t.Document?.ToggleOutlineCommand.Execute(null),
        [CommandIds.ToggleCommentsPanel] = _ => _main.ToggleCommentsActiveCommand.Execute(null),

        // Страницы
        [CommandIds.RotateRight] = t => t.Document?.RotateSelectedCommand.Execute(PagesOf(t)),
        [CommandIds.RotateLeft] = t => t.Document?.RotateSelectedLeftCommand.Execute(PagesOf(t)),
        [CommandIds.Rotate180] = t => t.Document?.RotateSelected180Command.Execute(PagesOf(t)),
        [CommandIds.DeletePages] = t => t.Document?.DeleteSelectedCommand.Execute(PagesOf(t)),
        [CommandIds.ExtractPages] = t => _main.ExtractSelectedCommand.Execute(PagesOf(t)),
        [CommandIds.PageProperties] = ShowPageProperties,
        [CommandIds.PrintSelectedPages] = t => PrintPages(t, PageIndicesOf(t)),
        [CommandIds.PrintCurrentPage] = t => PrintPages(t,
            t.Document is { } doc ? new[] { Math.Clamp(doc.CurrentPageNumber - 1, 0, doc.PageCount - 1) }
                                  : Array.Empty<int>()),

        // Содержимое
        [CommandIds.AddText] = _ => _main.AddTextOverlayCommand.Execute(null),
        [CommandIds.InsertImage] = _ => _main.InsertImageOverlayCommand.Execute(null),
        [CommandIds.InsertSignatureImage] = _ => _main.InsertSignatureCommand.Execute(null),
        [CommandIds.EditText] = _ => _main.EditExistingTextCommand.Execute(null),
        [CommandIds.EditImageInPaint] = _ => _main.EditImageInPaintCommand.Execute(null),
        [CommandIds.EditRegionInPaint] = _ => _main.EditRegionInPaintCommand.Execute(null),
        [CommandIds.EditPageInPaint] = _ => _main.EditPageInPaintCommand.Execute(null),
        [CommandIds.HeaderFooter] = _ => _main.ShowHeaderFooterCommand.Execute(null),
        [CommandIds.Watermark] = _ => _main.ShowWatermarkCommand.Execute(null),

        // Комментарии и разметка
        [CommandIds.AddNote] = _ => _main.AddNoteCommand.Execute(null),
        [CommandIds.Highlight] = t => Markup(t, TextMarkupKind.Highlight),
        [CommandIds.Underline] = t => Markup(t, TextMarkupKind.Underline),
        [CommandIds.Strikeout] = t => Markup(t, TextMarkupKind.StrikeOut),
        [CommandIds.AddRect] = _ => _main.AddRectShapeCommand.Execute(null),
        [CommandIds.AddEllipse] = _ => _main.AddEllipseShapeCommand.Execute(null),
        [CommandIds.DrawPencil] = _ => _main.DrawPencilCommand.Execute(null),
        [CommandIds.DrawLine] = _ => _main.DrawLineCommand.Execute(null),
        [CommandIds.DrawArrow] = _ => _main.DrawArrowCommand.Execute(null),
        [CommandIds.RestoreStroke] = t => t.Document?.RestoreFreeStrokeCommand.Execute(null),

        // Формы
        [CommandIds.ToggleFormMode] = _ => _main.ToggleFormModeActiveCommand.Execute(null),

        // Защита
        [CommandIds.Redact] = _ => _main.AddRedactionCommand.Execute(null),
        [CommandIds.ProtectWithPassword] = _ => _main.ProtectWithPasswordCommand.Execute(null),
        [CommandIds.SignWithCertificate] = _ => _main.SignWithCertificateCommand.Execute(null),
        [CommandIds.VerifySignature] = _ => _main.ShowSignaturesCommand.Execute(null),

        // Ссылки и закладки
        [CommandIds.OpenLink] = t =>
        {
            if (t.Document is { } doc && t.Link is { } link)
                doc.ActivateLink(link);
        },
        [CommandIds.CopyLinkAddress] = CopyLinkAddress,
        [CommandIds.GoToBookmark] = t =>
        {
            if (t.Document is { } doc && t.Bookmark is { } bookmark)
                doc.GoToBookmark(bookmark);
        },

        // Распознавание и печать
        [CommandIds.Ocr] = _ => _main.RecognizeTextCommand.Execute(null),
        [CommandIds.Print] = _ => _main.PrintActiveCommand.Execute(null),
        [CommandIds.BatchPrint] = _ => _main.ShowBatchPrintCommand.Execute(null),

        // Преобразование
        [CommandIds.CompressPages] = _ => _main.CompressImagesCommand.Execute(null),
        [CommandIds.EnhanceScans] = _ => _main.EnhanceScansCommand.Execute(null),
        [CommandIds.OptimizeCopy] = _ => _main.OptimizeCopyCommand.Execute(null),
        [CommandIds.ExportImages] = _ => _main.ExportImagesCommand.Execute(null),
        [CommandIds.ExtractText] = _ => _main.ExtractTextCommand.Execute(null),
        [CommandIds.CreateFromImages] = _ => _main.CreateFromImagesCommand.Execute(null),
        [CommandIds.MergePdfs] = _ => _main.MergePdfsCommand.Execute(null),
        [CommandIds.CompareDocuments] = _ => _main.CompareDocumentsCommand.Execute(null),
        [CommandIds.BatchProcess] = _ => _main.ShowBatchCommand.Execute(null),

        // Документ целиком
        [CommandIds.ShowLayers] = _ => _main.ShowLayersCommand.Execute(null),
        [CommandIds.ShowAttachments] = _ => _main.ShowAttachmentsCommand.Execute(null),

        // Окно
        [CommandIds.NewWindow] = _ => _main.NewWindowCommand.Execute(null),
        [CommandIds.NextTab] = _ => _main.NextTabCommand.Execute(null),
        [CommandIds.PreviousTab] = _ => _main.PreviousTabCommand.Execute(null),
        [CommandIds.DetachTab] = t => _main.DetachTabCommand.Execute(t.Document),
        [CommandIds.CommandPalette] = _ => ShowPalette(),
        [CommandIds.About] = _ => _main.AboutCommand.Execute(null),
    };

    // ----- Обработчики, у которых есть своя логика -----

    /// <summary>
    /// Разметка выделенного текста. Маркер и вымарывание живут в
    /// <see cref="MainViewModel"/>: без выделения они работают прежним
    /// растягиванием рамки, и обе точки входа обязаны вести себя одинаково.
    /// </summary>
    private void Markup(UxTarget target, TextMarkupKind kind)
    {
        if (kind == TextMarkupKind.Highlight)
        {
            _main.AddHighlightCommand.Execute(null);
            return;
        }
        // Подчёркивание и зачёркивание существуют только по выделению —
        // рамкой их ставить не над чем.
        target.Document?.MarkupSelection(kind);
    }

    private void CopyLinkAddress(UxTarget target)
    {
        var address = target.Link?.Uri;
        if (string.IsNullOrEmpty(address)) return;
        try
        {
            Clipboard.SetText(address);
            if (target.Document is { } doc)
                doc.StatusText = Loc.Get("UxLinkCopied");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось скопировать адрес ссылки");
        }
    }

    private void PrintPages(UxTarget target, IReadOnlyList<int> pageIndices)
    {
        if (target.Document is not { } doc || doc.IsBusy) return;
        PrintCenterDialog.Run(Owner, doc, _services, pageIndices);
    }

    /// <summary>Свойства страниц: размер в миллиметрах и пунктах, поворот, число.</summary>
    private void ShowPageProperties(UxTarget target)
    {
        if (target.Document is not { } doc) return;
        var pages = PagesOf(target).Cast<PageViewModel>().ToList();
        if (pages.Count == 0) return;

        var lines = new List<string>();
        foreach (var page in pages.Take(20))
        {
            var widthMm = page.SizePt.WidthPoints / 72.0 * 25.4;
            var heightMm = page.SizePt.HeightPoints / 72.0 * 25.4;
            lines.Add(Loc.F("UxPagePropsLine",
                page.PageNumber,
                Math.Round(widthMm, 1), Math.Round(heightMm, 1),
                Math.Round(page.SizePt.WidthPoints, 1), Math.Round(page.SizePt.HeightPoints, 1),
                page.RotationDegrees));
        }
        if (pages.Count > lines.Count)
            lines.Add(Loc.F("UxPagePropsMore", pages.Count - lines.Count));

        InfoDialog.Show(Owner, Loc.Get("UxPageProperties"), string.Join("\n", lines));
    }

    private void ShowPalette()
    {
        var chosen = CommandPaletteDialog.Pick(Owner, this, Snapshot());
        if (chosen == null) return;
        // Палитра закрывается ДО выполнения: команда может открыть свой диалог,
        // и два окна подряд поверх друг друга выглядят как ошибка.
        Invoke(chosen, new UxTarget { Context = Snapshot(), Document = _main.ActiveDocument });
    }
}
