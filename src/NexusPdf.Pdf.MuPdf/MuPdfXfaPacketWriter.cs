using System.Globalization;
using System.Text.RegularExpressions;
using MuPDF.NET;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Pdf.MuPdf;

/// <summary>
/// Замена пакета XFA в уже записанном PDF — ДОПИСЫВАНИЕМ к файлу.
///
/// PDFium произвольный объект документа поменять не даёт, а пакет XFA
/// datasets — отдельный поток, на который ссылается /AcroForm /XFA. MuPDF
/// находит его сам (в том числе в сжатых потоках объектов и в зашифрованном
/// файле — шифрование сохраняется), а дописывание не трогает уже записанные
/// байты: подписи остаются действительными.
/// </summary>
public sealed partial class MuPdfXfaPacketWriter : IXfaPacketWriter
{
    // Коды шифрования mupdf (pdf_write_options.do_encrypt): 0 — оставить как
    // есть. Document.SaveIncr из MuPDF.NET передаёт 1 («снять») и падает на
    // любом файле, поэтому параметры записи задаются явно.
    private const int EncryptKeep = 0;

    /// <summary>
    /// Пакет <paramref name="packetName"/> (например, «datasets») получает
    /// содержимое <paramref name="content"/>; если такого пакета нет, он
    /// добавляется перед закрывающим. <paramref name="packetName"/> = null —
    /// XFA хранится одним потоком, и заменяется весь XDP.
    /// После записи файл перечитывается: пакет обязан содержать ровно то,
    /// что записано.
    /// </summary>
    public void Replace(string path, string? password, string? packetName, byte[] content)
    {
        int written;
        string? temporary = null;
        using (var doc = Open(path, password))
        {
            written = Write(doc, packetName, content);
            if (doc.CanSaveIncrementally())
            {
                doc.Save(path, incremental: 1, encryption: EncryptKeep);
            }
            else
            {
                // Файл пришлось чинить при чтении — дописывать к нему нельзя.
                // Тогда полная перезапись рядом и подмена; шифрование сохраняется.
                temporary = path + ".xfa-" + Guid.NewGuid().ToString("N");
                doc.Save(temporary, encryption: EncryptKeep);
            }
        }
        if (temporary != null)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        using var check = Open(path, password);
        var stored = check.XrefStream(written);
        if (stored == null || !stored.AsSpan().SequenceEqual(content))
            throw new PdfEngineException("Данные XFA-формы не записались в файл.");
    }

    /// <summary>
    /// Содержимое пакета XFA (null — пакета нет). <paramref name="packetName"/>
    /// = null — XFA одним потоком, возвращается весь XDP.
    /// </summary>
    public static byte[]? ReadPacket(string path, string? password, string? packetName)
    {
        using var doc = Open(path, password);
        var (type, value) = doc.XrefGetKey(doc.PdfCatalog, "AcroForm/XFA");
        string array;
        switch (type)
        {
            case "array":
                array = value;
                break;
            case "xref":
                var xref = ParseReference(value);
                if (doc.XrefIsStream(xref))
                    return packetName == null ? doc.XrefStream(xref) : null;
                array = doc.XrefObject(xref);
                break;
            default:
                return null;
        }
        if (packetName == null) return null;
        var packet = ParsePackets(array).FirstOrDefault(p => p.Name == packetName);
        return packet.Name == null ? null : doc.XrefStream(packet.Xref);
    }

    private static Document Open(string path, string? password)
    {
        var doc = new Document(path);
        if (doc.NeedsPass && doc.Authenticate(password ?? "") == 0)
        {
            doc.Dispose();
            throw new PdfPasswordRequiredException();
        }
        return doc;
    }

    /// <returns>Номер объекта, куда записан пакет.</returns>
    private static int Write(Document doc, string? packetName, byte[] content)
    {
        var catalog = doc.PdfCatalog;
        var (type, value) = doc.XrefGetKey(catalog, "AcroForm/XFA");
        int? arrayXref = null;
        string? array = null;
        switch (type)
        {
            case "array":
                array = value;
                break;
            case "xref":
                var xref = ParseReference(value);
                if (doc.XrefIsStream(xref))
                {
                    // XFA одним потоком: заменяется весь XDP.
                    if (packetName != null)
                        throw new PdfEngineException("XFA этой формы хранится одним потоком, а не пакетами.");
                    doc.UpdateStream(xref, content, compress: true);
                    return xref;
                }
                arrayXref = xref;
                array = doc.XrefObject(xref);
                break;
            default:
                throw new PdfEngineException("В документе нет XFA-части формы.");
        }

        if (packetName == null)
            throw new PdfEngineException("XFA этой формы хранится пакетами, а не одним потоком.");

        var packets = ParsePackets(array);
        var existing = packets.FirstOrDefault(p => p.Name == packetName);
        if (existing.Name != null)
        {
            doc.UpdateStream(existing.Xref, content, compress: true);
            return existing.Xref;
        }

        // Пакета нет — новый поток, ссылка на него встаёт перед закрывающим
        // пакетом: XDP собирается склейкой пакетов по порядку.
        var created = doc.GetNewXref();
        doc.UpdateObject(created, "<<>>");
        doc.UpdateStream(created, content, compress: true);
        var entries = packets.Select(p => $"({p.Name}) {p.Xref} 0 R").ToList();
        var position = packets.Count > 0 && IsClosing(packets[^1].Name) ? packets.Count - 1 : packets.Count;
        entries.Insert(position, $"({packetName}) {created} 0 R");
        var newArray = "[" + string.Join(" ", entries) + "]";
        if (arrayXref is { } ax)
            doc.UpdateObject(ax, newArray);
        else
            doc.XrefSetKey(catalog, "AcroForm/XFA", newArray);
        return created;
    }

    private static bool IsClosing(string name) =>
        name == "postamble" || name.StartsWith("</", StringComparison.Ordinal);

    private static int ParseReference(string value)
    {
        var match = ReferencePattern().Match(value);
        if (!match.Success)
            throw new PdfEngineException("Ссылка на XFA-часть формы не читается.");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>Пары «имя пакета — номер объекта» из массива /XFA.</summary>
    internal static List<(string Name, int Xref)> ParsePackets(string array)
    {
        var result = new List<(string, int)>();
        foreach (Match match in PacketPattern().Matches(array))
        {
            var name = match.Groups["literal"].Success
                ? Unescape(match.Groups["literal"].Value)
                : HexToString(match.Groups["hex"].Value);
            result.Add((name, int.Parse(match.Groups["xref"].Value, CultureInfo.InvariantCulture)));
        }
        if (result.Count == 0)
            throw new PdfEngineException("Массив пакетов XFA не читается.");
        return result;
    }

    private static string Unescape(string literal) =>
        Regex.Replace(literal, @"\\(.)", m => m.Groups[1].Value);

    private static string HexToString(string hex)
    {
        var digits = new string(hex.Where(Uri.IsHexDigit).ToArray());
        if (digits.Length % 2 == 1) digits += "0";
        var bytes = Convert.FromHexString(digits);
        return System.Text.Encoding.Latin1.GetString(bytes);
    }

    [GeneratedRegex(@"^\s*(\d+)\s+\d+\s+R")]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"(?:\((?<literal>(?:\\.|[^\\)])*)\)|<(?<hex>[0-9A-Fa-f\s]*)>)\s*(?<xref>\d+)\s+\d+\s+R")]
    private static partial Regex PacketPattern();
}
