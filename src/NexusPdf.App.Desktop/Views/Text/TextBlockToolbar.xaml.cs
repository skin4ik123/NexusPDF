using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using NexusPdf.App.Desktop.ViewModels;

namespace NexusPdf.App.Desktop.Views.Text;

/// <summary>
/// Панель оформления над блоком: шрифт, размер с −/+, начертание, цвет,
/// выравнивание и «⋯» с остальным. Привязана к состоянию блока в
/// <see cref="DocumentViewModel"/>; кнопки не забирают клавиатуру у редактора.
/// </summary>
public partial class TextBlockToolbar : UserControl
{
    private readonly Popup _colors = new()
    {
        Placement = PlacementMode.Bottom,
        StaysOpen = false,
        AllowsTransparency = true,
        PopupAnimation = PopupAnimation.None,
    };

    public TextBlockToolbar()
    {
        InitializeComponent();
        Size.KeyDown += OnFieldKeyDown;
        Family.FamilyChanged += (_, _) =>
        {
            if (DataContext is DocumentViewModel vm)
                vm.SetTextFamily(Family.Family);
        };
        ColorButton.Click += (_, _) => OpenColors();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is DocumentViewModel old) old.PropertyChanged -= OnVmPropertyChanged;
            if (e.NewValue is DocumentViewModel vm) vm.PropertyChanged += OnVmPropertyChanged;
            ShowFamily();
        };
    }

    /// <summary>Недавние шрифты, шрифты документа и запоминание выбора — от представления документа.</summary>
    internal void SetFontContext(FontPickerContext? context) =>
        Family.Configure(null, (DataContext as DocumentViewModel)?.TextFamily ?? "", context);

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.TextFamily))
            ShowFamily();
    }

    private void ShowFamily()
    {
        if (DataContext is DocumentViewModel vm && !Family.IsOpen)
            Family.ShowFamily(vm.TextFamily);
    }

    private void OpenColors()
    {
        if (DataContext is not DocumentViewModel vm) return;
        var picker = new ColorPickerPopup(vm.TextColorArgb);
        picker.ColorPicked += (_, argb) =>
        {
            _colors.IsOpen = false;
            vm.SetTextColor(argb);
        };
        _colors.Child = picker;
        _colors.PlacementTarget = ColorButton;
        _colors.IsOpen = true;
    }

    /// <summary>Enter в поле — применить и вернуться к тексту.</summary>
    private void OnFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }
}
