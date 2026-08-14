using System.IO.MemoryMappedFiles;
using NexusPdf.Pdf.Abstractions;
using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

internal sealed class PdfiumDocumentHandle : IPdfDocumentHandle
{
    private readonly PdfiumThread _thread;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
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
        ThrowIfDisposed();
        return _thread.InvokeAsync(
            () => PdfiumRenderEngine.RenderCore(NativeDoc, pageIndex, pixelWidth, pixelHeight, extraQuarterTurns), ct);
    }

    public Task<string> GetPageTextAsync(int pageIndex, CancellationToken ct)
    {
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _thread.InvokeAsync(() =>
        {
            fpdfview.FPDF_CloseDocument(NativeDoc);
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _accessor.Dispose();
            _mmf.Dispose();
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
