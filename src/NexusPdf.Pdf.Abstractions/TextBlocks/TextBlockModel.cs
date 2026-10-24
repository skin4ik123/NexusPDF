namespace NexusPdf.Pdf.Abstractions.TextBlocks;

public enum TextAlign { Left, Center, Right, Justify }

public enum BaselineShift { None, Superscript, Subscript }

/// <summary>AutoWidth — блок растёт вправо, без переноса; Fixed — перенос по ширине рамки.</summary>
public enum TextBlockWidthMode { AutoWidth, Fixed }

/// <summary>Положение строк в рамке заданной высоты (при AutoHeight строки всегда сверху).</summary>
public enum TextVerticalAlign { Top, Center, Bottom }

/// <summary>Оформление фрагмента текста.</summary>
/// <param name="LetterSpacingPercent">Межбуквенный интервал в процентах размера шрифта.</param>
public sealed record TextStyle(
    string FontFamily,
    double FontSizePt,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    bool Strike = false,
    uint ColorArgb = 0xFF000000,
    BaselineShift Baseline = BaselineShift.None,
    double LetterSpacingPercent = 0);

/// <summary>Фрагмент текста одного оформления. Перевод строки внутри абзаца — <see cref="TextBlockText.LineBreak"/>.</summary>
public sealed record TextRun(string Text, TextStyle Style);

/// <summary>
/// Абзац: фрагменты, выравнивание и межстрочный интервал (множитель высоты строки).
/// Первая строка начинается с LeftIndentPt + FirstLineIndentPt, остальные — с
/// LeftIndentPt (отрицательный отступ первой строки — выступ, как в списках).
/// </summary>
public sealed record TextParagraph(
    IReadOnlyList<TextRun> Runs, TextAlign Align = TextAlign.Left, double LineSpacing = 1.15,
    double FirstLineIndentPt = 0, double LeftIndentPt = 0)
{
    public string Text => string.Concat(Runs.Select(r => r.Text));
}

/// <summary>
/// Текстовый блок. Рамка — в отображаемых пунктах от верхнего левого угла
/// страницы; поворот — вокруг центра рамки, в градусах против часовой.
/// BlockId — блок, уже записанный в файл (иначе пусто).
/// </summary>
public sealed record TextBlockOverlay(
    double XPt,
    double YPt,
    double WidthPt,
    double HeightPt,
    double RotationDegrees,
    TextBlockWidthMode WidthMode,
    bool AutoHeight,
    bool ShrinkToFit,
    IReadOnlyList<TextParagraph> Paragraphs,
    Guid BlockId = default,
    TextVerticalAlign VerticalAlign = TextVerticalAlign.Top) : PageOverlay;

/// <summary>
/// Правка блока, уже лежащего в файле: его объекты убираются со страницы и
/// (если NewBlock не null) блок пишется заново с тем же идентификатором.
/// </summary>
public sealed record TextBlockReplacement(Guid BlockId, TextBlockOverlay? NewBlock) : PageOverlay;

/// <summary>Объект страницы, который снимает замена абзаца: путь и отпечаток (см. <see cref="PageTextFingerprint"/>).</summary>
public sealed record ParagraphObjectRef(IReadOnlyList<int> Path, string Fingerprint);

/// <summary>
/// Абзац существующего текста, ставший блоком: при записи эти объекты (текст
/// и линии подчёркивания) снимаются со страницы — только если они на месте и
/// совпадают с отпечатком, — а NewBlock ложится блоком с меткой. NewBlock =
/// null — абзац удалён.
/// </summary>
public sealed record TextParagraphReplacement(IReadOnlyList<ParagraphObjectRef> Objects, TextBlockOverlay? NewBlock) : PageOverlay;

/// <summary>Кусок текста абзаца, как он записан одним объектом страницы.</summary>
public sealed record TextPiece(int Paragraph, int Start, string Text);
