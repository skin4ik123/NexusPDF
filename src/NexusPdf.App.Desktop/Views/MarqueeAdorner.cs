using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace NexusPdf.App.Desktop.Views;

/// <summary>
/// Рамка выделения, которую пользователь тянет мышью по пустому месту.
///
/// Рисуется поверх списка в слое украшений: сам список при этом не
/// перестраивается, поэтому рамка не мешает ни прокрутке, ни миниатюрам.
/// </summary>
public sealed class MarqueeAdorner : Adorner
{
    private static readonly Brush Fill = CreateFill();
    private static readonly Pen Outline = CreateOutline();

    private Rect _rect;

    public MarqueeAdorner(UIElement owner) : base(owner) => IsHitTestVisible = false;

    private static Brush CreateFill()
    {
        var brush = new SolidColorBrush(Color.FromArgb(48, 0x3B, 0x82, 0xF6));
        brush.Freeze();
        return brush;
    }

    private static Pen CreateOutline()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(200, 0x3B, 0x82, 0xF6)), 1);
        pen.Freeze();
        return pen;
    }

    /// <summary>Прямоугольник в координатах украшаемого элемента.</summary>
    public Rect Rect
    {
        get => _rect;
        set
        {
            if (_rect == value) return;
            _rect = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_rect.Width < 1 || _rect.Height < 1) return;
        // Половина пикселя — чтобы линия не размывалась между точками экрана.
        var crisp = new Rect(
            Math.Round(_rect.X) + 0.5, Math.Round(_rect.Y) + 0.5,
            Math.Max(1, Math.Round(_rect.Width) - 1), Math.Max(1, Math.Round(_rect.Height) - 1));
        dc.DrawRectangle(Fill, Outline, crisp);
    }
}
