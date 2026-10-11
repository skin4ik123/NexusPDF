using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Ocr;

/// <summary>Release a replaced recognizer after its current operations finish.</summary>
public sealed class RetainedTextRecognizer(ITextRecognizer inner) : ITextRecognizer
{
    private readonly object _gate = new();
    private int _users;
    private bool _retired;
    private bool _disposed;

    public string Id => inner.Id;
    public string DisplayName => inner.DisplayName;
    public bool ReturnsWholeLines => inner.ReturnsWholeLines;
    public bool IsAvailable { get { lock (_gate) return !_disposed && inner.IsAvailable; } }
    public string? UnavailableReason => inner.UnavailableReason;

    public IDisposable Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _users++;
            return new Lease(this);
        }
    }

    public async Task<OcrPageResult> RecognizeAsync(RenderedPageImage image, int renderDpi, CancellationToken ct)
    {
        using var lease = Acquire();
        return await inner.RecognizeAsync(image, renderDpi, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        var release = false;
        lock (_gate)
        {
            _retired = true;
            if (_users == 0 && !_disposed) { _disposed = true; release = true; }
        }
        if (release) inner.Dispose();
    }

    private void Release()
    {
        var release = false;
        lock (_gate)
        {
            if (--_users == 0 && _retired && !_disposed) { _disposed = true; release = true; }
        }
        if (release) inner.Dispose();
    }

    private sealed class Lease(RetainedTextRecognizer owner) : IDisposable
    {
        private RetainedTextRecognizer? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
