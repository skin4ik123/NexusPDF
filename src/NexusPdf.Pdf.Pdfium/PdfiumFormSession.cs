using NexusPdf.Pdf.Abstractions;
using PDFiumCore;
using Delegates = PDFiumCore.Delegates;

namespace NexusPdf.Pdf.Pdfium;

/// <summary>
/// Окружение AcroForm-форм для одного документа: рисует значения полей и
/// принимает ввод. Все вызовы —
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

    private readonly int _formType;
    private FpdfFormHandleT? _formHandle;
    private FpdfPageT? _activePage;
    private int _activePageIndex = -1;
    private PdfiumXfaHybrid? _xfa;
    // Поле гибридной формы, которое последним получило фокус: его значение
    // переносится на поля того же узла данных при следующем уходе из него.
    private string? _lastField;

    private PdfiumFormSession(FpdfDocumentT document, FPDF_FORMFILLINFO info, int formType)
    {
        _document = document;
        _info = info;
        _formType = formType;
    }

    public bool IsActive => _formHandle != null;

    /// <summary>
    /// Принимаются ли сейчас изменения полей. Гибридная форма (AcroForm + XFA)
    /// заполняется только после <see cref="EnableInput"/>: до этого не сделан
    /// снимок, с которым при сохранении сравниваются значения для XFA.
    /// </summary>
    public bool AllowsInput => _formType == 1 || _xfa != null;

    /// <summary>Гибридная форма: AcroForm + XFA (тип 3).</summary>
    public bool IsXfaHybrid => _formType == 3;

    /// <summary>
    /// Включение ввода. У гибридной формы разбирается XFA-часть и снимаются
    /// значения полей; не разобралась — false и причина: заполнять такую
    /// форму нельзя, Adobe Reader показал бы прежние данные.
    /// </summary>
    public bool EnableInput(out string? failure)
    {
        failure = null;
        if (AllowsInput) return true;
        if (_formType != 3 || _formHandle == null) return false;
        _xfa = PdfiumXfaHybrid.TryCreate(_document, _formHandle, TryGetActivePage, out failure);
        return _xfa != null;
    }

    /// <summary>
    /// Изменённые значения гибридной формы в виде нового пакета XFA; null —
    /// форма не гибридная или ничего не менялось.
    /// </summary>
    public XfaPacketUpdate? BuildXfaUpdate() =>
        _xfa?.BuildUpdate(_document, _formHandle!, TryGetActivePage);

    /// <summary>Поле гибридной формы, которому нет места в данных XFA: его не заполнить.</summary>
    private bool IsLockedField(string? name) => name != null && _xfa?.IsLocked(name) == true;

    private bool FocusIsLocked() =>
        _xfa is { LockedFields.Count: > 0 } && IsLockedField(PdfiumXfaHybrid.FocusedFieldName(_formHandle!));

    /// <summary>Значение последнего поля — на поля того же узла данных (если они есть).</summary>
    private void SyncPeers(string? candidate) =>
        _xfa?.SyncPeers(_document, _formHandle!, TryGetActivePage, candidate);

    /// <summary>Значение хотя бы одного поля изменилось с момента открытия документа.</summary>
    public bool IsModified { get; private set; }

    /// <summary>Растёт при каждом изменении значения поля.</summary>
    public long ChangeStamp { get; private set; }

    /// <summary>
    /// Создание окружения. Вызывать на PDFium-потоке. null — AcroForm-полей нет.
    ///
    /// 1 — обычный AcroForm. 3 — гибридная форма: те же AcroForm-поля и XFA
    /// поверх (так устроены, например, бланки CBP). PDFium здесь без XFA, и с
    /// XfaDisabled тип 3 работает как обычный AcroForm: FFLDraw рисует
    /// значения полей, как Chrome и Firefox. Без окружения такая форма
    /// выглядела пустой и на экране, и на печати. Заполнение типа 3 — см.
    /// <see cref="PdfiumXfaHybrid"/>. 2 — целиком динамическая XFA:
    /// AcroForm-виджетов нет, а разметку XFA без движка XFA не нарисовать.
    /// </summary>
    public static PdfiumFormSession? Create(FpdfDocumentT document)
    {
        var formType = fpdf_formfill.FPDF_GetFormType(document);
        if (formType is not (1 or 3))
            return null;

        var info = new FPDF_FORMFILLINFO { Version = 1, XfaDisabled = 1 };
        var session = new PdfiumFormSession(document, info, formType);

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
        // pdfium сообщает о любом изменении значения поля: по этому признаку
        // сохранение знает, что документ в памяти отличается от файла.
        session.Set(v => info.FFI_OnChange = v, new Delegates.Action___IntPtr(_ =>
        {
            session.IsModified = true;
            session.ChangeStamp++;
        }));
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
        // Окружение живёт всю жизнь документа и рисует значения полей в каждом
        // рендере; подсветка — принадлежность РЕЖИМА заполнения и включается
        // отдельно. Иначе поля выглядели бы «в рамочках» и вне режима.
        session.SetHighlight(false);
        return session;
    }

    /// <summary>
    /// Подсветка полей: мягкий голубой в режиме заполнения, ничего — вне его.
    /// Значения полей рисуются в обоих случаях; выключение режима раньше
    /// закрывало окружение целиком, и заполненное «исчезало» с экрана и из
    /// печати, хотя в документе оставалось.
    /// </summary>
    public void SetHighlight(bool on)
    {
        if (_formHandle == null) return;
        IsHighlightOn = on;
        if (on)
        {
            // Байты цвета pdfium кладёт в растр в порядке BGR: «0xB5D0FF» давал
            // персиковую подсветку вместо голубой. Здесь голубой — #B5D0FF.
            fpdf_formfill.FPDF_SetFormFieldHighlightColor(_formHandle, 0, 0xFFD0B5);
            fpdf_formfill.FPDF_SetFormFieldHighlightAlpha(_formHandle, 90);
        }
        else
        {
            fpdf_formfill.FPDF_RemoveFormFieldHighlight(_formHandle);
        }
    }

    /// <summary>
    /// Выход из режима заполнения: зафиксировать набранное (снятие фокуса
    /// генерирует значение и appearance-поток), отпустить активную страницу и
    /// погасить подсветку. Окружение остаётся жить — им рисуются значения.
    /// </summary>
    public void CommitAndIdle()
    {
        KillFocus();
        DeactivatePage();
        SetHighlight(false);
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

    /// <returns>Вид поля под точкой щелчка; Unknown — щелчок мимо полей.</returns>
    public PdfFormFieldKind Click(int pageIndex, int extraQuarterTurns, double displayedXPt, double displayedYPt,
        double displayedWidthPt, double displayedHeightPt)
    {
        if (!AllowsInput) return PdfFormFieldKind.Unknown;
        var page = ActivatePage(pageIndex);
        double pageX = 0, pageY = 0;
        // DeviceToPage учитывает и /Rotate источника, и наш добавочный поворот.
        fpdfview.FPDF_DeviceToPage(page, 0, 0,
            (int)Math.Round(displayedWidthPt), (int)Math.Round(displayedHeightPt),
            ((extraQuarterTurns % 4) + 4) % 4,
            (int)Math.Round(displayedXPt), (int)Math.Round(displayedYPt),
            ref pageX, ref pageY);
        var kind = fpdf_formfill.FPDFPageHasFormFieldAtPoint(_formHandle!, page, pageX, pageY);
        var clicked = _xfa != null ? FieldNameAt(page, pageX, pageY) : null;
        if (IsLockedField(clicked))
        {
            // Поле без места в данных XFA ведёт себя как «только чтение».
            KillFocus();
            return PdfFormFieldKind.Unknown;
        }

        // Пока в поле есть фокус, pdfium держит захват мыши на нём, и клик по
        // СОСЕДНЕМУ полю уходит в старое: перейти к следующей ячейке было
        // почти невозможно. Снятие фокуса перед кликом фиксирует набранное
        // (SaveData → значение и appearance-поток) и освобождает захват — клик
        // честно попадает туда, куда указывает мышь.
        KillFocus();
        if (_xfa != null) _lastField = clicked;
        fpdf_formfill.FORM_OnMouseMove(_formHandle!, page, 0, pageX, pageY);
        fpdf_formfill.FORM_OnLButtonDown(_formHandle!, page, 0, pageX, pageY);
        fpdf_formfill.FORM_OnLButtonUp(_formHandle!, page, 0, pageX, pageY);
        // Флажок меняется самим щелчком — его пару согласуем сразу.
        if (kind is (int)PdfFormFieldKind.CheckBox or (int)PdfFormFieldKind.RadioButton)
            SyncPeers(clicked);
        return Enum.IsDefined(typeof(PdfFormFieldKind), kind) ? (PdfFormFieldKind)kind : PdfFormFieldKind.Unknown;
    }

    private const int AnnotSubtypeWidget = 20;
    private const int AnnotFlagHidden = 2;
    private const int FieldFlagReadOnly = 1;

    /// <summary>
    /// Поля страницы с рамками в отображаемых пунктах. Страница, не активная
    /// для ввода, открывается на время чтения и сразу закрывается: активную
    /// трогать нельзя — переключение сняло бы фокус с поля, в котором пишут.
    /// </summary>
    public IReadOnlyList<PdfFormFieldArea> GetFieldAreas(
        int pageIndex, int extraQuarterTurns, double displayedWidthPt, double displayedHeightPt)
    {
        var transient = _activePageIndex != pageIndex || _activePage == null;
        var page = transient ? fpdfview.FPDF_LoadPage(_document, pageIndex) : _activePage!;
        if (page == null || page.__Instance == IntPtr.Zero)
            return Array.Empty<PdfFormFieldArea>();
        var rotate = ((extraQuarterTurns % 4) + 4) % 4;
        var width = (int)Math.Round(displayedWidthPt);
        var height = (int)Math.Round(displayedHeightPt);
        var result = new List<PdfFormFieldArea>();
        try
        {
            var count = fpdf_annot.FPDFPageGetAnnotCount(page);
            for (var i = 0; i < count; i++)
            {
                var annot = fpdf_annot.FPDFPageGetAnnot(page, i);
                if (annot == null || annot.__Instance == IntPtr.Zero) continue;
                try
                {
                    if (fpdf_annot.FPDFAnnotGetSubtype(annot) != AnnotSubtypeWidget) continue;
                    if ((fpdf_annot.FPDFAnnotGetFlags(annot) & AnnotFlagHidden) != 0) continue;
                    var type = fpdf_annot.FPDFAnnotGetFormFieldType(_formHandle!, annot);
                    if (!Enum.IsDefined(typeof(PdfFormFieldKind), type)) continue;
                    var kind = (PdfFormFieldKind)type;
                    if (kind is PdfFormFieldKind.Unknown or PdfFormFieldKind.Signature) continue;

                    var rect = new FS_RECTF_();
                    if (fpdf_annot.FPDFAnnotGetRect(annot, rect) == 0) continue;
                    int x1 = 0, y1 = 0, x2 = 0, y2 = 0;
                    fpdfview.FPDF_PageToDevice(page, 0, 0, width, height, rotate, rect.Left, rect.Top, ref x1, ref y1);
                    fpdfview.FPDF_PageToDevice(page, 0, 0, width, height, rotate, rect.Right, rect.Bottom, ref x2, ref y2);
                    var readOnly = (fpdf_annot.FPDFAnnotGetFormFieldFlags(_formHandle!, annot) & FieldFlagReadOnly) != 0 ||
                                   IsLockedField(_xfa is { LockedFields.Count: > 0 } ? FieldName(annot) : null);
                    result.Add(new PdfFormFieldArea(
                        Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1), kind, readOnly));
                }
                finally
                {
                    fpdf_annot.FPDFPageCloseAnnot(annot);
                }
            }
        }
        finally
        {
            if (transient)
                fpdfview.FPDF_ClosePage(page);
        }
        return result;
    }

    public void Char(char character)
    {
        if (!AllowsInput || _activePage == null || FocusIsLocked()) return;
        fpdf_formfill.FORM_OnChar(_formHandle!, _activePage, character, 0);
    }

    /// <summary>FWL_EVENTFLAG_ShiftKey: модификатор для Shift+Tab.</summary>
    private const int EventFlagShift = 1;

    /// <summary>VK_TAB: переход между полями разрешён и из поля, которое заполнять нельзя.</summary>
    private const int VirtualKeyTab = 0x09;

    public void KeyDown(int virtualKeyCode, bool shift = false)
    {
        if (!AllowsInput || _activePage == null) return;
        if (virtualKeyCode != VirtualKeyTab && FocusIsLocked()) return;
        var modifiers = shift ? EventFlagShift : 0;
        fpdf_formfill.FORM_OnKeyDown(_formHandle!, _activePage, virtualKeyCode, modifiers);
        fpdf_formfill.FORM_OnKeyUp(_formHandle!, _activePage, virtualKeyCode, modifiers);
        if (_xfa != null && virtualKeyCode == VirtualKeyTab)
        {
            // Tab зафиксировал прежнее поле и перевёл фокус в следующее.
            SyncPeers(_lastField);
            _lastField = PdfiumXfaHybrid.FocusedFieldName(_formHandle!);
        }
    }

    public void KillFocus()
    {
        if (_formHandle == null) return;
        fpdf_formfill.FORM_ForceToKillFocus(_formHandle);
        SyncPeers(_lastField);
    }

    private const int FieldTypeComboBox = 4; // FPDF_FORMFIELD_COMBOBOX
    private const int FieldTypeListBox = 5;  // FPDF_FORMFIELD_LISTBOX

    /// <summary>
    /// Выпадающий список/список в точке клика (отображаемые пункты).
    /// null — в точке нет такого поля. Рамка результата — в отображаемых
    /// пунктах текущей ориентации (для позиционирования всплывающего списка).
    /// </summary>
    public PdfComboInfo? GetComboAt(
        int pageIndex, int extraQuarterTurns, double displayedXPt, double displayedYPt,
        double displayedWidthPt, double displayedHeightPt)
    {
        if (!AllowsInput) return null;
        var page = ActivatePage(pageIndex);
        var rotate = ((extraQuarterTurns % 4) + 4) % 4;
        double pageX = 0, pageY = 0;
        fpdfview.FPDF_DeviceToPage(page, 0, 0,
            (int)Math.Round(displayedWidthPt), (int)Math.Round(displayedHeightPt),
            rotate, (int)Math.Round(displayedXPt), (int)Math.Round(displayedYPt),
            ref pageX, ref pageY);

        var point = new FS_POINTF_ { X = (float)pageX, Y = (float)pageY };
        var annot = fpdf_annot.FPDFAnnotGetFormFieldAtPoint(_formHandle!, page, point);
        if (annot == null || annot.__Instance == IntPtr.Zero)
            return null;
        try
        {
            if (IsLockedField(FieldName(annot)))
                return null;
            var fieldType = fpdf_annot.FPDFAnnotGetFormFieldType(_formHandle!, annot);
            if (fieldType != FieldTypeComboBox && fieldType != FieldTypeListBox)
                return null;
            var count = fpdf_annot.FPDFAnnotGetOptionCount(_formHandle!, annot);
            if (count <= 0)
                return null;

            var options = new List<string>(count);
            var selected = -1;
            for (var i = 0; i < count; i++)
            {
                options.Add(GetOptionLabel(annot, i));
                if (selected < 0 && fpdf_annot.FPDFAnnotIsOptionSelected(_formHandle!, annot, i) != 0)
                    selected = i;
            }

            // Рамка поля: координаты страницы → отображаемые.
            var rect = new FS_RECTF_();
            if (fpdf_annot.FPDFAnnotGetRect(annot, rect) == 0)
                return null;
            int dx1 = 0, dy1 = 0, dx2 = 0, dy2 = 0;
            fpdfview.FPDF_PageToDevice(page, 0, 0,
                (int)Math.Round(displayedWidthPt), (int)Math.Round(displayedHeightPt),
                rotate, rect.Left, rect.Top, ref dx1, ref dy1);
            fpdfview.FPDF_PageToDevice(page, 0, 0,
                (int)Math.Round(displayedWidthPt), (int)Math.Round(displayedHeightPt),
                rotate, rect.Right, rect.Bottom, ref dx2, ref dy2);
            return new PdfComboInfo(
                options, selected,
                Math.Min(dx1, dx2), Math.Min(dy1, dy2),
                Math.Abs(dx2 - dx1), Math.Abs(dy2 - dy1),
                fieldType == FieldTypeListBox);
        }
        finally
        {
            fpdf_annot.FPDFPageCloseAnnot(annot);
        }
    }

    /// <summary>Выбор пункта: фокус кликом по полю, затем выбор индекса у сфокусированного.</summary>
    public void SetComboSelection(
        int pageIndex, int extraQuarterTurns, double displayedXPt, double displayedYPt,
        double displayedWidthPt, double displayedHeightPt, int optionIndex)
    {
        if (!AllowsInput) return;
        Click(pageIndex, extraQuarterTurns, displayedXPt, displayedYPt,
            displayedWidthPt, displayedHeightPt);
        if (_activePage == null || optionIndex < 0)
            return;
        fpdf_formfill.FORM_SetIndexSelected(_formHandle!, _activePage, optionIndex, 1);
        SyncPeers(_lastField);
    }

    private string? FieldNameAt(FpdfPageT page, double pageX, double pageY)
    {
        var point = new FS_POINTF_ { X = (float)pageX, Y = (float)pageY };
        var annot = fpdf_annot.FPDFAnnotGetFormFieldAtPoint(_formHandle!, page, point);
        if (annot == null || annot.__Instance == IntPtr.Zero)
            return null;
        try
        {
            return FieldName(annot);
        }
        finally
        {
            fpdf_annot.FPDFPageCloseAnnot(annot);
        }
    }

    private string FieldName(FpdfAnnotationT annot)
    {
        var probe = new ushort[1];
        var bytesNeeded = fpdf_annot.FPDFAnnotGetFormFieldName(_formHandle!, annot, ref probe[0], 0);
        if (bytesNeeded <= 2)
            return "";
        var buffer = new ushort[bytesNeeded / 2];
        fpdf_annot.FPDFAnnotGetFormFieldName(_formHandle!, annot, ref buffer[0], bytesNeeded);
        var length = Array.IndexOf(buffer, (ushort)0);
        return new string(buffer.Take(length < 0 ? buffer.Length : length).Select(c => (char)c).ToArray());
    }

    private string GetOptionLabel(FpdfAnnotationT annot, int index)
    {
        var probe = new ushort[1];
        var bytesNeeded = fpdf_annot.FPDFAnnotGetOptionLabel(_formHandle!, annot, index, ref probe[0], 0);
        if (bytesNeeded <= 2)
            return "";
        var buffer = new ushort[bytesNeeded / 2];
        fpdf_annot.FPDFAnnotGetOptionLabel(_formHandle!, annot, index, ref buffer[0], bytesNeeded);
        var length = Array.IndexOf(buffer, (ushort)0);
        if (length < 0)
            length = buffer.Length;
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = (char)buffer[i];
        return new string(chars);
    }

    /// <summary>Включена ли сейчас подсветка полей (режим заполнения).</summary>
    public bool IsHighlightOn { get; private set; }

    /// <summary>Дорисовка полей формы поверх отрисованной страницы.</summary>
    public void DrawFields(FpdfBitmapT bitmap, FpdfPageT page, int width, int height, int rotate, int flags)
    {
        fpdf_formfill.FPDF_FFLDraw(_formHandle!, bitmap, page, 0, 0, width, height, rotate, flags);
    }

    /// <summary>
    /// Дорисовка полей для бумаги и файла: подсветка режима заполнения — это
    /// разметка экрана, и на печать она не идёт. Раньше лист, напечатанный во
    /// время заполнения формы, выходил с голубыми прямоугольниками на каждом поле.
    /// </summary>
    public void DrawFieldsForOutput(FpdfBitmapT bitmap, FpdfPageT page, int width, int height, int rotate, int flags)
    {
        var highlighted = IsHighlightOn;
        if (highlighted) SetHighlight(false);
        try
        {
            DrawFields(bitmap, page, width, height, rotate, flags);
        }
        finally
        {
            if (highlighted) SetHighlight(true);
        }
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
