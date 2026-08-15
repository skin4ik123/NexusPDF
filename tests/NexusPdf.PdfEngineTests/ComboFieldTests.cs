using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>Выпадающие списки форм: обнаружение в точке, опции, выбор и прямое сохранение значения.</summary>
public sealed class ComboFieldTests : IAsyncLifetime
{
    private PdfiumRenderEngine _pdfium = null!;

    public Task InitializeAsync()
    {
        _pdfium = new PdfiumRenderEngine();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _pdfium.DisposeAsync();

    [Fact]
    public async Task Combo_Is_Detected_With_Options_And_Selection_Persists()
    {
        var path = PdfFixture.WriteComboFieldToTemp("combo.pdf", "city", "One", "Two", "Three");
        await using var handle = await _pdfium.OpenAsync(path, null, CancellationToken.None);
        Assert.True(await handle.InitFormsAsync(CancellationToken.None));

        // Поле /Rect [100 600 400 640] на странице 612x792: отображаемая точка
        // центра поля — (250, 792-620=172).
        var combo = await handle.GetFormComboAtAsync(0, 0, 250, 172, CancellationToken.None);
        Assert.NotNull(combo);
        Assert.Equal(new[] { "One", "Two", "Three" }, combo!.Options);
        Assert.False(combo.IsListBox);
        // Рамка поля в отображаемых пунктах.
        Assert.InRange(combo.XPt, 95, 105);
        Assert.InRange(combo.YPt, 147, 157);

        // Точка мимо поля — не combo.
        Assert.Null(await handle.GetFormComboAtAsync(0, 0, 50, 50, CancellationToken.None));

        // Выбор второго пункта и прямое сохранение.
        await handle.SetFormComboSelectionAsync(0, 0, 250, 172, 1, CancellationToken.None);
        await handle.FormKillFocusAsync(CancellationToken.None);
        var saved = Path.Combine(Path.GetDirectoryName(path)!, "combo-saved.pdf");
        await handle.SaveCurrentAsync(saved, CancellationToken.None);

        await using var reopened = await _pdfium.OpenAsync(saved, null, CancellationToken.None);
        var annotations = await reopened.GetAnnotationsAsync(0, CancellationToken.None);
        var widget = Assert.Single(annotations, a => a.Subtype == 20);
        Assert.Equal("Two", widget.Value);
    }
}
