namespace NexusPdf.Pdf.Abstractions.TextBlocks;

/// <summary>
/// Новый блок для места в бланке. В клетке — рамка по клетке с отступом
/// 1 пт, текст по центру по высоте, кегль не больше, чем клетка вмещает, и
/// «уменьшать, чтобы влезло». На строке и в пропуске «____» — текст стоит на
/// линии до её конца. Отметка ✓ ✗ ● — по центру места, в 70 % его высоты.
/// </summary>
public static class FormFill
{
    private const double CellInsetPt = 1;
    private const double LineHeightFactor = 1.2;
    private const double MarkHeightShare = 0.7;

    public static TextBlockOverlay BlockFor(FormTarget target, TextStyle style, TextAlign align, double spacing)
    {
        var area = target.AreaPt;
        if (target.Kind is FormTargetKind.Cell or FormTargetKind.CaptionCell)
        {
            var frame = Inset(area);
            var size = Math.Clamp((frame.HeightPt - 2 * TextBlockLayout.PaddingPt) / LineHeightFactor,
                TextBlockLayout.MinimumFontSizePt, Math.Max(style.FontSizePt, TextBlockLayout.MinimumFontSizePt));
            return Block(frame, style with { FontSizePt = size }, "", align, spacing, TextVerticalAlign.Center);
        }

        // Строка: низ рамки — у линии, высота — не меньше одной строки текста.
        var height = Math.Max(area.HeightPt, style.FontSizePt * LineHeightFactor + 2 * TextBlockLayout.PaddingPt);
        var bottom = area.YPt + area.HeightPt;
        return Block(new OverlayBox(area.XPt, bottom - height, area.WidthPt, height), style, "", align, spacing,
            TextVerticalAlign.Bottom);
    }

    public static TextBlockOverlay MarkFor(FormTarget? target, double xPt, double yPt, char mark, TextStyle style)
    {
        if (target != null)
        {
            var area = target.Kind is FormTargetKind.Cell or FormTargetKind.CaptionCell ? Inset(target.AreaPt) : target.AreaPt;
            var size = Math.Max(TextBlockLayout.MinimumFontSizePt, target.AreaPt.HeightPt * MarkHeightShare);
            return Block(area, style with { FontSizePt = size }, mark.ToString(), TextAlign.Center, 1.0,
                TextVerticalAlign.Center);
        }

        // Без места — отметка у точки щелчка, растущая вправо, как надпись.
        var lineHeight = style.FontSizePt * LineHeightFactor;
        return new TextBlockOverlay(
            xPt - TextBlockLayout.PaddingPt, yPt - TextBlockLayout.PaddingPt - lineHeight / 2,
            2 * TextBlockLayout.PaddingPt + 1, 2 * TextBlockLayout.PaddingPt + lineHeight, 0,
            TextBlockWidthMode.AutoWidth, AutoHeight: true, ShrinkToFit: false,
            [new TextParagraph([new TextRun(mark.ToString(), style)], TextAlign.Left, 1.0)], Guid.NewGuid());
    }

    /// <summary>
    /// Оформление для отметки: свой шрифт, если в нём есть этот знак, иначе
    /// запасной, где он есть (чтобы знак не считался «написанным чужим
    /// шрифтом»); null — знака нет ни в одном шрифте, который можно вписать в PDF.
    /// </summary>
    public static TextStyle? MarkStyle(IFontProvider fonts, TextStyle style, char mark)
    {
        if (fonts.Resolve(style.FontFamily, style.Bold, style.Italic) is { Embeddable: true, Supported: true } own &&
            fonts.Load(own.Ref).HasGlyph(mark))
            return style;
        return fonts.FallbackFor(mark, style.Bold, style.Italic) is { } face ? style with { FontFamily = face.Family } : null;
    }

    private static OverlayBox Inset(OverlayBox area) =>
        new(area.XPt + CellInsetPt, area.YPt + CellInsetPt,
            Math.Max(1, area.WidthPt - 2 * CellInsetPt), Math.Max(1, area.HeightPt - 2 * CellInsetPt));

    private static TextBlockOverlay Block(OverlayBox frame, TextStyle style, string text, TextAlign align,
        double spacing, TextVerticalAlign vertical) =>
        new(frame.XPt, frame.YPt, frame.WidthPt, frame.HeightPt, 0, TextBlockWidthMode.Fixed,
            AutoHeight: false, ShrinkToFit: true,
            [new TextParagraph([new TextRun(text, style)], align, spacing)], Guid.NewGuid(), vertical);
}
