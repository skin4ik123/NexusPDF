using System.Printing;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.Application;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Printing;
using NexusPdf.Printing.Windows;

namespace NexusPdf.App.Desktop.Services.Printing;

/// <summary>Состояние отправленного задания.</summary>
/// <param name="JobIds">
/// Номера заданий в очереди Windows. Их может быть несколько: документ на
/// разной бумаге уходит частями, и каждая часть — своё задание. По этим
/// номерам программа потом показывает ход и отменяет печать.
/// </param>
public sealed record PrintJobStatus(
    IReadOnlyList<int> JobIds,
    string PrinterName,
    int SheetsSent);

/// <summary>Ход подготовки задания: сколько листов уже отрисовано.</summary>
public sealed record PrintProgress(int SheetsDone, int SheetsTotal);

/// <summary>
/// Почему задание не ушло: код строки словаря и её значения. Текст ошибки
/// показывается человеку главным сообщением, а не прячется в «Подробности».
/// </summary>
public sealed class PrintSubmitException(string code, params object[] args) : Exception(code)
{
    public string Code { get; } = code;
    public object[] Args { get; } = args;
}

/// <summary>
/// Печать остановлена человеком. Несёт то, что уже успело уйти в очередь:
/// сообщение «подготовлено листов: 0» после отправки половины задания было
/// неправдой, а части, уже переданные принтеру, должны остаться в очереди
/// программы, чтобы их можно было отменить.
/// </summary>
public sealed class PrintCancelledException(int sheetsSent, IReadOnlyList<int> jobIds, string printerName)
    : OperationCanceledException
{
    public int SheetsSent { get; } = sheetsSent;
    public IReadOnlyList<int> JobIds { get; } = jobIds;
    public string PrinterName { get; } = printerName;
}

/// <summary>
/// Отправка задания в очередь Windows. Листы готовятся ПОСТРАНИЧНО прямо во
/// время передачи: WPF запрашивает страницу у пагинатора по мере записи, и
/// тысяча листов не оказывается в памяти целиком.
/// </summary>
public sealed class PrintJobService
{
    public async Task<PrintJobStatus> SubmitAsync(
        OpenedDocument document,
        PrintJobPlan plan,
        IProgress<PrintProgress>? progress,
        CancellationToken ct)
    {
        if (plan.Sheets.Count == 0)
            throw new PrintSubmitException("PrintNoSheets");

        // Разрешения перечитываются перед самой отправкой, а не берутся из
        // плана: между открытием окна и нажатием «Печать» документ мог
        // смениться на другой в той же вкладке.
        var permissions = PrintPermissions.FromFlags(
            await document.PrimaryHandle.GetPermissionsAsync(ct).ConfigureAwait(false));
        if (!permissions.AllowPrint)
            throw new PrintSubmitException("PfPrintForbidden");
        var dpi = permissions.LimitDpi(plan.OutputDpi);

        // Проверка перед отправкой: принтер мог исчезнуть, пока окно было
        // открыто. Лёгкая — существование очереди, без опроса возможностей:
        // полный повторный опрос здесь добавлял до минуты ДО начала печати.
        if (!WindowsPrinterService.QueueExists(plan.PrinterName))
            throw new PrintSubmitException("PrintPrinterUnavailable", plan.PrinterName);

        // Бумага задаётся одним PrintTicket на задание, поэтому документ на
        // разной бумаге уходит несколькими заданиями — подряд идущими частями,
        // в порядке документа. Ориентация разбиения не требует.
        var parts = JobSplitter.SplitByPaperSize(plan);
        var sent = 0;
        var jobIds = new List<int>();

        // Копии размножает программа, если принтер не умеет сам: не сортирует
        // копии комплектами или принимает меньше копий, чем заказано. Раньше
        // окно обещало «копии раскладывает программа», а задание уходило с
        // одним лишь числом копий в билете — и драйвер без поддержки копий
        // печатал одну.
        var appCopies = plan.Copies > 1 &&
                        (plan.CollationBy == CollationExecutor.Application ||
                         plan.Capabilities.MaxCopies < plan.Copies)
            ? plan.Copies
            : 1;

        try
        {
            for (var p = 0; p < parts.Count; p++)
            {
                ct.ThrowIfCancellationRequested();
                var partPlan = parts[p].Plan;
                var jobName = parts.Count > 1
                    ? Loc.F("PrintJobNamePart", document.DisplayName, p + 1, parts.Count)
                    : Loc.F("PrintJobName", document.DisplayName);

                await RunOnStaThreadAsync(() =>
                {
                    using var server = new PrintServer();
                    using var queue = new PrintQueue(server, partPlan.PrinterName);

                    // Имя задания видно и в нашей очереди, и в системной: «Документ»
                    // среди десятка чужих заданий не говорит ничего.
                    queue.CurrentJobSettings.Description = jobName;

                    var landscape = IsLandscapeJob(partPlan);
                    var ticket = BuildTicket(queue, partPlan, landscape, appCopies > 1 ? 1 : partPlan.Copies);
                    var writer = PrintQueue.CreateXpsDocumentWriter(queue);
                    var paginator = new PlanPaginator(document, partPlan, dpi, landscape, appCopies,
                        progress, ct, onSheet: () => Interlocked.Increment(ref sent));

                    // Что было в очереди ДО отправки: своё задание опознаётся как
                    // новое с нашим именем. Прямого «номер только что созданного
                    // задания» система печати не отдаёт.
                    var before = JobIdsOf(queue);
                    try
                    {
                        writer.Write(paginator, ticket);
                    }
                    finally
                    {
                        // И при остановке тоже: часть, уже переданная принтеру,
                        // должна остаться в очереди программы — её можно отменить.
                        foreach (var id in JobIdsOf(queue).Except(before))
                            lock (jobIds) jobIds.Add(id);
                    }
                }, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw new PrintCancelledException(sent, jobIds, plan.PrinterName);
        }

        return new PrintJobStatus(jobIds, plan.PrinterName, sent);
    }

    /// <summary>
    /// Ориентация задания — по большинству листов: альбомный чертёж с книжной
    /// обложкой печатается альбомным заданием, книжный отчёт с парой альбомных
    /// таблиц — книжным. Листы другой ориентации поворачиваются на странице.
    /// </summary>
    private static bool IsLandscapeJob(PrintJobPlan plan) =>
        plan.Sheets.Count(s => s.PaperSizePt.IsLandscape) * 2 > plan.Sheets.Count;

    /// <summary>Номера заданий, которые сейчас в очереди принтера.</summary>
    private static HashSet<int> JobIdsOf(PrintQueue queue)
    {
        var ids = new HashSet<int>();
        try
        {
            queue.Refresh();
            foreach (var job in queue.GetPrintJobInfoCollection())
                using (job)
                    ids.Add(job.JobIdentifier);
        }
        catch (Exception)
        {
            // Не смогли заглянуть в очередь — печать от этого не страдает,
            // просто задание не попадёт в наш список для отмены.
        }
        return ids;
    }

    /// <summary>
    /// PrintTicket из плана, СОГЛАСОВАННЫЙ с возможностями принтера.
    /// MergeAndValidatePrintTicket обязателен: драйвер имеет право поправить
    /// несовместимое сочетание, и работать надо с тем, что он вернул, а не с
    /// тем, что мы попросили.
    /// </summary>
    private static PrintTicket BuildTicket(PrintQueue queue, PrintJobPlan plan, bool landscape, int ticketCopies)
    {
        var ticket = queue.UserPrintTicket?.Clone() ?? new PrintTicket();

        ticket.CopyCount = Math.Max(1, ticketCopies);
        ticket.Collation = plan.Collation == CollationMode.Collated ? Collation.Collated : Collation.Uncollated;

        ticket.Duplexing = plan.Duplex switch
        {
            DuplexMode.LongEdge => Duplexing.TwoSidedLongEdge,
            DuplexMode.ShortEdge => Duplexing.TwoSidedShortEdge,
            // Ручной дуплекс печатается как обычная односторонняя: переворот
            // делает человек, а не драйвер.
            _ => Duplexing.OneSided,
        };

        ticket.PageOrientation = landscape ? PageOrientation.Landscape : PageOrientation.Portrait;

        var first = plan.Sheets[0];
        ticket.OutputColor = first.Color switch
        {
            ColorMode.Grayscale => OutputColor.Grayscale,
            ColorMode.Monochrome => OutputColor.Monochrome,
            ColorMode.Color => OutputColor.Color,
            _ => ticket.OutputColor,
        };

        // Бумага — физическая, книжной стороной: ориентация задаётся отдельно.
        var physical = JobSplitter.Physical(first.PaperSizePt);
        ticket.PageMediaSize = FindQueueMediaSize(queue, ticket, physical)
                               ?? BuildMediaSize(FindPaper(plan.Capabilities, physical), physical);

        var validated = queue.MergeAndValidatePrintTicket(queue.UserPrintTicket, ticket);
        if (validated.ValidatedPrintTicket != null)
            return validated.ValidatedPrintTicket;
        return ticket;
    }

    private static PaperSizeOption? FindPaper(PrinterCapabilities caps, SizePt physical) =>
        caps.PaperSizes.FirstOrDefault(p => JobSplitter.SamePaper(p.SizePt, physical));

    /// <summary>
    /// Формат из СОБСТВЕННОГО списка очереди с тем же физическим размером: так
    /// драйвер получает свой формат по имени — конверты, фото- и фирменные
    /// размеры, а не безымянный «пользовательский» размер, который часть
    /// драйверов печатает на бумаге из другого лотка.
    /// </summary>
    private static PageMediaSize? FindQueueMediaSize(PrintQueue queue, PrintTicket ticket, SizePt physical)
    {
        try
        {
            var widthDiu = Units.PointsToDiu(physical.WidthPt);
            var heightDiu = Units.PointsToDiu(physical.HeightPt);
            const double toleranceDiu = 3; // около 0,8 мм
            return queue.GetPrintCapabilities(ticket).PageMediaSizeCapability.FirstOrDefault(m =>
                m.Width is { } w && m.Height is { } h &&
                Math.Abs(Math.Min(w, h) - widthDiu) <= toleranceDiu &&
                Math.Abs(Math.Max(w, h) - heightDiu) <= toleranceDiu);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Список форматов очереди не прочитан — формат задаётся размером");
            return null;
        }
    }

    /// <summary>
    /// Формат бумаги для PrintTicket, когда очередь своего списка не дала. Код
    /// драйвера (dmPaperSize из winspool) переводится в имя схемы печати для
    /// стандартных форматов; всё остальное задаётся физическим размером.
    /// </summary>
    private static PageMediaSize BuildMediaSize(PaperSizeOption? paper, SizePt physical)
    {
        var size = paper != null ? JobSplitter.Physical(paper.SizePt) : physical;
        var widthDiu = Units.PointsToDiu(size.WidthPt);
        var heightDiu = Units.PointsToDiu(size.HeightPt);

        if (paper?.DriverValue != null && short.TryParse(paper.DriverValue, out var dm) &&
            DmPaperToMediaName.TryGetValue(dm, out var name))
            return new PageMediaSize(name, widthDiu, heightDiu);

        return new PageMediaSize(widthDiu, heightDiu);
    }

    /// <summary>Стандартные коды DEVMODE.dmPaperSize → имена схемы печати.</summary>
    private static readonly Dictionary<short, PageMediaSizeName> DmPaperToMediaName = new()
    {
        [1] = PageMediaSizeName.NorthAmericaLetter,
        [3] = PageMediaSizeName.NorthAmericaTabloid,
        [5] = PageMediaSizeName.NorthAmericaLegal,
        [7] = PageMediaSizeName.NorthAmericaExecutive,
        [8] = PageMediaSizeName.ISOA3,
        [9] = PageMediaSizeName.ISOA4,
        [11] = PageMediaSizeName.ISOA5,
        [12] = PageMediaSizeName.JISB4,
        [13] = PageMediaSizeName.JISB5,
        [70] = PageMediaSizeName.ISOA6,
    };

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
            catch (Exception ex) when (ct.IsCancellationRequested)
            {
                // Остановка посреди записи: WPF заворачивает отмену из
                // пагинатора в свою ошибку записи — это всё равно остановка.
                Serilog.Log.Debug(ex, "Печать прервана во время записи задания");
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
        private readonly PrintJobPlan _plan;

        /// <summary>Разрешение растра с уже применённым ограничением документа.</summary>
        private readonly double _dpi;

        private readonly bool _landscapeJob;
        private readonly int _copies;

        /// <summary>
        /// Страниц на одну копию: при двусторонней печати нечётное число сторон
        /// добирается пустой, иначе следующая копия началась бы на обороте
        /// последнего листа предыдущей.
        /// </summary>
        private readonly int _pagesPerCopy;

        private readonly IProgress<PrintProgress>? _progress;
        private readonly CancellationToken _ct;
        private readonly Action _onSheet;
        private readonly PrintPlanRenderer _renderer;

        public PlanPaginator(
            OpenedDocument document, PrintJobPlan plan, double dpi, bool landscapeJob, int copies,
            IProgress<PrintProgress>? progress, CancellationToken ct, Action onSheet)
        {
            _plan = plan;
            _dpi = dpi;
            _landscapeJob = landscapeJob;
            _copies = Math.Max(1, copies);
            var twoSided = plan.Duplex is DuplexMode.LongEdge or DuplexMode.ShortEdge;
            _pagesPerCopy = plan.Sheets.Count + (twoSided && _copies > 1 && plan.Sheets.Count % 2 == 1 ? 1 : 0);
            _progress = progress;
            _ct = ct;
            _onSheet = onSheet;
            _renderer = new PrintPlanRenderer(document);
        }

        public override bool IsPageCountValid => true;
        public override int PageCount => _pagesPerCopy * _copies;
        public override Size PageSize { get; set; }
        public override IDocumentPaginatorSource? Source => null;

        public override DocumentPage GetPage(int pageNumber)
        {
            _ct.ThrowIfCancellationRequested();

            var index = pageNumber % _pagesPerCopy;
            var sheet = index < _plan.Sheets.Count ? _plan.Sheets[index] : null;
            var paperPt = sheet?.PaperSizePt ?? _plan.Sheets[^1].PaperSizePt;

            // Лист чужой ориентации поворачивается на странице задания на
            // четверть оборота против часовой — так же, как драйвер кладёт
            // альбомную страницу на книжный лист.
            var rotate = paperPt.IsLandscape != _landscapeJob;
            var pagePt = rotate ? new SizePt(paperPt.HeightPt, paperPt.WidthPt) : paperPt;
            var pageDiu = new Size(Units.PointsToDiu(pagePt.WidthPt), Units.PointsToDiu(pagePt.HeightPt));

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(pageDiu));
                if (sheet != null)
                {
                    var composed = SheetComposer.Compose(sheet, _dpi);
                    // Блокирующее ожидание здесь допустимо и намеренно: метод вызывается
                    // на выделенном STA-потоке печати, а не на потоке интерфейса.
                    var image = _renderer.RenderSheetAsync(composed, _ct).GetAwaiter().GetResult();
                    var bitmap = ForSpool(BitmapFactory.ToBitmapSource(image), sheet.Color);
                    var sheetDiu = new Size(Units.PointsToDiu(paperPt.WidthPt), Units.PointsToDiu(paperPt.HeightPt));
                    if (rotate)
                    {
                        dc.PushTransform(new TranslateTransform(0, pageDiu.Height));
                        dc.PushTransform(new RotateTransform(-90));
                        dc.DrawImage(bitmap, new Rect(sheetDiu));
                        dc.Pop();
                        dc.Pop();
                    }
                    else
                    {
                        dc.DrawImage(bitmap, new Rect(sheetDiu));
                    }
                }
            }

            _onSheet();
            _progress?.Report(new PrintProgress(pageNumber + 1, PageCount));
            return new DocumentPage(visual, pageDiu, new Rect(pageDiu), new Rect(pageDiu));
        }

        /// <summary>
        /// Серый и чёрно-белый листы уходят в очередь серым и однобитным
        /// растром, а не 32-битным цветным: задание в очереди в 4–32 раза
        /// меньше, и медленная сеть до принтера это чувствует. Цвет листа к
        /// этому моменту уже переведён программой, так что потерь нет.
        /// </summary>
        private static BitmapSource ForSpool(BitmapSource bitmap, ColorMode color)
        {
            var format = color switch
            {
                ColorMode.Grayscale => PixelFormats.Gray8,
                ColorMode.Monochrome => PixelFormats.BlackWhite,
                _ => (PixelFormat?)null,
            };
            if (format == null) return bitmap;
            var converted = new FormatConvertedBitmap(bitmap, format.Value, null, 0);
            converted.Freeze();
            return converted;
        }
    }
}
