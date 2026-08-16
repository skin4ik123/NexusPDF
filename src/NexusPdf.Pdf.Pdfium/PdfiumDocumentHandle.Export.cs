using NexusPdf.Pdf.Abstractions;
using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

/// <summary>
/// Геометрия содержимого страницы для экспорта в Word и Excel.
///
/// В PDF нет ни слов, ни строк, ни таблиц — есть символы с координатами и
/// нарисованные линии. Здесь добывается ровно это сырьё, а восстановлением
/// структуры занимается отдельный, полностью проверяемый тестами код без
/// нативных зависимостей.
/// </summary>
internal sealed partial class PdfiumDocumentHandle
{
    private const int PageObjectPath = 2;  // FPDF_PAGEOBJ_PATH
    private const int PageObjectForm = 5;  // FPDF_PAGEOBJ_FORM

    /// <summary>
    /// Тоньше этого — граница таблицы или подчёркивание; толще — уже заливка
    /// (фон строки, плашка), и границей считать её нельзя.
    /// </summary>
    private const double MaxRulingThicknessPt = 3.0;

    /// <summary>Короче этого линия не несёт структуры: точка, маркер, засечка.</summary>
    private const double MinRulingLengthPt = 8.0;

    /// <summary>Глубже вложенные XObject'ы не бывают у реальных генераторов.</summary>
    private const int MaxFormDepth = 6;

    public Task<IReadOnlyList<PdfTextWord>> GetTextWordsAsync(int pageIndex, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync<IReadOnlyList<PdfTextWord>>(
                () => CollectWords(pageIndex, ct), ct);
        }
    }

    public Task<IReadOnlyList<PdfRulingLine>> GetRulingLinesAsync(int pageIndex, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync<IReadOnlyList<PdfRulingLine>>(
                () => CollectRulingLines(pageIndex, ct), ct);
        }
    }

    /// <summary>
    /// Заполненные поля формы вместе с их рамками.
    ///
    /// Экспорт без них был бы обманом: значение поля НЕ входит в текст страницы,
    /// и заполненная анкета превратилась бы в пустой бланк.
    /// </summary>
    public Task<IReadOnlyList<PdfFormFieldValue>> GetFormFieldValuesAsync(int pageIndex, CancellationToken ct)
    {
        const int subtypeWidget = 20;

        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync<IReadOnlyList<PdfFormFieldValue>>(() =>
            {
                var page = fpdfview.FPDF_LoadPage(NativeDoc, pageIndex);
                if (page == null || page.__Instance == IntPtr.Zero)
                    return Array.Empty<PdfFormFieldValue>();
                try
                {
                    var fields = new List<PdfFormFieldValue>();
                    var count = fpdf_annot.FPDFPageGetAnnotCount(page);
                    for (var i = 0; i < count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var annot = fpdf_annot.FPDFPageGetAnnot(page, i);
                        if (annot == null || annot.__Instance == IntPtr.Zero) continue;
                        try
                        {
                            if (fpdf_annot.FPDFAnnotGetSubtype(annot) != subtypeWidget) continue;

                            var value = GetAnnotString(annot, "V");
                            // У флажков и переключателей значение хранится
                            // состоянием внешнего вида, а не строкой /V.
                            if (value.Length == 0) value = GetAnnotString(annot, "AS");
                            if (value.Length == 0 || value == "Off") continue;

                            var rect = new FS_RECTF_();
                            if (fpdf_annot.FPDFAnnotGetRect(annot, rect) == 0) continue;

                            fields.Add(new PdfFormFieldValue(
                                GetAnnotString(annot, "T"),
                                value,
                                new PdfTextRect(
                                    Math.Min(rect.Left, rect.Right), Math.Max(rect.Top, rect.Bottom),
                                    Math.Max(rect.Left, rect.Right), Math.Min(rect.Top, rect.Bottom))));
                        }
                        finally
                        {
                            fpdf_annot.FPDFPageCloseAnnot(annot);
                        }
                    }
                    return fields;
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }, ct);
        }
    }

    /// <summary>
    /// Картинки страницы с их местом. Крошечные отбрасываются: линейки,
    /// маркеры и однопиксельные распорки в Word не нужны, а замусорить
    /// документ ими проще простого.
    /// </summary>
    /// <param name="maxPixels">
    /// Предел суммарной площади: страница-скан в 300 dpi — это 34 МБ в
    /// памяти, и без предела десяток таких страниц кладёт процесс.
    /// </param>
    public Task<IReadOnlyList<PdfPageImage>> GetPageImagesAsync(
        int pageIndex, long maxPixels, CancellationToken ct)
    {
        const double minSidePt = 6.0;
        const int minPixelSide = 8;

        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync<IReadOnlyList<PdfPageImage>>(() =>
            {
                var page = fpdfview.FPDF_LoadPage(NativeDoc, pageIndex);
                if (page == null || page.__Instance == IntPtr.Zero)
                    return Array.Empty<PdfPageImage>();
                try
                {
                    var images = new List<PdfPageImage>();
                    long pixels = 0;
                    var count = fpdf_edit.FPDFPageCountObjects(page);
                    for (var i = 0; i < count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var obj = fpdf_edit.FPDFPageGetObject(page, i);
                        if (obj == null || obj.__Instance == IntPtr.Zero) continue;
                        if (fpdf_edit.FPDFPageObjGetType(obj) != PageObjectImage) continue;

                        float left = 0, bottom = 0, right = 0, top = 0;
                        if (fpdf_edit.FPDFPageObjGetBounds(obj, ref left, ref bottom, ref right, ref top) == 0)
                            continue;
                        var width = Math.Abs(right - left);
                        var height = Math.Abs(top - bottom);
                        if (width < minSidePt || height < minSidePt) continue;

                        var bitmap = fpdf_edit.FPDFImageObjGetBitmap(obj);
                        if (bitmap == null || bitmap.__Instance == IntPtr.Zero) continue;
                        try
                        {
                            var bgra = BitmapToBgra(bitmap, out var bw, out var bh);
                            if (bgra == null || bw < minPixelSide || bh < minPixelSide) continue;
                            if (pixels + (long)bw * bh > maxPixels) break;
                            pixels += (long)bw * bh;

                            images.Add(new PdfPageImage(bgra, bw, bh, new PdfTextRect(
                                Math.Min(left, right), Math.Max(top, bottom),
                                Math.Max(left, right), Math.Min(top, bottom))));
                        }
                        finally
                        {
                            fpdfview.FPDFBitmapDestroy(bitmap);
                        }
                    }
                    return images;
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }, ct);
        }
    }

    // ----- слова -----

    private IReadOnlyList<PdfTextWord> CollectWords(int pageIndex, CancellationToken ct)
    {
        var page = fpdfview.FPDF_LoadPage(NativeDoc, pageIndex);
        if (page == null || page.__Instance == IntPtr.Zero)
            return Array.Empty<PdfTextWord>();
        try
        {
            var textPage = fpdf_text.FPDFTextLoadPage(page);
            if (textPage == null || textPage.__Instance == IntPtr.Zero)
                return Array.Empty<PdfTextWord>();
            try
            {
                return BuildWords(textPage, ct);
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
    }

    /// <summary>
    /// Символы → слова.
    ///
    /// Слово рвётся на пробеле, на переводе строки и там, где между соседними
    /// символами зазор больше доли кегля: в PDF пробел часто не печатают вовсе,
    /// а просто сдвигают перо.
    ///
    /// Повёрнутый текст собирается вдоль СВОЕЙ оси. В шапках форм подпись
    /// сплошь и рядом стоит вертикально, и по горизонтальному правилу она
    /// рассыпалась бы на отдельные буквы.
    /// </summary>
    private static IReadOnlyList<PdfTextWord> BuildWords(FpdfTextpageT textPage, CancellationToken ct)
    {
        var count = fpdf_text.FPDFTextCountChars(textPage);
        if (count <= 0) return Array.Empty<PdfTextWord>();

        var words = new List<PdfTextWord>();
        var pending = new System.Text.StringBuilder();
        double left = 0, top = 0, right = 0, bottom = 0;
        double sizeSum = 0;
        int weightSum = 0, styled = 0;
        uint color = 0xFF000000;
        var font = string.Empty;
        double previousLeft = 0, previousRight = 0, previousTop = 0, previousBottom = 0;
        double previousSize = 0;
        var previousVertical = false;
        var quarters = 0;
        var open = false;

        void Flush()
        {
            if (!open) return;
            var text = pending.ToString();
            if (text.Length > 0 && right > left && top > bottom)
            {
                words.Add(new PdfTextWord(
                    text,
                    new PdfTextRect(left, top, right, bottom),
                    styled > 0 ? sizeSum / styled : 0,
                    styled > 0 ? (int)Math.Round((double)weightSum / styled) : 400,
                    color,
                    quarters,
                    font));
            }
            pending.Clear();
            open = false;
            sizeSum = 0;
            weightSum = 0;
            styled = 0;
        }

        for (var i = 0; i < count; i++)
        {
            if ((i & 0x3FF) == 0) ct.ThrowIfCancellationRequested();

            var unicode = fpdf_text.FPDFTextGetUnicode(textPage, i);
            var ch = unicode == 0 ? '\0' : (char)unicode;
            if (ch is '\0' or '\r' or '\n' or '\t' || char.IsWhiteSpace(ch))
            {
                Flush();
                continue;
            }

            double cl = 0, cr = 0, cb = 0, cttop = 0;
            fpdf_text.FPDFTextGetCharBox(textPage, i, ref cl, ref cr, ref cb, ref cttop);
            if (double.IsNaN(cl) || double.IsNaN(cr) || cr < cl)
                continue;

            var size = fpdf_text.FPDFTextGetFontSize(textPage, i);
            // Угол поворота символа: около ±90° — текст идёт вертикально.
            var angle = fpdf_text.FPDFTextGetCharAngle(textPage, i);
            var vertical = angle >= 0 && Math.Abs(Math.Cos(angle)) < 0.5;

            if (open)
            {
                var reference = Math.Max(size, previousSize);
                bool sameRun;
                double gap;
                if (vertical)
                {
                    // Вертикальная строка: соседние буквы стоят в одной колонке,
                    // а зазор считается по высоте — вверх или вниз, смотря куда
                    // читается надпись.
                    sameRun = Math.Abs((cl + cr) / 2.0 - (previousLeft + previousRight) / 2.0)
                              <= Math.Max(1.0, reference * 0.5);
                    gap = Math.Max(cb - previousTop, previousBottom - cttop);
                }
                else
                {
                    sameRun = Math.Abs((cttop + cb) / 2.0 - (previousTop + previousBottom) / 2.0)
                              <= Math.Max(1.0, reference * 0.5);
                    // Зазор берётся в обе стороны: письмо справа налево (иврит,
                    // арабский) идёт в другую сторону, но словом быть не перестаёт.
                    gap = Math.Max(cl - previousRight, previousLeft - cr);
                }

                if (vertical != previousVertical || !sameRun ||
                    gap > Math.Max(0.8, reference * 0.45))
                    Flush();
            }

            if (!open)
            {
                left = cl; right = cr; top = cttop; bottom = cb;
                quarters = QuarterTurns(angle);
                open = true;
            }
            else
            {
                left = Math.Min(left, cl);
                right = Math.Max(right, cr);
                top = Math.Max(top, cttop);
                bottom = Math.Min(bottom, cb);
            }

            pending.Append(ch);
            if (size > 0)
            {
                sizeSum += size;
                var weight = fpdf_text.FPDFTextGetFontWeight(textPage, i);
                weightSum += weight > 0 ? weight : 400;
                styled++;
            }
            if (pending.Length == 1)
            {
                color = ReadFillColor(textPage, i);
                font = ReadFontName(textPage, i);
            }

            previousLeft = cl;
            previousRight = cr;
            previousTop = cttop;
            previousBottom = cb;
            previousVertical = vertical;
            previousSize = size > 0 ? size : previousSize;
        }

        Flush();
        return words;
    }

    /// <summary>
    /// Угол символа в четверти поворота ПРОТИВ часовой стрелки: 1 — текст
    /// читается снизу вверх, 3 — сверху вниз.
    ///
    /// PDFium отсчитывает угол в другую сторону — у подписи, которая читается
    /// снизу вверх, он сообщает 270°, а не 90°. Проверено на настоящем
    /// документе, поэтому здесь направление разворачивается один раз, и
    /// остальной программе достаётся привычная математическая четверть.
    ///
    /// Наклонный текст, не попавший ни в одну четверть, считается обычным:
    /// перечитывать его по диагонали всё равно нечем.
    /// </summary>
    private static int QuarterTurns(double angle)
    {
        if (angle < 0 || double.IsNaN(angle)) return 0;
        var quarter = (int)Math.Round(angle / (Math.PI / 2.0)) & 3;
        var exact = Math.Abs(angle - quarter * Math.PI / 2.0);
        // Ровно на четверть — с запасом в 15°: у повёрнутого текста угол
        // приходит не идеально круглым.
        if (exact > Math.PI / 12.0) return 0;
        return (4 - quarter) & 3;
    }

    /// <summary>
    /// Имя шрифта символа в человеческом виде.
    ///
    /// В PDF оно приходит как «ABCDEF+TimesNewRoman,Bold»: шесть букв впереди —
    /// метка вшитого подмножества, хвост после запятой — начертание, которое в
    /// Word задаётся отдельно. И то и другое здесь снимается, иначе Word будет
    /// искать несуществующий шрифт и подставит свой.
    /// </summary>
    private static string ReadFontName(FpdfTextpageT textPage, int index)
    {
        const int capacity = 128;
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(capacity);
        try
        {
            var flags = 0;
            var written = fpdf_text.FPDFTextGetFontInfo(textPage, index, buffer, capacity, ref flags);
            if (written <= 1 || written > capacity) return string.Empty;

            var bytes = new byte[written - 1]; // без завершающего нуля
            System.Runtime.InteropServices.Marshal.Copy(buffer, bytes, 0, bytes.Length);
            var name = System.Text.Encoding.UTF8.GetString(bytes);

            var plus = name.IndexOf('+');
            if (plus == 6) name = name[(plus + 1)..];
            var comma = name.IndexOf(',');
            if (comma > 0) name = name[..comma];
            var dash = name.IndexOf('-');
            if (dash > 0) name = name[..dash];

            return SpaceOutCamelCase(name.Trim());
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>«TimesNewRoman» → «Times New Roman»: именно так шрифт зовут в Word.</summary>
    private static string SpaceOutCamelCase(string name)
    {
        if (name.Length == 0 || name.Contains(' ')) return name;
        var result = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                result.Append(' ');
            result.Append(name[i]);
        }
        return result.ToString();
    }

    private static uint ReadFillColor(FpdfTextpageT textPage, int index)
    {
        uint r = 0, g = 0, b = 0, a = 255;
        if (fpdf_text.FPDFTextGetFillColor(textPage, index, ref r, ref g, ref b, ref a) == 0)
            return 0xFF000000;
        // Полностью прозрачный текст — это слой OCR под сканом: цвет чёрный,
        // иначе в Word он окажется невидимым.
        if (a == 0) return 0xFF000000;
        return (0xFFu << 24) | ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);
    }

    // ----- линии -----

    private IReadOnlyList<PdfRulingLine> CollectRulingLines(int pageIndex, CancellationToken ct)
    {
        var page = fpdfview.FPDF_LoadPage(NativeDoc, pageIndex);
        if (page == null || page.__Instance == IntPtr.Zero)
            return Array.Empty<PdfRulingLine>();
        try
        {
            var lines = new List<PdfRulingLine>();
            var count = fpdf_edit.FPDFPageCountObjects(page);
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var obj = fpdf_edit.FPDFPageGetObject(page, i);
                if (obj == null || obj.__Instance == IntPtr.Zero) continue;
                CollectRulingFrom(obj, lines, 0, ct);
            }
            return lines;
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    /// <summary>
    /// Таблицы часто лежат внутри Form XObject (так делает, например, экспорт из
    /// Word), поэтому обход рекурсивный. Границы дочерних объектов PDFium уже
    /// отдаёт в координатах страницы, пересчитывать матрицу не нужно.
    /// </summary>
    private static void CollectRulingFrom(
        FpdfPageobjectT obj, List<PdfRulingLine> lines, int depth, CancellationToken ct)
    {
        var type = fpdf_edit.FPDFPageObjGetType(obj);

        if (type == PageObjectForm)
        {
            if (depth >= MaxFormDepth) return;
            var inner = fpdf_edit.FPDFFormObjCountObjects(obj);
            for (var i = 0; i < inner; i++)
            {
                ct.ThrowIfCancellationRequested();
                var child = fpdf_edit.FPDFFormObjGetObject(obj, (uint)i);
                if (child == null || child.__Instance == IntPtr.Zero) continue;
                CollectRulingFrom(child, lines, depth + 1, ct);
            }
            return;
        }

        if (type != PageObjectPath) return;
        if (!IsVisible(obj)) return;

        float l = 0, b = 0, r = 0, t = 0;
        if (fpdf_edit.FPDFPageObjGetBounds(obj, ref l, ref b, ref r, ref t) == 0) return;

        var width = r - l;
        var height = t - b;
        if (double.IsNaN(width) || double.IsNaN(height)) return;

        if (height <= MaxRulingThicknessPt && width >= MinRulingLengthPt)
            lines.Add(new PdfRulingLine(true, (t + b) / 2.0, l, r, Math.Max(height, 0.1)));
        else if (width <= MaxRulingThicknessPt && height >= MinRulingLengthPt)
            lines.Add(new PdfRulingLine(false, (l + r) / 2.0, b, t, Math.Max(width, 0.1)));
    }

    /// <summary>
    /// Линия считается нарисованной, только если её действительно видно:
    /// невидимые вспомогательные пути (нулевая альфа) границами не являются.
    /// </summary>
    private static bool IsVisible(FpdfPageobjectT obj)
    {
        uint r = 0, g = 0, b = 0, a = 0;
        if (fpdf_edit.FPDFPageObjGetFillColor(obj, ref r, ref g, ref b, ref a) != 0 && a > 0)
            return true;

        uint sr = 0, sg = 0, sb = 0, sa = 0;
        if (fpdf_edit.FPDFPageObjGetStrokeColor(obj, ref sr, ref sg, ref sb, ref sa) != 0 && sa > 0)
        {
            float stroke = 0;
            // Нулевая ширина штриха в PDF — это самая тонкая линия устройства,
            // а не отсутствие линии.
            return fpdf_edit.FPDFPageObjGetStrokeWidth(obj, ref stroke) == 0
                   || stroke <= MaxRulingThicknessPt;
        }

        return false;
    }
}
