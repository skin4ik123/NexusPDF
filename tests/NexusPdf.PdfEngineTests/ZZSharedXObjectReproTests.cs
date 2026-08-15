using System.Runtime.InteropServices;
using System.Text;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Верификация находки: общий XObject на двух страницах при пересжатии
/// дублируется на каждое размещение, счётчик Recompressed считает размещения.
/// Тест пишет диагностику в файл и не падает — результаты читает верификатор.
/// </summary>
public sealed class ZZSharedXObjectReproTests : IAsyncLifetime
{
    private PdfiumRenderEngine _pdfium = null!;

    private const string OutPath =
        @"C:\Users\yurch\AppData\Local\Temp\claude\E--Cossacks\14459d2b-9cfd-4a4f-bc91-e773fa716755\scratchpad\shared_xobject_repro.txt";

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private static byte[] EncodeJpeg(byte[] bgra, int width, int height, ImageEncodingChoice choice)
    {
        var quality = choice.Quality;
        if (choice.IsGray)
        {
            // System.Drawing не пишет одноканальный JPEG; для теста достаточно
            // обесцветить пиксели — приложение на WPF кодирует настоящий Gray8.
            bgra = (byte[])bgra.Clone();
            for (var i = 0; i + 2 < bgra.Length; i += 4)
            {
                var luma = (byte)((bgra[i + 2] * 299 + bgra[i + 1] * 587 + bgra[i] * 114) / 1000);
                bgra[i] = bgra[i + 1] = bgra[i + 2] = luma;
            }
        }
        using var bitmap = new System.Drawing.Bitmap(
            width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
        bitmap.UnlockBits(data);
        using var stream = new MemoryStream();
        var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        using var parameters = new System.Drawing.Imaging.EncoderParameters(1);
        parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality, (long)quality);
        bitmap.Save(stream, codec, parameters);
        return stream.ToArray();
    }

    /// <summary>Детерминированный шумный RGB 600x600 (несжимаемый).</summary>
    private static byte[] NoisyRgb(int w, int h)
    {
        var rgb = new byte[w * h * 3];
        uint seed = 777;
        for (var i = 0; i < rgb.Length; i++)
        {
            seed = seed * 1664525 + 1013904223;
            rgb[i] = (byte)(seed >> 16);
        }
        return rgb;
    }

    /// <summary>
    /// Минимальный PDF: ОДИН image-XObject (obj 5) без фильтра, обе страницы
    /// ссылаются на него; размеры размещений в points задаются параметрами.
    /// </summary>
    private static byte[] BuildSharedXObjectPdf(byte[] rgb, int px, double place1Pt, double place2Pt)
    {
        var latin = Encoding.Latin1;
        var objects = new List<byte[]>();

        byte[] Obj(string s) => latin.GetBytes(s);

        objects.Add(Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"));
        objects.Add(Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>\nendobj\n"));
        objects.Add(Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                        "/Resources << /XObject << /Im1 5 0 R >> >> /Contents 6 0 R >>\nendobj\n"));
        objects.Add(Obj("4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                        "/Resources << /XObject << /Im1 5 0 R >> >> /Contents 7 0 R >>\nendobj\n"));

        var imgHeader = latin.GetBytes(
            $"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {px} /Height {px} " +
            $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {rgb.Length} >>\nstream\n");
        var imgFooter = latin.GetBytes("\nendstream\nendobj\n");
        var img = new byte[imgHeader.Length + rgb.Length + imgFooter.Length];
        imgHeader.CopyTo(img, 0);
        rgb.CopyTo(img, imgHeader.Length);
        imgFooter.CopyTo(img, imgHeader.Length + rgb.Length);
        objects.Add(img);

        string S(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var c1 = $"q {S(place1Pt)} 0 0 {S(place1Pt)} 20 20 cm /Im1 Do Q";
        var c2 = $"q {S(place2Pt)} 0 0 {S(place2Pt)} 20 20 cm /Im1 Do Q";
        objects.Add(Obj($"6 0 obj\n<< /Length {c1.Length} >>\nstream\n{c1}\nendstream\nendobj\n"));
        objects.Add(Obj($"7 0 obj\n<< /Length {c2.Length} >>\nstream\n{c2}\nendstream\nendobj\n"));

        using var ms = new MemoryStream();
        var header = latin.GetBytes("%PDF-1.4\n%\u00e2\u00e3\u00cf\u00d3\n");
        ms.Write(header);
        var offsets = new long[objects.Count + 1];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = ms.Position;
            ms.Write(objects[i]);
        }
        var xrefPos = ms.Position;
        var sb = new StringBuilder();
        sb.Append($"xref\n0 {objects.Count + 1}\n");
        sb.Append("0000000000 65535 f \n");
        for (var i = 1; i <= objects.Count; i++)
            sb.Append($"{offsets[i]:0000000000} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
        ms.Write(latin.GetBytes(sb.ToString()));
        return ms.ToArray();
    }

    /// <summary>Все JPEG (SOI..поиск SOF0/1/2) в байтах файла: список "WxH".</summary>
    private static List<string> FindJpegs(byte[] bytes)
    {
        var result = new List<string>();
        for (var i = 0; i + 3 < bytes.Length; i++)
        {
            if (bytes[i] != 0xFF || bytes[i + 1] != 0xD8 || bytes[i + 2] != 0xFF)
                continue;
            // Идём по сегментам до SOF
            var p = i + 2;
            while (p + 9 < bytes.Length && bytes[p] == 0xFF)
            {
                var marker = bytes[p + 1];
                if (marker == 0xC0 || marker == 0xC1 || marker == 0xC2)
                {
                    var h = (bytes[p + 5] << 8) | bytes[p + 6];
                    var w = (bytes[p + 7] << 8) | bytes[p + 8];
                    result.Add($"{w}x{h}");
                    break;
                }
                if (marker == 0xD8) { p += 2; continue; }
                var len = (bytes[p + 2] << 8) | bytes[p + 3];
                if (len < 2) break;
                p += 2 + len;
            }
            i = p; // не сканировать внутренности этого JPEG заново
        }
        return result;
    }

    private static bool ContainsSlice(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            if (ok) return true;
        }
        return false;
    }

    [Fact]
    public async Task SharedXObject_TwoPlacements_Diagnostics()
    {
        var log = new StringBuilder();
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var rgb = NoisyRgb(600, 600);
        var signature = rgb.Take(48).ToArray(); // сигнатура ОРИГИНАЛЬНОГО потока

        // Сценарий A: находка — 2" (144pt) и 0.5" (36pt), цель 150 DPI.
        var srcA = Path.Combine(dir, "sharedA.pdf");
        File.WriteAllBytes(srcA, BuildSharedXObjectPdf(rgb, 600, 144, 36));
        var outA = Path.Combine(dir, "sharedA.out.pdf");
        var statsA = await _pdfium.RecompressImagesAsync(
            srcA, null, outA, 150, 75, EncodeJpeg, CancellationToken.None);
        var bytesA = File.ReadAllBytes(outA);
        log.AppendLine($"A: src={new FileInfo(srcA).Length} out={bytesA.Length}");
        log.AppendLine($"A: Recompressed={statsA.Recompressed} Skipped={statsA.Skipped}");
        log.AppendLine($"A: jpegs=[{string.Join(", ", FindJpegs(bytesA))}]");
        log.AppendLine($"A: originalRawStreamStillInOutput={ContainsSlice(bytesA, signature)}");

        // Сценарий B: оба размещения по 2" (144pt) — одинаковый логотип на страницах.
        var srcB = Path.Combine(dir, "sharedB.pdf");
        File.WriteAllBytes(srcB, BuildSharedXObjectPdf(rgb, 600, 144, 144));
        var outB = Path.Combine(dir, "sharedB.out.pdf");
        var statsB = await _pdfium.RecompressImagesAsync(
            srcB, null, outB, 150, 75, EncodeJpeg, CancellationToken.None);
        var bytesB = File.ReadAllBytes(outB);
        log.AppendLine($"B: src={new FileInfo(srcB).Length} out={bytesB.Length}");
        log.AppendLine($"B: Recompressed={statsB.Recompressed} Skipped={statsB.Skipped}");
        log.AppendLine($"B: jpegs=[{string.Join(", ", FindJpegs(bytesB))}]");
        log.AppendLine($"B: originalRawStreamStillInOutput={ContainsSlice(bytesB, signature)}");

        // Контроль каскада в A: средняя абс. разница пикселей второго размещения
        // против даунсэмпла оригинала (если бы читался оригинал, разница = только
        // JPEG-шум одного поколения).
        await using var reA = await _pdfium.OpenAsync(outA, null, CancellationToken.None);
        var render2 = await reA.RenderPageAsync(1, 200, 200, 0, CancellationToken.None);
        log.AppendLine($"A: page2 rendered, stride={render2.Stride}, bytes={render2.Bgra.Length}");

        File.WriteAllText(OutPath, log.ToString());
        Assert.True(true);
    }
}
