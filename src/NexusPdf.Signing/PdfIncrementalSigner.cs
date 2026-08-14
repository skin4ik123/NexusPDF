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
            var amendedAcro = AppendRefToArray(text, acro.Dict, "/Fields", sigFieldNum, newObjects, out _);
            newObjects.Add((acroFormRef.Value, EnsureSigFlags(amendedAcro)));
        }
        else if (catalog.Dict.Contains("/AcroForm", StringComparison.Ordinal))
        {
            // Инлайновый словарь AcroForm внутри каталога.
            newObjects.Add((rootRef, AmendCatalogInlineAcroForm(text, catalog.Dict, sigFieldNum, newObjects)));
        }
        else
        {
            var amended = InsertBeforeDictEnd(catalog.Dict,
                $" /AcroForm << /Fields [{sigFieldNum} 0 R] /SigFlags 3 >>");
            newObjects.Add((rootRef, amended));
        }

        var amendedPage = AppendRefToArray(text, page.Dict, "/Annots", sigFieldNum, newObjects, out var pageChanged);
        if (pageChanged)
            newObjects.Add((pageRef, amendedPage));

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

    private static string AmendCatalogInlineAcroForm(
        string text, string catalogDict, int sigFieldNum, List<(int Num, string Body)> newObjects)
    {
        var start = catalogDict.IndexOf("/AcroForm", StringComparison.Ordinal);
        var dictStart = catalogDict.IndexOf("<<", start, StringComparison.Ordinal);
        var dictEnd = FindBalancedDictEnd(catalogDict, dictStart);
        var inner = catalogDict[dictStart..dictEnd];
        var amended = EnsureSigFlags(AppendRefToArray(text, inner, "/Fields", sigFieldNum, newObjects, out _));
        return catalogDict[..dictStart] + amended + catalogDict[dictEnd..];
    }

    /// <summary>
    /// Дописывает ссылку на sig-поле в массив-значение ключа словаря.
    /// Скобка массива берётся строго ЗА ключом (а не первая «[» дальше по
    /// тексту — так ссылка попадала бы в чужой массив вроде /MediaBox).
    /// Косвенная ссылка на массив переопределяется отдельным объектом
    /// инкремента; сам словарь тогда не меняется (dictChanged=false).
    /// </summary>
    private static string AppendRefToArray(
        string text, string dict, string key, int sigFieldNum,
        List<(int Num, string Body)> newObjects, out bool dictChanged)
    {
        var match = Regex.Match(dict, Regex.Escape(key) + @"\s*(\[|(\d+)\s+\d+\s+R)");
        if (!match.Success)
        {
            if (dict.Contains(key, StringComparison.Ordinal))
                throw new PdfSigningException($"Значение {key} не разобрано (не массив и не ссылка).");
            dictChanged = true;
            return InsertBeforeDictEnd(dict, $" {key} [{sigFieldNum} 0 R]");
        }
        if (match.Groups[1].Value == "[")
        {
            dictChanged = true;
            var open = match.Groups[1].Index;
            return dict[..(open + 1)] + $"{sigFieldNum} 0 R " + dict[(open + 1)..];
        }

        // Косвенный массив. /Fields AcroForm и /Annots страницы могут указывать
        // на один и тот же объект (merged-виджеты) — второй раз не переопределяем.
        var arrayRef = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        dictChanged = false;
        if (newObjects.Any(o => o.Num == arrayRef))
            return dict;
        var body = GetArrayObjectBody(text, arrayRef)
            ?? throw new PdfSigningException($"Косвенный массив {key} ({arrayRef} 0 R) не разобран.");
        newObjects.Add((arrayRef, "[" + $"{sigFieldNum} 0 R " + body[1..]));
        return dict;
    }

    private static string EnsureSigFlags(string dict)
    {
        if (dict.Contains("/SigFlags", StringComparison.Ordinal))
            return Regex.Replace(dict, @"/SigFlags\s+\d+", "/SigFlags 3");
        return InsertBeforeDictEnd(dict, " /SigFlags 3");
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
        foreach (var index in FindObjectHeaders(text, objectNumber))
        {
            var dictStart = text.IndexOf("<<", index, StringComparison.Ordinal);
            if (dictStart < 0 ||
                text.IndexOf("endobj", index, dictStart - index, StringComparison.Ordinal) >= 0)
                continue; // у объекта нет словаря — это не тот, кого ищут
            var dictEnd = FindBalancedDictEnd(text, dictStart);
            return (text[dictStart..dictEnd], index);
        }
        return null;
    }

    private static string? GetArrayObjectBody(string text, int objectNumber)
    {
        foreach (var index in FindObjectHeaders(text, objectNumber))
        {
            var open = text.IndexOf('[', index);
            if (open < 0 ||
                text.IndexOf("endobj", index, open - index, StringComparison.Ordinal) >= 0)
                return null;
            var depth = 0;
            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '[') depth++;
                else if (text[i] == ']' && --depth == 0)
                    return text[open..(i + 1)];
            }
            return null;
        }
        return null;
    }

    /// <summary>
    /// Заголовки «N 0 obj» на верхнем уровне файла. Совпадения внутри данных
    /// потоков (QDF распаковывает всё, и вложенный PDF в /EmbeddedFiles
    /// содержит собственные «N 0 obj») — не объекты и пропускаются.
    /// </summary>
    private static IEnumerable<int> FindObjectHeaders(string text, int objectNumber)
    {
        var streams = GetStreamRanges(text);
        foreach (Match match in Regex.Matches(text, $@"(?<=^|\n){objectNumber} 0 obj\b"))
        {
            var inStream = false;
            foreach (var (start, end) in streams)
            {
                if (match.Index >= start && match.Index < end) { inStream = true; break; }
                if (start > match.Index) break; // диапазоны отсортированы
            }
            if (!inStream)
                yield return match.Index;
        }
    }

    [ThreadStatic] private static string? _streamRangesText;
    [ThreadStatic] private static List<(int Start, int End)>? _streamRanges;

    private static List<(int Start, int End)> GetStreamRanges(string text)
    {
        if (ReferenceEquals(_streamRangesText, text) && _streamRanges != null)
            return _streamRanges;
        var ranges = new List<(int Start, int End)>();
        var i = 0;
        while (true)
        {
            var s = text.IndexOf("stream", i, StringComparison.Ordinal);
            if (s < 0) break;
            // Отсеять суффикс «endstream» и имена вроде /AppStream.
            if (s > 0 && (char.IsLetterOrDigit(text[s - 1]) || text[s - 1] == '/'))
            {
                i = s + 6;
                continue;
            }
            var dataStart = s + 6;
            if (dataStart < text.Length && text[dataStart] == '\r') dataStart++;
            if (dataStart < text.Length && text[dataStart] == '\n') dataStart++;
            else { i = s + 6; continue; } // за ключевым словом stream обязан идти конец строки
            var e = text.IndexOf("endstream", dataStart, StringComparison.Ordinal);
            if (e < 0) break;
            ranges.Add((dataStart, e));
            i = e + 9;
        }
        _streamRangesText = text;
        _streamRanges = ranges;
        return ranges;
    }

    private static int FindBalancedDictEnd(string text, int dictStart)
    {
        if (dictStart < 0)
            throw new PdfSigningException("Словарь не найден.");
        // «<<»/«>>» считаются только вне литеральных строк, hex-строк и
        // комментариев: строка вида (a >> 2) внутри значения ключа иначе
        // преждевременно «закрывала» словарь и обрезала его.
        var depth = 0;
        var i = dictStart;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '(')
            {
                i++;
                var strDepth = 1;
                while (i < text.Length && strDepth > 0)
                {
                    if (text[i] == '\\') i++;
                    else if (text[i] == '(') strDepth++;
                    else if (text[i] == ')') strDepth--;
                    i++;
                }
                continue;
            }
            if (ch == '%')
            {
                while (i < text.Length && text[i] != '\n' && text[i] != '\r') i++;
                continue;
            }
            if (ch == '<' && i + 1 < text.Length && text[i + 1] == '<') { depth++; i += 2; continue; }
            if (ch == '<')
            {
                i++;
                while (i < text.Length && text[i] != '>') i++;
                i++;
                continue;
            }
            if (ch == '>' && i + 1 < text.Length && text[i + 1] == '>')
            {
                depth--;
                i += 2;
                if (depth == 0)
                    return i;
                continue;
            }
            i++;
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
