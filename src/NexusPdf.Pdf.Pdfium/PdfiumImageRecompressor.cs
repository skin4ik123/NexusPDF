using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NexusPdf.Pdf.Abstractions;
using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

/// <summary>
/// Пересжатие изображений документа: картинки с эффективным DPI выше целевого
/// уменьшаются (box-усреднение) и кодируются в JPEG на место исходного потока.
///
/// Два прохода. Первый собирает пригодные размещения и группирует их по
/// СОДЕРЖИМОМУ исходного потока: один XObject, использованный на многих
/// страницах, — это одна группа, и целевой размер берётся по САМОМУ КРУПНОМУ
/// размещению (иначе замена под мелкое размещение замылила бы крупные).
/// Второй проход заменяет поток один раз на группу; повторные размещения
/// уже заменённого общего объекта распознаются по изменившимся метаданным.
///
/// Пропускаются (честно, без порчи вида):
/// - изображения с прозрачностью — детект по АЛЬФЕ ОТРЕНДЕРЕННОГО битмапа
///   (FPDFImageObjGetRenderedBitmap вкомпоновывает /SMask; формат базового
///   растра о маске ничего не говорит);
/// - 1-битные (ImageMask-трафареты и bilevel-сканы: JPEG их портит и раздувает);
/// - факсовые/экзотические кодеки (CCITT/JBIG2/JPX);
/// - повёрнутые/вырожденные размещения (DPI считается из матрицы объекта);
/// - гиганты свыше лимита мегапикселей (защита от OOM) и мелочь.
/// </summary>
internal static class PdfiumImageRecompressor
{
    private const int PageObjectImage = 3; // FPDF_PAGEOBJ_IMAGE
    private const int MinSidePixels = 48;      // мелочь пересжимать бессмысленно
    private const long MaxSourcePixels = 40_000_000; // защита от OOM
    private const int HashPrefixBytes = 64 * 1024;

    private sealed record Placement(
        int PageIndex, int ObjectIndex, int PixelWidth, int PixelHeight,
        int TargetWidth, int TargetHeight, string DataKey);

    private sealed class Group
    {
        public List<Placement> Placements { get; } = new();
        public int TargetWidth;
        public int TargetHeight;
        public byte[]? Jpeg;
    }

    public static ImageRecompressStats RecompressCore(
        string sourcePath, string? password, string targetPath, double targetDpi,
        Func<byte[], int, int, byte[]> encodeJpeg, CancellationToken ct)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var doc = fpdfview.FPDF_LoadMemDocument(pin.AddrOfPinnedObject(), bytes.Length, password);
            if (doc == null || doc.__Instance == IntPtr.Zero)
                throw new PdfEngineException("Не удалось открыть документ для пересжатия.");
            try
            {
                var skipped = 0;
                var groups = CollectGroups(doc, targetDpi, ref skipped, ct);
                var recompressed = ReplaceGroups(doc, groups, encodeJpeg, ct);
                PdfiumRenderEngine.SaveDocument(doc, targetPath);
                return new ImageRecompressStats(recompressed, skipped);
            }
            finally
            {
                fpdfview.FPDF_CloseDocument(doc);
            }
        }
        finally
        {
            pin.Free();
        }
    }

    // ----- Проход 1: сбор пригодных размещений и группировка по содержимому -----

    private static Dictionary<string, Group> CollectGroups(
        FpdfDocumentT doc, double targetDpi, ref int skipped, CancellationToken ct)
    {
        var groups = new Dictionary<string, Group>();
        var pageCount = fpdfview.FPDF_GetPageCount(doc);
        for (var p = 0; p < pageCount; p++)
        {
            ct.ThrowIfCancellationRequested();
            var page = fpdfview.FPDF_LoadPage(doc, p);
            if (page == null || page.__Instance == IntPtr.Zero)
                throw new PdfEngineException($"Не удалось открыть страницу {p + 1}.");
            try
            {
                var objectCount = fpdf_edit.FPDFPageCountObjects(page);
                for (var i = 0; i < objectCount; i++)
                {
                    var obj = fpdf_edit.FPDFPageGetObject(page, i);
                    if (obj == null || obj.__Instance == IntPtr.Zero ||
                        fpdf_edit.FPDFPageObjGetType(obj) != PageObjectImage)
                        continue;

                    var placement = Inspect(doc, page, p, i, obj, targetDpi);
                    if (placement == null)
                    {
                        skipped++;
                        continue;
                    }
                    if (!groups.TryGetValue(placement.DataKey, out var group))
                        groups[placement.DataKey] = group = new Group();
                    group.Placements.Add(placement);
                    // Цель группы — по самому КРУПНОМУ размещению: замена под
                    // мелкое замылила бы крупные копии того же изображения.
                    if (placement.TargetWidth > group.TargetWidth)
                        (group.TargetWidth, group.TargetHeight) =
                            (placement.TargetWidth, placement.TargetHeight);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
        return groups;
    }

    private static Placement? Inspect(
        FpdfDocumentT doc, FpdfPageT page, int pageIndex, int objectIndex,
        FpdfPageobjectT obj, double targetDpi)
    {
        var meta = new FPDF_IMAGEOBJ_METADATA();
        if (fpdf_edit.FPDFImageObjGetImageMetadata(obj, page, meta) == 0)
            return null;
        var width = (int)meta.Width;
        var height = (int)meta.Height;
        if (width < MinSidePixels || height < MinSidePixels)
            return null;
        if ((long)width * height > MaxSourcePixels)
            return null; // защита от OOM на гигантских сканах
        if (meta.BitsPerPixel <= 1)
            return null; // ImageMask-трафареты и bilevel: JPEG их портит

        // Эффективный DPI — из матрицы размещения, а не из метаданных pdfium:
        // те считаются по осевому bounding box и врут для повёрнутых картинок.
        var matrix = new FS_MATRIX_();
        if (fpdf_edit.FPDFPageObjGetMatrix(obj, matrix) == 0)
            return null;
        var placedWidthPt = Math.Sqrt((double)matrix.A * matrix.A + (double)matrix.B * matrix.B);
        var placedHeightPt = Math.Sqrt((double)matrix.C * matrix.C + (double)matrix.D * matrix.D);
        if (placedWidthPt < 1 || placedHeightPt < 1)
            return null;
        var dpiX = width / (placedWidthPt / 72.0);
        var dpiY = height / (placedHeightPt / 72.0);
        if (Math.Max(dpiX, dpiY) <= targetDpi * 1.2)
            return null; // уже достаточно компактно

        if (HasUnsupportedFilter(obj))
            return null;
        if (HasTransparency(doc, page, obj))
            return null; // JPEG не несёт альфы, /SMask при замене теряется

        // Масштаб по осям раздельный, без апскейла: анизотропное размещение
        // не должно проседать по «медленной» оси ниже цели.
        var targetWidth = Math.Max(1, (int)Math.Round(width * Math.Min(1.0, targetDpi / dpiX)));
        var targetHeight = Math.Max(1, (int)Math.Round(height * Math.Min(1.0, targetDpi / dpiY)));
        if (targetWidth >= width && targetHeight >= height)
            return null;

        var dataKey = ComputeDataKey(obj);
        if (dataKey == null)
            return null;
        return new Placement(pageIndex, objectIndex, width, height, targetWidth, targetHeight, dataKey);
    }

    /// <summary>Прозрачность по альфе ОТРЕНДЕРЕННОГО битмапа: /SMask вкомпонована именно в него.</summary>
    private static bool HasTransparency(FpdfDocumentT doc, FpdfPageT page, FpdfPageobjectT obj)
    {
        var rendered = fpdf_edit.FPDFImageObjGetRenderedBitmap(doc, page, obj);
        if (rendered == null || rendered.__Instance == IntPtr.Zero)
            return true; // не смогли проверить — не рискуем
        try
        {
            var format = fpdfview.FPDFBitmapGetFormat(rendered);
            if (format != 4)
                return false; // без альфа-канала прозрачности нет
            var width = fpdfview.FPDFBitmapGetWidth(rendered);
            var height = fpdfview.FPDFBitmapGetHeight(rendered);
            var stride = fpdfview.FPDFBitmapGetStride(rendered);
            var buffer = fpdfview.FPDFBitmapGetBuffer(rendered);
            if (buffer == IntPtr.Zero)
                return true;
            unsafe
            {
                var src = (byte*)buffer;
                for (var y = 0; y < height; y++)
                {
                    var row = src + (long)y * stride;
                    for (var x = 0; x < width; x++)
                    {
                        if (row[x * 4 + 3] != 0xFF)
                            return true;
                    }
                }
            }
            return false;
        }
        finally
        {
            fpdfview.FPDFBitmapDestroy(rendered);
        }
    }

    /// <summary>
    /// Идентичность изображения — по ДЕКОДИРОВАННОМУ битмапу (размеры, формат,
    /// SHA-256 префикса пикселей). FPDFImageObjGetImageDataRaw для изображений
    /// без файлового потока (созданных SetBitmap) роняет процесс — к нему не
    /// прикасаемся вовсе.
    /// </summary>
    private static string? ComputeDataKey(FpdfPageobjectT obj)
    {
        var bitmap = fpdf_edit.FPDFImageObjGetBitmap(obj);
        if (bitmap == null || bitmap.__Instance == IntPtr.Zero)
            return null;
        try
        {
            var width = fpdfview.FPDFBitmapGetWidth(bitmap);
            var height = fpdfview.FPDFBitmapGetHeight(bitmap);
            var stride = fpdfview.FPDFBitmapGetStride(bitmap);
            var format = fpdfview.FPDFBitmapGetFormat(bitmap);
            var buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
            if (buffer == IntPtr.Zero)
                return null;
            var total = (long)stride * height;
            var take = (int)Math.Min(total, HashPrefixBytes);
            var prefix = new byte[take];
            Marshal.Copy(buffer, prefix, 0, take);
            return $"bmp:{width}x{height}:{format}:{total}:{Convert.ToHexString(SHA256.HashData(prefix))}";
        }
        finally
        {
            fpdfview.FPDFBitmapDestroy(bitmap);
        }
    }

    private static bool HasUnsupportedFilter(FpdfPageobjectT obj)
    {
        var count = fpdf_edit.FPDFImageObjGetImageFilterCount(obj);
        for (var i = 0; i < count; i++)
        {
            var length = fpdf_edit.FPDFImageObjGetImageFilter(obj, i, IntPtr.Zero, 0);
            if (length == 0)
                continue;
            var raw = Marshal.AllocHGlobal((int)length);
            try
            {
                fpdf_edit.FPDFImageObjGetImageFilter(obj, i, raw, length);
                var name = Marshal.PtrToStringAnsi(raw, (int)length - 1) ?? "";
                if (name is "CCITTFaxDecode" or "JBIG2Decode" or "JPXDecode")
                    return true;
            }
            finally
            {
                Marshal.FreeHGlobal(raw);
            }
        }
        return false;
    }

    // ----- Проход 2: замена по одному разу на группу -----

    private static int ReplaceGroups(
        FpdfDocumentT doc, Dictionary<string, Group> groups,
        Func<byte[], int, int, byte[]> encodeJpeg, CancellationToken ct)
    {
        var replacedGroups = new HashSet<string>();
        var byPage = groups.Values
            .SelectMany(g => g.Placements.Select(pl => (Placement: pl, Group: g)))
            .GroupBy(x => x.Placement.PageIndex)
            .OrderBy(g => g.Key);

        foreach (var pageGroup in byPage)
        {
            ct.ThrowIfCancellationRequested();
            var page = fpdfview.FPDF_LoadPage(doc, pageGroup.Key);
            if (page == null || page.__Instance == IntPtr.Zero)
                throw new PdfEngineException($"Не удалось открыть страницу {pageGroup.Key + 1}.");
            try
            {
                var pageChanged = false;
                foreach (var (placement, group) in pageGroup)
                {
                    var obj = fpdf_edit.FPDFPageGetObject(page, placement.ObjectIndex);
                    if (obj == null || obj.__Instance == IntPtr.Zero)
                        continue;

                    // Общий XObject уже заменён с другого размещения: его
                    // метаданные теперь целевые — повторная замена была бы
                    // пересжатием пересжатого.
                    var meta = new FPDF_IMAGEOBJ_METADATA();
                    if (fpdf_edit.FPDFImageObjGetImageMetadata(obj, page, meta) == 0 ||
                        (int)meta.Width != placement.PixelWidth ||
                        (int)meta.Height != placement.PixelHeight)
                        continue;

                    if (group.Jpeg == null)
                    {
                        var bitmap = fpdf_edit.FPDFImageObjGetBitmap(obj);
                        if (bitmap == null || bitmap.__Instance == IntPtr.Zero)
                            continue;
                        byte[]? bgra;
                        try
                        {
                            bgra = ToBgra(bitmap);
                        }
                        finally
                        {
                            fpdfview.FPDFBitmapDestroy(bitmap);
                        }
                        if (bgra == null)
                            continue;
                        var resampled = BoxDownsample(
                            bgra, placement.PixelWidth, placement.PixelHeight,
                            group.TargetWidth, group.TargetHeight);
                        group.Jpeg = encodeJpeg(resampled, group.TargetWidth, group.TargetHeight);
                    }

                    if (ReplaceWithJpeg(page, obj, group.Jpeg))
                    {
                        pageChanged = true;
                        replacedGroups.Add(placement.DataKey);
                    }
                }

                // Без пересборки содержимого SaveAsCopy пишет страницу с
                // ПРЕЖНИМИ потоками изображений.
                if (pageChanged && fpdf_edit.FPDFPageGenerateContent(page) == 0)
                    throw new PdfEngineException(
                        $"Не удалось зафиксировать пересжатую страницу {pageGroup.Key + 1}.");
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
        return replacedGroups.Count;
    }

    /// <summary>BGRA-копия битмапа; null — если формат неизвестен.</summary>
    private static unsafe byte[]? ToBgra(FpdfBitmapT bitmap)
    {
        var width = fpdfview.FPDFBitmapGetWidth(bitmap);
        var height = fpdfview.FPDFBitmapGetHeight(bitmap);
        var stride = fpdfview.FPDFBitmapGetStride(bitmap);
        var format = fpdfview.FPDFBitmapGetFormat(bitmap);
        var buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
        if (width < 1 || height < 1 || buffer == IntPtr.Zero)
            return null;

        var result = new byte[(long)width * height * 4];
        var src = (byte*)buffer;
        switch (format)
        {
            case 1: // Gray
                for (var y = 0; y < height; y++)
                {
                    var row = src + (long)y * stride;
                    for (var x = 0; x < width; x++)
                    {
                        var g = row[x];
                        var o = ((long)y * width + x) * 4;
                        result[o] = g;
                        result[o + 1] = g;
                        result[o + 2] = g;
                        result[o + 3] = 0xFF;
                    }
                }
                return result;
            case 2: // BGR
                for (var y = 0; y < height; y++)
                {
                    var row = src + (long)y * stride;
                    for (var x = 0; x < width; x++)
                    {
                        var o = ((long)y * width + x) * 4;
                        result[o] = row[x * 3];
                        result[o + 1] = row[x * 3 + 1];
                        result[o + 2] = row[x * 3 + 2];
                        result[o + 3] = 0xFF;
                    }
                }
                return result;
            case 3: // BGRx
            case 4: // BGRA (прозрачность уже отсеяна детектором по рендеру)
                for (var y = 0; y < height; y++)
                {
                    var row = src + (long)y * stride;
                    for (var x = 0; x < width; x++)
                    {
                        var o = ((long)y * width + x) * 4;
                        result[o] = row[x * 4];
                        result[o + 1] = row[x * 4 + 1];
                        result[o + 2] = row[x * 4 + 2];
                        result[o + 3] = 0xFF;
                    }
                }
                return result;
            default:
                return null;
        }
    }

    /// <summary>Уменьшение box-усреднением: каждый целевой пиксель — среднее прямоугольника источника.</summary>
    private static byte[] BoxDownsample(byte[] bgra, int width, int height, int newWidth, int newHeight)
    {
        newWidth = Math.Min(newWidth, width);
        newHeight = Math.Min(newHeight, height);
        var result = new byte[(long)newWidth * newHeight * 4];
        for (var y = 0; y < newHeight; y++)
        {
            var srcY0 = (int)((long)y * height / newHeight);
            var srcY1 = Math.Max(srcY0 + 1, (int)((long)(y + 1) * height / newHeight));
            for (var x = 0; x < newWidth; x++)
            {
                var srcX0 = (int)((long)x * width / newWidth);
                var srcX1 = Math.Max(srcX0 + 1, (int)((long)(x + 1) * width / newWidth));
                long b = 0, g = 0, r = 0;
                var n = 0;
                for (var sy = srcY0; sy < srcY1; sy++)
                {
                    for (var sx = srcX0; sx < srcX1; sx++)
                    {
                        var o = ((long)sy * width + sx) * 4;
                        b += bgra[o];
                        g += bgra[o + 1];
                        r += bgra[o + 2];
                        n++;
                    }
                }
                var t = ((long)y * newWidth + x) * 4;
                result[t] = (byte)(b / n);
                result[t + 1] = (byte)(g / n);
                result[t + 2] = (byte)(r / n);
                result[t + 3] = 0xFF;
            }
        }
        return result;
    }

    private static unsafe bool ReplaceWithJpeg(FpdfPageT page, FpdfPageobjectT obj, byte[] jpeg)
    {
        int GetBlock(IntPtr _, ulong position, byte* pBuf, ulong size)
        {
            if (position + size > (ulong)jpeg.Length || size > int.MaxValue)
                return 0;
            Marshal.Copy(jpeg, (int)position, (IntPtr)pBuf, (int)size);
            return 1;
        }

        var getBlock = new PDFiumCore.Delegates.Func_int___IntPtr_ulong_bytePtr_ulong(GetBlock);
        var access = new FPDF_FILEACCESS
        {
            MFileLen = (uint)jpeg.Length,
            MGetBlock = getBlock,
            MParam = IntPtr.Zero,
        };
        var ok = fpdf_edit.FPDFImageObjLoadJpegFileInline(page, 1, obj, access) != 0;
        GC.KeepAlive(getBlock);
        return ok;
    }
}
