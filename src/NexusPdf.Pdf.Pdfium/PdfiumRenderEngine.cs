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
            // Последовательные страницы одного источника импортируются одним вызовом:
            // FPDF_ImportPages сохраняет порядок перечисления в диапазоне.
            var insertAt = 0;
            var i = 0;
            while (i < pages.Count)
            {
                var source = (PdfiumDocumentHandle)pages[i].Source;
                var j = i;
                while (j < pages.Count && ReferenceEquals(pages[j].Source, source))
                    j++;

                var range = string.Join(",", pages.Skip(i).Take(j - i).Select(p => p.SourcePageIndex + 1));
                if (fpdf_ppo.FPDF_ImportPages(newDoc, source.NativeDoc, range, insertAt) == 0)
                    throw new PdfEngineException($"Не удалось импортировать страницы «{range}».");

                insertAt += j - i;
                i = j;
            }

            for (var k = 0; k < pages.Count; k++)
            {
                var extra = ((pages[k].ExtraQuarterTurns % 4) + 4) % 4;
                if (extra == 0) continue;
                var page = fpdfview.FPDF_LoadPage(newDoc, k);
                if (page == null || page.__Instance == IntPtr.Zero)
                    throw new PdfEngineException($"Не удалось открыть страницу {k + 1} нового документа.");
                try
                {
                    var current = fpdf_edit.FPDFPageGetRotation(page);
                    fpdf_edit.FPDFPageSetRotation(page, (current + extra) % 4);
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

                    if (overlayFont == null && overlays.Any(o => o is TextOverlay))
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

    internal static RenderedPageImage RenderCore(FpdfDocumentT doc, int pageIndex, int width, int height, int extraQuarterTurns)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width), "Размер растра должен быть положительным.");

        var page = fpdfview.FPDF_LoadPage(doc, pageIndex);
        if (page == null || page.__Instance == IntPtr.Zero)
            throw new PdfEngineException($"Не удалось открыть страницу {pageIndex + 1}.");

        try
        {
            var stride = width * 4;
            var pixels = new byte[stride * height];
            var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var bitmap = fpdfview.FPDFBitmapCreateEx(width, height, FpdfBitmapBgra, pin.AddrOfPinnedObject(), stride);
                if (bitmap == null || bitmap.__Instance == IntPtr.Zero)
                    throw new PdfEngineException("Не удалось создать растровый буфер.");
                try
                {
                    fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, width, height, 0xFFFFFFFFUL);
                    fpdfview.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height,
                        ((extraQuarterTurns % 4) + 4) % 4, RenderFlagAnnot | RenderFlagLcdText);
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
            return new RenderedPageImage(width, height, stride, pixels);
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        _thread.Dispose();
    }
}
