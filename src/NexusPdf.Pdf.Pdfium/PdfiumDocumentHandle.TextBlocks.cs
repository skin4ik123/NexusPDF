using System.Globalization;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Abstractions.TextBlocks;
using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

/// <summary>
/// Чтение текстовых блоков, записанных программой, и шрифтов документа.
/// </summary>
internal sealed partial class PdfiumDocumentHandle
{
    private const int MaxDocumentFontNames = 512;

    /// <summary>Больше блоков на странице не бывает; лишние группы меток — подделка, остаются текстом.</summary>
    private const int MaxTextBlocksPerPage = 1000;

    public Task<IReadOnlyList<StoredTextBlock>> GetTextBlocksAsync(int pageIndex, int extraQuarterTurns, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync<IReadOnlyList<StoredTextBlock>>(() =>
            {
                var page = fpdfview.FPDF_LoadPage(NativeDoc, pageIndex);
                if (page == null || page.__Instance == IntPtr.Zero)
                    return [];
                var textPage = fpdf_text.FPDFTextLoadPage(page);
                try
                {
                    var reader = new PdfiumTextBlockMarks.Reader();
                    var groups = new Dictionary<string, List<(FpdfPageobjectT Obj, TextBlockMark Mark)>>(StringComparer.Ordinal);
                    var count = fpdf_edit.FPDFPageCountObjects(page);
                    for (var i = 0; i < count && !reader.Exhausted; i++)
                    {
                        if (i % 256 == 0)
                            ct.ThrowIfCancellationRequested();
                        var obj = fpdf_edit.FPDFPageGetObject(page, i);
                        if (obj == null || obj.__Instance == IntPtr.Zero)
                            continue;
                        if (reader.Read(obj) is not { } mark)
                            continue;
                        if (!groups.TryGetValue(mark.Id, out var members))
                        {
                            if (groups.Count >= MaxTextBlocksPerPage)
                                continue;
                            groups[mark.Id] = members = [];
                        }
                        members.Add((obj, mark));
                    }

                    var blocks = new List<StoredTextBlock>();
                    var texts = groups.Count > 0 ? TextByObject(textPage, ct) : [];
                    foreach (var (id, members) in groups)
                    {
                        ct.ThrowIfCancellationRequested();
                        // Метка с чужим Id — не наша запись: обычный текст.
                        if (Guid.TryParseExact(id, "N", out var guid))
                            blocks.Add(ReadStoredBlock(page, texts, reader, pageIndex, extraQuarterTurns, guid, members));
                    }
                    // Метки страницы дочитаны не до конца: ни один блок нельзя
                    // считать целым — у него могли остаться непрочитанные части.
                    return reader.Exhausted
                        ? blocks.Select(b => b with { Block = null, Intact = false }).ToList()
                        : blocks;
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

    /// <summary>
    /// Блок из объектов одной группы. Целый — только если всё сошлось:
    /// версия, ссылки на места текста, описание и отпечаток объектов.
    /// </summary>
    private StoredTextBlock ReadStoredBlock(
        FpdfPageT page, IReadOnlyDictionary<IntPtr, string> texts, PdfiumTextBlockMarks.Reader reader, int pageIndex, int extraQuarterTurns,
        Guid id, List<(FpdfPageobjectT Obj, TextBlockMark Mark)> members)
    {
        var intact = true;
        string? data = null, hash = null;
        var pieces = new List<TextPiece>();
        var fingerprints = new List<TextBlockHash.Piece>();
        float left = float.MaxValue, bottom = float.MaxValue, right = float.MinValue, top = float.MinValue;

        foreach (var (obj, mark) in members)
        {
            float l = 0, b = 0, r = 0, t = 0;
            if (fpdf_edit.FPDFPageObjGetBounds(obj, ref l, ref b, ref r, ref t) != 0)
            {
                left = Math.Min(left, l);
                bottom = Math.Min(bottom, b);
                right = Math.Max(right, r);
                top = Math.Max(top, t);
            }

            if (mark.Version != "1")
                intact = false;
            // Описание читается с первого объекта, где оно есть, и только с него.
            if (data == null)
                (data, hash) = reader.ReadPayload(obj);

            var type = fpdf_edit.FPDFPageObjGetType(obj);
            if (mark.Ref is { } reference && reference.StartsWith("d:", StringComparison.Ordinal) && type == PageObjectPath)
            {
                fingerprints.Add(PdfiumOverlayWriter.Fingerprint(obj, reference, isText: false));
            }
            else if (type == PageObjectText && TryParsePieceRef(mark.Ref, out var paragraph, out var start))
            {
                // Объект из одних пробелов PDFium в текст страницы не выдаёт:
                // пустой кусок — место пробелов, их длину знает описание блока.
                var text = texts.GetValueOrDefault(obj.__Instance, "");
                pieces.Add(new TextPiece(paragraph, start, text));
                fingerprints.Add(PdfiumOverlayWriter.Fingerprint(obj, mark.Ref!, isText: true));
            }
            else
            {
                intact = false;
            }
        }

        TextBlockOverlay? block = null;
        if (intact && data != null && hash != null &&
            string.Equals(TextBlockHash.Compute(fingerprints), hash, StringComparison.Ordinal))
            block = TextBlockCodec.Decode(data, pieces, id);

        var bounds = default(OverlayBox);
        if (left <= right && bottom <= top)
        {
            var (x, y, w, h) = ContentRectToDisplayed(page, pageIndex, extraQuarterTurns, left, bottom, right, top);
            bounds = new OverlayBox(x, y, w, h);
        }
        return new StoredTextBlock(id, block, bounds, block != null);
    }

    /// <summary>Сделать объекты наших блоков неактивными (не рисуются); возвращает их, чтобы вернуть.</summary>
    private static List<FpdfPageobjectT> HideTextBlocks(FpdfPageT page)
    {
        var hidden = new List<FpdfPageobjectT>();
        var reader = new PdfiumTextBlockMarks.Reader();
        var count = fpdf_edit.FPDFPageCountObjects(page);
        for (var i = 0; i < count && !reader.Exhausted; i++)
        {
            var obj = fpdf_edit.FPDFPageGetObject(page, i);
            if (obj == null || obj.__Instance == IntPtr.Zero || reader.Read(obj) == null)
                continue;
            fpdf_edit.FPDFPageObjSetIsActive(obj, 0);
            hidden.Add(obj);
        }
        return hidden;
    }

    public Task<bool> HasTextBesidesBlocksAsync(int pageIndex, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync(() =>
            {
                var page = fpdfview.FPDF_LoadPage(NativeDoc, pageIndex);
                if (page == null || page.__Instance == IntPtr.Zero)
                    return false;
                var textPage = fpdf_text.FPDFTextLoadPage(page);
                try
                {
                    if (textPage == null || textPage.__Instance == IntPtr.Zero)
                        return false;
                    var reader = new PdfiumTextBlockMarks.Reader();
                    var ours = new Dictionary<IntPtr, bool>();
                    var count = fpdf_text.FPDFTextCountChars(textPage);
                    for (var i = 0; i < count; i++)
                    {
                        if (i % 4096 == 0)
                            ct.ThrowIfCancellationRequested();
                        if (fpdf_text.FPDFTextIsGenerated(textPage, i) != 0)
                            continue;
                        var code = fpdf_text.FPDFTextGetUnicode(textPage, i);
                        if (code == 0 || (code <= 0xFFFF && char.IsWhiteSpace((char)code)))
                            continue;
                        var obj = fpdf_text.FPDFTextGetTextObject(textPage, i);
                        if (obj == null || obj.__Instance == IntPtr.Zero)
                            return true;
                        if (!ours.TryGetValue(obj.__Instance, out var own))
                            ours[obj.__Instance] = own = reader.Read(obj) != null;
                        if (!own)
                            return true;
                    }
                    return false;
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

    /// <summary>
    /// Текст каждого объекта страницы — ровно записанные в нём буквы. Готовый
    /// текст объекта у PDFium содержит и «сгенерированные» пробелы — там, где
    /// до следующего объекта есть зазор («one» читался бы как «one »), — и по
    /// нему кусок блока не совпал бы с описанием.
    /// </summary>
    private static Dictionary<IntPtr, string> TextByObject(FpdfTextpageT? textPage, CancellationToken ct)
    {
        var builders = new Dictionary<IntPtr, System.Text.StringBuilder>();
        if (textPage == null || textPage.__Instance == IntPtr.Zero)
            return [];
        var count = fpdf_text.FPDFTextCountChars(textPage);
        for (var i = 0; i < count; i++)
        {
            if (i % 4096 == 0)
                ct.ThrowIfCancellationRequested();
            if (fpdf_text.FPDFTextIsGenerated(textPage, i) != 0)
                continue;
            var obj = fpdf_text.FPDFTextGetTextObject(textPage, i);
            if (obj == null || obj.__Instance == IntPtr.Zero)
                continue;
            if (!builders.TryGetValue(obj.__Instance, out var builder))
                builders[obj.__Instance] = builder = new System.Text.StringBuilder();
            var code = fpdf_text.FPDFTextGetUnicode(textPage, i);
            if (code > 0xFFFF && code <= 0x10FFFF)
                builder.Append(char.ConvertFromUtf32((int)code));
            else if (code is > 0 and <= 0xFFFF)
                builder.Append((char)code);
        }
        return builders.ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
    }

    /// <summary>"абзац:начало" — неотрицательные числа без знаков и пробелов.</summary>
    private static bool TryParsePieceRef(string? reference, out int paragraph, out int start)
    {
        paragraph = start = 0;
        var colon = reference?.IndexOf(':') ?? -1;
        return colon > 0 &&
               int.TryParse(reference.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out paragraph) &&
               int.TryParse(reference.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out start);
    }

    public Task<IReadOnlyList<string>> GetDocumentFontNamesAsync(int maxPages, CancellationToken ct)
    {
        lock (_admissionGate)
        {
            ThrowIfDisposed();
            return _thread.InvokeAsync<IReadOnlyList<string>>(() =>
            {
                var names = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var pages = Math.Min(Math.Max(maxPages, 0), fpdfview.FPDF_GetPageCount(NativeDoc));
                for (var p = 0; p < pages && names.Count < MaxDocumentFontNames; p++)
                {
                    ct.ThrowIfCancellationRequested();
                    var page = fpdfview.FPDF_LoadPage(NativeDoc, p);
                    if (page == null || page.__Instance == IntPtr.Zero)
                        continue;
                    try
                    {
                        foreach (var node in PdfObjectTree.TextObjects(page))
                        {
                            var font = fpdf_edit.FPDFTextObjGetFont(node.Object);
                            if (font == null || font.__Instance == IntPtr.Zero)
                                continue;
                            var name = ReadFontName(font);
                            if (name.Length > 0 && seen.Add(name))
                                names.Add(name);
                            if (names.Count >= MaxDocumentFontNames)
                                break;
                        }
                    }
                    finally
                    {
                        fpdfview.FPDF_ClosePage(page);
                    }
                }
                return names;
            }, ct);
        }
    }
}
