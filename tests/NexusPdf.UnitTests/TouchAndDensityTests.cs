using NexusPdf.Ux;

namespace NexusPdf.UnitTests;

/// <summary>
/// Плотность интерфейса и разбор жестов. Проверяется то, что ломается молча:
/// автоматика перебивает выбор пользователя, цели становятся меньше пальца,
/// щипок срабатывает от дрожания руки.
/// </summary>
public sealed class TouchAndDensityTests
{
    [Fact]
    public void Explicit_Choice_Always_Wins_Over_Automatic_Detection()
    {
        // Пользователь выбрал «плотно» — касание пальцем не имеет права
        // переключить интерфейс обратно.
        Assert.Equal(UiDensity.Compact,
            DensityPolicy.Resolve("compact", touchUsedRecently: true, hasTouchScreen: true));
        Assert.Equal(UiDensity.Touch,
            DensityPolicy.Resolve("touch", touchUsedRecently: false, hasTouchScreen: false));
    }

    [Fact]
    public void Touch_Screen_Alone_Does_Not_Enlarge_The_Interface()
    {
        // У трансформера сенсор есть всегда, а работают на нём обычно мышью.
        Assert.Equal(UiDensity.Comfortable,
            DensityPolicy.Resolve("auto", touchUsedRecently: false, hasTouchScreen: true));
    }

    [Fact]
    public void First_Touch_Switches_To_Finger_Sized_Targets()
    {
        Assert.Equal(UiDensity.Touch,
            DensityPolicy.Resolve("auto", touchUsedRecently: true, hasTouchScreen: true));
    }

    [Fact]
    public void Unknown_Setting_Falls_Back_To_Automatic_Instead_Of_Crashing()
    {
        Assert.Null(DensityPolicy.Parse("огромный"));
        Assert.Equal(UiDensity.Comfortable,
            DensityPolicy.Resolve("огромный", touchUsedRecently: false, hasTouchScreen: false));
    }

    [Fact]
    public void Setting_Round_Trips_Through_Text()
    {
        foreach (var density in Enum.GetValues<UiDensity>())
            Assert.Equal(density, DensityPolicy.Parse(DensityPolicy.ToSetting(density)));
    }

    [Fact]
    public void Touch_Targets_Are_At_Least_Forty_Four_Points()
    {
        // 44 точки ≈ 9 мм — минимум, ниже которого палец начинает промахиваться.
        Assert.True(UiMetrics.Touch.TouchTarget >= 44);
        Assert.True(UiMetrics.Touch.RowHeight >= 44);
        Assert.True(UiMetrics.Touch.Gap >= 4, "между целями нужен зазор, иначе жмётся соседняя");
    }

    [Fact]
    public void Density_Steps_Grow_Monotonically()
    {
        Assert.True(UiMetrics.Compact.TouchTarget < UiMetrics.Comfortable.TouchTarget);
        Assert.True(UiMetrics.Comfortable.TouchTarget < UiMetrics.Touch.TouchTarget);
        Assert.True(UiMetrics.Compact.FontSize <= UiMetrics.Comfortable.FontSize);
        Assert.True(UiMetrics.Comfortable.FontSize < UiMetrics.Touch.FontSize);
    }

    // ----- Жесты -----

    [Fact]
    public void Small_Finger_Tremor_Is_Not_A_Zoom()
    {
        Assert.Equal(GestureKind.Pan,
            TouchGestures.Classify(fingerCount: 2, scaleDelta: 1.005, movedDip: 3, heldMs: 100));
    }

    [Fact]
    public void Real_Pinch_Is_A_Zoom()
    {
        Assert.Equal(GestureKind.Zoom,
            TouchGestures.Classify(fingerCount: 2, scaleDelta: 1.2, movedDip: 20, heldMs: 200));
    }

    [Fact]
    public void Holding_A_Still_Finger_Opens_The_Context_Menu()
    {
        Assert.Equal(GestureKind.LongPress,
            TouchGestures.Classify(fingerCount: 1, scaleDelta: 1, movedDip: 2, heldMs: 500));
    }

    [Fact]
    public void Moving_Finger_Never_Becomes_A_Long_Press()
    {
        // Иначе меню выскакивало бы посреди прокрутки.
        Assert.Equal(GestureKind.Pan,
            TouchGestures.Classify(fingerCount: 1, scaleDelta: 1, movedDip: 40, heldMs: 900));
    }

    [Fact]
    public void Zoom_Stays_Inside_The_Same_Limits_As_The_Mouse_Wheel()
    {
        Assert.Equal(TouchGestures.MaxZoom, TouchGestures.ApplyZoom(3.9, 2.0));
        Assert.Equal(TouchGestures.MinZoom, TouchGestures.ApplyZoom(0.3, 0.1));
        Assert.Equal(1.5, TouchGestures.ApplyZoom(1.0, 1.5), 3);
    }

    [Fact]
    public void Pen_Pressure_Changes_Stroke_Width_But_Never_To_Zero()
    {
        Assert.Equal(2.0, TouchGestures.StrokeWidthFromPressure(2.0, 0));      // мышь: нажима нет
        Assert.True(TouchGestures.StrokeWidthFromPressure(2.0, 0.2) < 2.0);
        Assert.True(TouchGestures.StrokeWidthFromPressure(2.0, 1.0) > 2.0);
        Assert.True(TouchGestures.StrokeWidthFromPressure(2.0, 5.0) <= 3.0,
            "нажим за пределами шкалы не должен превращать линию в кляксу");
    }
}
