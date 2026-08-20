namespace NexusPdf.Export;

/// <summary>
/// Текст, пригодный для XML.
///
/// И Word, и Excel — это XML внутри ZIP, а в XML 1.0 управляющих символов не
/// существует. В PDF они встречаются: маркеры списков, разделители полей,
/// мусор от кривых генераторов. Один такой символ рушил ВЕСЬ экспорт —
/// «hexadecimal value 0x02 is an invalid character», файл не создавался вовсе.
/// Именно так не выгружалась портовая форма уведомления: ни в Word, ни в Excel.
///
/// Лучше потерять невидимый символ, чем документ целиком.
/// </summary>
internal static class XmlText
{
    public static string Safe(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Подавляющее большинство строк чистые, поэтому сначала быстрая
        // проверка без выделения памяти.
        var needsCleaning = false;
        foreach (var c in text)
        {
            if (IsForbidden(c) || char.IsSurrogate(c)) { needsCleaning = true; break; }
        }
        if (!needsCleaning) return text;

        var clean = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (IsForbidden(c)) continue;

            // Суррогаты допустимы только парой: одинокая половина ломает XML
            // так же, как управляющий символ.
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    clean.Append(c).Append(text[i + 1]);
                    i++;
                }
                continue;
            }
            if (char.IsLowSurrogate(c)) continue;

            clean.Append(c);
        }
        return clean.ToString();
    }

    private static bool IsForbidden(char c) =>
        (c < 0x20 && c != '\t' && c != '\n' && c != '\r') || c == '￾' || c == '￿';
}
