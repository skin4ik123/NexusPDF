using System.Text;
using System.Text.Json;

namespace NexusPdf.Pdf.Abstractions.TextBlocks;

/// <summary>Позиция в тексте блока: номер абзаца и смещение в нём (UTF-16).</summary>
public readonly record struct TextPosition(int Paragraph, int Offset) : IComparable<TextPosition>
{
    public int CompareTo(TextPosition other) =>
        Paragraph != other.Paragraph ? Paragraph.CompareTo(other.Paragraph) : Offset.CompareTo(other.Offset);

    public static bool operator <(TextPosition a, TextPosition b) => a.CompareTo(b) < 0;
    public static bool operator >(TextPosition a, TextPosition b) => a.CompareTo(b) > 0;
    public static bool operator <=(TextPosition a, TextPosition b) => a.CompareTo(b) <= 0;
    public static bool operator >=(TextPosition a, TextPosition b) => a.CompareTo(b) >= 0;
}

public enum CaretMove { Left, Right, WordLeft, WordRight, LineStart, LineEnd, Up, Down, BlockStart, BlockEnd }

/// <summary>Принят ли ввод; если нет — ключ словаря с объяснением.</summary>
public sealed record InputResult(bool Accepted, string? RejectedReasonKey)
{
    public static readonly InputResult Ok = new(true, null);
    public static readonly InputResult UnsupportedScript = new(false, "TextBlockUnsupportedScript");
}

/// <summary>
/// Редактирование текстового блока без экрана: текст, курсор, выделение,
/// стили фрагментов, отмена, буфер обмена, ввод через IME. Экран (WPF)
/// только рисует <see cref="Layout"/> и передаёт сюда клавиши и мышь —
/// поэтому всё поведение проверяется тестами без окна.
/// </summary>
public sealed class TextBlockEditorModel
{
    private readonly IFontProvider _fonts;
    private readonly TextStyle _defaultStyle;
    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();
    private List<Para> _paras;
    private TextBlockOverlay _frame;
    private TextStyle? _pending;
    private double? _desiredX;
    private TextPosition? _typingEnd;
    private TextStyle? _typingStyle;

    public TextBlockEditorModel(TextBlockOverlay block, IFontProvider fonts, TextStyle defaultStyle)
    {
        _fonts = fonts;
        _defaultStyle = defaultStyle;
        _frame = block;
        _paras = block.Paragraphs.Count == 0
            ? [Para.Empty(defaultStyle, TextAlign.Left, 1.15)]
            : block.Paragraphs.Select(p => Para.From(p, defaultStyle)).ToList();
        Relayout();
    }

    public TextBlockOverlay Block { get; private set; } = null!;
    public LaidOutBlock Layout { get; private set; } = null!;
    public TextPosition Caret { get; private set; }
    public TextPosition Anchor { get; private set; }
    public bool HasSelection => Caret != Anchor;
    public string Composition { get; private set; } = "";
    public bool IsEmpty => _paras.All(p => p.Text.Length == 0);
    public event EventHandler? Changed;

    private TextPosition SelectionStart => Caret < Anchor ? Caret : Anchor;
    private TextPosition SelectionEnd => Caret < Anchor ? Anchor : Caret;

    /// <summary>Стиль, которым пойдёт набор: заданный кнопкой или стиль буквы перед курсором.</summary>
    public TextStyle StyleAtCaret => _pending ?? _paras[Caret.Paragraph].StyleAt(Caret.Offset);

    // ----- Ввод -----

    public InputResult Insert(string text) => InsertCore(text, typing: true);

    public void NewParagraph() => InsertCore(TextBlockText.ParagraphBreak.ToString(), typing: false);

    public void LineBreak() => InsertCore(TextBlockText.LineBreak.ToString(), typing: false);

    private InputResult InsertCore(string raw, bool typing)
    {
        var text = TextBlockText.Normalize(raw);
        if (!Supported(text)) return InputResult.UnsupportedScript;
        if (text.Length == 0) return InputResult.Ok;

        var style = StyleAtCaret;
        var coalesce = typing && !HasSelection && text.Length <= 2 && text != TextBlockText.ParagraphBreak.ToString()
                       && _typingEnd == Caret && _typingStyle == style;
        if (!coalesce) PushUndo();

        DeleteSelectionCore();
        var parts = text.Split(TextBlockText.ParagraphBreak);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) SplitAtCaret();
            var para = _paras[Caret.Paragraph];
            para.Insert(Caret.Offset, parts[i], style);
            Caret = Anchor = new TextPosition(Caret.Paragraph, Caret.Offset + parts[i].Length);
        }
        _pending = null;
        _desiredX = null;
        _typingEnd = typing ? Caret : null;
        _typingStyle = typing ? style : null;
        Relayout();
        return InputResult.Ok;
    }

    private static bool Supported(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (!TextBlockText.IsSupported(rune.Value)) return false;
        }
        return true;
    }

    private void SplitAtCaret()
    {
        var para = _paras[Caret.Paragraph];
        var tail = para.Split(Caret.Offset);
        _paras.Insert(Caret.Paragraph + 1, tail);
        Caret = Anchor = new TextPosition(Caret.Paragraph + 1, 0);
    }

    public void Backspace(bool word) => Erase(word ? CaretMove.WordLeft : CaretMove.Left);

    public void Delete(bool word) => Erase(word ? CaretMove.WordRight : CaretMove.Right);

    private void Erase(CaretMove move)
    {
        if (!HasSelection)
        {
            var target = Moved(Caret, move);
            if (target == Caret) return;
            Anchor = target;
        }
        PushUndo();
        DeleteSelectionCore();
        _typingEnd = null;
        Relayout();
    }

    private void DeleteSelectionCore()
    {
        if (!HasSelection) return;
        var (origin, target) = (SelectionStart, SelectionEnd);
        var first = _paras[origin.Paragraph];
        var last = _paras[target.Paragraph];
        var tail = last.Split(target.Offset);
        first.Truncate(origin.Offset);
        first.Append(tail);
        _paras.RemoveRange(origin.Paragraph + 1, target.Paragraph - origin.Paragraph);
        Caret = Anchor = origin;
    }

    // ----- Курсор и выделение -----

    public void PlaceCaretAt(TextPosition position, bool extend = false)
    {
        var p = Math.Clamp(position.Paragraph, 0, _paras.Count - 1);
        var clamped = new TextPosition(p, Math.Clamp(position.Offset, 0, _paras[p].Text.Length));
        SetCaret(clamped, extend);
    }

    public void MoveCaret(CaretMove move, bool extend)
    {
        if (!extend && HasSelection && move is CaretMove.Left or CaretMove.Right)
        {
            SetCaret(move == CaretMove.Left ? SelectionStart : SelectionEnd, false);
            return;
        }
        // При движении вверх-вниз курсор держит исходную колонку, даже если
        // по пути попалась короткая строка.
        var keepX = move is CaretMove.Up or CaretMove.Down;
        var column = keepX ? _desiredX ?? CaretX(Caret) : 0;
        if (keepX) _desiredX = column;
        var target = Moved(Caret, move);
        SetCaret(target, extend);
        if (keepX) _desiredX = column;
    }

    public void PlaceCaret(double xPt, double yPt, bool extend) => SetCaret(HitTest(xPt, yPt), extend);

    public void SelectAll()
    {
        Anchor = new TextPosition(0, 0);
        Caret = new TextPosition(_paras.Count - 1, _paras[^1].Text.Length);
        AfterCaretChange();
    }

    public void SelectWordAt(double xPt, double yPt)
    {
        var at = HitTest(xPt, yPt);
        var text = _paras[at.Paragraph].Text;
        if (text.Length == 0) { SetCaret(at, false); return; }
        var probe = Math.Min(at.Offset, text.Length - 1);
        var kind = Kind(text[probe]);
        var start = probe;
        while (start > 0 && Kind(text[start - 1]) == kind) start--;
        var end = probe;
        while (end < text.Length && Kind(text[end]) == kind) end++;
        Anchor = new TextPosition(at.Paragraph, start);
        Caret = new TextPosition(at.Paragraph, end);
        AfterCaretChange();
    }

    public void SelectParagraphAt(double xPt, double yPt)
    {
        var at = HitTest(xPt, yPt);
        Anchor = new TextPosition(at.Paragraph, 0);
        Caret = new TextPosition(at.Paragraph, _paras[at.Paragraph].Text.Length);
        AfterCaretChange();
    }

    private void SetCaret(TextPosition position, bool extend)
    {
        Caret = position;
        if (!extend) Anchor = position;
        AfterCaretChange();
    }

    private void AfterCaretChange()
    {
        _pending = null;
        _desiredX = null;
        _typingEnd = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private TextPosition Moved(TextPosition origin, CaretMove move)
    {
        var text = _paras[origin.Paragraph].Text;
        switch (move)
        {
            case CaretMove.Left:
                if (origin.Offset == 0)
                    return origin.Paragraph == 0 ? origin : new TextPosition(origin.Paragraph - 1, _paras[origin.Paragraph - 1].Text.Length);
                var left = origin.Offset - 1;
                if (left > 0 && char.IsLowSurrogate(text[left]) && char.IsHighSurrogate(text[left - 1])) left--;
                return origin with { Offset = left };
            case CaretMove.Right:
                if (origin.Offset >= text.Length)
                    return origin.Paragraph == _paras.Count - 1 ? origin : new TextPosition(origin.Paragraph + 1, 0);
                var right = origin.Offset + 1;
                if (right < text.Length && char.IsLowSurrogate(text[right])) right++;
                return origin with { Offset = right };
            case CaretMove.WordLeft:
                if (origin.Offset == 0) return Moved(origin, CaretMove.Left);
                var w = origin.Offset;
                while (w > 0 && char.IsWhiteSpace(text[w - 1])) w--;
                if (w > 0)
                {
                    var kind = Kind(text[w - 1]);
                    while (w > 0 && Kind(text[w - 1]) == kind && !char.IsWhiteSpace(text[w - 1])) w--;
                }
                return origin with { Offset = w };
            case CaretMove.WordRight:
                if (origin.Offset >= text.Length) return Moved(origin, CaretMove.Right);
                var r = origin.Offset;
                if (!char.IsWhiteSpace(text[r]))
                {
                    var kind = Kind(text[r]);
                    while (r < text.Length && Kind(text[r]) == kind) r++;
                }
                while (r < text.Length && char.IsWhiteSpace(text[r]) && text[r] != TextBlockText.LineBreak) r++;
                return origin with { Offset = r };
            case CaretMove.LineStart:
                return origin with { Offset = Layout.Lines[LineOf(origin)].Start };
            case CaretMove.LineEnd:
                return origin with { Offset = LineMax(LineOf(origin)) };
            case CaretMove.Up:
            case CaretMove.Down:
                var index = LineOf(origin) + (move == CaretMove.Up ? -1 : 1);
                if (index < 0) return new TextPosition(0, 0);
                if (index >= Layout.Lines.Count) return new TextPosition(_paras.Count - 1, _paras[^1].Text.Length);
                var line = Layout.Lines[index];
                return HitLine(index, _desiredX ?? CaretX(origin), line);
            case CaretMove.BlockStart:
                return new TextPosition(0, 0);
            default:
                return new TextPosition(_paras.Count - 1, _paras[^1].Text.Length);
        }
    }

    /// <summary>Класс символа для границ слова: пробел, буква/цифра, прочее.</summary>
    private static int Kind(char c) => char.IsWhiteSpace(c) ? 0 : char.IsLetterOrDigit(c) || char.IsSurrogate(c) ? 1 : 2;

    // ----- Геометрия курсора (по вёрстке) -----

    private int LineOf(TextPosition position)
    {
        var lines = Layout.Lines;
        var last = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Paragraph != position.Paragraph) continue;
            last = i;
            if (position.Offset >= line.Start && position.Offset < line.Start + line.Length)
                return i;
        }
        return Math.Max(last, 0);
    }

    private bool IsLastOfParagraph(int index) =>
        index == Layout.Lines.Count - 1 || Layout.Lines[index + 1].Paragraph != Layout.Lines[index].Paragraph;

    /// <summary>Самое правое место курсора в строке: перед переводом строки или пробелом переноса.</summary>
    private int LineMax(int index)
    {
        var line = Layout.Lines[index];
        return IsLastOfParagraph(index) ? line.Start + line.Length : Math.Max(line.Start, line.Start + line.Length - 1);
    }

    private double CaretX(TextPosition position) => XIn(Layout.Lines[LineOf(position)], position.Offset);

    private double XIn(LaidOutLine line, int offset)
    {
        if (line.Pieces.Count == 0)
        {
            return _paras[line.Paragraph].Align switch
            {
                TextAlign.Center => Layout.WidthPt / 2,
                TextAlign.Right => Layout.WidthPt - TextBlockLayout.PaddingPt,
                _ => TextBlockLayout.PaddingPt,
            };
        }
        foreach (var piece in line.Pieces)
        {
            if (offset <= piece.Start) return piece.XPt;
            if (offset <= piece.Start + piece.Text.Length)
            {
                var x = piece.XPt;
                for (var k = 0; k < offset - piece.Start; k++) x += piece.AdvancesPt[k];
                return x;
            }
        }
        var lastPiece = line.Pieces[^1];
        return lastPiece.XPt + lastPiece.WidthPt;
    }

    public (double X, double Top, double Height) CaretRect()
    {
        var line = Layout.Lines[LineOf(Caret)];
        return (XIn(line, Caret.Offset), line.TopPt, line.HeightPt);
    }

    public IReadOnlyList<OverlayBox> SelectionRects()
    {
        var rects = new List<OverlayBox>();
        if (!HasSelection) return rects;
        var (origin, target) = (SelectionStart, SelectionEnd);
        foreach (var line in Layout.Lines)
        {
            var lineStart = new TextPosition(line.Paragraph, line.Start);
            var lineEnd = new TextPosition(line.Paragraph, line.Start + line.Length);
            if (origin >= lineEnd && line.Length > 0 || target <= lineStart) continue;
            var x1 = XIn(line, origin.Paragraph < line.Paragraph ? line.Start : Math.Max(origin.Offset, line.Start));
            var x2 = XIn(line, target.Paragraph > line.Paragraph ? line.Start + line.Length : Math.Min(target.Offset, line.Start + line.Length));
            if (x2 - x1 > 1e-9)
                rects.Add(new OverlayBox(x1, line.TopPt, x2 - x1, line.HeightPt));
        }
        return rects;
    }

    private TextPosition HitTest(double x, double y)
    {
        var lines = Layout.Lines;
        var index = lines.Count - 1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (y < lines[i].TopPt + lines[i].HeightPt) { index = i; break; }
        }
        return HitLine(index, x, lines[index]);
    }

    /// <summary>Ближайшая к x граница символа в строке — за один проход по кускам.</summary>
    private TextPosition HitLine(int index, double x, LaidOutLine line)
    {
        var text = _paras[line.Paragraph].Text;
        var max = LineMax(index);
        var best = line.Start;
        var bestDistance = Math.Abs(XIn(line, line.Start) - x);
        foreach (var piece in line.Pieces)
        {
            var px = piece.XPt;
            for (var k = 0; k < piece.AdvancesPt.Count; k++)
            {
                px += piece.AdvancesPt[k];
                var offset = piece.Start + k + 1;
                if (offset > max) break;
                if (offset < text.Length && char.IsLowSurrogate(text[offset])) continue;
                var distance = Math.Abs(px - x);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = offset;
                }
            }
        }
        return new TextPosition(line.Paragraph, best);
    }

    // ----- Стиль и абзацы -----

    public void ApplyStyle(Func<TextStyle, TextStyle> change)
    {
        if (!HasSelection)
        {
            _pending = change(StyleAtCaret);
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        PushUndo();
        var (origin, target) = (SelectionStart, SelectionEnd);
        for (var p = origin.Paragraph; p <= target.Paragraph; p++)
        {
            var start = p == origin.Paragraph ? origin.Offset : 0;
            var end = p == target.Paragraph ? target.Offset : _paras[p].Text.Length;
            _paras[p].Restyle(start, end, change);
        }
        _typingEnd = null;
        Relayout();
    }

    public void SetAlign(TextAlign align) => ForParagraphs(p => p.Align = align);

    public void SetLineSpacing(double spacing) => ForParagraphs(p => p.Spacing = Math.Clamp(spacing, 0.5, 5));

    private void ForParagraphs(Action<Para> change)
    {
        PushUndo();
        for (var p = SelectionStart.Paragraph; p <= SelectionEnd.Paragraph; p++) change(_paras[p]);
        _typingEnd = null;
        Relayout();
    }

    public void SetFrame(double xPt, double yPt, double widthPt, double heightPt,
        TextBlockWidthMode mode, bool autoHeight, bool shrinkToFit, double rotationDegrees)
    {
        _frame = _frame with
        {
            XPt = xPt, YPt = yPt, WidthPt = widthPt, HeightPt = heightPt, WidthMode = mode,
            AutoHeight = autoHeight, ShrinkToFit = shrinkToFit, RotationDegrees = rotationDegrees,
        };
        Relayout();
    }

    // ----- IME -----

    public void SetComposition(string preedit)
    {
        Composition = preedit;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void CommitComposition(string text)
    {
        Composition = "";
        if (text.Length > 0) Insert(text);
        else Changed?.Invoke(this, EventArgs.Empty);
    }

    // ----- Отмена -----

    private sealed record Snapshot(List<Para> Paras, TextPosition Caret, TextPosition Anchor);

    private void PushUndo()
    {
        _undo.Push(new Snapshot(_paras.Select(p => p.Clone()).ToList(), Caret, Anchor));
        _redo.Clear();
    }

    public bool Undo() => Restore(_undo, _redo);

    public bool Redo() => Restore(_redo, _undo);

    private bool Restore(Stack<Snapshot> origin, Stack<Snapshot> target)
    {
        if (origin.Count == 0) return false;
        target.Push(new Snapshot(_paras.Select(p => p.Clone()).ToList(), Caret, Anchor));
        var snapshot = origin.Pop();
        _paras = snapshot.Paras.Select(p => p.Clone()).ToList();
        Caret = snapshot.Caret;
        Anchor = snapshot.Anchor;
        _typingEnd = null;
        _pending = null;
        Relayout();
        return true;
    }

    // ----- Буфер обмена -----

    private sealed record RichParagraph(List<TextRun> Runs, TextAlign Align, double LineSpacing);

    public string CopyPlainText()
    {
        var builder = new StringBuilder();
        var (origin, target) = (SelectionStart, SelectionEnd);
        for (var p = origin.Paragraph; p <= target.Paragraph; p++)
        {
            var text = _paras[p].Text;
            var start = p == origin.Paragraph ? origin.Offset : 0;
            var end = p == target.Paragraph ? target.Offset : text.Length;
            if (p > origin.Paragraph) builder.Append("\r\n");
            builder.Append(text, start, end - start);
        }
        return builder.Replace(TextBlockText.LineBreak.ToString(), "\r\n").ToString();
    }

    /// <summary>Выделенное со стилями — свой формат буфера обмена внутри NexusPDF.</summary>
    public string CopyRich()
    {
        var (origin, target) = (SelectionStart, SelectionEnd);
        var list = new List<RichParagraph>();
        for (var p = origin.Paragraph; p <= target.Paragraph; p++)
        {
            var para = _paras[p].Clone();
            var end = p == target.Paragraph ? target.Offset : para.Text.Length;
            para.Truncate(end);
            var tail = p == origin.Paragraph ? para.Split(origin.Offset) : para;
            list.Add(new RichParagraph(tail.ToParagraph().Runs.ToList(), tail.Align, tail.Spacing));
        }
        return JsonSerializer.Serialize(list);
    }

    public InputResult Paste(string? rich, string plain)
    {
        var paragraphs = TryReadRich(rich);
        if (paragraphs == null) return InsertCore(plain, typing: false);
        if (!paragraphs.All(p => p.Runs.All(r => Supported(r.Text)))) return InputResult.UnsupportedScript;

        PushUndo();
        DeleteSelectionCore();
        for (var i = 0; i < paragraphs.Count; i++)
        {
            if (i > 0)
            {
                SplitAtCaret();
                _paras[Caret.Paragraph].Align = paragraphs[i].Align;
                _paras[Caret.Paragraph].Spacing = paragraphs[i].LineSpacing;
            }
            foreach (var run in paragraphs[i].Runs)
            {
                var text = TextBlockText.Normalize(run.Text).Replace(TextBlockText.ParagraphBreak.ToString(), "");
                _paras[Caret.Paragraph].Insert(Caret.Offset, text, run.Style);
                Caret = Anchor = Caret with { Offset = Caret.Offset + text.Length };
            }
        }
        _typingEnd = null;
        _pending = null;
        Relayout();
        return InputResult.Ok;
    }

    private static List<RichParagraph>? TryReadRich(string? rich)
    {
        if (string.IsNullOrEmpty(rich)) return null;
        try
        {
            var list = JsonSerializer.Deserialize<List<RichParagraph>>(rich);
            return list is { Count: > 0 } && list.All(p => p.Runs != null && p.Runs.All(r => r?.Text != null && r.Style?.FontFamily != null))
                ? list
                : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    // ----- Пересборка -----

    private void Relayout()
    {
        var draft = _frame with { Paragraphs = _paras.Select(p => p.ToParagraph()).ToList() };
        Layout = TextBlockLayout.Compute(draft, _fonts);
        Block = draft with
        {
            WidthPt = draft.WidthMode == TextBlockWidthMode.AutoWidth ? Layout.WidthPt : draft.WidthPt,
            HeightPt = draft.AutoHeight ? Layout.HeightPt : draft.HeightPt,
        };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Изменяемый абзац: текст и границы фрагментов стиля.</summary>
    private sealed class Para
    {
        private StringBuilder _text = new();
        private List<(int Length, TextStyle Style)> _spans = new();

        public TextAlign Align { get; set; }
        public double Spacing { get; set; }
        public string Text => _text.ToString();

        public static Para Empty(TextStyle style, TextAlign align, double spacing) =>
            new() { Align = align, Spacing = spacing, _spans = { (0, style) } };

        public static Para From(TextParagraph paragraph, TextStyle fallback)
        {
            var para = new Para { Align = paragraph.Align, Spacing = paragraph.LineSpacing };
            foreach (var run in paragraph.Runs)
            {
                para._text.Append(run.Text);
                para._spans.Add((run.Text.Length, run.Style));
            }
            if (para._spans.Count == 0) para._spans.Add((0, fallback));
            para.Normalize();
            return para;
        }

        public Para Clone() => new()
        {
            Align = Align, Spacing = Spacing, _text = new StringBuilder(Text), _spans = new List<(int, TextStyle)>(_spans),
        };

        public TextParagraph ToParagraph()
        {
            var runs = new List<TextRun>();
            var offset = 0;
            var text = Text;
            foreach (var (length, style) in _spans)
            {
                runs.Add(new TextRun(text.Substring(offset, length), style));
                offset += length;
            }
            return new TextParagraph(runs, Align, Spacing);
        }

        public TextStyle StyleAt(int offset)
        {
            var position = 0;
            var probe = offset > 0 ? offset - 1 : 0;
            foreach (var (length, style) in _spans)
            {
                if (probe < position + length) return style;
                position += length;
            }
            return _spans[^1].Style;
        }

        public void Insert(int offset, string text, TextStyle style)
        {
            if (text.Length == 0) return;
            SplitAt(offset);
            var index = SpanIndexAt(offset);
            _spans.Insert(index, (text.Length, style));
            _text.Insert(offset, text);
            Normalize();
        }

        /// <summary>Отрезает хвост с offset и возвращает его отдельным абзацем.</summary>
        public Para Split(int offset)
        {
            SplitAt(offset);
            var index = SpanIndexAt(offset);
            var tail = new Para { Align = Align, Spacing = Spacing };
            tail._text.Append(Text, offset, _text.Length - offset);
            tail._spans = _spans.Skip(index).ToList();
            var edgeStyle = StyleAt(offset);
            if (tail._spans.Count == 0) tail._spans.Add((0, edgeStyle));
            _text.Length = offset;
            _spans = _spans.Take(index).ToList();
            if (_spans.Count == 0) _spans.Add((0, edgeStyle));
            Normalize();
            tail.Normalize();
            return tail;
        }

        public void Truncate(int offset) => Split(offset);

        public void Append(Para other)
        {
            _text.Append(other.Text);
            _spans.AddRange(other._spans);
            Normalize();
        }

        public void Restyle(int start, int end, Func<TextStyle, TextStyle> change)
        {
            if (end <= start) return;
            SplitAt(start);
            SplitAt(end);
            var position = 0;
            for (var i = 0; i < _spans.Count; i++)
            {
                var (length, style) = _spans[i];
                if (position >= start && position + length <= end && length > 0)
                    _spans[i] = (length, change(style));
                position += length;
            }
            Normalize();
        }

        private void SplitAt(int offset)
        {
            var position = 0;
            for (var i = 0; i < _spans.Count; i++)
            {
                var (length, style) = _spans[i];
                if (offset > position && offset < position + length)
                {
                    _spans[i] = (offset - position, style);
                    _spans.Insert(i + 1, (position + length - offset, style));
                    return;
                }
                position += length;
            }
        }

        private int SpanIndexAt(int offset)
        {
            var position = 0;
            for (var i = 0; i < _spans.Count; i++)
            {
                if (position >= offset && _spans[i].Length > 0) return i;
                position += _spans[i].Length;
            }
            return _spans.Count;
        }

        /// <summary>Соседние фрагменты одного стиля сливаются, пустые выбрасываются (один пустой остаётся).</summary>
        private void Normalize()
        {
            var merged = new List<(int Length, TextStyle Style)>();
            foreach (var span in _spans)
            {
                if (span.Length == 0) continue;
                if (merged.Count > 0 && merged[^1].Style == span.Style)
                    merged[^1] = (merged[^1].Length + span.Length, span.Style);
                else
                    merged.Add(span);
            }
            if (merged.Count == 0) merged.Add((0, _spans.Count > 0 ? _spans[^1].Style : new TextStyle(FontCatalog.DefaultFamily, 11)));
            _spans = merged;
        }
    }
}
