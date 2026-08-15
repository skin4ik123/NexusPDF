namespace NexusPdf.Printing;

/// <summary>Виды типографских меток.</summary>
[Flags]
public enum PrinterMarks
{
    None = 0,

    /// <summary>Уголки по границе обрезки — по ним режут стопку.</summary>
    CropMarks = 1,

    /// <summary>Метки границы блока после обрезки (TrimBox).</summary>
    TrimMarks = 2,

    /// <summary>Приводочные кресты для совмещения красок.</summary>
    RegistrationMarks = 4,

    /// <summary>Метки границы вылета (BleedBox).</summary>
    BleedMarks = 8,

    /// <summary>Подпись листа: имя файла, страница, дата.</summary>
    PageInformation = 16,

    /// <summary>Линии сгиба для буклета.</summary>
    FoldMarks = 32,
}

/// <summary>Настройки типографской подготовки листа.</summary>
public sealed record MarkSettings
{
    public PrinterMarks Marks { get; init; } = PrinterMarks.None;

    /// <summary>Длина штриха метки.</summary>
    public double LengthPt { get; init; } = 14.17; // 5 мм

    /// <summary>Отступ метки от границы содержимого, чтобы она не легла на него.</summary>
    public double OffsetPt { get; init; } = 5.67; // 2 мм

    public double ThicknessPt { get; init; } = 0.5;

    /// <summary>Вылет за границу обрезки. 0 — вылета нет.</summary>
    public double BleedPt { get; init; }

    /// <summary>Текст подписи листа; поддерживает те же подстановки, что и наложения.</summary>
    public string PageInfoTemplate { get; init; } = "{file} · лист {sheet} из {sheets} · {date}";
}

/// <summary>Где на листе размещается печатное наложение.</summary>
public enum OverlayPosition
{
    TopLeft, TopCenter, TopRight,
    BottomLeft, BottomCenter, BottomRight,
}

/// <summary>На каких листах показывать наложение.</summary>
public enum OverlayScope
{
    AllSheets,
    FirstSheetOnly,
    LastSheetOnly,
    OddSheets,
    EvenSheets,
}

/// <summary>
/// Надпись, которая появляется ТОЛЬКО в печатном задании. Исходный PDF не
/// меняется: наложение живёт в плане печати и рисуется поверх листа.
/// </summary>
public sealed record PrintOverlay
{
    public required string Template { get; init; }
    public OverlayPosition Position { get; init; } = OverlayPosition.BottomRight;
    public OverlayScope Scope { get; init; } = OverlayScope.AllSheets;
    public double FontSizePt { get; init; } = 8;
    public uint ColorArgb { get; init; } = 0xFF808080;
    public double MarginPt { get; init; } = 14.17;

    public bool AppliesTo(int sheetIndex, int sheetCount) => Scope switch
    {
        OverlayScope.FirstSheetOnly => sheetIndex == 0,
        OverlayScope.LastSheetOnly => sheetIndex == sheetCount - 1,
        // Чётность считается по номеру листа для человека, с единицы.
        OverlayScope.OddSheets => (sheetIndex + 1) % 2 == 1,
        OverlayScope.EvenSheets => (sheetIndex + 1) % 2 == 0,
        _ => true,
    };
}

/// <summary>Подстановки для меток и наложений.</summary>
public sealed record OverlayContext(
    string FileName,
    int SheetNumber,
    int SheetCount,
    int CopyNumber,
    int CopyCount,
    string Date,
    string PrinterName,
    string UserName);

/// <summary>
/// Подставляет значения в шаблон наложения. Набор полей закрытый: подставлять
/// произвольные данные документа в печатный колонтитул нельзя — так в лист
/// попадает то, чего пользователь не выбирал.
/// </summary>
public static class OverlayTemplate
{
    public static string Render(string template, OverlayContext context)
    {
        if (string.IsNullOrEmpty(template)) return "";
        return template
            .Replace("{file}", context.FileName)
            .Replace("{sheet}", context.SheetNumber.ToString())
            .Replace("{sheets}", context.SheetCount.ToString())
            .Replace("{copy}", context.CopyNumber.ToString())
            .Replace("{copies}", context.CopyCount.ToString())
            .Replace("{date}", context.Date)
            .Replace("{printer}", context.PrinterName)
            .Replace("{user}", context.UserName);
    }

    /// <summary>Подстановки, которые понимает шаблон, — для подсказки в интерфейсе.</summary>
    public static readonly IReadOnlyList<string> Placeholders = new[]
    {
        "{file}", "{sheet}", "{sheets}", "{copy}", "{copies}", "{date}", "{printer}", "{user}",
    };
}

/// <summary>
/// Строит типографские метки для листа. Метки рассчитываются от области
/// содержимого, а не от края бумаги: они обозначают линию реза, и привязка к
/// бумаге сделала бы их бессмысленными при любом смещении раскладки.
/// </summary>
public static class MarkBuilder
{
    public static IReadOnlyList<SheetMark> Build(
        SheetPlan sheet, MarkSettings settings, OverlayContext context)
    {
        if (settings.Marks == PrinterMarks.None || sheet.Pages.Count == 0)
            return Array.Empty<SheetMark>();

        // Область содержимого — объединение всех размещённых страниц.
        var left = sheet.Pages.Min(p => p.TargetRectPt.XPt);
        var top = sheet.Pages.Min(p => p.TargetRectPt.YPt);
        var right = sheet.Pages.Max(p => p.TargetRectPt.RightPt);
        var bottom = sheet.Pages.Max(p => p.TargetRectPt.BottomPt);
        var content = new RectPt(left, top, right - left, bottom - top);

        var marks = new List<SheetMark>();

        if (settings.Marks.HasFlag(PrinterMarks.CropMarks))
            AddCorners(marks, "crop", content, settings);

        if (settings.Marks.HasFlag(PrinterMarks.TrimMarks))
            AddCorners(marks, "trim", content, settings);

        if (settings.Marks.HasFlag(PrinterMarks.BleedMarks) && settings.BleedPt > 0)
        {
            var bleed = new RectPt(
                content.XPt - settings.BleedPt, content.YPt - settings.BleedPt,
                content.WidthPt + settings.BleedPt * 2, content.HeightPt + settings.BleedPt * 2);
            AddCorners(marks, "bleed", bleed, settings);
        }

        if (settings.Marks.HasFlag(PrinterMarks.RegistrationMarks))
        {
            // Кресты по центрам сторон — по ним совмещают краски.
            var size = settings.LengthPt;
            var offset = settings.OffsetPt + size;
            marks.Add(new SheetMark("registration",
                new RectPt(content.XPt + content.WidthPt / 2 - size / 2, content.YPt - offset, size, size)));
            marks.Add(new SheetMark("registration",
                new RectPt(content.XPt + content.WidthPt / 2 - size / 2, content.BottomPt + offset - size, size, size)));
            marks.Add(new SheetMark("registration",
                new RectPt(content.XPt - offset, content.YPt + content.HeightPt / 2 - size / 2, size, size)));
            marks.Add(new SheetMark("registration",
                new RectPt(content.RightPt + offset - size, content.YPt + content.HeightPt / 2 - size / 2, size, size)));
        }

        if (settings.Marks.HasFlag(PrinterMarks.FoldMarks) && sheet.Pages.Count == 2)
        {
            // Сгиб буклета проходит между двумя половинами листа.
            var foldX = (sheet.Pages[0].TargetRectPt.RightPt + sheet.Pages[1].TargetRectPt.XPt) / 2;
            marks.Add(new SheetMark("fold",
                new RectPt(foldX, content.YPt - settings.OffsetPt - settings.LengthPt, 0, settings.LengthPt)));
            marks.Add(new SheetMark("fold",
                new RectPt(foldX, content.BottomPt + settings.OffsetPt, 0, settings.LengthPt)));
        }

        if (settings.Marks.HasFlag(PrinterMarks.PageInformation))
        {
            var text = OverlayTemplate.Render(settings.PageInfoTemplate, context);
            marks.Add(new SheetMark("page-info",
                new RectPt(content.XPt, sheet.PaperSizePt.HeightPt - settings.OffsetPt - 8,
                    content.WidthPt, 8), text));
        }

        return marks;
    }

    /// <summary>Уголки в четырёх углах прямоугольника: по два штриха на угол.</summary>
    private static void AddCorners(List<SheetMark> marks, string kind, RectPt area, MarkSettings settings)
    {
        var len = settings.LengthPt;
        var gap = settings.OffsetPt;

        // Горизонтальные штрихи слева и справа от каждого угла.
        foreach (var y in new[] { area.YPt, area.BottomPt })
        {
            marks.Add(new SheetMark(kind, new RectPt(area.XPt - gap - len, y, len, 0)));
            marks.Add(new SheetMark(kind, new RectPt(area.RightPt + gap, y, len, 0)));
        }
        // Вертикальные штрихи выше и ниже каждого угла.
        foreach (var x in new[] { area.XPt, area.RightPt })
        {
            marks.Add(new SheetMark(kind, new RectPt(x, area.YPt - gap - len, 0, len)));
            marks.Add(new SheetMark(kind, new RectPt(x, area.BottomPt + gap, 0, len)));
        }
    }
}
