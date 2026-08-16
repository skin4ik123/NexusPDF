using System.IO.MemoryMappedFiles;
using NexusPdf.Pdf.Abstractions;
using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

internal sealed partial class PdfiumDocumentHandle : IPdfDocumentHandle
{
    private readonly PdfiumThread _thread;
    // У документа, собранного в ПАМЯТИ (страница с применёнными правками),
    // файла за спиной нет — отображение отсутствует, и закрывать нечего.
    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _accessor;

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
        MemoryMappedFile? mmf,
        MemoryMappedViewAccessor? accessor)
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

    public Task<PdfActiveContent> GetActiveContentAsync(CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync(() =>
            {
                var scriptNames = new List<string>();
                var scriptCount = fpdf_javascript.FPDFDocGetJavaScriptActionCount(NativeDoc);
                for (var i = 0; i < scriptCount && i < 50; i++)
                {
                    var action = fpdf_javascript.FPDFDocGetJavaScriptAction(NativeDoc, i);
                    if (action == null || action.__Instance == IntPtr.Zero)
                        continue;
                    try
                    {
                        // В журнал и интерфейс попадает только ИМЯ скрипта:
                        // тело скрипта — содержимое документа, его не показываем.
                        var name = ReadUtf16((buffer, size) =>
                            fpdf_javascript.FPDFJavaScriptActionGetName(action, ref buffer[0], size));
                        scriptNames.Add(name.Length > 0 ? name : $"#{i + 1}");
                    }
                    finally
                    {
                        fpdf_javascript.FPDFDocCloseJavaScriptAction(action);
                    }
                }

                var attachmentNames = new List<string>();
                var attachmentCount = fpdf_attachment.FPDFDocGetAttachmentCount(NativeDoc);
                for (var i = 0; i < attachmentCount && i < 50; i++)
                {
                    var attachment = fpdf_attachment.FPDFDocGetAttachment(NativeDoc, i);
                    if (attachment == null || attachment.__Instance == IntPtr.Zero)
                        continue;
                    var name = ReadUtf16((buffer, size) =>
                        fpdf_attachment.FPDFAttachmentGetName(attachment, ref buffer[0], size));
                    attachmentNames.Add(name.Length > 0 ? name : $"#{i + 1}");
                }

                return new PdfActiveContent(
                    scriptCount, scriptNames,
                    attachmentCount, attachmentNames,
                    CountLaunchActions());
            }, ct);
        }
    }

    // Файлы больше этого размера на Launch-действия не сканируются: это
    // предупреждение, а не проверка целостности, и оно не стоит чтения
    // сотен мегабайт.
    private const long MaxLaunchScanBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Launch-действия (запуск внешней программы по клику). Публичный API
    /// PDFium их не отдаёт: FPDFLink_GetLinkAtPoint для такой ссылки не
    /// возвращает ничего (проверено тестом). Поэтому используется явно
    /// обозначенная эвристика — поиск маркера «/Launch» в байтах файла.
    /// Ложное срабатывание возможно (строка внутри потока), пропуск — при
    /// сжатых потоках объектов; и то и другое безопасно, потому что
    /// программа Launch-действия НИКОГДА не выполняет.
    /// </summary>
    private int CountLaunchActions()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length > MaxLaunchScanBytes)
                return 0;
            var bytes = File.ReadAllBytes(FilePath);
            var needle = "/Launch"u8;
            var found = 0;
            var span = bytes.AsSpan();
            var offset = 0;
            while (offset < span.Length)
            {
                var index = span[offset..].IndexOf(needle);
                if (index < 0)
                    break;
                found++;
                offset += index + needle.Length;
            }
            return found;
        }
        catch (IOException)
        {
            return 0; // файл занят — предупреждение просто не показываем
        }
    }

    /// <summary>Двухпроходное чтение строки UTF-16 из pdfium (сначала длина, потом данные).</summary>
    private static string ReadUtf16(Func<ushort[], ulong, ulong> read)
    {
        var probe = new ushort[1];
        var bytesNeeded = read(probe, 0);
        if (bytesNeeded <= 2)
            return "";
        var buffer = new ushort[bytesNeeded / 2];
        read(buffer, bytesNeeded);
        var length = Array.IndexOf(buffer, (ushort)0);
        if (length < 0)
            length = buffer.Length;
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = (char)buffer[i];
        return new string(chars);
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

    public Task<uint> GetPermissionsAsync(CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            // У незашифрованного документа PDFium возвращает все единицы —
            // это и есть «ограничений нет», а не «всё запрещено».
            // Биндинг отдаёт ulong; значащие только младшие 32 бита.
            return _thread.InvokeAsync(
                () => (uint)fpdfview.FPDF_GetDocPermissions(NativeDoc), ct);
        }
    }

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

    public Task<PdfImageSummary> GetImageSummaryAsync(int maxPages, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync(() => ImageSummaryCore(maxPages, ct), ct);
        }
    }

    /// <summary>
    /// Разбор без декодирования: размеры берутся из метаданных, реальное
    /// разрешение — из матрицы размещения (метаданные pdfium для повёрнутых
    /// картинок врут в разы).
    /// </summary>
    private PdfImageSummary ImageSummaryCore(int maxPages, CancellationToken ct)
    {
        var pageCount = fpdfview.FPDF_GetPageCount(NativeDoc);
        var sampled = Math.Clamp(maxPages, 1, Math.Max(1, pageCount));
        var images = 0;
        var textLength = 0;
        double dpiSum = 0;
        var dpiSamples = 0;

        for (var p = 0; p < sampled; p++)
        {
            ct.ThrowIfCancellationRequested();
            var page = fpdfview.FPDF_LoadPage(NativeDoc, p);
            if (page == null || page.__Instance == IntPtr.Zero)
                continue;
            try
            {
                var textPage = fpdf_text.FPDFTextLoadPage(page);
                if (textPage != null && textPage.__Instance != IntPtr.Zero)
                {
                    try
                    {
                        textLength += Math.Max(0, fpdf_text.FPDFTextCountChars(textPage));
                    }
                    finally
                    {
                        fpdf_text.FPDFTextClosePage(textPage);
                    }
                }

                var objects = fpdf_edit.FPDFPageCountObjects(page);
                for (var i = 0; i < objects; i++)
                {
                    var obj = fpdf_edit.FPDFPageGetObject(page, i);
                    if (obj == null || obj.__Instance == IntPtr.Zero ||
                        fpdf_edit.FPDFPageObjGetType(obj) != 3) // FPDF_PAGEOBJ_IMAGE
                        continue;
                    images++;
                    if (dpiSamples >= 20)
                        continue;

                    var meta = new FPDF_IMAGEOBJ_METADATA();
                    if (fpdf_edit.FPDFImageObjGetImageMetadata(obj, page, meta) == 0)
                        continue;
                    var matrix = new FS_MATRIX_();
                    if (fpdf_edit.FPDFPageObjGetMatrix(obj, matrix) == 0)
                        continue;
                    var widthPt = Math.Sqrt((double)matrix.A * matrix.A + (double)matrix.B * matrix.B);
                    if (widthPt < 1 || meta.Width < 1)
                        continue;
                    dpiSum += meta.Width / (widthPt / 72.0);
                    dpiSamples++;
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }

        return new PdfImageSummary(
            sampled, images, textLength, dpiSamples > 0 ? dpiSum / dpiSamples : 0);
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

    private const int PageObjectImage = 3; // FPDF_PAGEOBJ_IMAGE

    public Task<PdfImageObject?> GetImageObjectAtAsync(
        int pageIndex, int extraQuarterTurns, double xPt, double yPt, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync<PdfImageObject?>(() =>
            {
                var page = fpdfview.FPDF_LoadPage(NativeDoc, pageIndex);
                if (page == null || page.__Instance == IntPtr.Zero)
                    return null;
                try
                {
                    var (px, py) = DisplayedToPage(page, pageIndex, extraQuarterTurns, xPt, yPt);
                    var count = fpdf_edit.FPDFPageCountObjects(page);

                    // Идём с конца: верхний по порядку отрисовки объект под
                    // курсором и есть тот, который видит пользователь.
                    for (var i = count - 1; i >= 0; i--)
                    {
                        var obj = fpdf_edit.FPDFPageGetObject(page, i);
                        if (obj == null || obj.__Instance == IntPtr.Zero ||
                            fpdf_edit.FPDFPageObjGetType(obj) != PageObjectImage)
                            continue;

                        float left = 0, bottom = 0, right = 0, top = 0;
                        if (fpdf_edit.FPDFPageObjGetBounds(obj, ref left, ref bottom, ref right, ref top) == 0)
                            continue;
                        if (px < Math.Min(left, right) || px > Math.Max(left, right) ||
                            py < Math.Min(bottom, top) || py > Math.Max(bottom, top))
                            continue;

                        var bitmap = fpdf_edit.FPDFImageObjGetBitmap(obj);
                        if (bitmap == null || bitmap.__Instance == IntPtr.Zero)
                            continue;
                        try
                        {
                            var bgra = BitmapToBgra(bitmap, out var bw, out var bh);
                            if (bgra == null)
                                continue;

                            // Рамка объекта в отображаемых координатах страницы.
                            var size = Info.Pages[pageIndex];
                            var rotate = ((extraQuarterTurns % 4) + 4) % 4;
                            var displayedW = rotate % 2 == 0 ? size.WidthPoints : size.HeightPoints;
                            var displayedH = rotate % 2 == 0 ? size.HeightPoints : size.WidthPoints;
                            int dx1 = 0, dy1 = 0, dx2 = 0, dy2 = 0;
                            fpdfview.FPDF_PageToDevice(page, 0, 0,
                                (int)Math.Round(displayedW), (int)Math.Round(displayedH), rotate,
                                Math.Min(left, right), Math.Max(top, bottom), ref dx1, ref dy1);
                            fpdfview.FPDF_PageToDevice(page, 0, 0,
                                (int)Math.Round(displayedW), (int)Math.Round(displayedH), rotate,
                                Math.Max(left, right), Math.Min(top, bottom), ref dx2, ref dy2);

                            return new PdfImageObject(i, bgra, bw, bh,
                                Math.Min(dx1, dx2), Math.Min(dy1, dy2),
                                Math.Abs(dx2 - dx1), Math.Abs(dy2 - dy1));
                        }
                        finally
                        {
                            fpdfview.FPDFBitmapDestroy(bitmap);
                        }
                    }
                    return null;
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }, ct);
        }
    }

    public Task<IReadOnlyList<PdfAttachment>> GetAttachmentsAsync(CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync<IReadOnlyList<PdfAttachment>>(() =>
            {
                var result = new List<PdfAttachment>();
                var count = fpdf_attachment.FPDFDocGetAttachmentCount(NativeDoc);
                for (var i = 0; i < count; i++)
                {
                    var attachment = fpdf_attachment.FPDFDocGetAttachment(NativeDoc, i);
                    if (attachment == null || attachment.__Instance == IntPtr.Zero)
                        continue;
                    var name = ReadUtf16((buffer, size) =>
                        fpdf_attachment.FPDFAttachmentGetName(attachment, ref buffer[0], size));
                    result.Add(new PdfAttachment(
                        i,
                        name.Length > 0 ? name : $"#{i + 1}",
                        AttachmentSize(attachment)));
                }
                return result;
            }, ct);
        }
    }

    /// <summary>Размер вложения: первый вызов FPDFAttachment_GetFile с нулевым буфером.</summary>
    private static ulong ReadAttachmentInto(FpdfAttachmentT attachment, byte[]? buffer)
    {
        ulong written = 0;
        if (buffer == null)
        {
            fpdf_attachment.FPDFAttachmentGetFile(attachment, IntPtr.Zero, 0, ref written);
            return written;
        }
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(
            buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            fpdf_attachment.FPDFAttachmentGetFile(
                attachment, pin.AddrOfPinnedObject(), (ulong)buffer.Length, ref written);
        }
        finally
        {
            pin.Free();
        }
        return written;
    }

    private static long AttachmentSize(FpdfAttachmentT attachment) =>
        (long)ReadAttachmentInto(attachment, null);

    public Task<byte[]> ReadAttachmentAsync(int index, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync(() =>
            {
                var count = fpdf_attachment.FPDFDocGetAttachmentCount(NativeDoc);
                if (index < 0 || index >= count)
                    throw new PdfEngineException("Вложение с таким номером в документе отсутствует.");
                var attachment = fpdf_attachment.FPDFDocGetAttachment(NativeDoc, index);
                if (attachment == null || attachment.__Instance == IntPtr.Zero)
                    throw new PdfEngineException("Не удалось прочитать вложение.");

                var size = ReadAttachmentInto(attachment, null);
                if (size == 0)
                    return Array.Empty<byte>();
                // Вложение целиком грузится в память: гигабайтные вложения в
                // PDF не встречаются, а поточного API pdfium не даёт.
                if (size > int.MaxValue)
                    throw new PdfEngineException("Вложение слишком велико для извлечения.");
                var buffer = new byte[size];
                var written = ReadAttachmentInto(attachment, buffer);
                if (written != size)
                    throw new PdfEngineException("Вложение прочитано не полностью.");
                return buffer;
            }, ct);
        }
    }

    private const int PageObjectText = 1; // FPDF_PAGEOBJ_TEXT

    public Task<PdfTextObject?> GetTextObjectAtAsync(
        int pageIndex, int extraQuarterTurns, double xPt, double yPt, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync<PdfTextObject?>(() =>
            {
                var page = fpdfview.FPDF_LoadPage(NativeDoc, pageIndex);
                if (page == null || page.__Instance == IntPtr.Zero)
                    return null;
                var textPage = fpdf_text.FPDFTextLoadPage(page);
                try
                {
                    var (px, py) = DisplayedToPage(page, pageIndex, extraQuarterTurns, xPt, yPt);
                    var count = fpdf_edit.FPDFPageCountObjects(page);
                    for (var i = count - 1; i >= 0; i--)
                    {
                        var obj = fpdf_edit.FPDFPageGetObject(page, i);
                        if (obj == null || obj.__Instance == IntPtr.Zero ||
                            fpdf_edit.FPDFPageObjGetType(obj) != PageObjectText)
                            continue;

                        float left = 0, bottom = 0, right = 0, top = 0;
                        if (fpdf_edit.FPDFPageObjGetBounds(obj, ref left, ref bottom, ref right, ref top) == 0)
                            continue;
                        if (px < Math.Min(left, right) || px > Math.Max(left, right) ||
                            py < Math.Min(bottom, top) || py > Math.Max(bottom, top))
                            continue;

                        var text = ReadTextObjectText(obj, textPage);
                        if (text.Length == 0)
                            continue; // пустой объект править нечего

                        float size = 0;
                        fpdf_edit.FPDFTextObjGetFontSize(obj, ref size);

                        uint r = 0, g = 0, b = 0, a = 255;
                        fpdf_edit.FPDFPageObjGetFillColor(obj, ref r, ref g, ref b, ref a);

                        var fontName = "";
                        var embedded = false;
                        var font = fpdf_edit.FPDFTextObjGetFont(obj);
                        if (font != null && font.__Instance != IntPtr.Zero)
                        {
                            fontName = ReadFontName(font);
                            embedded = fpdf_edit.FPDFFontGetIsEmbedded(font) == 1;
                        }

                        var (x, y, w, h) = ContentRectToDisplayed(
                            page, pageIndex, extraQuarterTurns, left, bottom, right, top);
                        return new PdfTextObject(i, text, size,
                            (a << 24) | (r << 16) | (g << 8) | b,
                            fontName, embedded, x, y, w, h);
                    }
                    return null;
                }
                finally
                {
                    if (textPage != null && textPage.__Instance != IntPtr.Zero)
                        fpdf_text.FPDFTextClosePage(textPage);
                    fpdfview.FPDF_ClosePage(page);
                }
            }, ct);
        }
    }

    public Task<bool> CanFontRenderTextAsync(
        int pageIndex, int objectIndex, string text, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync(() =>
            {
                // Пустая и пробельная строка рисуется чем угодно.
                if (text.All(char.IsWhiteSpace))
                    return true;

                var page = fpdfview.FPDF_LoadPage(NativeDoc, pageIndex);
                if (page == null || page.__Instance == IntPtr.Zero)
                    return false;
                try
                {
                    var source = fpdf_edit.FPDFPageGetObject(page, objectIndex);
                    if (source == null || source.__Instance == IntPtr.Zero ||
                        fpdf_edit.FPDFPageObjGetType(source) != PageObjectText)
                        return false;
                    var font = fpdf_edit.FPDFTextObjGetFont(source);
                    if (font == null || font.__Instance == IntPtr.Zero)
                        return false;

                    // Пробный объект тем же шрифтом: сам документ не меняется.
                    var probe = fpdf_edit.FPDFPageObjCreateTextObj(NativeDoc, font, 24f);
                    if (probe == null || probe.__Instance == IntPtr.Zero)
                        return false;
                    try
                    {
                        var visible = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
                        var buffer = new ushort[visible.Length + 1];
                        for (var i = 0; i < visible.Length; i++)
                            buffer[i] = visible[i];
                        if (fpdf_edit.FPDFTextSetText(probe, ref buffer[0]) == 0)
                            return false;

                        var bitmap = fpdf_edit.FPDFTextObjGetRenderedBitmap(NativeDoc, page, probe, 1f);
                        if (bitmap == null || bitmap.__Instance == IntPtr.Zero)
                            return false;
                        try
                        {
                            // Если шрифт не знает этих букв, картинка выходит пустой.
                            return HasAnyInk(bitmap);
                        }
                        finally
                        {
                            fpdfview.FPDFBitmapDestroy(bitmap);
                        }
                    }
                    finally
                    {
                        fpdf_edit.FPDFPageObjDestroy(probe);
                    }
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }, ct);
        }
    }

    /// <summary>Есть ли в растре хоть один непрозрачный пиксель.</summary>
    private static unsafe bool HasAnyInk(FpdfBitmapT bitmap)
    {
        var width = fpdfview.FPDFBitmapGetWidth(bitmap);
        var height = fpdfview.FPDFBitmapGetHeight(bitmap);
        var stride = fpdfview.FPDFBitmapGetStride(bitmap);
        var buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
        if (width < 1 || height < 1 || buffer == IntPtr.Zero)
            return false;
        var src = (byte*)buffer;
        for (var y = 0; y < height; y++)
        {
            var row = src + (long)y * stride;
            for (var x = 0; x < width; x++)
            {
                if (row[x * 4 + 3] != 0)
                    return true;
            }
        }
        return false;
    }

    internal static string ReadTextObjectText(FpdfPageobjectT obj, FpdfTextpageT? textPage)
    {
        if (textPage == null || textPage.__Instance == IntPtr.Zero)
            return "";
        return ReadUtf16((buffer, size) =>
            fpdf_edit.FPDFTextObjGetText(obj, textPage, ref buffer[0], size));
    }

    private static unsafe string ReadFontName(FpdfFontT font)
    {
        var name = ReadAnsiName((buffer, size) =>
            fpdf_edit.FPDFFontGetBaseFontName(font, (sbyte*)buffer, size));
        if (name.Length == 0)
            name = ReadAnsiName((buffer, size) =>
                fpdf_edit.FPDFFontGetFamilyName(font, (sbyte*)buffer, size));
        return name;
    }

    /// <summary>Имя шрифта pdfium отдаёт однобайтовой строкой с завершающим нулём.</summary>
    private static unsafe string ReadAnsiName(Func<IntPtr, ulong, ulong> read)
    {
        var length = read(IntPtr.Zero, 0);
        if (length <= 1)
            return "";
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)length);
        try
        {
            read(buffer, length);
            return System.Runtime.InteropServices.Marshal
                .PtrToStringAnsi(buffer, (int)length - 1) ?? "";
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Рамка объекта из координат содержимого в отображаемые пункты.</summary>
    private (double X, double Y, double W, double H) ContentRectToDisplayed(
        FpdfPageT page, int pageIndex, int extraQuarterTurns,
        float left, float bottom, float right, float top)
    {
        var size = Info.Pages[pageIndex];
        var rotate = ((extraQuarterTurns % 4) + 4) % 4;
        var displayedW = rotate % 2 == 0 ? size.WidthPoints : size.HeightPoints;
        var displayedH = rotate % 2 == 0 ? size.HeightPoints : size.WidthPoints;
        int x1 = 0, y1 = 0, x2 = 0, y2 = 0;
        fpdfview.FPDF_PageToDevice(page, 0, 0,
            (int)Math.Round(displayedW), (int)Math.Round(displayedH), rotate,
            Math.Min(left, right), Math.Max(top, bottom), ref x1, ref y1);
        fpdfview.FPDF_PageToDevice(page, 0, 0,
            (int)Math.Round(displayedW), (int)Math.Round(displayedH), rotate,
            Math.Max(left, right), Math.Min(top, bottom), ref x2, ref y2);
        return (Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
    }

    /// <summary>Растр PDFium → BGRA. null — неизвестный формат.</summary>
    internal static unsafe byte[]? BitmapToBgra(FpdfBitmapT bitmap, out int width, out int height)
    {
        width = fpdfview.FPDFBitmapGetWidth(bitmap);
        height = fpdfview.FPDFBitmapGetHeight(bitmap);
        var stride = fpdfview.FPDFBitmapGetStride(bitmap);
        var format = fpdfview.FPDFBitmapGetFormat(bitmap);
        var buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
        if (width < 1 || height < 1 || buffer == IntPtr.Zero)
            return null;

        var result = new byte[(long)width * height * 4];
        var src = (byte*)buffer;
        for (var y = 0; y < height; y++)
        {
            var row = src + (long)y * stride;
            for (var x = 0; x < width; x++)
            {
                var o = ((long)y * width + x) * 4;
                switch (format)
                {
                    case 1: // Gray
                        result[o] = result[o + 1] = result[o + 2] = row[x];
                        result[o + 3] = 0xFF;
                        break;
                    case 2: // BGR
                        result[o] = row[x * 3];
                        result[o + 1] = row[x * 3 + 1];
                        result[o + 2] = row[x * 3 + 2];
                        result[o + 3] = 0xFF;
                        break;
                    case 3: // BGRx
                    case 4: // BGRA
                        result[o] = row[x * 4];
                        result[o + 1] = row[x * 4 + 1];
                        result[o + 2] = row[x * 4 + 2];
                        result[o + 3] = format == 4 ? row[x * 4 + 3] : (byte)0xFF;
                        break;
                    default:
                        return null;
                }
            }
        }
        return result;
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

    public Task<IReadOnlyList<PdfBookmark>> GetBookmarksAsync(CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync<IReadOnlyList<PdfBookmark>>(
                () => ReadBookmarkLevel(null, 0), ct);
        }
    }

    private const int BookmarkMaxDepth = 32;      // защита от циклов в /Outlines
    private const int BookmarkMaxPerLevel = 5000; // и от бесконечных цепочек Next

    /// <summary>
    /// Один уровень оглавления. PDF допускает битые деревья с циклами, поэтому
    /// глубина и длина цепочки ограничены, а посещённые узлы запоминаются.
    /// </summary>
    private List<PdfBookmark> ReadBookmarkLevel(FpdfBookmarkT? parent, int depth)
    {
        var result = new List<PdfBookmark>();
        if (depth >= BookmarkMaxDepth)
            return result;

        var seen = new HashSet<IntPtr>();
        var node = fpdf_doc.FPDFBookmarkGetFirstChild(NativeDoc, parent);
        while (node != null && node.__Instance != IntPtr.Zero &&
               result.Count < BookmarkMaxPerLevel && seen.Add(node.__Instance))
        {
            var title = ReadBookmarkTitle(node);
            result.Add(new PdfBookmark(
                title.Length > 0 ? title : "(без названия)",
                ResolveBookmarkPage(node),
                ReadBookmarkLevel(node, depth + 1)));
            node = fpdf_doc.FPDFBookmarkGetNextSibling(NativeDoc, node);
        }
        return result;
    }

    /// <summary>Заголовок закладки: UTF-16 с завершающим нулём, длина запрашивается первым проходом.</summary>
    private static string ReadBookmarkTitle(FpdfBookmarkT bookmark)
    {
        var bytes = fpdf_doc.FPDFBookmarkGetTitle(bookmark, IntPtr.Zero, 0);
        if (bytes <= 2)
            return "";
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)bytes);
        try
        {
            fpdf_doc.FPDFBookmarkGetTitle(bookmark, buffer, bytes);
            return System.Runtime.InteropServices.Marshal
                .PtrToStringUni(buffer, (int)(bytes / 2) - 1) ?? "";
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Целевая страница закладки: прямой /Dest, иначе действие GoTo. -1 — не определена.</summary>
    private int ResolveBookmarkPage(FpdfBookmarkT bookmark)
    {
        var dest = fpdf_doc.FPDFBookmarkGetDest(NativeDoc, bookmark);
        if (dest != null && dest.__Instance != IntPtr.Zero)
        {
            var page = fpdf_doc.FPDFDestGetDestPageIndex(NativeDoc, dest);
            if (page >= 0)
                return page;
        }

        var action = fpdf_doc.FPDFBookmarkGetAction(bookmark);
        if (action == null || action.__Instance == IntPtr.Zero)
            return -1;
        // Только переход внутри документа: Launch/URI из оглавления не выполняем.
        if (fpdf_doc.FPDFActionGetType(action) != ActionTypeGoto)
            return -1;
        var actionDest = fpdf_doc.FPDFActionGetDest(NativeDoc, action);
        if (actionDest == null || actionDest.__Instance == IntPtr.Zero)
            return -1;
        var target = fpdf_doc.FPDFDestGetDestPageIndex(NativeDoc, actionDest);
        return target >= 0 ? target : -1;
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

                        // Рамка нужна экспорту, чтобы привязать примечание к
                        // нужному месту текста. Её отсутствие не повод терять
                        // саму аннотацию.
                        var box = new FS_RECTF_();
                        PdfTextRect? rect = fpdf_annot.FPDFAnnotGetRect(annot, box) != 0
                            ? new PdfTextRect(
                                Math.Min(box.Left, box.Right), Math.Max(box.Top, box.Bottom),
                                Math.Max(box.Left, box.Right), Math.Min(box.Top, box.Bottom))
                            : null;

                        result.Add(new PdfAnnotationInfo(
                            i, subtype,
                            GetAnnotString(annot, "Contents"),
                            GetAnnotString(annot, "T"),
                            GetAnnotString(annot, "V"),
                            rect));
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
                if (_accessor != null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    _accessor.Dispose();
                }
                _mmf?.Dispose();
            }, CancellationToken.None);
        }
        await closeTask.ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
