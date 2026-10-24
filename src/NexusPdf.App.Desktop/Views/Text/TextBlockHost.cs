using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NexusPdf.App.Desktop.ViewModels;

namespace NexusPdf.App.Desktop.Views.Text;

/// <summary>
/// Ставит редактор блока на страницу: отдельным Popup (у слоя украшений нет
/// своей области фокуса, клавиатура до него не доходит), повёрнутым вместе с
/// блоком так, что видимый левый верхний угол рамки — точно на своём месте
/// страницы при любом масштабе. Пока идёт набор, рамку можно переносить,
/// растягивать и поворачивать — место редактора пересчитывается на ходу.
/// </summary>
internal sealed class TextBlockHost
{
    private readonly Popup _popup;
    private readonly Popup _toolbarPopup;
    private readonly TextBlockToolbar _toolbar;
    private readonly RotateTransform _rotation = new();
    private readonly PageViewModel _page;
    private readonly DocumentViewModel.TextBlockEdit _edit;

    public TextBlockHost(
        FrameworkElement pageElement, PageViewModel page, DocumentViewModel.TextBlockEdit edit,
        DocumentViewModel document, FontPickerContext? fonts)
    {
        _page = page;
        _edit = edit;
        Canvas = new TextBlockCanvas(edit.Model, page.DisplayScale);
        if (edit.CoversOriginal)
            Canvas.CoverPt = edit.CoverPt is { } cover
                ? new Size(cover.Width, cover.Height)
                : new Size(edit.StartBlock.WidthPt, edit.StartBlock.HeightPt);
        _popup = new Popup
        {
            Child = new Border { Child = Canvas, LayoutTransform = _rotation },
            PlacementTarget = pageElement,
            Placement = PlacementMode.Relative,
            AllowsTransparency = true,
            // Закрывает редактор вызывающий код: щелчок по выпадающему списку
            // шрифтов не должен считаться «щелчком мимо».
            StaysOpen = true,
            Focusable = false,
            PopupAnimation = PopupAnimation.None,
        };
        _toolbar = new TextBlockToolbar { DataContext = document };
        _toolbar.SetFontContext(fonts);
        _toolbarPopup = new Popup
        {
            Child = _toolbar,
            PlacementTarget = pageElement,
            Placement = PlacementMode.Relative,
            AllowsTransparency = true,
            StaysOpen = true,
            Focusable = false,
            PopupAnimation = PopupAnimation.None,
        };
        Canvas.PagePointerPt = () =>
        {
            var p = Mouse.GetPosition(pageElement);
            var zoom = _page.DisplayScale > 0 ? _page.DisplayScale : 1;
            return new Point(p.X / zoom, p.Y / zoom);
        };
        edit.Model.Changed += OnModelChanged;
    }

    public TextBlockCanvas Canvas { get; }

    public void Open()
    {
        Sync();
        _popup.IsOpen = true;
        _toolbarPopup.IsOpen = true;
        FocusEditor();
    }

    /// <summary>Вернуть клавиатуру редактору (после щелчка по панели оформления).</summary>
    public void FocusEditor() =>
        // Следующим проходом разметки: до него редактор может быть ещё не размещён.
        Canvas.Dispatcher.BeginInvoke(new Action(() =>
        {
            Canvas.Focus();
            Keyboard.Focus(Canvas);
        }), DispatcherPriority.Input);

    public void Close()
    {
        _edit.Model.Changed -= OnModelChanged;
        _popup.IsOpen = false;
        _toolbarPopup.IsOpen = false;
        _toolbar.DataContext = null;
    }

    private void OnModelChanged(object? sender, EventArgs e) => Sync();

    /// <summary>Масштаб и место по текущему виду страницы (после прокрутки, масштабирования, набора).</summary>
    public void Sync()
    {
        var zoom = _page.DisplayScale;
        if (!(zoom > 0)) return;
        Canvas.Zoom = zoom;
        var block = _edit.Model.Block;
        _rotation.Angle = block.RotationDegrees;

        Canvas.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Canvas.DesiredSize;
        var angle = block.RotationDegrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
        var boxW = Math.Abs(size.Width * cos) + Math.Abs(size.Height * sin);
        var boxH = Math.Abs(size.Width * sin) + Math.Abs(size.Height * cos);
        // Где на странице левый верхний угол рамки: угол после поворота вокруг её центра.
        var (cx, cy) = (block.XPt + block.WidthPt / 2, block.YPt + block.HeightPt / 2);
        var cornerX = (cx - block.WidthPt / 2 * cos + block.HeightPt / 2 * sin) * zoom;
        var cornerY = (cy - block.WidthPt / 2 * sin - block.HeightPt / 2 * cos) * zoom;
        // Центр редактора от угла рамки — в осях блока (поля для ручек вокруг
        // рамки неравные), затем повёрнутый. Окно — повёрнутый редактор вокруг
        // этого центра.
        var inset = TextBlockCanvas.Inset;
        var (ux, uy) = (size.Width / 2 - inset.Left, size.Height / 2 - inset.Top);
        var centreX = cornerX + ux * cos - uy * sin;
        var centreY = cornerY + ux * sin + uy * cos;

        _popup.HorizontalOffset = centreX - boxW / 2;
        _popup.VerticalOffset = centreY - boxH / 2;
        // Popup не следит за прокруткой цели — подталкиваем пересчёт места.
        _popup.HorizontalOffset += 0.01;
        _popup.HorizontalOffset -= 0.01;

        // Панель — над редактором вместе с его ручкой поворота.
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _toolbarPopup.HorizontalOffset = centreX - boxW / 2 + inset.Left;
        _toolbarPopup.VerticalOffset = centreY - boxH / 2 - _toolbar.DesiredSize.Height - 4;
        _toolbarPopup.HorizontalOffset += 0.01;
        _toolbarPopup.HorizontalOffset -= 0.01;
    }
}
