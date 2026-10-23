using System.Text;
using System.Text.Json;

namespace NexusPdf.Pdf.Abstractions.TextBlocks;

/// <summary>
/// Скрытое описание блока для метки в PDF: рамка, режимы, абзацы и стили
/// фрагментов с их длинами — БЕЗ самого текста. Текст собирается из
/// записанных объектов страницы: при вымарывании или стирании части объектов
/// копия текста в метке сохранила бы стёртое.
///
/// Метку может подделать кто угодно, поэтому разбор строг: любое отклонение —
/// null («это не наш целый блок»), никаких исключений наружу.
/// </summary>
public static class TextBlockCodec
{
    public const int FormatVersion = 1;

    private const int MaxParagraphs = 10_000;
    private const int MaxRuns = 100_000;
    private const int MaxTextLength = 1_000_000;
    private const double MaxCoordinate = 100_000;
    private const double MaxIndent = 1_000;

    public static string EncodeStructure(TextBlockOverlay block)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("V", FormatVersion);
            w.WriteNumber("X", block.XPt);
            w.WriteNumber("Y", block.YPt);
            w.WriteNumber("W", block.WidthPt);
            w.WriteNumber("H", block.HeightPt);
            w.WriteNumber("R", block.RotationDegrees);
            w.WriteString("WM", block.WidthMode.ToString());
            w.WriteBoolean("AH", block.AutoHeight);
            w.WriteBoolean("SF", block.ShrinkToFit);
            // Новые ключи пишутся только не по умолчанию: метка без них — та же,
            // что писали прежние версии.
            if (block.VerticalAlign != TextVerticalAlign.Top) w.WriteString("VA", block.VerticalAlign.ToString());
            w.WriteStartArray("P");
            foreach (var paragraph in block.Paragraphs)
            {
                w.WriteStartObject();
                w.WriteString("A", paragraph.Align.ToString());
                w.WriteNumber("LS", paragraph.LineSpacing);
                if (paragraph.FirstLineIndentPt != 0) w.WriteNumber("FI", paragraph.FirstLineIndentPt);
                if (paragraph.LeftIndentPt != 0) w.WriteNumber("LI", paragraph.LeftIndentPt);
                // Переводы строки внутри абзаца в PDF не пишутся (глифа нет) —
                // их места хранятся здесь.
                var text = paragraph.Text;
                if (text.Contains(TextBlockText.LineBreak))
                {
                    w.WriteStartArray("LB");
                    for (var i = 0; i < text.Length; i++)
                    {
                        if (text[i] == TextBlockText.LineBreak) w.WriteNumberValue(i);
                    }
                    w.WriteEndArray();
                }
                w.WriteStartArray("Runs");
                foreach (var run in paragraph.Runs)
                {
                    w.WriteStartObject();
                    w.WriteNumber("L", run.Text.Length);
                    w.WriteStartObject("S");
                    var s = run.Style;
                    w.WriteString("F", s.FontFamily);
                    w.WriteNumber("Sz", s.FontSizePt);
                    w.WriteBoolean("B", s.Bold);
                    w.WriteBoolean("I", s.Italic);
                    w.WriteBoolean("U", s.Underline);
                    w.WriteBoolean("St", s.Strike);
                    w.WriteNumber("C", s.ColorArgb);
                    w.WriteString("Bl", s.Baseline.ToString());
                    w.WriteNumber("LSp", s.LetterSpacingPercent);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Блок из описания и записанных кусков текста. null — описание испорчено
    /// или куски не покрывают текст абзацев ровно (дыра, перекрытие, лишнее).
    /// </summary>
    public static TextBlockOverlay? Decode(string json, IReadOnlyList<TextPiece> pieces, Guid id)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Int(root, "V") != FormatVersion)
                return null;

            var x = Number(root, "X", -MaxCoordinate, MaxCoordinate);
            var y = Number(root, "Y", -MaxCoordinate, MaxCoordinate);
            var width = Number(root, "W", 0, MaxCoordinate);
            var height = Number(root, "H", 0, MaxCoordinate);
            var rotation = Number(root, "R", -36_000, 36_000);
            var mode = Enum<TextBlockWidthMode>(root, "WM");
            var autoHeight = root.GetProperty("AH").GetBoolean();
            var shrink = root.GetProperty("SF").GetBoolean();
            var vertical = root.TryGetProperty("VA", out _) ? Enum<TextVerticalAlign>(root, "VA") : TextVerticalAlign.Top;

            var paragraphsJson = root.GetProperty("P");
            if (paragraphsJson.ValueKind != JsonValueKind.Array || paragraphsJson.GetArrayLength() > MaxParagraphs)
                return null;

            var layouts = new List<(TextAlign Align, double Spacing, double FirstIndent, double LeftIndent,
                List<(int Length, TextStyle Style)> Runs, List<int> Breaks)>();
            var runCount = 0;
            long totalLength = 0;
            foreach (var p in paragraphsJson.EnumerateArray())
            {
                var runs = new List<(int, TextStyle)>();
                var runsJson = p.GetProperty("Runs");
                if (runsJson.ValueKind != JsonValueKind.Array) return null;
                foreach (var r in runsJson.EnumerateArray())
                {
                    if (++runCount > MaxRuns) return null;
                    var length = Int(r, "L");
                    if (length < 0 || length > MaxTextLength) return null;
                    totalLength += length;
                    if (totalLength > MaxTextLength) return null;
                    runs.Add((length, Style(r.GetProperty("S"))));
                }
                if (runs.Count == 0) return null;
                var breaks = new List<int>();
                if (p.TryGetProperty("LB", out var lb))
                {
                    if (lb.ValueKind != JsonValueKind.Array || lb.GetArrayLength() > MaxTextLength) return null;
                    foreach (var b in lb.EnumerateArray())
                    {
                        var at = b.GetInt32();
                        if (at < 0 || (breaks.Count > 0 && at <= breaks[^1])) return null;
                        breaks.Add(at);
                    }
                }
                layouts.Add((Enum<TextAlign>(p, "A"), Number(p, "LS", 0.5, 5),
                    Optional(p, "FI", -MaxIndent, MaxIndent), Optional(p, "LI", -MaxIndent, MaxIndent), runs, breaks));
            }

            var texts = Assemble(layouts.Select(l => (l.Runs.Sum(r => r.Length), l.Breaks)).ToList(), pieces);
            if (texts == null) return null;

            var paragraphs = new List<TextParagraph>(layouts.Count);
            for (var i = 0; i < layouts.Count; i++)
            {
                var offset = 0;
                var runs = new List<TextRun>();
                foreach (var (length, style) in layouts[i].Runs)
                {
                    runs.Add(new TextRun(texts[i].Substring(offset, length), style));
                    offset += length;
                }
                paragraphs.Add(new TextParagraph(runs, layouts[i].Align, layouts[i].Spacing,
                    layouts[i].FirstIndent, layouts[i].LeftIndent));
            }
            return new TextBlockOverlay(x, y, width, height, rotation, mode, autoHeight, shrink, paragraphs, id, vertical);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
                                       or FormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Тексты абзацев из кусков; null — куски не покрывают абзацы ровно.</summary>
    private static List<string>? Assemble(IReadOnlyList<(int Length, List<int> Breaks)> paragraphs, IReadOnlyList<TextPiece> pieces)
    {
        var byParagraph = new List<TextPiece>[paragraphs.Count];
        foreach (var piece in pieces)
        {
            if (piece.Paragraph < 0 || piece.Paragraph >= paragraphs.Count || piece.Start < 0 ||
                piece.Text.Contains(TextBlockText.LineBreak))
                return null;
            (byParagraph[piece.Paragraph] ??= new List<TextPiece>()).Add(piece);
        }
        var texts = new List<string>(paragraphs.Count);
        for (var i = 0; i < paragraphs.Count; i++)
        {
            var (length, breaks) = paragraphs[i];
            var builder = new StringBuilder(length);
            var nextBreak = 0;
            void AppendBreaks()
            {
                while (nextBreak < breaks.Count && breaks[nextBreak] == builder.Length)
                {
                    builder.Append(TextBlockText.LineBreak);
                    nextBreak++;
                }
            }
            var ordered = byParagraph[i]?.OrderBy(p => p.Start).ToList() ?? [];
            for (var k = 0; k < ordered.Count; k++)
            {
                AppendBreaks();
                var piece = ordered[k];
                if (piece.Start != builder.Length) return null; // дыра или перекрытие
                if (piece.Text.Length > 0)
                {
                    builder.Append(piece.Text);
                    continue;
                }
                // Пустой кусок — объект из одних пробелов (их PDFium в текст не
                // выдаёт): пробелы до следующего куска, перевода строки или конца.
                var end = k + 1 < ordered.Count ? ordered[k + 1].Start : length;
                if (nextBreak < breaks.Count) end = Math.Min(end, breaks[nextBreak]);
                if (end <= builder.Length || end > length) return null;
                builder.Append(' ', end - builder.Length);
            }
            AppendBreaks();
            if (builder.Length != length || nextBreak != breaks.Count) return null;
            texts.Add(builder.ToString());
        }
        return texts;
    }

    private static TextStyle Style(JsonElement s)
    {
        var family = s.GetProperty("F").GetString();
        if (string.IsNullOrWhiteSpace(family) || family.Length > 256)
            throw new FormatException("Недопустимое имя шрифта.");
        return new TextStyle(
            family,
            Number(s, "Sz", 1, 1638),
            Bool(s, "B"), Bool(s, "I"), Bool(s, "U"), Bool(s, "St"),
            s.TryGetProperty("C", out var c) ? c.GetUInt32() : 0xFF000000,
            s.TryGetProperty("Bl", out _) ? Enum<BaselineShift>(s, "Bl") : BaselineShift.None,
            s.TryGetProperty("LSp", out _) ? Number(s, "LSp", -50, 500) : 0);
    }

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.GetBoolean();

    private static int Int(JsonElement e, string name) => e.GetProperty(name).GetInt32();

    private static double Number(JsonElement e, string name, double min, double max)
    {
        var value = e.GetProperty(name).GetDouble();
        if (!double.IsFinite(value) || value < min || value > max)
            throw new FormatException($"Значение {name} вне допустимого.");
        return value;
    }

    private static double Optional(JsonElement e, string name, double min, double max) =>
        e.TryGetProperty(name, out _) ? Number(e, name, min, max) : 0;

    private static T Enum<T>(JsonElement e, string name) where T : struct, System.Enum
    {
        var text = e.GetProperty(name).GetString();
        return System.Enum.TryParse<T>(text, ignoreCase: false, out var value) && System.Enum.IsDefined(value)
               && !int.TryParse(text, out _)
            ? value
            : throw new FormatException($"Недопустимое значение {name}.");
    }
}
