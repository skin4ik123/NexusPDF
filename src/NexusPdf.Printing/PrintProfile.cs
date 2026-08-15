namespace NexusPdf.Printing;

/// <summary>
/// Сохранённый набор настроек печати.
///
/// Это НЕ сериализация <see cref="LayoutSettings"/> целиком, а отдельная
/// плоская запись, и так сделано намеренно: профиль обязан хранить ровно то,
/// что перечислено, и физически не может унести с собой пароль документа,
/// PIN защищённой печати, путь к файлу или содержимое формы — их тут просто
/// нет полями.
/// </summary>
public sealed record PrintProfile
{
    public required string Name { get; init; }

    /// <summary>Встроенный профиль нельзя изменить или удалить.</summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>Привязка к принтеру; пустая строка — профиль общий.</summary>
    public string PrinterName { get; init; } = "";

    /// <summary>Имя формата бумаги; пустая строка — оставить текущий.</summary>
    public string PaperName { get; init; } = "";

    public ImpositionMode Imposition { get; init; } = ImpositionMode.Single;
    public SizeMode Size { get; init; } = SizeMode.ShrinkOversized;
    public double CustomScale { get; init; } = 1.0;
    public bool AllowEnlarge { get; init; }
    public PagePosition Position { get; init; } = PagePosition.Center;
    public OrientationMode Orientation { get; init; } = OrientationMode.Automatic;

    public int NUpRows { get; init; } = 2;
    public int NUpColumns { get; init; } = 2;
    public NUpOrder NUpOrder { get; init; } = NUpOrder.RowsLeftToRight;
    public double NUpGapPt { get; init; } = 8;

    public double PosterScale { get; init; } = 1.0;
    public double PosterOverlapPt { get; init; } = 14.17;

    public int SignatureSize { get; init; }
    public bool CompensateCreep { get; init; }

    public DuplexMode Duplex { get; init; } = DuplexMode.Simplex;
    public ColorMode Color { get; init; } = ColorMode.Color;
    public AnnotationPolicy Annotations { get; init; } = AnnotationPolicy.PrintableAnnotations;
    public FormPolicy Forms { get; init; } = FormPolicy.WithValues;
    public LayerPolicy Layers { get; init; } = LayerPolicy.CurrentView;
    public bool PrintAsImage { get; init; }

    public PrinterMarks Marks { get; init; } = PrinterMarks.None;
    public double BleedPt { get; init; }
    public double UserMarginPt { get; init; }

    /// <summary>
    /// Объём страниц НЕ сохраняется намеренно: профиль «Черновик», молча
    /// печатающий вчерашний диапазон вместо всего документа, — это ловушка.
    /// </summary>
    public PageParity Parity { get; init; } = PageParity.All;

    public LayoutSettings ToSettings() => new()
    {
        Imposition = Imposition,
        Size = Size,
        CustomScale = CustomScale,
        AllowEnlarge = AllowEnlarge,
        Position = Position,
        Orientation = Orientation,
        Duplex = Duplex,
        Color = Color,
        Annotations = Annotations,
        Forms = Forms,
        Layers = Layers,
        PrintAsImage = PrintAsImage,
        UserMarginsPt = MarginsPt.Uniform(UserMarginPt),
        NUp = new NUpSettings
        {
            Rows = NUpRows,
            Columns = NUpColumns,
            Order = NUpOrder,
            HorizontalGapPt = NUpGapPt,
            VerticalGapPt = NUpGapPt,
        },
        Poster = new PosterSettings { Scale = PosterScale, OverlapPt = PosterOverlapPt },
        Booklet = new BookletSettings { SignatureSize = SignatureSize, CompensateCreep = CompensateCreep },
        Marks = new MarkSettings { Marks = Marks, BleedPt = BleedPt },
    };

    public static PrintProfile FromSettings(string name, LayoutSettings settings,
        string printerName = "", string paperName = "") => new()
    {
        Name = name,
        PrinterName = printerName,
        PaperName = paperName,
        Imposition = settings.Imposition,
        Size = settings.Size,
        CustomScale = settings.CustomScale,
        AllowEnlarge = settings.AllowEnlarge,
        Position = settings.Position,
        Orientation = settings.Orientation,
        NUpRows = settings.NUp.Rows,
        NUpColumns = settings.NUp.Columns,
        NUpOrder = settings.NUp.Order,
        NUpGapPt = settings.NUp.HorizontalGapPt,
        PosterScale = settings.Poster.Scale,
        PosterOverlapPt = settings.Poster.OverlapPt,
        SignatureSize = settings.Booklet.SignatureSize,
        CompensateCreep = settings.Booklet.CompensateCreep,
        Duplex = settings.Duplex,
        Color = settings.Color,
        Annotations = settings.Annotations,
        Forms = settings.Forms,
        Layers = settings.Layers,
        PrintAsImage = settings.PrintAsImage,
        Marks = settings.Marks.Marks,
        BleedPt = settings.Marks.BleedPt,
        UserMarginPt = settings.UserMarginsPt.MaxPt,
    };
}

/// <summary>Встроенные профили: то, что нужно чаще всего, без настройки.</summary>
public static class BuiltInPrintProfiles
{
    public static IReadOnlyList<PrintProfile> All { get; } = new[]
    {
        new PrintProfile { Name = "По умолчанию", IsBuiltIn = true },

        new PrintProfile
        {
            Name = "Фактический размер", IsBuiltIn = true,
            Size = SizeMode.ActualSize, Orientation = OrientationMode.Portrait,
        },
        new PrintProfile
        {
            Name = "Вписать в лист", IsBuiltIn = true,
            Size = SizeMode.Fit,
        },
        new PrintProfile
        {
            Name = "Двусторонняя", IsBuiltIn = true,
            Duplex = DuplexMode.LongEdge,
        },
        new PrintProfile
        {
            Name = "2 страницы на лист", IsBuiltIn = true,
            Imposition = ImpositionMode.NUp, NUpRows = 1, NUpColumns = 2,
        },
        new PrintProfile
        {
            Name = "4 страницы на лист", IsBuiltIn = true,
            Imposition = ImpositionMode.NUp, NUpRows = 2, NUpColumns = 2,
        },
        new PrintProfile
        {
            Name = "Буклет", IsBuiltIn = true,
            Imposition = ImpositionMode.Booklet, Duplex = DuplexMode.LongEdge,
        },
        new PrintProfile
        {
            Name = "Плакат", IsBuiltIn = true,
            Imposition = ImpositionMode.Poster,
        },
        new PrintProfile
        {
            Name = "Черновик, оттенки серого", IsBuiltIn = true,
            Color = ColorMode.Grayscale, Size = SizeMode.Fit,
        },
        new PrintProfile
        {
            Name = "Только документ, без аннотаций", IsBuiltIn = true,
            Annotations = AnnotationPolicy.DocumentOnly,
        },
        new PrintProfile
        {
            Name = "Печатать как изображение", IsBuiltIn = true,
            PrintAsImage = true,
        },
        new PrintProfile
        {
            Name = "Типография: метки и вылет", IsBuiltIn = true,
            Size = SizeMode.ActualSize,
            Marks = PrinterMarks.CropMarks | PrinterMarks.RegistrationMarks
                  | PrinterMarks.BleedMarks | PrinterMarks.PageInformation,
            BleedPt = 8.5,
            UserMarginPt = 28.35,
        },
        new PrintProfile
        {
            Name = "Чертёж 100 %", IsBuiltIn = true,
            Size = SizeMode.ActualSize, Orientation = OrientationMode.Automatic,
        },
    };
}
