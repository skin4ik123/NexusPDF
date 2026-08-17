using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

/// <summary>
/// Обход объектов страницы СО СПУСКОМ внутрь Form XObject.
///
/// Зачем: половина программ, делающих PDF, заворачивает содержимое страницы в
/// форму — вложенный поток команд рисования. Обход только верхнего уровня на
/// таких документах не находит ни одного текстового объекта, хотя страница
/// полна текста. Именно поэтому правка текста «иногда не работала»: на самом
/// деле она не работала никогда, стоило документу быть устроенным так.
///
/// Адрес объекта — ПУТЬ, а не номер: [3] — четвёртый объект страницы, [3, 1] —
/// второй объект внутри него. Плоским номером вложенный объект не адресуется.
/// </summary>
internal static class PdfObjectTree
{
    // Коды из fpdf_edit.h: 0 unknown, 1 text, 2 path, 3 image, 4 shading, 5 form.
    private const int PageObjectText = 1;
    private const int PageObjectForm = 5;

    /// <summary>Матрица преобразования из системы координат объекта в систему страницы.</summary>
    internal readonly record struct Transform(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Transform Identity = new(1, 0, 0, 1, 0, 0);

        /// <summary>Сначала это преобразование, затем внешнее.</summary>
        public Transform Then(Transform outer) => new(
            A * outer.A + B * outer.C,
            A * outer.B + B * outer.D,
            C * outer.A + D * outer.C,
            C * outer.B + D * outer.D,
            E * outer.A + F * outer.C + outer.E,
            E * outer.B + F * outer.D + outer.F);

        public (double X, double Y) Apply(double x, double y) =>
            (A * x + C * y + E, B * x + D * y + F);
    }

    /// <summary>Найденный объект: сам объект, путь к нему и его место на странице.</summary>
    internal sealed record Node(FpdfPageobjectT Object, int[] Path, Transform ToPage);

    /// <summary>
    /// Все текстовые объекты страницы в порядке рисования, вложенные включительно.
    /// Порядок важен: при попадании нескольких объектов в одну точку выбирать
    /// надо нарисованный последним — он сверху.
    /// </summary>
    public static List<Node> TextObjects(FpdfPageT page)
    {
        var found = new List<Node>();
        Descend(
            index => fpdf_edit.FPDFPageGetObject(page, index),
            fpdf_edit.FPDFPageCountObjects(page),
            Array.Empty<int>(), Transform.Identity, found, depth: 0);
        return found;
    }

    /// <summary>Объект по пути или null, если путь больше никуда не ведёт.</summary>
    public static FpdfPageobjectT? Resolve(FpdfPageT page, IReadOnlyList<int> path)
    {
        if (path.Count == 0)
            return null;

        FpdfPageobjectT? current = null;
        for (var level = 0; level < path.Count; level++)
        {
            var index = path[level];
            if (index < 0)
                return null;

            if (level == 0)
            {
                if (index >= fpdf_edit.FPDFPageCountObjects(page))
                    return null;
                current = fpdf_edit.FPDFPageGetObject(page, index);
            }
            else
            {
                if (current == null || fpdf_edit.FPDFPageObjGetType(current) != PageObjectForm ||
                    index >= fpdf_edit.FPDFFormObjCountObjects(current))
                    return null;
                current = fpdf_edit.FPDFFormObjGetObject(current, (ulong)index);
            }

            if (current == null || current.__Instance == IntPtr.Zero)
                return null;
        }
        return current;
    }

    /// <summary>Рамка объекта в координатах СТРАНИЦЫ с учётом матриц всех форм над ним.</summary>
    public static bool TryGetPageBounds(
        FpdfPageobjectT obj, Transform toPage,
        out double left, out double bottom, out double right, out double top)
    {
        left = bottom = right = top = 0;
        float l = 0, b = 0, r = 0, t = 0;
        if (fpdf_edit.FPDFPageObjGetBounds(obj, ref l, ref b, ref r, ref t) == 0)
            return false;

        // Поворот формы делает из прямоугольника четырёхугольник, поэтому
        // берётся описанная рамка по всем четырём углам, а не по двум.
        var corners = new[]
        {
            toPage.Apply(l, b), toPage.Apply(r, b),
            toPage.Apply(r, t), toPage.Apply(l, t),
        };
        left = corners.Min(p => p.X);
        right = corners.Max(p => p.X);
        bottom = corners.Min(p => p.Y);
        top = corners.Max(p => p.Y);
        return true;
    }

    private static void Descend(
        Func<int, FpdfPageobjectT?> getter, int count,
        int[] prefix, Transform toPage, List<Node> found, int depth)
    {
        // Ограничение глубины — защита от документа, где форма ссылается сама
        // на себя: такой файл увёл бы обход в бесконечность.
        if (depth > 8)
            return;

        for (var i = 0; i < count; i++)
        {
            var obj = getter(i);
            if (obj == null || obj.__Instance == IntPtr.Zero)
                continue;

            var path = new int[prefix.Length + 1];
            Array.Copy(prefix, path, prefix.Length);
            path[prefix.Length] = i;

            var type = fpdf_edit.FPDFPageObjGetType(obj);
            if (type == PageObjectText)
            {
                found.Add(new Node(obj, path, toPage));
                continue;
            }
            if (type != PageObjectForm)
                continue;

            var inner = toPage;
            var matrix = new FS_MATRIX_();
            if (fpdf_edit.FPDFPageObjGetMatrix(obj, matrix) != 0)
            {
                inner = new Transform(matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F)
                    .Then(toPage);
            }

            var child = obj;
            Descend(index => fpdf_edit.FPDFFormObjGetObject(child, (ulong)index),
                fpdf_edit.FPDFFormObjCountObjects(child), path, inner, found, depth + 1);
        }
    }
}
