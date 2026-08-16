using NexusPdf.Ux;

namespace NexusPdf.UnitTests;

/// <summary>
/// Слова, которыми люди ищут инструменты. Пользователь набирает то, что хочет
/// СДЕЛАТЬ, а не название пункта: «кривой скан», «уменьшить размер», «пароль».
/// Если такой запрос ничего не находит, поиск по панели бесполезен — а панель
/// на шестьдесят пунктов без поиска бесполезна вдвойне.
/// </summary>
public sealed class ToolSearchWordsTests
{
    private static readonly CommandRegistry Registry = AppCommands.Build();

    private static bool Finds(string query, string expectedId) =>
        Registry.All.Any(c => c.Id == expectedId &&
            (c.Keywords.Any(k => k.Contains(query, StringComparison.CurrentCultureIgnoreCase))));

    [Theory]
    [InlineData("кривой скан", CommandIds.OptimizeDocument)]
    [InlineData("убрать шум", CommandIds.OptimizeDocument)]
    [InlineData("deskew", CommandIds.OptimizeDocument)]
    [InlineData("уменьшить размер", CommandIds.OptimizeDocument)]
    [InlineData("compress", CommandIds.OptimizeDocument)]
    [InlineData("линеаризовать", CommandIds.OptimizeDocument)]
    [InlineData("засвет", CommandIds.OptimizeDocument)]
    [InlineData("водяной знак", CommandIds.Watermark)]
    [InlineData("объединить", CommandIds.MergePdfs)]
    [InlineData("вымарать", CommandIds.Redact)]
    [InlineData("распознать", CommandIds.Ocr)]
    public void Everyday_Words_Lead_To_The_Right_Tool(string query, string id)
    {
        Assert.True(Finds(query, id),
            $"Запрос «{query}» обязан находить {id}: допишите синоним в каталог команд.");
    }

    [Fact]
    public void Every_Tool_In_The_Panel_Can_Be_Found_By_Some_Word()
    {
        var dumb = ToolsLayout.Default
            .SelectMany(g => g.Commands)
            .Select(id => Registry.Find(id))
            .Where(c => c is { Keywords.Count: 0 })
            .Select(c => c!.Id)
            .ToList();
        Assert.True(dumb.Count == 0,
            "У этих инструментов нет ни одного слова для поиска: " + string.Join(", ", dumb));
    }
}
