using NexusPdf.Pdf.Abstractions;
using NexusPdf.Printing;

namespace NexusPdf.Application;

/// <summary>Один документ в очереди пакетной печати.</summary>
public sealed record BatchPrintItem(string Path, string? Password = null);

/// <summary>Что случилось с одним документом.</summary>
public sealed record BatchPrintOutcome(
    string Path,
    bool Succeeded,
    int Sheets,
    string? Error);

/// <summary>Итог всего пакета.</summary>
public sealed record BatchPrintResult(IReadOnlyList<BatchPrintOutcome> Outcomes)
{
    public int Succeeded => Outcomes.Count(o => o.Succeeded);
    public int Failed => Outcomes.Count(o => !o.Succeeded);
    public int TotalSheets => Outcomes.Sum(o => o.Sheets);
}

/// <summary>Ход пакетной печати.</summary>
public sealed record BatchPrintProgress(int Done, int Total, string CurrentFile);

/// <summary>
/// Пакетная печать: один профиль применяется к набору файлов.
///
/// Ошибка одного документа НЕ прерывает остальные — иначе один повреждённый
/// файл в середине списка отменял бы работу по всем следующим, а узнал бы об
/// этом пользователь, вернувшись к пустому лотку. Итог по каждому файлу
/// возвращается отдельной строкой.
/// </summary>
public sealed class BatchPrintService
{
    private readonly IPdfRenderEngine _engine;

    /// <summary>Как отправить одно готовое задание: в очередь или в файл.</summary>
    public delegate Task<int> SubmitDelegate(
        OpenedDocument document, PrintJobPlan plan, CancellationToken ct);

    public BatchPrintService(IPdfRenderEngine engine) => _engine = engine;

    public async Task<BatchPrintResult> RunAsync(
        IReadOnlyList<BatchPrintItem> items,
        PrintProfile profile,
        PaperSizeOption paper,
        PrinterCapabilities capabilities,
        SubmitDelegate submit,
        IProgress<BatchPrintProgress>? progress,
        CancellationToken ct)
    {
        var outcomes = new List<BatchPrintOutcome>();
        var engine = new PrintLayoutEngine();
        var settings = profile.ToSettings();

        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];
            progress?.Report(new BatchPrintProgress(i, items.Count, Path.GetFileName(item.Path)));

            try
            {
                var document = await OpenedDocument
                    .OpenAsync(_engine, item.Path, item.Password, ct).ConfigureAwait(false);
                await using (document)
                {
                    // Запрет печати соблюдается и в пакете: пропускаем документ
                    // с честной причиной, а не печатаем в обход ограничения.
                    var permissions = PrintPermissions.FromFlags(
                        await document.PrimaryHandle.GetPermissionsAsync(ct).ConfigureAwait(false));
                    if (!permissions.AllowPrint)
                    {
                        outcomes.Add(new BatchPrintOutcome(item.Path, false, 0,
                            "Документ запрещает печать."));
                        continue;
                    }

                    var pages = Enumerable.Range(0, document.Session.Model.Pages.Count)
                        .Select(p =>
                        {
                            var size = document.GetLogicalPageSize(p);
                            return new SourcePage("doc", p, new SizePt(size.WidthPoints, size.HeightPoints));
                        })
                        .ToList();

                    var sheets = engine.BuildSheets(pages, settings, paper, capabilities);
                    sheets = engine.ApplyDuplexPairing(sheets, settings.Duplex, settings.Imposition);
                    sheets = engine.ApplyMarksAndOverlays(sheets, settings, new OverlayContext(
                        Path.GetFileName(item.Path), 1, sheets.Count, 1, 1,
                        DateTime.Now.ToString("dd.MM.yyyy"), capabilities.PrinterName, Environment.UserName));

                    var plan = new PrintJobPlan
                    {
                        JobName = Path.GetFileNameWithoutExtension(item.Path),
                        PrinterName = capabilities.PrinterName,
                        Capabilities = capabilities,
                        Sheets = sheets,
                        Duplex = settings.Duplex,
                    };

                    var sent = await submit(document, plan, ct).ConfigureAwait(false);
                    outcomes.Add(new BatchPrintOutcome(item.Path, true, sent, null));
                }
            }
            catch (OperationCanceledException)
            {
                // Отмена прерывает пакет целиком: остальные файлы не трогаем.
                throw;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Пакетная печать: файл {File} пропущен", item.Path);
                outcomes.Add(new BatchPrintOutcome(item.Path, false, 0, ex.Message));
            }
        }

        progress?.Report(new BatchPrintProgress(items.Count, items.Count, ""));
        return new BatchPrintResult(outcomes);
    }
}
