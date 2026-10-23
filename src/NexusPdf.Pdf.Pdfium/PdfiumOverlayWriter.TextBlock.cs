using System.Globalization;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.Abstractions.TextBlocks;
using PDFiumCore;

namespace NexusPdf.Pdf.Pdfium;

/// <summary>
/// Запись текстового блока: обычный текст страницы по общей раскладке плюс
/// скрытая метка, по которой блок потом открывается для правки снова.
/// </summary>
internal static partial class PdfiumOverlayWriter
{
    /// <summary>
    /// Точки привязки округляются до сотой пункта, наклон — до тысячной: такие
    /// числа PDFium записывает в файл без потерь, и отпечаток блока после
    /// повторного открытия считается по тем же значениям, что при записи.
    /// Сдвиг от округления — не больше 0,005 пт, поворот — сотые доли градуса.
    /// </summary>
    private const int OriginDecimals = 2;
    private const int OrientationDecimals = 3;

    private static void ApplyTextBlock(
        FpdfDocumentT document, FpdfPageT page, OverlayFontCache fonts, TextBlockOverlay block,
        int rotation, double offsetX, double offsetY, double contentWidth, double contentHeight)
    {
        if (!double.IsFinite(block.XPt) || !double.IsFinite(block.YPt) ||
            !double.IsFinite(block.WidthPt) || !double.IsFinite(block.HeightPt) ||
            !double.IsFinite(block.RotationDegrees))
            throw new PdfEngineException("Некорректная рамка текстового блока.");

        var layout = TextBlockLayout.Compute(block, fonts.Provider);
        var place = new BlockPlacement(block, layout, rotation, offsetX, offsetY, contentWidth, contentHeight);
        var written = new List<(FpdfPageobjectT Obj, string Ref, string Text)>();

        foreach (var piece in layout.Lines.SelectMany(l => l.Pieces))
        {
            var font = fonts.ForFace(piece.Face)
                       ?? throw new PdfEngineException($"Не удалось встроить шрифт «{piece.Family}» в документ.");
            if (piece.Style.LetterSpacingPercent == 0)
            {
                written.Add((WriteGlyphs(document, page, font, piece, piece.Text, piece.XPt, place),
                    PieceRef(piece.Paragraph, piece.Start), piece.Text));
                continue;
            }

            // Межбуквенный интервал: у PDFium нет установки Tc для объекта,
            // поэтому каждая буква (суррогатная пара целиком) ставится сама.
            var x = piece.XPt;
            for (var i = 0; i < piece.Text.Length;)
            {
                var length = char.IsHighSurrogate(piece.Text[i]) && i + 1 < piece.Text.Length ? 2 : 1;
                var glyph = piece.Text.Substring(i, length);
                written.Add((WriteGlyphs(document, page, font, piece, glyph, x, place),
                    PieceRef(piece.Paragraph, piece.Start + i), glyph));
                for (var k = 0; k < length; k++)
                    x += piece.AdvancesPt[i + k];
                i += length;
            }
        }

        for (var i = 0; i < layout.Decorations.Count; i++)
        {
            var obj = WriteDecoration(page, layout.Decorations[i], place);
            if (obj != null)
                written.Add((obj, "d:" + i.ToString(CultureInfo.InvariantCulture), ""));
        }

        if (written.Count == 0)
            return; // пустой блок в файл не пишется

        var id = (block.BlockId == Guid.Empty ? Guid.NewGuid() : block.BlockId).ToString("N");
        var version = TextBlockCodec.FormatVersion.ToString(CultureInfo.InvariantCulture);
        var hash = TextBlockHash.Compute(written.Select(w => Fingerprint(w.Obj, w.Ref, isText: w.Text.Length > 0)));
        var first = written.FindIndex(w => w.Text.Length > 0);
        for (var i = 0; i < written.Count; i++)
        {
            var (obj, reference, _) = written[i];
            var mark = PdfiumTextBlockMarks.Add(obj);
            PdfiumTextBlockMarks.Set(document, obj, mark, "Id", id);
            PdfiumTextBlockMarks.Set(document, obj, mark, "V", version);
            PdfiumTextBlockMarks.Set(document, obj, mark, "R", reference);
            if (i == first)
            {
                PdfiumTextBlockMarks.Set(document, obj, mark, "Data", TextBlockCodec.EncodeStructure(block));
                PdfiumTextBlockMarks.Set(document, obj, mark, "Hash", hash);
            }
        }
    }

    /// <summary>Убирает со страницы все объекты блока (верхнего уровня — только так блок и пишется).</summary>
    private static void RemoveTextBlock(FpdfPageT page, Guid blockId)
    {
        var id = blockId.ToString("N");
        var reader = new PdfiumTextBlockMarks.Reader();
        for (var i = fpdf_edit.FPDFPageCountObjects(page) - 1; i >= 0; i--)
        {
            var obj = fpdf_edit.FPDFPageGetObject(page, i);
            if (obj == null || obj.__Instance == IntPtr.Zero || reader.Read(obj)?.Id != id)
                continue;
            if (fpdf_edit.FPDFPageRemoveObject(page, obj) != 0)
                fpdf_edit.FPDFPageObjDestroy(obj); // владение вернулось нам
        }
        // Метки дочитать не удалось — часть старых объектов могла остаться, и
        // новая версия легла бы поверх них. Лучше отказать, чем задвоить текст.
        if (reader.Exhausted)
            throw new PdfEngineException("Страница перегружена метками блоков: заменить блок на ней нельзя.");
    }

    private static string PieceRef(int paragraph, int start) =>
        paragraph.ToString(CultureInfo.InvariantCulture) + ":" + start.ToString(CultureInfo.InvariantCulture);

    private static FpdfPageobjectT WriteGlyphs(
        FpdfDocumentT document, FpdfPageT page, FpdfFontT font, LaidOutPiece piece, string text, double xPt,
        BlockPlacement place)
    {
        var obj = fpdf_edit.FPDFPageObjCreateTextObj(document, font, (float)piece.FontSizePt);
        if (obj == null || obj.__Instance == IntPtr.Zero)
            throw new PdfEngineException("Не удалось создать текстовый объект блока.");

        var buffer = new ushort[text.Length + 1];
        for (var i = 0; i < text.Length; i++)
            buffer[i] = text[i];
        if (fpdf_edit.FPDFTextSetText(obj, ref buffer[0]) == 0)
        {
            fpdf_edit.FPDFPageObjDestroy(obj);
            throw new PdfEngineException("Не удалось задать текст объекта блока.");
        }

        var color = piece.Style.ColorArgb;
        fpdf_edit.FPDFPageObjSetFillColor(obj, (byte)(color >> 16), (byte)(color >> 8), (byte)color, (byte)(color >> 24));

        // Пространство глифов: начало — точка базовой линии, ось y вверх.
        var m = place.Matrix(xPt, piece.BaselinePt, flipY: true);
        fpdf_edit.FPDFPageObjTransform(obj, m.A, m.B, m.C, m.D, m.E, m.F);
        fpdf_edit.FPDFPageInsertObject(page, obj); // объект переходит во владение страницы
        return obj;
    }

    private static FpdfPageobjectT? WriteDecoration(FpdfPageT page, LaidOutDecoration line, BlockPlacement place)
    {
        if (!(line.WidthPt > 0) || !(line.HeightPt > 0))
            return null;
        var obj = fpdf_edit.FPDFPageObjCreateNewRect(0, 0, (float)line.WidthPt, (float)line.HeightPt);
        if (obj == null || obj.__Instance == IntPtr.Zero)
            throw new PdfEngineException("Не удалось создать линию подчёркивания.");

        var color = line.ColorArgb;
        fpdf_edit.FPDFPageObjSetFillColor(obj, (byte)(color >> 16), (byte)(color >> 8), (byte)color, (byte)(color >> 24));
        fpdf_edit.FPDFPathSetDrawMode(obj, 1, 0); // FPDF_FILLMODE_WINDING, без обводки

        // Начало прямоугольника — его левый нижний угол, ось y вверх.
        var m = place.Matrix(line.XPt, line.YPt + line.HeightPt, flipY: true);
        fpdf_edit.FPDFPageObjTransform(obj, m.A, m.B, m.C, m.D, m.E, m.F);
        fpdf_edit.FPDFPageInsertObject(page, obj);
        return obj;
    }

    /// <summary>Отпечаток объекта блока по значениям, которые реально лежат в объекте.</summary>
    internal static TextBlockHash.Piece Fingerprint(FpdfPageobjectT obj, string reference, bool isText)
    {
        var matrix = new FS_MATRIX_();
        fpdf_edit.FPDFPageObjGetMatrix(obj, matrix);
        float size = 0;
        var fontName = "";
        if (isText)
        {
            fpdf_edit.FPDFTextObjGetFontSize(obj, ref size);
            var font = fpdf_edit.FPDFTextObjGetFont(obj);
            if (font != null && font.__Instance != IntPtr.Zero)
                fontName = PdfiumDocumentHandle.ReadFontName(font);
        }
        uint r = 0, g = 0, b = 0, a = 0;
        fpdf_edit.FPDFPageObjGetFillColor(obj, ref r, ref g, ref b, ref a);
        return new TextBlockHash.Piece(reference, fontName, size, (a << 24) | (r << 16) | (g << 8) | b,
            matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F);
    }

    /// <summary>
    /// Перевод из координат блока (пункты от левого верхнего угла рамки, ось y
    /// вниз) в координаты содержимого страницы: поворот блока по часовой
    /// стрелке вокруг центра рамки, затем /Rotate и CropBox страницы.
    /// </summary>
    private readonly struct BlockPlacement(
        TextBlockOverlay block, LaidOutBlock layout,
        int rotation, double offsetX, double offsetY, double contentWidth, double contentHeight)
    {
        private readonly double _cos = Math.Cos(block.RotationDegrees * Math.PI / 180);
        private readonly double _sin = Math.Sin(block.RotationDegrees * Math.PI / 180);

        private (double X, double Y) ToContent(double lx, double ly)
        {
            var (halfW, halfH) = (layout.WidthPt / 2, layout.HeightPt / 2);
            var (dx, dy) = (lx - halfW, ly - halfH);
            var displayedX = block.XPt + halfW + dx * _cos - dy * _sin;
            var displayedY = block.YPt + halfH + dx * _sin + dy * _cos;
            var (cx, cy) = DisplayedToContent(displayedX, displayedY, rotation, contentWidth, contentHeight);
            return (offsetX + cx, offsetY + cy);
        }

        /// <summary>
        /// Матрица объекта, у которого начало координат — в точке (x, y) блока.
        /// flipY: собственная ось y объекта смотрит вверх (так у текста и
        /// путей PDF), а ось блока — вниз.
        /// </summary>
        public (double A, double B, double C, double D, double E, double F) Matrix(double x, double y, bool flipY)
        {
            var origin = ToContent(x, y);
            var unitX = ToContent(x + 1, y);
            var unitY = ToContent(x, flipY ? y - 1 : y + 1);
            return (Math.Round(unitX.X - origin.X, OrientationDecimals), Math.Round(unitX.Y - origin.Y, OrientationDecimals),
                    Math.Round(unitY.X - origin.X, OrientationDecimals), Math.Round(unitY.Y - origin.Y, OrientationDecimals),
                    Math.Round(origin.X, OriginDecimals), Math.Round(origin.Y, OriginDecimals));
        }
    }
}
