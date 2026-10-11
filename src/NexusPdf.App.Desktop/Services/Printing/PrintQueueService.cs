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

    /// <summary>
    /// Очередь принтера сейчас не читается (сеть, принтер выключен): показано
    /// последнее известное состояние с пометкой, а не выдуманное «готово».
    /// </summary>
    [ObservableProperty] private bool _isUnreachable;

    /// <summary>Состояние словами; тексты живут в словаре интерфейса.</summary>
    public string StateText => IsUnreachable
        ? Loc.F("PrintJobStateUnreachable", Loc.Get(PrintJobStateMapper.TitleKey(State)))
        : Loc.Get(PrintJobStateMapper.TitleKey(State));

    partial void OnIsUnreachableChanged(bool value) => OnPropertyChanged(nameof(StateText));

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
    /// <summary>Обычный шаг опроса: чаще — нагрузка на спулер, реже — ход «залипает».</summary>
    private static readonly TimeSpan NormalInterval = TimeSpan.FromSeconds(1.5);

    /// <summary>Шаг для очереди, которая не отвечает: не дёргать сеть каждые полторы секунды.</summary>
    private static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(10);

    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, HashSet<int>> _own = new(StringComparer.OrdinalIgnoreCase);
    private bool _refreshing;
    private bool _disposed;

    public PrintQueueService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = NormalInterval };
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

    /// <summary>
    /// Перечитать состояние из очереди Windows. Чтение — в фоне: сетевая
    /// очередь, которая не отвечает, раньше подвешивала всё окно программы на
    /// каждом шаге опроса.
    /// </summary>
    public async void Refresh()
    {
        if (_disposed || _refreshing) return;
        _refreshing = true;
        try
        {
            var requests = _own.ToDictionary(
                p => p.Key, p => (IReadOnlySet<int>)new HashSet<int>(p.Value), StringComparer.OrdinalIgnoreCase);
            var results = await Task.Run(() =>
            {
                using var monitor = new WindowsPrintQueueMonitor();
                return requests.ToDictionary(
                    r => r.Key, r => monitor.Read(r.Key, r.Value), StringComparer.OrdinalIgnoreCase);
            });
            if (_disposed) return;
            Apply(results);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Очередь печати не прочиталась");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Apply(IReadOnlyDictionary<string, IReadOnlyList<PrintJobSnapshot>?> results)
    {
        var anyActive = false;
        var anyUnreachable = false;
        foreach (var (printer, snapshots) in results)
        {
            var rows = Jobs.Where(j => string.Equals(j.PrinterName, printer, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var row in rows)
            {
                if (snapshots == null)
                {
                    // Очередь не прочиталась: последнее известное состояние
                    // остаётся, с пометкой «нет связи».
                    row.IsUnreachable = row.IsActive;
                    anyActive |= row.IsActive;
                    anyUnreachable |= row.IsActive;
                    continue;
                }
                row.IsUnreachable = false;

                var found = snapshots.FirstOrDefault(s => s.JobId == row.JobId);
                if (found == null)
                {
                    // Задания в очереди больше нет: спулер отдал его принтеру и
                    // убрал. Это «передано принтеру», а не «пропало»; было ли оно
                    // действительно напечатано, очередь уже не скажет.
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
        // таймер ради истории бессмысленно. Недоступная очередь опрашивается реже.
        _timer.Interval = anyUnreachable ? SlowInterval : NormalInterval;
        if (!anyActive) _timer.Stop();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<bool> Cancel(PrintQueueRow row) => Act(row, (m, p, id) => m.Cancel(p, id));

    public Task<bool> Pause(PrintQueueRow row) => Act(row, (m, p, id) => m.Pause(p, id));

    public Task<bool> Resume(PrintQueueRow row) => Act(row, (m, p, id) => m.Resume(p, id));

    private async Task<bool> Act(PrintQueueRow row, Func<WindowsPrintQueueMonitor, string, int, bool> action)
    {
        if (_disposed) return false;
        var printer = row.PrinterName;
        var id = row.JobId;
        var ok = await Task.Run(() =>
        {
            using var monitor = new WindowsPrintQueueMonitor();
            return action(monitor, printer, id);
        });
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
    }
}
