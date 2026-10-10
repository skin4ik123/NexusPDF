using System.IO;
using MuPDF.NET;
using FileInfo = System.IO.FileInfo;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Pdf.MuPdf;

/// <summary>
/// Сжатие через MuPDF (Artifex): структура, шрифты и дедупликация — движком
/// (garbage=4, SubsetFonts), а изображения — СВОИМ конвейером по одному.
///
/// Прежняя версия отдавала изображения RewriteImage самого MuPDF, и на
/// реальных сканах он молча не уменьшал их вовсе: файл из JPEG 171 DPI при
/// цели 100 DPI выходил с теми же 2000 пикселями ширины, только пережатыми
/// низким качеством. Получалось худшее из двух миров — мыло без выигрыша.
/// Теперь каждое изображение решается отдельно, и у каждого решения есть
/// откат: замена принимается только когда новый поток ощутимо меньше старого.
///
/// Правила конвейера:
///  - плотность считается по ФАКТИЧЕСКОМУ размещению на страницах, а не по
///    заголовку изображения: скан «2000 пикселей на всю A4» — это 171 DPI;
///  - заметно плотнее цели — уменьшение до цели и кодирование в JPEG;
///  - JPEG в цели тоже проверяется с выбранным качеством, но принимается
///    только существенное уменьшение, без бесполезного повторного кодирования;
///  - несжатые и Flate-изображения в цели — пробуем JPEG без уменьшения;
///  - 1-битные, CCITT, JBIG2, JPX и изображения с прозрачностью (SMask) не
///    трогаются вовсе;
///  - замена засчитывается, только если новый поток меньше старого минимум
///    на 15 %.
///
/// Лицензия MuPDF — AGPL-3.0 (см. docs/LICENSES.md). Нативная библиотека не
/// потокобезопасна для одновременных документов: все вызовы под семафором.
/// </summary>
public sealed class MuPdfCompressionEngine : IPdfCompressionEngine
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly Lazy<string?> _probe = new(Probe);

    /// <summary>
    /// Файл считается годным, только если стал заметно меньше: выигрыш в
    /// полпроцента не стоит пересобранного документа.
    /// </summary>
    private const double WorthwhileFileRatio = 0.985;

    /// <summary>Замена одного изображения должна экономить хотя бы 15 % его потока.</summary>
    private const double WorthwhileImageRatio = 0.85;

    /// <summary>Плотность выше цели на треть — уменьшаем; меньше — не выдумываем пиксели.</summary>
    private const double DownsampleTrigger = 1.30;

    public bool IsAvailable => _probe.Value == null;

    public string UnavailableReason => _probe.Value ?? "";

    /// <summary>Проверка нативной библиотеки: без неё честно говорим, что движка нет.</summary>
    private static string? Probe()
    {
        try
        {
            using var doc = new Document();
            return null;
        }
        catch (Exception ex)
        {
            return $"Библиотека MuPDF недоступна: {ex.Message}";
        }
    }

    public async Task<PdfCompressionResult> CompressAsync(
        string sourcePath, string targetPath, PdfCompressionRequest request, CancellationToken ct)
    {
        if (!IsAvailable)
            throw new PdfEngineException(UnavailableReason);

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => CompressCore(sourcePath, targetPath, request, ct), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static PdfCompressionResult CompressCore(
        string sourcePath, string targetPath, PdfCompressionRequest request, CancellationToken ct)
    {
        var before = new FileInfo(sourcePath).Length;
        var work = targetPath + ".mupdf";
        var recompressed = 0;
        var skipped = 0;
        var expectedPages = 0;
        try
        {
            ct.ThrowIfCancellationRequested();
            using (var doc = new Document(sourcePath))
            {
                if (doc.NeedsPass && doc.Authenticate("") == 0)
                    throw new PdfEngineException(
                        "Документ защищён паролем: пересохранение сняло бы защиту.");

                expectedPages = doc.PageCount;
                if (!request.StructureOnly)
                    (recompressed, skipped) = RecompressImages(doc, request, ct);

                ct.ThrowIfCancellationRequested();
                if (request.SubsetFonts)
                {
                    try
                    {
                        doc.SubsetFonts();
                    }
                    catch (Exception)
                    {
                        // Урезание шрифтов — приятный бонус, а не обязанность:
                        // на экзотических шрифтах оно может не получиться.
                    }
                }

                ct.ThrowIfCancellationRequested();
                // garbage=4 — единственный доступный нам способ выбросить
                // ПОВТОРЯЮЩИЕСЯ объекты: ни PDFium, ни qpdf этого не делают.
                doc.Save(work,
                    garbage: 4, clean: 1, deflate: 1, deflateImages: 1, deflateFonts: 1,
                    useObjstms: 1, preserveMetadata: 1);
            }

            // Пересобранный документ обязан открываться и держать все страницы:
            // молча отдать битый файл — хуже, чем не сжать вовсе.
            using (var check = new Document(work))
            {
                if (check.PageCount != expectedPages || check.PageCount <= 0)
                    throw new PdfEngineException("Пересобранный документ не читается.");
            }

            var after = new FileInfo(work).Length;
            if (after >= before * WorthwhileFileRatio)
            {
                // Файл не стал меньше — отдаём исходник как есть. Пересобранный
                // документ без выигрыша в размере это только риск и потеря
                // качества.
                File.Copy(sourcePath, targetPath, overwrite: true);
                return new PdfCompressionResult(before, before, 0, recompressed + skipped, KeptOriginal: true);
            }

            File.Move(work, targetPath, overwrite: true);
            return new PdfCompressionResult(before, after, recompressed, skipped, KeptOriginal: false);
        }
        finally
        {
            try { if (File.Exists(work)) File.Delete(work); } catch { /* лучшая попытка */ }
        }
    }

    // ----- Пер-картиночный конвейер -----

    private static (int Recompressed, int Skipped) RecompressImages(
        Document doc, PdfCompressionRequest request, CancellationToken ct)
    {
        var recompressed = 0;
        var skipped = 0;

        // Одна картинка может стоять на многих страницах: решение принимается
        // один раз по САМОМУ ПЛОТНОМУ размещению, иначе уменьшенная под обложку
        // копия расползётся там, где её печатают крупно.
        var plan = new Dictionary<int, ImagePlacement>();
        for (var pageIndex = 0; pageIndex < doc.PageCount; pageIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var page = doc[pageIndex];
            foreach (var entry in doc.GetPageImages(pageIndex, false))
            {
                if (entry.Xref <= 0) continue;
                if (!plan.TryGetValue(entry.Xref, out var placement))
                    plan[entry.Xref] = placement = new ImagePlacement(entry, pageIndex);

                try
                {
                    foreach (var rect in page.GetImageRects((object)entry.Xref))
                    {
                        placement.MaxShownWidthPt = Math.Max(placement.MaxShownWidthPt, Math.Abs(rect.Width));
                        placement.MaxShownHeightPt = Math.Max(placement.MaxShownHeightPt, Math.Abs(rect.Height));
                    }
                }
                catch (Exception)
                {
                    // Размещение не прочиталось (картинка внутри формы и т.п.) —
                    // плотность останется неизвестной, решение будет осторожным.
                }
            }
        }

        foreach (var placement in plan.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (ProcessImage(doc, placement, request))
                recompressed++;
            else
                skipped++;
        }

        return (recompressed, skipped);
    }

    private sealed class ImagePlacement
    {
        public ImagePlacement(Entry entry, int firstPageIndex)
        {
            Entry = entry;
            FirstPageIndex = firstPageIndex;
        }

        public Entry Entry { get; }
        public int FirstPageIndex { get; }
        public double MaxShownWidthPt { get; set; }
        public double MaxShownHeightPt { get; set; }
    }

    /// <summary>true — изображение заменено меньшим; false — оставлено как было.</summary>
    private static bool ProcessImage(Document doc, ImagePlacement placement, PdfCompressionRequest request)
    {
        var entry = placement.Entry;

        // Прозрачность, битональные и «особые» кодеки не трогаем: JPEG их
        // только испортит, а CCITT/JBIG2 и так плотнее любого JPEG.
        if (!HasOnlyOpaqueMask(doc, entry)) return false;
        if (entry.Bpc <= 1) return false;
        var filter = entry.Filter ?? "";
        if (filter.Contains("CCITT", StringComparison.OrdinalIgnoreCase) ||
            filter.Contains("JBIG2", StringComparison.OrdinalIgnoreCase) ||
            filter.Contains("JPX", StringComparison.OrdinalIgnoreCase))
            return false;

        // Плотность по размещению. Безразмерное размещение (не нашли) — плотность
        // неизвестна: уменьшать нельзя, пережать без потерь размера можно.
        double scale = 1.0;
        if (placement.MaxShownWidthPt > 1 && entry.Width > 16)
        {
            var dpiX = entry.Width / (placement.MaxShownWidthPt / 72.0);
            var dpiY = placement.MaxShownHeightPt > 1
                ? entry.Height / (placement.MaxShownHeightPt / 72.0)
                : dpiX;
            var dpi = Math.Max(dpiX, dpiY);
            if (dpi > request.TargetDpi * DownsampleTrigger)
                scale = request.TargetDpi / dpi;
        }

        // Quality can reduce an oversized JPEG even without downsampling.
        // The 15% size gate below rejects pointless repeated recompression.

        var replacingOriginal = false;
        long oldLength = ReadStreamLength(doc, entry.Xref);
        if (oldLength <= 2048) return false; // мелочь не окупает ни риска, ни времени

        try
        {
            var extracted = doc.ExtractImage(entry.Xref);
            if (extracted?.Image is not { Length: > 0 } bytes) return false;

            using var source = new Pixmap(bytes);
            var pix = source;
            var owned = false;
            try
            {
                // CMYK и прочие небазовые пространства приводим к RGB: JPEG
                // с четырьмя компонентами читается не всеми просмотрщиками.
                if (pix.N - pix.Alpha > 3)
                {
                    pix = new Pixmap(Colorspace.Rgb, pix);
                    owned = true;
                }

                if (scale < 0.999)
                {
                    var newW = Math.Max(16, (int)Math.Round(entry.Width * scale));
                    var newH = Math.Max(16, (int)Math.Round(entry.Height * scale));
                    var shrunk = new Pixmap(pix, newW, newH, null!);
                    if (owned) pix.Dispose();
                    pix = shrunk;
                    owned = true;
                }

                var jpeg = pix.ToBytes("jpeg", request.Quality);
                if (jpeg.Length >= oldLength * WorthwhileImageRatio)
                    return false; // выигрыш не стоит потери качества

                // Build the replacement independently and copy it over the same XObject.
                // Page.ReplaceImage inserts a temporary drawing and can clear
                // existing content when insertion reuses an image/content xref.
                var replacement = doc.GetNewXref();
                var colourSpace = pix.N - pix.Alpha == 1 ? "/DeviceGray" : "/DeviceRGB";
                doc.UpdateObject(replacement,
                    $"<< /Type /XObject /Subtype /Image /Width {pix.W} /Height {pix.H} " +
                    $"/ColorSpace {colourSpace} /BitsPerComponent 8 >>");
                doc.UpdateStream(replacement, jpeg, compress: false);
                doc.XrefSetKey(replacement, "Filter", "/DCTDecode");
                replacingOriginal = true;
                doc.XrefCopy(replacement, entry.Xref, new[] { "OC", "Interpolate" });
                return true;
            }
            finally
            {
                if (owned) pix.Dispose();
            }
        }
        catch (Exception ex)
        {
            if (replacingOriginal)
                throw new PdfEngineException("Не удалось безопасно заменить изображение; исходный документ сохранён.", ex);
            // Одна несговорчивая картинка не должна срывать сжатие документа:
            // она просто остаётся как была.
            return false;
        }
    }

    private static bool HasOnlyOpaqueMask(Document doc, Entry entry)
    {
        try
        {
            // A colour-key/hard mask also represents real transparency.
            if (doc.XrefGetKey(entry.Xref, "Mask").Item1 != "null") return false;
            if (entry.Smask <= 0) return true;
            // Older NexusPDF imports wrote an all-white SMask even for JPEGs.
            // Decode it before deciding: genuine alpha must never become JPEG.
            if ((long)entry.Width * entry.Height > 40_000_000) return false;
            using var mask = new Pixmap(doc, entry.Smask);
            return mask.N == 1 && mask.SAMPLES.All(sample => sample == 255);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static long ReadStreamLength(Document doc, int xref)
    {
        try
        {
            var (_, value) = doc.XrefGetKey(xref, "Length");
            return long.TryParse(value, out var length) ? length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
