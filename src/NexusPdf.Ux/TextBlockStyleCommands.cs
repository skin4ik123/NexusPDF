using System.Globalization;

namespace NexusPdf.Ux;

/// <summary>
/// Общие для панели оформления и правой колонки правила: шаги размера
/// кнопками −/+, разбор введённого размера и цвета.
/// </summary>
public static class TextBlockStyleCommands
{
    private static readonly double[] Ladder = [6, 7, 8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 72];
    private const double StepAbove72 = 12;
    public const double MinimumSizePt = 1;
    public const double MaximumSizePt = 999;

    /// <summary>
    /// Следующий размер по лестнице, как в текстовых редакторах; выше 72 —
    /// шагом 12. Размер между ступенями переходит на ближайшую в сторону шага.
    /// </summary>
    public static double NextSize(double current, int direction)
    {
        if (!double.IsFinite(current) || direction == 0) return current;
        if (direction > 0)
        {
            if (current >= Ladder[^1])
                return Math.Min(MaximumSizePt, Ladder[^1] + (Math.Floor((current - Ladder[^1]) / StepAbove72) + 1) * StepAbove72);
            return Ladder.First(step => step > current + 1e-9);
        }
        if (current > Ladder[^1] + 1e-9)
        {
            var above = Math.Ceiling((current - Ladder[^1]) / StepAbove72 - 1e-9) - 1;
            return Ladder[^1] + Math.Max(0, above) * StepAbove72;
        }
        return Ladder.LastOrDefault(step => step < current - 1e-9, Ladder[0]);
    }

    /// <summary>Размер в пунктах (1–999); и запятая, и точка, «pt» в конце допускается.</summary>
    public static bool TryParseSize(string? text, out double pt)
    {
        pt = 0;
        var trimmed = (text ?? "").Trim();
        if (trimmed.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^2].Trim();
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
            !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;
        if (!double.IsFinite(value) || value < MinimumSizePt || value > MaximumSizePt) return false;
        pt = value;
        return true;
    }

    /// <summary>Цвет из «#RGB», «#RRGGBB» или «#AARRGGBB» (решётка необязательна).</summary>
    public static bool TryParseColor(string? text, out uint argb)
    {
        argb = 0;
        var hex = (text ?? "").Trim().TrimStart('#');
        if (hex.Length is not (3 or 6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return false;
        argb = hex.Length switch
        {
            3 => 0xFF000000 | Expand(value >> 8) << 16 | Expand((value >> 4) & 0xF) << 8 | Expand(value & 0xF),
            6 => 0xFF000000 | value,
            _ => value,
        };
        return true;

        static uint Expand(uint nibble) => nibble << 4 | nibble;
    }

    /// <summary>Цвет как «#RRGGBB» (прозрачность показывается, только если она не полная).</summary>
    public static string FormatColor(uint argb) =>
        (argb >> 24) == 0xFF ? $"#{argb & 0xFFFFFF:X6}" : $"#{argb:X8}";
}
