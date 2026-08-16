using System.Globalization;

namespace NexusPdf.Export;

public enum CellKind { Text, Number, Percent, Currency, Date }

/// <summary>Разобранное значение ячейки. Text — всегда исходная строка.</summary>
public sealed record ParsedValue(CellKind Kind, string Text, double Number = 0, DateTime Date = default, string Currency = "")
{
    public static ParsedValue AsText(string text) => new(CellKind.Text, text);
}

/// <summary>
/// Строка ячейки → значение нужного типа.
///
/// Ради этого экспорт в Excel и делают: «1 234,50» должно стать числом, которое
/// суммируется, а не текстом, который только выглядит числом. Но осторожность
/// важнее: номер счёта, телефон и артикул с ведущим нулём обязаны остаться
/// текстом — иначе Excel их округлит или обрежет, и данные потеряются молча.
/// </summary>
public static class CellValueParser
{
    /// <summary>Больше 15 значащих цифр Excel уже не хранит точно — это не число, а номер.</summary>
    private const int MaxDigits = 15;

    private static readonly string[] DateFormats =
    {
        "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "dd/MM/yyyy", "d/M/yyyy",
        "yyyy-MM-dd", "dd-MM-yyyy", "MM/dd/yyyy",
    };

    private static readonly char[] GroupChars = { ' ', ' ', ' ', '\'' };

    private static readonly string[] CurrencySymbols =
    {
        "₽", "$", "€", "£", "¥", "₴", "₸", "zł", "руб.", "руб", "р.", "грн",
    };

    /// <summary>
    /// <paramref name="decimalIsComma"/> решает спор «1,234»: в русской записи
    /// это 1.234, в английской — 1234. Однозначно из самой строки это не
    /// следует, поэтому решает язык, а не догадка.
    /// </summary>
    public static ParsedValue Parse(string? text, bool decimalIsComma)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0) return ParsedValue.AsText(value);

        if (TryParseDate(value, out var date))
            return new ParsedValue(CellKind.Date, value, date.ToOADate(), date);

        var body = value;
        var kind = CellKind.Number;
        var currency = string.Empty;

        if (body.EndsWith('%'))
        {
            body = body[..^1].Trim();
            kind = CellKind.Percent;
        }
        else
        {
            foreach (var symbol in CurrencySymbols)
            {
                if (body.EndsWith(symbol, StringComparison.OrdinalIgnoreCase) && body.Length > symbol.Length)
                {
                    body = body[..^symbol.Length].Trim();
                    kind = CellKind.Currency;
                    currency = symbol;
                    break;
                }
                if (body.StartsWith(symbol, StringComparison.OrdinalIgnoreCase) && body.Length > symbol.Length)
                {
                    body = body[symbol.Length..].Trim();
                    kind = CellKind.Currency;
                    currency = symbol;
                    break;
                }
            }
        }

        if (!TryParseNumber(body, decimalIsComma, out var number))
            return ParsedValue.AsText(value);

        return kind switch
        {
            CellKind.Percent => new ParsedValue(CellKind.Percent, value, number / 100.0),
            CellKind.Currency => new ParsedValue(CellKind.Currency, value, number, Currency: currency),
            _ => new ParsedValue(CellKind.Number, value, number),
        };
    }

    private static bool TryParseNumber(string body, bool decimalIsComma, out double number)
    {
        number = 0;
        if (body.Length == 0) return false;

        var negative = false;
        // Бухгалтерская запись отрицательного числа — в скобках.
        if (body.Length > 2 && body[0] == '(' && body[^1] == ')')
        {
            negative = true;
            body = body[1..^1].Trim();
        }
        if (body.Length > 0 && (body[0] == '-' || body[0] == '−' || body[0] == '+'))
        {
            negative |= body[0] != '+';
            body = body[1..].Trim();
        }
        if (body.Length == 0) return false;

        var cleaned = new string(body.Where(c => !GroupChars.Contains(c)).ToArray());
        if (cleaned.Length == 0) return false;
        if (!cleaned.All(c => char.IsAsciiDigit(c) || c == '.' || c == ',')) return false;
        if (!cleaned.Any(char.IsAsciiDigit)) return false;

        // Ведущий ноль — признак кода, а не количества: «007» и «0123» должны
        // остаться как есть.
        var digitsOnly = new string(cleaned.Where(char.IsAsciiDigit).ToArray());
        if (digitsOnly.Length > MaxDigits) return false;
        if (cleaned.Length > 1 && cleaned[0] == '0' && char.IsAsciiDigit(cleaned[1])) return false;

        var dot = cleaned.LastIndexOf('.');
        var comma = cleaned.LastIndexOf(',');
        int decimalAt;

        if (dot >= 0 && comma >= 0)
        {
            // Есть оба разделителя — десятичным может быть только последний.
            decimalAt = Math.Max(dot, comma);
        }
        else if (dot < 0 && comma < 0)
        {
            decimalAt = -1;
        }
        else
        {
            var only = dot >= 0 ? '.' : ',';
            var occurrences = cleaned.Count(c => c == only);
            if (occurrences > 1)
            {
                decimalAt = -1; // повторяется — это разделитель тысяч
            }
            else
            {
                var at = dot >= 0 ? dot : comma;
                var after = cleaned.Length - at - 1;
                var isDecimalChar = only == (decimalIsComma ? ',' : '.');
                // Ровно три цифры после — спорный случай, решает язык.
                decimalAt = after == 3 && !isDecimalChar ? -1 : at;
            }
        }

        var integerPart = decimalAt < 0
            ? new string(cleaned.Where(char.IsAsciiDigit).ToArray())
            : new string(cleaned[..decimalAt].Where(char.IsAsciiDigit).ToArray());
        var fractionPart = decimalAt < 0
            ? string.Empty
            : new string(cleaned[(decimalAt + 1)..].Where(char.IsAsciiDigit).ToArray());
        if (integerPart.Length == 0 && fractionPart.Length == 0) return false;

        var normalized = integerPart.Length == 0 ? "0" : integerPart;
        if (fractionPart.Length > 0) normalized += "." + fractionPart;

        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return false;
        if (negative) number = -number;
        return true;
    }

    private static bool TryParseDate(string value, out DateTime date)
    {
        date = default;
        if (value.Length is < 6 or > 10) return false;
        if (!value.Any(char.IsAsciiDigit)) return false;

        foreach (var format in DateFormats)
        {
            if (DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out date))
                return true;
        }
        return false;
    }
}
