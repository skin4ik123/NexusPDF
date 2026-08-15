using NexusPdf.Infrastructure;
using NexusPdf.Printing;

namespace NexusPdf.UnitTests;

/// <summary>
/// Калибровка физического размера. Ошибка знака здесь делает чертёж хуже, чем
/// без калибровки вовсе, поэтому направление поправки проверяется явно.
/// </summary>
public sealed class CalibrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));

    private CalibrationStore NewStore()
    {
        Directory.CreateDirectory(_dir);
        return new CalibrationStore(Path.Combine(_dir, "calib.json"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Short_Print_Is_Corrected_By_Enlarging()
    {
        // Напечатали 98 мм вместо 100 — содержимое надо УВЕЛИЧИТЬ.
        var calibration = Calibration.FromMeasurements("HP", "A4", "", 98, 100);
        Assert.True(calibration.ScaleX > 1.0, "заниженный оттиск требует увеличения");
        Assert.Equal(100.0 / 98.0, calibration.ScaleX, 6);
        Assert.Equal(1.0, calibration.ScaleY, 6);
    }

    [Fact]
    public void Long_Print_Is_Corrected_By_Shrinking()
    {
        var calibration = Calibration.FromMeasurements("HP", "A4", "", 100, 102);
        Assert.True(calibration.ScaleY < 1.0, "завышенный оттиск требует уменьшения");
    }

    [Fact]
    public void Exact_Print_Needs_No_Correction()
    {
        var calibration = Calibration.FromMeasurements("HP", "A4", "", 100, 100);
        Assert.True(calibration.IsIdentity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(0.5)]
    public void Nonsense_Measurement_Is_Rejected(double measured)
    {
        // Нулевое измерение обнулило бы всю печать, отрицательное — отзеркалило.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Calibration.FromMeasurements("HP", "A4", "", measured, 100));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(130)]
    public void Correction_Beyond_Ten_Percent_Is_Rejected(double measured)
    {
        // Такой поправки принтеры не дают: почти наверняка измеряли не то.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Calibration.FromMeasurements("HP", "A4", "", measured, 100));
    }

    [Fact]
    public void Calibration_Is_Stored_Per_Printer_Paper_And_Tray()
    {
        var store = NewStore();
        store.Save(Calibration.FromMeasurements("HP", "A4", "Лоток 1", 99, 100));
        store.Save(Calibration.FromMeasurements("HP", "A3", "Лоток 1", 101, 100));

        var a4 = NewStore().Find("HP", "A4", "Лоток 1", "").Calibration;
        var a3 = NewStore().Find("HP", "A3", "Лоток 1", "").Calibration;

        Assert.NotNull(a4);
        Assert.NotNull(a3);
        Assert.True(a4!.ScaleX > 1.0);
        Assert.True(a3!.ScaleX < 1.0);

        // Другой лоток — своя калибровка, а не чужая.
        Assert.Null(NewStore().Find("HP", "A4", "Лоток 2", "").Calibration);
    }

    [Fact]
    public void Driver_Change_Is_Reported_Not_Hidden()
    {
        var store = NewStore();
        store.Save(Calibration.FromMeasurements("HP", "A4", "", 99, 100, driverName: "HP v1"));

        var (calibration, changed) = NewStore().Find("HP", "A4", "", "HP v2");
        Assert.NotNull(calibration);
        Assert.True(changed, "смена драйвера обязана быть замечена");

        var same = NewStore().Find("HP", "A4", "", "HP v1");
        Assert.False(same.DriverChanged);
    }

    [Fact]
    public void Saving_Twice_Replaces_Rather_Than_Duplicates()
    {
        var store = NewStore();
        store.Save(Calibration.FromMeasurements("HP", "A4", "", 99, 100));
        store.Save(Calibration.FromMeasurements("HP", "A4", "", 101, 100));

        Assert.Single(NewStore().LoadAll());
        Assert.True(NewStore().Find("HP", "A4", "", "").Calibration!.ScaleX < 1.0);
    }

    // ----- Тестовая страница -----

    private static readonly PaperSizeOption A4 = new("A4", new SizePt(595.28, 841.89));

    private static PrinterCapabilities Caps() => new()
    {
        PrinterName = "Тест",
        PaperSizes = new[] { A4 },
        HardMarginsByPaper = new Dictionary<string, MarginsPt> { ["A4"] = MarginsPt.Uniform(14.17) },
    };

    [Fact]
    public void Test_Sheet_Has_No_Document_Content()
    {
        // Калибровка проверяет принтер, а не файл: страниц документа на ней нет.
        var sheet = Calibration.BuildTestSheet(A4, Caps());
        Assert.Empty(sheet.Pages);
        Assert.NotEmpty(sheet.Marks);
    }

    [Fact]
    public void Rulers_Are_Exactly_One_Hundred_Millimetres()
    {
        var sheet = Calibration.BuildTestSheet(A4, Caps());
        var expected = Units.UnitToPoints(100, LengthUnit.Millimeters);

        // Горизонтальная линейка: линия нужной длины без высоты.
        Assert.Contains(sheet.Marks, m =>
            m.Kind == "cut" && m.AreaPt.HeightPt == 0 && Math.Abs(m.AreaPt.WidthPt - expected) < 0.01);

        // Вертикальная: линия нужной высоты без ширины.
        Assert.Contains(sheet.Marks, m =>
            m.Kind == "cut" && m.AreaPt.WidthPt == 0 && Math.Abs(m.AreaPt.HeightPt - expected) < 0.01);
    }

    [Fact]
    public void Everything_Stays_Inside_The_Printable_Area()
    {
        var sheet = Calibration.BuildTestSheet(A4, Caps());
        foreach (var mark in sheet.Marks)
        {
            // Метка ниже печатаемой области не напечаталась бы, и калибровка
            // измеряла бы обрезанную линейку.
            Assert.True(mark.AreaPt.XPt >= sheet.PrintableAreaPt.XPt - 0.01,
                $"метка {mark.Kind} {mark.AreaPt} левее печатаемой области");
            Assert.True(mark.AreaPt.BottomPt <= sheet.PrintableAreaPt.BottomPt + 0.01,
                $"метка {mark.Kind} {mark.AreaPt} ниже печатаемой области");
        }
    }

    [Fact]
    public void Test_Job_Is_A_Single_Sheet()
    {
        var job = Calibration.BuildTestJob(A4, Caps());
        Assert.Single(job.Sheets);
        Assert.Equal(1, job.SheetCount);
    }
}
