using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Abstractions.TextBlocks;

namespace NexusPdf.UnitTests.TextBlocks;

/// <summary>
/// Рамку блока двигают, растягивают и поворачивают прямо во время набора:
/// край рамки — перенос, квадраты — размер, кружок над рамкой — поворот,
/// внутри — текст. Допуски задаются в экранных точках, поэтому при
/// масштабе 2 они вдвое меньше в пунктах страницы.
/// </summary>
public sealed class BlockFrameDragTests
{
    private static readonly OverlayBox Frame = new(0, 0, 100, 50);

    private static (FrameZone, FrameHandle) Hit(double x, double y, double zoom) =>
        BlockFrameDrag.HitTest(Frame, x, y, borderPt: 6 / zoom, handlePt: 6 / zoom, rotateOffsetPt: 20 / zoom);

    private static TextBlockOverlay Block(double rotation = 0) =>
        new(100, 100, 40, 20, rotation, TextBlockWidthMode.AutoWidth, true, false,
            [new TextParagraph([new TextRun("text", new TextStyle("Liberation Sans", 10))])], Guid.NewGuid());

    private static (double X, double Y) Centre(TextBlockOverlay b) => (b.XPt + b.WidthPt / 2, b.YPt + b.HeightPt / 2);

    [Theory]
    [InlineData(1, 50, 25, FrameZone.Inside, FrameHandle.None)]
    [InlineData(1, 2.5, 10, FrameZone.Border, FrameHandle.None)]
    [InlineData(2, 2.5, 10, FrameZone.Inside, FrameHandle.None)]
    [InlineData(2, 1.4, 10, FrameZone.Border, FrameHandle.None)]
    [InlineData(1, 20, 52, FrameZone.Border, FrameHandle.None)]
    [InlineData(1, 0, 0, FrameZone.Handle, FrameHandle.TopLeft)]
    [InlineData(1, 50, 0, FrameZone.Handle, FrameHandle.Top)]
    [InlineData(1, 101, 25, FrameZone.Handle, FrameHandle.Right)]
    [InlineData(2, 99, 49, FrameZone.Handle, FrameHandle.BottomRight)]
    [InlineData(1, 0, 25, FrameZone.Handle, FrameHandle.Left)]
    [InlineData(1, 50, -20, FrameZone.Rotate, FrameHandle.None)]
    [InlineData(2, 50, -10, FrameZone.Rotate, FrameHandle.None)]
    [InlineData(2, 50, -20, FrameZone.Outside, FrameHandle.None)]
    [InlineData(1, 50, -40, FrameZone.Outside, FrameHandle.None)]
    [InlineData(1, 120, 25, FrameZone.Outside, FrameHandle.None)]
    [InlineData(1, -2, 80, FrameZone.Outside, FrameHandle.None)]
    public void Hit_Test_Finds_The_Zone_At_Any_Zoom(double zoom, double x, double y, FrameZone zone, FrameHandle handle)
    {
        Assert.Equal((zone, handle), Hit(x, y, zoom));
    }

    [Fact]
    public void Border_Drag_Moves_The_Frame()
    {
        var moved = BlockFrameDrag.Apply(Block(30), FrameZone.Border, FrameHandle.None, 5, -7, 0, 0);

        Assert.Equal((105.0, 93.0, 40.0, 20.0), (moved.XPt, moved.YPt, moved.WidthPt, moved.HeightPt));
        Assert.Equal(30, moved.RotationDegrees);
        Assert.Equal(TextBlockWidthMode.AutoWidth, moved.WidthMode);
        Assert.True(moved.AutoHeight);
    }

    [Fact]
    public void Handle_Drag_Fixes_The_Width_And_Only_A_Dragged_Height()
    {
        var wider = BlockFrameDrag.Apply(Block(), FrameZone.Handle, FrameHandle.Right, 20, 3, 0, 0);
        Assert.Equal((100.0, 100.0, 60.0, 20.0), (wider.XPt, wider.YPt, wider.WidthPt, wider.HeightPt));
        Assert.Equal(TextBlockWidthMode.Fixed, wider.WidthMode);
        Assert.True(wider.AutoHeight);

        var taller = BlockFrameDrag.Apply(Block(), FrameZone.Handle, FrameHandle.Bottom, 0, 30, 0, 0);
        Assert.Equal(50, taller.HeightPt);
        Assert.False(taller.AutoHeight);

        var fromTopLeft = BlockFrameDrag.Apply(Block(), FrameZone.Handle, FrameHandle.TopLeft, -10, -5, 0, 0);
        Assert.Equal((90.0, 95.0, 50.0, 25.0), (fromTopLeft.XPt, fromTopLeft.YPt, fromTopLeft.WidthPt, fromTopLeft.HeightPt));
    }

    [Fact]
    public void Handle_Drag_On_A_Rotated_Block_Keeps_The_Opposite_Edge()
    {
        // Повёрнутый на 90° по часовой блок: его правый край смотрит вниз, и
        // мышь, ушедшая на 10 пт вниз, растягивает его на 10 пт; левый край
        // (теперь верхний) остаётся на месте — те же числа, что в TextBlockGeometryTests.
        var resized = BlockFrameDrag.Apply(Block(90), FrameZone.Handle, FrameHandle.Right, 0, 10, 0, 0);

        Assert.Equal(50, resized.WidthPt, 6);
        var (cx, cy) = Centre(resized);
        Assert.Equal(120, cx, 6);
        Assert.Equal(115, cy, 6);
    }

    [Fact]
    public void Rotate_Drag_Snaps_To_Right_Angles_Within_3_Degrees()
    {
        var block = Block();
        var (cx, cy) = Centre(block);
        (double X, double Y) At(double degrees) =>
            (cx + 50 * Math.Sin(degrees * Math.PI / 180), cy - 50 * Math.Cos(degrees * Math.PI / 180));

        var near = At(88);
        Assert.Equal(90, BlockFrameDrag.Apply(block, FrameZone.Rotate, FrameHandle.None, 0, 0, near.X, near.Y).RotationDegrees, 6);
        var far = At(85);
        Assert.Equal(85, BlockFrameDrag.Apply(block, FrameZone.Rotate, FrameHandle.None, 0, 0, far.X, far.Y).RotationDegrees, 6);
        var back = At(-1.5);
        Assert.Equal(0, BlockFrameDrag.Apply(block, FrameZone.Rotate, FrameHandle.None, 0, 0, back.X, back.Y).RotationDegrees, 6);
    }

    [Fact]
    public void Inside_And_Outside_Leave_The_Block_As_It_Was()
    {
        var block = Block(15);
        Assert.Same(block, BlockFrameDrag.Apply(block, FrameZone.Inside, FrameHandle.None, 5, 5, 0, 0));
        Assert.Same(block, BlockFrameDrag.Apply(block, FrameZone.Outside, FrameHandle.None, 5, 5, 0, 0));
    }
}
