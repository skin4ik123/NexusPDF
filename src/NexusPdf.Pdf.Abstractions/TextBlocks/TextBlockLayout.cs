using System.Text;

namespace NexusPdf.Pdf.Abstractions.TextBlocks;

/// <summary>
/// Кусок строки одного шрифта и стиля — ровно то, что станет одним текстовым
/// объектом в PDF и одним GlyphRun на экране.
/// </summary>
/// <param name="Start">Смещение первого символа куска в тексте абзаца (UTF-16).</param>
/// <param name="AdvancesPt">Шаг после каждого символа UTF-16 (у второй половины суррогатной пары — 0).</param>
public sealed record LaidOutPiece(
    int Paragraph, int Start, string Text, FontFaceRef Face, string Family, TextStyle Style,
    double FontSizePt, double XPt, double BaselinePt, double WidthPt, IReadOnlyList<double> AdvancesPt, bool IsFallback);

/// <summary>Подчёркивание или зачёркивание — закрашенный прямоугольник.</summary>
public sealed record LaidOutDecoration(double XPt, double YPt, double WidthPt, double HeightPt, uint ColorArgb);

/// <summary>Строка блока. Length включает пробелы в конце и сам перевод строки, если он есть.</summary>
public sealed record LaidOutLine(
    int Paragraph, int Start, int Length, double TopPt, double BaselinePt, double HeightPt,
    IReadOnlyList<LaidOutPiece> Pieces);

/// <summary>
/// Результат вёрстки в координатах блока: начало — верхний левый угол рамки,
/// y вниз, до поворота. WidthPt/HeightPt — рамка после AutoWidth/AutoHeight.
/// </summary>
public sealed record LaidOutBlock(
    double WidthPt, double HeightPt, IReadOnlyList<LaidOutLine> Lines,
    IReadOnlyList<LaidOutDecoration> Decorations, bool Overflows, double Scale);

/// <summary>
/// Вёрстка текстового блока. Одна и та же для экрана, рамки и записи в PDF —
/// иначе текст, вписанный в клетку, после сохранения переносился бы иначе.
/// Ширины — из таблиц шрифта, без кернинга и лигатур (PDFium их тоже не
/// применяет).
/// </summary>
public static class TextBlockLayout
{
    public const double PaddingPt = 2;
    public const double MinimumFontSizePt = 4;
    private const double ScriptSizeFactor = 0.65;
    private const double ScriptShiftFactor = 0.33;
    private const double Epsilon = 1e-6;

    public static LaidOutBlock Compute(TextBlockOverlay block, IFontProvider fonts)
    {
        var result = Layout(block, fonts, 1.0);
        if (!block.ShrinkToFit || block.AutoHeight || !result.Overflows)
            return result;

        // Уменьшение всех размеров в одной пропорции шагом 0,5 пт крупнейшего,
        // но не мельче 4 пт у самого мелкого.
        var sizes = block.Paragraphs.SelectMany(p => p.Runs).Select(r => r.Style.FontSizePt).ToList();
        if (sizes.Count == 0) return result;
        var largest = sizes.Max();
        var smallest = sizes.Min();
        for (var size = largest - 0.5; ; size -= 0.5)
        {
            var scale = size / largest;
            if (smallest * scale < MinimumFontSizePt - Epsilon)
                return Layout(block, fonts, Math.Min(1.0, MinimumFontSizePt / smallest));
            result = Layout(block, fonts, scale);
            if (!result.Overflows)
                return result;
        }
    }

    // ----- Символы -----

    private sealed record Glyph(
        int Run, int Offset, int Units, int CodePoint, FontFaceInfo Face, TrueTypeFont Font, double Size,
        double BaseSize, double Raise, double Advance, double Spacing, bool IsFallback)
    {
        public bool IsSpace => CodePoint == ' ';
        public bool IsLineBreak => CodePoint == TextBlockText.LineBreak;
        public bool IsBreakAfter => CodePoint is '-' or '‐';
    }

    private sealed class FaceCache(IFontProvider fonts)
    {
        private readonly Dictionary<(int, bool, bool), FontFaceInfo?> _fallback = new();

        public FontFaceInfo Primary(TextStyle style) =>
            fonts.Resolve(style.FontFamily, style.Bold, style.Italic)
            ?? fonts.FallbackFamilies.Select(f => fonts.Resolve(f, style.Bold, style.Italic)).FirstOrDefault(f => f != null)
            ?? throw new InvalidOperationException("В системе нет ни одного шрифта для текста.");

        public FontFaceInfo? Fallback(int codePoint, bool bold, bool italic)
        {
            if (_fallback.TryGetValue((codePoint, bold, italic), out var cached)) return cached;
            FontFaceInfo? found = null;
            foreach (var family in fonts.FallbackFamilies)
            {
                var face = fonts.Resolve(family, bold, italic);
                if (face != null && fonts.Load(face.Ref).HasGlyph(codePoint))
                {
                    found = face;
                    break;
                }
            }
            _fallback[(codePoint, bold, italic)] = found;
            return found;
        }
    }

    private static List<Glyph> Glyphs(TextParagraph paragraph, IFontProvider fonts, FaceCache faces, double scale)
    {
        var glyphs = new List<Glyph>();
        var offset = 0;
        for (var r = 0; r < paragraph.Runs.Count; r++)
        {
            var run = paragraph.Runs[r];
            var style = run.Style;
            var baseSize = style.FontSizePt * scale;
            var size = style.Baseline == BaselineShift.None ? baseSize : baseSize * ScriptSizeFactor;
            var raise = style.Baseline switch
            {
                BaselineShift.Superscript => ScriptShiftFactor * baseSize,
                BaselineShift.Subscript => -ScriptShiftFactor * baseSize,
                _ => 0,
            };
            var spacing = style.LetterSpacingPercent / 100 * size;
            var primary = faces.Primary(style);
            var primaryFont = fonts.Load(primary.Ref);

            var text = run.Text;
            for (var i = 0; i < text.Length;)
            {
                var units = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
                var codePoint = units == 2 ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
                var face = primary;
                var font = primaryFont;
                var fallback = false;
                if (codePoint != TextBlockText.LineBreak && !primaryFont.HasGlyph(codePoint) &&
                    faces.Fallback(codePoint, style.Bold, style.Italic) is { } other)
                {
                    face = other;
                    font = fonts.Load(other.Ref);
                    fallback = true;
                }
                var advance = codePoint == TextBlockText.LineBreak
                    ? 0
                    : font.AdvanceWidth(codePoint) * size / font.UnitsPerEm + spacing;
                glyphs.Add(new Glyph(r, offset + i, units, codePoint, face, font, size, baseSize, raise,
                    advance, codePoint == TextBlockText.LineBreak ? 0 : spacing, fallback));
                i += units;
            }
            offset += text.Length;
        }
        return glyphs;
    }

    // ----- Строки -----

    private sealed record LineRange(int First, int End, bool Forced, bool LastOfParagraph);

    private static List<LineRange> Break(List<Glyph> glyphs, double available)
    {
        var lines = new List<LineRange>();
        var start = 0;
        while (true)
        {
            if (start >= glyphs.Count)
            {
                // Пустой абзац или перевод строки в самом конце — пустая строка.
                if (lines.Count == 0 || lines[^1].Forced)
                    lines.Add(new LineRange(start, start, false, true));
                break;
            }
            var width = 0.0;
            var lastBreak = -1;
            var end = glyphs.Count;
            var forced = false;
            for (var i = start; i < glyphs.Count; i++)
            {
                var g = glyphs[i];
                if (g.IsLineBreak)
                {
                    end = i + 1;
                    forced = true;
                    break;
                }
                if (g.IsSpace)
                {
                    // Пробелы «висят» за краем и перенос не вызывают.
                    width += g.Advance;
                    lastBreak = i + 1;
                    continue;
                }
                var candidate = width + g.Advance;
                if (candidate > available + Epsilon && i > start)
                {
                    end = lastBreak > start ? lastBreak : i;
                    break;
                }
                width = candidate;
                if (g.IsBreakAfter) lastBreak = i + 1;
            }
            lines.Add(new LineRange(start, end, forced, end >= glyphs.Count && !forced));
            start = end;
            if (start >= glyphs.Count && !forced) break;
        }
        return lines;
    }

    private static double VisualWidth(List<Glyph> glyphs, LineRange line)
    {
        var last = LastVisible(glyphs, line);
        var width = 0.0;
        for (var i = line.First; i <= last; i++) width += glyphs[i].Advance;
        return width;
    }

    private static int LastVisible(List<Glyph> glyphs, LineRange line)
    {
        var last = line.End - 1;
        while (last >= line.First && (glyphs[last].IsSpace || glyphs[last].IsLineBreak)) last--;
        return last;
    }

    // ----- Вёрстка -----

    private static LaidOutBlock Layout(TextBlockOverlay block, IFontProvider fonts, double scale)
    {
        var faces = new FaceCache(fonts);
        var fixedWidth = block.WidthMode == TextBlockWidthMode.Fixed;
        var available = fixedWidth ? Math.Max(0, block.WidthPt - 2 * PaddingPt) : double.PositiveInfinity;

        var paragraphs = new List<(TextParagraph Paragraph, List<Glyph> Glyphs, List<LineRange> Lines)>();
        var widest = 0.0;
        foreach (var paragraph in block.Paragraphs)
        {
            var glyphs = Glyphs(paragraph, fonts, faces, scale);
            var lines = Break(glyphs, available);
            foreach (var line in lines) widest = Math.Max(widest, VisualWidth(glyphs, line));
            paragraphs.Add((paragraph, glyphs, lines));
        }
        var alignWidth = fixedWidth ? available : widest;

        var result = new List<LaidOutLine>();
        var decorations = new List<LaidOutDecoration>();
        var top = PaddingPt;
        for (var p = 0; p < paragraphs.Count; p++)
        {
            var (paragraph, glyphs, lines) = paragraphs[p];
            foreach (var line in lines)
            {
                var (ascent, natural) = Metrics(paragraph, glyphs, line, fonts, faces, scale);
                var height = natural * paragraph.LineSpacing;
                var baseline = top + ascent;
                var pieces = Pieces(p, paragraph, glyphs, line, alignWidth, fixedWidth, baseline, decorations);
                var startOffset = line.First < glyphs.Count ? glyphs[line.First].Offset : paragraph.Text.Length;
                var endOffset = line.End < glyphs.Count ? glyphs[line.End].Offset : paragraph.Text.Length;
                result.Add(new LaidOutLine(p, startOffset, endOffset - startOffset, top, baseline, height, pieces));
                top += height;
            }
        }

        var content = top - PaddingPt;
        var width = fixedWidth ? block.WidthPt : widest + 2 * PaddingPt;
        var heightOut = block.AutoHeight ? content + 2 * PaddingPt : block.HeightPt;
        var overflows = !block.AutoHeight && content > block.HeightPt - 2 * PaddingPt + Epsilon;
        return new LaidOutBlock(width, heightOut, result, decorations, overflows, scale);
    }

    private static (double Ascent, double Natural) Metrics(
        TextParagraph paragraph, List<Glyph> glyphs, LineRange line, IFontProvider fonts, FaceCache faces, double scale)
    {
        double ascent = 0, natural = 0;
        var any = false;
        for (var i = line.First; i < line.End; i++)
        {
            var g = glyphs[i];
            Accumulate(g.Font, g.Size, g.Raise);
            any = true;
        }
        if (!any)
        {
            // Пустая строка — по стилю, в котором будет набираться текст.
            var style = paragraph.Runs.Count > 0 ? paragraph.Runs[^1].Style : new TextStyle(FontCatalog.DefaultFamily, 11);
            var font = fonts.Load(faces.Primary(style).Ref);
            Accumulate(font, style.FontSizePt * scale, 0);
        }
        return (ascent, natural);

        void Accumulate(TrueTypeFont font, double size, double raise)
        {
            var em = size / font.UnitsPerEm;
            ascent = Math.Max(ascent, font.Ascender * em + raise);
            natural = Math.Max(natural, (font.Ascender - font.Descender + font.LineGap) * em);
        }
    }

    private static List<LaidOutPiece> Pieces(
        int paragraphIndex, TextParagraph paragraph, List<Glyph> glyphs, LineRange line, double alignWidth,
        bool fixedWidth, double baseline, List<LaidOutDecoration> decorations)
    {
        var pieces = new List<LaidOutPiece>();
        var lastVisible = LastVisible(glyphs, line);
        var visual = VisualWidth(glyphs, line);

        var justify = paragraph.Align == TextAlign.Justify && fixedWidth && !line.LastOfParagraph && !line.Forced;
        var interior = 0;
        for (var i = line.First; i < lastVisible; i++)
        {
            if (glyphs[i].IsSpace) interior++;
        }
        var extra = justify && interior > 0 ? Math.Max(0, (alignWidth - visual) / interior) : 0;

        var x = PaddingPt + (paragraph.Align switch
        {
            TextAlign.Center => (alignWidth - visual) / 2,
            TextAlign.Right => alignWidth - visual,
            _ => 0,
        });
        var visualEnd = x + visual + extra * interior;

        var i0 = line.First;
        while (i0 < line.End)
        {
            if (glyphs[i0].IsLineBreak) { i0++; continue; }
            var first = glyphs[i0];
            var i1 = i0 + 1;
            // Кусок — подряд идущие символы одного фрагмента и шрифта; при
            // выравнивании по ширине каждый пробел — отдельный кусок.
            while (i1 < line.End && !glyphs[i1].IsLineBreak &&
                   glyphs[i1].Run == first.Run && ReferenceEquals(glyphs[i1].Face, first.Face) &&
                   !(extra > 0 && (glyphs[i1].IsSpace || glyphs[i1 - 1].IsSpace)))
                i1++;

            var text = new StringBuilder();
            var advances = new List<double>();
            var width = 0.0;
            for (var k = i0; k < i1; k++)
            {
                var g = glyphs[k];
                var advance = g.Advance + (g.IsSpace && k < lastVisible ? extra : 0);
                text.Append(char.ConvertFromUtf32(g.CodePoint));
                advances.Add(advance);
                if (g.Units == 2) advances.Add(0);
                width += advance;
            }
            var style = paragraph.Runs[first.Run].Style;
            var piece = new LaidOutPiece(paragraphIndex, first.Offset, text.ToString(), first.Face.Ref, first.Face.Family,
                style, first.Size, x, baseline - first.Raise, width, advances, first.IsFallback);
            pieces.Add(piece);
            Decorate(piece, first, visualEnd, decorations);
            x += width;
            i0 = i1;
        }
        return pieces;
    }

    private static void Decorate(LaidOutPiece piece, Glyph sample, double visualEnd, List<LaidOutDecoration> decorations)
    {
        var width = Math.Min(piece.XPt + piece.WidthPt, visualEnd) - piece.XPt;
        if (width <= Epsilon) return;
        var font = sample.Font;
        var em = piece.FontSizePt / font.UnitsPerEm;
        if (piece.Style.Underline)
        {
            decorations.Add(new LaidOutDecoration(piece.XPt, piece.BaselinePt - font.UnderlinePosition * em,
                width, Math.Max(font.UnderlineThickness * em, 0.1), piece.Style.ColorArgb));
        }
        if (piece.Style.Strike)
        {
            decorations.Add(new LaidOutDecoration(piece.XPt, piece.BaselinePt - font.StrikeoutPosition * em,
                width, Math.Max(font.StrikeoutSize * em, 0.1), piece.Style.ColorArgb));
        }
    }
}
