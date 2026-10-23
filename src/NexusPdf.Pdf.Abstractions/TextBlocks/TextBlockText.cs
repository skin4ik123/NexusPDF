using System.Text;

namespace NexusPdf.Pdf.Abstractions.TextBlocks;

/// <summary>Правила текста блока: что принимается и как приводится вставляемое.</summary>
public static class TextBlockText
{
    /// <summary>Новая строка внутри абзаца (Shift+Enter).</summary>
    public const char LineBreak = '\u2028';

    /// <summary>Разделитель абзацев во вставляемом тексте.</summary>
    public const char ParagraphBreak = '\u2029';

    /// <summary>
    /// Принимается ли символ. Письменности, которым нужна сложная вёрстка
    /// (справа налево, связки, перестановка знаков: арабская, иврит,
    /// индийские, тайская, бирманская, кхмерская), без движка shaping вышли
    /// бы неверно — их честнее не принять.
    /// </summary>
    public static bool IsSupported(int codePoint) => codePoint switch
    {
        >= 0x0590 and <= 0x08FF => false,
        >= 0x0900 and <= 0x0DFF => false,
        >= 0x0E00 and <= 0x0FFF => false,
        >= 0x1000 and <= 0x109F => false,
        >= 0x1780 and <= 0x17FF => false,
        >= 0xFB1D and <= 0xFDFF => false,
        >= 0xFE70 and <= 0xFEFF => false,
        _ => true,
    };

    /// <summary>
    /// Вставляемый текст: табуляция — пробел (так вставляются ячейки Excel),
    /// переводы строки любого вида — новые абзацы, разрыв строки Word (\v) —
    /// перевод строки внутри абзаца; управляющие символы, метка порядка байтов
    /// и одинокие половины суррогатных пар убираются.
    /// </summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\t':
                    builder.Append(' ');
                    break;
                case '\r':
                    builder.Append(ParagraphBreak);
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    break;
                case '\n':
                    builder.Append(ParagraphBreak);
                    break;
                case '\v':
                    builder.Append(LineBreak);
                    break;
                case '\uFEFF':
                    break;
                default:
                    if (char.IsHighSurrogate(c))
                    {
                        if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                        {
                            builder.Append(c).Append(text[i + 1]);
                            i++;
                        }
                    }
                    else if (!char.IsLowSurrogate(c) && !char.IsControl(c))
                    {
                        builder.Append(c);
                    }
                    break;
            }
        }
        return builder.ToString();
    }
}
