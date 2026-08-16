using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.Printing;
using NexusPdf.Printing.Windows;

namespace NexusPdf.App.Desktop.Services.Printing;

/// <summary>Строка очереди: одно задание так, как его видит пользователь.</summary>
public sealed partial class PrintQueueRow : ObservableObject
{
    public required int JobId { get; init; }
    public required string PrinterName { get; init; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private PrintJobState _state = PrintJobState.Queued;
    [ObservableProperty] private int _pagesPrinted;
    [ObservableProperty] private int _pagesTotal;
    [ObservableProperty] private bool _isActive = true;

    /// <summary>Состояние словами; тексты живут в словаре интерфейса.</summary>
    public string StateText => Loc.Get(PrintJobStateMapper.TitleKey(State));

    /// <summary>«3 из 12» либо просто число, когда всего страниц не сообщили.</summary>
    public string PagesText => PagesTotal > 0
        ? Loc.F("PrintQueuePagesOf", PagesPrinted, PagesTotal)
        : Loc.F("PrintQueuePages", PagesPrinted);

    /// <summary>Приостановленное задание продолжают, идущее — приостанавливают.</summary>
    public bool CanResume => State == PrintJobState.Paused;

    partial void OnStateChanged(PrintJobState value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(CanResume));
    }

    partial void OnPagesPrintedChanged(int value) => OnPropertyChanged(nameof(PagesText));

    partial void OnPagesTotalChanged(int value) => OnPropertyChanged(nameof(PagesText));
}

/// <summary>
/// Очередь печати программы: какие задания она отправила и что с ними сейчас.
///
/// Смысл ровно один — печать не должна заканчиваться словами «задание передано
/// принтеру». Дальше человеку нужно видеть, дошло ли оно, сколько напечатано и
/// как остановить, если отправил не то. Системное окно принтера для этого
/// годится плохо: там чужие задания вперемешку и никакой связи с документом.
///
/// Опрашиваются ТОЛЬКО свои задания: чужую печать программа показывает, но не
/// трогает — отменить чужое на общем принтере хуже, чем не отменить своё.
/// </summary>
public sealed class PrintQueueService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, HashSet<int>> _own = new(StringComparer.OrdinalIgnoreCase);
    private WindowsPrintQueueMonitor? _monitor;
    private bool _disposed;

    public PrintQueueService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // Раз в полторы секунды: чаще — лишняя нагрузка на спулер, реже —
            // ход печати выглядит «залипшим».
            Interval = TimeSpan.FromSeconds(1.5),
        };
        _timer.Tick += (_, _) => Refresh();
    }

    /// <summary>Задания, отправленные программой, — свежим сверху.</summary>
    public ObservableCollection<PrintQueueRow> Jobs { get; } = new();

    /// <summary>Есть ли что показывать: по этому признаку прячется пункт меню.</summary>
    public bool HasJobs => Jobs.Count > 0;

    public event EventHandler? Changed;

    /// <summary>Взять под наблюдение задания, только что отданные принтеру.</summary>
    public void Track(string printerName, IReadOnlyList<int> jobIds, string name)
    {
        if (_disposed || jobIds.Count == 0) return;
        if (!_own.TryGetValue(printerName, out var set))
            _own[printerName] = set = new HashSet<int>();

        foreach (var id in jobIds)
        {
            if (!set.Add(id)) continue;
            Jobs.Insert(0, new PrintQueueRow
            {
                JobId = id,
                PrinterName = printerName,
                Name = name,
            });
        }
        Refresh();
        _timer.Start();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Перечитать состояние из очереди Windows.</summary>
    public void Refresh()
    {
        if (_disposed) return;
        _monitor ??= new WindowsPrintQueueMonitor();

        var anyActive = false;
        foreach (var printer in _own.Keys.ToList())
        {
            var snapshots = _monitor.Read(printer, _own[printer]);
            foreach (var row in Jobs.Where(j => j.PrinterName == printer).ToList())
            {
                var found = snapshots.FirstOrDefault(s => s.JobId == row.JobId);
                if (found == null)
                {
                    // Задания в очереди больше нет: спулер удаляет напечатанное.
                    // Это не «пропало», а «допечатано».
                    if (row.IsActive && row.State != PrintJobState.Cancelled)
                        row.State = PrintJobState.Completed;
                    row.IsActive = false;
                    continue;
                }

                row.State = found.State;
                row.PagesPrinted = found.PagesPrinted;
                row.PagesTotal = found.PagesTotal;
                row.IsActive = found.IsActive;
                if (found.Name.Length > 0) row.Name = found.Name;
                anyActive |= found.IsActive;
            }
        }

        // Когда живых заданий не осталось, опрос останавливается: держать
        // таймер ради истории бессмысленно.
        if (!anyActive) _timer.Stop();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Cancel(PrintQueueRow row) => Act(row, (m, p, id) => m.Cancel(p, id));

    public bool Pause(PrintQueueRow row) => Act(row, (m, p, id) => m.Pause(p, id));

    public bool Resume(PrintQueueRow row) => Act(row, (m, p, id) => m.Resume(p, id));

    private bool Act(PrintQueueRow row, Func<WindowsPrintQueueMonitor, string, int, bool> action)
    {
        if (_disposed) return false;
        _monitor ??= new WindowsPrintQueueMonitor();
        var ok = action(_monitor, row.PrinterName, row.JobId);
        Refresh();
        return ok;
    }

    /// <summary>Убрать из списка законченные задания — историю чистит пользователь.</summary>
    public void ClearFinished()
    {
        foreach (var row in Jobs.Where(j => !j.IsActive).ToList())
            Jobs.Remove(row);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _monitor?.Dispose();
        _monitor = null;
    }
}
