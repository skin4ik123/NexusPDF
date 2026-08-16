using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace NexusPdf.App.Desktop.Views;

/// <summary>
/// Полоса на месте будущей вставки: показывает, КУДА лягут страницы, ещё до
/// того как пользователь отпустит файлы. Без неё перетаскивание из Проводника
/// превращается в лотерею «куда попадёт».
/// </summary>
public sealed class InsertionLineAdorner : Adorner
{
    private static readonly Pen Line = CreatePen();
    private Rect _target;

    public InsertionLineAdorner(UIElement owner) : base(owner) => IsHitTestVisible = false;

    private static Pen CreatePen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), 3);
        pen.Freeze();
        return pen;
    }

    /// <summary>Прямоугольник, ПЕРЕД которым встанут страницы (в координатах списка).</summary>
    public Rect Target
    {
        get => _target;
        set
        {
            if (_target == value) return;
            _target = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_target.Height < 1) return;
        var x = Math.Round(_target.X) + 0.5;
        dc.DrawLine(Line, new Point(x, _target.Y + 2), new Point(x, _target.Y + _target.Height - 2));
        // Засечки сверху и снизу — линию видно и на светлой миниатюре.
        dc.DrawLine(Line, new Point(x - 5, _target.Y + 2), new Point(x + 5, _target.Y + 2));
        dc.DrawLine(Line, new Point(x - 5, _target.Y + _target.Height - 2),
            new Point(x + 5, _target.Y + _target.Height - 2));
    }
}
