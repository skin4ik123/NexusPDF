using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using NexusPdf.Pdf.Abstractions;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
// Ячейка таблицы Word и ячейка распознанной таблицы — разные вещи с одним
// именем, поэтому первая зовётся здесь полным именем.
using WordTableCell = DocumentFormat.OpenXml.Wordprocessing.TableCell;

namespace NexusPdf.Export;

/// <param name="KeepLinks">Ссылки PDF — настоящими гиперссылками Word.</param>
/// <param name="KeepImages">Переносить картинки страниц.</param>
/// <param name="KeepComments">Аннотации PDF — примечаниями Word.</param>
/// <param name="Encode">Чем сжимать картинки. По умолчанию — встроенный PNG без потерь.</param>
public sealed record WordExportOptions(
    bool KeepLinks = true,
    bool KeepImages = true,
    bool KeepComments = true,
    EncodeImage? Encode = null);

/// <summary>Что получилось — для честного отчёта пользователю.</summary>
/// <param name="ScannedPages">Страниц без текстового слоя — сканов.</param>
/// <param name="RecognizedPages">Из них распознано на месте.</param>
public sealed record WordExportSummary(
    int Pages, int Paragraphs, int Tables, int Images, int Links, int Comments,
    int ScannedPages = 0, int RecognizedPages = 0);

/// <summary>
/// Документ PDF → документ Word.
///
/// Задача честнее, чем кажется: в PDF нет ни абзацев, ни таблиц, ни списков —
/// только буквы с координатами. Всё это восстанавливается, и результат всегда
/// компромисс. Поэтому здесь важно не «попиксельно как в PDF», а «пригодно для
/// правки»: строки собираются в абзацы, таблицы становятся таблицами Word,
/// ссылки — настоящими гиперссылками, аннотации — примечаниями. Иначе получился
/// бы документ, который выглядит похоже, но рассыпается от первой же правки.
///
/// Страницы пишутся по одной и тут же освобождаются: страница-скан занимает в
/// памяти десятки мегабайт, и держать их все разом нельзя.
/// </summary>
public sealed class DocxExporter : IDisposable
{
    private const double PointsToTwips = 20.0;      // твип = 1/20 пункта
    private const double PointsToEmu = 12700.0;     // EMU = 1/914400 дюйма

    private readonly WordprocessingDocument _document;
    private readonly MainDocumentPart _main;

    /// <summary>
    /// Тот же документ, что лежит в <see cref="MainDocumentPart.Document"/>.
    /// Держится отдельным полем, потому что у части это свойство объявлено
    /// как допускающее null: мы его сами и присваиваем в конструкторе, а на
    /// сохранении пришлось бы либо разыменовывать вслепую, либо проверять
    /// на null то, чего не бывает.
    /// </summary>
    private readonly Document _mainDocument;
    private readonly Body _body;
    private readonly WordExportOptions _options;
    private readonly EncodeImage _encode;

    private Comments? _comments;
    private int _commentId;
    private int _bookmarkId;
    private uint _drawingId = 1;
    private PdfPageDescriptor? _sectionSize;
    private (double Left, double Top, double Right, double Bottom) _sectionMargins;
    private bool _anyContent;

    public int Pages { get; private set; }
    public int Paragraphs { get; private set; }
    public int Tables { get; private set; }
    public int Images { get; private set; }
    public int Links { get; private set; }
    public int CommentCount { get; private set; }

    public DocxExporter(string path, WordExportOptions? options = null)
    {
        _options = options ?? new WordExportOptions();
        _encode = _options.Encode ?? PortablePng.Encode;

        _document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        _main = _document.AddMainDocumentPart();
        _body = new Body();
        _mainDocument = new Document(_body);
        _main.Document = _mainDocument;
        AddStyles();
    }

    public WordExportSummary Summary =>
        new(Pages, Paragraphs, Tables, Images, Links, CommentCount);

    /// <param name="pageLinks">Ссылки страницы (в координатах PDF).</param>
    /// <param name="notes">Аннотации страницы; без рамки примечание привязать не к чему.</param>
    /// <param name="images">Картинки страницы.</param>
    /// <param name="pageCount">Сколько страниц всего — для якорей внутренних ссылок.</param>
    public void AddPage(
        PageLayout layout,
        IReadOnlyList<PdfPageLink> pageLinks,
        IReadOnlyList<PdfAnnotationInfo> notes,
        IReadOnlyList<PdfPageImage> images,
        int pageCount)
    {
        var size = new PdfPageDescriptor(layout.WidthPt, layout.HeightPt);
        var margins = MarginsOf(layout);
        var breakBefore = false;

        if (_anyContent)
        {
            // Смена размера или ориентации — это новый раздел Word; иначе
            // альбомная страница отчёта стала бы книжной.
            if (Math.Abs(size.WidthPoints - _sectionSize!.WidthPoints) > 1 ||
                Math.Abs(size.HeightPoints - _sectionSize.HeightPoints) > 1)
                CloseSection();
            else
                breakBefore = true;
        }
        _sectionSize = size;
        if (!_anyContent) _sectionMargins = margins;

        // Якорь страницы: на него ведут внутренние ссылки документа.
        //
        // Он же несёт признак «начать с новой страницы». Отдельный абзац с
        // разрывом не годится: он сам занимает строку, и когда предыдущая
        // страница заполнена до низа, абзац переносится на следующую, а разрыв
        // гонит содержимое на третью — между разделами чек-листа RLM так
        // появлялись чистые листы.
        // Пустой абзац, которым закрывается таблица, перед разрывом не нужен:
        // он остаётся висеть внизу страницы и, если места уже нет, уезжает на
        // следующую — рождая чистый лист. Разделять две таблицы ему тут больше
        // нечего, дальше и так новая страница.
        if (breakBefore && _body.LastChild is Paragraph blank &&
            !blank.HasChildren)
            _body.RemoveChild(blank);

        var id = (++_bookmarkId).ToString();
        var anchor = new Paragraph(
            new BookmarkStart { Id = id, Name = PageAnchor(layout.PageIndex) },
            new BookmarkEnd { Id = id });
        if (breakBefore)
            anchor.PrependChild(new ParagraphProperties(new PageBreakBefore()));
        _body.AppendChild(anchor);

        foreach (var block in Order(layout, images))
        {
            if (block.Table is { } table) WriteTable(table, layout, pageLinks, notes, pageCount);
            else if (block.Images is { } row) WriteImageRow(row);
            else if (block.Paragraph is { } paragraph)
                WriteParagraph(paragraph, layout, pageLinks, notes, pageCount);
        }

        Pages++;
        _anyContent = true;
    }

    /// <summary>Всё содержимое страницы сверху вниз: таблицы, картинки и абзацы вперемешку.</summary>
    private static IEnumerable<(ExtractedTable? Table, IReadOnlyList<PdfPageImage>? Images, TextParagraph? Paragraph)> Order(
        PageLayout layout, IReadOnlyList<PdfPageImage> images)
    {
        var blocks = new List<(double Top, ExtractedTable? Table, IReadOnlyList<PdfPageImage>? Images, TextParagraph? Paragraph)>();
        blocks.AddRange(layout.Tables.Select(t =>
            (t.Bounds.Top, (ExtractedTable?)t, (IReadOnlyList<PdfPageImage>?)null, (TextParagraph?)null)));
        blocks.AddRange(ImageRows(images).Select(r =>
            (r.Max(i => i.RectPt.Top), (ExtractedTable?)null, (IReadOnlyList<PdfPageImage>?)r, (TextParagraph?)null)));
        blocks.AddRange(ParagraphBuilder.Build(layout.Lines).Select(p =>
            (p.Top, (ExtractedTable?)null, (IReadOnlyList<PdfPageImage>?)null, (TextParagraph?)p)));

        return blocks.OrderByDescending(b => b.Top).Select(b => (b.Table, b.Images, b.Paragraph));
    }

    /// <summary>
    /// Картинки, стоящие в один ряд, остаются рядом.
    ///
    /// Каждая в своём абзаце — и фотоотчёт из шести снимков «два в ряд»
    /// вытягивается в столбик на три страницы. Ряд определяется перекрытием по
    /// высоте, порядок внутри ряда — слева направо.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<PdfPageImage>> ImageRows(IReadOnlyList<PdfPageImage> images)
    {
        var rows = new List<List<PdfPageImage>>();
        foreach (var image in images.OrderByDescending(i => i.RectPt.Top).ThenBy(i => i.RectPt.Left))
        {
            var row = rows.FirstOrDefault(r =>
            {
                var top = r.Max(i => i.RectPt.Top);
                var bottom = r.Min(i => i.RectPt.Bottom);
                var overlap = Math.Min(top, image.RectPt.Top) - Math.Max(bottom, image.RectPt.Bottom);
                var smallest = Math.Min(top - bottom, image.RectPt.Top - image.RectPt.Bottom);
                return smallest > 0 && overlap >= smallest * 0.5;
            });
            if (row == null) rows.Add(new List<PdfPageImage> { image });
            else row.Add(image);
        }
        return rows
            .Select(r => (IReadOnlyList<PdfPageImage>)r.OrderBy(i => i.RectPt.Left).ToList())
            .ToList();
    }

    public void Finish()
    {
        _body.AppendChild(SectionProperties());
        _mainDocument.Save();
        if (_comments != null) _comments.Save();
    }

    public void Dispose() => _document.Dispose();

    // ----- абзацы -----

    private void WriteParagraph(
        TextParagraph paragraph, PageLayout layout,
        IReadOnlyList<PdfPageLink> links, IReadOnlyList<PdfAnnotationInfo> notes, int pageCount)
    {
        var element = new Paragraph();
        var properties = new ParagraphProperties(
            new SpacingBetweenLines { After = "0", Before = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto });

        if (paragraph.Alignment != ParagraphAlignment.Left)
        {
            properties.AppendChild(new Justification
            {
                Val = paragraph.Alignment == ParagraphAlignment.Center
                    ? JustificationValues.Center
                    : JustificationValues.Right,
            });
        }
        if (paragraph.IndentPt > 0)
            properties.AppendChild(new Indentation { Left = Twips(paragraph.IndentPt) });
        if (paragraph.IsHeading)
            properties.AppendChild(new ParagraphStyleId { Val = "Heading2" });

        element.AppendChild(properties);
        ApplyMeasuredLeading(element, paragraph.Lines);
        AppendRuns(element, paragraph.Lines.SelectMany(l => l.Words).ToList(), links, pageCount);
        AttachComments(element, notes, paragraph.Left, paragraph.Top, paragraph.Right, paragraph.Bottom);

        _body.AppendChild(element);
        Paragraphs++;
    }

    /// <summary>
    /// Слова → «пробеги» Word. Соседние слова с одинаковым начертанием
    /// объединяются: пробег на каждое слово раздувает файл и мешает править
    /// текст в Word.
    /// </summary>
    private void AppendRuns(
        OpenXmlElement target, IReadOnlyList<PdfTextWord> words,
        IReadOnlyList<PdfPageLink> links, int pageCount)
    {
        var buffer = new System.Text.StringBuilder();
        PdfTextWord? style = null;
        PdfPageLink? link = null;

        void Flush()
        {
            if (style == null || buffer.Length == 0) { buffer.Clear(); return; }
            var run = BuildRun(buffer.ToString(), style);
            if (link != null && Attach(target, run, link, pageCount)) Links++;
            else target.AppendChild(run);
            buffer.Clear();
        }

        foreach (var word in words)
        {
            var wordLink = _options.KeepLinks ? LinkFor(links, word) : null;
            if (style != null && (!SameStyle(style, word) || !ReferenceEquals(link, wordLink)))
                Flush();

            if (buffer.Length > 0) buffer.Append(' ');
            buffer.Append(word.Text);
            style = word;
            link = wordLink;
        }
        Flush();
    }

    private static bool SameStyle(PdfTextWord a, PdfTextWord b) =>
        a.IsBold == b.IsBold &&
        a.ColorArgb == b.ColorArgb &&
        string.Equals(a.FontName, b.FontName, StringComparison.Ordinal) &&
        Math.Abs(a.FontSizePt - b.FontSizePt) < 0.6;

    /// <summary>
    /// Годится ли имя шрифта из PDF для Word.
    ///
    /// В PDF имя шрифта — это имя РЕСУРСА, а не гарнитуры. У документов со
    /// встроенными подмножествами там сплошь и рядом «CIDFont+ F4» или просто
    /// «F1»: такого шрифта в системе нет, Word молча подставляет запасной, и
    /// документ выглядит набранным чужой гарнитурой — обычно более плотной и
    /// широкой. Именно так трёхстраничная форма разъезжалась на пять страниц
    /// и казалась целиком полужирной, хотя полужирным не был ни один пробег.
    ///
    /// Когда имя бессмысленно, честнее не указывать его вовсе: тогда работает
    /// шрифт документа по умолчанию, метрики предсказуемы, а пользователь
    /// меняет гарнитуру одним действием на весь текст.
    /// </summary>
    private static bool IsUsableFontName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length < 3) return false;
        if (trimmed.Contains("CIDFont", StringComparison.OrdinalIgnoreCase)) return false;

        // «F4», «TT12», «C0_0», «g_d0_f1» — идентификаторы ресурсов, не гарнитуры.
        var letters = trimmed.Count(char.IsLetter);
        if (letters <= 2) return false;

        var alphabetic = trimmed.Where(char.IsLetter).ToArray();
        if (alphabetic.Length > 0 && alphabetic.All(char.IsUpper) && trimmed.Any(char.IsDigit) && letters <= 3)
            return false;

        return true;
    }

    private static Run BuildRun(string text, PdfTextWord style)
    {
        var properties = new RunProperties();
        if (IsUsableFontName(style.FontName))
            properties.AppendChild(new RunFonts { Ascii = style.FontName, HighAnsi = style.FontName, ComplexScript = style.FontName });
        if (style.FontSizePt > 0)
        {
            // Word хранит кегль в ПОЛУпунктах.
            var half = Math.Clamp((int)Math.Round(style.FontSizePt * 2), 2, 3276).ToString();
            properties.AppendChild(new FontSize { Val = half });
            properties.AppendChild(new FontSizeComplexScript { Val = half });
        }
        if (style.IsBold) properties.AppendChild(new Bold());

        var rgb = style.ColorArgb & 0x00FFFFFF;
        if (rgb != 0) properties.AppendChild(new Color { Val = rgb.ToString("X6") });

        return new Run(properties, new Text(XmlText.Safe(text)) { Space = SpaceProcessingModeValues.Preserve });
    }

    /// <summary>Ставит пробег внутрь гиперссылки. false — ссылка не годится для Word.</summary>
    private bool Attach(OpenXmlElement target, Run run, PdfPageLink link, int pageCount)
    {
        if (XlsxExporter.IsSafeLink(link.Uri))
        {
            var relationship = _main.AddHyperlinkRelationship(new Uri(link.Uri!, UriKind.Absolute), true);
            target.AppendChild(new Hyperlink(Underlined(run)) { Id = relationship.Id });
            return true;
        }
        if (link.Uri == null && link.TargetPageIndex >= 0 && link.TargetPageIndex < pageCount)
        {
            target.AppendChild(new Hyperlink(Underlined(run)) { Anchor = PageAnchor(link.TargetPageIndex) });
            return true;
        }
        return false;
    }

    /// <summary>Ссылка обязана выглядеть ссылкой, даже если в PDF она была незаметной.</summary>
    private static Run Underlined(Run run)
    {
        var properties = run.GetFirstChild<RunProperties>() ?? run.PrependChild(new RunProperties());
        properties.RemoveAllChildren<Color>();
        properties.AppendChild(new Color { Val = "0563C1" });
        properties.AppendChild(new Underline { Val = UnderlineValues.Single });
        return run;
    }

    private static PdfPageLink? LinkFor(IReadOnlyList<PdfPageLink> links, PdfTextWord word)
    {
        foreach (var link in links)
        {
            var overlapX = Math.Min(word.RectPt.Right, link.RectPt.Right) - Math.Max(word.RectPt.Left, link.RectPt.Left);
            var overlapY = Math.Min(word.RectPt.Top, link.RectPt.Top) - Math.Max(word.RectPt.Bottom, link.RectPt.Bottom);
            // Слово принадлежит ссылке, если та накрывает его больше чем наполовину.
            if (overlapX > word.Width * 0.5 && overlapY > word.Height * 0.5) return link;
        }
        return null;
    }

    private static string PageAnchor(int pageIndex) => "NexusPdfPage" + (pageIndex + 1);

    // ----- таблицы -----

    private void WriteTable(
        ExtractedTable table, PageLayout layout,
        IReadOnlyList<PdfPageLink> links, IReadOnlyList<PdfAnnotationInfo> notes, int pageCount)
    {
        var element = new Table();
        var properties = new TableProperties(
            new TableWidth { Type = TableWidthUnitValues.Dxa, Width = Twips(table.Bounds.Right - table.Bounds.Left) });

        // Границы рисуются только там, где они были в документе: у таблицы,
        // распознанной по пробелам, границ не было, и придумывать их нельзя.
        if (table.Source == TableSource.Ruling)
            properties.AppendChild(Borders());

        // Ширины колонок — из документа, а не на усмотрение Word.
        //
        // Без этой строки Word подбирает ширины сам, по содержимому, и
        // записанную ниже сетку не смотрит вовсе. На заполненном бланке разницы
        // почти нет, а на ПУСТОМ подбирать не по чему: каждая колонка
        // схлопывается в минимальную, и таблица превращается в стопку ниток.
        // Именно так выгружался пустой чек-лист RLM.
        properties.AppendChild(new TableLayout { Type = TableLayoutValues.Fixed });
        element.AppendChild(properties);

        var grid = new TableGrid();
        for (var c = 0; c < table.ColumnCount; c++)
            grid.AppendChild(new GridColumn { Width = Twips(ColumnWidth(table, c)) });
        element.AppendChild(grid);

        // Объединение по вертикали в Word задаётся не размахом, а пометками
        // «начало» и «продолжение» в каждой строке.
        var continued = new bool[table.RowCount, table.ColumnCount];
        foreach (var cell in table.Cells)
            for (var r = cell.Row + 1; r < cell.Row + cell.RowSpan; r++)
                continued[r, cell.Column] = true;

        for (var r = 0; r < table.RowCount; r++)
        {
            var row = new TableRow();
            for (var c = 0; c < table.ColumnCount; c++)
            {
                var cell = table.At(r, c);
                if (cell == null && !continued[r, c]) continue;

                var cellProperties = new TableCellProperties(
                    new TableCellWidth
                    {
                        Type = TableWidthUnitValues.Dxa,
                        Width = Twips(SpanWidth(table, c, cell?.ColumnSpan ?? 1)),
                    });
                if (cell is { ColumnSpan: > 1 })
                    cellProperties.AppendChild(new GridSpan { Val = cell.ColumnSpan });

                // Повёрнутая подпись остаётся повёрнутой: Word умеет писать в
                // ячейке снизу вверх, и в узкой колонке это единственный способ
                // сохранить и текст, и ширину.
                var rotation = RotationOf(layout, cell);
                if (rotation != 0)
                {
                    cellProperties.AppendChild(new TextDirection
                    {
                        Val = rotation == 1
                            ? TextDirectionValues.BottomToTopLeftToRight
                            : TextDirectionValues.TopToBottomRightToLeft,
                    });
                }
                if (cell is { RowSpan: > 1 })
                    cellProperties.AppendChild(new VerticalMerge { Val = MergedCellValues.Restart });
                else if (cell == null)
                    cellProperties.AppendChild(new VerticalMerge { Val = MergedCellValues.Continue });

                var content = new WordTableCell(cellProperties);
                content.AppendChild(CellParagraph(cell, table, layout, links, notes, pageCount));
                row.AppendChild(content);

                if (cell is { ColumnSpan: > 1 }) c += cell.ColumnSpan - 1;
            }
            if (row.ChildElements.Count > 0) element.AppendChild(row);
        }

        _body.AppendChild(element);
        // Word требует абзац после таблицы: без него две таблицы подряд
        // слипаются в одну.
        _body.AppendChild(new Paragraph());
        Tables++;
    }

    private Paragraph CellParagraph(
        TableCell? cell, ExtractedTable table, PageLayout layout,
        IReadOnlyList<PdfPageLink> links, IReadOnlyList<PdfAnnotationInfo> notes, int pageCount)
    {
        var paragraph = new Paragraph(new ParagraphProperties(
            new SpacingBetweenLines { After = "0", Before = "0" }));
        if (cell == null || cell.Text.Length == 0) return paragraph;

        var words = WordsOf(layout, cell.Bounds);
        if (words.Count > 0)
        {
            // Строки ячейки переносятся строками, а не склеиваются в одну.
            //
            // Обычный абзац текста переливать по ширине правильно — там перенос
            // случаен и зависит от вёрстки. В ячейке всё наоборот: это бланк,
            // где каждая строка — отдельный пункт. Склейка превращала «1-Submitted
            // at the port of… 2-Name of ship… 3-Arriving from…» в нечитаемую
            // простыню, и заполнить такую форму было уже нельзя.
            var lines = TextLineBuilder.Build(words);
            ApplyMeasuredLeading(paragraph, lines);
            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0) paragraph.AppendChild(new Run(new Break()));
                AppendRuns(paragraph, lines[i].Words, links, pageCount);
            }
            AttachComments(paragraph, notes,
                cell.Bounds.Left, cell.Bounds.Top, cell.Bounds.Right, cell.Bounds.Bottom);
            return paragraph;
        }

        // Текст ячейки собран из слов, которых на странице уже нет (значение
        // поля формы): начертание брать неоткуда, но потерять содержимое нельзя.
        var properties = new RunProperties();
        if (cell.IsBold) properties.AppendChild(new Bold());
        paragraph.AppendChild(new Run(
            properties, new Text(XmlText.Safe(cell.Text)) { Space = SpaceProcessingModeValues.Preserve }));
        return paragraph;
    }

    /// <summary>
    /// Межстрочный интервал — такой же, каким он был в документе.
    ///
    /// Word по умолчанию ставит строки «одинарно», а это около 1,22 кегля. В
    /// вёрстке бланков интервал обычно плотнее, и разница в четверть строки на
    /// каждой строке превращает три страницы в семь: содержимое перестаёт
    /// помещаться в ячейки и переползает дальше. Здесь шаг берётся из самого
    /// PDF — по расстоянию между серединами соседних строк.
    ///
    /// Медиана, а не среднее: одна пустая строка или заголовок другого кегля
    /// внутри ячейки иначе растянули бы весь блок.
    /// </summary>
    private static void ApplyMeasuredLeading(Paragraph paragraph, IReadOnlyList<TextLine> lines)
    {
        if (lines.Count < 2) return;

        var steps = new List<double>();
        for (var i = 1; i < lines.Count; i++)
        {
            var step = lines[i - 1].CenterY - lines[i].CenterY;
            if (step > 0) steps.Add(step);
        }
        if (steps.Count == 0) return;

        var pitch = TextLine.Median(steps, 0);
        if (pitch <= 0) return;

        // Слишком тесный интервал срежет буквам верхушки: подставленный шрифт
        // может оказаться крупнее исходного. Но и отказываться при малейшем
        // сжатии нельзя — в бланках шаг строки почти всегда чуть меньше кегля,
        // и прежнее правило «не меньше кегля» выключало перенос шага там, где
        // он и нужен. Небольшое сжатие Word переживает без потерь.
        var biggest = lines.Max(l => l.FontSize);
        var floor = biggest * 0.85;
        if (pitch < floor) pitch = floor;

        var properties = paragraph.GetFirstChild<ParagraphProperties>();
        if (properties == null) return;
        properties.RemoveAllChildren<SpacingBetweenLines>();
        properties.AppendChild(new SpacingBetweenLines
        {
            After = "0",
            Before = "0",
            Line = Twips(pitch),
            LineRule = LineSpacingRuleValues.Exact,
        });
    }

    /// <summary>Поворот текста ячейки, если повёрнуто большинство её слов.</summary>
    private static int RotationOf(PageLayout layout, TableCell? cell)
    {
        if (cell == null) return 0;
        var words = WordsOf(layout, cell.Bounds);
        if (words.Count == 0) return 0;
        var rotated = words.Where(w => w.IsRotated).ToList();
        if (rotated.Count * 2 <= words.Count) return 0;
        return rotated.Count(w => w.RotationQuarters == 1) >= rotated.Count / 2.0 ? 1 : 3;
    }

    /// <summary>Слова страницы внутри прямоугольника — чтобы сохранить их начертание.</summary>
    private static IReadOnlyList<PdfTextWord> WordsOf(PageLayout layout, PdfTextRect box) =>
        layout.Words
            .Where(w =>
            {
                var x = (w.RectPt.Left + w.RectPt.Right) / 2.0;
                return x >= box.Left - 1 && x <= box.Right + 1 &&
                       w.CenterY <= box.Top + 1 && w.CenterY >= box.Bottom - 1;
            })
            .ToList();

    private static double ColumnWidth(ExtractedTable table, int column) =>
        table.ColumnEdges.Count > column + 1
            ? Math.Max(6, table.ColumnEdges[column + 1] - table.ColumnEdges[column])
            : Math.Max(6, (table.Bounds.Right - table.Bounds.Left) / Math.Max(1, table.ColumnCount));

    private static double SpanWidth(ExtractedTable table, int column, int span)
    {
        var width = 0.0;
        for (var i = column; i < column + span; i++) width += ColumnWidth(table, i);
        return width;
    }

    private static TableBorders Borders()
    {
        static EnumValue<BorderValues> Single() => new(BorderValues.Single);
        return new TableBorders(
            new TopBorder { Val = Single(), Size = 4 },
            new BottomBorder { Val = Single(), Size = 4 },
            new LeftBorder { Val = Single(), Size = 4 },
            new RightBorder { Val = Single(), Size = 4 },
            new InsideHorizontalBorder { Val = Single(), Size = 4 },
            new InsideVerticalBorder { Val = Single(), Size = 4 });
    }

    // ----- картинки -----

    /// <summary>Ряд картинок — одним абзацем, чтобы они остались в одной строке.</summary>
    private void WriteImageRow(IReadOnlyList<PdfPageImage> row)
    {
        if (!_options.KeepImages || row.Count == 0) return;

        var paragraph = new Paragraph(new ParagraphProperties(
            new SpacingBetweenLines { After = "0", Before = "0" }));
        var placed = 0;
        foreach (var image in row)
        {
            var drawing = BuildDrawing(image);
            if (drawing == null) continue;
            paragraph.AppendChild(new Run(drawing));
            placed++;
            Images++;
        }

        if (placed > 0) _body.AppendChild(paragraph);
    }

    /// <summary>
    /// Картинки сжимаются по одной, в том же потоке.
    ///
    /// Сжатие нескольких разом было измерено и отвергнуто: на кодеках Windows
    /// оно дало 0–4 % (в пределах шума), потому что время экспорта делится
    /// примерно поровну между PDFium и записью документа, а на сжатие уходит
    /// шестая часть. Зато несколько ФАЙЛОВ разом дают 1,75x — параллелить
    /// стоит там, а не здесь.
    /// </summary>
    private Drawing? BuildDrawing(PdfPageImage image)
    {
        var encoded = _encode(image.Bgra, image.PixelWidth, image.PixelHeight);
        if (encoded == null) return null;

        var partType = encoded.ContentType switch
        {
            "image/jpeg" => ImagePartType.Jpeg,
            "image/bmp" => ImagePartType.Bmp,
            _ => ImagePartType.Png,
        };
        var part = _main.AddImagePart(partType);
        using (var stream = new MemoryStream(encoded.Bytes))
            part.FeedData(stream);

        var width = (long)Math.Round(Math.Max(1, image.RectPt.Right - image.RectPt.Left) * PointsToEmu);
        var height = (long)Math.Round(Math.Max(1, image.RectPt.Top - image.RectPt.Bottom) * PointsToEmu);
        var id = _drawingId++;

        var drawing = new Drawing(new DW.Inline(
            new DW.Extent { Cx = width, Cy = height },
            new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
            new DW.DocProperties { Id = id, Name = "Рисунок " + id },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(
                        new PIC.NonVisualDrawingProperties { Id = 0U, Name = "image" + id },
                        new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(
                        new A.Blip { Embed = _main.GetIdOfPart(part) },
                        new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(
                        new A.Transform2D(
                            new A.Offset { X = 0L, Y = 0L },
                            new A.Extents { Cx = width, Cy = height }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        {
            DistanceFromTop = 0U,
            DistanceFromBottom = 0U,
            DistanceFromLeft = 0U,
            DistanceFromRight = 0U,
        });

        return drawing;
    }

    // ----- примечания -----

    /// <summary>
    /// Аннотации, попавшие в этот прямоугольник, становятся примечаниями Word
    /// к его тексту. Комментарий, оторванный от места, к которому относится, —
    /// это потерянный комментарий.
    /// </summary>
    private void AttachComments(
        OpenXmlElement paragraph, IReadOnlyList<PdfAnnotationInfo> notes,
        double left, double top, double right, double bottom)
    {
        if (!_options.KeepComments) return;

        foreach (var note in notes)
        {
            if (note.RectPt is not { } rect) continue;
            if (note.Contents.Length == 0) continue;
            var x = (rect.Left + rect.Right) / 2.0;
            var y = (rect.Top + rect.Bottom) / 2.0;
            if (x < left - 4 || x > right + 4 || y > top + 4 || y < bottom - 4) continue;

            var id = (++_commentId).ToString();
            EnsureComments().AppendChild(new Comment(
                new Paragraph(new Run(new Text(XmlText.Safe(note.Contents)) { Space = SpaceProcessingModeValues.Preserve })))
            {
                Id = id,
                Author = note.Author.Length > 0 ? note.Author : "PDF",
                Initials = note.Author.Length > 0 ? note.Author[..1] : "P",
            });

            paragraph.InsertAt(new CommentRangeStart { Id = id }, 1);
            paragraph.AppendChild(new CommentRangeEnd { Id = id });
            paragraph.AppendChild(new Run(new CommentReference { Id = id }));
            CommentCount++;
        }
    }

    private Comments EnsureComments()
    {
        if (_comments != null) return _comments;
        var part = _main.AddNewPart<WordprocessingCommentsPart>();
        part.Comments = new Comments();
        _comments = part.Comments;
        return _comments;
    }

    // ----- разделы и стили -----

    private void CloseSection()
    {
        // Свойства раздела живут в последнем абзаце этого раздела — так
        // устроен формат Word.
        var last = _body.Elements<Paragraph>().LastOrDefault();
        if (last == null)
        {
            last = new Paragraph();
            _body.AppendChild(last);
        }
        var properties = last.GetFirstChild<ParagraphProperties>() ?? last.PrependChild(new ParagraphProperties());
        properties.AppendChild(SectionProperties());
    }

    private SectionProperties SectionProperties()
    {
        var size = _sectionSize ?? new PdfPageDescriptor(595, 842);
        return new SectionProperties(
            new PageSize
            {
                Width = (uint)Math.Round(size.WidthPoints * PointsToTwips),
                Height = (uint)Math.Round(size.HeightPoints * PointsToTwips),
                Orient = size.WidthPoints > size.HeightPoints
                    ? PageOrientationValues.Landscape
                    : PageOrientationValues.Portrait,
            },
            new PageMargin
            {
                Left = (uint)Math.Round(_sectionMargins.Left * PointsToTwips),
                Right = (uint)Math.Round(_sectionMargins.Right * PointsToTwips),
                Top = (int)Math.Round(_sectionMargins.Top * PointsToTwips),
                Bottom = (int)Math.Round(_sectionMargins.Bottom * PointsToTwips),
                Header = 0,
                Footer = 0,
                Gutter = 0,
            });
    }

    /// <summary>
    /// Поля берутся по фактическому содержимому страницы: так текст в Word
    /// стоит там же, где стоял на бумаге. Слишком маленькие поля выправляются —
    /// с нулевыми полями документ невозможно напечатать.
    /// </summary>
    private static (double Left, double Top, double Right, double Bottom) MarginsOf(PageLayout layout)
    {
        var boxes = layout.Lines
            .Select(l => (l.Left, l.Top, l.Right, l.Bottom))
            .Concat(layout.Tables.Select(t => (t.Bounds.Left, t.Bounds.Top, t.Bounds.Right, t.Bounds.Bottom)))
            .ToList();
        if (boxes.Count == 0) return (56, 56, 56, 56);

        var left = Math.Clamp(boxes.Min(b => b.Item1), 18, 200);
        var right = Math.Clamp(layout.WidthPt - boxes.Max(b => b.Item3), 18, 200);
        var top = Math.Clamp(layout.HeightPt - boxes.Max(b => b.Item2), 18, 200);
        var bottom = Math.Clamp(boxes.Min(b => b.Item4), 18, 200);
        return (left, top, right, bottom);
    }

    private static string Twips(double points) =>
        Math.Max(0, (int)Math.Round(points * PointsToTwips)).ToString();

    private void AddStyles()
    {
        var part = _main.AddNewPart<StyleDefinitionsPart>();
        part.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" },
                    new FontSize { Val = "22" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto }))),
            new Style(
                new StyleName { Val = "heading 2" },
                new BasedOn { Val = "Normal" },
                new StyleParagraphProperties(new OutlineLevel { Val = 1 }),
                new StyleRunProperties(new Bold()))
            {
                Type = StyleValues.Paragraph,
                StyleId = "Heading2",
                PrimaryStyle = new PrimaryStyle(),
            });
        part.Styles.Save();
    }
}
