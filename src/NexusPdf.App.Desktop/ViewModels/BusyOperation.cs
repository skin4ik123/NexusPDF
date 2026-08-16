using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusPdf.App.Desktop.Localization;

namespace NexusPdf.App.Desktop.ViewModels;

/// <summary>
/// Ход длинной операции: что делается, сколько сделано, сколько идёт и кнопка
/// «Прервать».
///
/// До этого сжатие трёхсот страниц выглядело так: строка «Сжатие
/// изображений…» и минута тишины, после которой появлялось «Готово».
/// Пользователю негде было увидеть, движется ли дело, и нечем остановить
/// операцию, запущенную по ошибке. Отсюда три обязательства этого класса:
///
/// 1. показывать долю выполненного, когда она известна, и честно признаваться
///    полосой «идёт», когда неизвестна;
/// 2. показывать время — по нему видно, что программа не зависла;
/// 3. давать прервать, причём отмена доводится до самой операции токеном, а не
///    просто прячет окошко.
/// </summary>
public sealed partial class BusyOperation : ObservableObject
{
    private readonly DispatcherTimer _clock;
    private readonly Stopwatch _watch = new();
    private CancellationTokenSource? _cts;

    public BusyOperation()
    {
        _clock = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _clock.Tick += (_, _) => OnPropertyChanged(nameof(ElapsedText));
    }

    [ObservableProperty]
    private bool _isRunning;

    /// <summary>Что именно делается — словами пользователя, а не именем метода.</summary>
    [ObservableProperty]
    private string _title = "";

    /// <summary>Уточнение: страница 12 из 333, файл 3 из 10.</summary>
    [ObservableProperty]
    private string _detail = "";

    /// <summary>Доля выполненного 0..1; неизвестна — бегущая полоса.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    private double _progress;

    [ObservableProperty]
    private bool _isIndeterminate = true;

    /// <summary>Отмену предлагаем только там, где её действительно доводят до операции.</summary>
    [ObservableProperty]
    private bool _canCancel;

    [ObservableProperty]
    private bool _isCancelling;

    public string PercentText => IsIndeterminate ? "" : $"{Progress * 100:0}%";

    public string ElapsedText =>
        _watch.Elapsed.TotalSeconds < 1 ? "" : Loc.F("BusyElapsed", (int)_watch.Elapsed.TotalSeconds);

    /// <summary>
    /// Начало операции. Возвращает токен: его надо передать в саму работу,
    /// иначе кнопка «Прервать» будет обманом.
    /// </summary>
    public CancellationToken Start(string title, bool canCancel = true, bool determinate = false)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        Title = title;
        Detail = "";
        Progress = 0;
        IsIndeterminate = !determinate;
        CanCancel = canCancel;
        IsCancelling = false;
        IsRunning = true;
        _watch.Restart();
        _clock.Start();
        OnPropertyChanged(nameof(ElapsedText));
        return _cts.Token;
    }

    /// <summary>Отчёт о продвижении: доля и поясняющая строка.</summary>
    public void Report(double progress, string detail = "")
    {
        IsIndeterminate = false;
        Progress = Math.Clamp(progress, 0, 1);
        if (detail.Length > 0) Detail = detail;
    }

    public void Finish()
    {
        IsRunning = false;
        IsCancelling = false;
        _clock.Stop();
        _watch.Stop();
        _cts?.Dispose();
        _cts = null;
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_cts == null || IsCancelling) return;
        IsCancelling = true;
        Detail = Loc.Get("BusyCancelling");
        _cts.Cancel();
    }
}
