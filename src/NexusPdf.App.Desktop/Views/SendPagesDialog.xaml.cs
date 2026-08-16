using System.Windows;
using System.Windows.Controls;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.ViewModels;

namespace NexusPdf.App.Desktop.Views;

/// <param name="Target">Документ-приёмник.</param>
/// <param name="InsertIndex">Куда вставить: 0 — в начало, число страниц — в конец.</param>
public sealed record SendPagesRequest(DocumentViewModel Target, int InsertIndex);

/// <summary>
/// Отправка выбранных страниц в другой открытый документ.
///
/// Надёжный путь для того же, что делает перетаскивание на вкладку: жест
/// удобен, но зависеть только от него нельзя — он требует и точного
/// движения мышью, и открытого организатора у обоих документов.
///
/// Страницы КОПИРУЮТСЯ: исходный документ остаётся нетронутым, и об этом
/// сказано прямо в окне — «перенос», после которого пропали страницы, был бы
/// неприятной неожиданностью.
/// </summary>
public partial class SendPagesDialog : Window
{
    private SendPagesRequest? _result;
    private IReadOnlyList<DocumentViewModel> _targets = Array.Empty<DocumentViewModel>();

    private SendPagesDialog() => InitializeComponent();

    public static SendPagesRequest? Show(
        Window? owner, IReadOnlyList<DocumentViewModel> targets, int pageCount)
    {
        if (targets.Count == 0) return null;

        var dialog = new SendPagesDialog { _targets = targets };
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;

        dialog.IntroText.Text = Loc.F("SendPagesIntro", pageCount);
        dialog.TargetCombo.ItemsSource = targets;
        dialog.TargetCombo.SelectedIndex = 0;
        dialog.UpdateHint();
        dialog.ShowDialog();
        return dialog._result;
    }

    private DocumentViewModel Target => _targets[Math.Max(0, TargetCombo.SelectedIndex)];

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e) => UpdateHint();

    private void UpdateHint()
    {
        if (RangeHint == null || _targets.Count == 0) return;
        RangeHint.Text = Loc.F("SendPagesRangeHint", Target.PageCount);
    }

    private void OnSend(object sender, RoutedEventArgs e)
    {
        var target = Target;
        var count = target.PageCount;

        var index = count;                                   // по умолчанию — в конец
        if (AtStart.IsChecked == true) index = 0;
        else if (BeforePage.IsChecked == true)
        {
            if (!int.TryParse(PageBox.Text.Trim(), out var number))
            {
                // Номер не разобран — не закрываемся молча, а показываем,
                // какие номера тут вообще уместны.
                RangeHint.Text = Loc.F("SendPagesRangeHint", count);
                PageBox.Focus();
                PageBox.SelectAll();
                return;
            }
            // Номер страницы человеческий, с единицы; «перед 1» — это начало.
            index = Math.Clamp(number - 1, 0, count);
        }

        _result = new SendPagesRequest(target, index);
        DialogResult = true;
    }
}
