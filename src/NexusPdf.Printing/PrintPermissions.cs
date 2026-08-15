namespace NexusPdf.Printing;

/// <summary>Что документ разрешает делать при печати.</summary>
public sealed record PrintPermissions(bool AllowPrint, bool AllowHighQuality)
{
    /// <summary>Ограничений нет — документ не защищён.</summary>
    public static readonly PrintPermissions Unrestricted = new(true, true);

    /// <summary>
    /// Разбирает флаги разрешений PDF (таблица 22 спецификации, нумерация битов
    /// с единицы). Бит 3 — печать, бит 12 — печать высокого качества.
    ///
    /// Соблюдать эти биты — обязанность программы: их легко обойти технически,
    /// но именно поэтому запрет и должен уважаться явно, а не «случайно
    /// работать» из-за отсутствия проверки.
    /// </summary>
    public static PrintPermissions FromFlags(uint flags)
    {
        // Незашифрованный документ: PDFium отдаёт все единицы.
        if (flags == 0xFFFFFFFF) return Unrestricted;

        var print = (flags & PrintBit) != 0;
        var highQuality = (flags & HighQualityPrintBit) != 0;

        // Бит 12 без бита 3 смысла не имеет: печать запрещена целиком.
        return new PrintPermissions(print, print && highQuality);
    }

    private const uint PrintBit = 1u << 2;             // бит 3
    private const uint HighQualityPrintBit = 1u << 11; // бит 12

    /// <summary>
    /// Предел разрешения для печати, когда разрешена только низкокачественная.
    /// Спецификация PDF описывает такую печать как «низкого разрешения»;
    /// 150 DPI — общепринятая трактовка этого ограничения.
    /// </summary>
    public const double LowQualityDpi = 150.0;

    /// <summary>Разрешение, допустимое для этого документа.</summary>
    public double LimitDpi(double requestedDpi) =>
        AllowHighQuality ? requestedDpi : Math.Min(requestedDpi, LowQualityDpi);
}
