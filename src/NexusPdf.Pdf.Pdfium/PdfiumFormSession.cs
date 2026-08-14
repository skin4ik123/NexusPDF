using NexusPdf.Pdf.Abstractions;
using PDFiumCore;
using Delegates = PDFiumCore.Delegates;

namespace NexusPdf.Pdf.Pdfium;

/// <summary>
/// Окружение заполнения AcroForm-форм для одного документа. Все вызовы —
/// строго на PDFium-потоке. Колбэки FPDF_FORMFILLINFO укоренены на время
/// жизни сессии: сборка мусора не должна освобождать делегаты, пока pdfium
/// хранит указатели на них.
/// </summary>
internal sealed class PdfiumFormSession
{
    private readonly FpdfDocumentT _document;
    private readonly FPDF_FORMFILLINFO _info;
    // Укоренение делегатов: pdfium держит сырые указатели на эти функции.
    private readonly List<Delegate> _rootedCallbacks = new();

    private FpdfFormHandleT? _formHandle;
    private FpdfPageT? _activePage;
    private int _activePageIndex = -1;

    private PdfiumFormSession(FpdfDocumentT document, FPDF_FORMFILLINFO info)
    {
        _document = document;
        _info = info;
    }

    public bool IsActive => _formHandle != null;

    /// <summary>Создание окружения. Вызывать на PDFium-потоке. null — формы отсутствуют.</summary>
    public static PdfiumFormSession? Create(FpdfDocumentT document)
    {
        var formType = fpdf_formfill.FPDF_GetFormType(document);
        if (formType != 1) // поддерживаем только AcroForm; XFA честно не берём
            return null;

        var info = new FPDF_FORMFILLINFO { Version = 1, XfaDisabled = 1 };
        var session = new PdfiumFormSession(document, info);

        // Обязательные колбэки версии 1 — безопасные no-op реализации.
        session.Set(v => info.Release = v, new Delegates.Action___IntPtr(_ => { }));
        session.Set(v => info.FFI_Invalidate = v,
            new Delegates.Action___IntPtr___IntPtr_double_double_double_double((_, _, _, _, _, _) => { }));
        session.Set(v => info.FFI_OutputSelectedRect = v,
            new Delegates.Action___IntPtr___IntPtr_double_double_double_double((_, _, _, _, _, _) => { }));
        session.Set(v => info.FFI_SetCursor = v, new Delegates.Action___IntPtr_int((_, _) => { }));
        session.Set(v => info.FFI_SetTimer = v,
            new Delegates.Func_int___IntPtr_int_PDFiumCore_TimerCallback((_, _, _) => 0));
        session.Set(v => info.FFI_KillTimer = v, new Delegates.Action___IntPtr_int((_, _) => { }));
        session.Set(v => info.FFI_GetLocalTime = v,
            new Delegates.Func_PDFiumCore__FPDF_SYSTEMTIME___Internal___IntPtr(_ => default));
        session.Set(v => info.FFI_OnChange = v, new Delegates.Action___IntPtr(_ => { }));
        session.Set(v => info.FFI_GetPage = v,
            new Delegates.Func___IntPtr___IntPtr___IntPtr_int((_, _, _) => IntPtr.Zero));
        session.Set(v => info.FFI_GetCurrentPage = v,
            new Delegates.Func___IntPtr___IntPtr___IntPtr((_, _) => IntPtr.Zero));
        session.Set(v => info.FFI_GetRotation = v,
            new Delegates.Func_int___IntPtr___IntPtr((_, _) => 0));
        session.Set(v => info.FFI_ExecuteNamedAction = v,
            new Delegates.Action___IntPtr_string8((_, _) => { }));

        var handle = fpdf_formfill.FPDFDOC_InitFormFillEnvironment(document, info);
        if (handle == null || handle.__Instance == IntPtr.Zero)
            return null;

        session._formHandle = handle;
        // Подсветка полей: мягкий голубой, как в привычных просмотрщиках.
        fpdf_formfill.FPDF_SetFormFieldHighlightColor(handle, 0, 0xB5D0FF);
        fpdf_formfill.FPDF_SetFormFieldHighlightAlpha(handle, 90);
        return session;
    }

    private void Set<T>(Action<T> assign, T callback) where T : Delegate
    {
        _rootedCallbacks.Add(callback);
        assign(callback);
    }

    /// <summary>
    /// Страница для взаимодействия: держится открытой, пока фокус на ней —
    /// закрытие страницы убивало бы фокус поля при каждом ре-рендере.
    /// </summary>
    private FpdfPageT ActivatePage(int pageIndex)
    {
        if (_activePage != null && _activePageIndex == pageIndex)
            return _activePage;

        DeactivatePage();
        var page = fpdfview.FPDF_LoadPage(_document, pageIndex);
        if (page == null || page.__Instance == IntPtr.Zero)
            throw new PdfEngineException($"Не удалось открыть страницу {pageIndex + 1} для формы.");
        fpdf_formfill.FORM_OnAfterLoadPage(page, _formHandle!);
        _activePage = page;
        _activePageIndex = pageIndex;
        return page;
    }

    private void DeactivatePage()
    {
        if (_activePage == null) return;
        fpdf_formfill.FORM_ForceToKillFocus(_formHandle!);
        fpdf_formfill.FORM_OnBeforeClosePage(_activePage, _formHandle!);
        fpdfview.FPDF_ClosePage(_activePage);
        _activePage = null;
        _activePageIndex = -1;
    }

    public void Click(int pageIndex, int extraQuarterTurns, double displayedXPt, double displayedYPt,
        double displayedWidthPt, double displayedHeightPt)
    {
        var page = ActivatePage(pageIndex);
        double pageX = 0, pageY = 0;
        // DeviceToPage учитывает и /Rotate источника, и наш добавочный поворот.
        fpdfview.FPDF_DeviceToPage(page, 0, 0,
            (int)Math.Round(displayedWidthPt), (int)Math.Round(displayedHeightPt),
            ((extraQuarterTurns % 4) + 4) % 4,
            (int)Math.Round(displayedXPt), (int)Math.Round(displayedYPt),
            ref pageX, ref pageY);

        fpdf_formfill.FORM_OnLButtonDown(_formHandle!, page, 0, pageX, pageY);
        fpdf_formfill.FORM_OnLButtonUp(_formHandle!, page, 0, pageX, pageY);
    }

    public void Char(char character)
    {
        if (_activePage == null) return;
        fpdf_formfill.FORM_OnChar(_formHandle!, _activePage, character, 0);
    }

    public void KeyDown(int virtualKeyCode)
    {
        if (_activePage == null) return;
        fpdf_formfill.FORM_OnKeyDown(_formHandle!, _activePage, virtualKeyCode, 0);
    }

    public void KillFocus()
    {
        if (_formHandle != null)
            fpdf_formfill.FORM_ForceToKillFocus(_formHandle);
    }

    /// <summary>Дорисовка полей формы поверх отрисованной страницы.</summary>
    public void DrawFields(FpdfBitmapT bitmap, FpdfPageT page, int width, int height, int rotate, int flags)
    {
        fpdf_formfill.FPDF_FFLDraw(_formHandle!, bitmap, page, 0, 0, width, height, rotate, flags);
    }

    /// <summary>Является ли страница активной интерактивной страницей формы.</summary>
    public FpdfPageT? TryGetActivePage(int pageIndex) =>
        _activePageIndex == pageIndex ? _activePage : null;

    /// <summary>Транзитная страница рендера: уведомления окружения до/после.</summary>
    public void OnTransientPageLoaded(FpdfPageT page) =>
        fpdf_formfill.FORM_OnAfterLoadPage(page, _formHandle!);

    public void OnTransientPageClosing(FpdfPageT page) =>
        fpdf_formfill.FORM_OnBeforeClosePage(page, _formHandle!);

    public void Dispose()
    {
        DeactivatePage();
        if (_formHandle != null)
        {
            fpdf_formfill.FPDFDOC_ExitFormFillEnvironment(_formHandle);
            _formHandle = null;
        }
        GC.KeepAlive(_info);
        _rootedCallbacks.Clear();
    }
}
