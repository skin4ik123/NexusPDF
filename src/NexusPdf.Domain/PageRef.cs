using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Domain;

/// <summary>
/// Логическая страница документа: ссылка на страницу физического источника,
/// добавочный поворот и наложенный новый контент (текст, изображения).
/// Все операции выполняются над списком таких ссылок и не трогают исходный
/// файл до момента сохранения.
/// </summary>
public sealed record PageRef(
    Guid SourceId,
    int SourcePageIndex,
    int RotationOffset,
    IReadOnlyList<PageOverlay>? Overlays = null)
{
    public static int NormalizeQuarterTurns(int quarterTurns) => ((quarterTurns % 4) + 4) % 4;

    public IReadOnlyList<PageOverlay> OverlayList => Overlays ?? Array.Empty<PageOverlay>();

    public PageRef Rotated(int quarterTurns) =>
        this with { RotationOffset = NormalizeQuarterTurns(RotationOffset + quarterTurns) };

    public PageRef WithOverlay(PageOverlay overlay)
    {
        // Фиксируем ориентацию страницы на момент размещения: при последующем
        // повороте страницы движок пересчитает координаты оверлея.
        var stamped = overlay with { PlacedRotation = RotationOffset };
        var list = new List<PageOverlay>(OverlayList) { stamped };
        return this with { Overlays = list };
    }

    public PageRef WithoutOverlays() => this with { Overlays = null };
}
