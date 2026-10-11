using System.Globalization;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.UnitTests;

/// <summary>
/// Арифметика измерений и привязка к чертежу. Проверяется то, что на экране
/// выглядит как «почти правильное число»: перевод листа в натуру, разбор
/// масштаба, площадь, прилипание курсора к концам и пересечениям линий.
/// </summary>
public sealed class MeasurementTests
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    // ----- Лист → натура -----

    [Fact]
    public void Hatch_Of_A_Ship_Drawing_Measures_Its_Catalogue_Length()
    {
        // Чертёж 1:200: люк 22 080 мм — это 110,4 мм на листе, то есть 312,9 пт.
        var paperPt = 22080.0 / 200 / MeasurementMath.PtToMm;
        var metres = MeasurementMath.ToRealLength(paperPt, 200, MeasureUnit.Meter);
        Assert.Equal(22.08, metres, 6);
        Assert.Equal(22080, MeasurementMath.ToRealLength(paperPt, 200, MeasureUnit.Millimeter), 6);
    }

    [Fact]
    public void Area_Scales_With_The_Square_Of_The_Ratio()
    {
        // Квадрат 72×72 пт = 25,4 мм на листе; при 1:100 это 2,54 м → 6,4516 м².
        var square = new[] { new InkPoint(0, 0), new InkPoint(72, 0), new InkPoint(72, 72), new InkPoint(0, 72) };
        var areaPt2 = MeasurementMath.PolygonAreaPt2(square);
        Assert.Equal(72 * 72, areaPt2, 9);
        Assert.Equal(2.54 * 2.54, MeasurementMath.ToRealArea(areaPt2, 100, MeasureUnit.Meter), 9);
    }

    [Fact]
    public void Shoelace_Area_Does_Not_Depend_On_Winding_Direction()
    {
        var clockwise = new[] { new InkPoint(0, 0), new InkPoint(0, 10), new InkPoint(10, 10), new InkPoint(10, 0) };
        var counter = clockwise.Reverse().ToArray();
        Assert.Equal(100, MeasurementMath.PolygonAreaPt2(clockwise), 9);
        Assert.Equal(100, MeasurementMath.PolygonAreaPt2(counter), 9);
        // Треугольник 3-4-5: площадь 6.
        Assert.Equal(6, MeasurementMath.PolygonAreaPt2(new[] { new InkPoint(0, 0), new InkPoint(3, 0), new InkPoint(0, 4) }), 9);
    }

    [Fact]
    public void Perimeter_Closes_Only_When_Asked()
    {
        var points = new[] { new InkPoint(0, 0), new InkPoint(3, 0), new InkPoint(3, 4) };
        Assert.Equal(7, MeasurementMath.PathLengthPt(points, closed: false), 9);
        Assert.Equal(12, MeasurementMath.PathLengthPt(points, closed: true), 9);
    }

    [Fact]
    public void Calibration_Recovers_The_Ratio_From_A_Known_Segment()
    {
        // Люк 22,08 м занял на листе 312,9 пт → масштаб 1:200.
        var paperPt = 22080.0 / 200 / MeasurementMath.PtToMm;
        Assert.Equal(200, MeasurementMath.RatioFromKnownLength(paperPt, 22.08, MeasureUnit.Meter), 6);
        Assert.Equal(200, MeasurementMath.RatioFromKnownLength(paperPt, 22080, MeasureUnit.Millimeter), 6);
        Assert.Equal(0, MeasurementMath.RatioFromKnownLength(0, 22.08, MeasureUnit.Meter));
    }

    [Fact]
    public void Centroid_Of_A_Rectangle_Is_Its_Centre()
    {
        var rect = new[] { new InkPoint(10, 20), new InkPoint(50, 20), new InkPoint(50, 60), new InkPoint(10, 60) };
        var c = MeasurementMath.Centroid(rect);
        Assert.Equal(30, c.XPt, 9);
        Assert.Equal(40, c.YPt, 9);
    }

    // ----- Разбор масштаба и длины -----

    [Theory]
    [InlineData("1:200", 200)]
    [InlineData("1 : 200", 200)]
    [InlineData("1/200", 200)]
    [InlineData("200", 200)]
    [InlineData("1:2,5", 2.5)]
    [InlineData("2:1", 0.5)]
    public void Scale_Is_Read_In_Any_Familiar_Spelling(string text, double expected)
    {
        Assert.True(MeasurementMath.TryParseRatio(text, out var ratio));
        Assert.Equal(expected, ratio, 9);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0:200")]
    [InlineData("1:0")]
    [InlineData("1:2:3")]
    public void Nonsense_Scale_Is_Rejected(string text)
    {
        Assert.False(MeasurementMath.TryParseRatio(text, out _));
    }

    [Fact]
    public void Scale_Is_Shown_The_Way_Title_Blocks_Write_It()
    {
        Assert.Equal("1 : 200", MeasurementMath.FormatRatio(200));
        Assert.Equal("1 : 2.5", MeasurementMath.FormatRatio(2.5));
        Assert.Equal("1 : 2,5", MeasurementMath.FormatRatio(2.5, Ru));
        Assert.Equal("2 : 1", MeasurementMath.FormatRatio(0.5));
    }

    [Theory]
    [InlineData("22080 мм", 22080, MeasureUnit.Millimeter)]
    [InlineData("22,08 м", 22.08, MeasureUnit.Meter)]
    [InlineData("22.08 m", 22.08, MeasureUnit.Meter)]
    [InlineData("72 ft", 72, MeasureUnit.Foot)]
    [InlineData("1 500 мм", 1500, MeasureUnit.Millimeter)]
    [InlineData("22.08", 22.08, MeasureUnit.Meter)]
    public void Typed_Length_Is_Read_With_Or_Without_A_Unit(string text, double value, MeasureUnit unit)
    {
        Assert.True(MeasurementMath.TryParseLength(text, MeasureUnit.Meter, out var parsed, out var parsedUnit));
        Assert.Equal(value, parsed, 9);
        Assert.Equal(unit, parsedUnit);
    }

    [Fact]
    public void Unknown_Unit_Is_Not_Silently_Taken_As_Metres()
    {
        Assert.False(MeasurementMath.TryParseLength("22 parsecs", MeasureUnit.Meter, out _, out _));
    }

    [Fact]
    public void Value_Formatting_Follows_The_Culture()
    {
        Assert.Equal("33,12", MeasurementMath.FormatValue(33.1234, 2, Ru));
        Assert.Equal("33.1", MeasurementMath.FormatValue(33.1234, 1, CultureInfo.InvariantCulture));
    }

    // ----- Shift и угол -----

    [Fact]
    public void Shift_Snaps_The_Direction_To_45_Degrees_Keeping_The_Projection()
    {
        var anchor = new InkPoint(0, 0);
        // Почти горизонталь → ровно горизонталь той же длины по оси.
        var snapped = MeasurementMath.ConstrainTo45(anchor, new InkPoint(100, 3));
        Assert.Equal(100, snapped.XPt, 6);
        Assert.Equal(0, snapped.YPt, 6);
        // Почти диагональ → ровно 45°.
        var diagonal = MeasurementMath.ConstrainTo45(anchor, new InkPoint(100, 96));
        Assert.Equal(diagonal.XPt, diagonal.YPt, 6);
    }

    [Fact]
    public void Angle_Is_Measured_From_The_Horizontal_With_Screen_Y_Down()
    {
        var a = new InkPoint(0, 0);
        Assert.Equal(0, MeasurementMath.AngleDegrees(a, new InkPoint(10, 0)), 6);
        Assert.Equal(90, MeasurementMath.AngleDegrees(a, new InkPoint(0, -10)), 6);
        Assert.Equal(45, MeasurementMath.AngleDegrees(a, new InkPoint(10, -10)), 6);
        Assert.Equal(135, MeasurementMath.AngleDegrees(a, new InkPoint(-10, -10)), 6);
    }

    // ----- Привязка -----

    private static VectorSnapIndex Cross() => new(new[]
    {
        new PdfSegment(100, 200, 300, 200),   // горизонталь
        new PdfSegment(200, 100, 200, 300),   // вертикаль, пересекает её в (200, 200)
        new PdfSegment(400, 400, 500, 450),   // отдельный наклонный отрезок
    }, 612, 792);

    [Fact]
    public void Cursor_Near_A_Line_End_Snaps_To_The_End()
    {
        var hit = Cross().Find(103, 197, 8);
        Assert.NotNull(hit);
        Assert.Equal(SnapKind.Endpoint, hit!.Value.Kind);
        Assert.Equal(100, hit.Value.X, 9);
        Assert.Equal(200, hit.Value.Y, 9);
    }

    [Fact]
    public void Cursor_Near_A_Crossing_Snaps_To_The_Intersection()
    {
        var hit = Cross().Find(203, 198, 8);
        Assert.NotNull(hit);
        Assert.Equal(SnapKind.Intersection, hit!.Value.Kind);
        Assert.Equal(200, hit.Value.X, 9);
        Assert.Equal(200, hit.Value.Y, 9);
    }

    [Fact]
    public void Cursor_Near_The_Middle_Snaps_To_The_Midpoint()
    {
        // Середина наклонного отрезка (450, 425); рядом нет ни концов, ни пересечений.
        var hit = Cross().Find(452, 421, 8);
        Assert.NotNull(hit);
        Assert.Equal(SnapKind.Midpoint, hit!.Value.Kind);
        Assert.Equal(450, hit.Value.X, 9);
        Assert.Equal(425, hit.Value.Y, 9);
    }

    [Fact]
    public void Cursor_Beside_A_Line_Snaps_Onto_It()
    {
        var hit = Cross().Find(150, 205, 8);
        Assert.NotNull(hit);
        Assert.Equal(SnapKind.OnSegment, hit!.Value.Kind);
        Assert.Equal(150, hit.Value.X, 9);
        Assert.Equal(200, hit.Value.Y, 9);
    }

    [Fact]
    public void Far_From_Any_Line_There_Is_No_Snap()
    {
        Assert.Null(Cross().Find(50, 50, 8));
        Assert.Null(Cross().Find(150, 230, 8)); // 30 пт от линии при радиусе 8
    }

    [Fact]
    public void Ends_Win_Over_The_Line_Body_Even_When_The_Body_Is_Closer()
    {
        // Курсор в 6 пт от конца и в 2 пт от самой линии: берётся конец —
        // чертёжник целится в узел, а не в произвольную точку.
        var hit = Cross().Find(106, 198, 8);
        Assert.Equal(SnapKind.Endpoint, hit!.Value.Kind);
    }

    [Fact]
    public void Long_Diagonals_Are_Found_In_The_Middle_Of_Their_Span()
    {
        // Длинная диагональ через всю страницу: индекс по ячейкам обязан
        // видеть её и там, где она не пересекает ни одной «своей» ячейки целиком.
        var index = new VectorSnapIndex(new[] { new PdfSegment(0, 0, 600, 780) }, 612, 792, cellPt: 32);
        // Четверть длины: далеко и от концов, и от середины (300, 390).
        var hit = index.Find(150, 196, 6);
        Assert.NotNull(hit);
        Assert.Equal(SnapKind.OnSegment, hit!.Value.Kind);
        // Проекция курсора на диагональ: (150,5; 195,6).
        Assert.Equal(150.5, hit.Value.X, 0.6);
        Assert.Equal(195.6, hit.Value.Y, 0.6);
    }

    [Fact]
    public void Segment_Intersection_Ignores_Parallel_And_Disjoint_Pairs()
    {
        Assert.False(VectorSnapIndex.TryIntersect(
            new PdfSegment(0, 0, 10, 0), new PdfSegment(0, 5, 10, 5), out _, out _));
        Assert.False(VectorSnapIndex.TryIntersect(
            new PdfSegment(0, 0, 10, 0), new PdfSegment(20, -5, 20, 5), out _, out _));
        Assert.True(VectorSnapIndex.TryIntersect(
            new PdfSegment(0, 0, 10, 10), new PdfSegment(0, 10, 10, 0), out var x, out var y));
        Assert.Equal(5, x, 9);
        Assert.Equal(5, y, 9);
    }

    // ----- Измерение как объект страницы -----

    private static MeasurementDraft Distance() => new(
        MeasurementKind.Distance, new[] { new InkPoint(100, 100), new InkPoint(400, 100) },
        200, MeasureUnit.Meter, 2, "33,12 м", 0xFFDC2626, "тест");

    [Fact]
    public void A_Measurement_Can_Be_Moved_But_Not_Resized()
    {
        var abilities = OverlayGeometry.AbilitiesOf(Distance());
        Assert.True(abilities.CanSelect);
        Assert.True(abilities.CanMove);
        Assert.False(abilities.CanResize); // растянутый размер врал бы о длине

        var moved = (MeasurementDraft)OverlayGeometry.Moved(Distance(), 10, -5)!;
        Assert.Equal(110, moved.Points[0].XPt, 9);
        Assert.Equal(95, moved.Points[0].YPt, 9);
        Assert.Equal("33,12 м", moved.Label);
        Assert.Null(OverlayGeometry.Resized(Distance(), new OverlayBox(0, 0, 10, 10)));
    }

    [Fact]
    public void Measurement_Bounds_Cover_The_Points_With_Room_For_The_Label()
    {
        var box = OverlayGeometry.BoundsOf(Distance())!.Value;
        Assert.True(box.XPt < 100 && box.Right > 400);
        Assert.True(box.YPt < 100 && box.Bottom > 100);
    }

    [Fact]
    public void Measurement_Points_Follow_A_Page_Rotation_And_Keep_Their_Length()
    {
        // Портрет 600×800 → альбом 800×600 после четверти оборота.
        var (remapped, _) = OverlayDisplayMapper.ToFrame(Distance(), 1, 800, 600);
        var turned = (MeasurementDraft)remapped;
        Assert.Equal(300, MeasurementMath.PathLengthPt(turned.Points, false), 6);
        // Горизонталь стала вертикалью.
        Assert.Equal(turned.Points[0].XPt, turned.Points[1].XPt, 6);
    }
}
