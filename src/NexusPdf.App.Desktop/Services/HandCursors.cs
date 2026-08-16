using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NexusPdf.App.Desktop.Services;

/// <summary>
/// Курсоры-руки для перетаскивания страницы: раскрытая ладонь под курсором и
/// сжатая, пока страницу тащат.
///
/// Windows таких курсоров не поставляет: <see cref="Cursors.Hand"/> — это
/// указательный палец, которым в документе обозначаются ССЫЛКИ, а
/// <see cref="Cursors.SizeAll"/> — четыре стрелки «переместить объект».
/// И то, и другое в просмотрщике означало бы не то, что происходит, поэтому
/// рука рисуется здесь и упаковывается в настоящий .cur прямо в памяти —
/// без двоичных файлов в репозитории.
/// </summary>
public static class HandCursors
{
    private const int Size = 32;

    private static Cursor? _open;
    private static Cursor? _grab;

    /// <summary>Раскрытая ладонь: страницу можно взять.</summary>
    public static Cursor Open => _open ??= Build(open: true, hotX: 12, hotY: 8);

    /// <summary>Сжатая ладонь: страницу держат и ведут.</summary>
    public static Cursor Grab => _grab ??= Build(open: false, hotX: 12, hotY: 10);

    private static Cursor Build(bool open, int hotX, int hotY)
    {
        var bitmap = Draw(open);
        var pixels = new byte[Size * Size * 4];
        bitmap.CopyPixels(pixels, Size * 4, 0);
        using var stream = new MemoryStream();
        WriteCur(stream, pixels, hotX, hotY);
        stream.Position = 0;
        return new Cursor(stream);
    }

    /// <summary>
    /// Рисунок руки: белая заливка с чёрным контуром — так курсор виден и на
    /// белой странице, и на тёмном фоне вокруг неё.
    /// </summary>
    private static RenderTargetBitmap Draw(bool open)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var fill = Brushes.White;
            var pen = new Pen(Brushes.Black, 1.1);
            pen.Freeze();

            // Ладонь.
            dc.DrawRoundedRectangle(fill, pen, new Rect(7.5, 12.5, 13, open ? 12 : 10), 4.5, 4.5);

            // Пальцы: у раскрытой ладони торчат вверх, у сжатой поджаты.
            var top = open ? 4.0 : 10.0;
            for (var i = 0; i < 4; i++)
            {
                var x = 8.0 + i * 3.2;
                var length = open
                    ? i switch { 0 => 8.0, 1 => 10.0, 2 => 9.0, _ => 7.0 }
                    : 4.0;
                dc.DrawRoundedRectangle(fill, pen,
                    new Rect(x, top + (open ? (i == 1 ? -1.5 : i == 2 ? -0.5 : 0.5) : 0), 2.9, length),
                    1.4, 1.4);
            }

            // Большой палец сбоку.
            dc.PushTransform(new RotateTransform(open ? -35 : -18, 8, 18));
            dc.DrawRoundedRectangle(fill, pen, new Rect(4.5, 13, 3.2, open ? 8 : 6), 1.6, 1.6);
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    /// <summary>
    /// Упаковка в .cur: тот же формат, что и .ico, но с горячей точкой на месте
    /// плоскостей. Растр пишется снизу вверх — так устроен BMP внутри значка.
    /// </summary>
    private static void WriteCur(Stream stream, byte[] pbgra, int hotX, int hotY)
    {
        const int headerSize = 40;
        var maskStride = (Size + 31) / 32 * 4;      // 1 бит на пиксель, строки по 4 байта
        var imageSize = headerSize + Size * Size * 4 + maskStride * Size;

        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0);          // reserved
        writer.Write((ushort)2);          // 2 — курсор
        writer.Write((ushort)1);          // одно изображение
        writer.Write((byte)Size);
        writer.Write((byte)Size);
        writer.Write((byte)0);            // палитры нет
        writer.Write((byte)0);            // reserved
        writer.Write((ushort)hotX);
        writer.Write((ushort)hotY);
        writer.Write(imageSize);
        writer.Write(6 + 16);             // смещение растра

        writer.Write(headerSize);
        writer.Write(Size);
        writer.Write(Size * 2);           // высота за два слоя: цвет и маска
        writer.Write((ushort)1);          // плоскостей
        writer.Write((ushort)32);         // бит на пиксель
        writer.Write(0);                  // BI_RGB
        writer.Write(Size * Size * 4 + maskStride * Size);
        for (var i = 0; i < 4; i++) writer.Write(0); // разрешение и палитра

        for (var y = Size - 1; y >= 0; y--)
        {
            var row = y * Size * 4;
            writer.Write(pbgra, row, Size * 4);
        }

        // Маска прозрачности. Растр уже с альфой, поэтому маска нулевая — но
        // без неё Windows курсор не примет.
        var mask = new byte[maskStride * Size];
        writer.Write(mask);
    }
}
