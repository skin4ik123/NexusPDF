using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Application;

/// <summary>
/// Применение вымарок при сохранении: страница с <see cref="RedactionDraft"/>
/// рендерится в высоком разрешении, области закрашиваются чёрным ПО РАСТРУ,
/// и в композиции страница заменяется на чисто растровую. Исходные текст,
/// векторы, изображения и аннотации такой страницы в результат НЕ попадают.
///
/// Конвейер: промежуточный PDF вымарываемых страниц БЕЗ добавочных поворотов
/// и с УЖЕ применёнными удалениями аннотаций → рендер → закраска (края с
/// запасом в пиксель) → растровый PDF. Замена получает исходные
/// ExtraQuarterTurns и carried-оверлеи с их PlacedRotation — компоновка сама
/// доворачивает и ремапит их штатным путём (углы картинок/OCR не теряются).
/// Слова OCR-слоя, пересекающие вымарку, ОТБРАСЫВАЮТСЯ — иначе вымаранный
/// текст вернулся бы невидимым слоем поверх чёрного.
/// </summary>
public static class RedactionBaker
{
    private const double RenderDpi = 250.0;
    private const int MaxRenderSide = 6000;

    public sealed record BakeResult(
        IReadOnlyList<ComposedPage> Composition,
        int RedactedPages,
        IAsyncDisposable? TempSource,
        string? TempPath) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (TempSource != null)
                await TempSource.DisposeAsync().ConfigureAwait(false);
            if (TempPath != null)
            {
                try { File.Delete(TempPath); } catch { /* лучшая попытка */ }
            }
        }
    }

    public static bool HasRedactions(IReadOnlyList<ComposedPage> composition) =>
        composition.Any(p => p.Overlays?.Any(o => o is RedactionDraft) == true);

    /// <summary>
    /// Возвращает композицию, где страницы с вымарками заменены растровыми.
    /// Возвращённый объект держит временный источник — освобождать (await
    /// using) ПОСЛЕ компоновки результата.
    /// </summary>
    public static async Task<BakeResult> BakeAsync(
        IPdfRenderEngine engine, OpenedDocument document,
        IReadOnlyList<ComposedPage> composition, CancellationToken ct)
    {
        var redactedIndices = new List<int>();
        for (var i = 0; i < composition.Count; i++)
        {
            if (composition[i].Overlays?.Any(o => o is RedactionDraft) == true)
                redactedIndices.Add(i);
        }
        if (redactedIndices.Count == 0)
            return new BakeResult(composition, 0, null, null);

        // Промежуточный PDF: вымарываемые страницы БЕЗ добавочного поворота,
        // БЕЗ оверлеев, но с применёнными удалениями аннотаций — иначе
        // помеченная к удалению аннотация «воскресла» бы в растре.
        var prePath = TempFile("pre");
        var specs = new List<ImagePageSpec>(redactedIndices.Count);
        try
        {
            var prePages = redactedIndices
                .Select(i => composition[i])
                .Select(p => new ComposedPage(p.Source, p.SourcePageIndex, 0, null, p.RemovedAnnotations))
                .ToList();
            await engine.ComposeAsync(prePages, prePath, ct).ConfigureAwait(false);

            var preHandle = await engine.OpenAsync(prePath, null, ct).ConfigureAwait(false);
            await using (preHandle.ConfigureAwait(false))
            {
                for (var k = 0; k < redactedIndices.Count; k++)
                {
                    ct.ThrowIfCancellationRequested();
                    var page = composition[redactedIndices[k]];
                    // Размер — из снимка промежуточного файла (не из живой модели).
                    var size = preHandle.Info.Pages[k];
                    var scale = Math.Min(RenderDpi / 72.0,
                        MaxRenderSide / Math.Max(size.WidthPoints, size.HeightPoints));
                    var width = Math.Max(1, (int)Math.Round(size.WidthPoints * scale));
                    var height = Math.Max(1, (int)Math.Round(size.HeightPoints * scale));

                    var image = await preHandle.RenderPageAsync(k, width, height, 0, ct).ConfigureAwait(false);
                    var bgra = (byte[])image.Bgra.Clone();
                    foreach (var rect in RedactionRects(page, size))
                    {
                        FillBlack(bgra, width, height, image.Stride, scale, rect);
                    }
                    specs.Add(new ImagePageSpec(bgra, width, height, size.WidthPoints, size.HeightPoints));
                }
            }
        }
        finally
        {
            try { File.Delete(prePath); } catch { /* лучшая попытка */ }
        }

        var rasterPath = TempFile("raster");
        IPdfDocumentHandle rasterHandle;
        try
        {
            await engine.CreateImageDocumentAsync(specs, rasterPath, ct).ConfigureAwait(false);
            rasterHandle = await engine.OpenAsync(rasterPath, null, ct).ConfigureAwait(false);
        }
        catch
        {
            try { File.Delete(rasterPath); } catch { /* лучшая попытка */ }
            throw;
        }

        try
        {
            var patched = new List<ComposedPage>(composition);
            for (var k = 0; k < redactedIndices.Count; k++)
            {
                var index = redactedIndices[k];
                var page = composition[index];
                var size = specs[k];
                // Оверлеи переносятся КАК ЕСТЬ (PlacedRotation сохранён), а
                // замена получает исходный поворот: компоновка сама ремапит
                // координаты и углы штатным OverlayDisplayMapper'ом.
                var carried = CarryOverlays(page, new PdfPageDescriptor(size.WidthPoints, size.HeightPoints));
                patched[index] = new ComposedPage(
                    rasterHandle, k, page.ExtraQuarterTurns,
                    carried.Count > 0 ? carried : null);
            }
            return new BakeResult(patched, redactedIndices.Count, rasterHandle, rasterPath);
        }
        catch
        {
            await rasterHandle.DisposeAsync().ConfigureAwait(false);
            try { File.Delete(rasterPath); } catch { /* лучшая попытка */ }
            throw;
        }
    }

    /// <summary>Вымарки страницы в НЕповёрнутой рамке (растр рендерится без добавочного поворота).</summary>
    private static List<RedactionDraft> RedactionRects(ComposedPage page, PdfPageDescriptor size)
    {
        var rects = new List<RedactionDraft>();
        foreach (var overlay in page.Overlays!)
        {
            if (overlay is not RedactionDraft draft)
                continue;
            var (mapped, _) = OverlayDisplayMapper.ToFrame(
                draft, 0, size.WidthPoints, size.HeightPoints);
            rects.Add((RedactionDraft)mapped);
        }
        return rects;
    }

    /// <summary>
    /// Оверлеи, переносимые на растровую замену: вымарки потребляются здесь;
    /// из OCR-слоя отбрасываются слова, пересекающие вымарку — иначе
    /// вымаранный текст вернулся бы НЕВИДИМЫМ, но копируемым слоем.
    /// </summary>
    private static List<PageOverlay> CarryOverlays(ComposedPage page, PdfPageDescriptor size)
    {
        var rects = RedactionRects(page, size);
        var carried = new List<PageOverlay>();
        foreach (var overlay in page.Overlays!)
        {
            switch (overlay)
            {
                case RedactionDraft:
                    break; // применена растром
                case OcrTextLayerOverlay ocr:
                {
                    // Слова и вымарки приводятся к одной (неповёрнутой) рамке.
                    var (mappedOverlay, _) = OverlayDisplayMapper.ToFrame(
                        ocr, 0, size.WidthPoints, size.HeightPoints);
                    var mappedWords = ((OcrTextLayerOverlay)mappedOverlay).Words;
                    var survivors = new List<OcrWordBox>();
                    for (var i = 0; i < ocr.Words.Count; i++)
                    {
                        if (!IntersectsAny(mappedWords[i], rects))
                            survivors.Add(ocr.Words[i]);
                    }
                    if (survivors.Count > 0)
                        carried.Add(ocr with { Words = survivors });
                    break;
                }
                default:
                    carried.Add(overlay);
                    break;
            }
        }
        return carried;
    }

    private static bool IntersectsAny(OcrWordBox word, List<RedactionDraft> rects)
    {
        // Запас в 1 pt: слово, касающееся вымарки краем, тоже отбрасывается.
        const double margin = 1.0;
        foreach (var r in rects)
        {
            if (word.XPt < r.XPt + r.WidthPt + margin &&
                word.XPt + word.WidthPt > r.XPt - margin &&
                word.YPt < r.YPt + r.HeightPt + margin &&
                word.YPt + word.HeightPt > r.YPt - margin)
                return true;
        }
        return false;
    }

    private static string TempFile(string tag) =>
        Path.Combine(Path.GetTempPath(),
            $"NexusPdf-redact-{tag}-{Guid.NewGuid():N}.pdf");

    /// <summary>
    /// Закраска по абсолютным краям с запасом в пиксель: для вымарки края
    /// обязаны ПЕРЕКРЫВАТЬСЯ — частично накрытый пиксель с антиалиасным
    /// остатком контента недопустим.
    /// </summary>
    private static void FillBlack(
        byte[] bgra, int width, int height, int stride, double scale, RedactionDraft rect)
    {
        var x0 = Math.Clamp((int)Math.Floor(rect.XPt * scale) - 1, 0, width);
        var y0 = Math.Clamp((int)Math.Floor(rect.YPt * scale) - 1, 0, height);
        var x1 = Math.Clamp((int)Math.Ceiling((rect.XPt + rect.WidthPt) * scale) + 1, 0, width);
        var y1 = Math.Clamp((int)Math.Ceiling((rect.YPt + rect.HeightPt) * scale) + 1, 0, height);
        for (var row = y0; row < y1; row++)
        {
            var offset = row * stride + x0 * 4;
            for (var col = x0; col < x1; col++)
            {
                bgra[offset] = 0;
                bgra[offset + 1] = 0;
                bgra[offset + 2] = 0;
                bgra[offset + 3] = 0xFF;
                offset += 4;
            }
        }
    }
}
