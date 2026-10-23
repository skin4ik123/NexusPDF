using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NexusPdf.Pdf.Abstractions.TextBlocks;

namespace NexusPdf.App.Desktop.Views.Text;

/// <summary>
/// Редактор текстового блока прямо на странице. Рисует ТУ ЖЕ раскладку,
/// по которой блок пишется в PDF: глифы из тех же файлов шрифтов в тех же
/// позициях, в масштабе страницы. Всё поведение (курсор, выделение, стили,
/// отмена, буфер) — в <see cref="TextBlockEditorModel"/>; здесь только экран,
/// клавиши, мышь и IME.
///
/// Координаты элемента — рамка блока без поворота (пункты × масштаб);
/// поворот блока задаёт тот, кто ставит редактор на страницу.
/// </summary>
internal sealed class TextBlockCanvas : FrameworkElement
{
    public const string RichClipboardFormat = "NexusPdf.TextBlock";
    public const string EditorAutomationId = "TextBlockEditor";

    private static readonly Pen FramePen = MakeFramePen();
    private static readonly Pen FallbackPen = MakeFallbackPen();

    private readonly TextBlockEditorModel _model;
    private readonly DispatcherTimer? _blink;
    private double _zoom;
    private bool _caretVisible = true;
    private bool _composing;

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

    /// <summary>Ввод закончен: Esc или фокус ушёл на страницу.</summary>
    public event EventHandler? CommitRequested;

    /// <summary>Ввод отвергнут; аргумент — ключ словаря для строки состояния.</summary>
    public event EventHandler<string>? InputRejected;

    // ----- Отрисовка -----

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Max(1, _model.Layout.WidthPt * _zoom), Math.Max(1, _model.Layout.HeightPt * _zoom));

    protected override void OnRender(DrawingContext dc)
    {
        var layout = _model.Layout;
        var frame = new Rect(0, 0, layout.WidthPt * _zoom, layout.HeightPt * _zoom);
        // Прозрачная заливка — чтобы мышь попадала и между строк.
        dc.DrawRectangle(Brushes.Transparent, FramePen, frame);

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

        var (caretX, caretTop, caretHeight) = _model.CaretRect();
        if (_model.Composition.Length > 0)
            DrawComposition(dc, caretX, caretTop, caretHeight, pixelsPerDip);
        else if (IsKeyboardFocused && _caretVisible)
            dc.DrawRectangle(GlyphCache.Brush(_model.StyleAtCaret.ColorArgb | 0xFF000000), null,
                new Rect(caretX * _zoom, caretTop * _zoom, Math.Max(1, 1 / pixelsPerDip), caretHeight * _zoom));
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
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
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
            case Key.Escape: CommitRequested?.Invoke(this, EventArgs.Empty); break;
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
        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        var (x, y) = PointOf(e);
        _model.PlaceCaret(x, y, extend: true);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private (double X, double Y) PointOf(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        return (p.X / _zoom, p.Y / _zoom);
    }

    // ----- IME -----

    /// <summary>Окно составления IME — у курсора, а не в углу окна.</summary>
    private void PlaceImeWindow()
    {
        if (!OperatingSystem.IsWindows() || !IsKeyboardFocused) return;
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;
        var (x, top, height) = _model.CaretRect();
        var point = TransformToAncestor(source.RootVisual).Transform(new Point(x * _zoom, (top + height) * _zoom));
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
