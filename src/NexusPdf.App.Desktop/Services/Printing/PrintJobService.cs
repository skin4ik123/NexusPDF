using System.Printing;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using NexusPdf.Application;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Printing;
using NexusPdf.Printing.Windows;

namespace NexusPdf.App.Desktop.Services.Printing;

/// <summary>Состояние отправленного задания.</summary>
public sealed record PrintJobStatus(
    int JobId,
    string PrinterName,
    int SheetsSent,
    string StageText,
    bool IsFinished);

/// <summary>Ход подготовки задания: сколько листов уже отрисовано.</summary>
public sealed record PrintProgress(int SheetsDone, int SheetsTotal);

/// <summary>
/// Отправка задания в очередь Windows. Листы готовятся ПОСТРАНИЧНО прямо во
/// время передачи: WPF запрашивает страницу у пагинатора по мере записи, и
/// тысяча листов не оказывается в памяти целиком.
/// </summary>
public sealed class PrintJobService
{
    /// <summary>Разрешение растра листа. 300 dpi — обычная печать документов.</summary>
    private const double OutputDpi = 300.0;

    public async Task<PrintJobStatus> SubmitAsync(
        OpenedDocument document,
        PrintJobPlan plan,
        IProgress<PrintProgress>? progress,
        CancellationToken ct)
    {
        if (plan.Sheets.Count == 0)
            throw new InvalidOperationException("В плане печати нет ни одного листа.");

        // Разрешения перечитываются перед самой отправкой, а не берутся из
        // плана: между открытием окна и нажатием «Печать» документ мог
        // смениться на другой в той же вкладке.
        var permissions = PrintPermissions.FromFlags(
            await document.PrimaryHandle.GetPermissionsAsync(ct).ConfigureAwait(false));
        if (!permissions.AllowPrint)
            throw new InvalidOperationException("Документ запрещает печать.");
        var dpi = permissions.LimitDpi(OutputDpi);

        // Проверка перед отправкой: принтер мог исчезнуть, пока окно было открыто.
        using var probe = new WindowsPrinterService();
        var live = probe.Read(plan.PrinterName)
            ?? throw new InvalidOperationException(
                $"Принтер «{plan.PrinterName}» сейчас недоступен.");

        var sent = 0;
        await RunOnStaThreadAsync(() =>
        {
            using var server = new PrintServer();
            using var queue = new PrintQueue(server, plan.PrinterName);

            var ticket = BuildTicket(queue, plan);
            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            var paginator = new PlanPaginator(document, plan, dpi, progress, ct,
                onSheet: () => Interlocked.Increment(ref sent));

            writer.Write(paginator, ticket);
        }, ct).ConfigureAwait(false);

        return new PrintJobStatus(0, plan.PrinterName, sent, "Задание передано принтеру", true);
    }

    /// <summary>
    /// PrintTicket из плана, СОГЛАСОВАННЫЙ с возможностями принтера.
    /// MergeAndValidatePrintTicket обязателен: драйвер имеет право поправить
    /// несовместимое сочетание, и работать надо с тем, что он вернул, а не с
    /// тем, что мы попросили.
    /// </summary>
    private static PrintTicket BuildTicket(PrintQueue queue, PrintJobPlan plan)
    {
        var ticket = queue.UserPrintTicket?.Clone() ?? new PrintTicket();

        ticket.CopyCount = Math.Max(1, plan.Copies);
        ticket.Collation = plan.Collation == CollationMode.Collated ? Collation.Collated : Collation.Uncollated;

        ticket.Duplexing = plan.Duplex switch
        {
            DuplexMode.LongEdge => Duplexing.TwoSidedLongEdge,
            DuplexMode.ShortEdge => Duplexing.TwoSidedShortEdge,
            // Ручной дуплекс печатается как обычная односторонняя: переворот
            // делает человек, а не драйвер.
            _ => Duplexing.OneSided,
        };

        var first = plan.Sheets[0];
        ticket.PageOrientation = first.PaperSizePt.IsLandscape
            ? PageOrientation.Landscape
            : PageOrientation.Portrait;

        ticket.OutputColor = first.Color switch
        {
            ColorMode.Grayscale => OutputColor.Grayscale,
            ColorMode.Monochrome => OutputColor.Monochrome,
            ColorMode.Color => OutputColor.Color,
            _ => ticket.OutputColor,
        };

        var paperName = plan.Capabilities.PaperSizes
            .FirstOrDefault(p => Math.Abs(p.SizePt.WidthPt - first.PaperSizePt.WidthPt) < 2 &&
                                 Math.Abs(p.SizePt.HeightPt - first.PaperSizePt.HeightPt) < 2)
            ?? plan.Capabilities.PaperSizes.FirstOrDefault(p =>
                Math.Abs(p.SizePt.WidthPt - first.PaperSizePt.HeightPt) < 2 &&
                Math.Abs(p.SizePt.HeightPt - first.PaperSizePt.WidthPt) < 2);
        if (paperName?.DriverValue != null &&
            Enum.TryParse<PageMediaSizeName>(paperName.DriverValue, out var media))
            ticket.PageMediaSize = new PageMediaSize(media);

        var validated = queue.MergeAndValidatePrintTicket(queue.UserPrintTicket, ticket);
        if (validated.ValidatedPrintTicket != null)
            return validated.ValidatedPrintTicket;
        return ticket;
    }

    private static Task RunOnStaThreadAsync(Action action, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(ct);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "NexusPdf.Print",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    /// <summary>
    /// Пагинатор поверх плана. Каждый лист рисуется в момент запроса — это и
    /// даёт потоковую отправку вместо предварительного рендера всего задания.
    /// </summary>
    private sealed class PlanPaginator : DocumentPaginator
    {
        private readonly OpenedDocument _document;
        private readonly PrintJobPlan _plan;

        /// <summary>Разрешение растра с уже применённым ограничением документа.</summary>
        private readonly double _dpi;

        private readonly IProgress<PrintProgress>? _progress;
        private readonly CancellationToken _ct;
        private readonly Action _onSheet;
        private readonly PrintPlanRenderer _renderer;

        public PlanPaginator(
            OpenedDocument document, PrintJobPlan plan, double dpi,
            IProgress<PrintProgress>? progress, CancellationToken ct, Action onSheet)
        {
            _document = document;
            _plan = plan;
            _dpi = dpi;
            _progress = progress;
            _ct = ct;
            _onSheet = onSheet;
            _renderer = new PrintPlanRenderer(document);
        }

        public override bool IsPageCountValid => true;
        public override int PageCount => _plan.Sheets.Count;
        public override Size PageSize { get; set; }
        public override IDocumentPaginatorSource? Source => null;

        public override DocumentPage GetPage(int pageNumber)
        {
            _ct.ThrowIfCancellationRequested();

            var sheet = _plan.Sheets[pageNumber];
            var composed = SheetComposer.Compose(sheet, _dpi);

            // Блокирующее ожидание здесь допустимо и намеренно: метод вызывается
            // на выделенном STA-потоке печати, а не на потоке интерфейса.
            var image = _renderer.RenderSheetAsync(composed, _ct).GetAwaiter().GetResult();
            var bitmap = BitmapFactory.ToBitmapSource(image);

            var sizeDiu = new Size(
                Units.PointsToDiu(sheet.PaperSizePt.WidthPt),
                Units.PointsToDiu(sheet.PaperSizePt.HeightPt));

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(sizeDiu));
                dc.DrawImage(bitmap, new Rect(sizeDiu));
            }

            _onSheet();
            _progress?.Report(new PrintProgress(pageNumber + 1, PageCount));
            return new DocumentPage(visual, sizeDiu, new Rect(sizeDiu), new Rect(sizeDiu));
        }
    }
}
