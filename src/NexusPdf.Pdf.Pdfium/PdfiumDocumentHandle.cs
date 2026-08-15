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
    private PdfiumFormSession? _forms; // читается/меняется только на PDFium-потоке

    private const int FpdfBitmapBgra = 4;
    private const int RenderFlagAnnot = 0x01;
    private const int RenderFlagLcdText = 0x02;

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
                () => RenderCore(pageIndex, pixelWidth, pixelHeight, extraQuarterTurns, contentOnly: false), ct);
        }
    }

    public Task<RenderedPageImage> RenderPageContentOnlyAsync(int pageIndex, int pixelWidth, int pixelHeight, int extraQuarterTurns, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync(
                () => RenderCore(pageIndex, pixelWidth, pixelHeight, extraQuarterTurns, contentOnly: true), ct);
        }
    }

    /// <summary>
    /// Рендер страницы; при активном окружении форм поверх содержимого
    /// дорисовываются поля (FFLDraw). Активная интерактивная страница формы
    /// переиспользуется без закрытия — иначе каждый ре-рендер убивал бы фокус.
    /// contentOnly — рендер БЕЗ аннотаций и полей форм (растр для OCR: текст
    /// штампов/полей не является содержимым страницы и не должен запекаться).
    /// </summary>
    private RenderedPageImage RenderCore(int pageIndex, int width, int height, int extraQuarterTurns, bool contentOnly)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width), "Размер растра должен быть положительным.");

        var rotate = ((extraQuarterTurns % 4) + 4) % 4;
        var activePage = _forms?.TryGetActivePage(pageIndex);
        var page = activePage ?? fpdfview.FPDF_LoadPage(NativeDoc, pageIndex);
        if (page == null || page.__Instance == IntPtr.Zero)
            throw new PdfEngineException($"Не удалось открыть страницу {pageIndex + 1}.");
        var transient = activePage == null;
        if (transient && _forms is { IsActive: true })
            _forms.OnTransientPageLoaded(page);

        try
        {
            var stride = width * 4;
            var pixels = new byte[stride * height];
            var pin = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                var bitmap = fpdfview.FPDFBitmapCreateEx(width, height, FpdfBitmapBgra, pin.AddrOfPinnedObject(), stride);
                if (bitmap == null || bitmap.__Instance == IntPtr.Zero)
                    throw new PdfEngineException("Не удалось создать растровый буфер.");
                try
                {
                    var flags = contentOnly ? RenderFlagLcdText : RenderFlagAnnot | RenderFlagLcdText;
                    fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, width, height, 0xFFFFFFFFUL);
                    fpdfview.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, rotate, flags);
                    if (!contentOnly)
                        _forms?.DrawFields(bitmap, page, width, height, rotate, flags);
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
            if (transient)
            {
                if (_forms is { IsActive: true })
                    _forms.OnTransientPageClosing(page);
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    public Task<PdfDocumentMetadata> GetMetadataAsync(CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync(() =>
            {
                var version = 0;
                var versionText = fpdfview.FPDF_GetFileVersion(NativeDoc, ref version) != 0 && version > 0
                    ? $"{version / 10}.{version % 10}"
                    : "";
                // Ревизия security handler есть только у зашифрованных файлов —
                // ловит и файлы с одним owner-паролем, открытые без пароля.
                var encrypted = fpdfview.FPDF_GetSecurityHandlerRevision(NativeDoc) >= 0;
                return new PdfDocumentMetadata(
                    versionText, encrypted,
                    GetMetaText("Title"), GetMetaText("Author"), GetMetaText("Subject"),
                    GetMetaText("Creator"), GetMetaText("Producer"),
                    GetMetaText("CreationDate"), GetMetaText("ModDate"));
            }, ct);
        }
    }

    private string GetMetaText(string tag)
    {
        // Два вызова: длина в байтах UTF-16LE (включая NUL), затем данные.
        var length = fpdf_doc.FPDF_GetMetaText(NativeDoc, tag, IntPtr.Zero, 0);
        if (length <= 2)
            return "";
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)length);
        try
        {
            fpdf_doc.FPDF_GetMetaText(NativeDoc, tag, buffer, length);
            return System.Runtime.InteropServices.Marshal
                .PtrToStringUni(buffer, (int)(length / 2) - 1) ?? "";
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    // ----- Формы -----

    public Task<int> GetFormTypeAsync(CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync(() => fpdf_formfill.FPDF_GetFormType(NativeDoc), ct);
        }
    }

    public Task<bool> InitFormsAsync(CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync(() =>
            {
                _forms ??= PdfiumFormSession.Create(NativeDoc);
                return _forms != null;
            }, ct);
        }
    }

    public Task FormClickAsync(int pageIndex, int extraQuarterTurns, double xPt, double yPt, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync(() =>
            {
                if (_forms == null) return;
                var size = Info.Pages[pageIndex];
                var displayedW = extraQuarterTurns % 2 == 0 ? size.WidthPoints : size.HeightPoints;
                var displayedH = extraQuarterTurns % 2 == 0 ? size.HeightPoints : size.WidthPoints;
                _forms.Click(pageIndex, extraQuarterTurns, xPt, yPt, displayedW, displayedH);
            }, ct);
        }
    }

    public Task<PdfComboInfo?> GetFormComboAtAsync(
        int pageIndex, int extraQuarterTurns, double xPt, double yPt, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync(() =>
            {
                if (_forms == null) return null;
                var size = Info.Pages[pageIndex];
                var displayedW = extraQuarterTurns % 2 == 0 ? size.WidthPoints : size.HeightPoints;
                var displayedH = extraQuarterTurns % 2 == 0 ? size.HeightPoints : size.WidthPoints;
                return _forms.GetComboAt(pageIndex, extraQuarterTurns, xPt, yPt, displayedW, displayedH);
            }, ct);
        }
    }

    public Task SetFormComboSelectionAsync(
        int pageIndex, int extraQuarterTurns, double xPt, double yPt, int optionIndex, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync(() =>
            {
                if (_forms == null) return;
                var size = Info.Pages[pageIndex];
                var displayedW = extraQuarterTurns % 2 == 0 ? size.WidthPoints : size.HeightPoints;
                var displayedH = extraQuarterTurns % 2 == 0 ? size.HeightPoints : size.WidthPoints;
                _forms.SetComboSelection(pageIndex, extraQuarterTurns, xPt, yPt, displayedW, displayedH, optionIndex);
            }, ct);
        }
    }

    public Task FormCharAsync(char character, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync(() => _forms?.Char(character), ct);
        }
    }

    public Task FormKeyDownAsync(int virtualKeyCode, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync(() => _forms?.KeyDown(virtualKeyCode), ct);
        }
    }

    public Task FormKillFocusAsync(CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync(() => _forms?.KillFocus(), ct);
        }
    }

    public Task FormEndAsync(CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync(() =>
            {
                // Dispose фиксирует значение (DeactivatePage → KillFocus) и
                // закрывает окружение — подсветка исчезает из рендеров.
                // Заполненные значения остаются видимыми: pdfium сгенерировал
                // appearance-стримы виджетов, их рисует обычный FPDF_ANNOT.
                _forms?.Dispose();
                _forms = null;
            }, ct);
        }
    }

    public Task SaveCurrentAsync(string targetPath, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync(() =>
            {
                _forms?.KillFocus(); // зафиксировать значение редактируемого поля
                PdfiumRenderEngine.SaveDocument(NativeDoc, targetPath);
            }, ct);
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

    /// <summary>Отображаемые пункты (от левого верхнего угла) → координаты страницы PDF.</summary>
    private (double X, double Y) DisplayedToPage(
        FpdfPageT page, int pageIndex, int extraQuarterTurns, double xPt, double yPt)
    {
        var size = Info.Pages[pageIndex];
        var rotate = ((extraQuarterTurns % 4) + 4) % 4;
        var displayedW = rotate % 2 == 0 ? size.WidthPoints : size.HeightPoints;
        var displayedH = rotate % 2 == 0 ? size.HeightPoints : size.WidthPoints;
        double pageX = 0, pageY = 0;
        fpdfview.FPDF_DeviceToPage(page, 0, 0,
            (int)Math.Round(displayedW), (int)Math.Round(displayedH), rotate,
            (int)Math.Round(xPt), (int)Math.Round(yPt), ref pageX, ref pageY);
        return (pageX, pageY);
    }

    public Task<int> GetCharIndexAtAsync(
        int pageIndex, int extraQuarterTurns, double xPt, double yPt, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync(() =>
            {
                var page = fpdfview.FPDF_LoadPage(NativeDoc, pageIndex);
                if (page == null || page.__Instance == IntPtr.Zero)
                    return -1;
                try
                {
                    var textPage = fpdf_text.FPDFTextLoadPage(page);
                    if (textPage == null || textPage.__Instance == IntPtr.Zero)
                        return -1;
                    try
                    {
                        var (px, py) = DisplayedToPage(page, pageIndex, extraQuarterTurns, xPt, yPt);
                        // Допуск ~половина строки: клик редко попадает точно в глиф.
                        return fpdf_text.FPDFTextGetCharIndexAtPos(textPage, px, py, 6.0, 6.0);
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
    }

    public Task<PdfLinkInfo?> GetLinkAtAsync(
        int pageIndex, int extraQuarterTurns, double xPt, double yPt, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync<PdfLinkInfo?>(() =>
            {
                var page = fpdfview.FPDF_LoadPage(NativeDoc, pageIndex);
                if (page == null || page.__Instance == IntPtr.Zero)
                    return null;
                try
                {
                    var (px, py) = DisplayedToPage(page, pageIndex, extraQuarterTurns, xPt, yPt);
                    var link = fpdf_doc.FPDFLinkGetLinkAtPoint(page, px, py);
                    if (link == null || link.__Instance == IntPtr.Zero)
                        return null;
                    return DescribeLink(link);
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }, ct);
        }
    }

    public Task<IReadOnlyList<PdfPageLink>> GetPageLinksAsync(int pageIndex, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync<IReadOnlyList<PdfPageLink>>(() =>
            {
                var page = fpdfview.FPDF_LoadPage(NativeDoc, pageIndex);
                if (page == null || page.__Instance == IntPtr.Zero)
                    return Array.Empty<PdfPageLink>();
                try
                {
                    // Перечисляем Link-аннотации страницы и разрешаем каждую
                    // через FPDFLink_GetLinkAtPoint в центре её рамки: так
                    // используются только те вызовы, которые уже проверены
                    // тестами, без ручного конструирования нативных хэндлов.
                    var links = new List<PdfPageLink>();
                    var annotCount = fpdf_annot.FPDFPageGetAnnotCount(page);
                    for (var i = 0; i < annotCount; i++)
                    {
                        var annot = fpdf_annot.FPDFPageGetAnnot(page, i);
                        if (annot == null || annot.__Instance == IntPtr.Zero)
                            continue;
                        try
                        {
                            if (fpdf_annot.FPDFAnnotGetSubtype(annot) != AnnotSubtypeLink)
                                continue;
                            var rect = new FS_RECTF_();
                            if (fpdf_annot.FPDFAnnotGetRect(annot, rect) == 0)
                                continue;
                            var link = fpdf_doc.FPDFLinkGetLinkAtPoint(page,
                                (rect.Left + rect.Right) / 2.0, (rect.Top + rect.Bottom) / 2.0);
                            if (link == null || link.__Instance == IntPtr.Zero)
                                continue;
                            var info = DescribeLink(link);
                            if (info == null)
                                continue;
                            links.Add(new PdfPageLink(
                                new PdfTextRect(
                                    Math.Min(rect.Left, rect.Right), Math.Max(rect.Top, rect.Bottom),
                                    Math.Max(rect.Left, rect.Right), Math.Min(rect.Top, rect.Bottom)),
                                info.Uri, info.TargetPageIndex));
                        }
                        finally
                        {
                            fpdf_annot.FPDFPageCloseAnnot(annot);
                        }
                    }
                    return links;
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }, ct);
        }
    }

    /// <summary>Назначение ссылки: страница документа или внешний адрес. null — не поддерживается.</summary>
    private PdfLinkInfo? DescribeLink(FpdfLinkT link)
    {
        var dest = fpdf_doc.FPDFLinkGetDest(NativeDoc, link);
        if (dest != null && dest.__Instance != IntPtr.Zero)
        {
            var target = fpdf_doc.FPDFDestGetDestPageIndex(NativeDoc, dest);
            if (target >= 0)
                return new PdfLinkInfo(null, target);
        }

        var action = fpdf_doc.FPDFLinkGetAction(link);
        if (action == null || action.__Instance == IntPtr.Zero)
            return null;
        switch (fpdf_doc.FPDFActionGetType(action))
        {
            case ActionTypeGoto:
            {
                var actionDest = fpdf_doc.FPDFActionGetDest(NativeDoc, action);
                if (actionDest == null || actionDest.__Instance == IntPtr.Zero)
                    return null;
                var target = fpdf_doc.FPDFDestGetDestPageIndex(NativeDoc, actionDest);
                return target >= 0 ? new PdfLinkInfo(null, target) : null;
            }
            case ActionTypeUri:
            {
                var uri = GetActionUri(action);
                return uri.Length > 0 ? new PdfLinkInfo(uri, -1) : null;
            }
            default:
                // Launch и Remote-Goto намеренно НЕ поддерживаются: запуск
                // внешних файлов из документа небезопасен.
                return null;
        }
    }

    private const int ActionTypeGoto = 1;   // PDFACTION_GOTO
    private const int ActionTypeUri = 3;    // PDFACTION_URI
    private const int AnnotSubtypeLink = 2; // FPDF_ANNOT_LINK

    private string GetActionUri(FpdfActionT action)
    {
        var length = fpdf_doc.FPDFActionGetURIPath(NativeDoc, action, IntPtr.Zero, 0);
        if (length <= 1)
            return "";
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)length);
        try
        {
            fpdf_doc.FPDFActionGetURIPath(NativeDoc, action, buffer, length);
            // URI хранится однобайтовой строкой с завершающим нулём.
            return System.Runtime.InteropServices.Marshal
                .PtrToStringAnsi(buffer, (int)length - 1) ?? "";
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
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
                            GetAnnotString(annot, "T"),
                            GetAnnotString(annot, "V")));
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
                _forms?.Dispose();
                _forms = null;
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
