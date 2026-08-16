using NexusPdf.Ux;

namespace NexusPdf.UnitTests;

/// <summary>
/// Значки команд. Пользователь читает панель и меню картинками, а не текстом:
/// пункт без значка выглядит сломанным, а два соседних пункта с ОДНОЙ картинкой
/// сбивают с толку сильнее, чем отсутствие картинок вовсе.
///
/// Проверяется не «все значки разные» (одно и то же действие в разных местах
/// обязано выглядеть одинаково — удаление везде корзина), а «в одном месте нет
/// двух одинаковых».
/// </summary>
public sealed class CommandGlyphTests
{
    private static readonly CommandRegistry Registry = AppCommands.Build();

    private static string Title(string id) => Registry.Find(id)?.TitleKey ?? id;

    [Fact]
    public void Every_Command_Has_A_Picture()
    {
        var without = Registry.All.Where(c => string.IsNullOrEmpty(c.Glyph)).Select(c => c.Id).ToList();
        Assert.True(without.Count == 0,
            "Без значка остались команды: " + string.Join(", ", without));
    }

    /// <summary>
    /// Значки задаются ТОЛЬКО escape-последовательностями. Живой символ из
    /// области частного использования не переживает перезапись файла
    /// инструментами и молча превращается в пустоту — так уже терялись все 36.
    /// </summary>
    [Fact]
    public void Pictures_Are_Real_Icon_Codepoints()
    {
        foreach (var c in Registry.All)
        {
            Assert.True(c.Glyph.Length is 1 or 2, $"{c.Id}: значок должен быть одним символом.");
            var code = char.ConvertToUtf32(c.Glyph, 0);
            Assert.True(code is >= 0xE700 and <= 0xF8FF,
                $"{c.Id}: U+{code:X4} вне диапазона значков Segoe MDL2.");
        }
    }

    public static TheoryData<string> Places()
    {
        var data = new TheoryData<string>();
        foreach (var kind in Enum.GetValues<SelectionKind>())
            data.Add("menu:" + kind);
        foreach (var group in ToolsLayout.Default)
            data.Add("tools:" + group.TitleKey);
        data.Add("quick:");
        return data;
    }

    private static IReadOnlyList<string> Ids(string place)
    {
        var kind = place.Split(':')[0];
        var name = place[(place.IndexOf(':') + 1)..];
        return kind switch
        {
            "menu" => ContextMenuComposer.MenuIds.TryGetValue(Enum.Parse<SelectionKind>(name), out var m)
                ? m
                : Array.Empty<string>(),
            "tools" => ToolsLayout.Default.First(g => g.TitleKey == name).Commands,
            _ => QuickPanelLayout.Default,
        };
    }

    [Theory]
    [MemberData(nameof(Places))]
    public void No_Two_Items_Side_By_Side_Share_A_Picture(string place)
    {
        var clashes = Ids(place)
            .Select(id => Registry.Find(id))
            .Where(c => c != null && !string.IsNullOrEmpty(c.Glyph))
            .GroupBy(c => c!.Glyph)
            .Where(g => g.Count() > 1)
            .Select(g => $"U+{char.ConvertToUtf32(g.Key, 0):X4} у {string.Join(" и ", g.Select(c => Title(c!.Id)))}")
            .ToList();

        Assert.True(clashes.Count == 0, $"В «{place}» одинаковые картинки: {string.Join("; ", clashes)}");
    }

    /// <summary>
    /// Поворот вправо и влево обязаны быть ПАРОЙ: у пользователя это одно
    /// действие в двух направлениях, и разные по рисунку значки читаются как
    /// разные операции.
    /// </summary>
    [Fact]
    public void Rotate_Left_And_Right_Are_A_Matched_Pair()
    {
        var right = Registry.Find(CommandIds.RotateRight)!.Glyph;
        var left = Registry.Find(CommandIds.RotateLeft)!.Glyph;
        var half = Registry.Find(CommandIds.Rotate180)!.Glyph;

        Assert.NotEqual(right, left);
        Assert.NotEqual(right, half);
        Assert.NotEqual(left, half);
        // Обе круговые стрелки MDL2 лежат рядом по замыслу шрифта.
        Assert.Equal(0xE72C, char.ConvertToUtf32(right, 0));
        Assert.Equal(0xE777, char.ConvertToUtf32(left, 0));
    }
}
