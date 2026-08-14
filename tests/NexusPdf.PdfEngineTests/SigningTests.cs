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
