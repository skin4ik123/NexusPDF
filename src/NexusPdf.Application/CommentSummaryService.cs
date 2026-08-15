using NexusPdf.Domain;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Printing;

namespace NexusPdf.Application;

/// <summary>Итог построения сводки.</summary>
public sealed record CommentSummaryResult(int CommentCount, int PageCount, string Path);

/// <summary>
/// Сводка комментариев отдельным PDF.
///
/// Текст в сводке НАСТОЯЩИЙ, векторный: страница создаётся белыми листами и в
/// них запекаются текстовые оверлеи тем же кодом, что и надписи в документе.
/// Рисовать сводку растровым шрифтом печати было бы дёшево, но страницу с
/// комментариями стало бы невозможно читать и искать по ней.
///
/// Исходный документ не изменяется: создаётся отдельный файл.
/// </summary>
public sealed class CommentSummaryService
{
    private readonly IPdfRenderEngine _engine;

    public CommentSummaryService(IPdfRenderEngine engine) => _engine = engine;

    private static readonly SizePt A4 = new(595.28, 841.89);

    public async Task<CommentSummaryResult> BuildAsync(
        OpenedDocument document,
        string targetPath,
        CommentSummarySettings settings,
        string documentTitle,
        CancellationToken ct)
    {
        var comments = await CollectAsync(document, settings, ct).ConfigureAwait(false);
        var pages = CommentSummaryLayout.Build(comments, A4, settings, documentTitle);

        // Белые листы нужного размера: на них запекается текст.
        var white = MakeWhitePage();
        var blanks = Enumerable.Repeat(white, pages.Count).ToList();

        var tempPath = Path.Combine(Path.GetTempPath(),
            "nexuspdf-summary-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            await _engine.CreateImageDocumentAsync(blanks, tempPath, ct).ConfigureAwait(false);

            var summary = await OpenedDocument.OpenAsync(_engine, tempPath, null, ct).ConfigureAwait(false);
            await using (summary)
            {
                for (var i = 0; i < pages.Count; i++)
                {
                    foreach (var line in pages[i].Lines)
                    {
                        summary.Session.Apply(new AddOverlayOperation(i, new TextOverlay(
                            line.Text, line.XPt, line.YPt, line.FontSizePt,
                            ColorArgb: 0xFF1A1A1A, RotationDegrees: 0)));
                    }
                }

                await new SaveService(_engine).SaveCopyAsync(summary, targetPath, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (Exception ex) { Serilog.Log.Debug(ex, "Временный файл сводки не удалён"); }
        }

        return new CommentSummaryResult(comments.Count, pages.Count, targetPath);
    }

    /// <summary>Собирает аннотации со страниц документа с учётом фильтров.</summary>
    private static async Task<IReadOnlyList<SummaryComment>> CollectAsync(
        OpenedDocument document, CommentSummarySettings settings, CancellationToken ct)
    {
        var result = new List<SummaryComment>();
        var pages = document.Session.Model.Pages;
        var number = 1;

        for (var i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (settings.PageFilter != null && !settings.PageFilter.Contains(i)) continue;

            var page = pages[i];
            var annotations = await document.Handles[page.SourceId]
                .GetAnnotationsAsync(page.SourcePageIndex, ct).ConfigureAwait(false);

            foreach (var annotation in annotations)
            {
                // Виджеты форм — не комментарии: в сводке они были бы шумом.
                if (annotation.Subtype == 20) continue;

                // Аннотация без текста и без автора ничего не сообщает.
                if (string.IsNullOrWhiteSpace(annotation.Contents) &&
                    string.IsNullOrWhiteSpace(annotation.Author)) continue;

                if (settings.AuthorFilter.Length > 0 &&
                    !annotation.Author.Contains(settings.AuthorFilter, StringComparison.CurrentCultureIgnoreCase))
                    continue;

                result.Add(new SummaryComment(
                    number++, i + 1,
                    CommentSummaryLayout.DescribeSubtype(annotation.Subtype),
                    annotation.Author, "", annotation.Contents));
            }
        }
        return result;
    }

    /// <summary>Белый лист A4: подложка, поверх которой ложится текст сводки.</summary>
    private static ImagePageSpec MakeWhitePage()
    {
        // Одного пикселя достаточно: он растягивается на всю страницу, а текст
        // всё равно векторный. Растр во весь лист был бы лишними мегабайтами.
        var pixel = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        return new ImagePageSpec(pixel, 1, 1, A4.WidthPt, A4.HeightPt);
    }
}
