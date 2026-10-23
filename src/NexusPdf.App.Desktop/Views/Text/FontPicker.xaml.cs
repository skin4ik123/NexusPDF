using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.Ux;

namespace NexusPdf.App.Desktop.Views.Text;

/// <summary>Откуда список берёт недавние шрифты и шрифты документа и куда сообщает о выборе.</summary>
internal sealed record FontPickerContext(
    IReadOnlyList<string> Recent,
    Func<Task<IReadOnlyList<string>>>? DocumentFontNames,
    Action<string>? Picked);

/// <summary>Строка списка: заголовок группы или шрифт.</summary>
public sealed record FontPickerRow(bool IsHeader, string Title, string Family, string? Note, bool Enabled, FontFamily? Preview)
{
    public bool IsFont => !IsHeader;
    public bool HasNote => !string.IsNullOrEmpty(Note);
    public bool Selectable => !IsHeader && Enabled;
    public string Spoken => Note is null ? Title : Title + ". " + Note;
}

/// <summary>
/// Выбор шрифта как в редакторах вёрстки: поиск, недавние, шрифты документа
/// и все установленные; каждое имя нарисовано своим шрифтом, а то, что в PDF
/// не попадёт, видно с причиной и не выбирается.
/// </summary>
public partial class FontPicker : UserControl
{
    private static readonly Dictionary<string, FontFamily> Previews = new(StringComparer.OrdinalIgnoreCase);

    private string? _keepLabel;
    private FontPickerContext? _context;
    private IReadOnlyList<(string PdfName, string? Family)> _documentFonts = [];
    private bool _documentRequested;

    public FontPicker()
    {
        InitializeComponent();
        DropDown.Opened += async (_, _) => await OnOpenedAsync();
        Search.TextChanged += (_, _) => Rebuild();
        Search.PreviewKeyDown += OnSearchKey;
        List.PreviewKeyDown += OnListKey;
        List.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (List.SelectedItem is FontPickerRow { Selectable: true } row)
                Pick(row);
        };
    }

    /// <summary>Выбранная гарнитура; пусто — «оставить как есть».</summary>
    public string Family { get; private set; } = "";

    /// <summary>Открыт ли список — пока он открыт, Enter и Esc принадлежат ему.</summary>
    public bool IsOpen => DropDown.IsOpen;

    public event EventHandler? FamilyChanged;

    /// <param name="keepLabel">Первая строка «Оставить: X»; null — без неё.</param>
    internal void Configure(string? keepLabel, string family, FontPickerContext? context)
    {
        _keepLabel = keepLabel;
        _context = context;
        Family = family;
        ShowCurrent();
    }

    private async Task OnOpenedAsync()
    {
        Search.Text = "";
        if (!FontSources.IsReady)
        {
            List.ItemsSource = new[] { new FontPickerRow(true, "…", "", null, true, null) };
            await FontSources.WarmUpAsync();
        }
        if (!_documentRequested && _context?.DocumentFontNames is { } load)
        {
            _documentRequested = true;
            try
            {
                var names = await load();
                _documentFonts = names.Select(n => (n, FontSources.SubstituteFor(n))).ToList();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Без шрифтов документа список всё равно полезен.
                Serilog.Log.Debug(ex, "Шрифты документа для списка не прочитались");
            }
        }
        Rebuild();
        Search.Focus();
        Keyboard.Focus(Search);
    }

    private void Rebuild()
    {
        if (!FontSources.IsReady)
            return;
        var query = Search.Text;
        var rows = new List<FontPickerRow>();
        if (_keepLabel != null && string.IsNullOrWhiteSpace(query))
            rows.Add(new FontPickerRow(false, _keepLabel, "", null, true, null));

        FontGroup? group = null;
        foreach (var item in FontPickerModel.Build(FontSources.Choices, _context?.Recent ?? [], _documentFonts, query))
        {
            if (item.Group != group)
            {
                group = item.Group;
                rows.Add(new FontPickerRow(true, Loc.Get(item.Group switch
                {
                    FontGroup.Recent => "FontRecent",
                    FontGroup.InDocument => "FontInDocument",
                    _ => "FontAll",
                }), "", null, true, null));
            }
            var note = item.NoteKey switch
            {
                null => null,
                "FontWrittenWith" => Loc.F("FontWrittenWith", item.AppliedFamily),
                _ => Loc.Get(item.NoteKey),
            };
            rows.Add(new FontPickerRow(false, item.Family, item.AppliedFamily, note, item.Enabled, PreviewOf(item.AppliedFamily)));
        }
        List.ItemsSource = rows;
        List.SelectedItem = rows.FirstOrDefault(r => r.Selectable && r.Family.Equals(Family, StringComparison.OrdinalIgnoreCase));
    }

    private static FontFamily PreviewOf(string family)
    {
        if (!Previews.TryGetValue(family, out var preview))
            Previews[family] = preview = new FontFamily(family);
        return preview;
    }

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                FocusRow(List.Items.OfType<FontPickerRow>().FirstOrDefault(r => r.Selectable));
                e.Handled = true;
                break;
            case Key.Enter:
                if (List.Items.OfType<FontPickerRow>().FirstOrDefault(r => r.Selectable) is { } first)
                    Pick(first);
                e.Handled = true;
                break;
            case Key.Escape:
                Toggle.IsChecked = false;
                e.Handled = true;
                break;
        }
    }

    private void OnListKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when List.SelectedItem is FontPickerRow { Selectable: true } row:
                Pick(row);
                e.Handled = true;
                break;
            case Key.Escape:
                Toggle.IsChecked = false;
                e.Handled = true;
                break;
        }
    }

    private void FocusRow(FontPickerRow? row)
    {
        if (row == null) return;
        List.SelectedItem = row;
        List.ScrollIntoView(row);
        (List.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem)?.Focus();
    }

    private void Pick(FontPickerRow row)
    {
        Family = row.Family;
        ShowCurrent();
        Toggle.IsChecked = false;
        if (row.Family.Length > 0)
            _context?.Picked?.Invoke(row.Family);
        FamilyChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowCurrent()
    {
        Current.Text = Family.Length == 0 ? _keepLabel ?? "" : Family;
        if (Family.Length > 0)
            Current.FontFamily = PreviewOf(Family);
        else
            Current.ClearValue(TextBlock.FontFamilyProperty);
    }
}
