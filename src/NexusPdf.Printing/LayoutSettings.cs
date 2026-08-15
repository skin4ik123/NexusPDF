namespace NexusPdf.Printing;

/// <summary>Как страница вписывается в лист.</summary>
public enum SizeMode
{
    /// <summary>100 %: физический размер сохраняется, лишнее обрезается.</summary>
    ActualSize,

    /// <summary>Страница целиком помещается в печатаемую область.</summary>
    Fit,

    /// <summary>Помещающиеся печатаются 100 %, уменьшаются только слишком большие.</summary>
    ShrinkOversized,

    /// <summary>Заданный пользователем масштаб.</summary>
    CustomScale,

    /// <summary>Заполнить печатаемую область целиком, обрезав выступающее.</summary>
    FillSheet,
}

/// <summary>Куда прижимается страница на листе.</summary>
public enum PagePosition
{
    TopLeft, TopCenter, TopRight,
    MiddleLeft, Center, MiddleRight,
    BottomLeft, BottomCenter, BottomRight,
    Custom,
}

/// <summary>Ориентация листа.</summary>
public enum OrientationMode
{
    /// <summary>Выбрать ту, при которой страница займёт больше места.</summary>
    Automatic,
    Portrait,
    Landscape,
}

/// <summary>Какой размер бумаги брать для страниц документа.</summary>
public enum PaperSelectionMode
{
    /// <summary>Один выбранный размер на всё задание.</summary>
    Fixed,

    /// <summary>Под каждую страницу подбирается ближайший поддерживаемый размер.</summary>
    AutoPerPage,
}

/// <summary>Режим раскладки листа.</summary>
public enum ImpositionMode
{
    /// <summary>Одна страница на лист.</summary>
    Single,

    /// <summary>Несколько страниц на листе сеткой.</summary>
    NUp,

    /// <summary>Одна большая страница на нескольких листах.</summary>
    Poster,

    /// <summary>Буклет со сложением сигнатур.</summary>
    Booklet,
}

/// <summary>Сетка и оформление режима «несколько страниц на листе».</summary>
public sealed record NUpSettings
{
    public int Rows { get; init; } = 2;
    public int Columns { get; init; } = 1;
    public NUpOrder Order { get; init; } = NUpOrder.RowsLeftToRight;

    /// <summary>Промежуток между ячейками.</summary>
    public double HorizontalGapPt { get; init; }
    public double VerticalGapPt { get; init; }

    /// <summary>Поля от края печатаемой области до сетки.</summary>
    public MarginsPt OuterMarginsPt { get; init; } = MarginsPt.Zero;

    /// <summary>Рамка вокруг каждой страницы — помогает при резке.</summary>
    public bool DrawPageBorders { get; init; }

    /// <summary>Поворачивать страницу, если так она займёт в ячейке больше места.</summary>
    public bool AutoRotatePages { get; init; } = true;

    /// <summary>
    /// Один масштаб на все ячейки листа. Иначе страницы разного размера
    /// напечатаются разными по величине, что для конспекта обычно не нужно.
    /// </summary>
    public bool UniformScale { get; init; } = true;

    public int CellsPerSheet => Math.Max(1, Rows) * Math.Max(1, Columns);
}

/// <summary>Разбивка большой страницы на плитки.</summary>
public sealed record PosterSettings
{
    /// <summary>Масштаб исходной страницы; 1.0 — фактический размер.</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>Перекрытие соседних плиток для склейки.</summary>
    public double OverlapPt { get; init; } = 14.17; // 5 мм

    public bool DrawCutLines { get; init; } = true;
    public bool DrawTileLabels { get; init; } = true;

    /// <summary>Не печатать плитки, на которых нет содержимого.</summary>
    public bool SkipEmptyTiles { get; init; } = true;

    /// <summary>Плитки, снятые пользователем с печати; ключ — «столбец,строка» с нуля.</summary>
    public IReadOnlySet<(int Column, int Row)> ExcludedTiles { get; init; }
        = new HashSet<(int, int)>();
}

/// <summary>Порядок и оформление буклета.</summary>
public sealed record BookletSettings
{
    /// <summary>Переплёт слева — обычная европейская книга.</summary>
    public bool BindOnLeft { get; init; } = true;

    /// <summary>
    /// Страниц в одной сигнатуре, кратно четырём. 0 — весь документ одной
    /// сигнатурой (подходит для тонких брошюр).
    /// </summary>
    public int SignatureSize { get; init; }

    /// <summary>Компенсация выползания внешних листов при сложении.</summary>
    public bool CompensateCreep { get; init; }

    /// <summary>Толщина листа бумаги — из неё считается выползание.</summary>
    public double PaperThicknessPt { get; init; } = 0.28; // ~0.1 мм

    /// <summary>Дополнительное поле у сгиба.</summary>
    public double GutterPt { get; init; }
}

/// <summary>Всё, что задаёт пользователь для раскладки задания.</summary>
public sealed record LayoutSettings
{
    public ImpositionMode Imposition { get; init; } = ImpositionMode.Single;

    public SizeMode Size { get; init; } = SizeMode.ShrinkOversized;

    /// <summary>Масштаб для SizeMode.CustomScale, где 1.0 — 100 %.</summary>
    public double CustomScale { get; init; } = 1.0;

    /// <summary>Разрешить увеличивать страницы меньше листа в режиме Fit.</summary>
    public bool AllowEnlarge { get; init; }

    public PagePosition Position { get; init; } = PagePosition.Center;
    public double CustomOffsetXPt { get; init; }
    public double CustomOffsetYPt { get; init; }

    public OrientationMode Orientation { get; init; } = OrientationMode.Automatic;

    /// <summary>Дополнительный поворот содержимого, кратный 90.</summary>
    public int ExtraRotationDegrees { get; init; }

    public PageBoxKind Box { get; init; } = PageBoxKind.CropBox;

    public PaperSelectionMode PaperSelection { get; init; } = PaperSelectionMode.Fixed;

    /// <summary>Пользовательские поля поверх непечатаемых полей принтера.</summary>
    public MarginsPt UserMarginsPt { get; init; } = MarginsPt.Zero;

    public NUpSettings NUp { get; init; } = new();
    public PosterSettings Poster { get; init; } = new();
    public BookletSettings Booklet { get; init; } = new();

    public DuplexMode Duplex { get; init; } = DuplexMode.Simplex;

    public AnnotationPolicy Annotations { get; init; } = AnnotationPolicy.PrintableAnnotations;
    public FormPolicy Forms { get; init; } = FormPolicy.WithValues;
    public LayerPolicy Layers { get; init; } = LayerPolicy.CurrentView;
    public ColorMode Color { get; init; } = ColorMode.Color;

    /// <summary>Печатать всё растром — режим совместимости с проблемными драйверами.</summary>
    public bool PrintAsImage { get; init; }

    /// <summary>Типографские метки и вылет.</summary>
    public MarkSettings Marks { get; init; } = new();

    /// <summary>Надписи, добавляемые только в печатное задание.</summary>
    public IReadOnlyList<PrintOverlay> Overlays { get; init; } = Array.Empty<PrintOverlay>();

    /// <summary>Калибровка под конкретный принтер: поправки масштаба и смещения.</summary>
    public double CalibrationScaleX { get; init; } = 1.0;
    public double CalibrationScaleY { get; init; } = 1.0;
    public double CalibrationOffsetXPt { get; init; }
    public double CalibrationOffsetYPt { get; init; }
}

/// <summary>Одна страница документа на входе раскладки.</summary>
public sealed record SourcePage(
    string DocumentId,
    int PageIndex,
    SizePt SizePt,
    string? Label = null,
    int InherentRotationDegrees = 0);
