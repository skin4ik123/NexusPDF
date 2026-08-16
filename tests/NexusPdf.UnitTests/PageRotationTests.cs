using NexusPdf.Export;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.UnitTests;

/// <summary>
/// Приведение геометрии к отображаемому виду.
///
/// Это то место, где ошибка незаметна на обычных документах и фатальна на
/// сканах: у них /Rotate стоит почти всегда, а координаты объектов PDF живут в
/// НЕповёрнутой системе. Перепутать — значит выгрузить лист правильного
/// размера с текстом поперёк.
/// </summary>
public sealed class PageRotationTests
{
    // Лист 400 x 600 (книжный).
    private const double W = 400, H = 600;

    [Fact]
    public void Turning_The_Sheet_Swaps_Its_Sides()
    {
        Assert.Equal((W, H), PageRotation.Size(W, H, 0));
        Assert.Equal((H, W), PageRotation.Size(W, H, 1));
        Assert.Equal((W, H), PageRotation.Size(W, H, 2));
        Assert.Equal((H, W), PageRotation.Size(W, H, 3));
    }

    /// <summary>Углы листа после поворота вправо оказываются там, где их видит глаз.</summary>
    [Fact]
    public void Corners_Land_Where_The_Eye_Sees_Them()
    {
        // Левый верхний угол при повороте вправо уходит в правый верхний.
        Assert.Equal((H, W), PageRotation.Point(0, H, 1, W, H));
        // Левый нижний — в левый верхний.
        Assert.Equal((0.0, W), PageRotation.Point(0, 0, 1, W, H));
        // Поворот влево: левый верхний становится левым нижним.
        Assert.Equal((0.0, 0.0), PageRotation.Point(0, H, 3, W, H));
        // Разворот на 180°.
        Assert.Equal((W, H), PageRotation.Point(0, 0, 2, W, H));
    }

    [Fact]
    public void A_Rectangle_Keeps_Its_Size_And_Stays_Normalised()
    {
        var rect = new PdfTextRect(50, 520, 150, 500);   // ширина 100, высота 20

        var turned = PageRotation.Rect(rect, 1, W, H);

        Assert.True(turned.Right > turned.Left && turned.Top > turned.Bottom);
        Assert.Equal(20, turned.Right - turned.Left, 6);   // стороны поменялись местами
        Assert.Equal(100, turned.Top - turned.Bottom, 6);
    }

    /// <summary>
    /// У скана, снятого боком, подпись написана вдоль листа. После поворота
    /// листа она обязана стать обычной строкой — иначе Word напишет её боком.
    /// </summary>
    [Fact]
    public void Sideways_Text_Becomes_A_Normal_Line()
    {
        var word = new PdfTextWord("НАКЛАДНАЯ", new PdfTextRect(40, 300, 60, 100), 12, 400, 0xFF000000,
            RotationQuarters: 1);

        var straightened = PageRotation.Word(word, 1, W, H);

        Assert.Equal(0, straightened.RotationQuarters);
        Assert.True(straightened.Width > straightened.Height);   // легла горизонтально
    }

    /// <summary>Горизонтальная граница таблицы после поворота становится вертикальной.</summary>
    [Fact]
    public void A_Table_Border_Changes_Its_Direction()
    {
        var line = new PdfRulingLine(true, 500, 40, 360, 0.8);

        var turned = PageRotation.Ruling(line, 1, W, H);

        Assert.False(turned.IsHorizontal);
        Assert.Equal(320, turned.Length, 1);        // длина сохранилась
        Assert.Equal(500, turned.Position, 1);      // бывшая высота стала абсциссой
    }

    /// <summary>
    /// Картинка поворачивается вместе с листом ЦЕЛИКОМ. Забыть про пиксели —
    /// значит положить в документ фотографию набок.
    /// </summary>
    [Fact]
    public void The_Picture_Turns_With_The_Sheet()
    {
        // 2x1: левый пиксель красный, правый синий (BGRA).
        var bgra = new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 };

        var (turned, width, height) = PageRotation.RotatePixels(bgra, 2, 1, 1);

        Assert.Equal(1, width);
        Assert.Equal(2, height);
        // При повороте вправо левый пиксель уходит наверх.
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, turned.Take(4).ToArray());
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, turned.Skip(4).Take(4).ToArray());
    }

    [Fact]
    public void Four_Quarters_Return_Everything_To_Its_Place()
    {
        var rect = new PdfTextRect(40, 520, 160, 480);
        var moved = rect;
        var (w, h) = (W, H);
        for (var i = 0; i < 4; i++)
        {
            moved = PageRotation.Rect(moved, 1, w, h);
            (w, h) = PageRotation.Size(w, h, 1);
        }

        Assert.Equal(rect.Left, moved.Left, 6);
        Assert.Equal(rect.Top, moved.Top, 6);
        Assert.Equal(rect.Right, moved.Right, 6);
        Assert.Equal(rect.Bottom, moved.Bottom, 6);
    }

    [Fact]
    public void A_Page_Without_Text_But_With_A_Big_Picture_Is_A_Scan()
    {
        var full = new[] { new PdfTextRect(0, H, W, 0) };

        var scan = ScannedPageDetector.Classify(Array.Empty<PdfTextWord>(), full, W, H);
        Assert.True(scan.IsScan);
        Assert.False(scan.IsBlank);

        var blank = ScannedPageDetector.Classify(
            Array.Empty<PdfTextWord>(), Array.Empty<PdfTextRect>(), W, H);
        Assert.True(blank.IsBlank);
        Assert.False(blank.IsScan);

        var withText = ScannedPageDetector.Classify(
            new[] { new PdfTextWord("Договор поставки", new PdfTextRect(40, 520, 200, 500), 12, 400, 0) },
            full, W, H);
        Assert.True(withText.HasText);
        Assert.False(withText.IsScan);
    }

    /// <summary>
    /// Распознанные слова считаются от ВЕРХА страницы, а PDF — от низа.
    /// Перепутать эти начала координат — перевернуть страницу вверх ногами.
    /// </summary>
    [Fact]
    public void Recognised_Words_Land_The_Right_Way_Up()
    {
        var boxes = new[]
        {
            new OcrWordBox("Шапка", 40, 30, 100, 14),      // 30 пунктов от верха
            new OcrWordBox("Подвал", 40, H - 50, 100, 14), // у нижнего края
        };

        var words = ScannedPageDetector.FromRecognized(boxes, H);

        var header = words.Single(w => w.Text == "Шапка");
        var footer = words.Single(w => w.Text == "Подвал");
        Assert.True(header.CenterY > footer.CenterY);
        Assert.Equal(H - 30, header.RectPt.Top, 6);
        Assert.True(header.FontSizePt is > 5 and < 14);
    }
}
