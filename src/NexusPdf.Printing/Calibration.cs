namespace NexusPdf.Printing;

/// <summary>
/// Поправка масштаба и смещения для конкретного сочетания принтера и бумаги.
///
/// Калибровка НЕ общая на принтер: разные форматы и лотки подают бумагу
/// по-разному, и поправка от A4 на конверте даст промах в миллиметрах.
/// </summary>
public sealed record PrintCalibration
{
    public required string PrinterName { get; init; }
    public required string PaperName { get; init; }

    /// <summary>Лоток; пустая строка — любой.</summary>
    public string PaperSource { get; init; } = "";

    public double ScaleX { get; init; } = 1.0;
    public double ScaleY { get; init; } = 1.0;
    public double OffsetXPt { get; init; }
    public double OffsetYPt { get; init; }

    /// <summary>Драйвер на момент калибровки: после его смены поправка может стать неверной.</summary>
    public string DriverName { get; init; } = "";

    public string CreatedOn { get; init; } = "";

    public bool IsIdentity =>
        Math.Abs(ScaleX - 1) < 0.0005 && Math.Abs(ScaleY - 1) < 0.0005 &&
        Math.Abs(OffsetXPt) < 0.1 && Math.Abs(OffsetYPt) < 0.1;

    public string Key => MakeKey(PrinterName, PaperName, PaperSource);

    public static string MakeKey(string printer, string paper, string source) =>
        $"{printer}|{paper}|{source}".ToLowerInvariant();
}

/// <summary>
/// Расчёт поправки по измеренному оттиску и построение тестовой страницы.
/// </summary>
public static class Calibration
{
    /// <summary>Номинальная длина контрольной линейки.</summary>
    public const double NominalMm = 100.0;

    /// <summary>
    /// Поправка из измеренных значений. Если напечатанные 100 мм оказались
    /// 98 мм, содержимое надо УВЕЛИЧИТЬ: масштаб = номинал / измеренное.
    /// </summary>
    public static PrintCalibration FromMeasurements(
        string printerName, string paperName, string paperSource,
        double measuredWidthMm, double measuredHeightMm,
        double offsetXMm = 0, double offsetYMm = 0,
        string driverName = "", string createdOn = "")
    {
        // Нулевое или отрицательное измерение — не поправка, а опечатка;
        // принимать её значило бы обнулить или отзеркалить всю печать.
        if (measuredWidthMm <= 1 || measuredHeightMm <= 1)
            throw new ArgumentOutOfRangeException(nameof(measuredWidthMm),
                "Измеренная длина должна быть больше 1 мм.");

        var scaleX = NominalMm / measuredWidthMm;
        var scaleY = NominalMm / measuredHeightMm;

        // Поправка больше 10 % почти наверняка означает, что линейку приложили
        // не к той метке: такой масштаб принтеры не дают.
        if (scaleX is < 0.9 or > 1.1 || scaleY is < 0.9 or > 1.1)
            throw new ArgumentOutOfRangeException(nameof(measuredWidthMm),
                "Поправка больше 10 % — проверьте, что измерены именно контрольные 100 мм.");

        return new PrintCalibration
        {
            PrinterName = printerName,
            PaperName = paperName,
            PaperSource = paperSource,
            ScaleX = scaleX,
            ScaleY = scaleY,
            OffsetXPt = Units.UnitToPoints(offsetXMm, LengthUnit.Millimeters),
            OffsetYPt = Units.UnitToPoints(offsetYMm, LengthUnit.Millimeters),
            DriverName = driverName,
            CreatedOn = createdOn,
        };
    }

    /// <summary>
    /// Тестовая страница: две линейки по 100 мм, квадрат 100×100 мм, метки
    /// центра и граница печатаемой области. Страница состоит только из меток —
    /// содержимого документа на ней нет, поэтому она печатается одинаково
    /// независимо от того, какой файл открыт.
    /// </summary>
    public static SheetPlan BuildTestSheet(PaperSizeOption paper, PrinterCapabilities capabilities)
    {
        var margins = capabilities.HardMarginsFor(paper);
        var printable = RectPt.FromSize(paper.SizePt).Deflate(margins);
        var marks = new List<SheetMark>();

        var mm = Units.UnitToPoints(1, LengthUnit.Millimeters);
        var length = NominalMm * mm;

        // Начало линеек — в левом верхнем углу печатаемой области с отступом,
        // чтобы штрихи не упирались в самый край.
        var originX = printable.XPt + 20 * mm;
        var originY = printable.YPt + 20 * mm;

        // Горизонтальная линейка с делениями через 10 мм.
        marks.Add(new SheetMark("cut", new RectPt(originX, originY, length, 0)));
        for (var i = 0; i <= 10; i++)
        {
            var tick = i % 5 == 0 ? 5 * mm : 3 * mm;
            marks.Add(new SheetMark("cut", new RectPt(originX + i * 10 * mm, originY, 0, tick)));
        }
        marks.Add(new SheetMark("page-info",
            new RectPt(originX, originY - 8 * mm, length, 5 * mm), "100 ММ ПО ГОРИЗОНТАЛИ"));

        // Вертикальная линейка.
        marks.Add(new SheetMark("cut", new RectPt(originX, originY, 0, length)));
        for (var i = 0; i <= 10; i++)
        {
            var tick = i % 5 == 0 ? 5 * mm : 3 * mm;
            marks.Add(new SheetMark("cut", new RectPt(originX, originY + i * 10 * mm, tick, 0)));
        }
        // Подпись вертикальной линейки — СПРАВА от неё, а не под: снизу она
        // налезала на заголовок квадрата.
        marks.Add(new SheetMark("page-info",
            new RectPt(originX + 12 * mm, originY + length / 2, 60 * mm, 5 * mm), "100 ММ ПО ВЕРТИКАЛИ"));

        // Квадрат 100×100 мм: по нему проверяют обе стороны сразу.
        var squareY = originY + length + 20 * mm;
        marks.Add(new SheetMark("cut", new RectPt(originX, squareY, length, 0)));
        marks.Add(new SheetMark("cut", new RectPt(originX, squareY + length, length, 0)));
        marks.Add(new SheetMark("cut", new RectPt(originX, squareY, 0, length)));
        marks.Add(new SheetMark("cut", new RectPt(originX + length, squareY, 0, length)));
        marks.Add(new SheetMark("registration",
            new RectPt(originX + length / 2 - 4 * mm, squareY + length / 2 - 4 * mm, 8 * mm, 8 * mm)));
        marks.Add(new SheetMark("page-info",
            new RectPt(originX, squareY - 7 * mm, length, 5 * mm), "КВАДРАТ 100 X 100 ММ"));

        // Граница печатаемой области: по ней видно поля принтера.
        marks.Add(new SheetMark("cut", new RectPt(printable.XPt, printable.YPt, printable.WidthPt, 0)));
        marks.Add(new SheetMark("cut", new RectPt(printable.XPt, printable.BottomPt, printable.WidthPt, 0)));
        marks.Add(new SheetMark("cut", new RectPt(printable.XPt, printable.YPt, 0, printable.HeightPt)));
        marks.Add(new SheetMark("cut", new RectPt(printable.RightPt, printable.YPt, 0, printable.HeightPt)));

        marks.Add(new SheetMark("page-info",
            new RectPt(printable.XPt + 5 * mm, printable.BottomPt - 10 * mm, printable.WidthPt, 5 * mm),
            $"{paper.Name} · {capabilities.PrinterName}"));

        return new SheetPlan
        {
            SheetIndex = 0,
            PaperSizePt = paper.SizePt,
            PrintableAreaPt = printable,
            HardMarginsPt = margins,
            Marks = marks,
            // Страниц документа нет: калибровка проверяет принтер, а не файл.
            Pages = Array.Empty<PlacedPage>(),
        };
    }

    /// <summary>Задание печати тестовой страницы.</summary>
    public static PrintJobPlan BuildTestJob(PaperSizeOption paper, PrinterCapabilities capabilities) => new()
    {
        JobName = "Калибровочная страница",
        PrinterName = capabilities.PrinterName,
        Capabilities = capabilities,
        Sheets = new[] { BuildTestSheet(paper, capabilities) },
    };
}
