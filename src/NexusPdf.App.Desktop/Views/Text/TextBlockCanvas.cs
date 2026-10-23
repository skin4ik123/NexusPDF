using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Abstractions.TextBlocks;

namespace NexusPdf.App.Desktop.Views.Text;

/// <summary>
/// Редактор текстового блока прямо на странице. Рисует ТУ ЖЕ раскладку,
/// по которой блок пишется в PDF: глифы из тех же файлов шрифтов в тех же
/// позициях, в масштабе страницы. Всё поведение (курсор, выделение, стили,
/// отмена, буфер) — в <see cref="TextBlockEditorModel"/>; здесь только экран,
/// клавиши, мышь и IME.
///
/// Координаты элемента — рамка блока без поворота (пункты × масштаб),
/// сдвинутая на <see cref="Inset"/>: вокруг рамки место для ручек размера и
/// поворота. Поворот блока задаёт тот, кто ставит редактор на страницу.
/// </summary>
internal sealed class TextBlockCanvas : FrameworkElement
{
    public const string RichClipboardFormat = "NexusPdf.TextBlock";
    public const string EditorAutomationId = "TextBlockEditor";

    private static readonly Pen FramePen = MakeFramePen();
    private static readonly Pen FallbackPen = MakeFallbackPen();

    /// <summary>Полоса края рамки, за которую блок переносится, — в точках экрана.</summary>
    public const double BorderDip = 6;

    /// <summary>
    /// Поля вокруг рамки: в них ручки и ручка поворота над верхним краем.
    /// Мышь ловится только там, где что-то нарисовано, поэтому пустые поля
    /// щелчков у страницы не отнимают.
    /// </summary>
    public static readonly Thickness Inset = new(
        NexusPdf.Ux.ObjectHandles.HandleToleranceDip + 1,
        NexusPdf.Ux.ObjectHandles.RotateHandleOffsetDip + NexusPdf.Ux.ObjectHandles.HandleToleranceDip + 1,
        NexusPdf.Ux.ObjectHandles.HandleToleranceDip + 1,
        NexusPdf.Ux.ObjectHandles.HandleToleranceDip + 1);

    // Почти прозрачная заливка: у окна с прозрачностью щелчок по совсем
    // прозрачной точке уходит в окно под ним, а между строк и на краю рамки
    // мышь должна доставаться редактору.
    private static readonly Brush HitBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x01, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen HitPen = FrozenPen(new Pen(HitBrush, BorderDip));
    private static readonly Brush HandleBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)));
    private static readonly Pen HandlePen = FrozenPen(new Pen(Brushes.White, 1));
    private static readonly Pen RotateStemPen = FrozenPen(new Pen(HandleBrush, 1));

    private readonly TextBlockEditorModel _model;
    private readonly DispatcherTimer? _blink;
    private double _zoom;
    private bool _caretVisible = true;
    private bool _composing;
    private char? _pendingHighSurrogate;
    private FrameDrag? _drag;

    /// <summary>Перетаскивание рамки: что тянут, блок и курсор (пункты страницы) в начале.</summary>
    private sealed record FrameDrag(FrameZone Zone, FrameHandle Handle, TextBlockOverlay Start, Point StartPointer);

    public TextBlockCanvas(TextBlockEditorModel model, double zoom)
    {
        _model = model;
        _zoom = zoom > 0 ? zoom : 1;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        SnapsToDevicePixels = false;
        AutomationProperties.SetAutomationId(this, EditorAutomationId);
        InputMethod.SetIsInputMethodEnabled(this, true);

        var blinkMs = CaretBlinkMilliseconds();
        if (blinkMs > 0)
        {
            _blink = new DispatcherTimer(TimeSpan.FromMilliseconds(blinkMs), DispatcherPriority.Background,
                (_, _) => { _caretVisible = !_caretVisible; InvalidateVisual(); }, Dispatcher);
            _blink.Stop();
        }

        _model.Changed += OnModelChanged;
        TextCompositionManager.AddPreviewTextInputStartHandler(this, OnCompositionStart);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(this, OnCompositionUpdate);
        Unloaded += (_, _) => _blink?.Stop();
    }

    public TextBlockEditorModel Model => _model;

    /// <summary>
    /// Курсор в отображаемых пунктах страницы. Задаёт тот, кто ставит
    /// редактор: во время перетаскивания сам редактор едет вместе с рамкой,
    /// и считать сдвиг от него нельзя. Без него рамка не перетаскивается.
    /// </summary>
    public Func<Point>? PagePointerPt { get; set; }

    /// <summary>DIP на пункт страницы.</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            if (!(value > 0) || Math.Abs(value - _zoom) < 1e-9) return;
            _zoom = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Размер прежнего вида блока (пункты, от левого верхнего угла): он
    /// нарисован на странице под редактором и закрывается белым, пока идёт
    /// ввод, — иначе старый и новый текст лежали бы друг на друге.
    /// </summary>
    public Size CoverPt
    {
        get => _cover;
        set
        {
            _cover = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private Size _cover;

    /// <summary>Ввод закончен: Esc или фокус ушёл на страницу.</summary>
    public event EventHandler? CommitRequested;

    /// <summary>Ввод отвергнут; аргумент — ключ словаря для строки состояния.</summary>
    public event EventHandler<string>? InputRejected;

    // ----- Отрисовка -----

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Max(1, Math.Max(_model.Layout.WidthPt, _cover.Width) * _zoom) + Inset.Left + Inset.Right,
            Math.Max(1, Math.Max(_model.Layout.ContentHeightPt, _cover.Height) * _zoom) + Inset.Top + Inset.Bottom);

    protected override void OnRender(DrawingContext dc)
    {
        dc.PushTransform(new TranslateTransform(Inset.Left, Inset.Top));
        RenderBlock(dc);
        dc.Pop();
    }

    private void RenderBlock(DrawingContext dc)
    {
        var layout = _model.Layout;
        if (_cover.Width > 0 && _cover.Height > 0)
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, _cover.Width * _zoom, _cover.Height * _zoom));
        var frame = new Rect(0, 0, layout.WidthPt * _zoom, layout.HeightPt * _zoom);
        dc.DrawRectangle(HitBrush, HitPen, frame);
        dc.DrawRectangle(null, FramePen, frame);
        DrawHandles(dc, frame);

        var selection = SelectionBrush();
        foreach (var box in _model.SelectionRects())
            dc.DrawRectangle(selection, null, new Rect(box.XPt * _zoom, box.YPt * _zoom, box.WidthPt * _zoom, box.HeightPt * _zoom));

        var pixelsPerDip = (float)VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var line in layout.Lines)
        {
            foreach (var piece in line.Pieces)
            {
                if (GlyphCache.Run(piece, _zoom, pixelsPerDip) is { } run)
                    dc.DrawGlyphRun(GlyphCache.Brush(piece.Style.ColorArgb), run);
                // Буквы, которых нет в выбранном шрифте, написаны запасным —
                // это видно сразу, а не после сохранения.
                if (piece.IsFallback && piece.Text.Trim().Length > 0)
                {
                    var y = (piece.BaselinePt + piece.FontSizePt * 0.15) * _zoom;
                    dc.DrawLine(FallbackPen, new Point(piece.XPt * _zoom, y), new Point((piece.XPt + piece.WidthPt) * _zoom, y));
                }
            }
        }
        foreach (var line in layout.Decorations)
        {
            dc.DrawRectangle(GlyphCache.Brush(line.ColorArgb), null,
                new Rect(line.XPt * _zoom, line.YPt * _zoom, line.WidthPt * _zoom, line.HeightPt * _zoom));
        }

        if (layout.Overflows)
            DrawOverflowMark(dc, frame);

        var (caretX, caretTop, caretHeight) = _model.CaretRect();
        if (_model.Composition.Length > 0)
            DrawComposition(dc, caretX, caretTop, caretHeight, pixelsPerDip);
        else if (IsKeyboardFocused && _caretVisible)
            dc.DrawRectangle(GlyphCache.Brush(_model.StyleAtCaret.ColorArgb | 0xFF000000), null,
                new Rect(caretX * _zoom, caretTop * _zoom, Math.Max(1, 1 / pixelsPerDip), caretHeight * _zoom));
    }

    /// <summary>Ручки размера и ручка поворота — те же, что у выделенного объекта.</summary>
    private static void DrawHandles(DrawingContext dc, Rect frame)
    {
        var box = new OverlayBox(frame.X, frame.Y, frame.Width, frame.Height);
        var side = NexusPdf.Ux.ObjectHandles.HandleSizeDip;
        var top = BlockFrameDrag.CenterOf(box, FrameHandle.Top);
        var rotate = new Point(top.X, top.Y - NexusPdf.Ux.ObjectHandles.RotateHandleOffsetDip);
        dc.DrawLine(RotateStemPen, new Point(top.X, top.Y), rotate);
        dc.DrawEllipse(HandleBrush, HandlePen, rotate, side / 2, side / 2);
        foreach (var handle in Enum.GetValues<FrameHandle>())
        {
            if (handle == FrameHandle.None) continue;
            var (x, y) = BlockFrameDrag.CenterOf(box, handle);
            dc.DrawRectangle(HandleBrush, HandlePen, new Rect(x - side / 2, y - side / 2, side, side));
        }
    }

    /// <summary>
    /// Знак переполнения у правого нижнего угла рамки: текст не помещается в
    /// заданную высоту (строки ниже рамки видны, но в файле выйдут за неё).
    /// </summary>
    private static void DrawOverflowMark(DrawingContext dc, Rect frame)
    {
        const double side = 11;
        var box = new Rect(frame.Right - side, frame.Bottom - side / 2, side, side);
        dc.DrawRectangle(OverflowBrush, null, box);
        dc.DrawLine(OverflowPen, new Point(box.Left + 2.5, box.Top + side / 2), new Point(box.Right - 2.5, box.Top + side / 2));
        dc.DrawLine(OverflowPen, new Point(box.Left + side / 2, box.Top + 2.5), new Point(box.Left + side / 2, box.Bottom - 2.5));
    }

    private static readonly Brush OverflowBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)));
    private static readonly Pen OverflowPen = FrozenPen(new Pen(Brushes.White, 1.5));

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    /// <summary>Набираемое через IME — ещё не текст блока, поэтому рисуется поверх с подчёркиванием.</summary>
    private void DrawComposition(DrawingContext dc, double x, double top, double height, float pixelsPerDip)
    {
        var style = _model.StyleAtCaret;
        var text = new FormattedText(_model.Composition, System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface(style.FontFamily), style.FontSizePt * _zoom,
            GlyphCache.Brush(style.ColorArgb), pixelsPerDip);
        text.SetTextDecorations(TextDecorations.Underline);
        dc.DrawRectangle(Brushes.White, null, new Rect(x * _zoom, top * _zoom, text.WidthIncludingTrailingWhitespace, height * _zoom));
        dc.DrawText(text, new Point(x * _zoom, (top + height) * _zoom - text.Height));
    }

    private static Brush SelectionBrush()
    {
        var accent = System.Windows.Application.Current?.TryFindResource("Accent") as SolidColorBrush;
        var color = accent?.Color ?? Colors.DodgerBlue;
        var brush = new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static Pen MakeFramePen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xAA, 0x25, 0x63, 0xEB)), 1) { DashStyle = DashStyles.Dash };
        pen.Freeze();
        return pen;
    }

    private static Pen MakeFallbackPen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)), 1) { DashStyle = DashStyles.Dot };
        pen.Freeze();
        return pen;
    }

    private void OnModelChanged(object? sender, EventArgs e)
    {
        InvalidateMeasure();
        InvalidateVisual();
        RestartBlink();
        PlaceImeWindow();
        if (UIElementAutomationPeer.FromElement(this) is TextBlockCanvasAutomationPeer peer)
            peer.RaiseChanged();
    }

    // ----- Курсор -----

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        RestartBlink();
        PlaceImeWindow();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _blink?.Stop();
        InvalidateVisual();
        // Фокус ушёл в другое место ЭТОГО окна (страница, панель) — ввод
        // закончен. Всплывающие панели оформления — свои окна: туда фокус
        // уходит на время выбора шрифта, и ввод при этом продолжается.
        if (e.NewFocus is DependencyObject target && !IsAncestorOf(target) &&
            PresentationSource.FromDependencyObject(target) is { } source &&
            ReferenceEquals(source, PresentationSource.FromVisual(this)))
            CommitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RestartBlink()
    {
        _caretVisible = true;
        if (_blink != null && IsKeyboardFocused)
        {
            _blink.Stop();
            _blink.Start();
        }
        InvalidateVisual();
    }

    private static int CaretBlinkMilliseconds()
    {
        if (!OperatingSystem.IsWindows()) return 530;
        var time = GetCaretBlinkTime();
        // INFINITE — мигание выключено в системе (настройка доступности).
        return time == uint.MaxValue ? 0 : (int)Math.Clamp(time, 100u, 5000u);
    }

    // ----- Клавиатура -----

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        var text = e.Text;
        if (_composing)
        {
            _composing = false;
            if (text.Length > 0 && !char.IsControl(text[0]))
                Report(CommitComposition(text));
            else
                _model.SetComposition("");
            e.Handled = true;
            return;
        }
        // Управляющие символы (Backspace, Enter, Esc) приходят сюда тоже —
        // их обрабатывают клавиши.
        if (text.Length == 0 || text.All(char.IsControl))
            return;
        // Буква вне основной плоскости (эмодзи с панели Windows) может прийти
        // двумя событиями — половинками пары; первая ждёт вторую.
        if (_pendingHighSurrogate is { } high)
        {
            _pendingHighSurrogate = null;
            text = high + text;
        }
        if (text.Length > 0 && char.IsHighSurrogate(text[^1]))
        {
            _pendingHighSurrogate = text[^1];
            text = text[..^1];
            e.Handled = true;
            if (text.Length == 0) return;
        }
        Report(_model.Insert(text));
        e.Handled = true;
    }

    private InputResult CommitComposition(string text)
    {
        _model.SetComposition("");
        return _model.Insert(text);
    }

    private void OnCompositionStart(object sender, TextCompositionEventArgs e)
    {
        _composing = true;
        _model.SetComposition(e.TextComposition.CompositionText);
        PlaceImeWindow();
    }

    private void OnCompositionUpdate(object sender, TextCompositionEventArgs e)
    {
        _composing = true;
        _model.SetComposition(e.TextComposition.CompositionText);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var ctrl = NexusPdf.Ux.TextBlockStyleCommands.IsShortcut(
            (Keyboard.Modifiers & ModifierKeys.Control) != 0, (Keyboard.Modifiers & ModifierKeys.Alt) != 0);
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        e.Handled = true;
        switch (key)
        {
            case Key.Left: _model.MoveCaret(ctrl ? CaretMove.WordLeft : CaretMove.Left, shift); break;
            case Key.Right: _model.MoveCaret(ctrl ? CaretMove.WordRight : CaretMove.Right, shift); break;
            case Key.Up: _model.MoveCaret(CaretMove.Up, shift); break;
            case Key.Down: _model.MoveCaret(CaretMove.Down, shift); break;
            case Key.Home: _model.MoveCaret(ctrl ? CaretMove.BlockStart : CaretMove.LineStart, shift); break;
            case Key.End: _model.MoveCaret(ctrl ? CaretMove.BlockEnd : CaretMove.LineEnd, shift); break;
            case Key.PageUp: _model.MoveCaret(CaretMove.BlockStart, shift); break;
            case Key.PageDown: _model.MoveCaret(CaretMove.BlockEnd, shift); break;
            case Key.Back: _model.Backspace(ctrl); break;
            case Key.Delete when shift && !ctrl: Cut(); break;
            case Key.Delete: _model.Delete(ctrl); break;
            case Key.Enter when shift: _model.LineBreak(); break;
            case Key.Enter: _model.NewParagraph(); break;
            case Key.Tab when !ctrl: Report(_model.Insert("\t")); break;
            case Key.Insert when shift: Paste(); break;
            case Key.Insert when ctrl: Copy(); break;
            case Key.Escape:
                if (!CancelFrameDrag()) CommitRequested?.Invoke(this, EventArgs.Empty);
                break;
            case Key.A when ctrl: _model.SelectAll(); break;
            case Key.C when ctrl: Copy(); break;
            case Key.X when ctrl: Cut(); break;
            case Key.V when ctrl: Paste(); break;
            case Key.Z when ctrl && shift: _model.Redo(); break;
            case Key.Z when ctrl: _model.Undo(); break;
            case Key.Y when ctrl: _model.Redo(); break;
            case Key.B when ctrl: _model.ApplyStyle(s => s with { Bold = !_model.StyleAtCaret.Bold }); break;
            case Key.I when ctrl: _model.ApplyStyle(s => s with { Italic = !_model.StyleAtCaret.Italic }); break;
            case Key.U when ctrl: _model.ApplyStyle(s => s with { Underline = !_model.StyleAtCaret.Underline }); break;
            default: e.Handled = false; break;
        }
    }

    private void Report(InputResult result)
    {
        if (!result.Accepted && result.RejectedReasonKey is { } reason)
            InputRejected?.Invoke(this, reason);
    }

    // ----- Буфер обмена -----

    private void Copy()
    {
        if (!_model.HasSelection) return;
        var data = new DataObject();
        data.SetData(RichClipboardFormat, _model.CopyRich());
        data.SetText(_model.CopyPlainText(), TextDataFormat.UnicodeText);
        try
        {
            Clipboard.SetDataObject(data, copy: true);
        }
        catch (ExternalException ex)
        {
            // Буфер занят другой программой — копирование просто не случилось.
            Serilog.Log.Debug(ex, "Буфер обмена занят");
        }
    }

    private void Cut()
    {
        if (!_model.HasSelection) return;
        Copy();
        _model.Delete(word: false);
    }

    private void Paste()
    {
        string? rich = null;
        var plain = "";
        try
        {
            if (Clipboard.ContainsData(RichClipboardFormat))
                rich = Clipboard.GetData(RichClipboardFormat) as string;
            if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
                plain = Clipboard.GetText(TextDataFormat.UnicodeText);
        }
        catch (ExternalException ex)
        {
            Serilog.Log.Debug(ex, "Буфер обмена не прочитался");
            return;
        }
        if (rich == null && plain.Length == 0) return;
        Report(_model.Paste(rich, plain));
    }

    // ----- Мышь -----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        Keyboard.Focus(this);
        var (x, y) = PointOf(e);
        var (zone, handle) = ZoneAt(x, y);
        if (zone is FrameZone.Border or FrameZone.Handle or FrameZone.Rotate && PagePointerPt is { } pointer)
        {
            // Рамку тянут, не прерывая набор: блок остаётся в редакторе, и
            // вся правка — по-прежнему один шаг отмены.
            _drag = new FrameDrag(zone, handle, _model.Block, pointer());
            CaptureMouse();
            e.Handled = true;
            return;
        }
        switch (e.ClickCount)
        {
            case 2: _model.SelectWordAt(x, y); break;
            case >= 3: _model.SelectParagraphAt(x, y); break;
            default:
                _model.PlaceCaret(x, y, extend: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                CaptureMouse();
                break;
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var (x, y) = PointOf(e);
        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
        {
            if (!IsMouseCaptured) Cursor = CursorFor(ZoneAt(x, y));
            return;
        }
        if (_drag is { } drag && PagePointerPt is { } pointer)
        {
            var now = pointer();
            ApplyFrame(BlockFrameDrag.Apply(drag.Start, drag.Zone, drag.Handle,
                now.X - drag.StartPointer.X, now.Y - drag.StartPointer.Y, now.X, now.Y));
            return;
        }
        _model.PlaceCaret(x, y, extend: true);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _drag = null;
    }

    /// <summary>Esc во время перетаскивания возвращает рамку, а не заканчивает ввод.</summary>
    private bool CancelFrameDrag()
    {
        if (_drag is not { } drag) return false;
        _drag = null;
        ApplyFrame(drag.Start);
        if (IsMouseCaptured) ReleaseMouseCapture();
        return true;
    }

    private void ApplyFrame(TextBlockOverlay block) =>
        _model.SetFrame(block.XPt, block.YPt, block.WidthPt, block.HeightPt, block.WidthMode,
            block.AutoHeight, block.ShrinkToFit, block.RotationDegrees);

    private (FrameZone Zone, FrameHandle Handle) ZoneAt(double xPt, double yPt) =>
        BlockFrameDrag.HitTest(new OverlayBox(0, 0, _model.Layout.WidthPt, _model.Layout.HeightPt), xPt, yPt,
            BorderDip / _zoom, NexusPdf.Ux.ObjectHandles.HandleToleranceDip / _zoom, NexusPdf.Ux.ObjectHandles.RotateHandleOffsetDip / _zoom);

    private static Cursor CursorFor((FrameZone Zone, FrameHandle Handle) hit) => hit switch
    {
        (FrameZone.Border, _) => Cursors.SizeAll,
        (FrameZone.Rotate, _) => Cursors.Hand,
        (FrameZone.Handle, FrameHandle.TopLeft or FrameHandle.BottomRight) => Cursors.SizeNWSE,
        (FrameZone.Handle, FrameHandle.TopRight or FrameHandle.BottomLeft) => Cursors.SizeNESW,
        (FrameZone.Handle, FrameHandle.Top or FrameHandle.Bottom) => Cursors.SizeNS,
        (FrameZone.Handle, _) => Cursors.SizeWE,
        _ => Cursors.IBeam,
    };

    /// <summary>Точка мыши в пунктах блока (от левого верхнего угла рамки, без поворота).</summary>
    private (double X, double Y) PointOf(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        return ((p.X - Inset.Left) / _zoom, (p.Y - Inset.Top) / _zoom);
    }

    // ----- IME -----

    /// <summary>Окно составления IME — у курсора, а не в углу окна.</summary>
    private void PlaceImeWindow()
    {
        if (!OperatingSystem.IsWindows() || !IsKeyboardFocused) return;
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;
        var (x, top, height) = _model.CaretRect();
        var point = TransformToAncestor(source.RootVisual).Transform(
            new Point(x * _zoom + Inset.Left, (top + height) * _zoom + Inset.Top));
        var device = source.CompositionTarget.TransformToDevice.Transform(point);
        var context = ImmGetContext(source.Handle);
        if (context == IntPtr.Zero) return;
        try
        {
            var form = new CompositionForm
            {
                Style = CfsPoint,
                X = (int)Math.Round(device.X),
                Y = (int)Math.Round(device.Y),
            };
            ImmSetCompositionWindow(context, ref form);
        }
        finally
        {
            ImmReleaseContext(source.Handle, context);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TextBlockCanvasAutomationPeer(this);

    private const int CfsPoint = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionForm
    {
        public int Style;
        public int X;
        public int Y;
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern uint GetCaretBlinkTime();

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hwnd);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr context);

    [DllImport("imm32.dll")]
    private static extern bool ImmSetCompositionWindow(IntPtr context, ref CompositionForm form);
}
