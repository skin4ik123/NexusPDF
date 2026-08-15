using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Application;

/// <summary>
/// Применение вымарок при сохранении: страница с <see cref="RedactionDraft"/>
/// рендерится в высоком разрешении, области закрашиваются чёрным ПО РАСТРУ,
/// и в композиции страница заменяется на чисто растровую (через временный
/// PDF-источник). Исходные текст, векторы, изображения и аннотации такой
/// страницы в результат НЕ попадают вовсе — вымарку нельзя «отклеить».
/// Цена гарантии: страница теряет текстовый слой (поиск вернёт OCR).
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
    /// Возвращённый объект держит временный источник — освобождать ПОСЛЕ
    /// компоновки результата.
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

        // Растр каждой вымарываемой страницы с закрашенными областями.
        var specs = new List<ImagePageSpec>(redactedIndices.Count);
        foreach (var index in redactedIndices)
        {
            ct.ThrowIfCancellationRequested();
            var page = composition[index];
            var size = document.GetLogicalPageSize(FindLogicalIndex(document, page, index));
            var scale = Math.Min(RenderDpi / 72.0,
                MaxRenderSide / Math.Max(size.WidthPoints, size.HeightPoints));
            var width = Math.Max(1, (int)Math.Round(size.WidthPoints * scale));
            var height = Math.Max(1, (int)Math.Round(size.HeightPoints * scale));

            // Рендер ПОЛНОЙ страницы (с аннотациями): их вид сохраняется как
            // картинка, но как объекты они уничтожаются вместе с остальным.
            var image = await page.Source.RenderPageAsync(
                page.SourcePageIndex, width, height, page.ExtraQuarterTurns, ct).ConfigureAwait(false);

            var bgra = (byte[])image.Bgra.Clone();
            foreach (var overlay in page.Overlays!)
            {
                // Прочие оверлеи страницы (текст, фигуры…) запечь В РАСТР
                // нельзя — движок оверлеев работает по PDF-объектам; они
                // применяются к странице честным пропуском ниже (см. Replace).
                if (overlay is not RedactionDraft draft)
                    continue;
                var (mapped, _) = OverlayDisplayMapper.ToFrame(
                    draft, page.ExtraQuarterTurns, size.WidthPoints, size.HeightPoints);
                var rect = (RedactionDraft)mapped;
                FillBlack(bgra, width, height, image.Stride,
                    (int)Math.Floor(rect.XPt * scale), (int)Math.Floor(rect.YPt * scale),
                    (int)Math.Ceiling(rect.WidthPt * scale), (int)Math.Ceiling(rect.HeightPt * scale));
            }
            specs.Add(new ImagePageSpec(bgra, width, height, size.WidthPoints, size.HeightPoints));
        }

        var tempPath = Path.Combine(Path.GetTempPath(),
            "NexusPdf-redact-" + Guid.NewGuid().ToString("N") + ".pdf");
        await engine.CreateImageDocumentAsync(specs, tempPath, ct).ConfigureAwait(false);
        var tempHandle = await engine.OpenAsync(tempPath, null, ct).ConfigureAwait(false);

        try
        {
            var patched = new List<ComposedPage>(composition);
            for (var k = 0; k < redactedIndices.Count; k++)
            {
                var index = redactedIndices[k];
                // Прочие оверлеи вымарываемой страницы переносятся на растровую
                // замену (их координаты — в той же отображаемой рамке; поворот
                // уже запечён в растр, поэтому PlacedRotation обнуляется
                // относительно новой страницы без /Rotate).
                var carried = composition[index].Overlays!
                    .Where(o => o is not RedactionDraft)
                    .Select(o => OverlayDisplayMapper.ToFrame(
                        o, composition[index].ExtraQuarterTurns, specs[k].WidthPoints, specs[k].HeightPoints).Overlay
                        with { PlacedRotation = 0 })
                    .ToList();
                patched[index] = new ComposedPage(
                    tempHandle, k, 0, carried.Count > 0 ? carried : null);
            }
            return new BakeResult(patched, redactedIndices.Count, tempHandle, tempPath);
        }
        catch
        {
            await tempHandle.DisposeAsync().ConfigureAwait(false);
            try { File.Delete(tempPath); } catch { /* лучшая попытка */ }
            throw;
        }
    }

    /// <summary>Логический индекс страницы композиции (композиция строится 1:1 из модели).</summary>
    private static int FindLogicalIndex(OpenedDocument document, ComposedPage page, int compositionIndex)
    {
        var pages = document.Session.Model.Pages;
        if (compositionIndex < pages.Count)
            return compositionIndex; // BuildComposition сохраняет порядок модели
        return Math.Min(compositionIndex, pages.Count - 1);
    }

    private static void FillBlack(byte[] bgra, int width, int height, int stride, int x, int y, int w, int h)
    {
        var x0 = Math.Clamp(x, 0, width);
        var y0 = Math.Clamp(y, 0, height);
        var x1 = Math.Clamp(x + w, 0, width);
        var y1 = Math.Clamp(y + h, 0, height);
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
