namespace NexusPdf.Export;

public enum ParagraphAlignment { Left, Center, Right }

/// <summary>Абзац: подряд идущие строки, которые в исходнике были одним абзацем.</summary>
public sealed record TextParagraph(
    IReadOnlyList<TextLine> Lines,
    ParagraphAlignment Alignment,
    double IndentPt,
    bool IsHeading)
{
    public string Text => string.Join(" ", Lines.Select(l => l.Text));
    public double Top => Lines.Max(l => l.Top);
    public double Bottom => Lines.Min(l => l.Bottom);
    public double Left => Lines.Min(l => l.Left);
    public double Right => Lines.Max(l => l.Right);
    public double FontSize => TextLine.Median(Lines.Select(l => l.FontSize).ToList(), 10);
}

/// <summary>
/// Строки → абзацы.
///
/// Это главное, что отличает пригодный для правки документ Word от построчной
/// каши: в PDF абзацев нет, и если каждую строку сделать абзацем, то при
/// первой же правке текст поедет лесенкой, а перенос слов перестанет работать.
///
/// Строка продолжает абзац, если предыдущая дотянулась до правого края блока
/// (значит, её перенесли, а не закончили), расстояние между ними обычное для
/// этого кегля и не начался новый список.
/// </summary>
public static class ParagraphBuilder
{
    /// <summary>Насколько близко к правому краю должна кончаться перенесённая строка.</summary>
    private const double WrapTolerance = 0.06;

    /// <summary>Отступ меньше этого — не отступ, а погрешность разметки.</summary>
    private const double MinIndentPt = 8.0;

    private static readonly char[] BulletChars = { '•', '·', '▪', '–', '—', '-', '*' };

    public static IReadOnlyList<TextParagraph> Build(IReadOnlyList<TextLine> lines)
    {
        if (lines.Count == 0) return Array.Empty<TextParagraph>();

        var contentLeft = lines.Min(l => l.Left);
        var contentRight = lines.Max(l => l.Right);
        var width = Math.Max(1.0, contentRight - contentLeft);
        var bodySize = TextLine.Median(lines.Select(l => l.FontSize).ToList(), 10);

        var paragraphs = new List<TextParagraph>();
        var current = new List<TextLine> { lines[0] };

        for (var i = 1; i < lines.Count; i++)
        {
            if (Continues(current, lines[i], contentRight, width))
            {
                current.Add(lines[i]);
                continue;
            }
            paragraphs.Add(Describe(current, contentLeft, contentRight, width, bodySize));
            current = new List<TextLine> { lines[i] };
        }
        paragraphs.Add(Describe(current, contentLeft, contentRight, width, bodySize));
        return paragraphs;
    }

    private static bool Continues(
        List<TextLine> current, TextLine next, double contentRight, double width)
    {
        var previous = current[^1];
        var gap = previous.Bottom - next.Top;
        var size = Math.Max(previous.FontSize, next.FontSize);

        // Разные кегли — разные абзацы: заголовок не продолжается текстом.
        if (Math.Min(previous.FontSize, next.FontSize) > 0 &&
            Math.Abs(previous.FontSize - next.FontSize) > size * 0.2)
            return false;

        // Слишком далеко по вертикали — новый абзац.
        if (gap > size * 0.9) return false;
        // Строки идут вразнобой (колонки, наложение) — не абзац.
        if (gap < -size * 0.6) return false;

        // Предыдущая строка не дотянулась до правого края — значит, она была
        // последней в абзаце, а не перенесённой.
        if (previous.Right < contentRight - width * WrapTolerance) return false;

        // Новый пункт списка начинает свой абзац.
        if (StartsListItem(next)) return false;

        return true;
    }

    private static bool StartsListItem(TextLine line)
    {
        var text = line.Text.TrimStart();
        if (text.Length == 0) return false;
        if (BulletChars.Contains(text[0])) return true;

        // «1.», «2)», «а)» в начале строки.
        var i = 0;
        while (i < text.Length && char.IsLetterOrDigit(text[i]) && i < 3) i++;
        return i > 0 && i < text.Length && (text[i] == '.' || text[i] == ')') &&
               i + 1 < text.Length && text[i + 1] == ' ';
    }

    private static TextParagraph Describe(
        List<TextLine> lines, double contentLeft, double contentRight, double width, double bodySize)
    {
        var left = lines.Min(l => l.Left);
        var right = lines.Max(l => l.Right);
        var leftGap = left - contentLeft;
        var rightGap = contentRight - right;

        var alignment = ParagraphAlignment.Left;
        if (leftGap > width * 0.08 && Math.Abs(leftGap - rightGap) <= width * 0.1)
            alignment = ParagraphAlignment.Center;
        else if (leftGap > width * 0.15 && rightGap <= width * 0.03)
            alignment = ParagraphAlignment.Right;

        var indent = alignment == ParagraphAlignment.Left && leftGap >= MinIndentPt ? leftGap : 0;

        // Заголовок — короткий, в одну строку и крупнее или жирнее обычного
        // текста. Пометить им весь текст было бы хуже, чем не помечать вовсе.
        var size = TextLine.Median(lines.Select(l => l.FontSize).ToList(), bodySize);
        var heading = lines.Count == 1 &&
                      lines[0].Text.Length <= 120 &&
                      (size >= bodySize * 1.2 || (lines[0].IsBold && size >= bodySize));

        return new TextParagraph(lines, alignment, indent, heading);
    }
}
