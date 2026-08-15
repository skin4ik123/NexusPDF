namespace NexusPdf.Printing;

/// <summary>Один комментарий для сводки.</summary>
public sealed record SummaryComment(
    int Number,
    int PageNumber,
    string TypeName,
    string Author,
    string Date,
    string Text);

/// <summary>Строка сводки с координатами на листе.</summary>
public sealed record SummaryLine(string Text, double XPt, double YPt, double FontSizePt, bool IsBold);

/// <summary>Готовая страница сводки.</summary>
public sealed record SummaryPage(IReadOnlyList<SummaryLine> Lines);

/// <summary>Куда попадает сводка комментариев.</summary>
public enum CommentSummaryPlacement
{
    /// <summary>Отдельным файлом: документ не меняется вовсе.</summary>
    SeparateFile,

    /// <summary>Страницами после всего документа.</summary>
    AfterDocument,
}

/// <summary>Настройки сводки.</summary>
public sealed record CommentSummarySettings
{
    public CommentSummaryPlacement Placement { get; init; } = CommentSummaryPlacement.SeparateFile;

    /// <summary>Фильтр по автору; пустая строка — все.</summary>
    public string AuthorFilter { get; init; } = "";

    /// <summary>Диапазон страниц; null — все.</summary>
    public IReadOnlyList<int>? PageFilter { get; init; }

    public double FontSizePt { get; init; } = 10;
    public double TitleSizePt { get; init; } = 14;
    public MarginsPt MarginsPt { get; init; } = new(56.7, 56.7, 56.7, 56.7); // 20 мм
}

/// <summary>
/// Раскладка сводки комментариев по страницам. Чистый расчёт без движка:
/// перенос строк и разбиение на страницы — то место, где текст либо
/// обрезается, либо уезжает за поле, и это проверяется тестами.
/// </summary>
public static class CommentSummaryLayout
{
    /// <summary>
    /// Средняя ширина символа относительно кегля. Точных метрик шрифта здесь
    /// нет намеренно: сводка — служебная страница, и запас в переносе важнее
    /// плотности набора. Заниженная оценка привела бы к тексту за полем.
    /// </summary>
    private const double AverageCharWidthRatio = 0.55;

    public static IReadOnlyList<SummaryPage> Build(
        IReadOnlyList<SummaryComment> comments,
        SizePt pageSize,
        CommentSummarySettings settings,
        string documentTitle)
    {
        var area = RectPt.FromSize(pageSize).Deflate(settings.MarginsPt);
        var pages = new List<SummaryPage>();
        var lines = new List<SummaryLine>();

        var lineHeight = settings.FontSizePt * 1.45;
        var y = area.YPt;

        void NewPage()
        {
            if (lines.Count > 0) pages.Add(new SummaryPage(lines.ToList()));
            lines.Clear();
            y = area.YPt;
        }

        // Заголовок только на первой странице: повторять его на каждой —
        // тратить место, которого в сводке и так мало.
        lines.Add(new SummaryLine($"Комментарии: {documentTitle}",
            area.XPt, y, settings.TitleSizePt, true));
        y += settings.TitleSizePt * 1.8;

        if (comments.Count == 0)
        {
            lines.Add(new SummaryLine("В документе нет комментариев.",
                area.XPt, y, settings.FontSizePt, false));
            pages.Add(new SummaryPage(lines));
            return pages;
        }

        foreach (var comment in comments)
        {
            var header = $"{comment.Number}. Стр. {comment.PageNumber} · {comment.TypeName}";
            var meta = string.Join(" · ", new[] { comment.Author, comment.Date }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

            var body = Wrap(comment.Text, area.WidthPt, settings.FontSizePt);
            // Блок комментария не разрывается между страницами, если целиком
            // помещается: разорванный пополам комментарий читать неудобно.
            var blockHeight = lineHeight * (1 + (meta.Length > 0 ? 1 : 0) + body.Count) + lineHeight * 0.6;
            if (y + blockHeight > area.BottomPt && lines.Count > 1)
                NewPage();

            lines.Add(new SummaryLine(header, area.XPt, y, settings.FontSizePt, true));
            y += lineHeight;

            if (meta.Length > 0)
            {
                lines.Add(new SummaryLine(meta, area.XPt, y, settings.FontSizePt * 0.9, false));
                y += lineHeight;
            }

            foreach (var line in body)
            {
                if (y + lineHeight > area.BottomPt) NewPage();
                lines.Add(new SummaryLine(line, area.XPt + settings.FontSizePt, y, settings.FontSizePt, false));
                y += lineHeight;
            }

            y += lineHeight * 0.6; // промежуток между комментариями
        }

        if (lines.Count > 0) pages.Add(new SummaryPage(lines));
        return pages;
    }

    /// <summary>Перенос по словам; слишком длинное слово рвётся, а не уезжает за поле.</summary>
    public static IReadOnlyList<string> Wrap(string text, double widthPt, double fontSizePt)
    {
        if (string.IsNullOrWhiteSpace(text)) return new[] { "(без текста)" };

        var maxChars = Math.Max(8, (int)(widthPt / (fontSizePt * AverageCharWidthRatio)));
        var result = new List<string>();

        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            var current = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var piece = word;
                while (piece.Length > maxChars)
                {
                    // Слово длиннее строки: режем, иначе оно уйдёт за поле.
                    if (current.Length > 0) { result.Add(current); current = ""; }
                    result.Add(piece[..maxChars]);
                    piece = piece[maxChars..];
                }

                if (current.Length == 0) current = piece;
                else if (current.Length + 1 + piece.Length <= maxChars) current += " " + piece;
                else { result.Add(current); current = piece; }
            }
            result.Add(current);
        }

        return result.Count == 0 ? new[] { "(без текста)" } : result;
    }

    /// <summary>Человеческое имя типа аннотации по подтипу PDF.</summary>
    public static string DescribeSubtype(int subtype) => subtype switch
    {
        1 => "Заметка",
        2 => "Ссылка",
        3 => "Текст",
        4 => "Линия",
        5 => "Прямоугольник",
        6 => "Овал",
        7 => "Многоугольник",
        8 => "Ломаная",
        9 => "Выделение",
        10 => "Подчёркивание",
        11 => "Волнистое подчёркивание",
        12 => "Зачёркивание",
        13 => "Штамп",
        14 => "Знак вставки",
        15 => "Рисунок",
        16 => "Всплывающее окно",
        17 => "Вложение",
        20 => "Виджет формы",
        _ => "Аннотация",
    };
}
