using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Abstractions.TextBlocks;
using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

public sealed class PdfiumRenderEngine : IPdfRenderEngine
{
    // Значения из fpdfview.h
    private const int FpdfBitmapBgra = 4;
    private const int RenderFlagAnnot = 0x01;
    private const int RenderFlagLcdText = 0x02;
    private const ulong ErrPassword = 4;
    private const ulong ErrSecurity = 5;

    private readonly PdfiumThread _thread = PdfiumThread.Shared;

    public PdfiumRenderEngine() : this(null) { }

    /// <param name="xfaWriter">
    /// Запись данных XFA гибридных форм. Без него такие формы только
    /// показываются и печатаются: правка AcroForm без XFA-данных дала бы в
    /// Adobe Reader прежние значения.
    /// </param>
    public PdfiumRenderEngine(IXfaPacketWriter? xfaWriter) : this(xfaWriter, (IFontProvider?)null) { }

    /// <param name="fonts">
    /// Шрифты текстовых блоков. null — шрифты системы; тесты подставляют свой
    /// каталог, чтобы раскладка не зависела от машины.
    /// </param>
    public PdfiumRenderEngine(IXfaPacketWriter? xfaWriter, IFontProvider? fonts)
        : this(xfaWriter, () => fonts ?? FontCatalog.System) { }

    /// <param name="fonts">
    /// Каталог шрифтов, который строится только при первой записи текста:
    /// сканирование всех шрифтов системы — это чтение сотен файлов, и платить
    /// за него сохранению без текста незачем.
    /// </param>
    public PdfiumRenderEngine(IXfaPacketWriter? xfaWriter, Func<IFontProvider> fonts)
    {
        XfaWriter = xfaWriter;
        Fonts = new Lazy<IFontProvider>(fonts, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal IXfaPacketWriter? XfaWriter { get; }

    /// <summary>Каталог шрифтов надписей и блоков — по первому требованию.</summary>
    internal Lazy<IFontProvider> Fonts { get; }

    public string EngineName => "PDFium";

    public Task<IPdfDocumentHandle> OpenAsync(string filePath, string? password, CancellationToken ct) =>
        _thread.InvokeAsync<IPdfDocumentHandle>(() => OpenCore(filePath, password), ct);

    private unsafe IPdfDocumentHandle OpenCore(string filePath, string? password)
    {
        // Документ отображается в память: юникод-пути обрабатывает .NET,
        // страницы файла ОС подгружает лениво, управляемая куча не расходуется.
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        MemoryMappedFile? mmf = null;
        MemoryMappedViewAccessor? accessor = null;
        var pointerAcquired = false;

        try
        {
            var length = stream.Length;
            if (length == 0)
                throw new PdfCorruptedException("Файл пуст.");
            if (length > int.MaxValue)
                throw new PdfEngineException("Файлы крупнее 2 ГБ пока не поддерживаются.");

            mmf = MemoryMappedFile.CreateFromFile(stream, null, 0,
                MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
            accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            byte* basePointer = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePointer);
            pointerAcquired = true;

            var doc = fpdfview.FPDF_LoadMemDocument((IntPtr)basePointer, (int)length, password);
            if (doc == null || doc.__Instance == IntPtr.Zero)
            {
                var error = fpdfview.FPDF_GetLastError();
                if (error is ErrPassword or ErrSecurity)
                    throw new PdfPasswordRequiredException();
                throw new PdfCorruptedException($"Файл не распознан как корректный PDF (код PDFium: {error}).");
            }

            var pageCount = fpdfview.FPDF_GetPageCount(doc);
            var pages = new List<PdfPageDescriptor>(pageCount);
            var size = new FS_SIZEF_();
            for (var i = 0; i < pageCount; i++)
            {
                if (fpdfview.FPDF_GetPageSizeByIndexF(doc, i, size) == 0)
                    throw new PdfCorruptedException($"Не удалось получить размер страницы {i + 1}.");
                pages.Add(new PdfPageDescriptor(size.Width, size.Height));
            }

            return new PdfiumDocumentHandle(this, _thread, filePath, doc,
                new PdfDocumentInfo(pageCount, pages), mmf, accessor, password);
        }
        catch
        {
            if (pointerAcquired)
                accessor!.SafeMemoryMappedViewHandle.ReleasePointer();
            accessor?.Dispose();
            mmf?.Dispose();
            if (mmf == null)
                stream.Dispose();
            throw;
        }
    }

    public Task ComposeAsync(IReadOnlyList<ComposedPage> pages, string targetPath, CancellationToken ct) =>
        _thread.InvokeAsync(() => ComposeCore(pages, targetPath), ct);

    public Task<RenderedPageImage> RenderPageWithOverlaysAsync(
        IPdfDocumentHandle source, int sourcePageIndex, int extraQuarterTurns,
        IReadOnlyList<PageOverlay> overlays, int pixelWidth, int pixelHeight, CancellationToken ct) =>
        _thread.InvokeAsync(() => RenderWithOverlaysCore(
            (PdfiumDocumentHandle)source, sourcePageIndex, extraQuarterTurns,
            overlays, pixelWidth, pixelHeight), ct);

    public Task<RenderedPageImage> RenderOverlayLayerAsync(
        IPdfDocumentHandle source, int sourcePageIndex, int extraQuarterTurns,
        IReadOnlyList<PageOverlay> overlays, int pixelWidth, int pixelHeight, CancellationToken ct) =>
        _thread.InvokeAsync(() => RenderOverlayLayerCore(
            (PdfiumDocumentHandle)source, sourcePageIndex, extraQuarterTurns,
            overlays, pixelWidth, pixelHeight), ct);

    /// <summary>
    /// Правки на ПУСТОЙ странице той же геометрии (те же рамки и поворот), на
    /// прозрачном фоне. Содержимое исходной страницы не копируется и не
    /// рисуется — поэтому слой готов за миллисекунды и на тяжёлом чертеже.
    /// Путь запекания тот же, что при сохранении, и пометка на экране
    /// совпадает с будущим файлом.
    /// </summary>
    private RenderedPageImage RenderOverlayLayerCore(
        PdfiumDocumentHandle source, int sourcePageIndex, int extraQuarterTurns,
        IReadOnlyList<PageOverlay> overlays, int pixelWidth, int pixelHeight)
    {
        if (pixelWidth < 1 || pixelHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));

        var boxes = source.ReadPageBoxes(sourcePageIndex);
        var newDoc = fpdf_edit.FPDF_CreateNewDocument();
        if (newDoc == null || newDoc.__Instance == IntPtr.Zero)
            throw new PdfEngineException("Overlay layer document was not created.");
        try
        {
            var blank = fpdf_edit.FPDFPageNew(newDoc, 0,
                boxes.MediaRight - boxes.MediaLeft, boxes.MediaTop - boxes.MediaBottom);
            if (blank == null || blank.__Instance == IntPtr.Zero)
                throw new PdfEngineException("Overlay layer page was not created.");
            try
            {
                fpdf_transformpage.FPDFPageSetMediaBox(blank,
                    boxes.MediaLeft, boxes.MediaBottom, boxes.MediaRight, boxes.MediaTop);
                if (boxes.HasCrop)
                    fpdf_transformpage.FPDFPageSetCropBox(blank,
                        boxes.CropLeft, boxes.CropBottom, boxes.CropRight, boxes.CropTop);
                fpdf_edit.FPDFPageSetRotation(blank, boxes.Rotation);

                var rotation = ((extraQuarterTurns % 4) + 4) % 4;
                var shown = overlays.Select(OverlayLayers.ForDisplay).ToList();
                ApplyOverlaysInDisplayedFrame(newDoc, blank, shown, rotation);
            }
            finally
            {
                fpdfview.FPDF_ClosePage(blank);
            }

            var layer = fpdfview.FPDF_LoadPage(newDoc, 0);
            if (layer == null || layer.__Instance == IntPtr.Zero)
                throw new PdfEngineException("Overlay layer page did not reopen.");
            try
            {
                var stride = pixelWidth * 4;
                var pixels = new byte[(long)stride * pixelHeight];
                var pin = System.Runtime.InteropServices.GCHandle.Alloc(
                    pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    var bitmap = fpdfview.FPDFBitmapCreateEx(
                        pixelWidth, pixelHeight, 4, pin.AddrOfPinnedObject(), stride);
                    if (bitmap == null || bitmap.__Instance == IntPtr.Zero)
                        throw new PdfEngineException("Overlay layer bitmap was not created.");
                    try
                    {
                        // Прозрачный фон; субпиксельный текст на прозрачном
                        // фоне невозможен, поэтому только флаг аннотаций.
                        fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, pixelWidth, pixelHeight, 0x00000000UL);
                        fpdfview.FPDF_RenderPageBitmap(bitmap, layer, 0, 0, pixelWidth, pixelHeight,
                            ((extraQuarterTurns % 4) + 4) % 4, 0x01);
                    }
                    finally
                    {
                        fpdfview.FPDFBitmapDestroy(bitmap);
                    }
                }
                finally
                {
                    pin.Free();
                }
                return new RenderedPageImage(pixelWidth, pixelHeight, stride, pixels);
            }
            finally
            {
                fpdfview.FPDF_ClosePage(layer);
            }
        }
        finally
        {
            fpdfview.FPDF_CloseDocument(newDoc);
        }
    }

    /// <summary>
    /// Дескриптор ОДНОЙ страницы с применёнными правками — обычный документ в
    /// памяти из одной страницы. Через него работают все существующие операции
    /// (текст, поиск, выделение, ссылки), поэтому распознанный или добавленный
    /// текст доступен сразу, без сохранения файла.
    /// </summary>
    public Task<IPdfDocumentHandle> CreateBakedPageAsync(
        IPdfDocumentHandle source, int sourcePageIndex, int extraQuarterTurns,
        IReadOnlyList<PageOverlay> overlays, CancellationToken ct) =>
        _thread.InvokeAsync<IPdfDocumentHandle>(() =>
        {
            var doc = BuildBakedPage((PdfiumDocumentHandle)source, sourcePageIndex, extraQuarterTurns, overlays);
            try
            {
                var page = fpdfview.FPDF_LoadPage(doc, 0);
                if (page == null || page.__Instance == IntPtr.Zero)
                    throw new PdfEngineException("Не удалось открыть подготовленную страницу.");
                PdfPageDescriptor descriptor;
                try
                {
                    descriptor = new PdfPageDescriptor(
                        fpdfview.FPDF_GetPageWidthF(page), fpdfview.FPDF_GetPageHeightF(page));
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
                return new PdfiumDocumentHandle(
                    this, _thread, source.FilePath, doc,
                    new PdfDocumentInfo(1, new[] { descriptor }), null, null);
            }
            catch
            {
                fpdfview.FPDF_CloseDocument(doc);
                throw;
            }
        }, ct);

    /// <summary>Переносит одну страницу в новый документ и запекает в неё правки.</summary>
    private FpdfDocumentT BuildBakedPage(
        PdfiumDocumentHandle source, int sourcePageIndex, int extraQuarterTurns,
        IReadOnlyList<PageOverlay> overlays)
    {
        var newDoc = fpdf_edit.FPDF_CreateNewDocument();
        if (newDoc == null || newDoc.__Instance == IntPtr.Zero)
            throw new PdfEngineException("Не удалось подготовить страницу.");
        try
        {
            if (fpdf_ppo.FPDF_ImportPages(newDoc, source.NativeDoc,
                    (sourcePageIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), 0) == 0)
                throw new PdfEngineException("Не удалось перенести страницу.");

            if (overlays.Count > 0)
            {
                var page = fpdfview.FPDF_LoadPage(newDoc, 0);
                if (page == null || page.__Instance == IntPtr.Zero)
                    throw new PdfEngineException("Не удалось открыть страницу для запекания.");
                try
                {
                    ApplyOverlaysInDisplayedFrame(newDoc, page, overlays, extraQuarterTurns);
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }
            return newDoc;
        }
        catch
        {
            fpdfview.FPDF_CloseDocument(newDoc);
            throw;
        }
    }

    /// <summary>
    /// Запекание правок на страницу, которую ПОКАЖУТ с добавочным поворотом.
    ///
    /// Координаты правок заданы в отображаемой рамке (с учётом поворота,
    /// сделанного пользователем), а запекание читает поворот из самой страницы.
    /// При сохранении поворот к этому моменту уже записан в страницу, а здесь
    /// страница ещё исходная: без временной установки того же поворота правка
    /// на повёрнутой странице ложилась бы в чужую рамку. После запекания
    /// поворот возвращается — показ и выборка текста доворачивают страницу сами.
    /// </summary>
    private void ApplyOverlaysInDisplayedFrame(
        FpdfDocumentT document, FpdfPageT page, IReadOnlyList<PageOverlay> overlays, int extraQuarterTurns)
    {
        var extra = ((extraQuarterTurns % 4) + 4) % 4;
        var own = fpdf_edit.FPDFPageGetRotation(page);
        if (extra != 0)
            fpdf_edit.FPDFPageSetRotation(page, ((own + extra) % 4 + 4) % 4);
        try
        {
            PdfiumOverlayWriter.ApplyOverlays(document, page, new OverlayFontCache(document, Fonts), overlays, extra);
        }
        finally
        {
            if (extra != 0)
                fpdf_edit.FPDFPageSetRotation(page, own);
        }
    }

    /// <summary>
    /// Одна страница переносится во ВРЕМЕННЫЙ документ в памяти, туда же
    /// запекаются правки, и рисуется уже он. На диск ничего не пишется, а
    /// картинка совпадает с будущим файлом, потому что путь запекания тот же.
    /// </summary>
    private RenderedPageImage RenderWithOverlaysCore(
        PdfiumDocumentHandle source, int sourcePageIndex, int extraQuarterTurns,
        IReadOnlyList<PageOverlay> overlays, int pixelWidth, int pixelHeight)
    {
        if (pixelWidth < 1 || pixelHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));

        var newDoc = fpdf_edit.FPDF_CreateNewDocument();
        if (newDoc == null || newDoc.__Instance == IntPtr.Zero)
            throw new PdfEngineException("Не удалось подготовить страницу для показа.");
        try
        {
            if (fpdf_ppo.FPDF_ImportPages(newDoc, source.NativeDoc,
                    (sourcePageIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), 0) == 0)
                throw new PdfEngineException("Не удалось перенести страницу для показа.");

            var rotation = ((extraQuarterTurns % 4) + 4) % 4;
            var page = fpdfview.FPDF_LoadPage(newDoc, 0);
            if (page == null || page.__Instance == IntPtr.Zero)
                throw new PdfEngineException("Не удалось открыть страницу для показа.");
            try
            {
                if (overlays.Count > 0)
                    ApplyOverlaysInDisplayedFrame(newDoc, page, overlays, rotation);
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }

            // Страница перечитывается: после запекания её содержимое изменилось.
            var baked = fpdfview.FPDF_LoadPage(newDoc, 0);
            if (baked == null || baked.__Instance == IntPtr.Zero)
                throw new PdfEngineException("Не удалось открыть подготовленную страницу.");
            try
            {
                var stride = pixelWidth * 4;
                var pixels = new byte[(long)stride * pixelHeight];
                var pin = System.Runtime.InteropServices.GCHandle.Alloc(
                    pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    var bitmap = fpdfview.FPDFBitmapCreateEx(
                        pixelWidth, pixelHeight, 4, pin.AddrOfPinnedObject(), stride);
                    if (bitmap == null || bitmap.__Instance == IntPtr.Zero)
                        throw new PdfEngineException("Не удалось создать растровый буфер.");
                    try
                    {
                        fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, pixelWidth, pixelHeight, 0xFFFFFFFFUL);
                        fpdfview.FPDF_RenderPageBitmap(
                            bitmap, baked, 0, 0, pixelWidth, pixelHeight, rotation, 0x01 | 0x02);
                    }
                    finally
                    {
                        fpdfview.FPDFBitmapDestroy(bitmap);
                    }
                }
                finally
                {
                    pin.Free();
                }
                return new RenderedPageImage(pixelWidth, pixelHeight, stride, pixels);
            }
            finally
            {
                fpdfview.FPDF_ClosePage(baked);
            }
        }
        finally
        {
            fpdfview.FPDF_CloseDocument(newDoc);
        }
    }

    private void ComposeCore(IReadOnlyList<ComposedPage> pages, string targetPath)
    {
        if (pages.Count == 0)
            throw new PdfEngineException("Нечего сохранять: список страниц пуст.");

        var newDoc = fpdf_edit.FPDF_CreateNewDocument();
        if (newDoc == null || newDoc.__Instance == IntPtr.Zero)
            throw new PdfEngineException("Не удалось создать новый документ.");

        try
        {
            // Дубликаты одной исходной страницы в ОДНОМ вызове FPDF_ImportPages
            // получают общий клон косвенного массива /Annots (единая карта
            // объектов pdfium): удаление аннотаций с одной копии мутировало бы
            // и вторую. Такие страницы импортируются изолированными вызовами.
            var keyCounts = new Dictionary<(PdfiumDocumentHandle, int), int>();
            var removalKeys = new HashSet<(PdfiumDocumentHandle, int)>();
            foreach (var page in pages)
            {
                var key = ((PdfiumDocumentHandle)page.Source, page.SourcePageIndex);
                keyCounts[key] = keyCounts.TryGetValue(key, out var n) ? n + 1 : 1;
                if (page.RemovedAnnotations is { Count: > 0 })
                    removalKeys.Add(key);
            }

            bool NeedsIsolation(ComposedPage page)
            {
                var key = ((PdfiumDocumentHandle)page.Source, page.SourcePageIndex);
                return keyCounts[key] > 1 && removalKeys.Contains(key);
            }

            // Последовательные страницы одного источника импортируются одним вызовом:
            // FPDF_ImportPages сохраняет порядок перечисления в диапазоне.
            var insertAt = 0;
            var i = 0;
            while (i < pages.Count)
            {
                var source = (PdfiumDocumentHandle)pages[i].Source;
                int j;
                if (NeedsIsolation(pages[i]))
                {
                    j = i + 1;
                }
                else
                {
                    j = i;
                    while (j < pages.Count && ReferenceEquals(pages[j].Source, source) &&
                           !NeedsIsolation(pages[j]))
                        j++;
                }

                var range = string.Join(",", pages.Skip(i).Take(j - i).Select(p => p.SourcePageIndex + 1));
                if (fpdf_ppo.FPDF_ImportPages(newDoc, source.NativeDoc, range, insertAt) == 0)
                    throw new PdfEngineException($"Не удалось импортировать страницы «{range}».");

                insertAt += j - i;
                i = j;
            }

            SaveWithPageEdits(newDoc, pages, targetPath, saveFlags: 0);
        }
        finally
        {
            fpdfview.FPDF_CloseDocument(newDoc);
        }
    }

    /// <summary>
    /// Поворот, удаление аннотаций и запекание правок для готового набора
    /// страниц документа (страница k соответствует pages[k]) — и запись файла.
    /// Общая часть пересборки и сохранения на месте: оба пути обязаны давать
    /// одинаковую страницу.
    /// </summary>
    private void SaveWithPageEdits(
        FpdfDocumentT doc, IReadOnlyList<ComposedPage> pages, string targetPath, int saveFlags)
    {
        for (var k = 0; k < pages.Count; k++)
        {
            var extra = ((pages[k].ExtraQuarterTurns % 4) + 4) % 4;
            var removed = pages[k].RemovedAnnotations;
            if (extra == 0 && (removed == null || removed.Count == 0)) continue;
            var page = fpdfview.FPDF_LoadPage(doc, k);
            if (page == null || page.__Instance == IntPtr.Zero)
                throw new PdfEngineException($"Не удалось открыть страницу {k + 1} нового документа.");
            try
            {
                if (extra != 0)
                {
                    var current = fpdf_edit.FPDFPageGetRotation(page);
                    fpdf_edit.FPDFPageSetRotation(page, (current + extra) % 4);
                }

                if (removed is { Count: > 0 })
                {
                    // Индексы даны для исходной страницы; после импорта они
                    // совпадают. У заметок Acrobat есть парная Popup-аннотация —
                    // без каскада она осталась бы с висячей ссылкой /Parent.
                    var expanded = new List<int>();
                    foreach (var annotIndex in removed.Distinct())
                    {
                        expanded.Add(annotIndex);
                        var annot = fpdf_annot.FPDFPageGetAnnot(page, annotIndex);
                        if (annot == null || annot.__Instance == IntPtr.Zero)
                            continue;
                        try
                        {
                            var popup = fpdf_annot.FPDFAnnotGetLinkedAnnot(annot, "Popup");
                            if (popup != null && popup.__Instance != IntPtr.Zero)
                            {
                                try
                                {
                                    var popupIndex = fpdf_annot.FPDFPageGetAnnotIndex(page, popup);
                                    if (popupIndex >= 0)
                                        expanded.Add(popupIndex);
                                }
                                finally
                                {
                                    fpdf_annot.FPDFPageCloseAnnot(popup);
                                }
                            }
                        }
                        finally
                        {
                            fpdf_annot.FPDFPageCloseAnnot(annot);
                        }
                    }

                    // Удаление от большего к меньшему — индексы оставшихся
                    // не сдвигаются под ногами.
                    foreach (var annotIndex in expanded.Distinct().OrderByDescending(i => i))
                    {
                        if (fpdf_annot.FPDFPageRemoveAnnot(page, annotIndex) == 0)
                            throw new PdfEngineException(
                                $"Не удалось удалить аннотацию {annotIndex} со страницы {k + 1}.");
                    }
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }

        // Наложенный контент (новый текст, изображения) запекается после
        // установки поворотов: координаты оверлеев заданы в итоговой
        // отображаемой ориентации.
        // Один кэш на документ: гарнитура, использованная на пяти
        // страницах, обязана встроиться в файл один раз, а не пять.
        var overlayFonts = new OverlayFontCache(doc, Fonts);
        try
        {
            for (var k = 0; k < pages.Count; k++)
            {
                var overlays = pages[k].Overlays;
                if (overlays == null || overlays.Count == 0) continue;

                var page = fpdfview.FPDF_LoadPage(doc, k);
                if (page == null || page.__Instance == IntPtr.Zero)
                    throw new PdfEngineException($"Не удалось открыть страницу {k + 1} нового документа.");
                try
                {
                    PdfiumOverlayWriter.ApplyOverlays(doc, page, overlayFonts, overlays,
                        ((pages[k].ExtraQuarterTurns % 4) + 4) % 4);
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }

            SaveDocument(doc, targetPath, saveFlags);
        }
        finally
        {
            overlayFonts.CloseAll();
        }
    }

    /// <summary>FPDF_INCREMENTAL: изменения дописываются к исходным байтам — подписи остаются целы.</summary>
    private const int SaveIncremental = 1;

    /// <summary>FPDF_REMOVE_SECURITY: копия без шифрования (для перешифровки другим паролем).</summary>
    private const int SaveRemoveSecurity = 3;

    public Task SaveCompositionInPlaceAsync(
        IPdfDocumentHandle workingCopy, IPdfDocumentHandle baseSource,
        IReadOnlyList<ComposedPage> composition, string targetPath,
        InPlaceSaveOptions options, CancellationToken ct) =>
        _thread.InvokeAsync(() => SaveInPlaceCore(
            (PdfiumDocumentHandle)workingCopy, (PdfiumDocumentHandle)baseSource,
            composition, targetPath, options, ct), ct);

    /// <summary>
    /// Сохранение правок В КОПИИ исходного документа, а не сборкой нового.
    ///
    /// Пересборка через импорт страниц в пустой документ теряла всё, что
    /// живёт вне страниц: пароль, закладки, форму, вложения, свойства,
    /// именованные цели. Здесь же копия базового документа правится на месте:
    /// его страницы переиспользуются (закладки и поля указывают на них же),
    /// лишние удаляются, вставленные и повторённые импортируются, порядок
    /// выстраивается перестановкой. Сам открытый документ при этом не
    /// трогается вовсе — копия одноразовая, и неудача записи не может
    /// испортить то, что пользователь видит и правит.
    /// </summary>
    private void SaveInPlaceCore(
        PdfiumDocumentHandle working, PdfiumDocumentHandle baseSource,
        IReadOnlyList<ComposedPage> pages, string targetPath, InPlaceSaveOptions options, CancellationToken ct)
    {
        if (pages.Count == 0)
            throw new PdfEngineException("Nothing to save: the page list is empty.");

        var doc = working.NativeDoc;
        var originalCount = fpdfview.FPDF_GetPageCount(doc);
        var used = new bool[originalCount];
        var reused = new int[pages.Count];
        for (var j = 0; j < pages.Count; j++)
        {
            var index = pages[j].SourcePageIndex;
            if (ReferenceEquals(pages[j].Source, baseSource) && index >= 0 && index < originalCount && !used[index])
            {
                used[index] = true;
                reused[j] = index;
            }
            else
            {
                reused[j] = -1;
            }
        }

        // 1. Чужие страницы и повторы своих — импортом в конец, по одной: так
        // у копий одной страницы не окажется общего массива аннотаций.
        var imported = new int[pages.Count];
        for (var j = 0; j < pages.Count; j++)
        {
            if (reused[j] >= 0) continue;
            ct.ThrowIfCancellationRequested();
            var source = (PdfiumDocumentHandle)pages[j].Source;
            var at = fpdfview.FPDF_GetPageCount(doc);
            var range = (pages[j].SourcePageIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (fpdf_ppo.FPDF_ImportPages(doc, source.NativeDoc, range, at) == 0)
                throw new PdfEngineException($"Page {range} could not be imported.");
            imported[j] = at;
        }

        // 2. Страницы оригинала, которых в документе больше нет, — удаляются
        // с конца, чтобы индексы оставшихся не сдвигались под ногами.
        var removedBefore = new int[originalCount + 1];
        for (var i = 0; i < originalCount; i++)
            removedBefore[i + 1] = removedBefore[i] + (used[i] ? 0 : 1);
        for (var i = originalCount - 1; i >= 0; i--)
        {
            if (!used[i])
                fpdf_edit.FPDFPageDelete(doc, i);
        }

        // 3. Порядок: где сейчас стоит страница для каждой позиции итога.
        var current = new int[pages.Count];
        var inOrder = true;
        for (var j = 0; j < pages.Count; j++)
        {
            current[j] = reused[j] >= 0
                ? reused[j] - removedBefore[reused[j]]
                : imported[j] - removedBefore[originalCount];
            inOrder &= current[j] == j;
        }
        if (fpdfview.FPDF_GetPageCount(doc) != pages.Count)
            throw new PdfEngineException("Page count after editing does not match the document.");
        if (!inOrder && fpdf_edit.FPDF_MovePages(doc, ref current[0], (ulong)current.Length, 0) == 0)
            throw new PdfEngineException("Pages could not be put in order.");

        // 4. Повороты, удалённые аннотации, правки — тем же кодом, что и при
        // пересборке, и запись.
        var flags = options.RemoveSecurity ? SaveRemoveSecurity : options.Incremental ? SaveIncremental : 0;
        SaveWithPageEdits(doc, pages, targetPath, flags);
    }

    public Task CreateImageDocumentAsync(IReadOnlyList<ImagePageSpec> pages, string targetPath, CancellationToken ct) =>
        _thread.InvokeAsync(() => CreateImageDocumentCore(pages, targetPath), ct);

    public Task<ImageRecompressStats> RecompressImagesAsync(
        string sourcePath, string? password, string targetPath, double targetDpi,
        EncodeImage encode, CancellationToken ct) =>
        RecompressImagesAsync(sourcePath, password, targetPath, targetDpi, 75, encode, ct);

    public Task<ImageRecompressStats> RecompressImagesAsync(
        string sourcePath, string? password, string targetPath, double targetDpi,
        int quality, EncodeImage encode, CancellationToken ct) =>
        _thread.InvokeAsync(
            () => PdfiumImageRecompressor.RecompressCore(
                sourcePath, password, targetPath, targetDpi, quality, encode, ct), ct);

    public Task<ScanEnhanceStats> EnhanceScansAsync(
        string sourcePath, string? password, string targetPath,
        ScanEnhanceOptions options, IProgress<int>? progress, CancellationToken ct) =>
        _thread.InvokeAsync(
            () => PdfiumScanEnhancer.EnhanceCore(sourcePath, password, targetPath, options, progress, ct), ct);

    public Task<IReadOnlyList<PageSkew>> MeasureSkewAsync(
        string sourcePath, string? password, IReadOnlyList<int>? pages, CancellationToken ct) =>
        _thread.InvokeAsync(
            () => PdfiumScanEnhancer.MeasureSkewCore(sourcePath, password, pages, ct), ct);

    private void CreateImageDocumentCore(IReadOnlyList<ImagePageSpec> pages, string targetPath)
    {
        if (pages.Count == 0)
            throw new PdfEngineException("Нет изображений для сборки PDF.");

        var newDoc = fpdf_edit.FPDF_CreateNewDocument();
        if (newDoc == null || newDoc.__Instance == IntPtr.Zero)
            throw new PdfEngineException("Не удалось создать новый документ.");

        try
        {
            for (var i = 0; i < pages.Count; i++)
            {
                var spec = pages[i];
                if (spec.PixelWidth < 1 || spec.PixelHeight < 1 ||
                    !(spec.WidthPoints > 1) || !(spec.HeightPoints > 1) ||
                    spec.Bgra.Length < (long)spec.PixelWidth * spec.PixelHeight * 4)
                    throw new PdfEngineException($"Некорректное изображение для страницы {i + 1}.");

                var page = fpdf_edit.FPDFPageNew(newDoc, i, spec.WidthPoints, spec.HeightPoints);
                if (page == null || page.__Instance == IntPtr.Zero)
                    throw new PdfEngineException($"Не удалось создать страницу {i + 1}.");
                try
                {
                    var overlay = new ImageOverlay(
                        spec.Bgra, spec.PixelWidth, spec.PixelHeight,
                        0, 0, spec.WidthPoints, spec.HeightPoints, spec.EncodedJpeg);
                    // Здесь оверлей только растровый, шрифт не понадобится, но
                    // кэш всё равно передаётся: пустой он ничего не грузит.
                    PdfiumOverlayWriter.ApplyOverlays(
                        newDoc, page, new OverlayFontCache(newDoc, Fonts), new PageOverlay[] { overlay }, 0);
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }

            SaveDocument(newDoc, targetPath);
        }
        finally
        {
            fpdfview.FPDF_CloseDocument(newDoc);
        }
    }

    internal static void SaveDocument(FpdfDocumentT doc, string targetPath, int flags = 0)
    {
        using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        Exception? writeError = null;

        var writer = new FPDF_FILEWRITE_ { Version = 1 };
        var writeBlock = new PDFiumCore.Delegates.Func_int___IntPtr___IntPtr_ulong((_, data, size) =>
        {
            try
            {
                var remaining = (long)size;
                var offset = 0L;
                var buffer = new byte[(int)Math.Min(remaining, 81920)];
                while (remaining > 0)
                {
                    var chunk = (int)Math.Min(remaining, buffer.Length);
                    Marshal.Copy(data + (nint)offset, buffer, 0, chunk);
                    output.Write(buffer, 0, chunk);
                    offset += chunk;
                    remaining -= chunk;
                }
                return 1;
            }
            catch (Exception ex)
            {
                writeError = ex;
                return 0; // PDFium прервёт сохранение.
            }
        });
        writer.WriteBlock = writeBlock;

        var ok = fpdf_save.FPDF_SaveAsCopy(doc, writer, (uint)flags);
        GC.KeepAlive(writeBlock);

        if (writeError != null)
            throw new PdfEngineException("Ошибка записи файла.", writeError);
        if (ok == 0)
            throw new PdfEngineException("PDFium не смог сохранить документ.");

        output.Flush(flushToDisk: true);
    }

    // Рендер выполняется на уровне PdfiumDocumentHandle.RenderCore: он же
    // отвечает за дорисовку полей форм и жизненный цикл активной страницы.

    public ValueTask DisposeAsync()
    {
        // Строго синхронно: DisposeAsync вызывается из OnExit через
        // GetAwaiter().GetResult() на UI-потоке — любой await с возвратом на
        // диспетчер дал бы deadlock и оставил процесс-зомби без окон
        // (именно так и происходило до этого фикса).
        _thread.Dispose(); // общий поток фоновый и завершается вместе с процессом
        return ValueTask.CompletedTask;
    }
}
