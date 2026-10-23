namespace NexusPdf.Pdf.Abstractions.TextBlocks;

/// <summary>
/// Блок, найденный на странице по меткам его объектов.
/// </summary>
/// <param name="Block">Блок для правки; null — блок не целый (см. Intact).</param>
/// <param name="BoundsPt">Рамка всех объектов блока в отображаемых пунктах.</param>
/// <param name="Intact">
/// Описание читается, куски покрывают текст ровно, отпечаток совпал. Иначе
/// блок менял кто-то другой — он остаётся обычным текстом страницы.
/// </param>
public sealed record StoredTextBlock(Guid Id, TextBlockOverlay? Block, OverlayBox BoundsPt, bool Intact);
