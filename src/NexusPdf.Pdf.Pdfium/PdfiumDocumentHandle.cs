using System.IO.MemoryMappedFiles;
using NexusPdf.Pdf.Abstractions;
using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

internal sealed class PdfiumDocumentHandle : IPdfDocumentHandle
{
    private readonly PdfiumThread _thread;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;

    // Допуск операции и постановка FPDF_CloseDocument в очередь защищены одним
    // замком: очередь PdfiumThread — FIFO, поэтому закрытие документа всегда
    // встаёт ПОСЛЕ всех уже допущенных операций — нативный use-after-free
    // (рендер после close) исключён даже при гонке Dispose с фоновым рендером.
    private readonly object _admissionGate = new();
    private bool _disposed;

    internal PdfiumDocumentHandle(
        PdfiumRenderEngine engine,
        PdfiumThread thread,
        string filePath,
        FpdfDocumentT nativeDoc,
        PdfDocumentInfo info,
        MemoryMappedFile mmf,
        MemoryMappedViewAccessor accessor)
    {
        _thread = thread;
        FilePath = filePath;
        NativeDoc = nativeDoc;
        Info = info;
        _mmf = mmf;
        _accessor = accessor;
    }

    public string FilePath { get; }
    public PdfDocumentInfo Info { get; }
    internal FpdfDocumentT NativeDoc { get; }

    public Task<RenderedPageImage> RenderPageAsync(int pageIndex, int pixelWidth, int pixelHeight, int extraQuarterTurns, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync(
                () => PdfiumRenderEngine.RenderCore(NativeDoc, pageIndex, pixelWidth, pixelHeight, extraQuarterTurns), ct);
        }
    }

    public Task<string> GetPageTextAsync(int pageIndex, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return GetPageTextCore(pageIndex, ct);
        }
    }

    private Task<string> GetPageTextCore(int pageIndex, CancellationToken ct)
    {
        return _thread.InvokeAsync(() =>
        {
            var page = fpdfview.FPDF_LoadPage(NativeDoc, pageIndex);
            if (page == null || page.__Instance == IntPtr.Zero)
                throw new PdfEngineException($"Не удалось открыть страницу {pageIndex + 1}.");
            try
            {
                var textPage = fpdf_text.FPDFTextLoadPage(page);
                if (textPage == null || textPage.__Instance == IntPtr.Zero)
                    return string.Empty;
                try
                {
                    var count = fpdf_text.FPDFTextCountChars(textPage);
                    if (count <= 0)
                        return string.Empty;
                    var buffer = new ushort[count + 1];
                    var written = fpdf_text.FPDFTextGetText(textPage, 0, count, ref buffer[0]);
                    if (written <= 1)
                        return string.Empty;
                    var chars = new char[written - 1];
                    for (var i = 0; i < written - 1; i++)
                        chars[i] = (char)buffer[i];
                    return new string(chars);
                }
                finally
                {
                    fpdf_text.FPDFTextClosePage(textPage);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }, ct);
    }

    public Task<IReadOnlyList<PdfTextRect>> GetTextRectsAsync(int pageIndex, int startCharIndex, int charCount, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return GetTextRectsCore(pageIndex, startCharIndex, charCount, ct);
        }
    }

    private Task<IReadOnlyList<PdfTextRect>> GetTextRectsCore(int pageIndex, int startCharIndex, int charCount, CancellationToken ct)
    {
        return _thread.InvokeAsync<IReadOnlyList<PdfTextRect>>(() =>
        {
            var page = fpdfview.FPDF_LoadPage(NativeDoc, pageIndex);
            if (page == null || page.__Instance == IntPtr.Zero)
                throw new PdfEngineException($"Не удалось открыть страницу {pageIndex + 1}.");
            try
            {
                var textPage = fpdf_text.FPDFTextLoadPage(page);
                if (textPage == null || textPage.__Instance == IntPtr.Zero)
                    return Array.Empty<PdfTextRect>();
                try
                {
                    var rectCount = fpdf_text.FPDFTextCountRects(textPage, startCharIndex, charCount);
                    var rects = new List<PdfTextRect>(Math.Max(0, rectCount));
                    for (var i = 0; i < rectCount; i++)
                    {
                        double left = 0, top = 0, right = 0, bottom = 0;
                        fpdf_text.FPDFTextGetRect(textPage, i, ref left, ref top, ref right, ref bottom);
                        rects.Add(new PdfTextRect(left, top, right, bottom));
                    }
                    return rects;
                }
                finally
                {
                    fpdf_text.FPDFTextClosePage(textPage);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }, ct);
    }

    public Task<IReadOnlyList<PdfAnnotationInfo>> GetAnnotationsAsync(int pageIndex, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return GetAnnotationsCore(pageIndex, ct);
        }
    }

    private Task<IReadOnlyList<PdfAnnotationInfo>> GetAnnotationsCore(int pageIndex, CancellationToken ct)
    {
        const int subtypeLink = 2;
        const int subtypePopup = 16;

        return _thread.InvokeAsync<IReadOnlyList<PdfAnnotationInfo>>(() =>
        {
            var page = fpdfview.FPDF_LoadPage(NativeDoc, pageIndex);
            if (page == null || page.__Instance == IntPtr.Zero)
                throw new PdfEngineException($"Не удалось открыть страницу {pageIndex + 1}.");
            try
            {
                var count = fpdf_annot.FPDFPageGetAnnotCount(page);
                var result = new List<PdfAnnotationInfo>(Math.Max(0, count));
                for (var i = 0; i < count; i++)
                {
                    var annot = fpdf_annot.FPDFPageGetAnnot(page, i);
                    if (annot == null || annot.__Instance == IntPtr.Zero)
                        continue;
                    try
                    {
                        var subtype = fpdf_annot.FPDFAnnotGetSubtype(annot);
                        if (subtype is subtypeLink or subtypePopup)
                            continue;
                        result.Add(new PdfAnnotationInfo(
                            i, subtype,
                            GetAnnotString(annot, "Contents"),
                            GetAnnotString(annot, "T")));
                    }
                    finally
                    {
                        fpdf_annot.FPDFPageCloseAnnot(annot);
                    }
                }
                return result;
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }, ct);
    }

    private static string GetAnnotString(FpdfAnnotationT annot, string key)
    {
        var probe = new ushort[1];
        var bytesNeeded = fpdf_annot.FPDFAnnotGetStringValue(annot, key, ref probe[0], 0);
        if (bytesNeeded <= 2)
            return "";
        var buffer = new ushort[bytesNeeded / 2];
        fpdf_annot.FPDFAnnotGetStringValue(annot, key, ref buffer[0], bytesNeeded);
        var length = Array.IndexOf(buffer, (ushort)0);
        if (length < 0)
            length = buffer.Length;
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = (char)buffer[i];
        return new string(chars);
    }

    public async ValueTask DisposeAsync()
    {
        Task closeTask;
        lock (_admissionGate)
        {
            if (_disposed) return;
            _disposed = true;
            closeTask = _thread.InvokeAsync(() =>
            {
                fpdfview.FPDF_CloseDocument(NativeDoc);
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                _accessor.Dispose();
                _mmf.Dispose();
            }, CancellationToken.None);
        }
        await closeTask.ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
