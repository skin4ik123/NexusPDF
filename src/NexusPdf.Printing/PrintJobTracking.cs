namespace NexusPdf.Printing;

/// <summary>
/// Состояние задания в очереди — словами, а не флагами драйвера.
///
/// Windows отдаёт набор флагов, которые могут стоять одновременно
/// (например, «печатается» и «в очереди»), и часть из них означает не
/// состояние, а беду: кончилась бумага, замятие, принтер отключён. Здесь они
/// сведены к одному ответу на вопрос «что сейчас с моим заданием» — по
/// приоритету: сперва беда, потом остановка, потом обычный ход.
/// </summary>
public enum PrintJobState
{
    /// <summary>Ждёт очереди.</summary>
    Queued,

    /// <summary>Передаётся на принтер.</summary>
    Spooling,

    /// <summary>Печатается.</summary>
    Printing,

    /// <summary>Приостановлено пользователем или очередью.</summary>
    Paused,

    /// <summary>Требует вмешательства: бумага, замятие, отключён.</summary>
    NeedsAttention,

    /// <summary>Ошибка задания.</summary>
    Error,

    /// <summary>Удаляется или удалено.</summary>
    Cancelled,

    /// <summary>Напечатано.</summary>
    Completed,
}

/// <summary>Флаги задания Windows (JOB_STATUS_*), нужные для разбора состояния.</summary>
[Flags]
public enum WindowsJobStatus
{
    None = 0,
    Paused = 0x00000001,
    Error = 0x00000002,
    Deleting = 0x00000004,
    Spooling = 0x00000008,
    Printing = 0x00000010,
    Offline = 0x00000020,
    PaperOut = 0x00000040,
    Printed = 0x00000080,
    Deleted = 0x00000100,
    BlockedDevQ = 0x00000200,
    UserIntervention = 0x00000400,
    Restart = 0x00000800,
    Complete = 0x00001000,
    Retained = 0x00002000,
}

/// <summary>Снимок задания: то, что показывается пользователю в очереди.</summary>
/// <param name="JobId">Номер задания в очереди Windows.</param>
/// <param name="PrinterName">Принтер, которому задание отдано.</param>
/// <param name="Name">Имя задания (что печатается).</param>
/// <param name="State">Состояние одним словом.</param>
/// <param name="PagesPrinted">Сколько страниц уже напечатано.</param>
/// <param name="PagesTotal">Сколько всего страниц в задании (0 — неизвестно).</param>
/// <param name="IsOurs">Задание отправлено этой программой.</param>
public sealed record PrintJobSnapshot(
    int JobId, string PrinterName, string Name, PrintJobState State,
    int PagesPrinted, int PagesTotal, bool IsOurs)
{
    /// <summary>Задание ещё живёт в очереди: его можно приостановить или отменить.</summary>
    public bool IsActive => State is not (PrintJobState.Completed or PrintJobState.Cancelled);
}

/// <summary>Разбор флагов задания. Вынесен из Windows-слоя, чтобы быть проверяемым.</summary>
public static class PrintJobStateMapper
{
    /// <summary>
    /// Состояние по флагам. Порядок проверок — это и есть правило: человеку
    /// важнее узнать «кончилась бумага», чем «печатается», хотя флаги стоят оба.
    /// </summary>
    public static PrintJobState Map(WindowsJobStatus status)
    {
        if (status.HasFlag(WindowsJobStatus.Deleted) || status.HasFlag(WindowsJobStatus.Deleting))
            return PrintJobState.Cancelled;
        if (status.HasFlag(WindowsJobStatus.PaperOut) ||
            status.HasFlag(WindowsJobStatus.UserIntervention) ||
            status.HasFlag(WindowsJobStatus.Offline) ||
            status.HasFlag(WindowsJobStatus.BlockedDevQ))
            return PrintJobState.NeedsAttention;
        if (status.HasFlag(WindowsJobStatus.Error))
            return PrintJobState.Error;
        if (status.HasFlag(WindowsJobStatus.Paused))
            return PrintJobState.Paused;
        if (status.HasFlag(WindowsJobStatus.Printed) || status.HasFlag(WindowsJobStatus.Complete))
            return PrintJobState.Completed;
        if (status.HasFlag(WindowsJobStatus.Printing))
            return PrintJobState.Printing;
        if (status.HasFlag(WindowsJobStatus.Spooling))
            return PrintJobState.Spooling;
        return PrintJobState.Queued;
    }

    /// <summary>Ключ строки для состояния: тексты живут в словарях интерфейса.</summary>
    public static string TitleKey(PrintJobState state) => "PrintJobState_" + state;
}
