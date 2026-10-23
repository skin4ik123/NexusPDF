using NexusPdf.Pdf.Abstractions.TextBlocks;
using NexusPdf.Ux;

namespace NexusPdf.App.Desktop.Views.Text;

/// <summary>
/// Установленные шрифты для списка выбора. Сканирование папок шрифтов идёт
/// один раз, в фоне: на компьютере с тысячей шрифтов оно заметно по времени.
/// </summary>
internal static class FontSources
{
    private static readonly Lazy<IReadOnlyList<FontChoice>> LazyChoices = new(() =>
    {
        var catalog = FontCatalog.System;
        return catalog.Families
            .Select(f => new FontChoice(f.Name, f.HasCyrillic, f.Embeddable, f.Supported,
                IsLarge: f.Faces.Any(face => face.FileSize > catalog.LargeFontWarningBytes)))
            .ToList();
    });

    public static IReadOnlyList<FontChoice> Choices => LazyChoices.Value;

    public static bool IsReady => LazyChoices.IsValueCreated;

    /// <summary>Начать сканирование заранее, чтобы первый показ списка не ждал.</summary>
    public static Task WarmUpAsync() => Task.Run(() => _ = Choices);

    /// <summary>Установленная гарнитура, которой будет писаться шрифт документа; null — замены нет.</summary>
    public static string? SubstituteFor(string pdfFontName) =>
        FontSubstitution.FamilyFor(pdfFontName, FontCatalog.System);
}
