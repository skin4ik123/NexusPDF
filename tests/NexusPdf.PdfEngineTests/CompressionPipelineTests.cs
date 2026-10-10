using System.Text;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.MuPdf;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Пер-картиночный конвейер сжатия. Закрепляет жалобу «или мылит без
/// выигрыша, или не жмёт»: прежний движок молча не уменьшал изображения, и
/// весь «выигрыш» достигался затаптыванием качества.
/// </summary>
public sealed class CompressionPipelineTests
{
    private static readonly MuPdfCompressionEngine Engine = new();

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>JPEG из градиента с шумом: сжимаемый, но не вырождённый.</summary>
    private static byte[] MakeJpeg(int width, int height, long quality)
    {
        using var bitmap = new System.Drawing.Bitmap(width, height);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                   new System.Drawing.Rectangle(0, 0, width, height),
                   System.Drawing.Color.White, System.Drawing.Color.SteelBlue, 35f))
            g.FillRectangle(brush, 0, 0, width, height);
        var rnd = new Random(7);
        for (var i = 0; i < width * height / 40; i++)
            bitmap.SetPixel(rnd.Next(width), rnd.Next(height),
                System.Drawing.Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256)));

        var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        var parameters = new System.Drawing.Imaging.EncoderParameters(1);
        parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality, quality);
        using var stream = new MemoryStream();
        bitmap.Save(stream, codec, parameters);
        return stream.ToArray();
    }

    /// <summary>Одностраничный PDF A4 с JPEG на всю страницу (как делает сканер).</summary>
    private static string WriteScanPdf(string dir, byte[] jpeg, int width, int height)
    {
        var buffer = new MemoryStream();
        void Raw(string s) => buffer.Write(Encoding.ASCII.GetBytes(s));
        var offsets = new long[7];
        void Obj(int n, string body, byte[]? stream = null)
        {
            offsets[n] = buffer.Position;
            Raw($"{n} 0 obj\n{body}");
            if (stream != null)
            {
                Raw("\nstream\n");
                buffer.Write(stream);
                Raw("\nendstream");
            }
            Raw("\nendobj\n");
        }

        Raw("%PDF-1.5\n");
        buffer.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595.3 841.9] " +
               "/Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>");
        Obj(4, $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} " +
               $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>",
            jpeg);
        var content = Encoding.ASCII.GetBytes("q 595.3 0 0 841.9 0 0 cm /Im0 Do Q");
        Obj(5, $"<< /Length {content.Length} >>", content);
        var xref = buffer.Position;
        Raw("xref\n0 6\n0000000000 65535 f \n");
        for (var i = 1; i <= 5; i++) Raw($"{offsets[i]:0000000000} 00000 n \n");
        Raw($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        var path = Path.Combine(dir, "scan.pdf");
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }

    private static async Task<(int Width, string Filter, long FileLength)> FirstImageOf(string path)
    {
        // Движок рендера здесь не нужен — читаем тем же MuPDF.
        using var doc = new MuPDF.NET.Document(path);
        var entry = doc.GetPageImages(0, false)[0];
        return await Task.FromResult(((int)entry.Width, entry.Filter ?? "", new FileInfo(path).Length));
    }

    [Fact]
    public async Task A_Dense_Scan_Is_Downsampled_To_The_Target()
    {
        var dir = TempDir();
        // 2000 px на A4 ≈ 242 DPI — заметно плотнее цели 130.
        var source = WriteScanPdf(dir, MakeJpeg(2000, 2829, 88), 2000, 2829);
        var target = Path.Combine(dir, "out.pdf");

        var result = await Engine.CompressAsync(source, target,
            new PdfCompressionRequest(130, 68, StructureOnly: false, SubsetFonts: false),
            CancellationToken.None);

        Assert.False(result.KeptOriginal);
        Assert.Equal(1, result.Recompressed);
        Assert.True(result.BytesAfter < result.BytesBefore,
            $"файл не уменьшился: {result.BytesBefore} → {result.BytesAfter}");

        var (width, filter, _) = await FirstImageOf(target);
        Assert.Contains("DCT", filter);
        // 130 DPI на 595.3 пт — около 1075 px; допускаем округления.
        Assert.InRange(width, 900, 1200);
    }

    [Fact]
    public async Task A_Fitting_Jpeg_Can_Shrink_Without_Changing_Dimensions()
    {
        var dir = TempDir();
        // 900 px on A4 is below the target DPI: quality may change, dimensions must not.
        var jpeg = MakeJpeg(900, 1273, 85);
        var source = WriteScanPdf(dir, jpeg, 900, 1273);
        var target = Path.Combine(dir, "out.pdf");

        var result = await Engine.CompressAsync(source, target,
            new PdfCompressionRequest(130, 68, StructureOnly: false, SubsetFonts: false),
            CancellationToken.None);

        // Accept quality recompression only when the resulting file is smaller.
        Assert.True(result.BytesAfter <= result.BytesBefore);
        Assert.True(result.KeptOriginal || result.BytesAfter < result.BytesBefore * 0.985);
        var (width, _, _) = await FirstImageOf(result.KeptOriginal ? source : target);
        Assert.Equal(900, width);
    }

    [Fact]
    public async Task Structure_Only_Leaves_Images_Alone()
    {
        var dir = TempDir();
        var source = WriteScanPdf(dir, MakeJpeg(2000, 2829, 88), 2000, 2829);
        var target = Path.Combine(dir, "out.pdf");

        var result = await Engine.CompressAsync(source, target,
            new PdfCompressionRequest(130, 68, StructureOnly: true, SubsetFonts: false),
            CancellationToken.None);

        Assert.Equal(0, result.Recompressed);
        var (width, _, _) = await FirstImageOf(result.KeptOriginal ? source : target);
        Assert.Equal(2000, width);
    }
}
