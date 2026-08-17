namespace NexusPdf.Pdf.Abstractions;

/// <summary>
/// Шрифты, которыми можно писать текст в PDF.
///
/// Список намеренно закрытый, а не «все шрифты системы». Шрифт для PDF обязан
/// встроиться подмножеством и обязан содержать кириллицу — иначе документ
/// откроется у другого человека пустыми квадратами. Здесь перечислены только
/// те гарнитуры, что входят в саму Windows и оба условия выполняют; всё
/// остальное честнее не предлагать, чем предложить и подвести.
///
/// Начертания заданы отдельными файлами, а не синтетическим наклоном:
/// настоящий курсив рисуется по-другому, а не наклонённым прямым.
/// </summary>
public static class PdfFontCatalog
{
    /// <summary>Гарнитура: имя для человека и файлы четырёх начертаний.</summary>
    /// <param name="Family">Как называется в списке.</param>
    /// <param name="Regular">Обычное начертание.</param>
    /// <param name="Bold">Полужирное.</param>
    /// <param name="Italic">Курсив.</param>
    /// <param name="BoldItalic">Полужирный курсив.</param>
    public sealed record FontEntry(
        string Family, string Regular, string Bold, string Italic, string BoldItalic)
    {
        /// <summary>Файл нужного начертания. Недостающее заменяется ближайшим.</summary>
        public string File(bool bold, bool italic) => (bold, italic) switch
        {
            (true, true) => BoldItalic,
            (true, false) => Bold,
            (false, true) => Italic,
            _ => Regular,
        };
    }

    /// <summary>Гарнитура по умолчанию — ею пишется текст, если выбора не сделали.</summary>
    public const string DefaultFamily = "Segoe UI";

    private static readonly FontEntry[] All =
    [
        new("Segoe UI", "segoeui.ttf", "segoeuib.ttf", "segoeuii.ttf", "segoeuiz.ttf"),
        new("Arial", "arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf"),
        new("Times New Roman", "times.ttf", "timesbd.ttf", "timesi.ttf", "timesbi.ttf"),
        new("Georgia", "georgia.ttf", "georgiab.ttf", "georgiai.ttf", "georgiaz.ttf"),
        new("Verdana", "verdana.ttf", "verdanab.ttf", "verdanai.ttf", "verdanaz.ttf"),
        new("Tahoma", "tahoma.ttf", "tahomabd.ttf", "tahoma.ttf", "tahomabd.ttf"),
        new("Calibri", "calibri.ttf", "calibrib.ttf", "calibrii.ttf", "calibriz.ttf"),
        new("Courier New", "cour.ttf", "courbd.ttf", "couri.ttf", "courbi.ttf"),
    ];

    /// <summary>Каталог шрифтов системы.</summary>
    public static string FontsDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    /// <summary>
    /// Гарнитуры, которые в этой системе действительно есть. Проверяется файл
    /// обычного начертания: если нет даже его, предлагать гарнитуру незачем.
    /// </summary>
    public static IReadOnlyList<string> AvailableFamilies()
    {
        var dir = FontsDirectory;
        var found = new List<string>();
        foreach (var entry in All)
        {
            if (File.Exists(Path.Combine(dir, entry.Regular)))
                found.Add(entry.Family);
        }
        return found;
    }

    /// <summary>
    /// Полный путь к файлу нужного начертания или null, если такой гарнитуры
    /// нет. Отсутствующий файл начертания откатывается на обычное: у Tahoma,
    /// например, курсива в поставке Windows просто нет.
    /// </summary>
    public static string? ResolvePath(string family, bool bold, bool italic)
    {
        var entry = Array.Find(All, f =>
            string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
            return null;

        var dir = FontsDirectory;
        foreach (var candidate in new[] { entry.File(bold, italic), entry.File(bold, false), entry.Regular })
        {
            var path = Path.Combine(dir, candidate);
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    /// <summary>
    /// Путь к шрифту по умолчанию: сначала заданная гарнитура, затем любая из
    /// каталога. Возвращает null, только если в системе нет ни одной из них.
    /// </summary>
    public static string? ResolveDefaultPath(bool bold = false, bool italic = false)
    {
        var preferred = ResolvePath(DefaultFamily, bold, italic);
        if (preferred != null)
            return preferred;

        foreach (var entry in All)
        {
            var path = ResolvePath(entry.Family, bold, italic);
            if (path != null)
                return path;
        }
        return null;
    }
}
