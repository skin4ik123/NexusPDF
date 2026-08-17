using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.App.Desktop.Views;

/// <summary>Что человек задал строке: буквы и оформление.</summary>
internal sealed record InlineTextResult(
    string Text, string FontFamily, bool Bold, bool Italic, double FontSizePt, uint ColorArgb)
{
    /// <summary>Менялось ли оформление. Пустая гарнитура — «оставить как было».</summary>
    public bool StyleChanged => FontFamily.Length > 0;
}

/// <summary>
/// Правка строки ПРЯМО НА СТРАНИЦЕ: поле ввода поверх текста и панель
/// оформления над ним. Отдельное окно для этого не нужно — раньше каждая
/// правка стоила четырёх действий, а теперь это двойной клик и ввод.
///
/// Панель обязана быть здесь же: правка без выбора шрифта, кегля и цвета
/// наполовину бессмысленна — поменять можно только буквы, а всё, ради чего
/// текст обычно и правят, остаётся недоступным.
/// </summary>
internal sealed class InlineTextEditor
{
    private readonly Popup _popup;
    private readonly Grid _root;
    private readonly TextBox _box;
    private readonly ComboBox _family;
    private readonly TextBox _size;
    private readonly ToggleButton _bold;
    private readonly ToggleButton _italic;
    private readonly StackPanel _colors;
    private readonly Rect _rect;
    private readonly string _keepFamily;

    private readonly string _originalFamily;
    private readonly double _originalSize;
    private readonly uint _originalColor;
    private uint _color;
    private bool _closed;

    /// <summary>Правка завершена: результат или null, если отменили.</summary>
    public event EventHandler<InlineTextResult?>? Finished;

    private static readonly uint[] Palette =
    [
        0xFF1B1C20, 0xFF6B7280, 0xFFD3282F, 0xFF2563EB, 0xFF15803D, 0xFFB45309,
    ];

    private InlineTextEditor(
        UIElement adorned, Rect rect, string text,
        string family, double fontSizePt, uint colorArgb, double scale)
    {
        _rect = rect;
        _originalFamily = family;
        _originalSize = fontSizePt;
        _originalColor = colorArgb;
        _color = colorArgb;

        // Первым пунктом — «оставить как есть» с именем нынешнего шрифта.
        // Без него список у документа со шрифтом не из каталога (Helvetica,
        // встроенное подмножество) выглядел бы просто пустым, и было бы
        // непонятно, чем строка написана сейчас.
        _keepFamily = string.IsNullOrWhiteSpace(family)
            ? Loc.Get("FontKeepCurrent")
            : Loc.F("FontKeepNamed", ShortFontName(family));

        var items = new List<string> { _keepFamily };
        items.AddRange(PdfFontCatalog.AvailableFamilies());

        _family = new ComboBox
        {
            Width = 132,
            ItemsSource = items,
            ToolTip = Loc.Get("FontFamilyLabel"),
        };
        // Имя шрифта в PDF часто идёт с приставкой подмножества («ABCDEF+Arial»),
        // поэтому гарнитура ищется вхождением, а не точным совпадением.
        var match = PdfFontCatalog.AvailableFamilies()
            .FirstOrDefault(f => family.Contains(f, StringComparison.OrdinalIgnoreCase));
        _family.SelectedItem = match ?? _keepFamily;

        _size = new TextBox
        {
            Width = 42,
            Text = fontSizePt > 0 ? Math.Round(fontSizePt, 1).ToString(CultureInfo.CurrentCulture) : "",
            ToolTip = Loc.Get("FontSizeLabel"),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        _bold = MakeToggle("Ж", FontWeights.Bold, FontStyles.Normal, Loc.Get("FontBold"));
        _italic = MakeToggle("К", FontWeights.Normal, FontStyles.Italic, Loc.Get("FontItalic"));

        _colors = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
        foreach (var swatch in Palette)
            _colors.Children.Add(MakeSwatch(swatch));

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var child in new UIElement[] { _family, _size, _bold, _italic, _colors })
        {
            if (child is FrameworkElement fe && child != _family)
                fe.Margin = new Thickness(4, 0, 0, 0);
            bar.Children.Add(child);
        }

        var barHost = new Border
        {
            Child = bar,
            Padding = new Thickness(5),
            CornerRadius = new CornerRadius(6),
            Background = Brush("PanelBg", Colors.White),
            BorderBrush = Brush("PanelBorder", Colors.Gray),
            BorderThickness = new Thickness(1),
        };

        _box = new TextBox
        {
            Text = text,
            // Кегль поля — кегль строки на экране: править надо в том размере,
            // в котором текст и будет виден, а не в мелком поле поверх крупного
            // заголовка.
            MinWidth = Math.Max(rect.Width + 60, 200),
            FontSize = Math.Clamp(fontSizePt * scale, 8, 72),
            AcceptsReturn = false,
            TextWrapping = TextWrapping.NoWrap,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(3, 0, 3, 0),
            BorderThickness = new Thickness(2),
            BorderBrush = Brush("Accent", Colors.DodgerBlue),
            Background = Brush("InputBg", Colors.White),
            Foreground = Brush("TextBrush", Colors.Black),
        };
        _box.KeyDown += OnKeyDown;
        // Курсор возвращается в текст: после переключения начертания человек
        // продолжает набирать, а не ищет, куда кликнуть.
        _bold.Click += (_, _) => _box.Focus();
        _italic.Click += (_, _) => _box.Focus();

        _root = new Grid();
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(barHost, 0);
        Grid.SetRow(_box, 1);
        _root.Children.Add(barHost);
        _root.Children.Add(_box);
        // Enter и Esc обязаны работать из любого места редактора: после клика
        // по кнопке панели фокус уходит на неё, и правка иначе зависает —
        // человек жмёт Enter, а ничего не происходит.
        _root.PreviewKeyDown += OnKeyDown;

        // Popup, а НЕ слой украшений. У адорнера нет собственной области
        // фокуса: мышь до него доходит, а клавиатура — нет, и поле выглядело
        // рабочим, но не принимало ни одной буквы. Popup такую область создаёт
        // сам, поэтому ввод, Enter и Esc работают как в обычном окне.
        _popup = new Popup
        {
            Child = _root,
            PlacementTarget = adorned,
            Placement = PlacementMode.Relative,
            HorizontalOffset = Math.Max(rect.X - 4, 0),
            AllowsTransparency = true,
            // StaysOpen обязателен: выпадающий список гарнитур — сам по себе
            // popup, и при StaysOpen=false его открытие считалось «кликом
            // мимо» и схлопывало редактор вместе с несохранённой правкой.
            // Закрытие по клику вне страницы делает вызывающий код явно.
            StaysOpen = true,
            Focusable = false,
            PopupAnimation = PopupAnimation.None,
        };
    }

    private static ToggleButton MakeToggle(string glyph, FontWeight weight, FontStyle style, string tip)
    {
        var button = new ToggleButton
        {
            Content = new TextBlock { Text = glyph, FontWeight = weight, FontStyle = style },
            Width = 27,
            Height = 24,
            ToolTip = tip,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, tip);
        return button;
    }

    private Border MakeSwatch(uint argb)
    {
        var color = Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        var swatch = new Border
        {
            Width = 17,
            Height = 17,
            Margin = new Thickness(2, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(color),
            BorderThickness = new Thickness(2),
            BorderBrush = argb == _color ? Brush("Accent", Colors.DodgerBlue) : Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = Loc.Get("AtColor"),
        };
        swatch.MouseLeftButtonDown += (_, e) =>
        {
            _color = argb;
            foreach (var child in _colors.Children.OfType<Border>())
                child.BorderBrush = Brushes.Transparent;
            swatch.BorderBrush = Brush("Accent", Colors.DodgerBlue);
            _box.Focus();
            e.Handled = true;
        };
        return swatch;
    }

    /// <summary>Имя шрифта без приставки подмножества: «ABCDEF+Arial» → «Arial».</summary>
    private static string ShortFontName(string name)
    {
        var plus = name.IndexOf('+');
        return plus is > 0 and < 7 ? name[(plus + 1)..] : name;
    }

    // Полное имя обязательно: в решении есть своё пространство имён
    // NexusPdf.Application, и короткое Application разрешается в него.
    private static Brush Brush(string key, Color fallback) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush
        ?? new SolidColorBrush(fallback);

    /// <summary>Открывает поле над строкой. Возвращает null, если слой украшений недоступен.</summary>
    public static InlineTextEditor? Open(
        UIElement adorned, Rect rectDiu, string text,
        string family, double fontSizePt, uint colorArgb, double scale)
    {
        var editor = new InlineTextEditor(adorned, rectDiu, text, family, fontSizePt, colorArgb, scale);

        // Панель встаёт НАД строкой, а если сверху нет места — под ней.
        editor._root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var barHeight = editor._root.DesiredSize.Height - Math.Max(rectDiu.Height * 1.6, 26);
        var top = rectDiu.Y - Math.Max(barHeight, 0) - 6;
        editor._popup.VerticalOffset = top < 0 ? rectDiu.Y + rectDiu.Height + 6 : top;
        editor._popup.IsOpen = true;

        // Фокус — следующим проходом разметки: до него поле ещё не размещено,
        // и Focus() ушёл бы в никуда.
        editor._root.Dispatcher.BeginInvoke(new Action(() =>
        {
            editor._box.Focus();
            Keyboard.Focus(editor._box);
            editor._box.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);

        return editor;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Пока открыт список гарнитур, Enter принадлежит ЕМУ: он подтверждает
        // выбор пункта. Иначе выбор шрифта с клавиатуры вместо этого применял
        // всю правку — с той гарнитурой, что была выделена в этот момент.
        if (_family.IsDropDownOpen && e.Key is Key.Enter or Key.Escape)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                Apply();
                e.Handled = true;
                break;
            case Key.Escape:
                Close(null); // отмена обязана возвращать строку в прежний вид
                e.Handled = true;
                break;
        }
    }

    private void Apply()
    {
        var picked = _family.SelectedItem as string ?? "";
        var family = picked == _keepFamily ? "" : picked;
        var bold = _bold.IsChecked == true;
        var italic = _italic.IsChecked == true;

        var size = _originalSize;
        if (double.TryParse(_size.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var typed) ||
            double.TryParse(_size.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out typed))
        {
            if (typed is >= 4 and <= 300)
                size = typed;
        }

        // Оформление считается изменённым, только если его правда меняли:
        // иначе строка зря пошла бы по пути замены объекта, а самый безопасный
        // путь — оставить исходный шрифт нетронутым.
        var sameFamily = family.Length == 0 ||
            _originalFamily.Contains(family, StringComparison.OrdinalIgnoreCase);
        var styleChanged = !sameFamily || bold || italic ||
            Math.Abs(size - _originalSize) > 0.05 || _color != _originalColor;

        Close(new InlineTextResult(
            _box.Text,
            styleChanged ? (family.Length > 0 ? family : PdfFontCatalog.DefaultFamily) : "",
            bold, italic,
            styleChanged ? size : 0,
            styleChanged ? _color : 0));
    }

    /// <summary>Убирает поле и сообщает результат. Повторные вызовы безвредны.</summary>
    public void Close(InlineTextResult? result)
    {
        if (_closed)
            return;
        _closed = true;

        _popup.IsOpen = false;
        Finished?.Invoke(this, result);
    }

    /// <summary>Клик мимо поля — применить: так ведут себя все поля правки на месте.</summary>
    public void CommitFromOutside() => Apply();

}
