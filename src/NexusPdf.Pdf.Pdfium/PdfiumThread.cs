using System.Collections.Concurrent;
using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

/// <summary>
/// PDFium не потокобезопасен: все вызовы библиотеки сериализуются на одном
/// выделенном потоке. Инициализация и освобождение библиотеки привязаны к
/// жизненному циклу этого потока.
/// </summary>
internal sealed class PdfiumThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public PdfiumThread()
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
        _queue.CompleteAdding();
        if (!_thread.Join(TimeSpan.FromSeconds(10)))
        {
            // Поток занят затянувшейся нативной операцией; при выходе процесса он
            // будет остановлен как фоновый.
        }
        _queue.Dispose();
    }
}
