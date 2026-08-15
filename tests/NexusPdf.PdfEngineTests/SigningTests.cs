using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NexusPdf.Application;
using NexusPdf.Pdf.Pdfium;
using NexusPdf.Pdf.Qpdf;
using NexusPdf.Signing;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Цифровые подписи: подписание self-signed сертификатом через полный конвейер
/// (компоновка → qpdf QDF → инкрементальная подпись) и проверка инспектором.
/// Пропускаются, если qpdf не установлен.
/// </summary>
public sealed class SigningTests : IAsyncLifetime
{
    private readonly QpdfEngine _qpdf = new();
    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    private static X509Certificate2 MakeSelfSigned(string name = "CN=Тест Подписант")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: false));
        return request.CreateSelfSigned(
            DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
    }

    [Fact]
    public async Task Sign_Then_Inspect_Reports_Valid_Untrusted_Covering()
    {
        if (!_qpdf.IsAvailable) return;
        // Контент фикстуры — латиница (генератор пишет ASCII; кириллический
        // КОНТЕНТ покрыт оверлей-тестами с настоящим TTF).
        var path = PdfFixture.WriteToTemp("tosign.pdf",
            new PdfFixture.PageSpec(612, 792, Text: "Rental Agreement"));

        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            using var certificate = MakeSelfSigned();
            var target = Path.Combine(Path.GetDirectoryName(path)!, "signed.pdf");
            var tools = new DocumentToolsService(_pdfium, _qpdf, _qpdf);
            await tools.SignCopyAsync(document, target, certificate,
                "Согласовано", "Киев", CancellationToken.None);

            var signatures = await PdfSignatureInspector.InspectAsync(target, CancellationToken.None);
            var signature = Assert.Single(signatures);
            Assert.True(signature.IsCryptoValid, signature.Error);
            Assert.True(signature.CoversWholeDocument);
            Assert.False(signature.IsTrusted); // self-signed — цепочка недоверенная
            Assert.Equal("Тест Подписант", signature.SignerName);
            Assert.Equal("Согласовано", signature.Reason);
            Assert.NotNull(signature.SignTime);

            // Подписанный файл остаётся валидным PDF с исходным содержимым.
            await using var reopened = await _pdfium.OpenAsync(target, null, CancellationToken.None);
            Assert.Equal(1, reopened.Info.PageCount);
            Assert.Contains("Rental", await reopened.GetPageTextAsync(0, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Tampered_Byte_Invalidates_Signature()
    {
        if (!_qpdf.IsAvailable) return;
        var path = PdfFixture.WriteToTemp("tamper.pdf", new PdfFixture.PageSpec(612, 792));
        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        string target;
        await using (document)
        {
            using var certificate = MakeSelfSigned();
            target = Path.Combine(Path.GetDirectoryName(path)!, "tampered.pdf");
            await new DocumentToolsService(_pdfium, _qpdf, _qpdf)
                .SignCopyAsync(document, target, certificate, "", "", CancellationToken.None);
        }

        // Портим один байт содержимого (в начале файла, внутри ByteRange).
        var bytes = await File.ReadAllBytesAsync(target);
        bytes[64] ^= 0xFF;
        await File.WriteAllBytesAsync(target, bytes);

        var signature = Assert.Single(PdfSignatureInspector.Inspect(bytes));
        Assert.False(signature.IsCryptoValid);
    }

    [Fact]
    public async Task Appended_Data_Is_Reported_As_Modified_After_Signing()
    {
        if (!_qpdf.IsAvailable) return;
        var path = PdfFixture.WriteToTemp("append.pdf", new PdfFixture.PageSpec(612, 792));
        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        string target;
        await using (document)
        {
            using var certificate = MakeSelfSigned();
            target = Path.Combine(Path.GetDirectoryName(path)!, "appended.pdf");
            await new DocumentToolsService(_pdfium, _qpdf, _qpdf)
                .SignCopyAsync(document, target, certificate, "", "", CancellationToken.None);
        }

        // Инкрементальное дополнение после подписи: криптография цела,
        // но подпись больше не покрывает весь файл.
        var bytes = (await File.ReadAllBytesAsync(target))
            .Concat("\n% добавлено после подписи\n"u8.ToArray()).ToArray();

        var signature = Assert.Single(PdfSignatureInspector.Inspect(bytes));
        Assert.True(signature.IsCryptoValid, signature.Error);
        Assert.False(signature.CoversWholeDocument);
    }

    [Fact]
    public async Task Visible_Stamp_Is_Baked_And_Covered_By_Signature()
    {
        if (!_qpdf.IsAvailable) return;
        var path = PdfFixture.WriteToTemp("stamped.pdf",
            new PdfFixture.PageSpec(612, 792, Text: "Contract body"));
        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            using var certificate = MakeSelfSigned();
            var target = Path.Combine(Path.GetDirectoryName(path)!, "stamped-signed.pdf");
            await new DocumentToolsService(_pdfium, _qpdf, _qpdf).SignCopyAsync(
                document, target, certificate, "Согласовано", "",
                visibleStamp: true, CancellationToken.None);

            var signature = Assert.Single(
                await PdfSignatureInspector.InspectAsync(target, CancellationToken.None));
            Assert.True(signature.IsCryptoValid, signature.Error);
            Assert.True(signature.CoversWholeDocument);

            // Отметка запечена В СТРАНИЦУ (текст ищется) и покрыта подписью.
            await using var reopened = await _pdfium.OpenAsync(target, null, CancellationToken.None);
            var text = await reopened.GetPageTextAsync(0, CancellationToken.None);
            Assert.Contains("Подписано:", text);
            Assert.Contains("Тест Подписант", text);
            Assert.Contains("Согласовано", text);
            Assert.Contains("Contract body", text);
        }
    }

    [Fact]
    public async Task Widened_ByteRange_Hole_Attack_Is_Rejected()
    {
        if (!_qpdf.IsAvailable) return;
        var path = PdfFixture.WriteToTemp("holeattack.pdf", new PdfFixture.PageSpec(612, 792));
        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        string target;
        await using (document)
        {
            using var certificate = MakeSelfSigned();
            target = Path.Combine(Path.GetDirectoryName(path)!, "holeattacked.pdf");
            await new DocumentToolsService(_pdfium, _qpdf, _qpdf)
                .SignCopyAsync(document, target, certificate, "", "", CancellationToken.None);
        }

        // Атака: закрывающая «>» /Contents передвигается внутрь дыры ByteRange
        // (нулевой заполнитель это позволяет — инспектор обрезает хвостовые
        // нули), а освободившиеся байты дыры становятся НЕподписанными.
        // Криптография и границы ByteRange при этом не меняются.
        var bytes = await File.ReadAllBytesAsync(target);
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        var open = text.LastIndexOf("/Contents <", StringComparison.Ordinal) + "/Contents ".Length;
        var close = open + 1 + 16384 * 2;
        Assert.Equal((byte)'>', bytes[close]); // ориентация в файле верна
        bytes[close] = (byte)'A';              // мусор в конце дыры
        bytes[close - 4] = (byte)'>';          // ранняя закрывающая скобка

        var signature = Assert.Single(PdfSignatureInspector.Inspect(bytes));
        Assert.False(signature.IsCryptoValid && signature.CoversWholeDocument,
            "Файл с неподписанными байтами в дыре ByteRange не должен считаться валидным.");
    }

    [Fact]
    public async Task Signing_Twice_Is_Refused_By_Service()
    {
        if (!_qpdf.IsAvailable) return;
        var path = PdfFixture.WriteToTemp("twice.pdf", new PdfFixture.PageSpec(612, 792));
        var tools = new DocumentToolsService(_pdfium, _qpdf, _qpdf);
        using var certificate = MakeSelfSigned();

        var signedOnce = Path.Combine(Path.GetDirectoryName(path)!, "signed-once.pdf");
        var first = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (first)
        {
            await tools.SignCopyAsync(first, signedOnce, certificate, "", "", CancellationToken.None);
        }

        // Повторное подписание пересобрало бы файл и разрушило первую подпись —
        // сервис обязан отказать сам, не полагаясь на проверку в UI.
        var reopened = await OpenedDocument.OpenAsync(_pdfium, signedOnce, null, CancellationToken.None);
        await using (reopened)
        {
            var again = Path.Combine(Path.GetDirectoryName(path)!, "signed-twice.pdf");
            var ex = await Assert.ThrowsAsync<NexusPdf.Pdf.Abstractions.PdfEngineException>(() =>
                tools.SignCopyAsync(reopened, again, certificate, "", "", CancellationToken.None));
            Assert.Contains("уже содержит", ex.Message);
        }
    }

    [Fact]
    public async Task Indirect_Annots_Array_Is_Amended_Not_Corrupted()
    {
        if (!_qpdf.IsAvailable) return;
        // Страница ссылается на /Annots как на КОСВЕННЫЙ объект-массив —
        // частый вывод генераторов; ссылка подписи обязана попасть в сам
        // массив, а не в первую «[» следующего ключа (например /MediaBox).
        var raw = "%PDF-1.4\n" +
                  "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
                  "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
                  "3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots 4 0 R /MediaBox [0 0 612 792] /Resources << >> >>\nendobj\n" +
                  "4 0 obj\n[ ]\nendobj\n" +
                  "trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n0\n%%EOF\n";
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var rawPath = Path.Combine(dir, "indirect-annots.pdf");
        await File.WriteAllBytesAsync(rawPath, System.Text.Encoding.Latin1.GetBytes(raw));

        var normalized = Path.Combine(dir, "indirect-annots.qdf.pdf");
        await _qpdf.NormalizeAsync(rawPath, normalized, CancellationToken.None);
        var normalizedText = await File.ReadAllTextAsync(normalized);
        Assert.Matches(@"/Annots\s+\d+\s+0\s+R", normalizedText); // qpdf сохранил косвенность

        using var certificate = MakeSelfSigned();
        var signed = Path.Combine(dir, "indirect-annots-signed.pdf");
        PdfIncrementalSigner.Sign(normalized, signed, certificate, "", "");

        var signedText = await File.ReadAllTextAsync(signed);
        Assert.DoesNotMatch(@"/MediaBox\s*\[\s*\d+\s+0\s+R", signedText); // геометрия цела
        var signature = Assert.Single(
            await PdfSignatureInspector.InspectAsync(signed, CancellationToken.None));
        Assert.True(signature.IsCryptoValid, signature.Error);
        Assert.True(signature.CoversWholeDocument);

        await using var handle = await _pdfium.OpenAsync(signed, null, CancellationToken.None);
        Assert.Equal(1, handle.Info.PageCount);
    }

    [Fact]
    public async Task Catalog_String_With_Dict_Markers_Survives_Signing()
    {
        if (!_qpdf.IsAvailable) return;
        // «>>» внутри литеральной строки каталога (JavaScript в /OpenAction)
        // не должно преждевременно «закрывать» словарь при разборе.
        var raw = "%PDF-1.4\n" +
                  "1 0 obj\n<< /Type /Catalog /Pages 2 0 R " +
                  "/OpenAction << /S /JavaScript /JS (var x = a >> 2;) >> >>\nendobj\n" +
                  "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
                  "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>\nendobj\n" +
                  "trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n0\n%%EOF\n";
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var rawPath = Path.Combine(dir, "js-catalog.pdf");
        await File.WriteAllBytesAsync(rawPath, System.Text.Encoding.Latin1.GetBytes(raw));
        var normalized = Path.Combine(dir, "js-catalog.qdf.pdf");
        await _qpdf.NormalizeAsync(rawPath, normalized, CancellationToken.None);

        using var certificate = MakeSelfSigned();
        var signed = Path.Combine(dir, "js-catalog-signed.pdf");
        PdfIncrementalSigner.Sign(normalized, signed, certificate, "", "");

        // Переопределённый каталог в инкременте несёт и строку, и /AcroForm.
        var signedText = await File.ReadAllTextAsync(signed);
        var increment = signedText[signedText.IndexOf("%%EOF", StringComparison.Ordinal)..];
        if (increment.Contains("/OpenAction", StringComparison.Ordinal))
        {
            Assert.Contains("var x = a >> 2;", increment);
            Assert.Contains("/AcroForm", increment);
        }
        var signature = Assert.Single(
            await PdfSignatureInspector.InspectAsync(signed, CancellationToken.None));
        Assert.True(signature.IsCryptoValid, signature.Error);
        Assert.True(signature.CoversWholeDocument);
    }

    [Fact]
    public async Task Object_Header_Inside_Stream_Data_Is_Ignored()
    {
        if (!_qpdf.IsAvailable) return;
        // Данные потока содержат строку «2 0 obj» с поддельным словарём
        // (так выглядит вложенный PDF во /EmbeddedFiles после QDF-распаковки).
        // Разбор обязан пропустить её и найти настоящий объект 2 дальше.
        var decoy = "2 0 obj\n<< /Type /Page >>\nendobj\n";
        var raw = "%PDF-1.4\n" +
                  $"4 0 obj\n<< /Length {decoy.Length} >>\nstream\n{decoy}endstream\nendobj\n" +
                  "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
                  "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
                  "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>\nendobj\n" +
                  "trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n0\n%%EOF\n";
        var dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var rawPath = Path.Combine(dir, "stream-decoy.pdf");
        await File.WriteAllBytesAsync(rawPath, System.Text.Encoding.Latin1.GetBytes(raw));

        using var certificate = MakeSelfSigned();
        var signed = Path.Combine(dir, "stream-decoy-signed.pdf");
        PdfIncrementalSigner.Sign(rawPath, signed, certificate, "", "");

        // Инкремент дополняет /Annots настоящей страницы (объект 3), а не
        // переписывает узел /Pages телом подделки из данных потока.
        var signedText = await File.ReadAllTextAsync(signed);
        var increment = signedText[signedText.IndexOf("%%EOF", StringComparison.Ordinal)..];
        Assert.DoesNotContain("\n2 0 obj", increment);
        var signature = Assert.Single(
            await PdfSignatureInspector.InspectAsync(signed, CancellationToken.None));
        Assert.True(signature.IsCryptoValid, signature.Error);
        Assert.True(signature.CoversWholeDocument);
    }

    [Fact]
    public async Task Signing_Form_Document_Preserves_Existing_AcroForm()
    {
        if (!_qpdf.IsAvailable) return;
        var path = PdfFixture.WriteTextFieldToTemp("signform.pdf", "fio");
        var document = await OpenedDocument.OpenAsync(_pdfium, path, null, CancellationToken.None);
        await using (document)
        {
            using var certificate = MakeSelfSigned();
            var target = Path.Combine(Path.GetDirectoryName(path)!, "signedform.pdf");
            await new DocumentToolsService(_pdfium, _qpdf, _qpdf)
                .SignCopyAsync(document, target, certificate, "", "", CancellationToken.None);

            var signature = Assert.Single(
                await PdfSignatureInspector.InspectAsync(target, CancellationToken.None));
            Assert.True(signature.IsCryptoValid, signature.Error);

            // Исходное текстовое поле не потеряно.
            await using var reopened = await _pdfium.OpenAsync(target, null, CancellationToken.None);
            Assert.Equal(1, await reopened.GetFormTypeAsync(CancellationToken.None));
        }
    }
}
