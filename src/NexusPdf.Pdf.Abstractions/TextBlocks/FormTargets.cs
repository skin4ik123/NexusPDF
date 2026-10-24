using System.Runtime.CompilerServices;

namespace NexusPdf.Pdf.Abstractions.TextBlocks;

/// <summary>Что в бланке под щелчком.</summary>
public enum FormTargetKind { Cell, CaptionCell, Line, TextBlank }

/// <summary>
/// Место для ввода в бланке. AreaPt — куда ляжет текст (у клетки с подписью —
/// её свободная часть, у строки — от щелчка до конца линии); CellPt — вся
/// клетка или вся строка. TableId/Row/Column — место клетки в таблице для Tab
/// (у строк и пропусков TableId = −1). Координаты — отображаемые пункты, y вниз.
/// </summary>
public sealed record FormTarget(FormTargetKind Kind, OverlayBox AreaPt, OverlayBox CellPt, int TableId, int Row, int Column);

/// <summary>
/// Поиск мест для ввода в бланке по линиям и тексту страницы. Клетка —
/// наименьший замкнутый прямоугольник из горизонтальных и вертикальных линий
/// вокруг точки: куски одной линии склеиваются (зазор до 2 пт, сдвиг до
/// 1,5 пт), сторона считается замкнутой, если линии покрывают 90 % её длины.
/// Индекс строится один раз на страницу; поиск — без перебора всех линий.
/// </summary>
public sealed class FormTargetIndex
{
    private const double SnapPt = 1.5;
    private const double GapPt = 2;
    private const double Coverage = 0.9;
    private const double MinCellPt = 6;
    // Рамка вокруг всей страницы — не клетка бланка: по щелчку в пустом месте
    // листа блок размером с лист был бы ошибкой.
    private const double MaxCellHeightPt = 250;
    private const double MinLinePt = 20;
    private const double CaptionGapPt = 2;
    private const double MinFreeWidthPt = 10;
    private const double MinFreeHeightPt = 6;
    private const int CandidatesPerSide = 4;
    private const int MaxTableSegments = 5000;

    private readonly Segment[] _h;
    private readonly Segment[] _v;
    private readonly Cluster[] _hClusters;
    private readonly Cluster[] _vClusters;
    private readonly IReadOnlyList<PageTextObject> _texts;
    private readonly List<OverlayBox> _blanks;
    private readonly double _fontSize;
    private readonly Dictionary<int, Table> _tables = [];
    private readonly Dictionary<int, int> _tableOfSegment = [];

    /// <summary>Склеенный кусок линии. Номер — сквозной: сначала горизонтальные, потом вертикальные.</summary>
    private readonly record struct Segment(double Position, double Start, double End, int Cluster);

    /// <summary>Все куски на одной высоте (или абсциссе): First…First+Count в массиве кусков.</summary>
    private readonly record struct Cluster(double Position, int First, int Count);

    /// <summary>Таблица — связные линии; Ys/Xs — её различимые строки и колонки.</summary>
    private sealed record Table(int Id, double[] Ys, double[] Xs)
    {
        public List<FormTarget>? Cells { get; set; }
    }

    private FormTargetIndex(PageTextLayout layout, double defaultFontSizePt)
    {
        _fontSize = defaultFontSizePt > 0 ? defaultFontSizePt : 11;
        _texts = layout.Objects.Where(o => !o.Invisible && !string.IsNullOrWhiteSpace(o.Text)).ToList();
        (_h, _hClusters) = Merge(layout.Rulings.Where(r => r.Horizontal));
        (_v, _vClusters) = Merge(layout.Rulings.Where(r => !r.Horizontal));
        _blanks = FindBlanks(_texts);
    }

    public static FormTargetIndex Build(PageTextLayout layout, double defaultFontSizePt) => new(layout, defaultFontSizePt);

    /// <summary>Место для ввода под точкой: пропуск «____», клетка, строка-подчёркивание — или null.</summary>
    public FormTarget? At(double xPt, double yPt)
    {
        foreach (var blank in _blanks)
        {
            if (Contains(blank, xPt, yPt, 1))
                return new FormTarget(FormTargetKind.TextBlank, blank, blank, -1, 0, 0);
        }
        if (CellAt(xPt, yPt) is { } cell)
            return Describe(cell.Box, cell.Top, xPt, yPt);
        return LineAt(xPt, yPt);
    }

    /// <summary>Клетка, в которой целиком лежит область, или null.</summary>
    public OverlayBox? CellAround(OverlayBox area)
    {
        if (CellAt(area.XPt + area.WidthPt / 2, area.YPt + area.HeightPt / 2) is not { } cell)
            return null;
        var box = cell.Box;
        return area.XPt >= box.XPt - GapPt && area.YPt >= box.YPt - GapPt &&
               area.XPt + area.WidthPt <= box.XPt + box.WidthPt + GapPt &&
               area.YPt + area.HeightPt <= box.YPt + box.HeightPt + GapPt
            ? box
            : null;
    }

    /// <summary>Следующая (или предыдущая) клетка для ввода той же таблицы: слева направо, сверху вниз; заполненные пропускаются.</summary>
    public FormTarget? Next(FormTarget from, bool backwards)
    {
        if (from.Kind is not (FormTargetKind.Cell or FormTargetKind.CaptionCell) ||
            !_tables.TryGetValue(from.TableId, out var table))
            return null;
        var cells = table.Cells ??= CellsOf(table);
        var at = cells.FindIndex(c => Same(c.CellPt, from.CellPt));
        if (at < 0)
            return null;
        var next = at + (backwards ? -1 : 1);
        return next >= 0 && next < cells.Count ? cells[next] : null;
    }

    // ----- клетки -----

    // Первый поиск на странице идёт сразу после её открытия — без полной
    // оптимизации циклы на десятках тысяч линий работали бы в разы медленнее.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private (OverlayBox Box, int Top)? CellAt(double x, double y)
    {
        var tops = Candidates(_h, _hClusters, y, x, up: true);
        var bottoms = Candidates(_h, _hClusters, y, x, up: false);
        var lefts = Candidates(_v, _vClusters, x, y, up: true);
        var rights = Candidates(_v, _vClusters, x, y, up: false);
        if (tops.Count == 0 || bottoms.Count == 0 || lefts.Count == 0 || rights.Count == 0)
            return null;

        (OverlayBox Box, int Top)? best = null;
        var bestArea = double.MaxValue;
        foreach (var top in tops)
        foreach (var bottom in bottoms)
        foreach (var left in lefts)
        foreach (var right in rights)
        {
            var (t, b) = (_h[top].Position, _h[bottom].Position);
            var (l, r) = (_v[left].Position, _v[right].Position);
            var area = (b - t) * (r - l);
            if (area >= bestArea || b - t < MinCellPt || r - l < MinCellPt || b - t > MaxCellHeightPt)
                continue;
            if (Covered(_h, _hClusters[_h[top].Cluster], l, r) && Covered(_h, _hClusters[_h[bottom].Cluster], l, r) &&
                Covered(_v, _vClusters[_v[left].Cluster], t, b) && Covered(_v, _vClusters[_v[right].Cluster], t, b))
            {
                best = (new OverlayBox(l, t, r - l, b - t), top);
                bestArea = area;
            }
        }
        return best;
    }

    /// <summary>
    /// Ближайшие с одной стороны от точки куски линий, проходящие через
    /// вторую координату точки: по одному на высоту, не больше четырёх.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static List<int> Candidates(Segment[] segments, Cluster[] clusters, double at, double across, bool up)
    {
        var found = new List<int>(CandidatesPerSide);
        var i = LowerBound(clusters, at);
        for (var c = up ? i - 1 : i; c >= 0 && c < clusters.Length && found.Count < CandidatesPerSide; c += up ? -1 : 1)
        {
            var cluster = clusters[c];
            if (up ? cluster.Position >= at : cluster.Position <= at)
                continue;
            for (var k = cluster.First; k < cluster.First + cluster.Count; k++)
            {
                if (segments[k].Start - GapPt <= across && across <= segments[k].End + GapPt)
                {
                    found.Add(k);
                    break;
                }
            }
        }
        return found;
    }

    /// <summary>Покрывают ли куски линии на этой высоте 90 % отрезка from…to.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Covered(Segment[] segments, Cluster cluster, double from, double to)
    {
        var covered = 0.0;
        for (var k = cluster.First; k < cluster.First + cluster.Count; k++)
            covered += Math.Max(0, Math.Min(segments[k].End, to) - Math.Max(segments[k].Start, from));
        return covered >= (to - from) * Coverage;
    }

    /// <summary>Клетка — пустая, с подписью (свободная часть) или заполненная (null).</summary>
    private FormTarget? Describe(OverlayBox cell, int topSegment, double x, double y)
    {
        var table = TableOf(topSegment);
        var (row, column) = table == null
            ? (0, 0)
            : (Nearest(table.Ys, cell.YPt), Nearest(table.Xs, cell.XPt));
        var id = table?.Id ?? -1;

        var inside = _texts.Where(t => Contains(cell, t.BoundsPt.XPt + t.BoundsPt.WidthPt / 2,
            t.BoundsPt.YPt + t.BoundsPt.HeightPt / 2, -0.5)).ToList();
        if (inside.Count == 0)
            return new FormTarget(FormTargetKind.Cell, cell, cell, id, row, column);
        if (FreePart(cell, inside, x, y) is { } free)
            return new FormTarget(FormTargetKind.CaptionCell, free, cell, id, row, column);
        return null;
    }

    /// <summary>
    /// Свободная часть клетки с подписью: справа от подписи или под ней — с
    /// той стороны, где щелчок; щелчок по самой подписи — бо́льшая из двух.
    /// Подпись — не больше двух строк, иначе клетка уже заполнена.
    /// </summary>
    private static OverlayBox? FreePart(OverlayBox cell, List<PageTextObject> inside, double x, double y)
    {
        var baselines = inside.Select(t => t.OriginYPt).OrderBy(v => v).ToList();
        var lines = 1;
        for (var i = 1; i < baselines.Count; i++)
        {
            if (baselines[i] - baselines[i - 1] > 0.3 * inside[0].FontSizePt)
                lines++;
        }
        if (lines > 2)
            return null;

        var captionRight = inside.Max(t => t.BoundsPt.XPt + t.BoundsPt.WidthPt);
        var captionBottom = inside.Max(t => t.BoundsPt.YPt + t.BoundsPt.HeightPt);
        var cellRight = cell.XPt + cell.WidthPt;
        var cellBottom = cell.YPt + cell.HeightPt;
        OverlayBox? right = cellRight - captionRight - CaptionGapPt >= MinFreeWidthPt
            ? new OverlayBox(captionRight + CaptionGapPt, cell.YPt, cellRight - captionRight - CaptionGapPt, cell.HeightPt)
            : null;
        OverlayBox? below = cellBottom - captionBottom - CaptionGapPt >= MinFreeHeightPt
            ? new OverlayBox(cell.XPt, captionBottom + CaptionGapPt, cell.WidthPt, cellBottom - captionBottom - CaptionGapPt)
            : null;

        if (y > captionBottom && below != null) return below;
        if (x > captionRight && right != null) return right;
        if (right == null) return below;
        if (below == null) return right;
        return right.Value.WidthPt * right.Value.HeightPt >= below.Value.WidthPt * below.Value.HeightPt ? right : below;
    }

    // ----- таблицы -----

    /// <summary>
    /// Таблица, в которую входит кусок линии: все связанные с ним линии
    /// (поиск в ширину, одна таблица — один раз). Слишком большая сетка (чертёж,
    /// карта) таблицей для Tab не считается.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private Table? TableOf(int horizontalSegment)
    {
        if (_tableOfSegment.TryGetValue(horizontalSegment, out var known))
            return _tables.GetValueOrDefault(known);

        var seen = new HashSet<int> { horizontalSegment };
        var queue = new Queue<int>();
        queue.Enqueue(horizontalSegment);
        var overflow = false;
        while (queue.Count > 0 && !overflow)
        {
            var node = queue.Dequeue();
            var horizontal = node < _h.Length;
            var self = horizontal ? _h[node] : _v[node - _h.Length];
            var others = horizontal ? _v : _h;
            var offset = horizontal ? _h.Length : 0;
            var clusters = horizontal ? _vClusters : _hClusters;
            for (var c = LowerBound(clusters, self.Start - GapPt); c < clusters.Length && clusters[c].Position <= self.End + GapPt; c++)
            {
                for (var k = clusters[c].First; k < clusters[c].First + clusters[c].Count; k++)
                {
                    if (others[k].Start - GapPt > self.Position || self.Position > others[k].End + GapPt)
                        continue;
                    if (seen.Add(k + offset))
                    {
                        if (seen.Count > MaxTableSegments) { overflow = true; break; }
                        queue.Enqueue(k + offset);
                    }
                }
                if (overflow) break;
            }
        }

        var id = seen.Min();
        foreach (var node in seen) _tableOfSegment[node] = overflow ? -1 : id;
        if (overflow)
            return null;
        var ys = Distinct(seen.Where(n => n < _h.Length).Select(n => _h[n].Position));
        var xs = Distinct(seen.Where(n => n >= _h.Length).Select(n => _v[n - _h.Length].Position));
        var table = new Table(id, ys, xs);
        _tables[id] = table;
        return table;
    }

    /// <summary>Клетки таблицы для ввода по порядку Tab: по одной на каждый промежуток сетки, без повторов и заполненных.</summary>
    private List<FormTarget> CellsOf(Table table)
    {
        var cells = new List<FormTarget>();
        for (var r = 0; r + 1 < table.Ys.Length; r++)
        for (var c = 0; c + 1 < table.Xs.Length; c++)
        {
            var x = (table.Xs[c] + table.Xs[c + 1]) / 2;
            var y = (table.Ys[r] + table.Ys[r + 1]) / 2;
            if (CellAt(x, y) is not { } cell || cells.Any(known => Same(known.CellPt, cell.Box)))
                continue;
            if (Describe(cell.Box, cell.Top, cell.Box.XPt + cell.Box.WidthPt, cell.Box.YPt + cell.Box.HeightPt) is { } target &&
                target.TableId == table.Id)
                cells.Add(target);
        }
        return cells.OrderBy(c => Math.Round(c.CellPt.YPt)).ThenBy(c => c.CellPt.XPt).ToList();
    }

    // ----- строки и пропуски -----

    /// <summary>Строка-подчёркивание: горизонтальная линия от 20 пт, над которой нет текста.</summary>
    private FormTarget? LineAt(double x, double y)
    {
        var band = 1.5 * _fontSize;
        var c = LowerBound(_hClusters, y - 3);
        for (; c < _hClusters.Length && _hClusters[c].Position <= y + band; c++)
        {
            var cluster = _hClusters[c];
            if (y < cluster.Position - band || y > cluster.Position + 3)
                continue;
            for (var k = cluster.First; k < cluster.First + cluster.Count; k++)
            {
                var line = _h[k];
                if (line.End - line.Start < MinLinePt || x < line.Start - GapPt || x > line.End)
                    continue;
                var whole = new OverlayBox(line.Start, line.Position - band, line.End - line.Start, band - 1);
                if (_texts.Any(t => Overlaps(t.BoundsPt, whole)))
                    continue;
                var left = Math.Max(x, line.Start);
                return new FormTarget(FormTargetKind.Line,
                    new OverlayBox(left, line.Position - band, line.End - left, band - 1), whole, -1, 0, 0);
            }
        }
        return null;
    }

    /// <summary>
    /// Пропуски, набранные символами: три и больше «_» или четыре и больше «.»
    /// подряд (три точки — обычное многоточие во фразе). Рамка — по ширинам букв.
    /// </summary>
    private static List<OverlayBox> FindBlanks(IReadOnlyList<PageTextObject> texts)
    {
        var blanks = new List<OverlayBox>();
        foreach (var t in texts)
        {
            if (Math.Abs(t.AngleDeg) > 1 || t.CharAdvancesPt.Count != t.Text.Length)
                continue;
            var i = 0;
            while (i < t.Text.Length)
            {
                var ch = t.Text[i];
                var j = i;
                while (j < t.Text.Length && t.Text[j] == ch) j++;
                if ((ch == '_' && j - i >= 3) || (ch == '.' && j - i >= 4))
                {
                    var x = t.OriginXPt;
                    for (var k = 0; k < i; k++) x += t.CharAdvancesPt[k];
                    var width = 0.0;
                    for (var k = i; k < j; k++) width += t.CharAdvancesPt[k];
                    blanks.Add(new OverlayBox(x, t.OriginYPt - t.FontSizePt, width, 1.25 * t.FontSizePt));
                }
                i = j;
            }
        }
        return blanks;
    }

    // ----- общее -----

    /// <summary>
    /// Склейка линий: близкие по положению (до 1,5 пт) — одна высота, на ней
    /// куски с зазором до 2 пт — один кусок.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static (Segment[] Segments, Cluster[] Clusters) Merge(IEnumerable<PageRuling> rulings)
    {
        var pieces = new List<Piece>();
        foreach (var r in rulings)
        {
            if (double.IsFinite(r.PositionPt) && double.IsFinite(r.StartPt) && double.IsFinite(r.EndPt))
                pieces.Add(new Piece(r.PositionPt, Math.Min(r.StartPt, r.EndPt), Math.Max(r.StartPt, r.EndPt)));
        }
        var sorted = pieces.ToArray();
        Array.Sort(sorted, PieceByPosition.Instance);

        var segments = new List<Segment>(sorted.Length);
        var clusters = new List<Cluster>();
        var i = 0;
        while (i < sorted.Length)
        {
            var anchor = sorted[i].Position;
            var j = i;
            double weighted = 0, weight = 0;
            for (; j < sorted.Length && sorted[j].Position - anchor <= SnapPt; j++)
            {
                var w = sorted[j].End - sorted[j].Start + 1;
                weighted += sorted[j].Position * w;
                weight += w;
            }
            var position = weighted / weight;
            Array.Sort(sorted, i, j - i, PieceByStart.Instance);
            var first = segments.Count;
            for (var k = i; k < j; k++)
            {
                if (segments.Count > first && sorted[k].Start <= segments[^1].End + GapPt)
                    segments[^1] = segments[^1] with { End = Math.Max(segments[^1].End, sorted[k].End) };
                else
                    segments.Add(new Segment(position, sorted[k].Start, sorted[k].End, clusters.Count));
            }
            clusters.Add(new Cluster(position, first, segments.Count - first));
            i = j;
        }
        return (segments.ToArray(), clusters.ToArray());
    }

    private readonly record struct Piece(double Position, double Start, double End);

    private sealed class PieceByPosition : IComparer<Piece>
    {
        public static readonly PieceByPosition Instance = new();

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public int Compare(Piece a, Piece b) => a.Position.CompareTo(b.Position);
    }

    private sealed class PieceByStart : IComparer<Piece>
    {
        public static readonly PieceByStart Instance = new();

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public int Compare(Piece a, Piece b) => a.Start.CompareTo(b.Start);
    }

    /// <summary>Первая высота не меньше value.</summary>
    private static int LowerBound(Cluster[] clusters, double value)
    {
        var (lo, hi) = (0, clusters.Length);
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (clusters[mid].Position < value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static double[] Distinct(IEnumerable<double> positions)
    {
        var result = new List<double>();
        foreach (var p in positions.OrderBy(p => p))
        {
            if (result.Count == 0 || p - result[^1] > SnapPt) result.Add(p);
        }
        return result.ToArray();
    }

    private static int Nearest(double[] positions, double value)
    {
        var best = 0;
        for (var i = 1; i < positions.Length; i++)
        {
            if (Math.Abs(positions[i] - value) < Math.Abs(positions[best] - value)) best = i;
        }
        return best;
    }

    private static bool Contains(OverlayBox box, double x, double y, double margin) =>
        x >= box.XPt - margin && x <= box.XPt + box.WidthPt + margin &&
        y >= box.YPt - margin && y <= box.YPt + box.HeightPt + margin;

    private static bool Overlaps(OverlayBox a, OverlayBox b) =>
        a.XPt < b.XPt + b.WidthPt && b.XPt < a.XPt + a.WidthPt && a.YPt < b.YPt + b.HeightPt && b.YPt < a.YPt + a.HeightPt;

    private static bool Same(OverlayBox a, OverlayBox b) =>
        Math.Abs(a.XPt - b.XPt) < 1 && Math.Abs(a.YPt - b.YPt) < 1 &&
        Math.Abs(a.WidthPt - b.WidthPt) < 1 && Math.Abs(a.HeightPt - b.HeightPt) < 1;
}
