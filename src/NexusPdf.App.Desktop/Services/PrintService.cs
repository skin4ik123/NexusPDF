using System.Printing;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.ViewModels;
using NexusPdf.App.Desktop.Views;
using NexusPdf.Application;

namespace NexusPdf.App.Desktop.Services;

public sealed record PrintJob(IReadOnlyList<int> LogicalIndices, bool FitToPage);

/// <summary>
/// Печать: параметры (диапазон, масштаб) выбираются в собственном диалоге,
/// принтер и копии — в системном. Страницы рендерятся движком на фоновом
/// STA-потоке и отправляются в очередь печати; интерфейс не блокируется.
/// </summary>
public sealed class PrintService
{
    public async Task PrintInteractiveAsync(DocumentViewModel doc, Window owner)
    {
        var job = PrintOptionsDialog.Show(owner, doc);
        if (job == null)
            return;

        var printDialog = new System.Windows.Controls.PrintDialog();
        if (printDialog.ShowDialog() != true)
            return;

        var queue = printDialog.PrintQueue;
        var ticket = printDialog.PrintTicket;
        var printableSize = new Size(printDialog.PrintableAreaWidth, printDialog.PrintableAreaHeight);
        var dispatcher = Dispatcher.CurrentDispatcher;

        doc.IsBusy = true;
        doc.StatusText = Loc.Get("Printing");
        try
        {
            await RunOnStaThreadAsync(() =>
            {
                var writer = PrintQueue.CreateXpsDocumentWriter(queue);
                var paginator = new PdfPrintPaginator(
                    doc.Document, job, printableSize,
                    progress: (current, total) => dispatcher.BeginInvoke(() =>
                        doc.StatusText = Loc.F("PrintingPage", current, total)));
                writer.Write(paginator, ticket);
            });
            doc.StatusText = Loc.Get("PrintDone");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Ошибка печати");
            doc.StatusText = Loc.Get("Ready");
            ErrorDialog.Show(owner, Loc.Get("ErrorTitle"), Loc.Get("PrintFailed"), ex.ToString());
        }
        finally
        {
            doc.IsBusy = false;
        }
    }

    private static Task RunOnStaThreadAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
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
}

/// <summary>Пагинатор: каждая страница задания рендерится PDFium в растр 300 DPI и вписывается в область печати.</summary>
internal sealed class PdfPrintPaginator : DocumentPaginator
{
    private const double PtToDiu = 96.0 / 72.0;
    private const double RenderDpi = 300.0;
    private const int MaxRenderPixels = 8000;

    private readonly OpenedDocument _document;
    private readonly PrintJob _job;
    private readonly Size _printableSize;
    private readonly Action<int, int> _progress;

    public PdfPrintPaginator(OpenedDocument document, PrintJob job, Size printableSize, Action<int, int> progress)
    {
        _document = document;
        _job = job;
        _printableSize = printableSize;
        _progress = progress;
    }

    public override bool IsPageCountValid => true;
    public override int PageCount => _job.LogicalIndices.Count;
    public override Size PageSize { get; set; }
    public override IDocumentPaginatorSource? Source => null;

    public override DocumentPage GetPage(int pageNumber)
    {
        _progress(pageNumber + 1, PageCount);

        var logicalIndex = _job.LogicalIndices[pageNumber];
        var sizePt = _document.GetLogicalPageSize(logicalIndex);
        var pageDiu = new Size(sizePt.WidthPoints * PtToDiu, sizePt.HeightPoints * PtToDiu);

        Rect target;
        if (_job.FitToPage)
        {
            var scale = Math.Min(_printableSize.Width / pageDiu.Width, _printableSize.Height / pageDiu.Height);
            var w = pageDiu.Width * scale;
            var h = pageDiu.Height * scale;
            target = new Rect((_printableSize.Width - w) / 2, (_printableSize.Height - h) / 2, w, h);
        }
        else
        {
            // Фактический размер, по центру; крупные страницы могут быть обрезаны — это честное поведение режима.
            target = new Rect(
                (_printableSize.Width - pageDiu.Width) / 2,
                (_printableSize.Height - pageDiu.Height) / 2,
                pageDiu.Width, pageDiu.Height);
        }

        var pixelWidth = Math.Clamp((int)Math.Round(target.Width * RenderDpi / 96.0), 16, MaxRenderPixels);
        var pixelHeight = Math.Clamp((int)Math.Round(target.Height * RenderDpi / 96.0), 16, MaxRenderPixels);

        var raw = _document.RenderLogicalPageAsync(logicalIndex, pixelWidth, pixelHeight, CancellationToken.None)
            .GetAwaiter().GetResult();
        var bitmap = BitmapFactory.ToBitmapSource(raw);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, _printableSize.Width, _printableSize.Height));
            dc.DrawImage(bitmap, target);
        }
        return new DocumentPage(visual, _printableSize, new Rect(_printableSize), new Rect(_printableSize));
    }
}
