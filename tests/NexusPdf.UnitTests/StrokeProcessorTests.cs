using NexusPdf.Application;

namespace NexusPdf.UnitTests;

/// <summary>Стабилизация, прореживание, автовыпрямление и привязка углов.</summary>
public sealed class StrokeProcessorTests
{
    private static StrokePoint P(double x, double y) => new(x, y);

    /// <summary>Насколько точки отходят от той линии, которую человек ВЁЛ (в тестах — y = 100).</summary>
    private static double Jitter(IEnumerable<StrokePoint> points) =>
        points.Max(p => Math.Abs(p.Y - 100));

    [Fact]
    public void Stabilizer_Reduces_Jitter_But_Keeps_Ends()
    {
        // Прямая слева направо с дрожанием ±2 пт по вертикали.
        var raw = new List<StrokePoint>();
        for (var i = 0; i <= 40; i++)
            raw.Add(P(i * 5, 100 + (i % 2 == 0 ? 2 : -2)));

        var smooth = StrokeProcessor.Stabilize(raw);

        Assert.Equal(raw.Count, smooth.Count);
        Assert.Equal(2, Jitter(raw), 6);

        // Фильтр стартует из первой точки и несколько отсчётов «разгоняется»
        // (замерено: на 7-й точке ещё 0,54 при установившихся 0,42), поэтому
        // установившееся дрожание меряем по второй половине штриха, не трогая
        // последнюю точку — она намеренно оставлена сырой.
        var half = smooth.Count / 2;
        var body = smooth.Skip(half).Take(smooth.Count - half - 1).ToList();
        Assert.True(Jitter(body) < 0.5,
            $"установившееся дрожание должно упасть вчетверо, осталось {Jitter(body):0.###}");

        // И ни в одной точке сглаживание не выносит линию ДАЛЬШЕ, чем было.
        Assert.True(Jitter(smooth) <= Jitter(raw) + 1e-9);

        // Концы штриха там, где пользователь нажал и отпустил.
        Assert.Equal(raw[0], smooth[0]);
        Assert.Equal(raw[^1], smooth[^1]);
    }

    [Fact]
    public void Stabilizer_Off_Returns_Points_Unchanged()
    {
        var raw = new[] { P(0, 0), P(10, 3), P(20, 0) };
        Assert.Same(raw, StrokeProcessor.Stabilize(raw, 0));
    }

    [Fact]
    public void Simplify_Drops_Redundant_Points_Without_Changing_Shape()
    {
        // 200 точек ровно по прямой — достаточно двух.
        var raw = Enumerable.Range(0, 200).Select(i => P(i, i * 0.5)).ToList();
        var simplified = StrokeProcessor.Simplify(raw);

        Assert.Equal(2, simplified.Count);
        Assert.Equal(raw[0], simplified[0]);
        Assert.Equal(raw[^1], simplified[^1]);
    }

    [Fact]
    public void Simplify_Keeps_A_Real_Corner()
    {
        var raw = new List<StrokePoint>();
        for (var i = 0; i <= 50; i++) raw.Add(P(i, 0));
        for (var i = 1; i <= 50; i++) raw.Add(P(50, i));

        var simplified = StrokeProcessor.Simplify(raw);

        Assert.Equal(3, simplified.Count);
        Assert.Equal(P(50, 0), simplified[1]); // угол на месте
    }

    [Fact]
    public void Almost_Straight_Stroke_Becomes_A_Segment()
    {
        var raw = new List<StrokePoint>();
        for (var i = 0; i <= 30; i++)
            raw.Add(P(i * 10, 200 + (i % 3 == 0 ? 0.8 : -0.8))); // 300 пт длины, ±0.8 пт шума

        var result = StrokeProcessor.AutoStraighten(raw);

        Assert.True(result.WasStraightened);
        Assert.Equal(2, result.Points.Count);
        Assert.Equal(raw[0], result.Points[0]);
    }

    [Fact]
    public void Curved_Stroke_Is_Left_Alone()
    {
        // Дуга: середина уходит от хорды далеко.
        var raw = new List<StrokePoint>();
        for (var i = 0; i <= 30; i++)
        {
            var t = i / 30.0;
            raw.Add(P(t * 300, 200 - Math.Sin(t * Math.PI) * 60));
        }

        var result = StrokeProcessor.AutoStraighten(raw);

        Assert.False(result.WasStraightened);
        Assert.Same(raw, result.Points); // исходные точки возвращаются как есть
    }

    [Fact]
    public void Short_Scribble_Is_Never_Straightened()
    {
        var raw = new[] { P(0, 0), P(3, 1), P(6, 0) };
        Assert.False(StrokeProcessor.AutoStraighten(raw).WasStraightened);
    }

    [Fact]
    public void Loop_Is_Not_Straightened_Even_Though_Ends_Are_Close()
    {
        // Замкнутая петля: концы рядом, но это не прямая.
        var raw = new List<StrokePoint>();
        for (var i = 0; i <= 36; i++)
        {
            var a = i * 10 * Math.PI / 180;
            raw.Add(P(100 + Math.Cos(a) * 50, 100 + Math.Sin(a) * 50));
        }
        Assert.False(StrokeProcessor.AutoStraighten(raw).WasStraightened);
    }

    [Fact]
    public void Nearly_Horizontal_Line_Snaps_To_Exact_Horizontal()
    {
        var raw = new List<StrokePoint>();
        for (var i = 0; i <= 20; i++)
            raw.Add(P(i * 15, 100 + i * 0.3)); // ~1.1° наклона

        var result = StrokeProcessor.AutoStraighten(raw);

        Assert.True(result.WasStraightened);
        Assert.True(result.WasSnapped);
        Assert.Equal(result.Points[0].Y, result.Points[1].Y, 6);
    }

    [Fact]
    public void Diagonal_Line_Is_Straightened_But_Not_Snapped()
    {
        var raw = new List<StrokePoint>();
        for (var i = 0; i <= 20; i++)
            raw.Add(P(i * 10, 100 + i * 10)); // ровно 45°

        var result = StrokeProcessor.AutoStraighten(raw);

        Assert.True(result.WasStraightened);
        Assert.False(result.WasSnapped);
    }

    [Theory]
    [InlineData(100, 10, 100, 0)]   // почти горизонталь -> 0°, конец под курсором
    [InlineData(100, 80, 90, 90)]   // ближе к 45° -> проекция на диагональ
    [InlineData(10, 100, 0, 100)]   // почти вертикаль -> 90°
    public void Shift_Snaps_Direction_To_45_Degrees(
        double dx, double dy, double expectedX, double expectedY)
    {
        var start = P(0, 0);
        var snapped = StrokeProcessor.SnapTo45(start, P(dx, dy));

        Assert.Equal(expectedX, snapped.X, 6);
        Assert.Equal(expectedY, snapped.Y, 6);
        // Направление действительно кратно 45°.
        var degrees = Math.Atan2(snapped.Y - start.Y, snapped.X - start.X) * 180 / Math.PI;
        Assert.Equal(0, Math.IEEERemainder(degrees, 45), 6);
    }

    [Fact]
    public void Arrow_Head_Has_Two_Barbs_Meeting_At_The_Tip()
    {
        var from = P(0, 0);
        var to = P(100, 0);
        var head = StrokeProcessor.ArrowHead(from, to, 2);

        Assert.Equal(2, head.Count);
        foreach (var barb in head)
        {
            Assert.Equal(2, barb.Count);
            Assert.Equal(to, barb[^1]);                 // усики сходятся в острие
            Assert.True(barb[0].X < to.X);              // и смотрят назад по линии
            Assert.True(StrokeProcessor.Distance(barb[0], to) > 5);
        }
        // Усики по разные стороны от линии.
        Assert.True(head[0][0].Y * head[1][0].Y < 0);
    }

    [Fact]
    public void Arrow_Head_Of_Degenerate_Line_Is_Empty()
    {
        Assert.Empty(StrokeProcessor.ArrowHead(P(5, 5), P(5, 5), 2));
    }

    // ----- Сборка штриха для записи в документ -----

    private static List<StrokePoint> WobblyLine(int count = 30, double noise = 0.8)
    {
        var points = new List<StrokePoint>();
        for (var i = 0; i <= count; i++)
            points.Add(P(i * 10, 200 + (i % 3 == 0 ? noise : -noise)));
        return points;
    }

    [Fact]
    public void Commit_Ignores_A_Stray_Click()
    {
        Assert.Null(StrokeProcessor.Commit(
            new[] { P(10, 10) }, StrokeProcessor.StrokeKind.Pencil, 0.65, true, 2));
        Assert.Null(StrokeProcessor.Commit(
            new[] { P(10, 10), P(10.2, 10.1) }, StrokeProcessor.StrokeKind.Pencil, 0.65, true, 2));
    }

    [Fact]
    public void Commit_Pencil_Straightens_And_Keeps_The_Free_Variant()
    {
        var commit = StrokeProcessor.Commit(
            WobblyLine(), StrokeProcessor.StrokeKind.Pencil, 0.65, autoStraighten: true, 2);

        Assert.NotNull(commit);
        Assert.True(commit!.WasStraightened);
        Assert.Equal(2, Assert.Single(commit.Strokes).Count);
        // Свободный вариант сохранён и он НЕ прямой отрезок из двух точек.
        Assert.True(Assert.Single(commit.FreeStrokes).Count > 2);
    }

    [Fact]
    public void Commit_Pencil_Without_Straightening_Keeps_The_Hand_Drawn_Shape()
    {
        var commit = StrokeProcessor.Commit(
            WobblyLine(), StrokeProcessor.StrokeKind.Pencil, 0.65, autoStraighten: false, 2);

        Assert.NotNull(commit);
        Assert.False(commit!.WasStraightened);
        Assert.True(Assert.Single(commit.Strokes).Count > 2);
    }

    [Fact]
    public void Commit_Line_Uses_Only_The_Two_Ends()
    {
        var commit = StrokeProcessor.Commit(
            WobblyLine(), StrokeProcessor.StrokeKind.Line, 0.65, true, 2);

        var stroke = Assert.Single(commit!.Strokes);
        Assert.Equal(2, stroke.Count);
        Assert.Equal(P(0, 200.8), stroke[0]);
        Assert.Equal(300, stroke[1].X, 6);
    }

    [Fact]
    public void Commit_Arrow_Adds_Two_Barbs_At_The_End()
    {
        var commit = StrokeProcessor.Commit(
            WobblyLine(), StrokeProcessor.StrokeKind.Arrow, 0.65, true, 2);

        Assert.Equal(3, commit!.Strokes.Count); // сама линия + два усика
        var tip = commit.Strokes[0][^1];
        Assert.Equal(tip, commit.Strokes[1][^1]);
        Assert.Equal(tip, commit.Strokes[2][^1]);
    }
}
