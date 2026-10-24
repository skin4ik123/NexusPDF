using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NexusPdf.Pdf.Abstractions.TextBlocks;

/// <summary>
/// Текстовый объект страницы так, как он нарисован. Все координаты — в
/// отображаемых пунктах (y вниз, с учётом поворота показа); угол — по часовой
/// стрелке на экране. Text — ровно записанные в объекте буквы, без пробелов,
/// которые PDFium додумывает между объектами. FontSizePt — кегль с учётом
/// матриц (cm, Tm, формы). Path — адрес объекта (вложенный — через формы).
/// Clipped — часть объекта срезана обтравкой (текст, вылезающий из клетки
/// Excel). SpaceAfter — номера букв, после которых внутри объекта стоит
/// зазор-пробел (сдвиг в TJ, так пишет pdfTeX): в Text этих пробелов нет.
/// </summary>
public sealed record PageTextObject(
    IReadOnlyList<int> Path, string Text, OverlayBox BoundsPt,
    double OriginXPt, double OriginYPt, double AngleDeg, double FontSizePt, string FontName,
    bool Bold, bool Italic, uint ColorArgb, bool Invisible, bool Type3, bool InForm,
    IReadOnlyList<double> CharAdvancesPt, string Fingerprint,
    bool Clipped = false, IReadOnlyList<int>? SpaceAfter = null);

/// <summary>Тонкая линия страницы (подчёркивание, линия бланка): залитый узкий прямоугольник или отрезок.</summary>
public sealed record PageLineObject(IReadOnlyList<int> Path, OverlayBox BoundsPt, uint ColorArgb, string Fingerprint = "");

/// <summary>Граница таблицы в отображаемых пунктах: Position — y горизонтальной, x вертикальной.</summary>
public sealed record PageRuling(bool Horizontal, double PositionPt, double StartPt, double EndPt);

/// <summary>Текст, тонкие линии и границы таблиц страницы — всё, из чего собираются абзацы и клетки бланка.</summary>
public sealed record PageTextLayout(
    IReadOnlyList<PageTextObject> Objects, IReadOnlyList<PageLineObject> Lines, IReadOnlyList<PageRuling> Rulings)
{
    public static readonly PageTextLayout Empty = new([], [], []);
}

/// <summary>
/// Отпечаток текстового объекта: по нему запись проверяет, что заменяет тот
/// самый объект, который видел пользователь, а не сдвинувшийся на его место
/// чужой. Начало строки — в координатах содержимого страницы (без поворота
/// показа), округлено до 0,01 пт; кегль — до 0,01.
/// </summary>
public static class PageTextFingerprint
{
    public static string Of(string text, double originXPt, double originYPt, double fontSizePt)
    {
        var key = string.Join('\u0001', text, Round(originXPt), Round(originYPt), Round(fontSizePt));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>Отпечаток пути (линии) — по его рамке в координатах содержимого страницы.</summary>
    public static string OfPath(double left, double bottom, double right, double top) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\u0001', "path", Round(left), Round(bottom), Round(right), Round(top)))));

    private static string Round(double value) =>
        double.IsFinite(value) ? (Math.Round(value, 2) + 0.0).ToString("0.00", CultureInfo.InvariantCulture) : "NaN";
}
