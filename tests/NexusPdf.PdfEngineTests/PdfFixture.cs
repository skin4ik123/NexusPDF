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

    public static string WriteToTemp(string fileName, params PageSpec[] pages)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, Build(pages));
        return path;
    }
}
