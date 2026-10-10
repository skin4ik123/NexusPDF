using System.IO;
using NexusPdf.App.Desktop.Services;
using NexusPdf.Pdf.Pdfium;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        const int width = 600, height = 800;
        var pixels = new byte[width * height * 4];
        var random = new Random(42);
        random.NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var jpeg = ImageEncoder.EncodeJpeg(pixels, width, height, 10);
        var directory = Path.Combine(Path.GetTempPath(), "nexuspdf-desktop-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "photo.jpg");
            File.WriteAllBytes(source, jpeg);
            var loaded = ImageLoader.FromFile(source);
            var page = ImageEncoder.DecodeAsPageSpec(source);
            if (loaded.EncodedJpeg == null || !loaded.EncodedJpeg.SequenceEqual(jpeg) ||
                page.EncodedJpeg == null || !page.EncodedJpeg.SequenceEqual(jpeg))
                throw new InvalidOperationException("A desktop image loader discarded the original JPEG stream.");
            var target = Path.Combine(directory, "imported.pdf");
            using var engine = new EngineLifetime();
            engine.Value.CreateImageDocumentAsync(new[] { page }, target, CancellationToken.None)
                .GetAwaiter().GetResult();
            var length = new FileInfo(target).Length;
            if (length > jpeg.Length + 4096)
                throw new InvalidOperationException($"Desktop import inflated {jpeg.Length} bytes to {length}.");
            Console.WriteLine($"Desktop WPF import: JPEG {jpeg.Length} bytes -> PDF {length} bytes; both loaders preserve the stream.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class EngineLifetime : IDisposable
    {
        public PdfiumRenderEngine Value { get; } = new();
        public void Dispose() => Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
