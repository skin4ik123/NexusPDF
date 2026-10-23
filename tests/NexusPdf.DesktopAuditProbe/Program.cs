using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Collections.Specialized;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
using NexusPdf.Pdf.Abstractions.TextBlocks;
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
        await VerifyPageRenderingAsync();
        var jpegPixels = new byte[600 * 800 * 4];
        new Random(42).NextBytes(jpegPixels);
        for (var i = 3; i < jpegPixels.Length; i += 4) jpegPixels[i] = 255;
        File.WriteAllBytes(Path.Combine(_output, "compressed-photo.jpg"), ImageEncoder.EncodeJpeg(jpegPixels, 600, 800, 10));
        var original = _document.Pages.ToArray();
        _document.IsFindVisible = true;
        _document.FindQuery = "NexusPDF";
        await _document.RunSearchAsync();
        Require(_document.Matches.Count == 3, "Search finds all fixture pages");
        _document.GoToPage(3);
        await WaitUntilAsync(() => _document.CurrentPageNumber == 3, "Page navigation scrolls to the third page");
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
        Results.Add(new { Kind = "performance", Pages = 300, Reused = kept, Milliseconds = watch.Elapsed.TotalMilliseconds });
        Require(kept == 299, "Editing a 300-page document keeps the other 299 pages");
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
        await InspectReaderToolsAsync();
        await VerifyTextBlocksAsync();
    }

    /// <summary>
    /// Инструмент «Текст» от начала до конца: блок в пустой клетке, набор,
    /// жирный, другой шрифт, ширина, сохранение, повторное открытие, правка
    /// сохранённого блока по двойному щелчку, кириллица без IME, блок с
    /// переносом по протянутой рамке.
    /// </summary>
    private static async Task VerifyTextBlocksAsync()
    {
        _document.CurrentPageNumber = 1;
        await WaitForVisiblePageAsync("Text block fixture page is shown");
        var page = _document.Pages[0];

        _document.ToggleTextTool();
        Require(_document.IsTextTool, "The Text tool turns on");
        // Щелчок инструментом: сначала поиск блока под точкой, потом новый блок.
        if (!await _document.EditTextBlockAsync(page, 110, 160))
            await _document.BeginTextBlockAtAsync(page, 110, 160);
        var editor = await FindTextEditorAsync("A click with the Text tool opens the on-page editor");
        Require(Keyboard.FocusedElement == editor, "The block editor takes the keyboard");
        Type(editor, "24 Oct 2026");
        Capture(_main, "text-block-typing");

        var model = _document.ActiveTextBlock ?? throw new InvalidOperationException("No active block");
        model.SelectAll();
        _document.TextBold = true;
        var family = FontCatalog.System.Families.FirstOrDefault(f => f.Embeddable && f.Supported && f.HasCyrillic && f.Name != _document.TextFamily)?.Name;
        if (family != null)
            _document.SetTextFamily(family);
        _document.TextBoxWMm = 80;
        Require(model.Block.WidthMode == TextBlockWidthMode.Fixed && Math.Abs(model.Block.WidthPt - 80 * 72 / 25.4) < 0.01,
            "Resizing the block fixes its width");
        _document.CommitTextBlock();
        var block = _document.Document.Session.Model.Pages[0].OverlayList.OfType<TextBlockOverlay>().LastOrDefault();
        Require(block != null && block.Paragraphs[0].Text == "24 Oct 2026" && block.Paragraphs[0].Runs.All(r => r.Style.Bold),
            "Finished typing becomes one bold text block");
        Require(family == null || block!.Paragraphs[0].Runs.All(r => r.Style.FontFamily == family), "The picked font reaches the block");

        var saved = Path.Combine(_output, "text-block.pdf");
        await _services.SaveService.SaveAsAsync(_document.Document, saved, keepBackup: false, CancellationToken.None);
        await _main.ViewModel.OpenFilesAsync(new[] { saved });
        var reopened = _main.ViewModel.ActiveDocument ?? throw new InvalidOperationException("Saved file did not open");
        // После «Сохранить как» вкладка переходит на записанный файл: источник
        // получает новый Id, и страницы пересобираются следующим проходом
        // диспетчера. Брать страницу надо уже из нового состава.
        await WaitUntilAsync(() => reopened.Pages.Count > 0 &&
            reopened.Document.Handles.ContainsKey(reopened.Pages[0].PageRef.SourceId),
            "The tab shows the saved file after Save As");
        var reopenedPage = reopened.Pages[0];
        await reopened.EnsureStoredTextBlocksAsync(reopenedPage);
        Require(await reopened.EditTextBlockAsync(reopenedPage, block!.XPt + 4, block.YPt + 4),
            "A double click opens the saved block again");
        editor = await FindTextEditorAsync("The saved block opens in the editor");
        Require(reopened.ActiveTextBlock?.Block.Paragraphs[0].Text == "24 Oct 2026", "The saved block keeps its text");
        reopened.ActiveTextBlock!.MoveCaret(CaretMove.BlockEnd, extend: false);
        Type(editor, " — готово");
        reopened.CommitTextBlock();
        Require(reopened.Document.Session.Model.Pages[0].OverlayList.OfType<TextBlockReplacement>()
                .SingleOrDefault()?.BlockId == block.BlockId, "Editing a saved block replaces it under the same Id");

        var resaved = Path.Combine(_output, "text-block-edited.pdf");
        await _services.SaveService.SaveAsAsync(reopened.Document, resaved, keepBackup: false, CancellationToken.None);
        await using (var check = await _services.Engine.OpenAsync(resaved, null, CancellationToken.None))
        {
            var text = await check.GetPageTextAsync(0, CancellationToken.None);
            Require(text.Contains("24 Oct 2026 — готово", StringComparison.Ordinal), "The edited block text is in the file");
            var stored = await check.GetTextBlocksAsync(0, 0, CancellationToken.None);
            Require(stored.Count(b => b.Intact) == 1 && stored.Single(b => b.Intact).Id == block.BlockId,
                "The file holds exactly one intact block with the original Id");
        }

        reopened.ToggleTextTool();
        await reopened.BeginTextBlockInRectAsync(reopenedPage, new OverlayBox(60, 300, 120, 20));
        editor = await FindTextEditorAsync("A dragged frame opens a wrapping block");
        Type(editor, "Длинная строка текста переносится по словам внутри рамки заданной ширины");
        var wrapped = reopened.ActiveTextBlock!;
        Require(wrapped.Layout.Lines.Count > 1 && wrapped.Block.WidthMode == TextBlockWidthMode.Fixed,
            "Text in a dragged frame wraps by words");
        Capture(_main, "text-block-wrapped");
        reopened.CommitTextBlock();
        reopened.ToggleTextTool();
        Require(!reopened.IsTextTool, "The Text tool turns off");
    }

    /// <summary>Редактор блока — отдельное окно поверх страницы: ищется среди всех окон по AutomationId.</summary>
    private static async Task<FrameworkElement> FindTextEditorAsync(string description)
    {
        FrameworkElement? found = null;
        for (var i = 0; i < 100 && found == null; i++)
        {
            await _main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            found = PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>()
                .Where(source => source.RootVisual != null)
                .SelectMany(source => Descendants(source.RootVisual))
                .OfType<FrameworkElement>()
                .FirstOrDefault(e => e.IsVisible && System.Windows.Automation.AutomationProperties.GetAutomationId(e) == "TextBlockEditor");
            if (found == null) await Task.Delay(50);
        }
        Require(found != null, description);
        return found!;
    }

    /// <summary>
    /// Набор тем же путём, что и с клавиатуры без IME (событие TextInput у
    /// курсора); результат читается через UI Automation, как его прочтёт диктор.
    /// </summary>
    private static void Type(FrameworkElement editor, string text)
    {
        editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,
            new TextComposition(InputManager.Current, editor, text))
        {
            RoutedEvent = TextCompositionManager.TextInputEvent,
        });
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(editor);
        var value = (System.Windows.Automation.Provider.IValueProvider)peer.GetPattern(
            System.Windows.Automation.Peers.PatternInterface.Value);
        Require(value.Value.EndsWith(text, StringComparison.Ordinal), "Typed text reaches the block: " + text);
    }

    private static async Task InspectReaderToolsAsync()
    {
        _document.CurrentPageNumber = 1;
        foreach (var group in new[] { "Comment", "Edit", "Measure", "Protect" })
        {
            await _main.ViewModel.SelectToolGroupCommand.ExecuteAsync(group);
            await Task.Delay(120);
            Capture(_main, "main-" + group.ToLowerInvariant());
        }
        _main.ViewModel.ActiveToolGroup = MainViewModel.ToolGroup.None;
        _document.StopMeasurement();
        _document.IsFindVisible = true;
        _document.FindQuery = "NexusPDF";
        await _document.RunSearchAsync();
        Capture(_main, "main-search");
        _document.FindQuery = "";
        _document.IsFindVisible = false;

        _document.Document.Session.Apply(new AddOverlayOperation(0, new NoteAnnotationDraft(40, 90, "Заметка для проверки", "NexusPDF")));
        await _document.ToggleCommentsCommand.ExecuteAsync(null);
        await WaitForReaderAsync();
        Capture(_main, "main-comments-panel");
        _document.IsCommentsVisible = false;
        _document.Document.Session.Undo();

        var text = new TextOverlay("Текст для правки", 60, 410, 24, 0xFF000000, 0);
        _document.Document.Session.Apply(new AddOverlayOperation(0, text));
        var box = OverlayGeometry.BoundsOf(text)!.Value;
        Require(_document.SelectObjectAt(_document.Pages[0], box.XPt + box.WidthPt / 2, box.YPt + box.HeightPt / 2),
            "Added text can be selected and its properties inspected");
        await CheckPropertiesAsync("ObjectProperties");
        await WaitForReaderAsync();
        Capture(_main, "main-object-properties");
        _document.SelectedText = "NexusPDF";
        await CheckPropertiesAsync("ObjectProperties");
        _document.ClearObjectSelection();
        Require(_document.StatusText != Loc.Get("UxObjectSelected"), "Clearing an object removes its stale status hint");
        await CheckPropertiesAsync("TextProperties");
        Capture(_main, "main-text-properties");
        _document.ClearTextSelection();
        await CheckPropertiesAsync("PageProperties");
        _document.Document.Session.Undo();

        _main.ViewModel.Panels = _main.ViewModel.Panels with { Tools = true, Properties = false };
        foreach (var group in _main.ViewModel.Tools.Groups) group.IsExpanded = true;
        _main.UpdateLayout();
        var items = Descendants(_main).OfType<ItemsControl>().First(c => ReferenceEquals(c.ItemsSource, _main.ViewModel.Tools.Groups));
        DependencyObject parent = items;
        while (parent is not ScrollViewer) parent = VisualTreeHelper.GetParent(parent);
        var scroll = (ScrollViewer)parent;
        await WaitForReaderAsync();
        var frame = 1;
        for (var offset = 0.0; offset <= scroll.ScrollableHeight + 1; offset += Math.Max(100, scroll.ViewportHeight - 50))
        {
            scroll.ScrollToVerticalOffset(offset);
            await Task.Delay(80);
            Capture(_main, "main-all-tools-" + frame++);
        }
        scroll.ScrollToBottom();
        await Task.Delay(80);
        Capture(_main, "main-all-tools-last");
        foreach (var group in _main.ViewModel.Tools.Groups) group.IsExpanded = false;
        _main.ViewModel.Panels = PanelLayout.Default;

        var fixture = NexusPdf.PdfEngineTests.PdfFixture.WriteFormTrioToTemp("form-fixture.pdf");
        var formPath = Path.Combine(_output, "form-fixture.pdf");
        File.Copy(fixture, formPath);
        Directory.Delete(Path.GetDirectoryName(fixture)!, recursive: true);
        await _main.ViewModel.OpenFilesAsync(new[] { formPath });
        var form = _main.ViewModel.ActiveDocument!;
        await form.DetectFormsAsync();
        await ClickFormsButtonAsync("FormsRailButton");
        Require(form.IsFormMode && _main.ViewModel.IsGroupForms,
            "One click on Forms immediately enables actual AcroForm fields");
        _main.UpdateLayout();
        var done = (Button)_main.FindName("FormsDoneButton");
        var bounds = done.TransformToAncestor(_main).TransformBounds(new Rect(done.RenderSize));
        Require(done.IsVisible && bounds.Right <= _main.ActualWidth && bounds.Left >= 0,
            "The short form hint keeps Done visible within the window");
        await WaitForReaderAsync();
        Capture(_main, "main-forms-one-click");
        await form.FormClickAsync(form.Pages[0], 200, 72, 1);
        foreach (var character in "FORM VALUE") await form.FormCharAsync(character, 1);
        await form.FormKeyAsync(0x09, 1);
        foreach (var character in "NEXT") await form.FormCharAsync(character, 1);
        await form.FormKeyAsync(0x09, 1, shift: true);
        await form.FormKeyAsync(0x23, 1);
        await form.FormCharAsync('!', 1);
        await form.ReleaseFormFieldAsync(1);
        await WaitUntilAsync(() => form.CurrentPageTextInfo == Loc.F("PropFormFields", 3),
            "A form page is described as fields rather than a scan");
        await WaitForReaderAsync();
        Capture(_main, "main-filled-form");
        // PDFium commits the edit buffer when focus leaves the field. Check
        // committed values without interrupting the actual Tab/Shift+Tab flow.
        var typedFields = await form.Document.PrimaryHandle.GetFormFieldValuesAsync(0, CancellationToken.None);
        Results.Add(new { Kind = "form-values", Fields = typedFields.Select(field => new { field.Name, field.Value }).ToArray() });
        Require(typedFields.Any(field => field.Name == "text2" && field.Value == "NEXT"), "Tab moves input to the next PDF field");
        Require(typedFields.Any(field => field.Name == "text1" && field.Value == "FORM VALUE!"), "Shift+Tab returns input to the previous PDF field");
        await ClickFormsButtonAsync("FormsDoneButton");
        Require(!form.IsFormMode && !_main.ViewModel.IsGroupForms && form.IsDirty,
            "Done exits filling and keeps edited field values unsaved until Save");
        await WaitForReaderAsync();
        Capture(_main, "main-forms-done");
        await _main.ViewModel.SaveCommand.ExecuteAsync(null);
        Require(!form.IsDirty, "Saving the filled form clears its unsaved state");
        await using var saved = await _services.Engine.OpenAsync(formPath, null, CancellationToken.None);
        var savedFields = await saved.GetFormFieldValuesAsync(0, CancellationToken.None);
        Require(savedFields.Any(field => field.Name == "text1" && field.Value == "FORM VALUE!") &&
            savedFields.Any(field => field.Name == "text2" && field.Value == "NEXT"),
            "Both filled fields retain their values after exiting and reopening the saved file");
        await form.DetectFormsAsync();
        await form.FillFieldAtAsync(form.Pages[0], 200, 72, 1);
        Require(form.IsFormMode && form.HasActiveFormPage && !_main.ViewModel.IsGroupForms && !form.IsDirty,
            "Clicking a field in the reader enables filling without a toolbar step or false unsaved state");
        await form.EscapeFormAsync(1);
        Require(form.IsFormMode && !form.HasActiveFormPage, "First Escape releases the field without disabling filling");
        await form.EscapeFormAsync(1);
        Require(!form.IsFormMode, "Second Escape exits filling");
        form.IsBusy = true;
        await ClickFormsButtonAsync("FormsRailButton");
        Require(!form.IsFormMode && !_main.ViewModel.IsGroupForms, "A busy document cannot enter filling or show a misleading Forms toolbar");
        form.IsBusy = false;
        _main.ViewModel.ActiveDocument = _document;
    }

    private static async Task ClickFormsButtonAsync(string name)
    {
        var button = (System.Windows.Controls.Primitives.ButtonBase)_main.FindName(name);
        _main.UpdateLayout();
        Require(button.IsVisible && button.IsEnabled, name + " is available as an actual WPF action");
        typeof(System.Windows.Controls.Primitives.ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(button, null);
        await (_main.ViewModel.SelectToolGroupCommand.ExecutionTask ?? Task.CompletedTask);
        await _main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
    }

    private static async Task CheckPropertiesAsync(string expected)
    {
        await _main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
        _main.UpdateLayout();
        foreach (var name in new[] { "ObjectProperties", "TextProperties", "PageProperties" })
        {
            var panel = Descendants(_main).OfType<FrameworkElement>().Single(e => e.Name == name && ReferenceEquals(e.DataContext, _document));
            Require(panel.IsVisible == (name == expected), expected + " displays without overlap from " + name);
        }
    }

    private static async Task WaitForReaderAsync()
    {
        await Task.Delay(250);
        var document = _main.ViewModel.ActiveDocument!;
        var page = document.Pages[Math.Clamp(document.CurrentPageNumber - 1, 0, document.Pages.Count - 1)];
        await WaitUntilAsync(() => page.Image != null && !page.IsRendering, "Reader finishes rendering before visual inspection");
        _main.UpdateLayout();
    }

    private static async Task VerifyPageRenderingAsync()
    {
        _document.GoToPage(1);
        await WaitUntilAsync(() => _document.CurrentPageNumber == 1, "Rendering regression starts on the first visible page");
        await WaitForVisiblePageAsync("Initial page renders automatically");
        Capture(_main, "render-before-edit");
        var original = _document.Pages.ToArray();
        var initialImage = original[0].Image;

        _document.Document.Session.Apply(new AddOverlayOperation(0, new NoteAnnotationDraft(40, 90, "Render regression", "NexusPDF")));
        await WaitForVisiblePageAsync("A note renders automatically after replacing the visible page model");
        Require(!ReferenceEquals(_document.Pages[0].Image, initialImage) &&
            ReferenceEquals(_document.Pages[1], original[1]) && ReferenceEquals(_document.Pages[2], original[2]),
            "Editing refreshes the affected raster and preserves the other page models");
        Capture(_main, "render-after-note");
        _document.Document.Session.Undo();
        await WaitForVisiblePageAsync("Undoing a note restores the page automatically");

        for (var i = 0; i < 3; i++)
        {
            _document.Document.Session.Apply(new RotatePagesOperation(new[] { 0 }, 1));
            await WaitForVisiblePageAsync("Rotation " + (i + 1) + " renders automatically");
            Require(_document.Pages[0].Image!.PixelWidth > _document.Pages[0].Image!.PixelHeight,
                "A rotated page displays the landscape raster");
            _document.UndoCommand.Execute(null);
            await WaitForVisiblePageAsync("Undo rotation " + (i + 1) + " renders automatically");
            _document.RedoCommand.Execute(null);
            await WaitForVisiblePageAsync("Redo rotation " + (i + 1) + " renders automatically");
            _document.UndoCommand.Execute(null);
            await WaitForVisiblePageAsync("Restoring rotation " + (i + 1) + " renders automatically");
        }

        var text = new TextOverlay("AUTOMATIC RENDER", 60, 300, 24, 0xFF000000, 0);
        _document.Document.Session.Apply(new AddOverlayOperation(0, text));
        await WaitForVisiblePageAsync("Adding text renders automatically");
        var draft = (TextOverlay)_document.Document.Session.Model.Pages[0].OverlayList[0];
        _document.Document.Session.Apply(new ReplaceOverlayOperation(0, draft, draft with { Text = "EDITED RENDER", ColorArgb = 0xFFCC0000 }));
        await WaitForVisiblePageAsync("Editing an existing text overlay renders automatically");
        _document.Document.Session.Apply(new RemoveOverlaysOperation(new[] { 0 }));
        await WaitForVisiblePageAsync("Removing overlays renders automatically");
        _document.UndoCommand.Execute(null);
        await WaitForVisiblePageAsync("Undoing overlay removal renders automatically");
        _document.UndoCommand.Execute(null);
        await WaitForVisiblePageAsync("Undoing text replacement renders automatically");
        _document.UndoCommand.Execute(null);
        await WaitForVisiblePageAsync("Removing added text by Undo renders automatically");
        _document.Document.Session.Apply(new DuplicatePagesOperation(new[] { 0 }));
        await WaitForVisiblePageAsync("Duplicating a page keeps the reader rendered");
        _document.UndoCommand.Execute(null);
        await WaitForVisiblePageAsync("Undoing page duplication keeps the reader rendered");
        _document.Document.Session.Apply(new DeletePagesOperation(new[] { 0 }));
        await WaitForVisiblePageAsync("Deleting the current page renders its replacement");
        _document.UndoCommand.Execute(null);
        await WaitForVisiblePageAsync("Undoing deletion renders the restored page");
        _document.Document.Session.Apply(new MovePagesOperation(new[] { 0 }, 3));
        await WaitForVisiblePageAsync("Moving the current page renders the new visible page");
        _document.UndoCommand.Execute(null);
        await WaitForVisiblePageAsync("Undoing a page move renders automatically");
        Require(!_document.IsDirty && _document.PageCount == 3, "Repeated render checks preserve the original document");
        Capture(_main, "render-after-repeated-actions");

        _document.IsOrganizeMode = true;
        await Task.Delay(200);
        _document.Document.Session.Apply(new AddOverlayOperation(1, new NoteAnnotationDraft(40, 90, "Thumbnail regression", "NexusPDF")));
        await WaitUntilAsync(() => _document.Pages[1].ThumbImage != null, "Editing a page in the organizer refreshes its thumbnail automatically");
        _document.UndoCommand.Execute(null);
        await WaitUntilAsync(() => _document.Pages[1].ThumbImage != null, "Undo refreshes the organizer thumbnail automatically");
        _document.IsOrganizeMode = false;
        await WaitForVisiblePageAsync("Returning from the organizer renders the reader automatically");

        var references = _document.Document.Session.Model.Pages.ToArray();
        _document.Document.Session.Apply(new InsertPagesOperation(3,
            Enumerable.Range(0, 297).Select(i => references[i % references.Length]).ToArray()));
        await WaitForVisiblePageAsync("A bulk insertion preserves rendering of reused page models");
        _document.GoToPage(150);
        await WaitUntilAsync(() => _document.CurrentPageNumber == 150, "Virtualized scrolling reaches page 150");
        await WaitForVisiblePageAsync("A recycled card renders page 150 automatically");
        _document.Document.Session.Apply(new AddOverlayOperation(149, new NoteAnnotationDraft(40, 90, "Page 150 edit", "NexusPDF")));
        await WaitForVisiblePageAsync("Editing page 150 renders automatically in a 300-page document");
        _document.UndoCommand.Execute(null);
        await WaitForVisiblePageAsync("Undo renders page 150 automatically");
        _document.GoToPage(1);
        await WaitUntilAsync(() => _document.CurrentPageNumber == 1, "Virtualized scrolling returns to the first page");
        _document.UndoCommand.Execute(null);
        await WaitForVisiblePageAsync("A bulk reset renders reused page models automatically");
        for (var i = 0; i < 8; i++)
        {
            _document.Document.Session.Apply(new RotatePagesOperation(new[] { 0 }, 1));
            _document.UndoCommand.Execute(null);
        }
        await WaitForVisiblePageAsync("A burst of sixteen page changes finishes rendering the final state");
        var zoom = _document.Zoom;
        _document.SetZoom(zoom * 1.2);
        _document.SetZoom(zoom);
        await WaitForVisiblePageAsync("Rapid zoom changes finish rendering the final scale");
        Require(!_document.IsDirty && _document.PageCount == 3, "Bulk, recycled and rapid render checks preserve all original pages");
        Capture(_main, "render-after-300-pages-and-rapid-actions");

        await VerifyRecycledThumbnailAsync();

        // All three calls happen on the same UI turn, before an awaited render
        // can resume: an already-cancelled request must not block a new one.
        var page = new PageViewModel(_document, 0, _document.Document.Session.Model.Pages[0], _document.Document.GetLogicalPageSize(0));
        _document.Cache.RemoveSource(page.PageRef.SourceId);
        page.Attach(_document.DpiScale);
        page.Detach();
        page.Attach(_document.DpiScale);
        await WaitUntilAsync(() => page.Image != null && !page.IsRendering,
            "A page detached and immediately reattached finishes rendering");
        page.Attach(_document.DpiScale);
        page.Detach();
        Require(page.Image != null, "Detaching one presenter keeps the page image used by another presenter");
        page.Detach();
        Require(page.Image == null && !page.IsRendering, "Detaching the last presenter releases its image and rendering state");
        page.CancelAll();
    }

    private static async Task VerifyRecycledThumbnailAsync()
    {
        var pages = Enumerable.Range(0, 3).Select(i => new PageViewModel(_document, i,
            _document.Document.Session.Model.Pages[i], _document.Document.GetLogicalPageSize(i))).ToArray();
        _document.Cache.RemoveSource(pages[0].PageRef.SourceId);
        var host = new Border { DataContext = pages[0] };
        ViewBehaviors.SetRenderThumbnail(host, true);
        var window = new Window { Owner = _main, Width = 180, Height = 240, Content = host, ShowInTaskbar = false };
        try
        {
            window.Show();
            await WaitUntilAsync(() => pages[0].ThumbImage != null, "A thumbnail-only presenter renders on loading");
            host.DataContext = pages[1];
            await WaitUntilAsync(() => pages[1].ThumbImage != null, "A loaded thumbnail-only presenter renders its replacement model");
            Require(pages[1].Image == null, "Thumbnail replacement does not depend on a full-page render request");
            ViewBehaviors.SetRenderThumbnail(host, false);
            host.DataContext = pages[2];
            await Task.Delay(100);
            Require(pages[2].ThumbImage == null, "Disabling thumbnail rendering removes the context listener");
            ViewBehaviors.SetRenderThumbnail(host, true);
            await WaitUntilAsync(() => pages[2].ThumbImage != null, "Enabling thumbnail rendering on an already-loaded presenter renders immediately");
        }
        finally
        {
            window.Close();
            foreach (var page in pages) page.CancelAll();
        }
    }

    private static async Task WaitForVisiblePageAsync(string description)
    {
        await _main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        _main.UpdateLayout();
        var page = _document.Pages[Math.Clamp(_document.CurrentPageNumber - 1, 0, _document.Pages.Count - 1)];
        // Never call EnsureImage here: doing so hid a missing visual-lifecycle
        // request in the earlier audit while actual users saw "Rendering".
        for (var i = 0; i < 100 && (page.Image == null || page.IsRendering || page.ThumbImage == null); i++)
            await Task.Delay(50);
        if (page.Image == null || page.IsRendering || page.ThumbImage == null)
        {
            Results.Add(new { Kind = "render-state", Description = description, page.LogicalIndex, page.IsRendering,
                HasImage = page.Image != null, HasThumbnail = page.ThumbImage != null,
                IsAttached = typeof(PageViewModel).GetField("_shown", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page),
                VisibleCards = Descendants(_main).OfType<FrameworkElement>().Count(e =>
                    e.IsVisible && ViewBehaviors.GetRenderFullPage(e) && ReferenceEquals(e.DataContext, page)) });
            Capture(_main, "render-regression-failure");
        }
        Require(page.Image != null && !page.IsRendering && page.ThumbImage != null, description);
        var bitmap = page.Image!;
        var expected = await _document.Document.RenderLogicalPageAsync(page.LogicalIndex, bitmap.PixelWidth, bitmap.PixelHeight, CancellationToken.None);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        Require(expected.Stride == bitmap.PixelWidth * 4 && pixels.AsSpan().SequenceEqual(expected.Bgra),
            description + ": the displayed pixels match the current PDF contents");
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
