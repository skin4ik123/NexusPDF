using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using NexusPdf.Export;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.UnitTests;

/// <summary>
/// Сборка абзацев и запись документа Word.
///
/// Главное здесь — абзацы. Если каждую строку PDF сделать абзацем Word, файл
/// выглядит похоже, но разваливается от первой же правки: перенос слов не
/// работает, текст едет лесенкой. Поэтому проверяется именно склейка
/// перенесённых строк и НЕсклейка того, что абзацем не было.
/// </summary>
public sealed class WordExportTests
{
    private static PdfTextWord Word(
        string text, double left, double bottom, double width, double height = 10,
        bool bold = false, string font = "Calibri") =>
        new(text, new PdfTextRect(left, bottom + height, left + width, bottom),
            height * 0.8, bold ? 700 : 400, 0xFF000000, 0, font);

    /// <summary>Строка на всю ширину — перенос; короткая — конец абзаца.</summary>
    private static IReadOnlyList<TextLine> Lines(params (string Text, double Left, double Right, double Y)[] rows) =>
        rows.Select(r => new TextLine(new[] { Word(r.Text, r.Left, r.Y, r.Right - r.Left) })).ToList();

    [Fact]
    public void Wrapped_Lines_Become_One_Paragraph()
    {
        var lines = Lines(
            ("Настоящий договор составлен в двух", 40, 500, 700),
            ("экземплярах, имеющих равную силу для", 40, 500, 688),
            ("обеих сторон.", 40, 180, 676),
            ("Второй абзац начинается здесь и тоже", 40, 500, 660),
            ("тянется до края.", 40, 220, 648));

        var paragraphs = ParagraphBuilder.Build(lines);

        Assert.Equal(2, paragraphs.Count);
        Assert.Equal("Настоящий договор составлен в двух экземплярах, имеющих равную силу для обеих сторон.",
            paragraphs[0].Text);
        Assert.Equal(2, paragraphs[1].Lines.Count);
    }

    /// <summary>Большой вертикальный разрыв разделяет абзацы, даже если строка полная.</summary>
    [Fact]
    public void A_Wide_Vertical_Gap_Splits_Paragraphs()
    {
        var lines = Lines(
            ("Первая строка тянется до правого края", 40, 500, 700),
            ("Вторая строка после большого отступа", 40, 500, 640));

        Assert.Equal(2, ParagraphBuilder.Build(lines).Count);
    }

    /// <summary>Пункты списка — разные абзацы, иначе список слипнется в кашу.</summary>
    [Fact]
    public void List_Items_Stay_Separate()
    {
        var lines = Lines(
            ("• первый пункт списка тянется до края", 40, 500, 700),
            ("• второй пункт списка тоже до края", 40, 500, 688),
            ("1. третий пункт уже нумерованный край", 40, 500, 676));

        Assert.Equal(3, ParagraphBuilder.Build(lines).Count);
    }

    [Fact]
    public void Centered_And_Indented_Text_Is_Recognised()
    {
        var lines = new List<TextLine>
        {
            new(new[] { Word("Первая строка блока во всю ширину", 40, 700, 460) }),
            new(new[] { Word("ЗАГОЛОВОК ПО ЦЕНТРУ", 200, 660, 140) }),
            new(new[] { Word("Абзац с красной строки идёт дальше", 68, 620, 432) }),
        };

        var paragraphs = ParagraphBuilder.Build(lines);

        Assert.Equal(ParagraphAlignment.Center, paragraphs[1].Alignment);
        Assert.Equal(ParagraphAlignment.Left, paragraphs[2].Alignment);
        Assert.True(paragraphs[2].IndentPt >= 8);
    }

    // ----- запись документа -----

    private static string TempFile(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    private static PageLayout SampleLayout()
    {
        var words = new[]
        {
            Word("Отчёт", 40, 740, 60, 16, bold: true, font: "Times New Roman"),
            Word("за", 104, 740, 20, 16, bold: true, font: "Times New Roman"),
            Word("год", 128, 740, 30, 16, bold: true, font: "Times New Roman"),
            Word("Подробности", 40, 700, 80),
            Word("на", 124, 700, 20),
            Word("сайте", 148, 700, 40),
            Word("Итого", 60, 664, 60), Word("Сумма", 330, 664, 50),
            Word("Болт", 50, 644, 40), Word("10", 200, 644, 20), Word("1234", 330, 644, 40),
        };
        var rulings = new PdfRulingLine[]
        {
            new(true, 680, 40, 440, 0.8), new(true, 660, 40, 440, 0.8), new(true, 640, 40, 440, 0.8),
            new(false, 40, 640, 680, 0.8), new(false, 440, 640, 680, 0.8),
            new(false, 190, 640, 660, 0.8), new(false, 320, 640, 680, 0.8),
        };
        return PageAnalyzer.Analyze(0, 595, 842, words, rulings, Array.Empty<PdfFormFieldValue>());
    }

    [Fact]
    public void The_Document_Opens_With_Paragraphs_Tables_Links_And_Comments()
    {
        var path = TempFile("отчёт.docx");
        var links = new PdfPageLink[]
        {
            new(new PdfTextRect(146, 712, 190, 698), "https://example.org/", -1),
        };
        var notes = new PdfAnnotationInfo[]
        {
            new(0, 1, "Проверить сумму", "Артур", "", new PdfTextRect(330, 654, 380, 644)),
        };
        var image = new PdfPageImage(
            new byte[8 * 8 * 4].Select((_, i) => (byte)(i % 251)).ToArray(), 8, 8,
            new PdfTextRect(40, 600, 140, 520));

        using (var writer = new DocxExporter(path))
        {
            writer.AddPage(SampleLayout(), links, notes, new[] { image }, 1);
            writer.Finish();
            Assert.Equal(1, writer.Pages);
            Assert.Equal(1, writer.Tables);
            Assert.Equal(1, writer.Images);
            Assert.Equal(1, writer.Links);
            Assert.Equal(1, writer.CommentCount);
        }

        using var document = WordprocessingDocument.Open(path, false);
        var main = document.MainDocumentPart!;
        var body = main.Document.Body!;

        // Таблица стала таблицей Word с объединённой шапкой, а не текстом.
        var table = body.Descendants<Table>().Single();
        var firstRow = table.Elements<TableRow>().First();
        Assert.Equal(2, firstRow.Elements<DocumentFormat.OpenXml.Wordprocessing.TableCell>().Count());
        Assert.Equal(2, firstRow.Descendants<GridSpan>().Single().Val!.Value);

        // Заголовок сохранил кегль, начертание и шрифт.
        var heading = body.Descendants<Paragraph>()
            .First(p => p.InnerText.Contains("Отчёт"));
        var runProps = heading.Descendants<RunProperties>().First();
        Assert.NotNull(runProps.Bold);
        Assert.Equal("Times New Roman", runProps.RunFonts!.Ascii!.Value);
        Assert.Equal("26", runProps.FontSize!.Val!.Value);   // 12.8 пт → 26 полупунктов

        // Ссылка живая: у неё есть внешняя цель.
        var hyperlink = body.Descendants<Hyperlink>().Single();
        var relationship = main.HyperlinkRelationships.Single(r => r.Id == hyperlink.Id!.Value);
        Assert.Equal("https://example.org/", relationship.Uri.ToString());

        // Аннотация стала примечанием Word с автором.
        var comment = main.WordprocessingCommentsPart!.Comments.Elements<Comment>().Single();
        Assert.Equal("Проверить сумму", comment.InnerText);
        Assert.Equal("Артур", comment.Author!.Value);
        Assert.Single(body.Descendants<CommentReference>());

        // Картинка вставлена как настоящий рисунок, а не потеряна.
        Assert.Single(body.Descendants<Drawing>());
        Assert.Single(main.ImageParts);

        // Размер страницы перенесён из PDF (595×842 пункта → твипы).
        var pageSize = body.Descendants<PageSize>().Last();
        Assert.Equal(11900u, pageSize.Width!.Value);
        Assert.Equal(16840u, pageSize.Height!.Value);
    }

    /// <summary>Смена ориентации — это новый раздел Word, а не просто разрыв страницы.</summary>
    [Fact]
    public void A_Landscape_Page_Starts_Its_Own_Section()
    {
        var path = TempFile("развороты.docx");
        var portrait = PageAnalyzer.Analyze(0, 595, 842,
            new[] { Word("книжная", 40, 700, 60) },
            Array.Empty<PdfRulingLine>(), Array.Empty<PdfFormFieldValue>());
        var landscape = PageAnalyzer.Analyze(1, 842, 595,
            new[] { Word("альбомная", 40, 500, 70) },
            Array.Empty<PdfRulingLine>(), Array.Empty<PdfFormFieldValue>());

        using (var writer = new DocxExporter(path))
        {
            writer.AddPage(portrait, Array.Empty<PdfPageLink>(), Array.Empty<PdfAnnotationInfo>(),
                Array.Empty<PdfPageImage>(), 2);
            writer.AddPage(landscape, Array.Empty<PdfPageLink>(), Array.Empty<PdfAnnotationInfo>(),
                Array.Empty<PdfPageImage>(), 2);
            writer.Finish();
        }

        using var document = WordprocessingDocument.Open(path, false);
        var sizes = document.MainDocumentPart!.Document.Body!.Descendants<PageSize>().ToList();

        Assert.Equal(2, sizes.Count);
        Assert.Equal(PageOrientationValues.Portrait, sizes[0].Orient!.Value);
        Assert.Equal(PageOrientationValues.Landscape, sizes[1].Orient!.Value);
    }

    /// <summary>Встроенный PNG нужен там, где кодеков Windows нет: без него картинки терялись бы.</summary>
    [Fact]
    public void The_Built_In_Png_Is_A_Real_Png()
    {
        var bgra = new byte[4 * 3 * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 10; bgra[i + 1] = 120; bgra[i + 2] = 230; bgra[i + 3] = 255;
        }

        var encoded = PortablePng.Encode(bgra, 4, 3);

        Assert.Equal("image/png", encoded.ContentType);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
            encoded.Bytes.Take(8).ToArray());
        Assert.Contains("IHDR", System.Text.Encoding.ASCII.GetString(encoded.Bytes.Take(20).ToArray()));
        Assert.EndsWith("IEND", System.Text.Encoding.ASCII.GetString(
            encoded.Bytes.Skip(encoded.Bytes.Length - 8).Take(4).ToArray()));
    }

    /// <summary>Документ Word — это ZIP с частями; битый файл Word открыть не сможет.</summary>
    [Fact]
    public void The_File_Is_A_Valid_Package()
    {
        var path = TempFile("пакет.docx");
        using (var writer = new DocxExporter(path))
        {
            writer.AddPage(SampleLayout(), Array.Empty<PdfPageLink>(), Array.Empty<PdfAnnotationInfo>(),
                Array.Empty<PdfPageImage>(), 1);
            writer.Finish();
        }

        using var archive = ZipFile.OpenRead(path);
        Assert.Contains(archive.Entries, e => e.FullName == "word/document.xml");
        Assert.Contains(archive.Entries, e => e.FullName == "[Content_Types].xml");
        Assert.True(new FileInfo(path).Length > 1000);
    }
}
