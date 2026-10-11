using NexusPdf.Ocr;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.UnitTests;

public sealed class RecognizerLifetimeTests
{
    private sealed class Recognizer : ITextRecognizer
    {
        public string Id => "fake";
        public string DisplayName => "Fake";
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public bool ReturnsWholeLines => false;
        public int Disposals;
        public TaskCompletionSource<OcrPageResult> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<OcrPageResult> RecognizeAsync(RenderedPageImage image, int dpi, CancellationToken ct) => Result.Task;
        public void Dispose() => Disposals++;
    }

    [Fact]
    public async Task Switching_Ocr_Does_Not_Dispose_An_Active_Page()
    {
        var engine = new Recognizer();
        var retained = new RetainedTextRecognizer(engine);
        var pending = retained.RecognizeAsync(null!, 300, CancellationToken.None);
        retained.Dispose();
        Assert.Equal(0, engine.Disposals);
        engine.Result.SetResult(new OcrPageResult(Array.Empty<OcrWord>(), 0));
        await pending;
        Assert.Equal(1, engine.Disposals);
        Assert.False(retained.IsAvailable);
    }

    [Fact]
    public async Task A_Multi_Page_Operation_Keeps_Its_Engine_Across_A_Settings_Change()
    {
        var engine = new Recognizer();
        engine.Result.SetResult(new OcrPageResult(Array.Empty<OcrWord>(), 0));
        var retained = new RetainedTextRecognizer(engine);
        var operation = retained.Acquire();
        await retained.RecognizeAsync(null!, 300, CancellationToken.None);
        retained.Dispose();
        await retained.RecognizeAsync(null!, 300, CancellationToken.None);
        Assert.Equal(0, engine.Disposals);
        operation.Dispose();
        operation.Dispose();
        retained.Dispose();
        Assert.Equal(1, engine.Disposals);
        Assert.Throws<ObjectDisposedException>(() => retained.Acquire());
    }
}
