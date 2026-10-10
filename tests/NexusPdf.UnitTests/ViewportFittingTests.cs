using NexusPdf.Ux;

namespace NexusPdf.UnitTests;

/// <summary>
/// Подгонка масштаба, пиксельная сетка карточек и вписывание окон в экран.
/// Проверяется арифметика, из-за которой страница мылилась, а диалоги
/// уходили под панель задач на 1366×768.
/// </summary>
public sealed class ViewportFittingTests
{
    private const double A4Width = 595.276;
    private const double A4Height = 841.89;

    [Fact]
    public void Fit_To_Width_Uses_The_Whole_Viewport_Minus_Margins()
    {
        // 1097 единиц области, по 24 на поля: лист A4 занимает ровно остаток.
        var zoom = ZoomFitting.ByWidth(1097, A4Width, 24);
        Assert.Equal(1049, A4Width * ZoomFitting.PtToDiu * zoom, 3);
    }

    [Fact]
    public void Fit_To_Page_Is_Limited_By_The_Tighter_Side()
    {
        // Ноутбучная область 1097×600: портретный A4 упирается в высоту.
        var zoom = ZoomFitting.ByPage(1097, 600, A4Width, A4Height, 24);
        Assert.Equal(552, A4Height * ZoomFitting.PtToDiu * zoom, 3);
        Assert.True(A4Width * ZoomFitting.PtToDiu * zoom < 1049);
    }

    [Fact]
    public void Zoom_Never_Leaves_The_Wheel_Limits()
    {
        Assert.Equal(TouchGestures.MaxZoom, ZoomFitting.ByWidth(100000, A4Width, 24));
        Assert.Equal(TouchGestures.MinZoom, ZoomFitting.ByPage(100, 100, A4Width, A4Height, 24));
    }

    [Fact]
    public void Landscape_Pages_Open_Whole_And_Portrait_Pages_Open_By_Width()
    {
        // Слайд по ширине занял бы полтора экрана в высоту; документ по
        // странице читался бы мелко.
        Assert.True(ZoomFitting.PrefersWholePage(A4Height, A4Width));
        Assert.False(ZoomFitting.PrefersWholePage(A4Width, A4Height));
    }

    [Theory]
    [InlineData(1049.37, 1.0, 1049.0)]
    [InlineData(1049.62, 1.0, 1050.0)]
    [InlineData(1049.37, 1.25, 1049.6)]   // 1312 пикселей ÷ 1,25
    [InlineData(0.2, 1.5, 1.0 / 1.5)]     // меньше пикселя не бывает
    public void Card_Sizes_Snap_To_Whole_Device_Pixels(double diu, double dpi, double expected)
    {
        var snapped = ZoomFitting.SnapToPixels(diu, dpi);
        Assert.Equal(expected, snapped, 9);
        Assert.Equal(Math.Round(snapped * dpi), snapped * dpi, 9);
    }

    [Fact]
    public void Broken_Dpi_Falls_Back_To_The_Plain_Grid()
    {
        Assert.Equal(1049.0, ZoomFitting.SnapToPixels(1049.37, 0));
        Assert.Equal(1049.0, ZoomFitting.SnapToPixels(1049.37, double.NaN));
    }

    [Fact]
    public void A_Dialog_Taller_Than_The_Screen_Is_Cut_To_The_Work_Area()
    {
        // Окно печати 1180×820 на ноутбуке 1366×768 при 125 %: область 1093×582.
        var window = new ScreenBox(-44, -119, 1180, 820);
        var work = new ScreenBox(0, 0, 1093, 582);

        var fit = ScreenFit.Fit(window, work, minWidth: 900, minHeight: 600);

        Assert.Equal(1085, fit.Box.Width);
        Assert.Equal(574, fit.Box.Height);
        Assert.Equal(0, fit.Box.Left);
        Assert.Equal(0, fit.Box.Top);
        // Минимум больше экрана опускается: иначе окно нельзя ни уменьшить, ни
        // увидеть целиком. Минимум, который помещается, остаётся прежним.
        Assert.Equal(900, fit.MinWidth);
        Assert.Equal(574, fit.MinHeight);
    }

    [Fact]
    public void A_Window_That_Fits_Is_Left_Alone()
    {
        var window = new ScreenBox(200, 100, 960, 640);
        var work = new ScreenBox(0, 0, 1920, 1040);

        var fit = ScreenFit.Fit(window, work, minWidth: 760, minHeight: 480);

        Assert.Equal(window, fit.Box);
        Assert.Equal(760, fit.MinWidth);
        Assert.Equal(480, fit.MinHeight);
    }

    [Fact]
    public void A_Window_Hanging_Off_The_Edge_Is_Pulled_Back_On_Screen()
    {
        // Второй монитор слева от главного: координаты отрицательные.
        var work = new ScreenBox(-1920, 0, 1920, 1040);
        var window = new ScreenBox(-300, 900, 760, 560);

        var fit = ScreenFit.Fit(window, work, 0, 0);

        Assert.Equal(-760, fit.Box.Left);
        Assert.Equal(480, fit.Box.Top);
        Assert.Equal(760, fit.Box.Width);
        Assert.Equal(560, fit.Box.Height);
    }
}
