using System.Collections.Concurrent;
using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

/// <summary>
/// PDFium не потокобезопасен, а его инициализация глобальна для процесса:
/// все вызовы библиотеки сериализуются на одном общем выделенном потоке.
/// Поток создаётся один на процесс и живёт до его завершения — повторные
/// FPDF_InitLibrary/FPDF_DestroyLibrary из разных экземпляров движка
/// приводили бы к порче глобального состояния.
/// </summary>
internal sealed class PdfiumThread : IDisposable
{
    private static readonly Lazy<PdfiumThread> SharedInstance =
        new(() => new PdfiumThread(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Общий поток PDFium для всего процесса.</summary>
    public static PdfiumThread Shared => SharedInstance.Value;

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    private PdfiumThread()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "PdfiumEngine",
        };
        _thread.Start();
    }

    private void Run()
    {
        fpdfview.FPDF_InitLibrary();
        try
        {
            foreach (var action in _queue.GetConsumingEnumerable())
                action();
        }
        finally
        {
            fpdfview.FPDF_DestroyLibrary();
        }
    }

    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.IsCancellationRequested)
        {
            tcs.TrySetCanceled(ct);
            return tcs.Task;
        }

        try
        {
            _queue.Add(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(ct);
                    return;
                }
                try
                {
                    tcs.TrySetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
        }
        catch (InvalidOperationException)
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(PdfiumThread)));
        }
        return tcs.Task;
    }

    public Task InvokeAsync(Action action, CancellationToken ct) =>
        InvokeAsync(() =>
        {
            action();
            return true;
        }, ct);

    public void Dispose()
    {
        // Общий поток процесса не останавливается: он фоновый и завершится
        // вместе с процессом. Явная остановка ломала бы других пользователей
        // PDFium в том же процессе (например, параллельные тесты).
    }
}
