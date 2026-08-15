namespace NexusPdf.Printing;

/// <summary>Состояние принтера, как его сообщает драйвер.</summary>
public enum PrinterState
{
    /// <summary>Драйвер состояние не сообщил. Придумывать его нельзя.</summary>
    Unknown,
    Ready,
    Busy,
    Printing,
    Paused,
    Offline,
    PaperOut,
    PaperJam,
    DoorOpen,
    TonerLow,
    Error,
}

/// <summary>Тип подключения принтера — влияет и на выбор backend, и на подсказки.</summary>
public enum PrinterConnection
{
    Unknown,
    Local,
    Network,
    Virtual,
    RemoteDesktop,
}

/// <summary>Поддерживаемый принтером размер бумаги.</summary>
public sealed record PaperSizeOption(string Name, SizePt SizePt, string? DriverValue = null);

/// <summary>Лоток подачи.</summary>
public sealed record PaperSourceOption(string Name, string? DriverValue = null, bool IsManualFeed = false);

/// <summary>Тип носителя.</summary>
public sealed record MediaTypeOption(string Name, string? DriverValue = null);

/// <summary>Выходной лоток.</summary>
public sealed record OutputBinOption(string Name, string? DriverValue = null);

/// <summary>
/// Снимок возможностей принтера. Именно снимок: план печати считается по нему,
/// и если драйвер сменится, несоответствие будет видно, а не проявится браком
/// на бумаге.
/// </summary>
public sealed record PrinterCapabilities
{
    public required string PrinterName { get; init; }
    public string? DriverName { get; init; }
    public string? Location { get; init; }
    public string? PortName { get; init; }
    public PrinterConnection Connection { get; init; } = PrinterConnection.Unknown;
    public PrinterState State { get; init; } = PrinterState.Unknown;
    public bool IsDefault { get; init; }

    /// <summary>Виртуальный принтер вроде «Microsoft Print to PDF»: бумага не расходуется.</summary>
    public bool IsVirtual { get; init; }

    public IReadOnlyList<PaperSizeOption> PaperSizes { get; init; } = Array.Empty<PaperSizeOption>();
    public IReadOnlyList<PaperSourceOption> PaperSources { get; init; } = Array.Empty<PaperSourceOption>();
    public IReadOnlyList<MediaTypeOption> MediaTypes { get; init; } = Array.Empty<MediaTypeOption>();
    public IReadOnlyList<OutputBinOption> OutputBins { get; init; } = Array.Empty<OutputBinOption>();

    /// <summary>Разрешения печати в DPI, если драйвер их перечисляет.</summary>
    public IReadOnlyList<int> ResolutionsDpi { get; init; } = Array.Empty<int>();

    public bool SupportsColor { get; init; }
    public bool SupportsMonochrome { get; init; } = true;
    public bool SupportsDuplexLongEdge { get; init; }
    public bool SupportsDuplexShortEdge { get; init; }
    public bool SupportsCollation { get; init; }
    public bool SupportsStapling { get; init; }
    public bool SupportsHolePunch { get; init; }
    public bool SupportsBooklet { get; init; }
    public bool SupportsBorderless { get; init; }

    /// <summary>Максимум копий, который принимает драйвер; 1 — размножать придётся программе.</summary>
    public int MaxCopies { get; init; } = 1;

    /// <summary>Есть ли у принтера автоматический дуплекс любого вида.</summary>
    public bool SupportsAnyDuplex => SupportsDuplexLongEdge || SupportsDuplexShortEdge;

    /// <summary>
    /// Непечатаемые поля для конкретного размера бумаги. Драйверы сообщают их
    /// по-разному, поэтому величина хранится рядом с размером, а не одна на принтер.
    /// </summary>
    public IReadOnlyDictionary<string, MarginsPt> HardMarginsByPaper { get; init; }
        = new Dictionary<string, MarginsPt>();

    /// <summary>
    /// Печатаемая область для выбранной бумаги. Если драйвер полей не сообщил,
    /// возвращается весь лист — это честнее, чем выдумать «стандартные» 5 мм.
    /// </summary>
    public RectPt PrintableAreaFor(PaperSizeOption paper)
    {
        var margins = HardMarginsByPaper.TryGetValue(paper.Name, out var m) ? m : MarginsPt.Zero;
        return RectPt.FromSize(paper.SizePt).Deflate(margins);
    }

    public MarginsPt HardMarginsFor(PaperSizeOption paper) =>
        HardMarginsByPaper.TryGetValue(paper.Name, out var m) ? m : MarginsPt.Zero;

    /// <summary>Ближайший поддерживаемый размер бумаги под запрошенный, с учётом поворота.</summary>
    public PaperSizeOption? FindClosest(SizePt wanted, double tolerancePt = 4.0)
    {
        if (PaperSizes.Count == 0) return null;

        PaperSizeOption? best = null;
        var bestPenalty = double.MaxValue;
        foreach (var option in PaperSizes)
        {
            foreach (var candidate in new[] { option.SizePt, option.SizePt.Swapped })
            {
                // Бумага должна вмещать страницу: отрицательный запас штрафуется
                // сильнее, чем лишнее поле, иначе «ближайшим» окажется меньший лист.
                var dw = candidate.WidthPt - wanted.WidthPt;
                var dh = candidate.HeightPt - wanted.HeightPt;
                var penalty = (dw < -tolerancePt ? -dw * 10 : Math.Abs(dw))
                            + (dh < -tolerancePt ? -dh * 10 : Math.Abs(dh));
                if (penalty < bestPenalty)
                {
                    bestPenalty = penalty;
                    best = option;
                }
            }
        }
        return best;
    }

    /// <summary>Заглушка возможностей для тестов и для документа без выбранного принтера.</summary>
    public static PrinterCapabilities Unknown(string name = "") => new()
    {
        PrinterName = name,
        State = PrinterState.Unknown,
    };
}
