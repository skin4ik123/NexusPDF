using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Collections.Specialized;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.Services;
using NexusPdf.App.Desktop.Services.Ux;
using NexusPdf.App.Desktop.ViewModels;
using NexusPdf.App.Desktop.Views;
using NexusPdf.Application;
using NexusPdf.Domain;
using NexusPdf.Export;
using NexusPdf.Infrastructure;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;
using NexusPdf.Printing;
using NexusPdf.Ux;

internal static class Program
{
    private static readonly List<object> Results = new();
    private static readonly List<string> BindingErrors = new();
    private static string _output = "";
    private static AppServices _services = null!;
    private static MainWindow _main = null!;
    private static DocumentViewModel _document = null!;
    private static BitmapSource _bitmap = null!;
    private static byte[] _pixels = null!;
    private static string _imagePath = "";
    private static ExternalImageEditor _editor = null!;
    private static bool _verify;

    [STAThread]
    private static int Main(string[] args)
    {
        _output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/ui-audit");
        _verify = args.Contains("--verify");
        Directory.CreateDirectory(_output);
        var app = new ProbeApp();
        foreach (var theme in new[] { "Light", "Styles" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("/NexusPdf;component/Themes/" + theme + ".xaml", UriKind.Relative) });
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Loc.Load("ru");
        WindowScreenFit.HookAllWindows();
        CrispRendering.HookAllWindows();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingListener());
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        app.DispatcherUnhandledException += (_, e) =>
        {
            Results.Add(new { Kind = "unhandled", Error = e.Exception.ToString() });
            e.Handled = true;
        };
        var exitCode = 0;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try { await AuditAsync(); }
            catch (Exception e)
            {
                exitCode = 1;
                Results.Add(new { Kind = "fatal", Error = e.ToString() });
                Console.Error.WriteLine(e);
            }
            finally
            {
                File.WriteAllText(Path.Combine(_output, "audit.json"), JsonSerializer.Serialize(
                    new { Results, BindingErrors }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown();
            }
        });
        app.Run();
        return exitCode;
    }

    private static async Task AuditAsync()
    {
        var settings = new AppSettings { Language = "ru", Theme = "light", SingleInstance = false };
        _services = new AppServices(new PdfiumRenderEngine(), settings,
            new JsonSettingsStore(Path.Combine(_output, "settings.json")));
        _main = WindowManager.OpenWindow(_services, null);
        await Task.Delay(350);
        Capture(_main, "main-empty");

        const int width = 600, height = 800;
        _pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        for (var y = 100; y < 370; y++)
        for (var x = 70; x < 520; x++)
        {
            var p = (y * width + x) * 4;
            _pixels[p] = 215; _pixels[p + 1] = 185; _pixels[p + 2] = 55;
        }
        var spec = new ImagePageSpec(_pixels, width, height, 450, 600);
        var source = Path.Combine(_output, "fixture-source.pdf");
        await _services.Engine.CreateImageDocumentAsync(new[] { spec, spec, spec }, source, CancellationToken.None);
        var target = Path.Combine(_output, "fixture.pdf");
        await using (var handle = await _services.Engine.OpenAsync(source, null, CancellationToken.None))
        {
            var pages = Enumerable.Range(0, 3).Select(i => new ComposedPage(handle, i, 0,
                new PageOverlay[] { new TextOverlay("NexusPDF — проверка документа " + (i + 1),
                    30, 35, 16, 0xFF1E293B, 0) })).ToArray();
            await _services.Engine.ComposeAsync(pages, target, CancellationToken.None);
        }
        await _main.ViewModel.OpenFilesAsync(new[] { target });
        _document = _main.ViewModel.ActiveDocument ?? throw new Exception("Fixture did not open");
        await Task.Delay(800);
        Capture(_main, "main-reader");
        _document.IsOrganizeMode = true;
        await Task.Delay(400);
        Capture(_main, "main-organizer");
        _document.IsOrganizeMode = false;
        _main.ViewModel.Panels = _main.ViewModel.Panels with { Tools = true, Properties = true };
        await Task.Delay(250);
        Capture(_main, "main-tools");
        _main.ViewModel.Panels = PanelLayout.Default;
        ThemeManager.Apply("dark");
        await Task.Delay(200);
        Capture(_main, "main-dark");
        ThemeManager.Apply("light");

        _bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, _pixels, width * 4);
        _bitmap.Freeze();
        _imagePath = Path.Combine(_output, "fixture.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(_bitmap));
        using (var stream = File.Create(_imagePath)) encoder.Save(stream);
        _editor = new ExternalImageEditor(_imagePath); // Watch a fixture; never launch an editor.

        foreach (var type in typeof(MainWindow).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(MainWindow).Namespace && t.IsSubclassOf(typeof(Window)))
            .OrderBy(t => t.Name))
        {
            if (type == typeof(MainWindow)) continue;
            Console.WriteLine("INSPECT " + type.Name);
            try { await InspectDialogAsync(type); }
            catch (Exception e)
            {
                var cause = e is TargetInvocationException { InnerException: not null } tie ? tie.InnerException : e;
                Results.Add(new { Kind = "dialog-error", Window = type.Name, Error = cause!.ToString() });
                Console.WriteLine("DIALOG ERROR " + type.Name + ": " + cause.Message);
            }
        }
        if (_verify)
        {
            await InspectDialogAsync(typeof(ExportDocumentDialog), excel: true);
            ThemeManager.Apply("dark");
            foreach (var type in new[] { typeof(AboutDialog), typeof(ExportImagesDialog), typeof(OptimizeDocumentDialog), typeof(PrintCenterDialog), typeof(BatchDialog) })
                await InspectDialogAsync(type, suffix: "-dark");
            ThemeManager.Apply("light");
            await VerifyWorkflowsAsync();
        }
        _editor.Dispose();
        await _main.ViewModel.TryCloseAllAsync();
        _main.Close();
        await _services.DisposeAsync();
        Require(!Results.Any(r => JsonSerializer.SerializeToElement(r).GetProperty("Kind").GetString()
            is "unhandled" or "dialog-error"), "Every production window opens without an exception");
        Require(BindingErrors.Count == 0, "All visible controls have valid WPF bindings");
        Console.WriteLine("UI audit completed; " + Results.Count + " reports, " + BindingErrors.Count + " binding errors.");
    }

    private static async Task InspectDialogAsync(Type type, bool excel = false, string suffix = "")
    {
        // Public factories configure the real window; close it after capturing.
        var factory = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.DeclaringType == type && !m.IsSpecialName)
            .OrderByDescending(m => m.Name is "Show" or "Run" or "ShowAsync" or "Choose" or "Edit" or "Pick" or "Configure")
            .FirstOrDefault();
        if (factory == null) throw new InvalidOperationException("No public dialog factory: " + type.Name);
        var parameters = factory.GetParameters().Select(Argument).ToArray();
        if (excel)
            parameters[Array.FindIndex(factory.GetParameters(), p => p.Name == "forWord")] = false;
        if (_verify && type == typeof(ExportImagesDialog))
            parameters[Array.FindIndex(factory.GetParameters(), p => p.Name == "selectedPages")] = new[] { 2, 0, 2 };
        var captured = false;
        Exception? captureError = null;
        var attempts = 0;
        var name = (excel ? "ExportExcelDialog" : type.Name) + suffix;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(
            type == typeof(OptimizeDocumentDialog) || type == typeof(PrintCenterDialog) ? 1800 : 500) };
        timer.Tick += (_, _) =>
        {
            if (System.Windows.Application.Current.Windows.OfType<Window>()
                .FirstOrDefault(w => w.GetType() == type && w.IsVisible) is { } window)
            {
                timer.Stop();
                try
                {
                    Capture(window, name);
                    captured = true;
                    if (_verify) VerifyDialogControls(window, name);
                }
                catch (Exception e) { captureError = e; }
                finally { window.Close(); }
            }
            else if (++attempts > 30)
            {
                timer.Stop();
                foreach (var remaining in System.Windows.Application.Current.Windows.OfType<Window>().ToArray())
                    if (remaining != _main) remaining.Close();
            }
        };
        timer.Start();
        try
        {
            var result = factory.Invoke(null, parameters);
            if (result is Task pending) await pending;
            if (captureError != null) throw captureError;
            if (!captured) throw new InvalidOperationException("Factory returned without a visible window");
            if (_verify && result is ExportImagesRequest images)
                Require(images.Pages!.SequenceEqual(new[] { 1, 2 }) && images.Jpeg && images.JpegQuality == 80,
                    name + " exports only the chosen pages with the chosen JPEG quality");
            if (_verify && result is ExportDocumentRequest document)
                Require(document.Pages!.SequenceEqual(new[] { 1, 2 }), name + " exports only the chosen pages");
        }
        finally { timer.Stop(); }
    }

    private static void VerifyDialogControls(Window window, string name)
    {
        if (window is ExportDocumentDialog or ExportImagesDialog)
        {
            var all = (RadioButton)window.FindName("ScopeAll");
            var current = (RadioButton)window.FindName("ScopeCurrent");
            var range = (RadioButton)window.FindName("ScopeRange");
            var box = (TextBox)window.FindName("RangeBox");
            var export = (Button)window.FindName("ExportButton");
            if (window is ExportImagesDialog)
                Require(range.IsChecked == true && box.Text == "1,3", name + " preselects organizer pages without duplicates");
            range.IsChecked = true;
            box.Text = "";
            Require(!export.IsEnabled, name + " rejects an empty range");
            box.Text = "99";
            Require(!export.IsEnabled, name + " rejects pages outside the document");
            current.IsChecked = true;
            Require(range.IsChecked == false && all.IsChecked == false && export.IsEnabled, name + " switches to the current page cleanly");
            all.IsChecked = true;
            Require(range.IsChecked == false && current.IsChecked == false && export.IsEnabled, name + " switches to all pages cleanly");
            range.IsChecked = true;
            box.Text = window is ExportImagesDialog ? "2-3,3" : "2-3";
            Require(export.IsEnabled && all.IsChecked == false && current.IsChecked == false, name + " accepts a valid range");
            if (window is ExportImagesDialog)
            {
                ((RadioButton)window.FindName("FormatJpeg")).IsChecked = true;
                var quality = (ComboBox)window.FindName("QualityCombo");
                quality.SelectedIndex = 0;
                window.UpdateLayout();
                Require(quality.IsVisible, name + " exposes JPEG quality only for JPEG");
            }
            Capture(window, name + "-configured");
            export.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        if (window is BatchDialog)
            Require(!((Button)window.FindName("RunButton")).IsEnabled, name + " disables processing an empty batch");
        if (window is BatchPrintDialog)
            Require(!((Button)window.FindName("StartButton")).IsEnabled, name + " disables printing an empty batch");
        if (window is CompareDialog)
            Require(!((Button)window.FindName("RunButton")).IsEnabled, name + " waits for both comparison files");
        if (window is AboutDialog or SignatureLibraryDialog)
        {
            var root = (FrameworkElement)window.Content;
            root.UpdateLayout();
            Require(Descendants(root).OfType<Button>().Where(b => b.IsVisible).All(b =>
                b.TransformToAncestor(root).TransformBounds(new Rect(b.RenderSize)).Left >= -1),
                name + " keeps every action within the visible window");
        }
    }

    private static object? Argument(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        var name = parameter.Name ?? "";
        if (type == typeof(Window)) return _main;
        if (type == typeof(AppServices)) return _services;
        if (type == typeof(DocumentViewModel)) return _document;
        if (type == typeof(IPdfRenderEngine)) return _services.Engine;
        if (type == typeof(IPdfDocumentHandle)) return _document.Document.PrimaryHandle;
        if (type == typeof(BitmapSource)) return _bitmap;
        if (type == typeof(ExternalImageEditor)) return _editor;
        if (type == typeof(SignatureStore)) return _services.Signatures;
        if (type == typeof(UxCommandHub)) return _main.ViewModel.Ux;
        if (type == typeof(SelectionContext)) return _main.ViewModel.Ux.Snapshot();
        if (type == typeof(QuickPanel)) return _main.ViewModel.QuickPanel;
        if (type == typeof(NexusPdf.App.Desktop.Services.Printing.PrintQueueService)) return _services.PrintQueue;
        if (type == typeof(ScanPreviewPage)) return new ScanPreviewPage(_pixels, 600, 800, 1);
        if (type == typeof(DocumentImageProfile)) return DocumentImageProfile.Unknown;
        if (type == typeof(PrintJobPlan)) return ManualPlan();
        if (type == typeof(Func<string, Task<bool>>)) return new Func<string, Task<bool>>(_ => Task.FromResult(true));
        if (type == typeof(Func<int, Task<byte[]>>)) return new Func<int, Task<byte[]>>(_ => Task.FromResult(new byte[128]));
        if (type == typeof(IReadOnlyList<PdfAttachment>)) return new[] { new PdfAttachment(0, "Приложение.pdf", 81920), new PdfAttachment(1, "Таблица.xlsx", 15360) };
        if (type == typeof(IReadOnlyList<PdfLayer>)) return new[] { new PdfLayer("1 0 R", "Основной чертёж", true), new PdfLayer("2 0 R", "Размеры и примечания", true), new PdfLayer("3 0 R", "Дополнительный слой", false) };
        if (type == typeof(IReadOnlyList<DocumentViewModel>)) return new[] { _document };
        if (parameter.HasDefaultValue && parameter.DefaultValue == null) return null;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            return Array.CreateInstance(type.GetGenericArguments()[0], 0);
        if (type == typeof(int)) return name.Contains("Count", StringComparison.OrdinalIgnoreCase) ? 3 : 0;
        if (type == typeof(double)) return 35.0;
        if (type == typeof(bool)) return !name.Contains("wrong", StringComparison.OrdinalIgnoreCase);
        if (type == typeof(string)) return name.Contains("Path", StringComparison.OrdinalIgnoreCase) ? Path.Combine(_output, "fixture.pdf")
            : name.Contains("ocr", StringComparison.OrdinalIgnoreCase) ? _services.Ocr.EngineName
            : name.Contains("title", StringComparison.OrdinalIgnoreCase) ? "Проверка NexusPDF"
            : name.Contains("file", StringComparison.OrdinalIgnoreCase) ? "Документ.pdf"
            : name.Contains("accept", StringComparison.OrdinalIgnoreCase) ? "Продолжить"
            : "Пример текста для проверки расположения элементов и удобства чтения.";
        if (parameter.HasDefaultValue) return parameter.DefaultValue;
        throw new NotSupportedException("Unsupported factory argument " + type.Name + " " + name);
    }

    private static PrintJobPlan ManualPlan()
    {
        var paper = new PaperSizeOption("A4", new SizePt(595.28, 841.89));
        var caps = new PrinterCapabilities { PrinterName = "Проверочный принтер", PaperSizes = new[] { paper },
            HardMarginsByPaper = new Dictionary<string, MarginsPt> { ["A4"] = MarginsPt.Zero } };
        var layout = new PrintLayoutEngine();
        var sheets = layout.BuildSheets(Enumerable.Range(0, 3).Select(i => new SourcePage("doc", i, paper.SizePt)).ToList(),
            new LayoutSettings { Duplex = DuplexMode.Manual }, paper, caps);
        return new PrintJobPlan { JobName = "Проверка", PrinterName = caps.PrinterName, Capabilities = caps,
            Sheets = layout.ApplyDuplexPairing(sheets, DuplexMode.Manual, new LayoutSettings().Imposition),
            Duplex = DuplexMode.Manual };
    }

    private static async Task VerifyWorkflowsAsync()
    {
        var jpegPixels = new byte[600 * 800 * 4];
        new Random(42).NextBytes(jpegPixels);
        for (var i = 3; i < jpegPixels.Length; i += 4) jpegPixels[i] = 255;
        File.WriteAllBytes(Path.Combine(_output, "compressed-photo.jpg"), ImageEncoder.EncodeJpeg(jpegPixels, 600, 800, 10));
        var original = _document.Pages.ToArray();
        _document.IsFindVisible = true;
        _document.FindQuery = "NexusPDF";
        await _document.RunSearchAsync();
        Require(_document.Matches.Count == 3, "Search finds all fixture pages");
        _document.CurrentPageNumber = 3;
        var actions = new List<NotifyCollectionChangedAction>();
        NotifyCollectionChangedEventHandler listener = (_, e) => actions.Add(e.Action);
        _document.Pages.CollectionChanged += listener;
        _document.Document.Session.Apply(new AddOverlayOperation(0, new NoteAnnotationDraft(40, 90, "Audit note", "NexusPDF")));
        Require(ReferenceEquals(original[1], _document.Pages[1]) && ReferenceEquals(original[2], _document.Pages[2]),
            "Unchanged page models and thumbnails survive a note");
        Require(actions.SequenceEqual(new[] { NotifyCollectionChangedAction.Replace }), "A note updates one page without resetting the document");
        await WaitUntilAsync(() => _document.Matches.Count == 3, "Search refreshes after an edit");
        Require(_document.CurrentPageNumber == 3, "Automatic search refresh preserves reading position");
        _document.IsBusy = true;
        _document.UndoCommand.Execute(null);
        Require(_document.Document.Session.Model.Pages[0].OverlayList.Count == 1, "Busy documents reject Ctrl+Z");
        _document.IsBusy = false;
        _document.UndoCommand.Execute(null);
        Require(!_document.IsDirty, "Undo restores the clean document");
        _document.Pages.CollectionChanged -= listener;
        _document.FindQuery = "";
        _document.IsFindVisible = false;
        Capture(_main, "verified-reader");

        var refs = _document.Document.Session.Model.Pages.ToArray();
        _document.Document.Session.Apply(new InsertPagesOperation(3,
            Enumerable.Range(0, 297).Select(i => refs[i % refs.Length]).ToArray()));
        var before = _document.Pages.ToArray();
        var watch = Stopwatch.StartNew();
        _document.Document.Session.Apply(new AddOverlayOperation(150, new NoteAnnotationDraft(40, 90, "Performance", "NexusPDF")));
        watch.Stop();
        var kept = before.Where((page, i) => ReferenceEquals(page, _document.Pages[i])).Count();
        Require(kept == 299, "Editing a 300-page document keeps the other 299 pages");
        Results.Add(new { Kind = "performance", Pages = 300, Reused = kept, Milliseconds = watch.Elapsed.TotalMilliseconds });
        _document.Document.Session.Undo();
        _document.Document.Session.Undo();
        Require(_document.PageCount == 3 && !_document.IsDirty, "Large-document edits undo without losing pages");

        var page = new TextOverlay("NEXUS PDF 12345", 30, 480, 36, 0xFF000000, 0);
        var textPdf = Path.Combine(_output, "ocr-text-source.pdf");
        await _services.Engine.ComposeAsync(new[] { new ComposedPage(_document.Document.PrimaryHandle, 0, 0,
            new PageOverlay[] { page }) }, textPdf, CancellationToken.None);
        RenderedPageImage raster;
        await using (var handle = await _services.Engine.OpenAsync(textPdf, null, CancellationToken.None))
            raster = await handle.RenderPageAsync(0, 900, 1200, 0, CancellationToken.None);
        var scanPath = Path.Combine(_output, "ocr-scan.pdf");
        await _services.Engine.CreateImageDocumentAsync(new[] { new ImagePageSpec(raster.Bgra, raster.PixelWidth, raster.PixelHeight, 450, 600) },
            scanPath, CancellationToken.None);
        await using var scan = await OpenedDocument.OpenAsync(_services.Engine, scanPath, null, CancellationToken.None);
        Require(string.IsNullOrWhiteSpace(await scan.PrimaryHandle.GetPageTextAsync(0, CancellationToken.None)), "OCR fixture has no native text");
        _services.ApplyOcrSettings("tesseract", "cyrillic");
        Require(_services.Ocr.IsAvailable && _services.OcrEngine.Id == "tesseract", "Changing the OCR engine takes effect");
        var target = Path.Combine(_output, "ocr-switch.docx");
        var summary = await _services.Convert.ExportToWordAsync(scan, target, null, new WordExportOptions(KeepImages: false),
            new PageAnalysisOptions(RecognizeScans: true), null, CancellationToken.None);
        using var word = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(target, false);
        Require(summary.RecognizedPages == 1 && word.MainDocumentPart!.Document!.InnerText.Contains("NEXUS", StringComparison.OrdinalIgnoreCase),
            "Word export recognizes scans after switching the OCR engine");
    }

    private static void Require(bool condition, string description)
    {
        Results.Add(new { Kind = "check", Description = description, Passed = condition });
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine("PASS " + description);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Require(condition(), description);
    }

    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using (var stream = File.Create(Path.Combine(_output, name + ".png"))) encoder.Save(stream);
        var controls = Descendants(window).OfType<FrameworkElement>().Where(e => e.IsVisible &&
            (e is Button || e is TextBox || e is ComboBox)).Select(e => new
            { Type = e.GetType().Name, e.Name, e.IsEnabled, e.ActualWidth, e.ActualHeight }).ToArray();
        Results.Add(new { Kind = "screenshot", Window = name, Width = width, Height = height, Controls = controls });
        File.WriteAllText(Path.Combine(_output, "audit-progress.json"), JsonSerializer.Serialize(
            new { Results, BindingErrors }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("CAPTURE " + name + " " + width + "x" + height);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private sealed class ProbeApp : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e) { }
        protected override void OnExit(ExitEventArgs e) { }
    }

    private sealed class BindingListener : TraceListener
    {
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) BindingErrors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
