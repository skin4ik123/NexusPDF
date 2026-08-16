using System.Windows;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.App.Desktop.Services.Printing;

namespace NexusPdf.App.Desktop.Views;

/// <summary>
/// Окно очереди печати. Показывает задания, отправленные программой, и даёт
/// их приостановить, продолжить и отменить — чтобы печать не заканчивалась
/// словами «задание передано принтеру».
/// </summary>
public partial class PrintQueueDialog : Window
{
    private readonly PrintQueueService _queue;

    private PrintQueueDialog(PrintQueueService queue)
    {
        _queue = queue;
        InitializeComponent();
        DataContext = queue;
        // Пока окно открыто, состояние перечитывается: пользователь смотрит
        // именно сюда, и «залипший» список здесь заметнее всего.
        _queue.Refresh();
    }

    public static void Show(Window? owner, PrintQueueService queue)
    {
        var dialog = new PrintQueueDialog(queue);
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        dialog.ShowDialog();
    }

    private PrintQueueRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.Tag as PrintQueueRow;

    private void OnPause(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) _queue.Pause(row);
    }

    private void OnResume(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) _queue.Resume(row);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        // Отмена печати необратима — спрашиваем, но коротко и по делу.
        if (!ConfirmDialog.Ask(this, Loc.Get("PrintQueueCancel"),
                Loc.F("PrintQueueCancelAsk", row.Name), "", Loc.Get("PrintQueueCancel")))
            return;
        _queue.Cancel(row);
    }

    private void OnClearFinished(object sender, RoutedEventArgs e) => _queue.ClearFinished();
}
