using NexusPdf.Printing;

namespace NexusPdf.UnitTests;

/// <summary>
/// Предварительная проверка ПО ДОКУМЕНТУ: находки, которые невозможно вывести
/// из геометрии листов. Проверяется и то, что она молчит, когда сказать нечего:
/// окно печати, засыпанное подсказками на каждый файл, читать перестают.
/// </summary>
public sealed class DocumentPreflightTests
{
    private static string Describe(string code, object[] args) =>
        args.Length == 0 ? code : code + ":" + string.Join(",", args);

    private static PrinterCapabilities Colour() =>
        PrinterCapabilities.Unknown("Цветной") with { SupportsColor = true };

    private static PrinterCapabilities Mono() =>
        PrinterCapabilities.Unknown("Монохромный") with { SupportsColor = false };

    [Fact]
    public void An_Ordinary_Text_Document_Gets_No_Remarks()
    {
        var facts = new PrintDocumentFacts(
            Pages: 20, SampledPages: 12, Images: 2, TextLength: 40000,
            AverageImageDpi: 300, HasLayers: false);

        var issues = DocumentPreflight.Analyze(facts, ColorMode.Color, Colour(), Describe);

        Assert.Empty(issues);
    }

    [Fact]
    public void A_Soft_Scan_Is_Reported_With_Its_Resolution()
    {
        var facts = new PrintDocumentFacts(
            Pages: 8, SampledPages: 8, Images: 8, TextLength: 0,
            AverageImageDpi: 110, HasLayers: false);

        var issue = Assert.Single(
            DocumentPreflight.Analyze(facts, ColorMode.Color, Colour(), Describe));

        Assert.Equal(DocumentPreflight.CodeScanLowDpi, issue.Code);
        Assert.Equal(PreflightLevel.Info, issue.Level);
        Assert.Contains("110", issue.Message);
    }

    /// <summary>Хороший скан — не повод для замечания.</summary>
    [Fact]
    public void A_Sharp_Scan_Is_Left_Alone()
    {
        var facts = new PrintDocumentFacts(6, 6, 6, 0, 400, false);
        Assert.Empty(DocumentPreflight.Analyze(facts, ColorMode.Color, Colour(), Describe));
    }

    [Fact]
    public void A_Document_Without_Text_Or_Images_Is_Reported_As_Possibly_Empty()
    {
        var facts = new PrintDocumentFacts(3, 3, 0, 0, 0, false);

        var issue = Assert.Single(
            DocumentPreflight.Analyze(facts, ColorMode.Color, Colour(), Describe));

        Assert.Equal(DocumentPreflight.CodeNoContent, issue.Code);
        Assert.Equal(PreflightLevel.Warning, issue.Level);
    }

    /// <summary>
    /// Серый режим на цветном принтере — частая случайность: профиль
    /// «Черновик» остался с прошлого раза, и цветная презентация уходит серой.
    /// </summary>
    [Fact]
    public void Grey_Mode_On_A_Colour_Printer_Is_Pointed_Out()
    {
        var facts = new PrintDocumentFacts(4, 4, 1, 9000, 300, false);

        var issue = Assert.Single(
            DocumentPreflight.Analyze(facts, ColorMode.Grayscale, Colour(), Describe));

        Assert.Equal(DocumentPreflight.CodeGrayOnColorPrinter, issue.Code);
    }

    /// <summary>А на монохромном принтере серый режим — единственно возможный.</summary>
    [Fact]
    public void Grey_Mode_On_A_Monochrome_Printer_Is_Not_Worth_Mentioning()
    {
        var facts = new PrintDocumentFacts(4, 4, 1, 9000, 300, false);
        Assert.Empty(DocumentPreflight.Analyze(facts, ColorMode.Grayscale, Mono(), Describe));
    }

    [Fact]
    public void Layers_Are_Mentioned_Because_What_Prints_Is_What_Is_Visible()
    {
        var facts = new PrintDocumentFacts(4, 4, 1, 9000, 300, HasLayers: true);

        var issue = Assert.Single(
            DocumentPreflight.Analyze(facts, ColorMode.Color, Colour(), Describe));

        Assert.Equal(DocumentPreflight.CodeLayers, issue.Code);
        Assert.Equal(PreflightLevel.Info, issue.Level);
    }

    /// <summary>Ничего не известно о документе — и замечаний тоже нет.</summary>
    [Fact]
    public void An_Unread_Document_Produces_Nothing()
    {
        Assert.Empty(DocumentPreflight.Analyze(
            PrintDocumentFacts.Unknown, ColorMode.Grayscale, Colour(), Describe));
    }

    /// <summary>Ни одна находка документа не блокирует печать — это подсказки.</summary>
    [Fact]
    public void Document_Remarks_Never_Block_Printing()
    {
        var facts = new PrintDocumentFacts(5, 5, 5, 0, 90, HasLayers: true);

        var issues = DocumentPreflight.Analyze(facts, ColorMode.Monochrome, Colour(), Describe);

        Assert.NotEmpty(issues);
        Assert.All(issues, i => Assert.NotEqual(PreflightLevel.Critical, i.Level));
    }
}
