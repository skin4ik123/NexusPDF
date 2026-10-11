using System.Diagnostics;
using System.IO;
using System.Reflection;
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

    [STAThread]
    private static int Main(string[] args)
    {
        _output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/ui-audit");
        Directory.CreateDirectory(_output);
        var app = new NexusPdf.App.Desktop.App();
        app.InitializeComponent();
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
        Dispatcher.Run();
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
            try { await InspectDialogAsync(type); }
            catch (Exception e)
            {
                var cause = e is TargetInvocationException { InnerException: not null } tie ? tie.InnerException : e;
                Results.Add(new { Kind = "dialog-error", Window = type.Name, Error = cause!.ToString() });
                Console.WriteLine("DIALOG ERROR " + type.Name + ": " + cause.Message);
            }
        }
        _editor.Dispose();
        await _main.ViewModel.TryCloseAllAsync();
        _main.Close();
        await _services.DisposeAsync();
        Console.WriteLine("UI audit completed; " + Results.Count + " reports, " + BindingErrors.Count + " binding errors.");
    }

    private static async Task InspectDialogAsync(Type type)
    {
        // Public factories configure the real window; close it after capturing.
        var factory = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.DeclaringType == type && !m.IsSpecialName)
            .OrderByDescending(m => m.Name is "Show" or "Run" or "ShowAsync" or "Choose" or "Edit" or "Pick" or "Configure")
            .FirstOrDefault();
        if (factory == null) throw new InvalidOperationException("No public dialog factory: " + type.Name);
        var parameters = factory.GetParameters().Select(Argument).ToArray();
        var captured = false;
        var attempts = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            if (System.Windows.Application.Current.Windows.OfType<Window>()
                .FirstOrDefault(w => w.GetType() == type && w.IsVisible) is { } window)
            {
                timer.Stop();
                try { Capture(window, type.Name); captured = true; }
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
            if (factory.Invoke(null, parameters) is Task pending) await pending;
            if (!captured) throw new InvalidOperationException("Factory returned without a visible window");
        }
        finally { timer.Stop(); }
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
        if (type == typeof(IReadOnlyList<DocumentViewModel>)) return new[] { _document };
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

    private sealed class BindingListener : TraceListener
    {
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) BindingErrors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
