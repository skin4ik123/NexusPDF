using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusPdf.Application;

namespace NexusPdf.App.Desktop.Views;

/// <summary>
/// Слои документа. Видимость записывается в конфигурацию /OCProperties по
/// умолчанию, поэтому получается ОБЫЧНЫЙ PDF, который открывается везде уже
/// с нужными слоями. Содержимое выключённых слоёв остаётся в файле — это не
/// вымарывание, и в подсказке про это сказано прямо.
/// </summary>
public partial class LayersDialog : Window
{
    public sealed partial class Row : ObservableObject
    {
        public Row(PdfLayer layer)
        {
            Reference = layer.Reference;
            Name = layer.Name;
            IsVisible = layer.IsVisible;
        }

        public string Reference { get; }
        public string Name { get; }

        [ObservableProperty]
        private bool _isVisible;
    }

    private LayersDialog() => InitializeComponent();

    /// <summary>Возвращает выбранную видимость слоёв или null, если пользователь отказался.</summary>
    public static IReadOnlyDictionary<string, bool>? Choose(Window? owner, IReadOnlyList<PdfLayer> layers)
    {
        var dialog = new LayersDialog();
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        var rows = layers.Select(l => new Row(l)).ToList();
        dialog.List.ItemsSource = rows;
        if (dialog.ShowDialog() != true)
            return null;
        return rows.ToDictionary(r => r.Reference, r => r.IsVisible);
    }

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;
}
