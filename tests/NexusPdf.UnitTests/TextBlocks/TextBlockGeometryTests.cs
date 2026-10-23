using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Abstractions.TextBlocks;
using NexusPdf.Ux;

namespace NexusPdf.UnitTests.TextBlocks;

/// <summary>
/// Блок двигают, растягивают и поворачивают как объект: рамка — его
/// собственная, поворот — вокруг её центра.
/// </summary>
public sealed class TextBlockGeometryTests
{
    private static TextBlockOverlay Block(double rotation = 0, TextBlockWidthMode mode = TextBlockWidthMode.Fixed) =>
        new(100, 100, 40, 20, rotation, mode, true, false,
            [new TextParagraph([new TextRun("text", new TextStyle("Liberation Sans", 10))])], Guid.NewGuid());

    private static (double X, double Y) Centre(TextBlockOverlay b) => (b.XPt + b.WidthPt / 2, b.YPt + b.HeightPt / 2);

    [Fact]
    public void Block_Can_Be_Moved_Resized_And_Rotated()
    {
        Assert.Equal(new OverlayAbilities(true, true, true, CanRotate: true), OverlayGeometry.AbilitiesOf(Block()));
        Assert.Equal(new OverlayBox(100, 100, 40, 20), OverlayGeometry.BoundsOf(Block()));

        var replacement = new TextBlockReplacement(Guid.NewGuid(), Block());
        Assert.True(OverlayGeometry.AbilitiesOf(replacement).CanRotate);
        Assert.Equal(new OverlayBox(100, 100, 40, 20), OverlayGeometry.BoundsOf(replacement));
        Assert.Equal(OverlayAbilities.None, OverlayGeometry.AbilitiesOf(new TextBlockReplacement(Guid.NewGuid(), null)));
    }

    [Fact]
    public void Existing_Objects_Do_Not_Rotate()
    {
        Assert.False(OverlayGeometry.AbilitiesOf(new TextOverlay("t", 0, 0, 10, 0xFF000000, 0)).CanRotate);
        Assert.False(OverlayGeometry.AbilitiesOf(new ImageOverlay([0, 0, 0, 0], 1, 1, 0, 0, 10, 10)).CanRotate);
        Assert.Null(OverlayGeometry.Rotated(new TextOverlay("t", 0, 0, 10, 0xFF000000, 0), 45));
    }

    [Fact]
    public void Moved_Shifts_The_Frame()
    {
        var moved = Assert.IsType<TextBlockOverlay>(OverlayGeometry.Moved(Block(30), 5, -7));
        Assert.Equal((105, 93), (moved.XPt, moved.YPt));
        Assert.Equal(30, moved.RotationDegrees);

        var id = Guid.NewGuid();
        var replacement = Assert.IsType<TextBlockReplacement>(OverlayGeometry.Moved(new TextBlockReplacement(id, Block()), 1, 1));
        Assert.Equal(id, replacement.BlockId);
        Assert.Equal(101, replacement.NewBlock!.XPt);
    }

    [Fact]
    public void Resized_Fixes_The_Width_And_Only_A_Changed_Height()
    {
        var wider = Assert.IsType<TextBlockOverlay>(
            OverlayGeometry.Resized(Block(mode: TextBlockWidthMode.AutoWidth), new OverlayBox(100, 100, 60, 20)));
        Assert.Equal(TextBlockWidthMode.Fixed, wider.WidthMode);
        Assert.Equal(60, wider.WidthPt);
        Assert.True(wider.AutoHeight);

        var taller = Assert.IsType<TextBlockOverlay>(OverlayGeometry.Resized(Block(), new OverlayBox(100, 100, 40, 50)));
        Assert.False(taller.AutoHeight);
        Assert.Equal(50, taller.HeightPt);
    }

    [Fact]
    public void Resizing_A_Rotated_Block_Keeps_The_Opposite_Edge_In_Place()
    {
        // Рамка задаётся в осях самого блока; при повороте на 90° по часовой
        // правый край блока смотрит вниз, а левый должен остаться на месте.
        var block = Block(90);
        var resized = Assert.IsType<TextBlockOverlay>(OverlayGeometry.Resized(block, new OverlayBox(100, 100, 50, 20)));

        Assert.Equal(50, resized.WidthPt);
        var (cx, cy) = Centre(resized);
        Assert.Equal(120, cx, 6);
        Assert.Equal(115, cy, 6);
    }

    [Theory]
    [InlineData(45, 45)]
    [InlineData(-30, 330)]
    [InlineData(720, 0)]
    [InlineData(365, 5)]
    public void Rotated_Normalizes_To_A_Full_Turn(double degrees, double expected)
    {
        var rotated = Assert.IsType<TextBlockOverlay>(OverlayGeometry.Rotated(Block(), degrees));
        Assert.Equal(expected, rotated.RotationDegrees, 6);
        Assert.Equal((100, 100), (rotated.XPt, rotated.YPt));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Display_Mapper_Round_Trips_A_Block_Through_Page_Turns(int turns)
    {
        const double width = 400, height = 300;
        var block = Block(10);
        var (finalW, finalH) = turns % 2 == 0 ? (width, height) : (height, width);

        var (turned, extra) = OverlayDisplayMapper.ToFrame(block, turns, finalW, finalH);
        var t = Assert.IsType<TextBlockOverlay>(turned);
        Assert.Equal(0, extra);
        Assert.Equal((10 + 90.0 * turns) % 360, t.RotationDegrees, 6);
        var expectedCentre = OverlayDisplayMapper.RemapPoint(120, 110, turns, finalW, finalH);
        Assert.Equal(expectedCentre.X, Centre(t).X, 6);
        Assert.Equal(expectedCentre.Y, Centre(t).Y, 6);

        var (back, _) = OverlayDisplayMapper.ToFrame(t with { PlacedRotation = turns }, 0, width, height);
        var b = Assert.IsType<TextBlockOverlay>(back);
        Assert.Equal(block.XPt, b.XPt, 6);
        Assert.Equal(block.YPt, b.YPt, 6);
        Assert.Equal(block.RotationDegrees, b.RotationDegrees, 6);
    }

    [Fact]
    public void Display_Mapper_Turns_The_New_Version_Of_A_Replacement()
    {
        var replacement = new TextBlockReplacement(Guid.NewGuid(), Block());
        var (turned, _) = OverlayDisplayMapper.ToFrame(replacement, 1, 300, 400);
        Assert.Equal(90, Assert.IsType<TextBlockReplacement>(turned).NewBlock!.RotationDegrees, 6);
    }

    [Fact]
    public void Rotate_Handle_Sits_Above_The_Top_Centre_Handle()
    {
        var box = new HandleBox(100, 100, 40, 20);
        var (x, y) = ObjectHandles.CenterOf(box, ResizeHandle.Rotate);
        Assert.Equal((120, 100 - ObjectHandles.RotateHandleOffsetDip), (x, y));

        Assert.Equal(ResizeHandle.Rotate, ObjectHandles.HitTest(box, 120, 80, canResize: true, canRotate: true));
        Assert.Equal(ResizeHandle.None, ObjectHandles.HitTest(box, 120, 80, canResize: true, canRotate: false));
        Assert.Equal(ResizeHandle.Top, ObjectHandles.HitTest(box, 120, 100, canResize: true, canRotate: true));
        // При мелком масштабе ручка отодвигается на те же 20 точек экрана.
        Assert.Equal(ResizeHandle.Rotate, ObjectHandles.HitTest(box, 120, 60, canResize: true, canRotate: true, scale: 2));
    }

    [Theory]
    [InlineData(120, 50, 0)]      // прямо над центром
    [InlineData(170, 110, 90)]    // справа
    [InlineData(120, 160, 180)]   // снизу
    [InlineData(70, 110, 270)]    // слева
    public void Rotation_Angle_Follows_The_Pointer_Clockwise_From_Up(double x, double y, double expected)
    {
        Assert.Equal(expected, ObjectHandles.RotationAngle(new HandleBox(100, 100, 40, 20), x, y), 6);
    }
}
