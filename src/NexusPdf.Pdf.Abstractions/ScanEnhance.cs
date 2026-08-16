namespace NexusPdf.Pdf.Abstractions;

/// <summary>Что делать со сканом.</summary>
/// <param name="Deskew">Выровнять наклон страницы.</param>
/// <param name="Despeckle">Убрать точечный мусор сканера.</param>
/// <param name="LevelBackground">Выровнять фон: серая бумага и тени → белое.</param>
/// <param name="MaxSpeckleArea">Наибольшее пятно, считающееся мусором (в пикселях растра).</param>
/// <param name="Pages">
/// Номера страниц (с нуля), которые надо обработать. Пустой список — весь документ.
/// </param>
/// <param name="BackgroundStrength">
/// 0…100: насколько широкую полосу «почти белого» добеливать до ровного 255.
/// Слабое выравнивание только снимает неровность подсветки; сильное делает лист
/// по-настоящему чистым, но может съесть очень бледный карандаш.
/// </param>
/// <param name="NeutralizeTint">
/// Считать цвет бумаги по каждому каналу отдельно и тем снимать желтизну старой
/// бумаги и цветной след от лампы.
/// </param>
/// <param name="TrimDarkEdges">
/// Стирать тёмную кайму по краю — щель между крышкой сканера и листом.
/// </param>
public sealed record ScanEnhanceOptions(
    bool Deskew = true,
    bool Despeckle = true,
    bool LevelBackground = false,
    int MaxSpeckleArea = 12,
    IReadOnlyList<int>? Pages = null,
    int BackgroundStrength = 60,
    bool NeutralizeTint = true,
    bool TrimDarkEdges = false);

/// <param name="PagesProcessed">Сколько страниц просмотрено.</param>
/// <param name="PagesStraightened">На скольких исправлен наклон.</param>
/// <param name="MaxAngleDegrees">Самый большой исправленный угол.</param>
/// <param name="ImagesCleaned">Сколько растров вычищено.</param>
/// <param name="SpecklesRemoved">Сколько пятен мусора удалено.</param>
/// <param name="EdgesTrimmed">Сколько полос тёмной каймы стёрто.</param>
public readonly record struct ScanEnhanceStats(
    int PagesProcessed, int PagesStraightened, double MaxAngleDegrees,
    int ImagesCleaned, int SpecklesRemoved, int EdgesTrimmed = 0);

/// <summary>Найденный наклон одной страницы — для предпросмотра до применения.</summary>
public readonly record struct PageSkew(int PageIndex, double AngleDegrees, double Confidence);
