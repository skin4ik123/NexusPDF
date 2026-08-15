using NexusPdf.Ux;

namespace NexusPdf.UnitTests;

/// <summary>
/// Профили рабочих пространств. Проверяется, что они действительно РАЗНЫЕ:
/// профиль, который ничего не меняет, — лишний пункт меню.
/// </summary>
public sealed class WorkspaceProfileTests
{
    [Fact]
    public void Unknown_Profile_Falls_Back_To_Reading()
    {
        Assert.Equal(WorkspaceProfile.Reading, WorkspaceProfile.ById("нет-такого"));
        Assert.Equal(WorkspaceProfile.Reading, WorkspaceProfile.ById(null));
    }

    [Fact]
    public void Profiles_Round_Trip_By_Identifier()
    {
        foreach (var profile in WorkspaceProfile.All)
            Assert.Equal(profile, WorkspaceProfile.ById(profile.Id));
    }

    [Fact]
    public void Identifier_Case_Does_Not_Matter()
    {
        Assert.Equal(WorkspaceProfile.Editing, WorkspaceProfile.ById("EDITING"));
    }

    [Fact]
    public void Every_Profile_Differs_From_The_Others()
    {
        // Одинаковые профили — это лишние пункты меню, которые ничего не делают.
        var states = WorkspaceProfile.All
            .Select(p => (p.Rail, p.CommentsPanel, p.Outline, p.Organize, p.FitWholePage))
            .ToList();
        Assert.Equal(states.Count, states.Distinct().Count());
    }

    [Fact]
    public void Reading_Hides_Editing_Tools()
    {
        Assert.Equal(ToolRail.None, WorkspaceProfile.Reading.Rail);
        Assert.False(WorkspaceProfile.Reading.CommentsPanel);
        Assert.True(WorkspaceProfile.Reading.FitWholePage);
    }

    [Fact]
    public void Reviewing_Opens_The_Comments_Panel_With_Comment_Tools()
    {
        Assert.Equal(ToolRail.Comment, WorkspaceProfile.Reviewing.Rail);
        Assert.True(WorkspaceProfile.Reviewing.CommentsPanel);
    }

    [Fact]
    public void Pages_Profile_Turns_On_The_Organize_Mode()
    {
        Assert.True(WorkspaceProfile.Pages.Organize);
        Assert.Equal(ToolRail.Pages, WorkspaceProfile.Pages.Rail);
    }

    [Fact]
    public void Rail_Name_Matches_What_The_Interface_Expects()
    {
        // Имя группы уходит в SelectToolGroup строкой: опечатка здесь просто
        // не откроет нужную полосу инструментов.
        Assert.Null(WorkspaceProfile.Reading.RailName);
        Assert.Equal("Comment", WorkspaceProfile.Reviewing.RailName);
        Assert.Equal("Edit", WorkspaceProfile.Editing.RailName);
        Assert.Equal("Pages", WorkspaceProfile.Pages.RailName);
    }
}
