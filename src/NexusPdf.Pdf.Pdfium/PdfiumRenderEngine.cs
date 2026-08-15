using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using NexusPdf.Pdf.Abstractions;
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
                new PdfDocumentInfo(pageCount, pages), mmf, accessor);
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

    private static void ComposeCore(IReadOnlyList<ComposedPage> pages, string targetPath)
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

            for (var k = 0; k < pages.Count; k++)
            {
                var extra = ((pages[k].ExtraQuarterTurns % 4) + 4) % 4;
                var removed = pages[k].RemovedAnnotations;
                if (extra == 0 && (removed == null || removed.Count == 0)) continue;
                var page = fpdfview.FPDF_LoadPage(newDoc, k);
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
            FpdfFontT? overlayFont = null;
            try
            {
                for (var k = 0; k < pages.Count; k++)
                {
                    var overlays = pages[k].Overlays;
                    if (overlays == null || overlays.Count == 0) continue;

                    if (overlayFont == null && overlays.Any(o => o is TextOverlay or OcrTextLayerOverlay))
                    {
                        overlayFont = PdfiumOverlayWriter.LoadOverlayFont(newDoc);
                    }

                    var page = fpdfview.FPDF_LoadPage(newDoc, k);
                    if (page == null || page.__Instance == IntPtr.Zero)
                        throw new PdfEngineException($"Не удалось открыть страницу {k + 1} нового документа.");
                    try
                    {
                        PdfiumOverlayWriter.ApplyOverlays(newDoc, page, overlayFont, overlays,
                            ((pages[k].ExtraQuarterTurns % 4) + 4) % 4);
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
                if (overlayFont != null)
                    fpdf_edit.FPDFFontClose(overlayFont);
            }
        }
        finally
        {
            fpdfview.FPDF_CloseDocument(newDoc);
        }
    }

    public Task CreateImageDocumentAsync(IReadOnlyList<ImagePageSpec> pages, string targetPath, CancellationToken ct) =>
        _thread.InvokeAsync(() => CreateImageDocumentCore(pages, targetPath), ct);

    private static void CreateImageDocumentCore(IReadOnlyList<ImagePageSpec> pages, string targetPath)
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
                        0, 0, spec.WidthPoints, spec.HeightPoints);
                    PdfiumOverlayWriter.ApplyOverlays(
                        newDoc, page, null, new PageOverlay[] { overlay }, 0);
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

    internal static void SaveDocument(FpdfDocumentT doc, string targetPath)
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

        var ok = fpdf_save.FPDF_SaveAsCopy(doc, writer, 0);
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
