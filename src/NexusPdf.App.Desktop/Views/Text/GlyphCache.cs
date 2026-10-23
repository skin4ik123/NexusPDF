using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using NexusPdf.Pdf.Abstractions.TextBlocks;

namespace NexusPdf.App.Desktop.Views.Text;

/// <summary>
/// Начертания WPF из тех же файлов шрифтов, что верстают и пишут блок в PDF.
/// Буквы рисуются по номерам глифов и шагам из общей раскладки — никогда
/// по собственным метрикам WPF, иначе экран и файл разошлись бы.
/// </summary>
internal static class GlyphCache
{
    private static readonly Dictionary<FontFaceRef, GlyphTypeface?> Faces = new();
    private static readonly Dictionary<uint, SolidColorBrush> ColorBrushes = new();
    private static readonly XmlLanguage Language = XmlLanguage.GetLanguage(CultureInfo.InvariantCulture.IetfLanguageTag);

    /// <summary>Начертание по ссылке на грань файла; у коллекции .ttc номер грани — во фрагменте URI.</summary>
    public static GlyphTypeface? Face(FontFaceRef face)
    {
        if (Faces.TryGetValue(face, out var cached))
            return cached;
        GlyphTypeface? typeface = null;
        try
        {
            var uri = new UriBuilder(new Uri(Path.GetFullPath(face.Path)))
            {
                Fragment = face.FaceIndex > 0 ? face.FaceIndex.ToString(CultureInfo.InvariantCulture) : "",
            }.Uri;
            typeface = new GlyphTypeface(uri);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or FileFormatException or UriFormatException)
        {
            Serilog.Log.Debug(ex, "Шрифт {Path} для блока не открылся", face.Path);
        }
        Faces[face] = typeface;
        return typeface;
    }

    public static SolidColorBrush Brush(uint argb)
    {
        if (!ColorBrushes.TryGetValue(argb, out var brush))
        {
            brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            brush.Freeze();
            ColorBrushes[argb] = brush;
        }
        return brush;
    }

    /// <summary>
    /// Строка глифов куска раскладки в масштабе <paramref name="zoom"/> (DIP на пункт).
    /// null — шрифт не открылся или в куске нечего рисовать.
    /// </summary>
    public static GlyphRun? Run(LaidOutPiece piece, double zoom, float pixelsPerDip)
    {
        if (piece.Text.Length == 0 || Face(piece.Face) is not { } typeface)
            return null;

        var indices = new List<ushort>(piece.Text.Length);
        var advances = new List<double>(piece.Text.Length);
        for (var i = 0; i < piece.Text.Length;)
        {
            var units = char.IsHighSurrogate(piece.Text[i]) && i + 1 < piece.Text.Length && char.IsLowSurrogate(piece.Text[i + 1]) ? 2 : 1;
            var codePoint = units == 2 ? char.ConvertToUtf32(piece.Text[i], piece.Text[i + 1]) : piece.Text[i];
            indices.Add(typeface.CharacterToGlyphMap.TryGetValue(codePoint, out var glyph) ? glyph : (ushort)0);
            var advance = 0.0;
            for (var k = 0; k < units; k++)
                advance += piece.AdvancesPt[i + k];
            advances.Add(advance * zoom);
            i += units;
        }

        return new GlyphRun(typeface, 0, false, piece.FontSizePt * zoom, pixelsPerDip, indices,
            new Point(piece.XPt * zoom, piece.BaselinePt * zoom), advances,
            null, null, null, null, null, Language);
    }
}
