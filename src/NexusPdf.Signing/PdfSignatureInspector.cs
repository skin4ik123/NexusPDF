using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace NexusPdf.Signing;

public sealed record PdfSignatureInfo(
    string SignerName,
    string CertificateSubject,
    DateTimeOffset? SignTime,
    string Reason,
    string Location,
    bool IsCryptoValid,
    bool IsTrusted,
    bool CoversWholeDocument,
    string? Error);

/// <summary>
/// Проверка цифровых подписей PDF (adbe.pkcs7.detached / ETSI.CAdES).
/// Словарь подписи по спецификации не может лежать в object stream (его
/// /ByteRange ссылается на физические байты файла), поэтому подписи надёжно
/// находятся прямым сканом байтов, без полного парсера PDF.
/// </summary>
public static class PdfSignatureInspector
{
    public static Task<IReadOnlyList<PdfSignatureInfo>> InspectAsync(string filePath, CancellationToken ct) =>
        Task.Run<IReadOnlyList<PdfSignatureInfo>>(() => Inspect(File.ReadAllBytes(filePath)), ct);

    public static IReadOnlyList<PdfSignatureInfo> Inspect(byte[] fileBytes)
    {
        var results = new List<PdfSignatureInfo>();
        var offset = 0;
        while (true)
        {
            var byteRangePos = IndexOf(fileBytes, "/ByteRange"u8, offset);
            if (byteRangePos < 0)
                break;
            offset = byteRangePos + 10;

            try
            {
                var info = InspectOne(fileBytes, byteRangePos);
                if (info != null)
                    results.Add(info);
            }
            catch (Exception ex)
            {
                results.Add(new PdfSignatureInfo("", "", null, "", "", false, false, false,
                    "Подпись не разобрана: " + ex.Message));
            }
        }
        return results;
    }

    private static PdfSignatureInfo? InspectOne(byte[] bytes, int byteRangePos)
    {
        // /ByteRange [ a b c d ]
        var ranges = ParseByteRange(bytes, byteRangePos);
        if (ranges == null)
            return null;
        var (a, b, c, d) = ranges.Value;
        if (a < 0 || b < 0 || c < 0 || d < 0 || a + b > bytes.LongLength || c + d > bytes.LongLength || c < a + b)
            return new PdfSignatureInfo("", "", null, "", "", false, false, false, "Некорректный ByteRange.");

        // /Contents <hex> — в пределах того же объекта подписи; дырка ByteRange
        // (a+b .. c) и есть значение Contents вместе с ограждающими скобками.
        var contentsHex = ExtractHexBetween(bytes, a + b, c);
        if (contentsHex == null)
            return new PdfSignatureInfo("", "", null, "", "", false, false, false,
                "Дыра ByteRange не совпадает со значением /Contents — в файле есть байты, не покрытые подписью.");

        var pkcs7 = Convert.FromHexString(contentsHex);
        // Хвостовые нулевые байты заполнителя допустимы.
        var realLength = pkcs7.Length;
        while (realLength > 0 && pkcs7[realLength - 1] == 0)
            realLength--;
        pkcs7 = pkcs7[..realLength];

        var signedData = new byte[b + d];
        Array.Copy(bytes, a, signedData, 0, b);
        Array.Copy(bytes, c, signedData, b, d);

        var coversWhole = a == 0 && c + d == bytes.LongLength;

        string signerName = "", subject = "", error = "";
        DateTimeOffset? signTime = null;
        var cryptoValid = false;
        var trusted = false;
        try
        {
            var cms = new SignedCms(new ContentInfo(signedData), detached: true);
            cms.Decode(pkcs7);
            try
            {
                cms.CheckSignature(verifySignatureOnly: true);
                cryptoValid = true;
            }
            catch (CryptographicException ex)
            {
                error = "Криптографическая проверка не пройдена: " + ex.Message;
            }

            if (cms.SignerInfos.Count > 0)
            {
                var signer = cms.SignerInfos[0];
                var certificate = signer.Certificate;
                if (certificate != null)
                {
                    subject = certificate.Subject;
                    signerName = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
                    using var chain = new X509Chain();
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // офлайн-проверка
                    trusted = chain.Build(certificate);
                }
                foreach (var attribute in signer.SignedAttributes)
                {
                    if (attribute.Oid?.Value != "1.2.840.113549.1.9.5" || attribute.Values.Count == 0)
                        continue;
                    try
                    {
                        signTime = attribute.Values[0] is Pkcs9SigningTime typed
                            ? typed.SigningTime
                            : new Pkcs9SigningTime(attribute.Values[0].RawData).SigningTime;
                    }
                    catch (CryptographicException)
                    {
                        // Время возьмём из /M словаря подписи.
                    }
                }
            }
        }
        catch (Exception ex)
        {
            error = "PKCS#7 не разобран: " + ex.Message;
        }

        // Строковые поля (/M /Name /Reason /Location) могут стоять по обе
        // стороны от многокилобайтного hex-заполнителя Contents; каждый
        // сегмент окна словаря подписи просматривается отдельно.
        var (dictBefore, dictAfter) = GetSignatureDictWindow(bytes, byteRangePos, a + b, c);
        string? FindKey(string key) =>
            FindLiteralString(dictBefore, key) ?? FindLiteralString(dictAfter, key);
        signTime ??= ParsePdfDate(FindKey("/M"));
        var reason = FindKey("/Reason") ?? "";
        var location = FindKey("/Location") ?? "";
        if (signerName.Length == 0)
            signerName = FindKey("/Name") ?? "";

        return new PdfSignatureInfo(
            signerName, subject, signTime, reason, location,
            cryptoValid, trusted, coversWhole,
            error.Length > 0 ? error : null);
    }

    private static (long A, long B, long C, long D)? ParseByteRange(byte[] bytes, int byteRangePos)
    {
        var open = Array.IndexOf(bytes, (byte)'[', byteRangePos);
        if (open < 0 || open - byteRangePos > 64) return null;
        var close = Array.IndexOf(bytes, (byte)']', open);
        if (close < 0 || close - open > 200) return null;

        var text = Encoding.ASCII.GetString(bytes, open + 1, close - open - 1);
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4) return null;
        var values = new long[4];
        for (var i = 0; i < 4; i++)
        {
            if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]))
                return null;
        }
        return (values[0], values[1], values[2], values[3]);
    }

    /// <summary>
    /// Hex-содержимое «дырки» ByteRange. Дырка обязана быть РОВНО значением
    /// /Contents: «&lt;» первым байтом, «&gt;» последним, внутри только hex и
    /// пробелы. Любые лишние байты в дырке не покрыты подписью — так строится
    /// подделка «полностью валидного» файла, поэтому такие подписи отвергаются.
    /// </summary>
    private static string? ExtractHexBetween(byte[] bytes, long from, long to)
    {
        if (to - from < 2 || bytes[from] != '<' || bytes[to - 1] != '>')
            return null;
        var builder = new StringBuilder((int)(to - from));
        for (var i = from + 1; i < to - 1; i++)
        {
            var ch = (char)bytes[i];
            if (char.IsWhiteSpace(ch))
                continue;
            if (ch is not ((>= '0' and <= '9') or (>= 'A' and <= 'F') or (>= 'a' and <= 'f')))
                return null;
            builder.Append(ch);
        }
        return builder.Length % 2 == 0 ? builder.ToString() : null;
    }

    /// <summary>
    /// Окно словаря подписи: два сегмента внутри границ самого словаря
    /// (сбалансированный скан от /ByteRange назад до «&lt;&lt;» и вперёд до
    /// парного «&gt;&gt;») — до значения /Contents и после него. Сегменты не
    /// склеиваются: строка не должна «перетекать» через многокилобайтную
    /// hex-дырку, а ключи соседних объектов файла не должны попадать в окно.
    /// </summary>
    private static (string Before, string After) GetSignatureDictWindow(
        byte[] bytes, int byteRangePos, long contentsStart, long contentsEnd)
    {
        var dictStart = FindEnclosingDictStart(bytes, byteRangePos);
        var dictEnd = FindEnclosingDictEnd(bytes, byteRangePos);
        var beforeEnd = Math.Min(contentsStart, dictEnd);
        var afterStart = Math.Max(contentsEnd, dictStart);
        // Латиница и разделители PDF читаются как Latin-1; юникод-строки
        // (hex UTF-16BE с BOM) декодирует FindLiteralString.
        static string Cut(byte[] source, long start, long end) =>
            end > start ? Encoding.Latin1.GetString(source, (int)start, (int)(end - start)) : "";
        return (Cut(bytes, dictStart, beforeEnd), Cut(bytes, afterStart, dictEnd));
    }

    private static long FindEnclosingDictStart(byte[] bytes, long pos)
    {
        var depth = 0;
        for (var i = pos - 1; i > 0; i--)
        {
            if (bytes[i] == '>' && bytes[i - 1] == '>') { depth++; i--; }
            else if (bytes[i] == '<' && bytes[i - 1] == '<')
            {
                if (depth == 0) return i - 1;
                depth--;
                i--;
            }
        }
        return 0;
    }

    private static long FindEnclosingDictEnd(byte[] bytes, long pos)
    {
        var depth = 1L;
        var i = pos;
        while (i < bytes.LongLength)
        {
            var ch = bytes[i];
            if (ch == '(')
            {
                // Литеральная строка: до парной «)» с учётом \-экранирования.
                i++;
                var strDepth = 1;
                while (i < bytes.LongLength && strDepth > 0)
                {
                    if (bytes[i] == '\\') i++;
                    else if (bytes[i] == '(') strDepth++;
                    else if (bytes[i] == ')') strDepth--;
                    i++;
                }
                continue;
            }
            if (ch == '<' && i + 1 < bytes.LongLength && bytes[i + 1] == '<') { depth++; i += 2; continue; }
            if (ch == '<')
            {
                // Hex-строка (в т.ч. дырка /Contents): до закрывающей «>».
                i++;
                while (i < bytes.LongLength && bytes[i] != '>') i++;
                i++;
                continue;
            }
            if (ch == '>' && i + 1 < bytes.LongLength && bytes[i + 1] == '>')
            {
                if (--depth == 0) return i;
                i += 2;
                continue;
            }
            i++;
        }
        return bytes.LongLength;
    }

    private static string? FindLiteralString(string window, string key)
    {
        // Значение может быть литеральной строкой (…) или hex-строкой <…>.
        var match = Regex.Match(window, Regex.Escape(key) + @"\s*([(<])");
        if (!match.Success) return null;
        if (match.Groups[1].Value == "<")
        {
            var hexEnd = window.IndexOf('>', match.Index);
            if (hexEnd < 0) return null;
            var hexStart = window.IndexOf('<', match.Index) + 1;
            var hex = new string(window[hexStart..hexEnd].Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (hex.Length % 2 == 1) hex += "0";
            try
            {
                var bytes = Convert.FromHexString(hex);
                if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                    return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
                return Encoding.Latin1.GetString(bytes);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        var open = window.IndexOf('(', match.Index);
        if (open < 0) return null;
        var builder = new StringBuilder();
        var depth = 1;
        for (var i = open + 1; i < window.Length; i++)
        {
            var ch = window[i];
            if (ch == '\\' && i + 1 < window.Length)
            {
                builder.Append(window[++i]);
                continue;
            }
            if (ch == '(') depth++;
            if (ch == ')' && --depth == 0)
                return DecodePdfText(builder.ToString());
            builder.Append(ch);
        }
        return null;
    }

    private static string DecodePdfText(string raw)
    {
        // UTF-16BE с BOM (þÿ в Latin-1)
        if (raw.Length >= 2 && raw[0] == 'þ' && raw[1] == 'ÿ')
        {
            var bytes = raw.Select(c => (byte)c).ToArray();
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        return raw;
    }

    private static DateTimeOffset? ParsePdfDate(string? pdfDate)
    {
        // D:YYYYMMDDHHmmSS+HH'mm'
        if (string.IsNullOrEmpty(pdfDate) || !pdfDate.StartsWith("D:", StringComparison.Ordinal))
            return null;
        var s = pdfDate[2..];
        try
        {
            var year = int.Parse(s[..4], CultureInfo.InvariantCulture);
            var month = s.Length >= 6 ? int.Parse(s[4..6], CultureInfo.InvariantCulture) : 1;
            var day = s.Length >= 8 ? int.Parse(s[6..8], CultureInfo.InvariantCulture) : 1;
            var hour = s.Length >= 10 ? int.Parse(s[8..10], CultureInfo.InvariantCulture) : 0;
            var minute = s.Length >= 12 ? int.Parse(s[10..12], CultureInfo.InvariantCulture) : 0;
            var second = s.Length >= 14 ? int.Parse(s[12..14], CultureInfo.InvariantCulture) : 0;
            var offset = TimeSpan.Zero;
            if (s.Length >= 15 && (s[14] == '+' || s[14] == '-'))
            {
                var oh = s.Length >= 17 ? int.Parse(s[15..17], CultureInfo.InvariantCulture) : 0;
                var om = s.Length >= 20 ? int.Parse(s[18..20], CultureInfo.InvariantCulture) : 0;
                offset = new TimeSpan(oh, om, 0);
                if (s[14] == '-') offset = -offset;
            }
            return new DateTimeOffset(year, month, day, hour, minute, second, offset);
        }
        catch
        {
            return null;
        }
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle, int start)
    {
        var span = haystack.AsSpan(start);
        var index = span.IndexOf(needle);
        return index < 0 ? -1 : start + index;
    }
}
