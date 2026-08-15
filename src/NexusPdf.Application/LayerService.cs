using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Application;

/// <summary>Слой документа: имя, ссылка на объект PDF и текущая видимость.</summary>
public sealed record PdfLayer(string Reference, string Name, bool IsVisible);

/// <summary>
/// Необязательные группы содержимого (слои, OCG). У pdfium публичного API для
/// них нет, поэтому список читается из структуры документа через qpdf, а
/// переключение видимости пишется в конфигурацию по умолчанию
/// /OCProperties /D: pdfium её при отрисовке учитывает — это проверено
/// тестом LayerProbeTests, и вся функция держится на этом факте.
/// </summary>
public sealed class LayerService
{
    private readonly IPdfStructureJsonEngine _qpdf;

    public LayerService(IPdfStructureJsonEngine qpdf) => _qpdf = qpdf;

    public bool IsAvailable => _qpdf.IsAvailable;

    /// <summary>Слои документа в порядке /Order. Пустой список — слоёв нет.</summary>
    public async Task<IReadOnlyList<PdfLayer>> GetLayersAsync(
        string filePath, string? password, CancellationToken ct)
    {
        var json = await _qpdf.DescribeJsonAsync(filePath, password, ct).ConfigureAwait(false);
        var objects = ParseObjects(json);
        if (objects == null)
            return Array.Empty<PdfLayer>();

        var (_, properties) = FindOcProperties(objects);
        if (properties == null)
            return Array.Empty<PdfLayer>();

        var order = ReadRefArray(properties["/D"]?["/Order"]);
        var all = ReadRefArray(properties["/OCGs"]);
        var off = new HashSet<string>(ReadRefArray(properties["/D"]?["/OFF"]), StringComparer.Ordinal);

        // /Order задаёт порядок показа, но в нём может не быть части групп —
        // такие добавляются в конец, иначе слой был бы не виден пользователю.
        var ordered = order.Where(all.Contains).ToList();
        ordered.AddRange(all.Where(r => !ordered.Contains(r)));

        return ordered
            .Select(reference => new PdfLayer(
                reference,
                ReadLayerName(objects, reference),
                !off.Contains(reference)))
            .ToList();
    }

    /// <summary>
    /// Сохраняет копию документа с заданной видимостью слоёв. Меняется только
    /// конфигурация /OCProperties /D — содержимое страниц не трогается,
    /// поэтому выключенный слой можно включить обратно в любой момент.
    /// </summary>
    public async Task SetLayerVisibilityAsync(
        string sourcePath, string? password, IReadOnlyDictionary<string, bool> visibility,
        string targetPath, CancellationToken ct)
    {
        var json = await _qpdf.DescribeJsonAsync(sourcePath, password, ct).ConfigureAwait(false);
        var objects = ParseObjects(json)
            ?? throw new PdfEngineException("qpdf не вернул объекты документа.");
        var (holderKey, properties) = FindOcProperties(objects);
        if (properties == null || holderKey == null)
            throw new PdfEngineException("В документе нет слоёв.");

        var all = ReadRefArray(properties["/OCGs"]);
        var on = new JsonArray();
        var off = new JsonArray();
        foreach (var reference in all)
        {
            var visible = !visibility.TryGetValue(reference, out var wanted) || wanted;
            (visible ? on : off).Add(JsonValue.Create(reference));
        }

        var config = properties["/D"] as JsonObject ?? new JsonObject();
        config["/ON"] = on;
        config["/OFF"] = off;
        properties["/D"] = config;

        // Патч содержит ТОЛЬКО объект-держатель /OCProperties: остальные
        // объекты документа qpdf переносит как есть.
        var holder = objects[holderKey]!.DeepClone();
        var patch = new JsonObject
        {
            ["qpdf"] = new JsonArray
            {
                new JsonObject
                {
                    ["jsonversion"] = 2,
                    ["pdfversion"] = ReadPdfVersion(json),
                    ["maxobjectid"] = ReadMaxObjectId(json),
                },
                new JsonObject { [holderKey] = holder },
            },
        };

        await _qpdf.UpdateFromJsonAsync(
            sourcePath, patch.ToJsonString(), targetPath, password, ct).ConfigureAwait(false);
    }

    // ----- Разбор JSON qpdf -----

    /// <summary>Второй элемент массива "qpdf" — словарь объектов вида "obj:1 0 R".</summary>
    private static JsonObject? ParseObjects(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        if (root?["qpdf"] is not JsonArray parts || parts.Count < 2)
            return null;
        return parts[1] as JsonObject;
    }

    private static string ReadPdfVersion(string json) =>
        (JsonNode.Parse(json) as JsonObject)?["qpdf"]?[0]?["pdfversion"]?.GetValue<string>() ?? "1.7";

    private static int ReadMaxObjectId(string json) =>
        (JsonNode.Parse(json) as JsonObject)?["qpdf"]?[0]?["maxobjectid"]?.GetValue<int>() ?? 0;

    /// <summary>
    /// Объект, у которого лежит /OCProperties (обычно каталог), и сам словарь.
    /// Каталог ищется по содержимому, а не по номеру: номер объекта каталога
    /// в реальных файлах любой.
    /// </summary>
    private static (string? Key, JsonObject? Properties) FindOcProperties(JsonObject objects)
    {
        foreach (var (key, node) in objects)
        {
            if (node?["value"] is not JsonObject value)
                continue;
            if (value["/OCProperties"] is JsonObject properties)
                return (key, properties);
        }
        return (null, null);
    }

    private static List<string> ReadRefArray(JsonNode? node)
    {
        var result = new List<string>();
        if (node is not JsonArray array)
            return result;
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
                result.Add(text);
        }
        return result;
    }

    /// <summary>Имя слоя из его объекта; безымянные показываются ссылкой, а не пустой строкой.</summary>
    private static string ReadLayerName(JsonObject objects, string reference)
    {
        var node = objects["obj:" + reference]?["value"];
        var name = node?["/Name"];
        if (name is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
            return text.StartsWith("u:", StringComparison.Ordinal) ? text[2..] : text;
        return reference;
    }
}
