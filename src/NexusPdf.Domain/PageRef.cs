namespace NexusPdf.Domain;

/// <summary>
/// Логическая страница документа: ссылка на страницу физического источника
/// плюс добавочный поворот. Все структурные операции (перестановка, удаление,
/// поворот) выполняются над списком таких ссылок и не трогают исходный файл
/// до момента сохранения.
/// </summary>
public sealed record PageRef(Guid SourceId, int SourcePageIndex, int RotationOffset)
{
    public static int NormalizeQuarterTurns(int quarterTurns) => ((quarterTurns % 4) + 4) % 4;

    public PageRef Rotated(int quarterTurns) =>
        this with { RotationOffset = NormalizeQuarterTurns(RotationOffset + quarterTurns) };
}
