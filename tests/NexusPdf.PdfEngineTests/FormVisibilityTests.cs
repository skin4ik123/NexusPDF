using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Жалобы с реальной формы: фокус не переходил в следующее поле кликом, а
/// заполненные значения были видны только при включённом режиме форм и
/// «пропадали» при его выключении. Оба сценария закреплены здесь на уровне
/// движка: клик — перенос фокуса — ввод — выход из режима — обычный рендер.
/// </summary>
public sealed class FormVisibilityTests : IAsyncLifetime
{
    private PdfiumRenderEngine _engine = null!;

    public Task InitializeAsync()
    {
        _engine = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _engine.DisposeAsync();

    // Центры полей фикстуры в отображаемых координатах (y сверху, страница 612x792):
    // text1 [100 700..740 400] → (250, 792-720=72); text2 → (250, 172); чекбокс → (115, 277).
    private const double Text1X = 250, Text1Y = 72;
    private const double Text2X = 250, Text2Y = 172;
    private const double CheckX = 115, CheckY = 277;

    // Текст в поле прижат к левому краю — чернила ищутся там, а не в центре.
    private const double Text1InkX = 130;

    [Fact]
    public async Task A_Click_Moves_Focus_To_The_Next_Field()
    {
        var path = PdfFixture.WriteFormTrioToTemp("refocus.pdf");
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);
        Assert.True(await doc.InitFormsAsync(CancellationToken.None));

        await doc.FormClickAsync(0, 0, Text1X, Text1Y, CancellationToken.None);
        foreach (var c in "AB")
            await doc.FormCharAsync(c, CancellationToken.None);

        // Переход в другое поле — просто кликом, без KillFocus между ними:
        // ровно так делает мышь.
        await doc.FormClickAsync(0, 0, Text2X, Text2Y, CancellationToken.None);
        foreach (var c in "CD")
            await doc.FormCharAsync(c, CancellationToken.None);

        await doc.FormKillFocusAsync(CancellationToken.None);

        var widgets = (await doc.GetAnnotationsAsync(0, CancellationToken.None))
            .Where(a => a.Subtype == 20).ToList();
        Assert.Equal("AB", Assert.Single(widgets, w => w.Author == "text1").Value);
        Assert.Equal("CD", Assert.Single(widgets, w => w.Author == "text2").Value);
    }

    [Fact]
    public async Task Values_Stay_Visible_After_Leaving_Form_Mode()
    {
        var path = PdfFixture.WriteFormTrioToTemp("visible.pdf");
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);
        Assert.True(await doc.InitFormsAsync(CancellationToken.None));

        await doc.FormClickAsync(0, 0, Text1X, Text1Y, CancellationToken.None);
        foreach (var c in "XYZW")
            await doc.FormCharAsync(c, CancellationToken.None);
        await doc.FormClickAsync(0, 0, CheckX, CheckY, CancellationToken.None); // галка

        // Ровно то, что делает выключение режима форм в программе.
        await doc.FormEndAsync(CancellationToken.None);

        var image = await Render(doc);
        Assert.True(InkInField(image, Text1InkX, Text1Y),
            "текст поля не виден в обычном рендере после выхода из режима форм");
        Assert.True(InkInField(image, CheckX, CheckY),
            "галка чекбокса не видна в обычном рендере после выхода из режима форм");
    }

    [Fact]
    public async Task Values_Stay_Visible_In_A_Saved_Copy()
    {
        var path = PdfFixture.WriteFormTrioToTemp("saved.pdf");
        var target = Path.Combine(Path.GetDirectoryName(path)!, "saved-out.pdf");
        await using (var doc = await _engine.OpenAsync(path, null, CancellationToken.None))
        {
            Assert.True(await doc.InitFormsAsync(CancellationToken.None));
            await doc.FormClickAsync(0, 0, Text1X, Text1Y, CancellationToken.None);
            foreach (var c in "SAVED")
                await doc.FormCharAsync(c, CancellationToken.None);
            await doc.FormClickAsync(0, 0, CheckX, CheckY, CancellationToken.None);
            await doc.FormEndAsync(CancellationToken.None);
            await doc.SaveCurrentAsync(target, CancellationToken.None);
        }

        // Свежее открытие: никакого форм-окружения, только то, что в файле.
        await using var reopened = await _engine.OpenAsync(target, null, CancellationToken.None);
        Assert.True(InkInField(await Render(reopened), Text1InkX, Text1Y),
            "текст поля не виден в сохранённой копии");
        Assert.True(InkInField(await Render(reopened), CheckX, CheckY),
            "галка чекбокса не видна в сохранённой копии");
    }

    [Fact]
    public async Task Values_Reach_The_Print_Render()
    {
        var path = PdfFixture.WriteFormTrioToTemp("print.pdf");
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);
        Assert.True(await doc.InitFormsAsync(CancellationToken.None));

        await doc.FormClickAsync(0, 0, Text1X, Text1Y, CancellationToken.None);
        foreach (var c in "PRINT")
            await doc.FormCharAsync(c, CancellationToken.None);
        await doc.FormClickAsync(0, 0, CheckX, CheckY, CancellationToken.None);
        await doc.FormEndAsync(CancellationToken.None);

        // Ровно то, что просит Центр печати с «Со значениями полей».
        var image = await doc.RenderPageForPrintAsync(0, 612, 792, 0,
            new NexusPdf.Pdf.Abstractions.PrintContentOptions(
                IncludeAnnotations: true, OnlyPrintableAnnotations: true, IncludeFormFields: true),
            CancellationToken.None);
        Assert.True(InkInField(image, Text1InkX, Text1Y), "текст поля не попал в печатный рендер");
        Assert.True(InkInField(image, CheckX, CheckY), "галка не попала в печатный рендер");
    }

    private static Task<NexusPdf.Pdf.Abstractions.RenderedPageImage> Render(
        NexusPdf.Pdf.Abstractions.IPdfDocumentHandle doc) =>
        doc.RenderPageAsync(0, 612, 792, 0, CancellationToken.None);

    /// <summary>Есть ли в окрестности точки (±40×±14 px при масштабе 1 px/pt) не-белые пиксели.</summary>
    private static bool InkInField(NexusPdf.Pdf.Abstractions.RenderedPageImage image, double x, double y)
    {
        var dark = 0;
        for (var dy = -14; dy <= 14; dy++)
        {
            var row = (int)y + dy;
            if (row < 0 || row >= image.PixelHeight) continue;
            for (var dx = -40; dx <= 40; dx++)
            {
                var col = (int)x + dx;
                if (col < 0 || col >= image.PixelWidth) continue;
                var i = row * image.Stride + col * 4;
                if (image.Bgra[i] < 200 || image.Bgra[i + 1] < 200 || image.Bgra[i + 2] < 200)
                    dark++;
            }
        }
        return dark > 10;
    }
}
