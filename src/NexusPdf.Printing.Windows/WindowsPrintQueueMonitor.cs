using System.Printing;

namespace NexusPdf.Printing.Windows;

/// <summary>
/// Очередь печати Windows: что там сейчас с заданиями и как ими управлять.
///
/// Программа показывает СВОИ задания (те, что сама отправила) и умеет их
/// приостановить, продолжить и отменить. Чужие задания того же принтера
/// видны, но не трогаются: закрыть чужую печать по ошибке — худшее, что может
/// сделать программа с общим офисным принтером.
/// </summary>
public sealed class WindowsPrintQueueMonitor : IDisposable
{
    private readonly PrintServer _server = new();
    private bool _disposed;

    /// <summary>Задания принтера. Свои узнаются по номерам, выданным при отправке.</summary>
    public IReadOnlyList<PrintJobSnapshot> Read(string printerName, IReadOnlySet<int> ownJobIds)
    {
        if (_disposed) return Array.Empty<PrintJobSnapshot>();
        try
        {
            using var queue = new PrintQueue(_server, printerName);
            var jobs = new List<PrintJobSnapshot>();
            foreach (var job in queue.GetPrintJobInfoCollection())
            {
                using (job)
                {
                    jobs.Add(new PrintJobSnapshot(
                        job.JobIdentifier,
                        printerName,
                        job.Name ?? "",
                        PrintJobStateMapper.Map((WindowsJobStatus)(int)job.JobStatus),
                        job.NumberOfPagesPrinted,
                        job.NumberOfPages,
                        ownJobIds.Contains(job.JobIdentifier)));
                }
            }
            return jobs;
        }
        catch (Exception)
        {
            // Очередь могла исчезнуть вместе с принтером: пустой список честнее
            // исключения — окно продолжает работать.
            return Array.Empty<PrintJobSnapshot>();
        }
    }

    /// <summary>Отменить задание. Возвращает false, если очередь уже его не знает.</summary>
    public bool Cancel(string printerName, int jobId) => Act(printerName, jobId, job => job.Cancel());

    public bool Pause(string printerName, int jobId) => Act(printerName, jobId, job => job.Pause());

    public bool Resume(string printerName, int jobId) => Act(printerName, jobId, job => job.Resume());

    private bool Act(string printerName, int jobId, Action<PrintSystemJobInfo> action)
    {
        if (_disposed) return false;
        try
        {
            using var queue = new PrintQueue(_server, printerName);
            using var job = queue.GetJob(jobId);
            action(job);
            job.Commit();
            return true;
        }
        catch (Exception)
        {
            // Задание могло закончиться само между списком и нажатием кнопки —
            // это не ошибка, о которой стоит кричать окном.
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _server.Dispose();
    }
}
