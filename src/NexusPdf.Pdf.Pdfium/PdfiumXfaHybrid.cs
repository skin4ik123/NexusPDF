using System.Runtime.InteropServices;
using System.Text;
using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

/// <summary>Новое содержимое пакета XFA. PacketName = null — XFA одним потоком, заменяется весь XDP.</summary>
internal sealed record XfaPacketUpdate(string? PacketName, byte[] Content);

/// <summary>
/// Заполнение гибридной формы (AcroForm + XFA). Поля заполняются как обычный
/// AcroForm, а при сохранении изменённые значения переносятся и в пакет XFA
/// datasets — его читает Adobe Reader (см. <see cref="XfaDataBinding"/>).
///
/// Что изменилось, определяется сравнением со снимком значений, сделанным при
/// включении заполнения: так не важно, чем поле поменяли — мышью, клавиатурой
/// или списком, — а нетронутые поля в данных не переписываются вовсе.
///
/// Поля, которые делят один узел данных (название судна в шапке каждой
/// страницы), в Adobe Reader всегда равны — правка одного переносится на
/// остальные, как это делает Acrobat. Поля без места в данных XFA (привязка
/// «none», поля мастер-страниц) заполнять нельзя: их значение увидели бы все
/// программы, кроме Adobe Reader. Все вызовы — на PDFium-потоке.
/// </summary>
internal sealed class PdfiumXfaHybrid
{
    private readonly XfaDataBinding _binding;
    private readonly string? _packetName;
    private readonly string? _wholeXdp;
    private readonly Dictionary<string, FieldState> _snapshot;
    private readonly Dictionary<string, List<WidgetRef>> _widgets;
    private readonly HashSet<string> _locked;
    // Поле → все поля его общего узла данных (включая его самого).
    private readonly Dictionary<string, string[]> _peers = new(StringComparer.Ordinal);
    // Последнее согласованное значение каждого поля из общих узлов.
    private readonly Dictionary<string, FieldState> _peerLast = new(StringComparer.Ordinal);

    private PdfiumXfaHybrid(XfaDataBinding binding, string? packetName, string? wholeXdp,
        Dictionary<string, FieldState> snapshot, Dictionary<string, List<WidgetRef>> widgets)
    {
        _binding = binding;
        _packetName = packetName;
        _wholeXdp = wholeXdp;
        _snapshot = snapshot;
        _widgets = widgets;
        _locked = new HashSet<string>(
            snapshot.Where(f => f.Value.Editable && !binding.CanWrite(f.Key)).Select(f => f.Key),
            StringComparer.Ordinal);
        foreach (var group in binding.SharedFields)
        {
            var present = group.Where(n => widgets.ContainsKey(n) && !_locked.Contains(n)).ToArray();
            if (present.Length < 2) continue;
            foreach (var name in present)
            {
                _peers[name] = present;
                _peerLast[name] = snapshot[name];
            }
        }
    }

    /// <summary>Поля, которые нельзя заполнять: им нет места в данных XFA.</summary>
    public IReadOnlyCollection<string> LockedFields => _locked;

    public bool IsLocked(string fieldName) => _locked.Contains(fieldName);

    /// <summary>
    /// Разбор XFA-части и снимок значений. null — XFA-часть не читается
    /// (нет шаблона, испорченный XML): заполнять такую форму нельзя, иначе
    /// Adobe Reader показал бы прежние данные.
    /// </summary>
    public static PdfiumXfaHybrid? TryCreate(FpdfDocumentT document, FpdfFormHandleT form,
        Func<int, FpdfPageT?> activePage, out string? failure)
    {
        failure = null;
        try
        {
            var packets = ReadPackets(document);
            XfaDataBinding binding;
            string? packetName;
            string? wholeXdp = null;
            if (packets.Count == 1 && packets[0].Name.Length == 0)
            {
                wholeXdp = packets[0].Content ?? "";
                var (template, datasets) = XfaDataBinding.SplitXdp(wholeXdp);
                if (template == null) { failure = "в XFA нет шаблона"; return null; }
                binding = XfaDataBinding.Create(template, datasets);
                packetName = null;
            }
            else
            {
                var template = packets.FirstOrDefault(p => p.Name == "template").Content;
                if (template == null) { failure = "в XFA нет шаблона"; return null; }
                var datasets = packets.FirstOrDefault(p => p.Name == "datasets").Content;
                binding = XfaDataBinding.Create(template, datasets);
                packetName = "datasets";
            }

            var widgets = new Dictionary<string, List<WidgetRef>>(StringComparer.Ordinal);
            var snapshot = ReadStates(document, form, activePage, null, widgets);
            return new PdfiumXfaHybrid(binding, packetName, wholeXdp, snapshot, widgets);
        }
        catch (FormatException ex)
        {
            failure = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Пакет XFA с перенесёнными изменениями; null — ни одно поле не менялось.
    /// Перед этим общие поля согласуются: в файл уходят одинаковые значения.
    /// </summary>
    public XfaPacketUpdate? BuildUpdate(FpdfDocumentT document, FpdfFormHandleT form, Func<int, FpdfPageT?> activePage)
    {
        SyncPeers(document, form, activePage, candidate: null);
        var current = ReadStates(document, form, activePage, null, null);
        var changed = current
            .Where(f => !_snapshot.TryGetValue(f.Key, out var before) || !SameValue(before, f.Value))
            .Select(f => new XfaFieldValue(f.Key, f.Value.Value, f.Value.CheckedControl, f.Value.CheckedExport))
            .ToList();
        if (changed.Count == 0)
            return null;

        var datasets = _binding.Apply(changed, out _);
        var content = _packetName == null ? XfaDataBinding.ReplaceDatasetsInXdp(_wholeXdp!, datasets) : datasets;
        return new XfaPacketUpdate(_packetName, new UTF8Encoding(false).GetBytes(content));
    }

    /// <summary>
    /// Перенос значения на поля того же узла данных. <paramref name="candidate"/>
    /// — поле, которое могли только что изменить (проверяется лишь его группа);
    /// null — проверить все. Фокус ввода остаётся там, где был.
    /// </summary>
    public void SyncPeers(FpdfDocumentT document, FpdfFormHandleT form, Func<int, FpdfPageT?> activePage,
        string? candidate)
    {
        if (_peers.Count == 0) return;
        List<string[]> groups;
        if (candidate == null)
            groups = _peers.Values.Distinct().ToList();
        else if (_peers.TryGetValue(candidate, out var group))
            groups = new List<string[]> { group };
        else
            return;

        var focused = GetFocusedAnnot(form.__Instance, out _, out var annot) != 0 ? annot : IntPtr.Zero;
        try
        {
            foreach (var group in groups)
            {
                // Кого проверять: у одного кандидата — только его страница.
                var watched = candidate != null ? new[] { candidate } : group;
                var states = ReadStates(document, form, activePage, PagesOf(watched), null);
                var source = watched.FirstOrDefault(n =>
                    states.TryGetValue(n, out var now) && !SameValue(now, _peerLast[n]));
                if (source == null) continue;
                var value = states[source];
                var others = group.Where(n => n != source).ToArray();
                var current = ReadStates(document, form, activePage, PagesOf(others), null);
                foreach (var other in others)
                {
                    if (current.TryGetValue(other, out var state) && !SameValue(state, value))
                        Assign(document, form, activePage, other, state, value);
                }
                foreach (var member in group)
                    _peerLast[member] = value;
            }
        }
        finally
        {
            if (focused != IntPtr.Zero)
            {
                SetFocusedAnnot(form.__Instance, focused);
                CloseAnnot(focused);
            }
        }
    }

    private HashSet<int> PagesOf(IEnumerable<string> names) =>
        names.SelectMany(n => _widgets.TryGetValue(n, out var w) ? w.Select(x => x.Page) : Enumerable.Empty<int>())
            .ToHashSet();

    private static bool SameValue(FieldState a, FieldState b) =>
        a.Value == b.Value && a.CheckedControl == b.CheckedControl;

    // ----- Установка значения поля -----

    private const int FieldTypeText = 6;
    private const int FieldTypeComboBox = 4;
    private const int FieldTypeListBox = 5;

    private void Assign(FpdfDocumentT document, FpdfFormHandleT form, Func<int, FpdfPageT?> activePage,
        string name, FieldState current, FieldState value)
    {
        var widgets = _widgets[name];
        var first = widgets[0];
        switch (first.Type)
        {
            case FieldTypeText:
                OnWidget(document, form, activePage, first, (page, annot) =>
                {
                    if (fpdf_formfill.FORM_SetFocusedAnnot(form, annot) == 0) return;
                    fpdf_formfill.FORM_SelectAllText(form, page);
                    var text = (value.Value + "\0").Select(c => (ushort)c).ToArray();
                    fpdf_formfill.FORM_ReplaceSelection(form, page, ref text[0]);
                    fpdf_formfill.FORM_ForceToKillFocus(form);
                });
                break;
            case FieldTypeComboBox or FieldTypeListBox:
                OnWidget(document, form, activePage, first, (page, annot) =>
                {
                    var count = Math.Max(0, fpdf_annot.FPDFAnnotGetOptionCount(form, annot));
                    var index = Enumerable.Range(0, count).FirstOrDefault(
                        i => Read((b, n) => fpdf_annot.FPDFAnnotGetOptionLabel(form, annot, i, ref b[0], n)) == value.Value,
                        -1);
                    if (fpdf_formfill.FORM_SetFocusedAnnot(form, annot) == 0) return;
                    if (index >= 0)
                    {
                        fpdf_formfill.FORM_SetIndexSelected(form, page, index, 1);
                    }
                    else if (first.Type == FieldTypeComboBox)
                    {
                        // Свой текст в редактируемом списке.
                        fpdf_formfill.FORM_SelectAllText(form, page);
                        var text = (value.Value + "\0").Select(c => (ushort)c).ToArray();
                        fpdf_formfill.FORM_ReplaceSelection(form, page, ref text[0]);
                    }
                    fpdf_formfill.FORM_ForceToKillFocus(form);
                });
                break;
            default:
                // Флажок и переключатели — щелчком по нужному виджету, как мышью:
                // включить — по тому, что должен быть отмечен; выключить — по
                // отмеченному сейчас.
                var wanted = value.CheckedControl >= 0 ? value.CheckedControl : current.CheckedControl;
                if (wanted < 0) return;
                var target = widgets.FirstOrDefault(w => w.Control == wanted, first);
                OnWidget(document, form, activePage, target, (page, _) =>
                {
                    fpdf_formfill.FORM_ForceToKillFocus(form);
                    fpdf_formfill.FORM_OnLButtonDown(form, page, 0, target.X, target.Y);
                    fpdf_formfill.FORM_OnLButtonUp(form, page, 0, target.X, target.Y);
                    fpdf_formfill.FORM_ForceToKillFocus(form);
                });
                break;
        }
    }

    /// <summary>
    /// Действие над виджетом на странице, известной окружению форм: активная
    /// страница берётся как есть, остальные открываются на время действия.
    /// </summary>
    private static void OnWidget(FpdfDocumentT document, FpdfFormHandleT form, Func<int, FpdfPageT?> activePage,
        WidgetRef widget, Action<FpdfPageT, FpdfAnnotationT> action)
    {
        var active = activePage(widget.Page);
        var page = active ?? fpdfview.FPDF_LoadPage(document, widget.Page);
        if (page == null || page.__Instance == IntPtr.Zero) return;
        if (active == null) fpdf_formfill.FORM_OnAfterLoadPage(page, form);
        try
        {
            var annot = fpdf_annot.FPDFPageGetAnnot(page, widget.Annot);
            if (annot == null || annot.__Instance == IntPtr.Zero) return;
            try
            {
                action(page, annot);
            }
            finally
            {
                fpdf_annot.FPDFPageCloseAnnot(annot);
            }
        }
        finally
        {
            if (active == null)
            {
                fpdf_formfill.FORM_ForceToKillFocus(form);
                fpdf_formfill.FORM_OnBeforeClosePage(page, form);
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    // ----- Фокус ввода -----

    /// <summary>Имя поля в фокусе ввода; null — фокуса нет.</summary>
    public static string? FocusedFieldName(FpdfFormHandleT form)
    {
        // Обёртка PDFiumCore для FORM_GetFocusedAnnot не умеет вернуть
        // аннотацию через указатель — вызов идёт напрямую.
        if (GetFocusedAnnot(form.__Instance, out _, out var annot) == 0 || annot == IntPtr.Zero)
            return null;
        try
        {
            unsafe
            {
                var needed = GetFieldName(form.__Instance, annot, null, 0);
                if (needed <= 2) return "";
                var buffer = new ushort[needed / 2];
                fixed (ushort* p = buffer)
                    GetFieldName(form.__Instance, annot, p, needed);
                return Utf16(buffer);
            }
        }
        finally
        {
            CloseAnnot(annot);
        }
    }

    [DllImport("pdfium", EntryPoint = "FORM_GetFocusedAnnot", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetFocusedAnnot(IntPtr form, out int pageIndex, out IntPtr annot);

    [DllImport("pdfium", EntryPoint = "FORM_SetFocusedAnnot", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SetFocusedAnnot(IntPtr form, IntPtr annot);

    [DllImport("pdfium", EntryPoint = "FPDFAnnot_GetFormFieldName", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe ulong GetFieldName(IntPtr form, IntPtr annot, ushort* buffer, ulong length);

    [DllImport("pdfium", EntryPoint = "FPDFPage_CloseAnnot", CallingConvention = CallingConvention.Cdecl)]
    private static extern void CloseAnnot(IntPtr annot);

    // ----- Пакеты XFA -----

    private readonly record struct Packet(string Name, string? Content);

    private static List<Packet> ReadPackets(FpdfDocumentT document)
    {
        var count = fpdfview.FPDF_GetXFAPacketCount(document);
        var result = new List<Packet>(Math.Max(0, count));
        for (var i = 0; i < count; i++)
            result.Add(new Packet(PacketName(document, i), PacketContent(document, i)));
        return result;
    }

    private static string PacketName(FpdfDocumentT document, int index)
    {
        var needed = fpdfview.FPDF_GetXFAPacketName(document, index, IntPtr.Zero, 0);
        if (needed <= 1) return "";
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            fpdfview.FPDF_GetXFAPacketName(document, index, buffer, needed);
            return Marshal.PtrToStringUTF8(buffer) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? PacketContent(FpdfDocumentT document, int index)
    {
        ulong needed = 0;
        if (fpdfview.FPDF_GetXFAPacketContent(document, index, IntPtr.Zero, 0, ref needed) == 0 || needed == 0)
            return null;
        if (needed > 64UL * 1024 * 1024)
            throw new FormatException("Пакет XFA слишком велик.");
        var bytes = new byte[needed];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            ulong written = 0;
            if (fpdfview.FPDF_GetXFAPacketContent(document, index, handle.AddrOfPinnedObject(), needed, ref written) == 0)
                return null;
            // Кодировку задаёт метка порядка байтов; без неё XFA — UTF-8.
            using var reader = new StreamReader(new MemoryStream(bytes, 0, (int)Math.Min(written, needed)),
                new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        finally
        {
            handle.Free();
        }
    }

    // ----- Значения полей -----

    /// <summary>Состояние поля: значение и отмеченный виджет (у флажков и переключателей).</summary>
    private readonly record struct FieldState(string Value, int CheckedControl, string CheckedExport, bool Editable);

    /// <summary>Виджет поля: страница, номер аннотации, номер элемента управления, тип и центр.</summary>
    private readonly record struct WidgetRef(int Page, int Annot, int Control, int Type, double X, double Y);

    private const int AnnotSubtypeWidget = 20;
    private const int FieldTypePushButton = 1;
    private const int FieldTypeCheckBox = 2;
    private const int FieldTypeRadio = 3;
    private const int FieldTypeSignature = 7;

    /// <param name="pages">Какие страницы читать; null — все.</param>
    /// <param name="widgets">Куда сложить расположение виджетов; null — не нужно.</param>
    private static Dictionary<string, FieldState> ReadStates(
        FpdfDocumentT document, FpdfFormHandleT form, Func<int, FpdfPageT?> activePage,
        ISet<int>? pages, Dictionary<string, List<WidgetRef>>? widgets)
    {
        var states = new Dictionary<string, FieldState>(StringComparer.Ordinal);
        var pageCount = fpdfview.FPDF_GetPageCount(document);
        for (var p = 0; p < pageCount; p++)
        {
            if (pages != null && !pages.Contains(p)) continue;
            var active = activePage(p);
            var page = active ?? fpdfview.FPDF_LoadPage(document, p);
            if (page == null || page.__Instance == IntPtr.Zero) continue;
            // Отметку флажка pdfium сообщает только для страницы, известной
            // окружению форм: на просто открытой странице любой флажок читался
            // «снятым», и сохранение теряло отметки, сделанные на другой странице.
            if (active == null)
                fpdf_formfill.FORM_OnAfterLoadPage(page, form);
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
                        var name = Read((b, n) => fpdf_annot.FPDFAnnotGetFormFieldName(form, annot, ref b[0], n));
                        if (name.Length == 0) continue;
                        var type = fpdf_annot.FPDFAnnotGetFormFieldType(form, annot);
                        var control = Math.Max(0, fpdf_annot.FPDFAnnotGetFormControlIndex(form, annot));
                        if (!states.TryGetValue(name, out var state))
                        {
                            state = new FieldState(
                                Read((b, n) => fpdf_annot.FPDFAnnotGetFormFieldValue(form, annot, ref b[0], n)),
                                -1, "", type > 0 && type is not (FieldTypeSignature or FieldTypePushButton));
                        }
                        if (type is FieldTypeCheckBox or FieldTypeRadio && fpdf_annot.FPDFAnnotIsChecked(form, annot) != 0)
                        {
                            state = state with
                            {
                                CheckedControl = control,
                                CheckedExport = Read((b, n) =>
                                    fpdf_annot.FPDFAnnotGetFormFieldExportValue(form, annot, ref b[0], n)),
                            };
                        }
                        states[name] = state;

                        if (widgets != null)
                        {
                            var rect = new FS_RECTF_();
                            fpdf_annot.FPDFAnnotGetRect(annot, rect);
                            if (!widgets.TryGetValue(name, out var list))
                                widgets[name] = list = new List<WidgetRef>();
                            list.Add(new WidgetRef(p, i, control, type,
                                (rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2));
                        }
                    }
                    finally
                    {
                        fpdf_annot.FPDFPageCloseAnnot(annot);
                    }
                }
            }
            finally
            {
                if (active == null)
                {
                    fpdf_formfill.FORM_OnBeforeClosePage(page, form);
                    fpdfview.FPDF_ClosePage(page);
                }
            }
        }
        return states;
    }

    private delegate ulong StringCall(ushort[] buffer, ulong length);

    private static string Read(StringCall call)
    {
        var probe = new ushort[1];
        var needed = call(probe, 0);
        if (needed <= 2) return "";
        var buffer = new ushort[needed / 2];
        call(buffer, needed);
        return Utf16(buffer);
    }

    private static string Utf16(ushort[] buffer)
    {
        var length = Array.IndexOf(buffer, (ushort)0);
        if (length < 0) length = buffer.Length;
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = (char)buffer[i];
        return new string(chars);
    }
}
