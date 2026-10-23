using System.Globalization;
using System.Text;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Генератор миниатюрных, но структурно корректных PDF для тестов движка:
/// произвольные размеры страниц, /Rotate и простой текст стандартным шрифтом.
/// </summary>
public static class PdfFixture
{
    /// <param name="RawContent">
    /// Готовый поток содержимого вместо текста — для страниц с линиями и
    /// фигурами (проверка геометрии); null — обычный текст.
    /// </param>
    /// <param name="ExtraResources">Дополнительные записи словаря /Resources страницы (например, /Properties).</param>
    public sealed record PageSpec(
        double Width, double Height, int Rotate = 0, string Text = "Hello NexusPDF", string? RawContent = null,
        string? ExtraResources = null);

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
                $"/Contents {contentObj} 0 R /Resources << /Font << /F1 {fontObjNumber} 0 R >> {p.ExtraResources} >> >>");

            var text = p.Text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
            var stream = p.RawContent ?? $"BT /F1 24 Tf 72 72 Td ({text}) Tj ET";
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

    /// <summary>
    /// Форма из трёх полей: два текстовых и чекбокс — минимум, на котором
    /// проверяется переход фокуса кликами и видимость значений вне режима.
    /// Прямоугольники в PDF-координатах (y снизу): text1 [100 700..740 400],
    /// text2 [100 600..640 400], чекбокс [100 500..530 130].
    /// </summary>
    public static string WriteFormTrioToTemp(string fileName)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R 6 0 R] " +
            "/NeedAppearances true /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv 7 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R 5 0 R 6 0 R] " +
            "/Resources << /Font << /Helv 7 0 R >> >> >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (text1) " +
            "/Rect [100 700 400 740] /F 4 /DA (/Helv 12 Tf 0 g) >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (text2) " +
            "/Rect [100 600 400 640] /F 4 /DA (/Helv 12 Tf 0 g) >>",
            // Чекбокс: без /AP, состояние /AS Off; ZapfDingbats в DA — как делает Acrobat.
            "<< /Type /Annot /Subtype /Widget /FT /Btn /T (check1) " +
            "/Rect [100 500 130 530] /F 4 /AS /Off /DA (/ZaDb 0 Tf 0 g) /MK << /CA (4) >> >>",
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

        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
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

    /// <summary>Какая форма: обычный AcroForm, гибрид AcroForm + XFA или целиком динамическая XFA.</summary>
    public enum XfaKind { None, Hybrid, Dynamic }

    /// <summary>
    /// Заполненная форма с одним текстовым полем [100 600 400 640] и значением
    /// в /V, /AP и (если есть XFA) в пакете datasets — так устроены бланки,
    /// сохранённые Adobe. Hybrid — /XFA без NeedsRendering (FPDF_GetFormType
    /// = 3), Dynamic — NeedsRendering и ни одного AcroForm-виджета (= 2).
    /// </summary>
    public static byte[] BuildXfaForm(XfaKind kind, string value)
    {
        var template =
            "<template xmlns=\"http://www.xfa.org/schema/xfa-template/2.8/\">" +
            "<subform name=\"form1\" layout=\"tb\"><pageSet><pageArea name=\"Page1\">" +
            "<contentArea x=\"0pt\" y=\"0pt\" w=\"612pt\" h=\"792pt\"/></pageArea></pageSet>" +
            "<subform name=\"Page1\"><field name=\"Name\" x=\"100pt\" y=\"152pt\" w=\"300pt\" h=\"40pt\">" +
            "<ui><textEdit/></ui></field></subform></subform></template>";
        var datasets =
            "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><xfa:data>" +
            $"<form1><Page1><Name>{value}</Name></Page1></form1></xfa:data></xfa:datasets>";
        var appearance = $"/Tx BMC q BT /Helv 12 Tf 0 g 2 14 Td ({value}) Tj ET Q EMC";
        var widgets = kind != XfaKind.Dynamic;
        var xfa = kind == XfaKind.None
            ? ""
            : " /XFA [(preamble) 7 0 R (template) 8 0 R (datasets) 9 0 R (postamble) 10 0 R]";

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R" + (kind == XfaKind.Dynamic ? " /NeedsRendering true" : "") +
            $" /AcroForm << /Fields [{(widgets ? "4 0 R" : "")}] /DA (/Helv 12 Tf 0 g)" +
            $" /DR << /Font << /Helv 5 0 R >> >>{xfa} >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" + (widgets ? " /Annots [4 0 R]" : "") +
            " /Resources << /Font << /Helv 5 0 R >> >> >>",
            // Имя поля — SOM-путь, как у полей, созданных из XFA-шаблона.
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (form1[0].Page1[0].Name[0]) /V ({value}) " +
            "/Rect [100 600 400 640] /F 4 /DA (/Helv 12 Tf 0 g) /AP << /N 6 0 R >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 300 40] /Resources << /Font << /Helv 5 0 R >> >> " +
            $"/Length {appearance.Length} >>\nstream\n{appearance}\nendstream",
        };
        foreach (var packet in new[] { "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">", template, datasets, "</xdp:xdp>" })
            objects.Add($"<< /Length {packet.Length} >>\nstream\n{packet}\nendstream");

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

    public static string WriteXfaFormToTemp(string fileName, XfaKind kind, string value)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, BuildXfaForm(kind, value));
        return path;
    }

    /// <summary>
    /// Гибридная форма со всеми видами привязки XFA, как у бланков Adobe
    /// LiveCycle. Поля (прямоугольники в PDF-координатах, страница 612×792):
    /// <list type="bullet">
    /// <item>Page1: Name [100 700 400 730]; Vessel (global) [100 650 400 680];
    /// флажок Agree [100 600 130 630], items 1/0; переключатели Choice: A
    /// [100 550 130 580] = «alpha», B [200 550 230 580] = «beta»; Note
    /// (привязка none) [100 500 400 530];</item>
    /// <item>безымянная подформа (#subform[1]): Row[0] в области [100 450 400 480]
    /// со значением «first»; Row[1] [100 400 400 430]; Vessel (global)
    /// [100 350 400 380]; Code (dataRef $record.Extra.Code) [100 300 400 330].</item>
    /// </list>
    /// В datasets есть Page1/Name, Page1/Agree, Row «first» и пустой Vessel;
    /// остального нет — узлы создаются при записи.
    /// </summary>
    public static byte[] BuildRichHybrid()
    {
        const string template =
            "<template xmlns=\"http://www.xfa.org/schema/xfa-template/3.3/\"><subform name=\"form1\" layout=\"tb\">" +
            "<pageSet><pageArea name=\"Page1\"><contentArea x=\"0pt\" y=\"0pt\" w=\"612pt\" h=\"792pt\"/>" +
            "<field name=\"Footer\"><ui><textEdit/></ui></field></pageArea></pageSet>" +
            "<subform name=\"Page1\">" +
            "<field name=\"Name\"><ui><textEdit/></ui></field>" +
            "<field name=\"Vessel\"><ui><textEdit/></ui><bind match=\"global\"/></field>" +
            "<field name=\"Agree\"><ui><checkButton/></ui><items><integer>1</integer><integer>0</integer></items></field>" +
            "<exclGroup name=\"Choice\">" +
            "<field name=\"A\"><ui><checkButton shape=\"round\"/></ui><items><text>alpha</text></items></field>" +
            "<field name=\"B\"><ui><checkButton shape=\"round\"/></ui><items><text>beta</text></items></field>" +
            "</exclGroup>" +
            "<field name=\"Note\"><ui><textEdit/></ui><bind match=\"none\"/></field>" +
            "</subform>" +
            "<subform><area><field name=\"Row\"><ui><textEdit/></ui></field></area>" +
            "<field name=\"Row\"><ui><textEdit/></ui></field>" +
            "<field name=\"Vessel\"><ui><textEdit/></ui><bind match=\"global\"/></field>" +
            "<field name=\"Code\"><ui><textEdit/></ui><bind match=\"dataRef\" ref=\"$record.Extra.Code\"/></field>" +
            "</subform></subform></template>";
        const string datasets =
            "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><xfa:data><form1>" +
            "<Page1><Name/><Agree>0</Agree></Page1><Row>first</Row><Vessel/></form1></xfa:data></xfa:datasets>";

        static string Text(string name, string rect, string value = "") =>
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({name}) /V ({value}) /Rect [{rect}] /F 4 /DA (/Helv 12 Tf 0 g) >>";
        static string Radio(int parent, string rect, string state) =>
            $"<< /Type /Annot /Subtype /Widget /Parent {parent} 0 R /Rect [{rect}] /F 4 /AS /Off " +
            $"/MK << /CA (l) >> /DA (/ZaDb 0 Tf 0 g) /AP << /N << /{state} 13 0 R /Off 14 0 R >> >> >>";
        const string on = "0 g 5 5 20 20 re f";
        var objects = new List<string>
        {
            // 1 каталог: поля и XFA
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R 7 0 R 8 0 R 9 0 R 12 0 R 15 0 R 16 0 R 17 0 R 18 0 R] " +
            "/NeedAppearances true /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv 4 0 R /ZaDb 5 0 R >> >> " +
            "/XFA [(preamble) 19 0 R (template) 20 0 R (datasets) 21 0 R (postamble) 22 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Annots [6 0 R 7 0 R 8 0 R 10 0 R 11 0 R 12 0 R 15 0 R 16 0 R 17 0 R 18 0 R] " +
            "/Resources << /Font << /Helv 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats >>",
            Text("form1[0].Page1[0].Name[0]", "100 700 400 730"),
            Text("form1[0].Page1[0].Vessel[0]", "100 650 400 680"),
            "<< /Type /Annot /Subtype /Widget /FT /Btn /T (form1[0].Page1[0].Agree[0]) /V /Off /AS /Off " +
            "/Rect [100 600 130 630] /F 4 /MK << /CA (4) >> /DA (/ZaDb 0 Tf 0 g) /AP << /N << /1 13 0 R /Off 14 0 R >> >> >>",
            // 9: группа переключателей, 10–11: её виджеты
            "<< /FT /Btn /Ff 49152 /T (form1[0].Page1[0].Choice[0]) /V /Off /Kids [10 0 R 11 0 R] >>",
            Radio(9, "100 550 130 580", "alpha"),
            Radio(9, "200 550 230 580", "beta"),
            Text("form1[0].Page1[0].Note[0]", "100 500 400 530"),
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 30 30] /Length {on.Length} >>\nstream\n{on}\nendstream",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 30 30] /Length 0 >>\nstream\n\nendstream",
            Text("form1[0].#subform[1].Row[0]", "100 450 400 480", "first"),
            Text("form1[0].#subform[1].Row[1]", "100 400 400 430"),
            Text("form1[0].#subform[1].Vessel[0]", "100 350 400 380"),
            Text("form1[0].#subform[1].Code[0]", "100 300 400 330"),
        };
        foreach (var packet in new[] { "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">", template, datasets, "</xdp:xdp>" })
            objects.Add($"<< /Length {packet.Length} >>\nstream\n{packet}\nendstream");

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

    /// <summary>
    /// Двухстраничная гибридная форма. Страница 1: флажок Agree [100 600 130 630]
    /// и Vessel (global) [100 650 400 680]; страница 2: Name [100 700 400 730] и
    /// второй Vessel (global) там же, где на первой.
    /// </summary>
    public static byte[] BuildTwoPageHybrid()
    {
        const string template =
            "<template xmlns=\"http://www.xfa.org/schema/xfa-template/3.3/\"><subform name=\"form1\">" +
            "<subform name=\"Page1\">" +
            "<field name=\"Agree\"><ui><checkButton/></ui><items><integer>1</integer><integer>0</integer></items></field>" +
            "<field name=\"Vessel\"><ui><textEdit/></ui><bind match=\"global\"/></field></subform>" +
            "<subform name=\"Page2\"><field name=\"Name\"><ui><textEdit/></ui></field>" +
            "<field name=\"Vessel\"><ui><textEdit/></ui><bind match=\"global\"/></field></subform>" +
            "</subform></template>";
        const string datasets =
            "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><xfa:data><form1>" +
            "<Page1><Agree>0</Agree></Page1><Page2><Name/></Page2><Vessel/></form1></xfa:data></xfa:datasets>";
        static string Text(string name, string rect) =>
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({name}) /V () /Rect [{rect}] /F 4 /DA (/Helv 12 Tf 0 g) >>";
        const string on = "0 g 5 5 20 20 re f";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R 7 0 R 8 0 R 9 0 R] " +
            "/NeedAppearances true /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv 5 0 R /ZaDb 10 0 R >> >> " +
            "/XFA [(preamble) 13 0 R (template) 14 0 R (datasets) 15 0 R (postamble) 16 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [6 0 R 7 0 R] /Resources << /Font << /Helv 5 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [8 0 R 9 0 R] /Resources << /Font << /Helv 5 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Annot /Subtype /Widget /FT /Btn /T (form1[0].Page1[0].Agree[0]) /V /Off /AS /Off " +
            "/Rect [100 600 130 630] /F 4 /MK << /CA (4) >> /DA (/ZaDb 0 Tf 0 g) /AP << /N << /1 11 0 R /Off 12 0 R >> >> >>",
            Text("form1[0].Page1[0].Vessel[0]", "100 650 400 680"),
            Text("form1[0].Page2[0].Name[0]", "100 700 400 730"),
            Text("form1[0].Page2[0].Vessel[0]", "100 650 400 680"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 30 30] /Length {on.Length} >>\nstream\n{on}\nendstream",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 30 30] /Length 0 >>\nstream\n\nendstream",
        };
        foreach (var packet in new[] { "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">", template, datasets, "</xdp:xdp>" })
            objects.Add($"<< /Length {packet.Length} >>\nstream\n{packet}\nendstream");

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

    public static string WriteTwoPageHybridToTemp(string fileName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, BuildTwoPageHybrid());
        return path;
    }

    public static string WriteRichHybridToTemp(string fileName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, BuildRichHybrid());
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
