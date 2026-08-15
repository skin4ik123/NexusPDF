namespace NexusPdf.Printing;

/// <summary>
/// Превращает список страниц и настройки в готовый план листов.
/// Это единственное место, где считается геометрия печати: и предпросмотр, и
/// отправка в очередь берут результат отсюда, поэтому «в preview одно, на
/// бумаге другое» становится невозможным по устройству, а не по договорённости.
/// </summary>
public sealed class PrintLayoutEngine
{
    /// <param name="pages">Страницы в том порядке, в котором их надо печатать.</param>
    /// <param name="paper">Выбранный размер бумаги.</param>
    /// <param name="capabilities">Возможности принтера — из них берутся непечатаемые поля.</param>
    public IReadOnlyList<SheetPlan> BuildSheets(
        IReadOnlyList<SourcePage> pages,
        LayoutSettings settings,
        PaperSizeOption paper,
        PrinterCapabilities capabilities)
    {
        if (pages.Count == 0)
            return Array.Empty<SheetPlan>();

        return settings.Imposition switch
        {
            ImpositionMode.NUp => BuildNUp(pages, settings, paper, capabilities),
            ImpositionMode.Poster => BuildPoster(pages, settings, paper, capabilities),
            ImpositionMode.Booklet => BuildBooklet(pages, settings, paper, capabilities),
            _ => BuildSingle(pages, settings, paper, capabilities),
        };
    }

    // ----- Общая геометрия листа -----

    /// <summary>
    /// Размер листа с учётом ориентации. Автоматическая ориентация выбирает ту,
    /// при которой страница займёт больше площади: это и есть «минимум
    /// масштабирования» из требований.
    /// </summary>
    private static SizePt ResolvePaperSize(SizePt paperPt, SizePt contentPt, OrientationMode mode)
    {
        switch (mode)
        {
            case OrientationMode.Portrait:
                return paperPt.WidthPt <= paperPt.HeightPt ? paperPt : paperPt.Swapped;
            case OrientationMode.Landscape:
                return paperPt.WidthPt >= paperPt.HeightPt ? paperPt : paperPt.Swapped;
            default:
                var upright = FitScale(contentPt, paperPt);
                var rotated = FitScale(contentPt, paperPt.Swapped);
                return rotated > upright ? paperPt.Swapped : paperPt;
        }
    }

    private static double FitScale(SizePt content, SizePt into)
    {
        if (content.WidthPt <= 0 || content.HeightPt <= 0) return 1.0;
        return Math.Min(into.WidthPt / content.WidthPt, into.HeightPt / content.HeightPt);
    }

    /// <summary>Область, в которую реально кладётся содержимое: печатаемая область минус пользовательские поля.</summary>
    private static RectPt ContentArea(SizePt paperPt, PrinterCapabilities caps, PaperSizeOption paper, LayoutSettings settings)
    {
        var hard = caps.HardMarginsFor(paper);
        // Поля драйвера заданы для его собственной ориентации листа; при повороте
        // на 90° левое поле становится верхним. Без этого содержимое уезжало бы
        // к краю ровно на разницу полей.
        var rotated = paperPt.IsLandscape != paper.SizePt.IsLandscape;
        if (rotated)
            hard = new MarginsPt(hard.TopPt, hard.RightPt, hard.BottomPt, hard.LeftPt);

        var printable = RectPt.FromSize(paperPt).Deflate(hard);
        return printable.Deflate(settings.UserMarginsPt);
    }

    /// <summary>Масштаб содержимого по выбранному режиму размера.</summary>
    private static double ResolveScale(SizePt content, SizePt into, LayoutSettings settings)
    {
        var fit = FitScale(content, into);
        return settings.Size switch
        {
            SizeMode.ActualSize => 1.0,
            SizeMode.Fit => settings.AllowEnlarge ? fit : Math.Min(1.0, fit),
            SizeMode.ShrinkOversized => Math.Min(1.0, fit),
            SizeMode.CustomScale => Math.Max(0.001, settings.CustomScale),
            SizeMode.FillSheet => content.WidthPt <= 0 || content.HeightPt <= 0
                ? 1.0
                : Math.Max(into.WidthPt / content.WidthPt, into.HeightPt / content.HeightPt),
            _ => fit,
        };
    }

    /// <summary>Положение прямоугольника содержимого внутри области по выбранной привязке.</summary>
    private static RectPt Place(SizePt size, RectPt area, LayoutSettings settings)
    {
        double x, y;
        switch (settings.Position)
        {
            case PagePosition.TopLeft: x = area.XPt; y = area.YPt; break;
            case PagePosition.TopCenter: x = area.XPt + (area.WidthPt - size.WidthPt) / 2; y = area.YPt; break;
            case PagePosition.TopRight: x = area.RightPt - size.WidthPt; y = area.YPt; break;
            case PagePosition.MiddleLeft: x = area.XPt; y = area.YPt + (area.HeightPt - size.HeightPt) / 2; break;
            case PagePosition.MiddleRight: x = area.RightPt - size.WidthPt; y = area.YPt + (area.HeightPt - size.HeightPt) / 2; break;
            case PagePosition.BottomLeft: x = area.XPt; y = area.BottomPt - size.HeightPt; break;
            case PagePosition.BottomCenter: x = area.XPt + (area.WidthPt - size.WidthPt) / 2; y = area.BottomPt - size.HeightPt; break;
            case PagePosition.BottomRight: x = area.RightPt - size.WidthPt; y = area.BottomPt - size.HeightPt; break;
            case PagePosition.Custom:
                x = area.XPt + settings.CustomOffsetXPt;
                y = area.YPt + settings.CustomOffsetYPt;
                break;
            default:
                x = area.XPt + (area.WidthPt - size.WidthPt) / 2;
                y = area.YPt + (area.HeightPt - size.HeightPt) / 2;
                break;
        }

        // Калибровка сдвигает уже готовое положение: она компенсирует подачу
        // бумаги конкретным принтером, а не меняет замысел раскладки.
        return new RectPt(
            x + settings.CalibrationOffsetXPt,
            y + settings.CalibrationOffsetYPt,
            size.WidthPt, size.HeightPt);
    }

    /// <summary>Размер содержимого после поворота на кратный 90° угол.</summary>
    private static SizePt Rotate(SizePt size, int degrees) =>
        ((degrees % 360) + 360) % 360 is 90 or 270 ? size.Swapped : size;

    // ----- Одна страница на лист -----

    private IReadOnlyList<SheetPlan> BuildSingle(
        IReadOnlyList<SourcePage> pages, LayoutSettings settings,
        PaperSizeOption paper, PrinterCapabilities caps)
    {
        var sheets = new List<SheetPlan>(pages.Count);
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var effectivePaper = ChoosePaper(page, settings, paper, caps);
            var content = Rotate(page.SizePt, settings.ExtraRotationDegrees);
            var paperPt = ResolvePaperSize(effectivePaper.SizePt, content, settings.Orientation);
            var area = ContentArea(paperPt, caps, effectivePaper, settings);

            var scale = ResolveScale(content, area.Size, settings);
            var scaled = new SizePt(
                content.WidthPt * scale * settings.CalibrationScaleX,
                content.HeightPt * scale * settings.CalibrationScaleY);
            var target = Place(scaled, area, settings);

            sheets.Add(new SheetPlan
            {
                SheetIndex = sheets.Count,
                PaperSizePt = paperPt,
                PrintableAreaPt = RectPt.FromSize(paperPt).Deflate(caps.HardMarginsFor(effectivePaper)),
                HardMarginsPt = caps.HardMarginsFor(effectivePaper),
                Color = settings.Color,
                Pages = new[] { MakePlaced(page, settings, target, area, scale) },
            });
        }
        return sheets;
    }

    /// <summary>Размер бумаги под конкретную страницу — для документов со смешанными форматами.</summary>
    private static PaperSizeOption ChoosePaper(
        SourcePage page, LayoutSettings settings, PaperSizeOption fixedPaper, PrinterCapabilities caps)
    {
        if (settings.PaperSelection != PaperSelectionMode.AutoPerPage)
            return fixedPaper;
        return caps.FindClosest(page.SizePt) ?? fixedPaper;
    }

    private static PlacedPage MakePlaced(
        SourcePage page, LayoutSettings settings, RectPt target, RectPt clipArea, double scale)
    {
        var clip = target.Intersect(clipArea);
        return new PlacedPage
        {
            DocumentId = page.DocumentId,
            SourcePageIndex = page.PageIndex,
            PageLabel = page.Label,
            Box = settings.Box,
            SourceRectPt = RectPt.FromSize(page.SizePt),
            TargetRectPt = target,
            ClipRectPt = clip,
            Scale = scale,
            RotationDegrees = settings.ExtraRotationDegrees,
            Annotations = settings.Annotations,
            Forms = settings.Forms,
            Layers = settings.Layers,
            Raster = settings.PrintAsImage ? RasterReason.UserRequested : RasterReason.None,
        };
    }

    // ----- Несколько страниц на листе -----

    private IReadOnlyList<SheetPlan> BuildNUp(
        IReadOnlyList<SourcePage> pages, LayoutSettings settings,
        PaperSizeOption paper, PrinterCapabilities caps)
    {
        var nup = settings.NUp;
        var rows = Math.Max(1, nup.Rows);
        var columns = Math.Max(1, nup.Columns);
        var perSheet = rows * columns;

        // Ориентация листа выбирается один раз на всё задание: сетка не должна
        // прыгать между листами.
        var reference = pages[0].SizePt;
        var paperPt = ResolvePaperSize(paper.SizePt, reference, settings.Orientation);
        var area = ContentArea(paperPt, caps, paper, settings).Deflate(nup.OuterMarginsPt);

        var cellWidth = (area.WidthPt - nup.HorizontalGapPt * (columns - 1)) / columns;
        var cellHeight = (area.HeightPt - nup.VerticalGapPt * (rows - 1)) / rows;
        if (cellWidth <= 0 || cellHeight <= 0)
            return Array.Empty<SheetPlan>();
        var cell = new SizePt(cellWidth, cellHeight);

        // Единый масштаб считается по самой большой странице задания — иначе
        // на одном листе окажутся страницы разной величины.
        double? uniform = null;
        if (nup.UniformScale)
        {
            uniform = double.MaxValue;
            foreach (var page in pages)
            {
                var content = ContentForCell(page, settings, nup, cell);
                uniform = Math.Min(uniform.Value, Math.Min(1.0, FitScale(content, cell)));
            }
        }

        var sheets = new List<SheetPlan>();
        for (var start = 0; start < pages.Count; start += perSheet)
        {
            var placed = new List<PlacedPage>();
            for (var slot = 0; slot < perSheet; slot++)
            {
                var pageIndex = start + slot;
                if (pageIndex >= pages.Count) break;
                var page = pages[pageIndex];

                var (row, column) = SlotToCell(slot, rows, columns, nup.Order);
                var cellRect = new RectPt(
                    area.XPt + column * (cellWidth + nup.HorizontalGapPt),
                    area.YPt + row * (cellHeight + nup.VerticalGapPt),
                    cellWidth, cellHeight);

                var rotation = CellRotation(page, settings, nup, cell);
                var content = Rotate(page.SizePt, rotation);
                var scale = uniform ?? Math.Min(1.0, FitScale(content, cell));
                var scaled = new SizePt(content.WidthPt * scale, content.HeightPt * scale);
                var target = Place(scaled, cellRect, settings with { Position = PagePosition.Center });

                placed.Add(new PlacedPage
                {
                    DocumentId = page.DocumentId,
                    SourcePageIndex = page.PageIndex,
                    PageLabel = page.Label,
                    Box = settings.Box,
                    SourceRectPt = RectPt.FromSize(page.SizePt),
                    TargetRectPt = target,
                    ClipRectPt = target.Intersect(cellRect),
                    Scale = scale,
                    RotationDegrees = rotation,
                    Annotations = settings.Annotations,
                    Forms = settings.Forms,
                    Layers = settings.Layers,
                    Raster = settings.PrintAsImage ? RasterReason.UserRequested : RasterReason.None,
                });
            }

            sheets.Add(new SheetPlan
            {
                SheetIndex = sheets.Count,
                PaperSizePt = paperPt,
                PrintableAreaPt = RectPt.FromSize(paperPt).Deflate(caps.HardMarginsFor(paper)),
                HardMarginsPt = caps.HardMarginsFor(paper),
                Color = settings.Color,
                Pages = placed,
            });
        }
        return sheets;
    }

    private static SizePt ContentForCell(SourcePage page, LayoutSettings settings, NUpSettings nup, SizePt cell) =>
        Rotate(page.SizePt, CellRotation(page, settings, nup, cell));

    /// <summary>
    /// Поворот страницы в ячейке. Поворачиваем только если это ДЕЙСТВИТЕЛЬНО
    /// увеличивает масштаб: бессмысленный поворот портрета в портретной ячейке
    /// раздражает сильнее, чем чуть меньший масштаб.
    /// </summary>
    private static int CellRotation(SourcePage page, LayoutSettings settings, NUpSettings nup, SizePt cell)
    {
        var baseRotation = settings.ExtraRotationDegrees;
        if (!nup.AutoRotatePages) return baseRotation;

        var upright = FitScale(Rotate(page.SizePt, baseRotation), cell);
        var turned = FitScale(Rotate(page.SizePt, baseRotation + 90), cell);
        return turned > upright * 1.01 ? baseRotation + 90 : baseRotation;
    }

    /// <summary>Номер ячейки в сетке по выбранному порядку обхода.</summary>
    private static (int Row, int Column) SlotToCell(int slot, int rows, int columns, NUpOrder order) => order switch
    {
        NUpOrder.RowsRightToLeft => (slot / columns, columns - 1 - slot % columns),
        NUpOrder.ColumnsTopToBottom => (slot % rows, slot / rows),
        NUpOrder.ColumnsBottomToTop => (rows - 1 - slot % rows, slot / rows),
        _ => (slot / columns, slot % columns),
    };

    // ----- Плакат -----

    private IReadOnlyList<SheetPlan> BuildPoster(
        IReadOnlyList<SourcePage> pages, LayoutSettings settings,
        PaperSizeOption paper, PrinterCapabilities caps)
    {
        var poster = settings.Poster;
        var sheets = new List<SheetPlan>();

        foreach (var page in pages)
        {
            var content = new SizePt(
                page.SizePt.WidthPt * poster.Scale,
                page.SizePt.HeightPt * poster.Scale);
            var paperPt = ResolvePaperSize(paper.SizePt, content, settings.Orientation);
            var area = ContentArea(paperPt, caps, paper, settings);

            // Шаг плитки меньше её размера ровно на перекрытие — иначе при
            // склейке получилась бы дырка шириной в overlap.
            var stepX = Math.Max(1, area.WidthPt - poster.OverlapPt);
            var stepY = Math.Max(1, area.HeightPt - poster.OverlapPt);
            var columns = Math.Max(1, (int)Math.Ceiling(content.WidthPt / stepX));
            var rows = Math.Max(1, (int)Math.Ceiling(content.HeightPt / stepY));

            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    if (poster.ExcludedTiles.Contains((column, row))) continue;

                    // Какой кусок исходной страницы попадает на эту плитку.
                    var srcX = column * stepX / poster.Scale;
                    var srcY = row * stepY / poster.Scale;
                    var srcW = Math.Min(area.WidthPt / poster.Scale, page.SizePt.WidthPt - srcX);
                    var srcH = Math.Min(area.HeightPt / poster.Scale, page.SizePt.HeightPt - srcY);
                    if (srcW <= 0.5 || srcH <= 0.5) continue;

                    var target = new RectPt(area.XPt, area.YPt, srcW * poster.Scale, srcH * poster.Scale);

                    var marks = new List<SheetMark>();
                    if (poster.DrawCutLines)
                        marks.Add(new SheetMark("cut", target));
                    if (poster.DrawTileLabels)
                        marks.Add(new SheetMark("tile-label", target,
                            $"{(char)('A' + row)}{column + 1}"));

                    sheets.Add(new SheetPlan
                    {
                        SheetIndex = sheets.Count,
                        PaperSizePt = paperPt,
                        PrintableAreaPt = RectPt.FromSize(paperPt).Deflate(caps.HardMarginsFor(paper)),
                        HardMarginsPt = caps.HardMarginsFor(paper),
                        Color = settings.Color,
                        Marks = marks,
                        Pages = new[]
                        {
                            new PlacedPage
                            {
                                DocumentId = page.DocumentId,
                                SourcePageIndex = page.PageIndex,
                                PageLabel = page.Label,
                                Box = settings.Box,
                                SourceRectPt = new RectPt(srcX, srcY, srcW, srcH),
                                TargetRectPt = target,
                                ClipRectPt = target.Intersect(area),
                                Scale = poster.Scale,
                                RotationDegrees = settings.ExtraRotationDegrees,
                                Annotations = settings.Annotations,
                                Forms = settings.Forms,
                                Layers = settings.Layers,
                                Raster = settings.PrintAsImage ? RasterReason.UserRequested : RasterReason.None,
                            },
                        },
                    });
                }
            }
        }
        return sheets;
    }

    // ----- Буклет -----

    private IReadOnlyList<SheetPlan> BuildBooklet(
        IReadOnlyList<SourcePage> pages, LayoutSettings settings,
        PaperSizeOption paper, PrinterCapabilities caps)
    {
        var booklet = settings.Booklet;
        var sheets = new List<SheetPlan>();

        // Буклет всегда печатается на листе, где две страницы лежат рядом,
        // поэтому лист берётся в альбомной ориентации относительно страницы.
        var pageSize = pages[0].SizePt;
        var spread = new SizePt(pageSize.WidthPt * 2, pageSize.HeightPt);
        var paperPt = ResolvePaperSize(paper.SizePt, spread, OrientationMode.Automatic);
        var area = ContentArea(paperPt, caps, paper, settings);

        foreach (var signature in BookletImposition.SplitSignatures(pages.Count, booklet.SignatureSize))
        {
            var order = BookletImposition.SheetOrder(signature.Count);
            var sheetsInSignature = signature.Count / 4;

            for (var sheetInSig = 0; sheetInSig < sheetsInSignature; sheetInSig++)
            {
                foreach (var isFront in new[] { true, false })
                {
                    var slots = order[sheetInSig * 2 + (isFront ? 0 : 1)];
                    var placed = new List<PlacedPage>();

                    for (var side = 0; side < 2; side++)
                    {
                        var slotIndex = slots[side];
                        if (slotIndex < 0) continue; // намеренно пустая половина

                        var absolute = signature.FirstPage + slotIndex;
                        if (absolute >= pages.Count) continue; // добор пустыми
                        var page = pages[absolute];

                        var half = new RectPt(
                            area.XPt + side * area.WidthPt / 2,
                            area.YPt, area.WidthPt / 2, area.HeightPt);

                        // Выползание: чем ближе лист к середине сигнатуры, тем
                        // меньше сдвиг. Одинаковый сдвиг на все листы — типичная
                        // ошибка, из-за которой у сложенной брошюры «плывут» поля.
                        var creep = booklet.CompensateCreep
                            ? (sheetsInSignature - 1 - sheetInSig) * booklet.PaperThicknessPt
                            : 0;
                        var creepDirection = side == 0 ? creep : -creep;

                        var inner = half.Deflate(side == 0
                            ? new MarginsPt(0, 0, booklet.GutterPt, 0)
                            : new MarginsPt(booklet.GutterPt, 0, 0, 0));

                        var scale = Math.Min(1.0, FitScale(page.SizePt, inner.Size));
                        var scaled = new SizePt(page.SizePt.WidthPt * scale, page.SizePt.HeightPt * scale);
                        var target = Place(scaled, inner, settings with { Position = PagePosition.Center });
                        target = target with { XPt = target.XPt + creepDirection };

                        placed.Add(new PlacedPage
                        {
                            DocumentId = page.DocumentId,
                            SourcePageIndex = page.PageIndex,
                            PageLabel = page.Label,
                            Box = settings.Box,
                            SourceRectPt = RectPt.FromSize(page.SizePt),
                            TargetRectPt = target,
                            ClipRectPt = target.Intersect(half),
                            Scale = scale,
                            RotationDegrees = settings.ExtraRotationDegrees,
                            Annotations = settings.Annotations,
                            Forms = settings.Forms,
                            Layers = settings.Layers,
                            Raster = settings.PrintAsImage ? RasterReason.UserRequested : RasterReason.None,
                        });
                    }

                    sheets.Add(new SheetPlan
                    {
                        SheetIndex = sheets.Count,
                        PaperSizePt = paperPt,
                        PrintableAreaPt = RectPt.FromSize(paperPt).Deflate(caps.HardMarginsFor(paper)),
                        HardMarginsPt = caps.HardMarginsFor(paper),
                        IsFront = isFront,
                        PairedSheetIndex = isFront ? sheets.Count + 1 : sheets.Count - 1,
                        Color = settings.Color,
                        Pages = placed,
                    });
                }
            }
        }
        return sheets;
    }
}
