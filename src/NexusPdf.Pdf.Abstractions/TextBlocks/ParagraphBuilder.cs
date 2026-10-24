using System.Text;

namespace NexusPdf.Pdf.Abstractions.TextBlocks;

/// <summary>
/// Абзац текста страницы: его объекты, линии подчёркивания и зачёркивания,
/// которые снимаются вместе с ним, рамка (отображаемые пункты) и блок, в
/// который он превращается. Block = null — абзац взять нельзя, причина —
/// ключ словаря RefusedKey. SubstitutedFonts — шрифты документа, которых нет
/// в системе, и чем они будут написаны.
/// </summary>
public sealed record DetectedParagraph(
    IReadOnlyList<PageTextObject> Objects, IReadOnlyList<PageLineObject> Decorations, OverlayBox BoundsPt,
    TextBlockOverlay? Block, string? RefusedKey, IReadOnlyList<(string Document, string Written)> SubstitutedFonts);

/// <summary>
/// Абзацы из текстовых объектов страницы. Строка — объекты с общей базовой
/// линией (±0,3 кегля) без большого разрыва (больше двух кеглей — другая
/// колонка); абзац — соседние строки с одинаковым шагом (±20 %), одного
/// поворота, с общим краем или центром. Блок из абзаца стоит там же, где
/// текст: первая базовая линия, левый край, шаг строк, отступы — прежние.
/// </summary>
public static class ParagraphBuilder
{
    private const double AngleTolerance = 1;
    private const double BaselineShare = 0.3;
    private const double ColumnGapShare = 2;
    private const double SpaceGapShare = 0.15;
    private const double OverlapShare = 0.3;
    private const double PitchTolerance = 0.2;
    private const double MinPitchShare = 0.6;
    private const double MaxPitchShare = 2.5;
    private const double SizeRatio = 1.25;
    private const double IndentShare = 4;
    private const double SpaceShare = 0.25;
    private const double DecorationThicknessShare = 0.12;
    private const double UnderlineDepthShare = 0.25;
    private const double DecorationReachPt = 2;
    private const double WidthSlackPt = 0.5;
    private const double CellInsetPt = 1;

    /// <summary>Объект в осях строки: U — вдоль текста, V — базовая линия (вниз — больше).</summary>
    private sealed class Item(PageTextObject source, double u, double v, double width, double size)
    {
        public PageTextObject Source { get; } = source;
        public double U { get; } = u;
        public double V { get; } = v;
        public double Width { get; } = width;
        public double Size { get; } = size;
        public double End => U + Width;
        public bool Blank => string.IsNullOrWhiteSpace(Source.Text);
    }

    private sealed class Line(List<Item> items)
    {
        public List<Item> Items { get; } = items;
        public double V { get; set; }
        public double Size { get; set; }
        public double Left { get; set; }
        public double Right { get; set; }
        public bool Overlap { get; set; }
        public double Centre => (Left + Right) / 2;
    }

    public static IReadOnlyList<DetectedParagraph> Build(PageTextLayout layout, FormTargetIndex cells, IFontProvider fonts)
    {
        var result = new List<DetectedParagraph>();
        var objects = layout.Objects
            .Where(o => o.Text.Length > 0 && double.IsFinite(o.OriginXPt) && double.IsFinite(o.OriginYPt) &&
                        double.IsFinite(o.AngleDeg))
            .ToList();
        foreach (var (angle, group) in ByAngle(objects))
        {
            var lines = BuildLines(group.Select(o => ToItem(o, angle)).ToList());
            foreach (var paragraph in GroupLines(lines))
                result.Add(Describe(paragraph, angle, layout.Lines, cells, fonts));
        }
        return result;
    }

    // ----- строки и абзацы -----

    private static IEnumerable<(double Angle, List<PageTextObject> Objects)> ByAngle(List<PageTextObject> objects)
    {
        var sorted = objects.OrderBy(o => Normalize(o.AngleDeg)).ToList();
        var i = 0;
        while (i < sorted.Count)
        {
            var anchor = Normalize(sorted[i].AngleDeg);
            var j = i;
            while (j < sorted.Count && Normalize(sorted[j].AngleDeg) - anchor <= AngleTolerance) j++;
            var group = sorted.GetRange(i, j - i);
            var angle = group.Average(o => Normalize(o.AngleDeg));
            yield return (Math.Abs(angle) < 0.01 ? 0 : angle, group);
            i = j;
        }
    }

    /// <summary>Угол в (−180, 180].</summary>
    private static double Normalize(double degrees)
    {
        var a = degrees % 360;
        if (a > 180) a -= 360;
        if (a <= -180) a += 360;
        return a;
    }

    private static Item ToItem(PageTextObject o, double angle)
    {
        var (cos, sin) = CosSin(angle);
        var width = o.CharAdvancesPt.Count == o.Text.Length ? o.CharAdvancesPt.Sum() : 0;
        if (!(width > 0)) width = angle == 0 ? o.BoundsPt.WidthPt : 0;
        var size = o.FontSizePt > 0 ? o.FontSizePt : Math.Max(1, Math.Min(o.BoundsPt.WidthPt, o.BoundsPt.HeightPt));
        return new Item(o, o.OriginXPt * cos + o.OriginYPt * sin, -o.OriginXPt * sin + o.OriginYPt * cos, width, size);
    }

    private static (double Cos, double Sin) CosSin(double degrees)
    {
        var a = degrees * Math.PI / 180;
        return (Math.Cos(a), Math.Sin(a));
    }

    /// <summary>Строки: общая базовая линия, по порядку слева направо; большой разрыв — другая колонка.</summary>
    private static List<Line> BuildLines(List<Item> items)
    {
        var lines = new List<Line>();
        var rows = new List<List<Item>>();
        foreach (var item in items.OrderBy(i => i.V).ThenBy(i => i.U))
        {
            if (rows.Count > 0)
            {
                var row = rows[^1];
                var anchor = row[0];
                if (Math.Abs(item.V - anchor.V) <= BaselineShare * Math.Max(item.Size, anchor.Size))
                {
                    row.Add(item);
                    continue;
                }
            }
            rows.Add([item]);
        }

        foreach (var row in rows)
        {
            row.Sort((a, b) => a.U.CompareTo(b.U));
            var current = new List<Item> { row[0] };
            var overlap = false;
            for (var k = 1; k < row.Count; k++)
            {
                var (prev, next) = (current[^1], row[k]);
                var size = Math.Max(prev.Size, next.Size);
                if (next.U - prev.End > ColumnGapShare * size)
                {
                    Close(current, overlap);
                    current = [next];
                    overlap = false;
                    continue;
                }
                if (!prev.Blank && !next.Blank && next.U < prev.End - OverlapShare * size)
                    overlap = true;
                current.Add(next);
            }
            Close(current, overlap);
        }
        return lines;

        void Close(List<Item> members, bool overlap)
        {
            var line = new Line(members) { Overlap = overlap };
            if (Measure(line)) lines.Add(line);
        }
    }

    /// <summary>Края строки по видимым буквам; строка из одних пробелов — не строка.</summary>
    private static bool Measure(Line line)
    {
        double left = double.MaxValue, right = double.MinValue;
        foreach (var item in line.Items)
        {
            var u = item.U;
            var advances = item.Source.CharAdvancesPt;
            for (var k = 0; k < item.Source.Text.Length; k++)
            {
                var advance = k < advances.Count ? advances[k] : 0;
                if (!char.IsWhiteSpace(item.Source.Text[k]) && !char.IsControl(item.Source.Text[k]))
                {
                    left = Math.Min(left, u);
                    right = Math.Max(right, u + advance);
                }
                u += advance;
            }
        }
        if (left > right) return false;
        var main = line.Items.Where(i => !i.Blank).DefaultIfEmpty(line.Items[0]).MaxBy(i => i.Size)!;
        line.V = main.V;
        line.Size = line.Items.Max(i => i.Size);
        line.Left = left;
        line.Right = right;
        return true;
    }

    private static List<List<Line>> GroupLines(List<Line> lines)
    {
        var paragraphs = new List<List<Line>>();
        var open = new List<List<Line>>();
        foreach (var line in lines.OrderBy(l => l.V).ThenBy(l => l.Left))
        {
            open.RemoveAll(p => line.V - p[^1].V > MaxPitchShare * Math.Max(line.Size, p[^1].Size));
            List<Line>? best = null;
            var bestDistance = double.MaxValue;
            foreach (var paragraph in open)
            {
                var distance = line.V - paragraph[^1].V;
                if (distance < bestDistance && Accepts(paragraph, line))
                {
                    best = paragraph;
                    bestDistance = distance;
                }
            }
            if (best != null)
            {
                best.Add(line);
                continue;
            }
            var created = new List<Line> { line };
            paragraphs.Add(created);
            open.Add(created);
        }
        return paragraphs;
    }

    private static bool Accepts(List<Line> paragraph, Line line)
    {
        var last = paragraph[^1];
        var size = Math.Max(last.Size, line.Size);
        if (Math.Max(last.Size, line.Size) > SizeRatio * Math.Min(last.Size, line.Size)) return false;
        var distance = line.V - last.V;
        if (distance < MinPitchShare * size || distance > MaxPitchShare * size) return false;
        if (!(line.Left < last.Right && last.Left < line.Right)) return false;
        if (paragraph.Count >= 2)
        {
            var pitch = paragraph[1].V - paragraph[0].V;
            if (Math.Abs(distance - pitch) > PitchTolerance * pitch) return false;
        }
        var tolerance = Math.Max(1, 0.25 * size);
        return Math.Abs(line.Left - last.Left) <= tolerance ||
               Math.Abs(line.Right - last.Right) <= tolerance ||
               Math.Abs(line.Centre - last.Centre) <= tolerance ||
               (paragraph.Count == 1 && Math.Abs(line.Left - last.Left) <= IndentShare * size);
    }

    // ----- абзац → блок -----

    private static DetectedParagraph Describe(List<Line> lines, double angle, IReadOnlyList<PageLineObject> thinLines,
        FormTargetIndex cells, IFontProvider fonts)
    {
        var items = lines.SelectMany(l => l.Items).ToList();
        var objects = items.Select(i => i.Source).ToList();
        var bounds = Union(objects.Select(o => o.BoundsPt));

        var refused =
            objects.Any(o => o.InForm) ? "TextParagraphInForm"
            : objects.Any(o => o.Type3) ? "TextParagraphType3"
            : objects.Any(o => o.Invisible) ? "TextParagraphInvisible"
            : objects.Any(o => !Supported(o.Text)) ? "TextBlockUnsupportedScript"
            : lines.Any(l => l.Overlap) ? "TextParagraphOverlap"
            : null;
        if (refused != null)
            return new DetectedParagraph(objects, [], bounds, null, refused, []);

        var cell = angle == 0 ? cells.CellAround(bounds) : null;
        var decorations = Decorations(lines, angle, thinLines, cell);
        var substituted = new List<(string, string)>();
        var styles = items.ToDictionary(i => i, i => StyleOf(i.Source, fonts, substituted));

        // Выравнивание и отступы — по краям строк.
        var n = lines.Count;
        var sizeOf = lines.Max(l => l.Size);
        var edge = Math.Max(0.5, 0.05 * sizeOf);
        var near = Math.Max(1, 0.25 * sizeOf);
        var maxRight = lines.Max(l => l.Right);
        var restLeft = n >= 2 ? lines[1].Left : lines[0].Left;
        var allLeft = lines.All(l => Math.Abs(l.Left - lines[0].Left) <= near);
        var restAligned = lines.Skip(1).All(l => Math.Abs(l.Left - restLeft) <= near);
        var align = TextAlign.Left;
        if (n >= 2 && !allLeft && lines.All(l => Math.Abs(l.Right - maxRight) <= edge))
            align = TextAlign.Right;
        else if (n >= 2 && !allLeft && lines.All(l => Math.Abs(l.Centre - lines[0].Centre) <= near))
            align = TextAlign.Center;
        else if (n >= 3 && restAligned && lines.Take(n - 1).All(l => Math.Abs(l.Right - maxRight) <= edge))
            align = TextAlign.Justify;

        double frameLeft, firstIndent = 0, leftIndent = 0;
        if (n >= 2 && align is TextAlign.Left or TextAlign.Justify)
        {
            frameLeft = Math.Min(lines[0].Left, restLeft);
            firstIndent = lines[0].Left - restLeft;
            leftIndent = restLeft - frameLeft;
        }
        else
        {
            frameLeft = lines.Min(l => l.Left);
        }

        var runs = Runs(lines, styles, decorations, maxRight);
        var spacing = n >= 2
            ? Math.Clamp((lines[^1].V - lines[0].V) / (n - 1) / NaturalHeight(styles[lines[0].Items[0]], fonts), 0.5, 5)
            : 1.15;

        if (cell is { } box)
        {
            var frame = new OverlayBox(box.XPt + CellInsetPt, box.YPt + CellInsetPt,
                Math.Max(1, box.WidthPt - 2 * CellInsetPt), Math.Max(1, box.HeightPt - 2 * CellInsetPt));
            if (n == 1)
            {
                var (leftGap, rightGap) = (lines[0].Left - box.XPt, box.XPt + box.WidthPt - lines[0].Right);
                align = Math.Abs(leftGap - rightGap) <= Math.Max(2, 0.1 * box.WidthPt) ? TextAlign.Center
                    : rightGap < leftGap ? TextAlign.Right : TextAlign.Left;
                firstIndent = leftIndent = 0;
            }
            var (topGap, bottomGap) = (bounds.YPt - box.YPt, box.YPt + box.HeightPt - (bounds.YPt + bounds.HeightPt));
            var vertical = Math.Abs(topGap - bottomGap) <= 0.25 * box.HeightPt ? TextVerticalAlign.Center
                : topGap < bottomGap ? TextVerticalAlign.Top : TextVerticalAlign.Bottom;
            var inCell = new TextBlockOverlay(frame.XPt, frame.YPt, frame.WidthPt, frame.HeightPt, 0,
                TextBlockWidthMode.Fixed, AutoHeight: false, ShrinkToFit: true,
                [new TextParagraph(runs, align, spacing, firstIndent, leftIndent)], Guid.NewGuid(), vertical);
            return new DetectedParagraph(objects, decorations.Select(d => d.Source).Distinct().ToList(), bounds, inCell, null,
                substituted.Distinct().ToList());
        }

        var mode = n >= 2 ? TextBlockWidthMode.Fixed : TextBlockWidthMode.AutoWidth;
        var width = maxRight - frameLeft + 2 * TextBlockLayout.PaddingPt + (n >= 2 ? WidthSlackPt : 0);
        var draft = new TextBlockOverlay(0, 0, width, 1, 0, mode, AutoHeight: true, ShrinkToFit: false,
            [new TextParagraph(runs, align, spacing, firstIndent, leftIndent)], Guid.NewGuid());
        var laid = TextBlockLayout.Compute(draft, fonts);
        var (w, h) = (laid.WidthPt, laid.HeightPt);
        // Угол рамки в осях строки: первая базовая линия — там же, где была.
        var u0 = frameLeft - TextBlockLayout.PaddingPt;
        var v0 = lines[0].V - (laid.Lines.Count > 0 ? laid.Lines[0].BaselinePt : 0);
        var (cos, sin) = CosSin(angle);
        var (cu, cv) = (u0 + w / 2, v0 + h / 2);
        var (cx, cy) = (cu * cos - cv * sin, cu * sin + cv * cos);
        var block = draft with
        {
            XPt = cx - w / 2, YPt = cy - h / 2, WidthPt = w, HeightPt = h,
            RotationDegrees = OverlayGeometry.NormalizeDegrees(angle),
        };
        return new DetectedParagraph(objects, decorations.Select(d => d.Source).Distinct().ToList(), bounds, block, null,
            substituted.Distinct().ToList());
    }

    private static bool Supported(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var code = char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text, i) : text[i];
            if (!TextBlockText.IsSupported(code)) return false;
            if (code > 0xFFFF) i++;
        }
        return true;
    }

    private static TextStyle StyleOf(PageTextObject o, IFontProvider fonts, List<(string, string)> substituted)
    {
        var document = FontSubstitution.DisplayName(o.FontName);
        var family = FontSubstitution.FamilyFor(o.FontName, fonts)
                     ?? (fonts.Resolve(FontCatalog.DefaultFamily, false, false)?.Family
                         ?? fonts.FallbackFamilies.FirstOrDefault() ?? FontCatalog.DefaultFamily);
        if (document.Length > 0 && Compact(document) != Compact(family))
            substituted.Add((document, family));
        var bold = o.Bold || NameSays(o.FontName, "Bold", "Black", "Heavy", "Semibold", "Demi");
        var italic = o.Italic || NameSays(o.FontName, "Italic", "Oblique");
        return new TextStyle(family, Math.Round(o.FontSizePt, 2), bold, italic) { ColorArgb = o.ColorArgb };
    }

    private static bool NameSays(string name, params string[] words) =>
        words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static string Compact(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static double NaturalHeight(TextStyle style, IFontProvider fonts)
    {
        if (fonts.Resolve(style.FontFamily, style.Bold, style.Italic) is { } face)
        {
            var font = fonts.Load(face.Ref);
            var height = (font.Ascender - font.Descender + font.LineGap) * style.FontSizePt / font.UnitsPerEm;
            if (height > 0) return height;
        }
        return style.FontSizePt * 1.2;
    }

    // ----- линии под текстом -----

    private sealed record Decoration(PageLineObject Source, int Line, double From, double To, bool Strike);

    /// <summary>
    /// Подчёркивание и зачёркивание, нарисованные линиями: тонкие (до 0,12
    /// кегля), под базовой линией (до 0,25 кегля) или на середине строчных
    /// букв, не шире текста строки больше чем на 2 пт. Края клетки — не они.
    /// </summary>
    private static List<Decoration> Decorations(List<Line> lines, double angle, IReadOnlyList<PageLineObject> thinLines,
        OverlayBox? cell)
    {
        var found = new List<Decoration>();
        var (cos, sin) = CosSin(angle);
        foreach (var thin in thinLines)
        {
            var b = thin.BoundsPt;
            var corners = new[] { (b.XPt, b.YPt), (b.XPt + b.WidthPt, b.YPt), (b.XPt, b.YPt + b.HeightPt), (b.XPt + b.WidthPt, b.YPt + b.HeightPt) }
                .Select(p => (U: p.Item1 * cos + p.Item2 * sin, V: -p.Item1 * sin + p.Item2 * cos)).ToList();
            var (u0, u1) = (corners.Min(c => c.U), corners.Max(c => c.U));
            var (v0, v1) = (corners.Min(c => c.V), corners.Max(c => c.V));
            var centre = (v0 + v1) / 2;
            if (cell is { } box && (Math.Abs(centre - box.YPt) <= 1.5 || Math.Abs(centre - (box.YPt + box.HeightPt)) <= 1.5))
                continue;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (v1 - v0 > DecorationThicknessShare * line.Size) continue;
                if (u0 < line.Left - DecorationReachPt || u1 > line.Right + DecorationReachPt) continue;
                var depth = centre - line.V;
                if (depth >= -0.05 * line.Size && depth <= UnderlineDepthShare * line.Size)
                    found.Add(new Decoration(thin, i, u0, u1, Strike: false));
                else if (depth >= -0.45 * line.Size && depth <= -0.15 * line.Size)
                    found.Add(new Decoration(thin, i, u0, u1, Strike: true));
            }
        }
        return found;
    }

    // ----- фрагменты -----

    /// <summary>
    /// Текст абзаца фрагментами по объектам: пробелы — там, где между
    /// объектами зазор, а пробела нет; строки — через пробел, без него после
    /// дефиса, переводом строки, если перенос был намеренным.
    /// </summary>
    private static List<TextRun> Runs(List<Line> lines, Dictionary<Item, TextStyle> styles,
        List<Decoration> decorations, double frameRight)
    {
        var pieces = new List<(string Text, TextStyle Style)>();
        for (var li = 0; li < lines.Count; li++)
        {
            var line = lines[li];
            var marks = decorations.Where(d => d.Line == li).ToList();
            Item? previous = null;
            foreach (var item in line.Items)
            {
                var style = styles[item];
                if (previous != null && item.U - previous.End > SpaceGapShare * Math.Max(item.Size, previous.Size) &&
                    !EndsWithSpace(previous.Source.Text) && !char.IsWhiteSpace(item.Source.Text[0]))
                    pieces.Add((" ", Decorate(styles[previous], marks, (previous.End + item.U) / 2)));
                var u = item.U;
                var text = item.Source.Text;
                var advances = item.Source.CharAdvancesPt;
                for (var k = 0; k < text.Length; k++)
                {
                    var advance = k < advances.Count ? advances[k] : 0;
                    var ch = text[k];
                    if (ch == '﻿') { u += advance; continue; }
                    var value = char.IsControl(ch) ? " " : ch.ToString();
                    pieces.Add((value, Decorate(style, marks, u + advance / 2)));
                    u += advance;
                }
                previous = item;
            }

            if (li + 1 < lines.Count)
                pieces.Add(Join(line, lines[li + 1], styles, frameRight));
        }

        var runs = new List<TextRun>();
        foreach (var (text, style) in pieces)
        {
            if (text.Length == 0) continue;
            if (runs.Count > 0 && runs[^1].Style == style)
                runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
            else
                runs.Add(new TextRun(text, style));
        }
        return runs;
    }

    private static TextStyle Decorate(TextStyle style, List<Decoration> marks, double u)
    {
        var underline = marks.Any(m => !m.Strike && u >= m.From && u <= m.To);
        var strike = marks.Any(m => m.Strike && u >= m.From && u <= m.To);
        return underline || strike ? style with { Underline = underline, Strike = strike } : style;
    }

    private static (string Text, TextStyle Style) Join(Line line, Line next, Dictionary<Item, TextStyle> styles, double frameRight)
    {
        var last = line.Items[^1];
        var style = styles[last] with { Underline = false, Strike = false };
        var text = string.Concat(line.Items.Select(i => i.Source.Text)).TrimEnd();
        var nextText = string.Concat(next.Items.Select(i => i.Source.Text)).TrimStart();
        if (text.Length >= 2 && text[^1] is '-' or '­' && char.IsLetter(text[^2]) &&
            nextText.Length > 0 && char.IsLetter(nextText[0]))
            return ("", style);
        if (text.EndsWith(':') ||
            line.Right + SpaceShare * line.Size + FirstWordWidth(next) <= frameRight + WidthSlackPt)
            return (TextBlockText.LineBreak.ToString(), style);
        return (EndsWithSpace(string.Concat(line.Items.Select(i => i.Source.Text))) ? "" : " ", style);
    }

    /// <summary>Ширина первого слова строки — по ширинам букв.</summary>
    private static double FirstWordWidth(Line line)
    {
        var width = 0.0;
        var started = false;
        Item? previous = null;
        foreach (var item in line.Items)
        {
            if (started && previous != null && item.U - previous.End > SpaceGapShare * item.Size)
                return width;
            var advances = item.Source.CharAdvancesPt;
            for (var k = 0; k < item.Source.Text.Length; k++)
            {
                var blank = char.IsWhiteSpace(item.Source.Text[k]);
                if (blank && started) return width;
                if (blank) continue;
                started = true;
                width += k < advances.Count ? advances[k] : 0;
            }
            previous = item;
        }
        return width;
    }

    private static bool EndsWithSpace(string text) => text.Length > 0 && char.IsWhiteSpace(text[^1]);

    private static OverlayBox Union(IEnumerable<OverlayBox> boxes)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var b in boxes)
        {
            left = Math.Min(left, b.XPt);
            top = Math.Min(top, b.YPt);
            right = Math.Max(right, b.XPt + b.WidthPt);
            bottom = Math.Max(bottom, b.YPt + b.HeightPt);
        }
        return left > right ? default : new OverlayBox(left, top, right - left, bottom - top);
    }
}
