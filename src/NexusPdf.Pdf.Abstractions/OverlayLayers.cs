namespace NexusPdf.Pdf.Abstractions;

/// <summary>
/// Какие правки рисуются ПОВЕРХ страницы, а какие меняют её содержимое.
///
/// От этого зависит цена показа. Пометка поверх (заметка, фигура, рисунок,
/// измерение, надпись, вымарка до сохранения) рисуется отдельным прозрачным
/// слоем над уже готовым растром страницы. Правка содержимого (замена текста
/// или картинки внутри страницы, замена всей страницы растром) требует
/// перерисовать страницу целиком — иначе на экране было бы не то, что
/// окажется в файле.
/// </summary>
public static class OverlayLayers
{
    public static bool IsDrawnOnTop(PageOverlay overlay) =>
        overlay is not (TextObjectReplacement or ImageObjectReplacement or PageRasterReplacement
            // Замена блока убирает его прежние объекты со страницы.
            or TextBlocks.TextBlockReplacement
            // Маркер — полупрозрачная заливка с режимом наложения «умножение»:
            // на прозрачном слое он стал бы сплошной плашкой поверх текста.
            or TextMarkupDraft { Kind: TextMarkupKind.Highlight });

    /// <summary>Все правки страницы можно показать слоем поверх готового растра.</summary>
    public static bool CanDrawAsLayer(IReadOnlyList<PageOverlay> overlays) =>
        overlays.Count > 0 && overlays.All(IsDrawnOnTop);

    /// <summary>
    /// Меняет ли правка ТЕКСТ страницы — то, что видят поиск, выделение и
    /// копирование. Для остальных правок испечённая копия страницы не нужна:
    /// раньше она собиралась после каждой пометки, и на чертеже с сотнями
    /// тысяч линий это стоило секунд.
    /// </summary>
    public static bool ChangesText(PageOverlay overlay) =>
        overlay is TextOverlay or OcrEditableTextOverlay or OcrTextLayerOverlay
            or TextObjectReplacement or PageRasterReplacement
            or TextBlocks.TextBlockOverlay or TextBlocks.TextBlockReplacement;

    /// <summary>
    /// Слой для показа: вымарка и стирание до сохранения рисуются тем, чем
    /// станут в файле. Раньше их на экране не было вовсе, и человек не видел,
    /// что именно будет уничтожено.
    /// </summary>
    public static PageOverlay ForDisplay(PageOverlay overlay) => overlay switch
    {
        RedactionDraft r => new ShapeAnnotationDraft(
            r.XPt, r.YPt, r.WidthPt, r.HeightPt,
            StrokeArgb: 0xFFDC2626, FillArgb: 0xFF000000, BorderWidthPt: 1.0,
            IsEllipse: false, Contents: "", Author: "") { PlacedRotation = r.PlacedRotation },
        RegionEraseDraft e => new ShapeAnnotationDraft(
            e.XPt, e.YPt, e.WidthPt, e.HeightPt,
            StrokeArgb: e.FillArgb | 0xFF000000, FillArgb: e.FillArgb | 0xFF000000, BorderWidthPt: 0.5,
            IsEllipse: false, Contents: "", Author: "") { PlacedRotation = e.PlacedRotation },
        _ => overlay,
    };
}
