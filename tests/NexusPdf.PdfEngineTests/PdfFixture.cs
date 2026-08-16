using System.Globalization;
using System.Text;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Генератор миниатюрных, но структурно корректных PDF для тестов движка:
/// произвольные размеры страниц, /Rotate и простой текст стандартным шрифтом.
/// </summary>
public static class PdfFixture
{
    public sealed record PageSpec(double Width, double Height, int Rotate = 0, string Text = "Hello NexusPDF");

    public static byte[] Build(params PageSpec[] pages)
    {
        if (pages.Length == 0)
            throw new ArgumentException("Нужна хотя бы одна страница.");

        var objects = new List<string>();
        var fontObjNumber = 3 + pages.Length * 2;

        var kids = string.Join(" ", Enumerable.Range(0, pages.Length).Select(i => $"{3 + i * 2} 0 R"));
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages.Length} >>");

        for (var i = 0; i < pages.Length; i++)
        {
            var p = pages[i];
            var contentObj = 4 + i * 2;
            var w = p.Width.ToString("0.##", CultureInfo.InvariantCulture);
            var h = p.Height.ToString("0.##", CultureInfo.InvariantCulture);
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Rotate {p.Rotate} " +
                $"/Contents {contentObj} 0 R /Resources << /Font << /F1 {fontObjNumber} 0 R >> >> >>");

            var text = p.Text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
            var stream = $"BT /F1 24 Tf 72 72 Td ({text}) Tj ET";
            objects.Add($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");
        }

        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        var buffer = new MemoryStream();
        void WriteRaw(string s) => buffer.Write(Encoding.ASCII.GetBytes(s));

        WriteRaw("%PDF-1.4\n");
        buffer.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

        var offsets = new long[objects.Count + 1];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = buffer.Position;
            WriteRaw($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xrefPosition = buffer.Position;
        WriteRaw($"xref\n0 {objects.Count + 1}\n");
        WriteRaw("0000000000 65535 f \n");
        for (var i = 1; i <= objects.Count; i++)
            WriteRaw($"{offsets[i]:0000000000} 00000 n \n");
        WriteRaw($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefPosition}\n%%EOF\n");

        return buffer.ToArray();
    }

    /// <summary>Одностраничный PDF с настоящим AcroForm-текстовым полем (merged field+widget).</summary>
    public static byte[] BuildWithTextField(string fieldName, double left, double bottom, double right, double top)
    {
        var objects = new List<string>
        {
            // 1: каталог с AcroForm
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /NeedAppearances true " +
            "/DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv 5 0 R >> >> >> >>",
            // 2: дерево страниц
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            // 3: страница с виджетом
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] " +
            "/Resources << /Font << /Helv 5 0 R >> >> >>",
            // 4: поле-виджет
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({fieldName}) " +
            $"/Rect [{left} {bottom} {right} {top}] /F 4 /DA (/Helv 12 Tf 0 g) >>",
            // 5: шрифт
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var buffer = new MemoryStream();
        void WriteRaw(string s) => buffer.Write(Encoding.ASCII.GetBytes(s));

        WriteRaw("%PDF-1.5\n");
        buffer.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

        var offsets = new long[objects.Count + 1];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = buffer.Position;
            WriteRaw($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xrefPosition = buffer.Position;
        WriteRaw($"xref\n0 {objects.Count + 1}\n");
        WriteRaw("0000000000 65535 f \n");
        for (var i = 1; i <= objects.Count; i++)
            WriteRaw($"{offsets[i]:0000000000} 00000 n \n");
        WriteRaw($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefPosition}\n%%EOF\n");
        return buffer.ToArray();
    }

    /// <summary>Одностраничный PDF с выпадающим списком (combobox, /FT /Ch + флаг Combo).</summary>
    public static byte[] BuildWithComboField(string fieldName, params string[] options)
    {
        var opt = string.Join(" ", options.Select(o => $"({o})"));
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /NeedAppearances true " +
            "/DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv 5 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] " +
            "/Resources << /Font << /Helv 5 0 R >> >> >>",
            // /Ff 131072 = бит Combo (1<<17)
            $"<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 131072 /T ({fieldName}) " +
            $"/Opt [{opt}] /Rect [100 600 400 640] /F 4 /DA (/Helv 12 Tf 0 g) >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var buffer = new MemoryStream();
        void WriteRaw(string s) => buffer.Write(Encoding.ASCII.GetBytes(s));

        WriteRaw("%PDF-1.5\n");
        buffer.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });
        var offsets = new long[objects.Count + 1];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = buffer.Position;
            WriteRaw($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xrefPosition = buffer.Position;
        WriteRaw($"xref\n0 {objects.Count + 1}\n");
        WriteRaw("0000000000 65535 f \n");
        for (var i = 1; i <= objects.Count; i++)
            WriteRaw($"{offsets[i]:0000000000} 00000 n \n");
        WriteRaw($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefPosition}\n%%EOF\n");
        return buffer.ToArray();
    }

    /// <summary>
    /// Трёхстраничный PDF с оглавлением: два верхних раздела, у первого —
    /// вложенный подраздел. Цели заданы прямым /Dest (первый и третий) и
    /// действием /GoTo (второй) — оба способа встречаются в реальных файлах.
    /// </summary>
    public static byte[] BuildWithOutline()
    {
        var objects = new List<string>
        {
            // 1: каталог с оглавлением
            "<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R >>",
            // 2: дерево страниц
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
            // 3..5: страницы
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            // 6: корень оглавления
            "<< /Type /Outlines /First 7 0 R /Last 8 0 R /Count 3 >>",
            // 7: первый раздел -> страница 1, с вложенным узлом
            "<< /Title (Chapter One) /Parent 6 0 R /Next 8 0 R /First 9 0 R /Last 9 0 R " +
            "/Count 1 /Dest [3 0 R /Fit] >>",
            // 8: второй раздел -> страница 3 через действие GoTo
            "<< /Title (Chapter Two) /Parent 6 0 R /Prev 7 0 R " +
            "/A << /S /GoTo /D [5 0 R /Fit] >> >>",
            // 9: вложенный подраздел -> страница 2
            "<< /Title (Section 1.1) /Parent 7 0 R /Dest [4 0 R /Fit] >>",
        };

        var buffer = new MemoryStream();
        void WriteRaw(string s) => buffer.Write(Encoding.ASCII.GetBytes(s));

        WriteRaw("%PDF-1.5\n");
        buffer.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });
        var offsets = new long[objects.Count + 1];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = buffer.Position;
            WriteRaw($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xrefPosition = buffer.Position;
        WriteRaw($"xref\n0 {objects.Count + 1}\n");
        WriteRaw("0000000000 65535 f \n");
        for (var i = 1; i <= objects.Count; i++)
            WriteRaw($"{offsets[i]:0000000000} 00000 n \n");
        WriteRaw($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefPosition}\n%%EOF\n");
        return buffer.ToArray();
    }

    /// <summary>Одностраничный PDF с вложенным файлом (/Names /EmbeddedFiles).</summary>
    public static byte[] BuildWithAttachment(string attachmentName, string attachmentContent)
    {
        var objects = new List<string>
        {
            // 1: каталог со списком вложений
            $"<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names " +
            $"[({attachmentName}) 4 0 R] >> >> >>",
            // 2: дерево страниц
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            // 3: страница
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] >>",
            // 4: описание файла
            $"<< /Type /Filespec /F ({attachmentName}) /UF ({attachmentName}) " +
            $"/Desc (Test attachment) /EF << /F 5 0 R >> " +
            $"/Params << /Size {attachmentContent.Length} /CreationDate (D:20260101000000Z) >> >>",
            // 5: сам поток вложения
            $"<< /Type /EmbeddedFile /Subtype /text#2Fplain /Length {attachmentContent.Length} >>\n" +
            $"stream\n{attachmentContent}\nendstream",
        };

        var buffer = new MemoryStream();
        void WriteRaw(string s) => buffer.Write(Encoding.ASCII.GetBytes(s));

        WriteRaw("%PDF-1.7\n");
        buffer.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });
        var offsets = new long[objects.Count + 1];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = buffer.Position;
            WriteRaw($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xrefPosition = buffer.Position;
        WriteRaw($"xref\n0 {objects.Count + 1}\n");
        WriteRaw("0000000000 65535 f \n");
        for (var i = 1; i <= objects.Count; i++)
            WriteRaw($"{offsets[i]:0000000000} 00000 n \n");
        WriteRaw($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefPosition}\n%%EOF\n");
        return buffer.ToArray();
    }

    public static string WriteAttachmentToTemp(string fileName, string attachmentName, string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, BuildWithAttachment(attachmentName, content));
        return path;
    }

    /// <summary>
    /// Двухслойный PDF: по строке текста в каждом слое (/OC ... BDC).
    /// offLayer 0 — оба слоя включены, 1 или 2 — соответствующий выключен в
    /// конфигурации по умолчанию.
    /// </summary>
    public static byte[] BuildWithLayers(int offLayer = 0)
    {
        var on = offLayer switch
        {
            1 => "7 0 R",
            2 => "6 0 R",
            _ => "6 0 R 7 0 R",
        };
        var off = offLayer switch
        {
            1 => "6 0 R",
            2 => "7 0 R",
            _ => "",
        };

        var stream =
            "/OC /L1 BDC BT /F1 24 Tf 72 700 Td (LAYERONE) Tj ET EMC\n" +
            "/OC /L2 BDC BT /F1 24 Tf 72 600 Td (LAYERTWO) Tj ET EMC";

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [6 0 R 7 0 R] " +
            $"/D << /Order [6 0 R 7 0 R] /ON [{on}] /OFF [{off}] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> /Properties << /L1 6 0 R /L2 7 0 R >> >> >>",
            $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /OCG /Name (Layer one) >>",
            "<< /Type /OCG /Name (Layer two) >>",
        };

        var buffer = new MemoryStream();
        void WriteRaw(string s) => buffer.Write(Encoding.ASCII.GetBytes(s));

        WriteRaw("%PDF-1.6\n");
        buffer.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });
        var offsets = new long[objects.Count + 1];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = buffer.Position;
            WriteRaw($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xrefPosition = buffer.Position;
        WriteRaw($"xref\n0 {objects.Count + 1}\n");
        WriteRaw("0000000000 65535 f \n");
        for (var i = 1; i <= objects.Count; i++)
            WriteRaw($"{offsets[i]:0000000000} 00000 n \n");
        WriteRaw($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefPosition}\n%%EOF\n");
        return buffer.ToArray();
    }

    public static string WriteLayersToTemp(string fileName, int offLayer = 0)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, BuildWithLayers(offLayer));
        return path;
    }

    public static string WriteOutlineToTemp(string fileName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, BuildWithOutline());
        return path;
    }

    public static string WriteComboFieldToTemp(string fileName, string fieldName, params string[] options)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, BuildWithComboField(fieldName, options));
        return path;
    }

    public static string WriteTextFieldToTemp(string fileName, string fieldName,
        double left = 100, double bottom = 600, double right = 400, double top = 640)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, BuildWithTextField(fieldName, left, bottom, right, top));
        return path;
    }

    /// <summary>
    /// Страница с НАСТОЯЩЕЙ таблицей: тонкие залитые прямоугольники вместо
    /// границ (так рисует большинство генераторов), текст по ячейкам, ссылка
    /// поверх одной из них и заполненное поле формы под таблицей.
    ///
    /// Только латиница: у стандартного Helvetica нет кириллицы, и текст из
    /// такого PDF извлекался бы мусором — проверять было бы нечего.
    /// </summary>
    public static byte[] BuildWithTable()
    {
        var content = new StringBuilder();
        content.Append("0 g\n");

        // Горизонтальные границы: y = 700, 680, 660, 640.
        foreach (var y in new[] { 700, 680, 660, 640 })
            content.Append($"40 {y} 400 0.8 re f\n");
        // Вертикальные границы: x = 40, 190, 320, 440.
        foreach (var x in new[] { 40, 190, 320, 440 })
            content.Append($"{x} 640 0.8 60 re f\n");

        void Cell(int x, int y, string text) =>
            content.Append($"BT /F1 10 Tf {x} {y} Td ({text}) Tj ET\n");

        Cell(50, 686, "Item");
        Cell(200, 686, "Qty");
        Cell(330, 686, "Price");
        Cell(50, 666, "Bolt");
        Cell(200, 666, "10");
        Cell(330, 666, "25,50");
        Cell(50, 646, "Nut");
        Cell(200, 646, "20");
        Cell(330, 646, "7,00");

        // Обычная строка вне таблицы.
        Cell(40, 560, "Total order for the month");

        // Подпись, повёрнутая на 90° против часовой (читается снизу вверх) —
        // так подписывают узкие колонки в бланках.
        content.Append("BT /F1 9 Tf 0 1 -1 0 470 640 Tm (SIDE LABEL) Tj ET\n");

        var stream = content.ToString();
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [7 0 R] /DA (/F1 10 Tf 0 g) >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Annots [6 0 R 7 0 R] /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            // 6: ссылка поверх ячейки «Bolt»
            "<< /Type /Annot /Subtype /Link /Rect [45 660 120 680] /Border [0 0 0] " +
            "/A << /S /URI /URI (https://example.org/bolt) >> >>",
            // 7: заполненное текстовое поле формы
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Customer) /V (Acme Ltd) " +
            "/Rect [40 600 300 620] /F 4 /DA (/F1 10 Tf 0 g) >>",
        };

        var buffer = new MemoryStream();
        void WriteRaw(string s) => buffer.Write(Encoding.ASCII.GetBytes(s));

        WriteRaw("%PDF-1.7\n");
        buffer.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });
        var offsets = new long[objects.Count + 1];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = buffer.Position;
            WriteRaw($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xrefPosition = buffer.Position;
        WriteRaw($"xref\n0 {objects.Count + 1}\n");
        WriteRaw("0000000000 65535 f \n");
        for (var i = 1; i <= objects.Count; i++)
            WriteRaw($"{offsets[i]:0000000000} 00000 n \n");
        WriteRaw($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefPosition}\n%%EOF\n");
        return buffer.ToArray();
    }

    public static string WriteTableToTemp(string fileName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, BuildWithTable());
        return path;
    }

    public static string WriteToTemp(string fileName, params PageSpec[] pages)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, Build(pages));
        return path;
    }
}
