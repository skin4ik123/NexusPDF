using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Application;

/// <summary>Итог сравнения одной пары страниц (без растров).</summary>
public sealed record PageCompareInfo(
    int PageIndex,
    bool OnlyInFirst,
    bool OnlyInSecond,
    bool SizeMismatch,
    double DiffPercent)
{
    public bool IsDifferent => OnlyInFirst || OnlyInSecond || SizeMismatch || DiffPercent > 0.01;
}

/// <summary>Пара растров страницы на общем канвасе + маска отличий (byte на пиксель, 1 — отличие).</summary>
public sealed record PageCompareImages(
    RenderedPageImage? First,
    RenderedPageImage? Second,
    byte[]? DiffMask,
    int Width,
    int Height);

public sealed record CompareSummary(
    IReadOnlyList<PageCompareInfo> Pages,
    int DifferentPages,
    string FirstPath,
    string SecondPath);

/// <summary>
/// Визуальное сравнение двух PDF: страницы попарно рендерятся при одном DPI
/// на общий белый канвас и сравниваются попиксельно с допуском на сглаживание.
/// Это сравнение ИЗОБРАЖЕНИЯ страниц, а не структуры: перестановка невидимых
/// объектов не считается отличием, любое видимое изменение — считается.
/// Сессия держит оба файла открытыми: сводка считается сразу, растры пары
/// отдаются по требованию (иначе стостраничные документы съели бы гигабайты).
/// </summary>
public sealed class CompareSession : IAsyncDisposable
{
    private const double RenderDpi = 96.0;
    private const int MaxCompareSide = 2200;

    // Сумма |ΔR|+|ΔG|+|ΔB| выше порога — отличие; ниже — шум сглаживания.
    private const int ChannelTolerance = 40;

    private readonly IPdfDocumentHandle _first;
    private readonly IPdfDocumentHandle _second;

    private CompareSession(IPdfDocumentHandle first, IPdfDocumentHandle second, string firstPath, string secondPath)
    {
        _first = first;
        _second = second;
        FirstPath = firstPath;
        SecondPath = secondPath;
    }

    public string FirstPath { get; }
    public string SecondPath { get; }

    public static async Task<CompareSession> OpenAsync(
        IPdfRenderEngine engine,
        string firstPath, string? firstPassword,
        string secondPath, string? secondPassword,
        CancellationToken ct)
    {
        var first = await engine.OpenAsync(firstPath, firstPassword, ct).ConfigureAwait(false);
        try
        {
            var second = await engine.OpenAsync(secondPath, secondPassword, ct).ConfigureAwait(false);
            return new CompareSession(first, second, firstPath, secondPath);
        }
        catch
        {
            await first.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Полный проход по парам страниц: только статистика отличий.</summary>
    public async Task<CompareSummary> AnalyzeAsync(IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var total = Math.Max(_first.Info.PageCount, _second.Info.PageCount);
        var pages = new List<PageCompareInfo>(total);
        var different = 0;
        for (var i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (info, _) = await ComparePageAsync(i, keepImages: false, ct).ConfigureAwait(false);
            if (info.IsDifferent)
                different++;
            pages.Add(info);
            progress?.Report((i + 1, total));
        }
        return new CompareSummary(pages, different, FirstPath, SecondPath);
    }

    /// <summary>Растры и маска отличий одной пары страниц (для просмотра).</summary>
    public async Task<PageCompareImages> GetPageImagesAsync(int pageIndex, CancellationToken ct)
    {
        var (_, images) = await ComparePageAsync(pageIndex, keepImages: true, ct).ConfigureAwait(false);
        return images!;
    }

    private async Task<(PageCompareInfo Info, PageCompareImages? Images)> ComparePageAsync(
        int pageIndex, bool keepImages, CancellationToken ct)
    {
        var inFirst = pageIndex < _first.Info.PageCount;
        var inSecond = pageIndex < _second.Info.PageCount;
        if (!inFirst || !inSecond)
        {
            var only = inFirst ? _first : _second;
            var size = only.Info.Pages[pageIndex];
            var (w, h) = PixelSize(size.WidthPoints, size.HeightPoints);
            var info = new PageCompareInfo(pageIndex, inFirst, inSecond, false, 100);
            if (!keepImages)
                return (info, null);
            var image = await only.RenderPageAsync(pageIndex, w, h, 0, ct).ConfigureAwait(false);
            return (info, new PageCompareImages(
                inFirst ? image : null, inSecond ? image : null, null, w, h));
        }

        var sizeA = _first.Info.Pages[pageIndex];
        var sizeB = _second.Info.Pages[pageIndex];
        var sizeMismatch =
            Math.Abs(sizeA.WidthPoints - sizeB.WidthPoints) > 1 ||
            Math.Abs(sizeA.HeightPoints - sizeB.HeightPoints) > 1;

        // Общий канвас по большей странице; каждая страница рендерится в СВОЁМ
        // разрешении того же DPI (без искажения аспекта) от левого верхнего угла.
        var (wA, hA) = PixelSize(sizeA.WidthPoints, sizeA.HeightPoints);
        var (wB, hB) = PixelSize(sizeB.WidthPoints, sizeB.HeightPoints);
        var width = Math.Max(wA, wB);
        var height = Math.Max(hA, hB);

        var imageA = await _first.RenderPageAsync(pageIndex, wA, hA, 0, ct).ConfigureAwait(false);
        var imageB = await _second.RenderPageAsync(pageIndex, wB, hB, 0, ct).ConfigureAwait(false);
        var canvasA = ToCanvas(imageA, width, height);
        var canvasB = ToCanvas(imageB, width, height);

        var mask = keepImages ? new byte[width * height] : null;
        long diffCount = 0;
        var totalPixels = width * height;
        for (var p = 0; p < totalPixels; p++)
        {
            var o = p * 4;
            var delta = Math.Abs(canvasA[o] - canvasB[o]) +
                        Math.Abs(canvasA[o + 1] - canvasB[o + 1]) +
                        Math.Abs(canvasA[o + 2] - canvasB[o + 2]);
            if (delta > ChannelTolerance)
            {
                diffCount++;
                if (mask != null)
                    mask[p] = 1;
            }
        }

        var infoResult = new PageCompareInfo(
            pageIndex, false, false, sizeMismatch, diffCount * 100.0 / totalPixels);
        if (!keepImages)
            return (infoResult, null);
        return (infoResult, new PageCompareImages(
            new RenderedPageImage(width, height, width * 4, canvasA),
            new RenderedPageImage(width, height, width * 4, canvasB),
            diffCount > 0 ? mask : null, width, height));
    }

    private static (int Width, int Height) PixelSize(double widthPoints, double heightPoints)
    {
        var scale = Math.Min(RenderDpi / 72.0, MaxCompareSide / Math.Max(widthPoints, heightPoints));
        return (Math.Max(1, (int)Math.Round(widthPoints * scale)),
                Math.Max(1, (int)Math.Round(heightPoints * scale)));
    }

    /// <summary>Растр страницы на белом канвасе большего размера (от левого верхнего угла).</summary>
    private static byte[] ToCanvas(RenderedPageImage image, int width, int height)
    {
        if (image.PixelWidth == width && image.PixelHeight == height)
            return image.Bgra;
        var canvas = new byte[width * height * 4];
        Array.Fill(canvas, (byte)0xFF);
        for (var y = 0; y < image.PixelHeight; y++)
            Array.Copy(image.Bgra, y * image.Stride, canvas, y * width * 4, image.PixelWidth * 4);
        return canvas;
    }

    public async ValueTask DisposeAsync()
    {
        await _first.DisposeAsync().ConfigureAwait(false);
        await _second.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Детект ЗАЯВЛЕННОГО соответствия PDF/A по XMP-метаданным (pdfaid:part /
/// pdfaid:conformance). Это именно заявление автора файла: полная валидация
/// PDF/A требует стороннего валидатора и здесь не выполняется.
/// </summary>
public static class PdfAClaimDetector
{
    public static string? DetectClaim(string filePath)
    {
        // XMP лежит близко к началу или к концу файла — читаем до 4 МиБ с
        // каждой стороны, не загружая гигантский файл целиком.
        const int window = 4 * 1024 * 1024;
        using var stream = File.OpenRead(filePath);
        var headLength = (int)Math.Min(stream.Length, window);
        var head = new byte[headLength];
        stream.ReadExactly(head, 0, headLength);
        var claim = Scan(head);
        if (claim != null || stream.Length <= window)
            return claim;

        stream.Seek(-window, SeekOrigin.End);
        var tail = new byte[window];
        stream.ReadExactly(tail, 0, window);
        return Scan(tail);
    }

    private static string? Scan(byte[] bytes)
    {
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        var part = System.Text.RegularExpressions.Regex.Match(text,
            @"pdfaid:part(?:>\s*|="")(\d)");
        if (!part.Success)
            return null;
        var conformance = System.Text.RegularExpressions.Regex.Match(text,
            @"pdfaid:conformance(?:>\s*|="")([ABUab])");
        return conformance.Success
            ? $"PDF/A-{part.Groups[1].Value}{conformance.Groups[1].Value.ToUpperInvariant()}"
            : $"PDF/A-{part.Groups[1].Value}";
    }
}
