using NexusPdf.Ux;

namespace NexusPdf.UnitTests;

/// <summary>
/// Недавние команды. Список маленький, поэтому каждое его свойство заметно
/// глазу: порядок, отсутствие повторов и то, что он переживает обновление
/// программы.
/// </summary>
public sealed class RecentCommandsTests
{
    private static readonly CommandRegistry Registry = AppCommands.Build();

    [Fact]
    public void The_Last_Used_Command_Comes_First()
    {
        var recent = RecentCommands.Use(Array.Empty<string>(), CommandIds.Print);
        recent = RecentCommands.Use(recent, CommandIds.RotateRight);
        Assert.Equal(new[] { CommandIds.RotateRight, CommandIds.Print }, recent);
    }

    [Fact]
    public void Using_The_Same_Command_Again_Does_Not_Duplicate_It()
    {
        var recent = RecentCommands.Use(Array.Empty<string>(), CommandIds.Print);
        recent = RecentCommands.Use(recent, CommandIds.RotateRight);
        recent = RecentCommands.Use(recent, CommandIds.Print);

        Assert.Equal(new[] { CommandIds.Print, CommandIds.RotateRight }, recent);
        Assert.Single(recent.Where(id => id == CommandIds.Print));
    }

    [Fact]
    public void The_List_Never_Grows_Past_The_Limit()
    {
        IReadOnlyList<string> recent = Array.Empty<string>();
        foreach (var command in Registry.All.Take(RecentCommands.Limit + 5))
            recent = RecentCommands.Use(recent, command.Id);

        Assert.Equal(RecentCommands.Limit, recent.Count);
    }

    [Fact]
    public void A_Command_Removed_From_The_Program_Disappears_Quietly()
    {
        var saved = new[] { CommandIds.Print, "команды.больше.нет", CommandIds.RotateLeft };
        var recent = RecentCommands.Sanitize(saved, id => Registry.Find(id) != null);
        Assert.Equal(new[] { CommandIds.Print, CommandIds.RotateLeft }, recent);
    }

    [Fact]
    public void One_Lonely_Command_Is_Not_Worth_A_Section()
    {
        Assert.False(RecentCommands.WorthShowing(Array.Empty<string>()));
        Assert.False(RecentCommands.WorthShowing(new[] { CommandIds.Print }));
        Assert.True(RecentCommands.WorthShowing(new[] { CommandIds.Print, CommandIds.RotateLeft }));
    }

    [Fact]
    public void Rubbish_In_Settings_Does_Not_Break_The_Panel()
    {
        Assert.Empty(RecentCommands.Sanitize(null, _ => true));
        Assert.Empty(RecentCommands.Sanitize(new[] { "", "  " }, id => Registry.Find(id) != null));
    }
}
