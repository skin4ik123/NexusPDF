using System.IO;
using MuPDF.NET;
using FileInfo = System.IO.FileInfo;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Pdf.MuPdf;

/// <summary>
/// Сжатие через MuPDF (Artifex). Взят вместо собственного пересжатия на PDFium
/// по итогам замера на реальном документе: при одинаковых настройках даёт
/// меньший файл и работает втрое быстрее, а главное — умеет то, чего публичный
/// API PDFium не умеет вовсе:
///
/// - переписывать 1-битные (bitonal) сканы, не разрушая их;
/// - выбирать между потерями и без потерь внутри самого движка;
/// - собирать мусор с ДЕДУПЛИКАЦИЕЙ объектов (garbage=4);
/// - урезать встроенные шрифты до используемых глифов.
///
/// Лицензия MuPDF — AGPL-3.0 (см. docs/LICENSES.md).
///
/// Нативная библиотека не потокобезопасна для одновременных документов, поэтому
/// все вызовы выстроены в очередь одним семафором.
/// </summary>
public sealed class MuPdfCompressionEngine : IPdfCompressionEngine
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly Lazy<string?> _probe = new(Probe);

    /// <summary>
    /// Результат считается годным, только если файл стал заметно меньше:
    /// выигрыш в полпроцента не стоит пересобранного документа.
    /// </summary>
    private const double WorthwhileRatio = 0.985;

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
        try
        {
            ct.ThrowIfCancellationRequested();
            using (var doc = new Document(sourcePath))
            {
                if (doc.NeedsPass && doc.Authenticate("") == 0)
                    throw new PdfEngineException(
                        "Документ защищён паролем: пересохранение сняло бы защиту.");

                if (!request.StructureOnly)
                {
                    // Порог чуть выше цели: картинку, которая уже почти в цель,
                    // трогать незачем — потеряем качество без выигрыша.
                    doc.RewriteImage(
                        dpiThreshold: (int)Math.Max(request.TargetDpi + 8, 55),
                        dpiTarget: (int)request.TargetDpi,
                        quality: request.Quality,
                        lossy: true, lossless: true, bitonal: true,
                        color: true, gray: true, setToGray: false);
                }

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

            var after = new FileInfo(work).Length;
            if (after >= before * WorthwhileRatio)
            {
                // Файл не стал меньше — отдаём исходник как есть. Пересобранный
                // документ без выигрыша в размере это только риск и потеря
                // качества.
                File.Copy(sourcePath, targetPath, overwrite: true);
                return new PdfCompressionResult(before, before, 0, 0, KeptOriginal: true);
            }

            File.Move(work, targetPath, overwrite: true);
            return new PdfCompressionResult(before, after, 0, 0, KeptOriginal: false);
        }
        finally
        {
            try { if (File.Exists(work)) File.Delete(work); } catch { /* лучшая попытка */ }
        }
    }
}
