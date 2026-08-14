using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.UnitTests;

public sealed class OverlayDisplayMapperTests
{
    // Портрет 600x800, довёрнутый на 90° cw → альбом 800x600.
    private const double FinalW = 800;
    private const double FinalH = 600;

    [Fact]
    public void RemapPoint_Quarter_Turn_Moves_Top_To_Right()
    {
        // Точка у верха портретной рамки (600x800) уходит к правому краю альбомной.
        var (x, y) = OverlayDisplayMapper.RemapPoint(100, 10, 1, FinalW, FinalH);
        Assert.Equal(790, x, 3);
        Assert.Equal(100, y, 3);
    }

    [Fact]
    public void Note_Icon_Center_Is_Preserved()
    {
        var note = new NoteAnnotationDraft(100, 10, "т", "а") { PlacedRotation = 0 };
        var (remapped, _) = OverlayDisplayMapper.ToFrame(note, 1, FinalW, FinalH);
        var mapped = (NoteAnnotationDraft)remapped;

        // Центр значка (110, 20) в старой рамке → (780, 110) в новой.
        Assert.Equal(780 - OverlayDisplayMapper.NoteIconSizePt / 2, mapped.XPt, 3);
        Assert.Equal(110 - OverlayDisplayMapper.NoteIconSizePt / 2, mapped.YPt, 3);
    }

    [Fact]
    public void Shape_Corners_Are_Normalized()
    {
        var shape = new ShapeAnnotationDraft(50, 700, 100, 60, 0xFF000000, 0, 2, false, "", "")
        { PlacedRotation = 0 };
        var (remapped, _) = OverlayDisplayMapper.ToFrame(shape, 1, FinalW, FinalH);
        var mapped = (ShapeAnnotationDraft)remapped;

        Assert.True(mapped.WidthPt > 0 && mapped.HeightPt > 0);
        // Прямоугольник у низа портрета → у левого края альбома.
        Assert.Equal(800 - 760, mapped.XPt, 3);      // x = H_old - (y+h) = 800-760
        Assert.Equal(50, mapped.YPt, 3);
        Assert.Equal(60, mapped.WidthPt, 3);          // стороны переставились
        Assert.Equal(100, mapped.HeightPt, 3);
    }

    [Fact]
    public void Delta_Zero_Is_Identity()
    {
        var note = new NoteAnnotationDraft(10, 20, "т", "а") { PlacedRotation = 2 };
        var (remapped, angle) = OverlayDisplayMapper.ToFrame(note, 2, FinalW, FinalH);
        Assert.Same(note, remapped);
        Assert.Equal(0, angle);
    }

    [Fact]
    public void Image_Gets_Extra_Angle_And_Keeps_Center()
    {
        var image = new ImageOverlay(new byte[4], 1, 1, 100, 100, 200, 50) { PlacedRotation = 0 };
        var (remapped, angle) = OverlayDisplayMapper.ToFrame(image, 1, FinalW, FinalH);
        var mapped = (ImageOverlay)remapped;

        Assert.Equal(-90, angle, 3);
        // Центр (200, 125) → (800-125, 200) = (675, 200).
        Assert.Equal(675 - 100, mapped.XPt, 3);
        Assert.Equal(200 - 25, mapped.YPt, 3);
    }
}
