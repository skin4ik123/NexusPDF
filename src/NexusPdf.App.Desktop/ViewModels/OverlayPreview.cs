using System.Windows.Media;
using System.Windows.Media.Imaging;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.App.Desktop.ViewModels;

/// <summary>
/// Предпросмотр наложенного контента в режиме чтения. Координаты — в пунктах
/// страницы; масштабирование выполняет LayoutTransform слоя предпросмотра.
/// </summary>
public sealed class OverlayPreview
{
    private OverlayPreview() { }

    public bool IsText { get; private init; }
    public bool IsNote { get; private init; }
    public bool IsRectShape { get; private init; }
    public bool IsEllipseShape { get; private init; }
    public bool IsInk { get; private init; }

    /// <summary>Готовая геометрия штрихов рисунка в пунктах страницы.</summary>
    public Geometry? InkGeometry { get; private init; }
    public Brush Stroke { get; private init; } = Brushes.Transparent;
    public double StrokeThickness { get; private init; }
    public string Text { get; private init; } = "";
    public double FontSizePt { get; private init; }
    public Brush Fill { get; private init; } = Brushes.Black;
    public double XPt { get; private init; }
    public double YPt { get; private init; }
    public double WidthPt { get; private init; }
    public double HeightPt { get; private init; }
    /// <summary>Угол для WPF RotateTransform (по часовой; знак уже инвертирован).</summary>
    public double AngleDeg { get; private init; }
    /// <summary>Центр вращения по Y — базовая линия WPF-текста (совпадает с точкой вращения при запекании).</summary>
    public double BaselineCenterY { get; private init; }
    public BitmapSource? Image { get; private init; }

    // Запекание ставит базовую линию на 0.75·fs ниже анкера; WPF рисует её на
    // FontFamily.Baseline·fs ниже верха TextBlock. Разницу компенсируем сдвигом,
    // иначе предпросмотр систематически стоял бы на ~0.33·fs ниже результата.
    private const double BakedBaselineFactor = 0.75;
    private static readonly double WpfBaselineFactor = new FontFamily("Segoe UI").Baseline;

    /// <summary>Угол изображения (страница повёрнута после размещения) для RenderTransform вокруг центра.</summary>
    public double ImageAngleDeg { get; private init; }

    public static OverlayPreview? From(PageOverlay overlay, double imageExtraAngleDeg = 0)
    {
        switch (overlay)
        {
            case TextOverlay text:
            {
                var brush = new SolidColorBrush(Color.FromArgb(
                    (byte)(text.ColorArgb >> 24), (byte)(text.ColorArgb >> 16),
                    (byte)(text.ColorArgb >> 8), (byte)text.ColorArgb));
                brush.Freeze();
                return new OverlayPreview
                {
                    IsText = true,
                    Text = text.Text,
                    FontSizePt = text.FontSizePt,
                    Fill = brush,
                    XPt = text.XPt,
                    YPt = text.YPt + (BakedBaselineFactor - WpfBaselineFactor) * text.FontSizePt,
                    AngleDeg = -text.RotationDegrees,
                    BaselineCenterY = WpfBaselineFactor * text.FontSizePt,
                };
            }
            case ImageOverlay image:
            {
                var bitmap = BitmapSource.Create(
                    image.PixelWidth, image.PixelHeight, 96, 96,
                    PixelFormats.Bgra32, null, image.Bgra, image.PixelWidth * 4);
                bitmap.Freeze();
                return new OverlayPreview
                {
                    Image = bitmap,
                    XPt = image.XPt,
                    YPt = image.YPt,
                    WidthPt = image.WidthPt,
                    HeightPt = image.HeightPt,
                    // Знак: маппер отдаёт угол в ccw-конвенции PDF, WPF вращает по часовой.
                    ImageAngleDeg = -imageExtraAngleDeg,
                };
            }
            case NoteAnnotationDraft note:
                return new OverlayPreview
                {
                    IsNote = true,
                    Text = note.Contents,
                    XPt = note.XPt,
                    YPt = note.YPt,
                    WidthPt = 20,
                    HeightPt = 20,
                };
            case ShapeAnnotationDraft shape:
            {
                var stroke = MakeBrush(shape.StrokeArgb);
                var fill = MakeBrush(shape.FillArgb);
                return new OverlayPreview
                {
                    IsRectShape = !shape.IsEllipse,
                    IsEllipseShape = shape.IsEllipse,
                    Stroke = stroke,
                    Fill = fill,
                    StrokeThickness = shape.BorderWidthPt,
                    XPt = shape.XPt,
                    YPt = shape.YPt,
                    WidthPt = shape.WidthPt,
                    HeightPt = shape.HeightPt,
                };
            }
            case RedactionDraft redaction:
                // Предпросмотр вымарки: чёрная заливка с красной рамкой —
                // видно и «что скроется», и что это именно вымарка.
                return new OverlayPreview
                {
                    IsRectShape = true,
                    Stroke = MakeBrush(0xFFDC2626),
                    Fill = MakeBrush(0xE6000000),
                    StrokeThickness = 1.5,
                    XPt = redaction.XPt,
                    YPt = redaction.YPt,
                    WidthPt = redaction.WidthPt,
                    HeightPt = redaction.HeightPt,
                };
            case InkAnnotationDraft ink:
            {
                // Без этой ветки нарисованное было видно только ПОСЛЕ сохранения:
                // штрих ложился в модель, но на экране не появлялся, и выглядело
                // это так, будто инструмент не работает.
                var geometry = new PathGeometry();
                foreach (var stroke in ink.Strokes)
                {
                    if (stroke.Count < 2) continue;
                    var figure = new PathFigure
                    {
                        StartPoint = new System.Windows.Point(stroke[0].XPt, stroke[0].YPt),
                        IsClosed = false,
                        IsFilled = false,
                    };
                    for (var i = 1; i < stroke.Count; i++)
                    {
                        figure.Segments.Add(new LineSegment(
                            new System.Windows.Point(stroke[i].XPt, stroke[i].YPt), true));
                    }
                    figure.Freeze();
                    geometry.Figures.Add(figure);
                }
                if (geometry.Figures.Count == 0)
                    return null;
                geometry.Freeze();
                return new OverlayPreview
                {
                    IsInk = true,
                    InkGeometry = geometry,
                    Stroke = MakeBrush(ink.StrokeArgb),
                    StrokeThickness = ink.WidthPt,
                };
            }
            default:
                return null;
        }
    }

    private static Brush MakeBrush(uint argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }
}
