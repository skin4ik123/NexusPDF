using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using NexusPdf.Pdf.Abstractions.TextBlocks;

namespace NexusPdf.App.Desktop.Views.Text;

/// <summary>
/// Редактор блока для UI Automation: поле ввода со значением (ValuePattern)
/// и текстом (TextPattern) — экранный диктор читает содержимое, выделение и
/// курсор, а проверки интерфейса находят редактор по AutomationId.
/// Текст для автоматизации — абзацы через "\n"; позиции совпадают с моделью.
/// Курсор виден как пустое выделение (ITextProvider2 в WPF нет).
/// </summary>
internal sealed class TextBlockCanvasAutomationPeer(TextBlockCanvas owner)
    : FrameworkElementAutomationPeer(owner), IValueProvider, ITextProvider
{
    private string _lastValue = "";

    private TextBlockEditorModel Model => owner.Model;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;

    protected override string GetClassNameCore() => nameof(TextBlockCanvas);

    protected override bool IsKeyboardFocusableCore() => true;

    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface is PatternInterface.Value or PatternInterface.Text ? this : base.GetPattern(patternInterface);

    internal IRawElementProviderSimple Provider() => ProviderFromPeer(this);

    internal void RaiseChanged()
    {
        var value = Text;
        if (value == _lastValue) return;
        RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, _lastValue, value);
        RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);
        _lastValue = value;
    }

    // ----- Текст и позиции -----

    internal string Text =>
        string.Join("\n", Model.Block.Paragraphs.Select(p => p.Text)).Replace(TextBlockText.LineBreak, '\n');

    internal TextPosition PositionAt(int offset)
    {
        var paragraphs = Model.Block.Paragraphs;
        for (var p = 0; p < paragraphs.Count; p++)
        {
            var length = paragraphs[p].Text.Length;
            if (offset <= length) return new TextPosition(p, Math.Max(0, offset));
            offset -= length + 1;
        }
        var last = paragraphs.Count - 1;
        return new TextPosition(last, paragraphs[last].Text.Length);
    }

    internal int OffsetOf(TextPosition position)
    {
        var paragraphs = Model.Block.Paragraphs;
        var offset = 0;
        for (var p = 0; p < position.Paragraph && p < paragraphs.Count; p++)
            offset += paragraphs[p].Text.Length + 1;
        return offset + position.Offset;
    }

    internal void Select(int start, int end)
    {
        Model.PlaceCaretAt(PositionAt(start));
        Model.PlaceCaretAt(PositionAt(end), extend: true);
    }

    // ----- ValuePattern -----

    public bool IsReadOnly => false;

    public string Value => Text;

    public void SetValue(string value)
    {
        Model.SelectAll();
        if (value.Length == 0)
            Model.Delete(word: false);
        else
            Model.Insert(value);
    }

    // ----- TextPattern -----

    public ITextRangeProvider DocumentRange => new TextBlockTextRange(this, 0, Text.Length);

    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;

    public ITextRangeProvider[] GetSelection()
    {
        var (a, b) = (OffsetOf(Model.Anchor), OffsetOf(Model.Caret));
        return [new TextBlockTextRange(this, Math.Min(a, b), Math.Max(a, b))];
    }

    public ITextRangeProvider[] GetVisibleRanges() => [DocumentRange];

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) => DocumentRange;

    public ITextRangeProvider RangeFromPoint(Point screenLocation) => CaretRange();

    private TextBlockTextRange CaretRange()
    {
        var caret = OffsetOf(Model.Caret);
        return new TextBlockTextRange(this, caret, caret);
    }
}

/// <summary>Диапазон текста блока в смещениях "\n"-текста.</summary>
internal sealed class TextBlockTextRange(TextBlockCanvasAutomationPeer peer, int start, int end) : ITextRangeProvider
{
    private int _start = start;
    private int _end = end;

    private string Text => peer.Text;

    public ITextRangeProvider Clone() => new TextBlockTextRange(peer, _start, _end);

    public bool Compare(ITextRangeProvider range) =>
        range is TextBlockTextRange other && other._start == _start && other._end == _end;

    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        if (targetRange is not TextBlockTextRange other) throw new ArgumentException("Чужой диапазон.", nameof(targetRange));
        return Endpoint(endpoint).CompareTo(other.Endpoint(targetEndpoint));
    }

    public void ExpandToEnclosingUnit(TextUnit unit)
    {
        var text = Text;
        _start = Math.Clamp(_start, 0, text.Length);
        switch (unit)
        {
            case TextUnit.Character:
                _end = Math.Min(_start + 1, text.Length);
                break;
            case TextUnit.Format:
            case TextUnit.Word:
                while (_start > 0 && !char.IsWhiteSpace(text[_start - 1])) _start--;
                _end = _start;
                while (_end < text.Length && !char.IsWhiteSpace(text[_end])) _end++;
                while (_end < text.Length && text[_end] == ' ') _end++;
                break;
            case TextUnit.Line:
            case TextUnit.Paragraph:
                while (_start > 0 && text[_start - 1] != '\n') _start--;
                _end = text.IndexOf('\n', _start) is var next and >= 0 ? next + 1 : text.Length;
                break;
            default:
                _start = 0;
                _end = text.Length;
                break;
        }
    }

    public ITextRangeProvider? FindAttribute(int attribute, object value, bool backward) => null;

    public ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase)
    {
        var slice = Text[_start.._end];
        var comparison = ignoreCase ? StringComparison.CurrentCultureIgnoreCase : StringComparison.CurrentCulture;
        var at = backward ? slice.LastIndexOf(text, comparison) : slice.IndexOf(text, comparison);
        return at < 0 ? null : new TextBlockTextRange(peer, _start + at, _start + at + text.Length);
    }

    public object GetAttributeValue(int attribute) => AutomationElementIdentifiers.NotSupported;

    public double[] GetBoundingRectangles() => [];

    public IRawElementProviderSimple GetEnclosingElement() => peer.Provider();

    public string GetText(int maxLength)
    {
        var text = Text;
        var slice = text[Math.Clamp(_start, 0, text.Length)..Math.Clamp(_end, 0, text.Length)];
        return maxLength >= 0 && slice.Length > maxLength ? slice[..maxLength] : slice;
    }

    public int Move(TextUnit unit, int count)
    {
        var degenerate = _start == _end;
        var moved = MoveOffset(ref _start, unit, count);
        _end = _start;
        if (!degenerate) ExpandToEnclosingUnit(unit);
        return moved;
    }

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
    {
        var offset = Endpoint(endpoint);
        var moved = MoveOffset(ref offset, unit, count);
        SetEndpoint(endpoint, offset);
        return moved;
    }

    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        if (targetRange is not TextBlockTextRange other) throw new ArgumentException("Чужой диапазон.", nameof(targetRange));
        SetEndpoint(endpoint, other.Endpoint(targetEndpoint));
    }

    public void Select() => peer.Select(_start, _end);

    public void AddToSelection() => throw new InvalidOperationException("Выделение у блока одно.");

    public void RemoveFromSelection() => throw new InvalidOperationException("Выделение у блока одно.");

    public void ScrollIntoView(bool alignToTop) { }

    public IRawElementProviderSimple[] GetChildren() => [];

    private int Endpoint(TextPatternRangeEndpoint endpoint) => endpoint == TextPatternRangeEndpoint.Start ? _start : _end;

    private void SetEndpoint(TextPatternRangeEndpoint endpoint, int offset)
    {
        if (endpoint == TextPatternRangeEndpoint.Start)
        {
            _start = offset;
            if (_end < _start) _end = _start;
        }
        else
        {
            _end = offset;
            if (_start > _end) _start = _end;
        }
    }

    /// <summary>Сдвиг позиции на count единиц; возвращает, на сколько удалось.</summary>
    private int MoveOffset(ref int offset, TextUnit unit, int count)
    {
        var text = Text;
        var moved = 0;
        var step = Math.Sign(count);
        while (moved != count)
        {
            var next = unit switch
            {
                TextUnit.Character => offset + step,
                TextUnit.Format or TextUnit.Word => NextBoundary(text, offset, step, char.IsWhiteSpace),
                TextUnit.Line or TextUnit.Paragraph => NextBoundary(text, offset, step, c => c == '\n'),
                _ => step > 0 ? text.Length : 0,
            };
            next = Math.Clamp(next, 0, text.Length);
            if (next == offset) break;
            offset = next;
            moved += step;
        }
        return moved;
    }

    private static int NextBoundary(string text, int offset, int step, Func<char, bool> separator)
    {
        if (step > 0)
        {
            var i = offset;
            while (i < text.Length && !separator(text[i])) i++;
            while (i < text.Length && separator(text[i])) i++;
            return i;
        }
        var j = offset;
        while (j > 0 && separator(text[j - 1])) j--;
        while (j > 0 && !separator(text[j - 1])) j--;
        return j;
    }
}
