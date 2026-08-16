using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NexusPdf.Office;

/// <param name="TargetPath">Куда записан PDF.</param>
/// <param name="Application">Чем сделано: Word, Excel или PowerPoint.</param>
/// <param name="KeepsLinks">Ссылки документа перенесены живыми.</param>
/// <param name="KeepsOutline">Заголовки стали оглавлением PDF.</param>
/// <param name="KeepsTags">Структура помечена тегами (доступность, копирование текста по порядку).</param>
public sealed record OfficeConversionResult(
    string TargetPath, string Application, bool KeepsLinks, bool KeepsOutline, bool KeepsTags);

/// <summary>
/// Документы Office → PDF.
///
/// Способ выбран сознательно: <c>ExportAsFixedFormat</c>, а НЕ печать в
/// PDF-принтер. Печать выдаёт плоскую картинку страницы — в ней мертвы
/// гиперссылки, нет оглавления по заголовкам, нет тегов структуры, а текст
/// копируется в произвольном порядке. Экспорт сохраняет всё это, потому что
/// раскладку и разметку считает сам Word (Excel, PowerPoint), а не драйвер
/// принтера.
///
/// Макросы при открытии ОТКЛЮЧАЮТСЯ принудительно: превращать чужой .docm в
/// PDF — не повод исполнять его код.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OfficeToPdfConverter
{
    /// <summary>Что умеем принимать.</summary>
    public static IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".doc", ".docx", ".docm", ".rtf", ".odt",
            ".xls", ".xlsx", ".xlsm", ".ods", ".csv",
            ".ppt", ".pptx", ".pptm", ".odp",
        };

    public static bool IsOfficeFile(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path));

    /// <summary>Есть ли нужное приложение Office для этого файла.</summary>
    public static bool CanConvert(string path) => AppIdFor(path) is { } id && IsInstalled(id);

    /// <summary>Установлен ли Word/Excel/PowerPoint (по регистрации COM-класса).</summary>
    public static bool IsInstalled(string progId) => Type.GetTypeFromProgID(progId) != null;

    /// <summary>Почему не получится — словами, а не пустым отказом.</summary>
    public static string UnavailableReason(string path)
    {
        var id = AppIdFor(path);
        if (id == null) return "Этот формат не относится к документам Office.";
        var name = id switch
        {
            "Word.Application" => "Microsoft Word",
            "Excel.Application" => "Microsoft Excel",
            _ => "Microsoft PowerPoint",
        };
        return $"Для преобразования нужен установленный {name}: раскладку страниц считает он сам.";
    }

    private static string? AppIdFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".doc" or ".docx" or ".docm" or ".rtf" or ".odt" => "Word.Application",
        ".xls" or ".xlsx" or ".xlsm" or ".ods" or ".csv" => "Excel.Application",
        ".ppt" or ".pptx" or ".pptm" or ".odp" => "PowerPoint.Application",
        _ => null,
    };

    public Task<OfficeConversionResult> ConvertAsync(
        string sourcePath, string targetPath, CancellationToken ct) =>
        Task.Run(() => Convert(sourcePath, targetPath, ct), ct);

    public OfficeConversionResult Convert(string sourcePath, string targetPath, CancellationToken ct)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Файл для преобразования не найден.", sourcePath);
        var appId = AppIdFor(sourcePath)
            ?? throw new NotSupportedException(UnavailableReason(sourcePath));
        if (!IsInstalled(appId))
            throw new InvalidOperationException(UnavailableReason(sourcePath));

        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        return appId switch
        {
            "Word.Application" => ConvertWord(sourcePath, targetPath),
            "Excel.Application" => ConvertExcel(sourcePath, targetPath),
            _ => ConvertPowerPoint(sourcePath, targetPath),
        };
    }

    // ----- Word -----

    private static OfficeConversionResult ConvertWord(string source, string target)
    {
        dynamic? app = null;
        dynamic? doc = null;
        try
        {
            app = Create("Word.Application");
            app.Visible = false;
            app.DisplayAlerts = 0;              // wdAlertsNone
            app.AutomationSecurity = 3;         // макросы отключены принудительно

            doc = app.Documents.Open(
                FileName: source,
                ConfirmConversions: false,
                ReadOnly: true,
                AddToRecentFiles: false,
                PasswordDocument: "",
                Revert: false,
                Visible: false);

            doc.ExportAsFixedFormat(
                OutputFileName: target,
                ExportFormat: 17,               // wdExportFormatPDF
                OpenAfterExport: false,
                OptimizeFor: 0,                 // wdExportOptimizeForPrint — полное качество
                Range: 0,                       // весь документ
                Item: 0,                        // содержимое, без пометок исправлений
                IncludeDocProps: true,
                KeepIRM: true,
                CreateBookmarks: 1,             // заголовки → оглавление PDF
                DocStructureTags: true,         // теги структуры: доступность и порядок текста
                BitmapMissingFonts: true,
                UseISO19005_1: false);          // PDF/A выключен: он убивает прозрачность и часть ссылок

            return new OfficeConversionResult(target, "Word",
                KeepsLinks: true, KeepsOutline: true, KeepsTags: true);
        }
        finally
        {
            CloseDocument((object?)doc, static d => d.Close(SaveChanges: 0));
            Quit((object?)app);
        }
    }

    // ----- Excel -----

    private static OfficeConversionResult ConvertExcel(string source, string target)
    {
        dynamic? app = null;
        dynamic? book = null;
        try
        {
            app = Create("Excel.Application");
            app.Visible = false;
            app.DisplayAlerts = false;
            app.AutomationSecurity = 3;

            book = app.Workbooks.Open(
                Filename: source,
                UpdateLinks: 0,
                ReadOnly: true,
                AddToMru: false);

            book.ExportAsFixedFormat(
                Type: 0,                        // xlTypePDF
                Filename: target,
                Quality: 0,                     // xlQualityStandard
                IncludeDocProperties: true,
                IgnorePrintAreas: false,        // области печати книги уважаются
                OpenAfterPublish: false);

            // Excel переносит гиперссылки, но оглавления по заголовкам у него
            // нет — обещать его было бы неправдой.
            return new OfficeConversionResult(target, "Excel",
                KeepsLinks: true, KeepsOutline: false, KeepsTags: true);
        }
        finally
        {
            CloseDocument((object?)book, static b => b.Close(SaveChanges: false));
            Quit((object?)app);
        }
    }

    // ----- PowerPoint -----

    private static OfficeConversionResult ConvertPowerPoint(string source, string target)
    {
        dynamic? app = null;
        dynamic? presentation = null;
        try
        {
            app = Create("PowerPoint.Application");
            app.DisplayAlerts = 1;              // ppAlertsNone
            app.AutomationSecurity = 3;

            // PowerPoint не умеет открывать презентацию невидимо во всех
            // версиях: WithWindow=false — самое близкое, что он допускает.
            presentation = app.Presentations.Open(
                FileName: source, ReadOnly: -1, Untitled: 0, WithWindow: 0);

            presentation.ExportAsFixedFormat(
                Path: target,
                FixedFormatType: 2,             // ppFixedFormatTypePDF
                Intent: 1,                      // ppFixedFormatIntentPrint
                FrameSlides: 0,
                HandoutOrder: 1,
                OutputType: 0,                  // слайды целиком
                PrintHiddenSlides: 0,
                PrintRange: Type.Missing,
                RangeType: 1,
                SlideShowName: "",
                IncludeDocProperties: true,
                KeepIRMSettings: true,
                DocStructureTags: true,
                BitmapMissingFonts: true,
                UseISO19005_1: false);

            return new OfficeConversionResult(target, "PowerPoint",
                KeepsLinks: true, KeepsOutline: false, KeepsTags: true);
        }
        finally
        {
            CloseDocument((object?)presentation, static p => p.Close());
            Quit((object?)app);
        }
    }

    // ----- Общее -----

    private static dynamic Create(string progId)
    {
        var type = Type.GetTypeFromProgID(progId)
            ?? throw new InvalidOperationException($"{progId} не зарегистрирован.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Не удалось запустить {progId}.");
    }

    /// <summary>
    /// Закрытие документа. Ошибка здесь не должна затирать настоящую причину
    /// сбоя преобразования, поэтому глушится.
    /// </summary>
    private static void CloseDocument(object? document, Action<dynamic> close)
    {
        if (document == null) return;
        try { close(document); } catch (Exception) { /* документ мог не открыться */ }
        Release(document);
    }

    /// <summary>
    /// Приложение обязано закрыться ВСЕГДА: невидимый Word, переживший сбой,
    /// остаётся в памяти навсегда и потом мешает пользователю открыть файл.
    /// </summary>
    private static void Quit(object? app)
    {
        if (app == null) return;
        try { ((dynamic)app).Quit(); } catch (Exception) { /* уже закрыт */ }
        Release(app);
    }

    private static void Release(object instance)
    {
        try
        {
            if (Marshal.IsComObject(instance))
                Marshal.FinalReleaseComObject(instance);
        }
        catch (Exception)
        {
            // Освобождение COM — уборка, а не операция: молчим.
        }
    }
}
