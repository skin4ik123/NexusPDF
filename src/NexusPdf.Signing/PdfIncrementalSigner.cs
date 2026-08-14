using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace NexusPdf.Signing;

public sealed class PdfSigningException : Exception
{
    public PdfSigningException(string message) : base(message) { }
}

/// <summary>
/// Невидимая цифровая подпись (adbe.pkcs7.detached, SHA-256) через
/// инкрементальное обновление PDF. Вход обязан быть «нормализованным» файлом
/// с классической xref-таблицей и текстовыми словарями (вывод qpdf --qdf) —
/// вызывающая сторона сначала прогоняет документ через qpdf.
/// Исходные байты не меняются: подпись дописывается в конец, как того требует
/// стандарт — любые предыдущие подписи остаются проверяемыми.
/// </summary>
public static class PdfIncrementalSigner
{
    private const int SignaturePlaceholderBytes = 16384;

    public static void Sign(
        string inputPath, string outputPath, X509Certificate2 certificate,
        string reason, string location)
    {
        if (!certificate.HasPrivateKey)
            throw new PdfSigningException("У выбранного сертификата нет закрытого ключа.");

        var bytes = File.ReadAllBytes(inputPath);
        var text = Encoding.Latin1.GetString(bytes);

        var trailer = FindLastTrailer(text);
        if (trailer.Dict.Contains("/Encrypt", StringComparison.Ordinal))
            throw new PdfSigningException("Подписание зашифрованных документов пока не поддерживается: сначала снимите пароль.");

        var rootRef = ParseRef(trailer.Dict, "/Root")
            ?? throw new PdfSigningException("В трейлере не найден /Root.");
        var size = ParseInt(trailer.Dict, "/Size")
            ?? throw new PdfSigningException("В трейлере не найден /Size.");
        var prevXref = FindLastStartxref(text);

        var catalog = GetObject(text, rootRef)
            ?? throw new PdfSigningException("Каталог документа не найден.");
        var pageRef = ResolveFirstPage(text, catalog.Dict)
            ?? throw new PdfSigningException("Первая страница не найдена.");
        var page = GetObject(text, pageRef)
            ?? throw new PdfSigningException("Объект страницы не найден.");

        var sigFieldNum = size;
        var sigValueNum = size + 1;

        // --- Обновлённые словари ---
        var newObjects = new List<(int Num, string Body)>();

        var acroFormRef = ParseRef(catalog.Dict, "/AcroForm");
        if (acroFormRef != null)
        {
            // AcroForm — отдельный объект: каталог не трогаем, дополняем его.
            var acro = GetObject(text, acroFormRef.Value)
                ?? throw new PdfSigningException("Объект AcroForm не найден.");
            newObjects.Add((acroFormRef.Value, AmendAcroForm(acro.Dict, sigFieldNum)));
        }
        else if (catalog.Dict.Contains("/AcroForm", StringComparison.Ordinal))
        {
            // Инлайновый словарь AcroForm внутри каталога.
            newObjects.Add((rootRef, AmendCatalogInlineAcroForm(catalog.Dict, sigFieldNum)));
        }
        else
        {
            var amended = InsertBeforeDictEnd(catalog.Dict,
                $" /AcroForm << /Fields [{sigFieldNum} 0 R] /SigFlags 3 >>");
            newObjects.Add((rootRef, amended));
        }

        newObjects.Add((pageRef, AmendPageAnnots(page.Dict, sigFieldNum)));

        var signName = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        var mDate = "D:" + DateTimeOffset.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "Z";
        newObjects.Add((sigFieldNum,
            $"<< /Type /Annot /Subtype /Widget /FT /Sig /T (NexusSig{size}) /Rect [0 0 0 0] /F 132 " +
            $"/P {pageRef} 0 R /V {sigValueNum} 0 R >>"));

        var sigDict = new StringBuilder();
        sigDict.Append("<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached ");
        sigDict.Append("/ByteRange [0 0000000000 0000000000 0000000000] ");
        sigDict.Append("/Contents <").Append(new string('0', SignaturePlaceholderBytes * 2)).Append("> ");
        sigDict.Append("/M (").Append(mDate).Append(") ");
        if (signName.Length > 0)
            sigDict.Append("/Name ").Append(PdfHexString(signName)).Append(' ');
        if (reason.Length > 0)
            sigDict.Append("/Reason ").Append(PdfHexString(reason)).Append(' ');
        if (location.Length > 0)
            sigDict.Append("/Location ").Append(PdfHexString(location)).Append(' ');
        sigDict.Append(">>");
        newObjects.Add((sigValueNum, sigDict.ToString()));

        // --- Сборка инкремента ---
        var increment = new StringBuilder();
        if (bytes.Length > 0 && bytes[^1] != '\n')
            increment.Append('\n');

        var offsets = new Dictionary<int, long>();
        foreach (var (num, body) in newObjects.OrderBy(o => o.Num))
        {
            offsets[num] = bytes.LongLength + increment.Length;
            increment.Append(num).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        var xrefOffset = bytes.LongLength + increment.Length;
        increment.Append("xref\n");
        foreach (var section in BuildXrefSections(offsets))
            increment.Append(section);
        increment.Append("trailer\n<< /Size ").Append(size + 2)
            .Append(" /Root ").Append(rootRef).Append(" 0 R /Prev ").Append(prevXref);
        var infoRef = ParseRef(trailer.Dict, "/Info");
        if (infoRef != null)
            increment.Append(" /Info ").Append(infoRef.Value).Append(" 0 R");
        increment.Append(" >>\nstartxref\n").Append(xrefOffset).Append("\n%%EOF\n");

        var incrementBytes = Encoding.Latin1.GetBytes(increment.ToString());
        var result = new byte[bytes.LongLength + incrementBytes.Length];
        Array.Copy(bytes, result, bytes.Length);
        Array.Copy(incrementBytes, 0, result, bytes.Length, incrementBytes.Length);

        // --- ByteRange и сама подпись ---
        var contentsOpen = LastIndexOf(result, "/Contents <"u8) + "/Contents ".Length;
        var contentsClose = contentsOpen + 1 + SignaturePlaceholderBytes * 2; // индекс '>'
        var a = 0L;
        var b = (long)contentsOpen;
        var c = (long)contentsClose + 1;
        var d = result.LongLength - c;

        var byteRangePatch = string.Format(CultureInfo.InvariantCulture,
            "[0 {0:0000000000} {1:0000000000} {2:0000000000}]", b, c, d);
        var byteRangePos = LastIndexOf(result, "/ByteRange ["u8) + "/ByteRange ".Length;
        Encoding.ASCII.GetBytes(byteRangePatch).CopyTo(result, byteRangePos);

        var signedData = new byte[b + d];
        Array.Copy(result, a, signedData, 0, b);
        Array.Copy(result, c, signedData, b, d);

        var cms = new SignedCms(new ContentInfo(signedData), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), // SHA-256
        };
        signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));
        cms.ComputeSignature(signer, silent: true);
        var pkcs7 = cms.Encode();
        if (pkcs7.Length > SignaturePlaceholderBytes)
            throw new PdfSigningException("Подпись не поместилась в зарезервированное место.");

        var hex = Convert.ToHexString(pkcs7);
        Encoding.ASCII.GetBytes(hex).CopyTo(result, contentsOpen + 1);

        File.WriteAllBytes(outputPath, result);
    }

    // ----- Правки словарей (текстовые, по нормализованному QDF-выводу) -----

    private static string AmendAcroForm(string acroDict, int sigFieldNum)
    {
        var withField = AppendToFieldsArray(acroDict, sigFieldNum);
        return EnsureSigFlags(withField);
    }

    private static string AmendCatalogInlineAcroForm(string catalogDict, int sigFieldNum)
    {
        var start = catalogDict.IndexOf("/AcroForm", StringComparison.Ordinal);
        var dictStart = catalogDict.IndexOf("<<", start, StringComparison.Ordinal);
        var dictEnd = FindBalancedDictEnd(catalogDict, dictStart);
        var inner = catalogDict[dictStart..dictEnd];
        var amended = EnsureSigFlags(AppendToFieldsArray(inner, sigFieldNum));
        return catalogDict[..dictStart] + amended + catalogDict[dictEnd..];
    }

    private static string AppendToFieldsArray(string dict, int sigFieldNum)
    {
        var fields = dict.IndexOf("/Fields", StringComparison.Ordinal);
        if (fields < 0)
            return InsertBeforeDictEnd(dict, $" /Fields [{sigFieldNum} 0 R]");
        var open = dict.IndexOf('[', fields);
        if (open < 0)
            throw new PdfSigningException("Массив /Fields не разобран.");
        return dict[..(open + 1)] + $"{sigFieldNum} 0 R " + dict[(open + 1)..];
    }

    private static string EnsureSigFlags(string dict)
    {
        if (dict.Contains("/SigFlags", StringComparison.Ordinal))
            return Regex.Replace(dict, @"/SigFlags\s+\d+", "/SigFlags 3");
        return InsertBeforeDictEnd(dict, " /SigFlags 3");
    }

    private static string AmendPageAnnots(string pageDict, int sigFieldNum)
    {
        var annots = pageDict.IndexOf("/Annots", StringComparison.Ordinal);
        if (annots < 0)
            return InsertBeforeDictEnd(pageDict, $" /Annots [{sigFieldNum} 0 R]");
        var open = pageDict.IndexOf('[', annots);
        if (open < 0)
            throw new PdfSigningException("Массив /Annots страницы не разобран (косвенный массив не поддержан).");
        return pageDict[..(open + 1)] + $"{sigFieldNum} 0 R " + pageDict[(open + 1)..];
    }

    private static string InsertBeforeDictEnd(string dict, string insertion)
    {
        var end = dict.LastIndexOf(">>", StringComparison.Ordinal);
        if (end < 0)
            throw new PdfSigningException("Словарь не разобран.");
        return dict[..end] + insertion + " " + dict[end..];
    }

    // ----- Парсинг нормализованного PDF -----

    private static (string Dict, int Position) FindLastTrailer(string text)
    {
        var pos = text.LastIndexOf("trailer", StringComparison.Ordinal);
        if (pos < 0)
            throw new PdfSigningException("Trailer не найден (файл не нормализован?).");
        var dictStart = text.IndexOf("<<", pos, StringComparison.Ordinal);
        var dictEnd = FindBalancedDictEnd(text, dictStart);
        return (text[dictStart..dictEnd], pos);
    }

    private static long FindLastStartxref(string text)
    {
        var pos = text.LastIndexOf("startxref", StringComparison.Ordinal);
        if (pos < 0)
            throw new PdfSigningException("startxref не найден.");
        var match = Regex.Match(text[(pos + 9)..Math.Min(text.Length, pos + 40)], @"\d+");
        if (!match.Success)
            throw new PdfSigningException("Смещение startxref не разобрано.");
        return long.Parse(match.Value, CultureInfo.InvariantCulture);
    }

    private static (string Dict, int Position)? GetObject(string text, int objectNumber)
    {
        var match = Regex.Match(text, $@"(?<=^|\n){objectNumber} 0 obj\b");
        if (!match.Success)
            return null;
        var dictStart = text.IndexOf("<<", match.Index, StringComparison.Ordinal);
        if (dictStart < 0)
            return null;
        var dictEnd = FindBalancedDictEnd(text, dictStart);
        return (text[dictStart..dictEnd], match.Index);
    }

    private static int FindBalancedDictEnd(string text, int dictStart)
    {
        if (dictStart < 0)
            throw new PdfSigningException("Словарь не найден.");
        var depth = 0;
        for (var i = dictStart; i < text.Length - 1; i++)
        {
            if (text[i] == '<' && text[i + 1] == '<') { depth++; i++; }
            else if (text[i] == '>' && text[i + 1] == '>')
            {
                depth--;
                i++;
                if (depth == 0)
                    return i + 1;
            }
        }
        throw new PdfSigningException("Несбалансированный словарь.");
    }

    private static int? ParseRef(string dict, string key)
    {
        var match = Regex.Match(dict, Regex.Escape(key) + @"\s+(\d+)\s+0\s+R");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static int? ParseInt(string dict, string key)
    {
        var match = Regex.Match(dict, Regex.Escape(key) + @"\s+(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static int? ResolveFirstPage(string text, string catalogDict)
    {
        var current = ParseRef(catalogDict, "/Pages");
        for (var depth = 0; depth < 32 && current != null; depth++)
        {
            var node = GetObject(text, current.Value);
            if (node == null) return null;
            if (node.Value.Dict.Contains("/Type /Page", StringComparison.Ordinal) &&
                !node.Value.Dict.Contains("/Type /Pages", StringComparison.Ordinal))
                return current;
            var kids = Regex.Match(node.Value.Dict, @"/Kids\s*\[\s*(\d+)\s+0\s+R");
            if (!kids.Success) return null;
            current = int.Parse(kids.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return null;
    }

    private static IEnumerable<string> BuildXrefSections(Dictionary<int, long> offsets)
    {
        var nums = offsets.Keys.OrderBy(n => n).ToList();
        var i = 0;
        while (i < nums.Count)
        {
            var start = i;
            while (i + 1 < nums.Count && nums[i + 1] == nums[i] + 1)
                i++;
            var section = new StringBuilder();
            section.Append(nums[start]).Append(' ').Append(i - start + 1).Append('\n');
            for (var k = start; k <= i; k++)
                section.Append(offsets[nums[k]].ToString("0000000000", CultureInfo.InvariantCulture))
                       .Append(" 00000 n \n");
            i++;
            yield return section.ToString();
        }
    }

    /// <summary>Юникод-строка PDF как hex-строка UTF-16BE с BOM — без проблем экранирования.</summary>
    private static string PdfHexString(string value)
    {
        var bytes = Encoding.BigEndianUnicode.GetBytes(value);
        return "<FEFF" + Convert.ToHexString(bytes) + ">";
    }

    private static int LastIndexOf(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        var index = haystack.AsSpan().LastIndexOf(needle);
        if (index < 0)
            throw new PdfSigningException("Внутренняя ошибка: маркер не найден.");
        return index;
    }
}
