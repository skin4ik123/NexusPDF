using NexusPdf.Domain;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Abstractions.TextBlocks;

namespace NexusPdf.Application;

/// <summary>
/// Что попадает в историю правок, когда ввод в текстовом блоке закончен.
/// Каждое завершение — один шаг отмены: весь набор в блоке отменяется разом.
/// </summary>
public static class TextBlockSessionService
{
    /// <param name="pageIndex">Логический номер страницы.</param>
    /// <param name="original">Правленый черновик (сам объект из списка страницы); null — новый или сохранённый блок.</param>
    /// <param name="storedId">Id блока, сохранённого в файле; null — блок ещё не сохранялся.</param>
    /// <param name="edited">Блок после ввода в рамке текущего показа страницы; пустой или null — блок удалён.</param>
    public static void Commit(
        DocumentSession session, int pageIndex, TextBlockOverlay? original, Guid? storedId, TextBlockOverlay? edited)
    {
        // Страницы уже нет (её удалили, пока шёл ввод) — писать некуда.
        if (pageIndex < 0 || pageIndex >= session.Model.Pages.Count)
            return;
        var empty = edited == null || edited.Paragraphs.All(p => p.Text.Length == 0);

        if (storedId is { } id)
        {
            // Сохранённый блок меняется заменой: при записи его объекты уйдут
            // со страницы, а новая версия ляжет с тем же Id. Повторная правка
            // до сохранения правит ту же замену, а не копит новые.
            var pending = session.Model.Pages[pageIndex].OverlayList
                .OfType<TextBlockReplacement>()
                .LastOrDefault(r => r.BlockId == id);
            var replacement = new TextBlockReplacement(id, empty ? null : edited! with { BlockId = id })
            {
                PlacedRotation = edited?.PlacedRotation ?? pending?.PlacedRotation ?? 0,
            };
            session.Apply(pending != null
                ? new ReplaceOverlayOperation(pageIndex, pending, replacement)
                : new AddOverlayOperation(pageIndex, replacement));
            return;
        }

        if (original != null)
        {
            var overlays = session.Model.Pages[pageIndex].OverlayList;
            var index = -1;
            for (var i = 0; i < overlays.Count; i++)
            {
                if (ReferenceEquals(overlays[i], original)) { index = i; break; }
            }
            if (index >= 0)
            {
                session.Apply(empty
                    ? new RemoveOverlayAtOperation(pageIndex, index)
                    : new ReplaceOverlayOperation(pageIndex, original, edited! with { BlockId = original.BlockId }));
                return;
            }
            // Черновика уже нет (отменили, пока шёл ввод): набранное не
            // теряется, а ложится новым блоком.
        }

        if (!empty)
        {
            session.Apply(new AddOverlayOperation(pageIndex,
                edited! with { BlockId = edited.BlockId == Guid.Empty ? Guid.NewGuid() : edited.BlockId }));
        }
    }

    /// <summary>
    /// Абзац существующего текста после ввода: его объекты и линии под ним
    /// снимаются при записи, на их место ложится блок. Повторная правка того
    /// же абзаца до сохранения правит ту же замену; пустой блок — абзац удалён.
    /// </summary>
    public static void CommitParagraph(DocumentSession session, int pageIndex, DetectedParagraph source, TextBlockOverlay? edited) =>
        CommitParagraph(session, pageIndex,
            source.Objects.Select(o => new ParagraphObjectRef(o.Path, o.Fingerprint))
                .Concat(source.Decorations.Select(d => new ParagraphObjectRef(d.Path, d.Fingerprint)))
                .ToList(),
            edited);

    /// <param name="objects">Объекты абзаца — из найденного абзаца или из уже ждущей замены.</param>
    public static void CommitParagraph(
        DocumentSession session, int pageIndex, IReadOnlyList<ParagraphObjectRef> objects, TextBlockOverlay? edited)
    {
        if (pageIndex < 0 || pageIndex >= session.Model.Pages.Count || objects.Count == 0)
            return;
        var empty = edited == null || edited.Paragraphs.All(p => p.Text.Length == 0);
        var pending = PendingParagraph(session, pageIndex, objects);
        var id = pending?.NewBlock?.BlockId is { } known && known != Guid.Empty ? known
            : edited?.BlockId is { } own && own != Guid.Empty ? own
            : Guid.NewGuid();
        var replacement = new TextParagraphReplacement(objects, empty ? null : edited! with { BlockId = id })
        {
            PlacedRotation = edited?.PlacedRotation ?? pending?.PlacedRotation ?? 0,
        };
        session.Apply(pending != null
            ? new ReplaceOverlayOperation(pageIndex, pending, replacement)
            : new AddOverlayOperation(pageIndex, replacement));
    }

    /// <summary>Ждущая замена того же абзаца (те же объекты) на странице.</summary>
    private static TextParagraphReplacement? PendingParagraph(
        DocumentSession session, int pageIndex, IReadOnlyList<ParagraphObjectRef> objects) =>
        session.Model.Pages[pageIndex].OverlayList
            .OfType<TextParagraphReplacement>()
            .LastOrDefault(r => r.Objects.Count == objects.Count &&
                                r.Objects.Zip(objects).All(pair =>
                                    pair.First.Fingerprint == pair.Second.Fingerprint &&
                                    pair.First.Path.SequenceEqual(pair.Second.Path)));

    /// <summary>
    /// Удалить объект страницы. Замена сохранённого блока не снимается (иначе
    /// блок вернулся бы в прежнем виде), а становится заменой без новой версии.
    /// </summary>
    public static void Delete(DocumentSession session, int pageIndex, int overlayIndex)
    {
        if (pageIndex < 0 || pageIndex >= session.Model.Pages.Count) return;
        var overlays = session.Model.Pages[pageIndex].OverlayList;
        if (overlayIndex < 0 || overlayIndex >= overlays.Count) return;
        if (overlays[overlayIndex] is TextBlockReplacement replacement)
            Commit(session, pageIndex, null, replacement.BlockId, null);
        else if (overlays[overlayIndex] is TextParagraphReplacement paragraph)
            CommitParagraph(session, pageIndex, paragraph.Objects, null);
        else
            session.Apply(new RemoveOverlayAtOperation(pageIndex, overlayIndex));
    }

    /// <summary>Одинаковы ли блоки по содержанию (рамка, режимы, абзацы, стили, текст).</summary>
    public static bool SameContent(TextBlockOverlay a, TextBlockOverlay b) =>
        TextBlockCodec.EncodeStructure(a) == TextBlockCodec.EncodeStructure(b) &&
        a.Paragraphs.Select(p => p.Text).SequenceEqual(b.Paragraphs.Select(p => p.Text));
}
