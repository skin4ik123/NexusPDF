using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace NexusPdf.Pdf.Pdfium;

/// <summary>
/// Состояние поля AcroForm, которое переносится в данные XFA.
/// </summary>
/// <param name="FieldName">Полное имя поля AcroForm — SOM-путь вида form1[0].Page1[0].Name[0].</param>
/// <param name="Value">Значение поля (/V): текст, выбранный пункт списка, имя состояния флажка.</param>
/// <param name="CheckedControl">Номер отмеченного виджета флажка или переключателя; -1 — ничего не отмечено.</param>
/// <param name="CheckedExport">Имя «включённого» состояния отмеченного виджета (экспортное значение).</param>
public sealed record XfaFieldValue(string FieldName, string Value, int CheckedControl = -1, string CheckedExport = "");

/// <summary>
/// Привязка полей гибридной формы (AcroForm + XFA) к пакету XFA datasets.
///
/// Adobe Reader у такой формы берёт значения не из AcroForm, а из datasets:
/// правка одного AcroForm видна везде, кроме него. Поэтому изменённые значения
/// дописываются и в datasets — туда, куда их положил бы сам Adobe.
///
/// Куда именно, определяет слияние XFA (template + data): контейнеры обходятся
/// в порядке документа, именованная подформа спускается в одноимённую группу
/// данных, поле занимает первый ещё не занятый одноимённый узел своей группы
/// («once»), «global» делит один узел на все одноимённые поля, «dataRef»
/// указывает путь явно, «none» не связано с данными вовсе. Безымянные
/// подформы, области и наборы подформ прозрачны. Те же правила дают имена
/// полей AcroForm, поэтому имя поля однозначно находит свой контейнер.
///
/// Поле, для которого место в данных определить нельзя (привязка «none»,
/// поле мастер-страницы, непонятный dataRef), не заполняется: значение
/// осталось бы только в AcroForm, и Adobe Reader его бы не показал.
/// </summary>
public sealed class XfaDataBinding
{
    private const string DataNamespace = "http://www.xfa.org/schema/xfa-data/1.0/";
    private const string XhtmlNamespace = "http://www.w3.org/1999/xhtml";
    private const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    // Шаблон приходит из файла — недоверенные данные. Без пределов глубокая
    // вложенность роняла бы программу переполнением стека, а вложенные
    // повторы (occur) и огромный номер в dataRef раздували бы память.
    private const int MaxDepth = 256;
    private const int MaxContainers = 200_000;
    private const int MaxRefParts = 64;
    private const int MaxNewSiblings = 1_000;
    // Сколько узлов данных слияние может запланировать к созданию всего:
    // больше, чем контейнеров, чтобы обычный пустой бланк в него помещался.
    private const int MaxPlannedNodes = 250_000;
    private const int MaxNameLength = 512;
    private const int MaxRefLength = 4_096;
    private const long MaxPathChars = 32_000_000;

    private readonly XElement _rootSubform;
    // Привязка каждого элемента шаблона и элементы с id (для use="#id"):
    // считаются один раз, а не заново на каждом экземпляре повтора.
    private readonly Dictionary<XElement, BindInfo> _binds = new();
    private Dictionary<string, XElement>? _ids;
    private readonly string? _datasets;
    private readonly IReadOnlyDictionary<string, string?> _writable;
    private readonly IReadOnlyList<IReadOnlyList<string>> _shared;

    private XfaDataBinding(XElement rootSubform, string? datasets)
    {
        _rootSubform = rootSubform;
        _datasets = datasets;
        // Пробный проход по исходным данным: какие поля вообще можно записать.
        var probe = Bind(ParseDatasets(datasets));
        _writable = probe.Fields.ToDictionary(f => f.Key, f => f.Value.Target == null ? f.Value.Reason : null);
        _shared = probe.Fields
            .Where(f => f.Value.Target != null)
            .GroupBy(f => f.Value.Target!.Key)
            .Where(g => g.Count() > 1)
            .Select(g => (IReadOnlyList<string>)g.Select(f => f.Key).ToList())
            .ToList();
    }

    /// <summary>
    /// Разбор шаблона и данных. <paramref name="datasets"/> — пакет datasets
    /// целиком (элемент xfa:datasets) или null, если его в документе нет.
    /// Бросает <see cref="FormatException"/>, если шаблон не читается.
    /// </summary>
    public static XfaDataBinding Create(string template, string? datasets)
    {
        var doc = ParseXml(template);
        var root = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "subform")
                   ?? throw new FormatException("В шаблоне XFA нет корневой подформы.");
        return new XfaDataBinding(root, datasets);
    }

    /// <summary>
    /// Можно ли записать поле в данные XFA. false и для неизвестного имени:
    /// поле, которого нет в шаблоне, некуда записать.
    /// </summary>
    public bool CanWrite(string fieldName) =>
        _writable.TryGetValue(fieldName, out var reason) && reason == null;

    /// <summary>
    /// Поля, которые делят один узел данных (обычно «global»: название судна в
    /// шапке каждой страницы). В Adobe Reader у них всегда одно значение —
    /// поэтому и в AcroForm правка одного переносится на остальные.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> SharedFields => _shared;

    /// <summary>Поля шаблона, которые записать нельзя, с причиной — для журнала.</summary>
    public IEnumerable<KeyValuePair<string, string>> UnwritableFields =>
        _writable.Where(f => f.Value != null).Select(f => new KeyValuePair<string, string>(f.Key, f.Value!));

    /// <summary>
    /// Новый пакет datasets: исходные данные, в которых значения перечисленных
    /// полей заменены, а недостающие узлы созданы. Остальное не меняется.
    /// Поля, которые записать нельзя, пропускаются и возвращаются в
    /// <paramref name="skipped"/>.
    /// </summary>
    public string Apply(IEnumerable<XfaFieldValue> values, out IReadOnlyList<string> skipped)
    {
        var datasets = ParseDatasets(_datasets);
        var bound = Bind(datasets);
        var missed = new List<string>();
        foreach (var value in values)
        {
            if (!bound.Fields.TryGetValue(value.FieldName, out var field) || field.Target == null)
            {
                missed.Add(value.FieldName);
                continue;
            }
            WriteValue(field.Target.Materialize(), field.ToDataValue(value));
        }
        skipped = missed;
        return Serialize(datasets);
    }

    /// <summary>
    /// XFA одним потоком (весь XDP целиком): пакеты template и datasets как
    /// отдельные XML-строки. null — пакета нет.
    /// </summary>
    public static (string? Template, string? Datasets) SplitXdp(string xdp)
    {
        var doc = ParseXml(xdp);
        string? Packet(string name) => doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == name)
            ?.ToString(SaveOptions.DisableFormatting);
        return (Packet("template"), Packet("datasets"));
    }

    /// <summary>Тот же XDP, в котором пакет datasets заменён (или добавлен перед закрывающим тегом).</summary>
    public static string ReplaceDatasetsInXdp(string xdp, string datasets)
    {
        var doc = ParseXml(xdp);
        var replacement = ParseXml(datasets).Root!;
        var old = doc.Root!.Elements().FirstOrDefault(e => e.Name.LocalName == "datasets");
        if (old != null) old.ReplaceWith(replacement);
        else doc.Root.Add(replacement);
        return Serialize(doc);
    }

    // ----- Разбор и запись XML -----

    /// <summary>
    /// XML из PDF — недоверенные данные: DTD запрещены, внешние ресурсы не
    /// загружаются (без этого файл мог бы читать локальные файлы или
    /// раздувать память сущностями).
    /// </summary>
    private static XDocument ParseXml(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreProcessingInstructions = false,
        };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new FormatException("Пакет XFA не является корректным XML.", ex);
        }
    }

    private static XDocument ParseDatasets(string? datasets)
    {
        if (!string.IsNullOrWhiteSpace(datasets))
        {
            var doc = ParseXml(datasets);
            if (doc.Root?.Name.LocalName == "datasets")
                return doc;
            throw new FormatException("Пакет datasets не начинается с элемента xfa:datasets.");
        }
        XNamespace xfa = DataNamespace;
        return new XDocument(new XElement(xfa + "datasets",
            new XAttribute(XNamespace.Xmlns + "xfa", DataNamespace),
            new XElement(xfa + "data")));
    }

    private static string Serialize(XDocument doc)
    {
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = doc.Declaration == null,
            Indent = false,
            NewLineHandling = NewLineHandling.None,
            Encoding = new UTF8Encoding(false),
        };
        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, settings))
            doc.Save(writer);
        // StringBuilder заставляет писатель объявить utf-16; байты же уходят в UTF-8.
        var text = builder.ToString();
        return doc.Declaration == null ? text : text.Replace("encoding=\"utf-16\"", "encoding=\"UTF-8\"");
    }

    private static void WriteValue(XElement node, string value)
    {
        node.RemoveNodes();
        // Значение становится простым текстом: пометки «пусто» и «форматированный
        // текст» от прежнего значения к новому не относятся.
        node.Attributes().Where(a =>
                (a.Name.Namespace == XsiNamespace && a.Name.LocalName == "nil") ||
                (a.Name.Namespace == DataNamespace && a.Name.LocalName == "contentType"))
            .Remove();
        if (value.Length > 0)
            node.Add(new XText(value));
    }

    // ----- Слияние шаблона с данными -----

    private sealed class Bound
    {
        public readonly Dictionary<string, BoundField> Fields = new(StringComparer.Ordinal);
        public readonly HashSet<XElement> Used = new();
        public readonly Dictionary<string, DataNode> Globals = new(StringComparer.Ordinal);
        public readonly Dictionary<(DataNode Scope, string Name), int> Virtual = new();
        public DataNode Record = null!;
        public DataNode Data = null!;
        public int Visited;
        public int Planned;
        public long PathChars;

        /// <summary>Счёт контейнеров всего слияния: предел против вложенных повторов.</summary>
        public void Visit()
        {
            if (++Visited > MaxContainers)
                throw new FormatException("Шаблон XFA слишком велик.");
        }

        /// <summary>Общий счёт символов в именах полей: длинные имена × повторы не раздувают память.</summary>
        public void CountPath(string path)
        {
            PathChars += path.Length;
            if (PathChars > MaxPathChars)
                throw new FormatException("Шаблон XFA слишком велик.");
        }

        /// <summary>Запланировать создание узлов; false — общий предел исчерпан.</summary>
        public bool Plan(int nodes)
        {
            if (nodes <= 0) return true;
            if (Planned + (long)nodes > MaxPlannedNodes) return false;
            Planned += nodes;
            return true;
        }
    }

    private Bound Bind(XDocument datasets)
    {
        var bound = new Bound();
        XNamespace xfa = DataNamespace;
        var root = datasets.Root!;
        var data = root.Elements().FirstOrDefault(e => e.Name.LocalName == "data");
        if (data == null)
        {
            data = new XElement(xfa + "data");
            root.Add(data);
        }
        bound.Data = DataNode.Existing(data);
        var record = data.Elements().FirstOrDefault();
        bound.Record = record != null
            ? DataNode.Existing(record)
            : DataNode.Virtual(bound.Data, NameOr(_rootSubform, "form1"), 0);

        var rootName = NameOr(_rootSubform, "#subform");
        var rootPath = (_rootSubform.Attribute("name") != null ? rootName : "#subform") + "[0]";
        if (BindOf(_rootSubform).Match == "none")
            WalkUnbound(_rootSubform, rootPath, bound, "привязка none у корневой подформы", depth: 0);
        else
            Walk(_rootSubform, rootPath, new Dictionary<string, int>(StringComparer.Ordinal),
                bound.Record, new List<DataNode>(), inPageSet: false, bound, depth: 0);
        return bound;
    }

    private static string NameOr(XElement e, string fallback) =>
        e.Attribute("name")?.Value is { Length: > 0 } n ? n : fallback;

    private static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
        { "subform", "field", "exclGroup", "subformSet", "area", "pageSet", "pageArea" };

    /// <summary>
    /// Дети для SOM-имён: области и наборы подформ в именах не участвуют —
    /// их содержимое считается детьми родителя.
    /// </summary>
    private static IEnumerable<XElement> SomChildren(XElement e, int depth = 0)
    {
        CheckDepth(depth);
        foreach (var c in e.Elements())
        {
            var t = c.Name.LocalName;
            if (t is "area" or "subformSet")
            {
                foreach (var nested in SomChildren(c, depth + 1))
                    yield return nested;
            }
            else if (Containers.Contains(t))
            {
                yield return c;
            }
        }
    }

    private static void CheckDepth(int depth)
    {
        if (depth > MaxDepth)
            throw new FormatException("Шаблон XFA вложен слишком глубоко.");
    }

    private void Walk(XElement container, string prefix, Dictionary<string, int> names,
        DataNode scope, List<DataNode> ancestors, bool inPageSet, Bound bound, int depth)
    {
        CheckDepth(depth);
        var classes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var child in SomChildren(container))
        {
            var type = child.Name.LocalName;
            var name = NameOf(child);
            var instances = type == "subform" ? InstanceCount(child) : 1;
            for (var instance = 0; instance < instances; instance++)
            {
                bound.Visit();
                // Индекс у именованного узла — номер среди одноимённых в той же
                // области имён (сквозь прозрачные безымянные подформы); у
                // безымянного — номер среди соседей того же класса.
                string segment;
                if (!string.IsNullOrEmpty(name))
                {
                    names.TryGetValue(name, out var index);
                    names[name] = index + 1;
                    segment = $"{name}[{index}]";
                }
                else
                {
                    segment = $"#{type}[{classes.GetValueOrDefault(type)}]";
                }
                classes[type] = classes.GetValueOrDefault(type) + 1;
                var path = prefix + "." + segment;
                bound.CountPath(path);
                var bind = BindOf(child);

                if (type is "field" or "exclGroup")
                {
                    bound.Fields[path] = BindField(child, name, bind, scope, ancestors, inPageSet, bound, instance);
                }
                else if (type == "pageSet")
                {
                    // Мастер-страницы — своя область имён, и в обычное слияние
                    // данных их поля не входят.
                    Walk(child, path, new Dictionary<string, int>(StringComparer.Ordinal),
                        scope, ancestors, inPageSet: true, bound, depth + 1);
                }
                else if (bind.Match == "none")
                {
                    // Подформа без привязки: её поля связываются относительно
                    // той же группы данных, что и она сама.
                    WalkOrReset(child, path, name, names, scope, ancestors, inPageSet, bound, depth + 1);
                }
                else if (bind.Match == "dataRef")
                {
                    var target = ResolveRef(bind.Ref, scope, bound, instance);
                    if (target == null)
                        WalkUnbound(child, path, bound, "непонятный dataRef подформы", depth + 1);
                    else
                        Walk(child, path, new Dictionary<string, int>(StringComparer.Ordinal),
                            target, Append(ancestors, scope), inPageSet, bound, depth + 1);
                }
                else if (!string.IsNullOrEmpty(name) && type == "subform" &&
                         child.Attribute("scope")?.Value != "none")
                {
                    var group = TakeOnce(scope, name!, bound, wantGroup: true);
                    Walk(child, path, new Dictionary<string, int>(StringComparer.Ordinal),
                        group, Append(ancestors, scope), inPageSet, bound, depth + 1);
                }
                else
                {
                    // Безымянная подформа прозрачна и для имён, и для данных;
                    // страница мастер-набора — своя область имён, но не группа данных.
                    WalkOrReset(child, path, name, names, scope, ancestors, inPageSet, bound, depth + 1);
                }
            }
        }
    }

    private void WalkOrReset(XElement child, string path, string? name, Dictionary<string, int> names,
        DataNode scope, List<DataNode> ancestors, bool inPageSet, Bound bound, int depth) =>
        Walk(child, path, string.IsNullOrEmpty(name) ? names : new Dictionary<string, int>(StringComparer.Ordinal),
            scope, ancestors, inPageSet, bound, depth);

    private static List<DataNode> Append(List<DataNode> list, DataNode node) => new(list) { node };

    /// <summary>Все поля ветки — без места в данных (ветку записать нельзя).</summary>
    private static void WalkUnbound(XElement container, string prefix, Bound bound, string reason, int depth)
    {
        CheckDepth(depth);
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        var classes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var child in SomChildren(container))
        {
            bound.Visit();
            var type = child.Name.LocalName;
            var name = NameOf(child);
            string segment;
            if (!string.IsNullOrEmpty(name))
            {
                names.TryGetValue(name, out var index);
                names[name] = index + 1;
                segment = $"{name}[{index}]";
            }
            else
            {
                segment = $"#{type}[{classes.GetValueOrDefault(type)}]";
            }
            classes[type] = classes.GetValueOrDefault(type) + 1;
            var path = prefix + "." + segment;
            bound.CountPath(path);
            if (type is "field" or "exclGroup")
                bound.Fields[path] = new BoundField(child, null, reason);
            else
                WalkUnbound(child, path, bound, reason, depth + 1);
        }
    }

    private static int InstanceCount(XElement subform)
    {
        // Статическая форма не растёт: экземпляров столько, сколько задано
        // изначально (initial, по умолчанию — min, по умолчанию — 1).
        var occur = subform.Elements().FirstOrDefault(e => e.Name.LocalName == "occur");
        if (occur == null) return 1;
        static int? Read(XElement e, string attr) =>
            int.TryParse(e.Attribute(attr)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        var count = Read(occur, "initial") ?? Read(occur, "min") ?? 1;
        return Math.Clamp(count, 0, 10_000);
    }

    private readonly record struct BindInfo(string Match, string? Ref);

    private BindInfo BindOf(XElement container)
    {
        if (_binds.TryGetValue(container, out var cached)) return cached;
        var bind = container.Elements().FirstOrDefault(e => e.Name.LocalName == "bind")
                   ?? PrototypeBind(container);
        var reference = bind?.Attribute("ref")?.Value;
        // Слишком длинная ссылка — не путь в данных, а мусор: место не определить.
        if (reference is { Length: > MaxRefLength }) reference = null;
        var info = bind == null
            ? new BindInfo("once", null)
            : new BindInfo(bind.Attribute("match")?.Value ?? "once", reference);
        _binds[container] = info;
        return info;
    }

    /// <summary>Привязка, унаследованная от прототипа (use="#id").</summary>
    private XElement? PrototypeBind(XElement container)
    {
        var use = container.Attribute("use")?.Value;
        if (string.IsNullOrEmpty(use) || !use.StartsWith('#')) return null;
        _ids ??= BuildIds();
        return _ids.TryGetValue(use[1..], out var proto)
            ? proto.Elements().FirstOrDefault(e => e.Name.LocalName == "bind")
            : null;
    }

    private Dictionary<string, XElement> BuildIds()
    {
        var ids = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var e in _rootSubform.Document?.Descendants() ?? Enumerable.Empty<XElement>())
        {
            if (e.Attribute("id")?.Value is { } id)
                ids.TryAdd(id, e);
        }
        return ids;
    }

    /// <summary>Имя контейнера; слишком длинное — не бланк, и шаблон не принимается.</summary>
    private static string? NameOf(XElement container)
    {
        var name = container.Attribute("name")?.Value;
        if (name is { Length: > MaxNameLength })
            throw new FormatException("В шаблоне XFA слишком длинное имя.");
        return name;
    }

    private BoundField BindField(XElement field, string? name, BindInfo bind, DataNode scope,
        List<DataNode> ancestors, bool inPageSet, Bound bound, int instance)
    {
        if (string.IsNullOrEmpty(name))
            return new BoundField(field, null, "безымянное поле");
        if (field.Attribute("usehref") != null)
            return new BoundField(field, null, "поле из внешнего фрагмента");
        switch (bind.Match)
        {
            case "none":
                return new BoundField(field, null, "привязка none");
            case "global":
                return new BoundField(field, TakeGlobal(scope, ancestors, name, bound), null);
            case "dataRef":
                var target = ResolveRef(bind.Ref, scope, bound, instance);
                return target == null
                    ? new BoundField(field, null, "непонятный dataRef")
                    : new BoundField(field, target, null);
            default:
                if (inPageSet)
                    return new BoundField(field, null, "поле мастер-страницы");
                return new BoundField(field, TakeOnce(scope, name, bound, wantGroup: false), null);
        }
    }

    /// <summary>
    /// «once»: первый ещё не занятый одноимённый узел группы; если таких не
    /// осталось — новый узел в конце группы.
    /// </summary>
    private static DataNode TakeOnce(DataNode scope, string name, Bound bound, bool wantGroup)
    {
        if (scope.Element != null)
        {
            foreach (var candidate in scope.Element.Elements())
            {
                if (candidate.Name.LocalName != name || bound.Used.Contains(candidate)) continue;
                if (!wantGroup && IsGroup(candidate)) continue;
                bound.Used.Add(candidate);
                return DataNode.Existing(candidate);
            }
        }
        return NewNode(scope, name, bound);
    }

    /// <summary>
    /// «global»: все одноимённые глобальные поля делят один узел. Он ищется
    /// среди детей текущей группы и её предков, затем во всей записи; если
    /// его нет — создаётся на верхнем уровне записи.
    /// </summary>
    private static DataNode TakeGlobal(DataNode scope, List<DataNode> ancestors, string name, Bound bound)
    {
        if (bound.Globals.TryGetValue(name, out var shared))
            return shared;
        DataNode? found = null;
        foreach (var level in new[] { scope }.Concat(Enumerable.Reverse(ancestors)))
        {
            var match = level.Element?.Elements().FirstOrDefault(e => e.Name.LocalName == name && !IsGroup(e));
            if (match != null) { found = DataNode.Existing(match); break; }
        }
        if (found == null && bound.Record.Element != null)
        {
            var match = bound.Record.Element.Descendants().FirstOrDefault(e => e.Name.LocalName == name && !IsGroup(e));
            if (match != null) found = DataNode.Existing(match);
        }
        found ??= NewNode(bound.Record, name, bound);
        if (found.Element != null) bound.Used.Add(found.Element);
        bound.Globals[name] = found;
        return found;
    }

    private static DataNode NewNode(DataNode scope, string name, Bound bound)
    {
        var existing = scope.Element?.Elements().Count(e => e.Name.LocalName == name) ?? 0;
        var key = (scope, name);
        bound.Virtual.TryGetValue(key, out var allocated);
        if (!bound.Plan(1))
            throw new FormatException("Шаблон XFA слишком велик.");
        bound.Virtual[key] = allocated + 1;
        return DataNode.Virtual(scope, name, existing + allocated);
    }

    /// <summary>
    /// Явная привязка: $.a.b, $record.a[2], $data.a, a.b (относительно
    /// текущей группы); [*] — номер экземпляра подформы. Остальное (поиск по
    /// потомкам, предикаты, другие модели) — null: угадывать место нельзя.
    /// </summary>
    private static DataNode? ResolveRef(string? reference, DataNode scope, Bound bound, int instance)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var text = reference.Trim();
        if (text.Contains("..", StringComparison.Ordinal) || text.Contains('#') || text.Contains('(') ||
            text.StartsWith('!') || text.StartsWith("xfa.", StringComparison.Ordinal))
            return null;
        var parts = text.Split('.');
        if (parts.Length > MaxRefParts) return null;
        DataNode node;
        var first = 0;
        switch (parts[0])
        {
            case "$": node = scope; first = 1; break;
            case "$record": node = bound.Record; first = 1; break;
            case "$data": node = bound.Data; first = 1; break;
            default:
                if (parts[0].StartsWith('$')) return null;
                node = scope;
                break;
        }
        for (var i = first; i < parts.Length; i++)
        {
            var part = parts[i];
            var name = part;
            var index = 0;
            var open = part.IndexOf('[');
            if (open >= 0)
            {
                if (!part.EndsWith(']')) return null;
                name = part[..open];
                var inner = part[(open + 1)..^1];
                if (inner == "*") index = instance;
                else if (!int.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out index)) return null;
            }
            if (name.Length == 0 || !XmlNameIsValid(name)) return null;
            var existing = node.Element?.Elements().Where(e => e.Name.LocalName == name).ToList();
            // Пустые узлы перед нужным создаются, но не тысячами: такой номер —
            // не бланк, а мусор или атака.
            // long: номер int.MaxValue + 1 иначе перевернулся бы в отрицательный.
            var missing = (long)index + 1 - (existing?.Count ?? 0);
            if (missing > MaxNewSiblings || !bound.Plan((int)Math.Max(0, missing))) return null;
            node = existing != null && index < existing.Count
                ? DataNode.Existing(existing[index])
                : DataNode.Virtual(node, name, index);
        }
        return node;
    }

    private static bool XmlNameIsValid(string name)
    {
        try
        {
            XmlConvert.VerifyNCName(name);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary>Группа данных (а не значение): есть дочерние элементы, кроме форматированного текста.</summary>
    private static bool IsGroup(XElement e) =>
        e.Attribute(XName.Get("dataNode", DataNamespace))?.Value == "dataGroup" ||
        e.Elements().Any(c => c.Name.Namespace != XhtmlNamespace);

    /// <summary>Узел данных: существующий или ещё не созданный (родитель + имя + номер среди одноимённых).</summary>
    private sealed class DataNode
    {
        public XElement? Element { get; private set; }
        private readonly DataNode? _parent;
        private readonly string _name = "";
        private readonly int _index;

        private DataNode(XElement? element, DataNode? parent, string name, int index)
        {
            Element = element;
            _parent = parent;
            _name = name;
            _index = index;
        }

        public static DataNode Existing(XElement element) => new(element, null, element.Name.LocalName, 0);

        /// <summary>
        /// Одинаков у узлов, которые окажутся одним элементом: у существующего —
        /// сам элемент, у будущего — путь «родитель, имя, номер».
        /// </summary>
        public object Key => Element ?? (object)(_parent!.Key, _name, _index);

        public static DataNode Virtual(DataNode parent, string name, int index) => new(null, parent, name, index);

        /// <summary>
        /// Создаёт узел (и недостающих предков). Перед ним создаются пустые
        /// одноимённые узлы до нужного номера: иначе он оказался бы на чужом
        /// месте, и Adobe отдал бы его другому полю.
        /// </summary>
        public XElement Materialize()
        {
            if (Element != null) return Element;
            var parent = _parent!.Materialize();
            var siblings = parent.Elements().Where(e => e.Name.LocalName == _name).ToList();
            // Запись под xfa:data — уже пользовательские данные, без пространства имён XFA.
            var ns = parent.Name.Namespace == DataNamespace ? XNamespace.None : parent.Name.Namespace;
            while (siblings.Count <= _index)
            {
                var created = new XElement(ns + _name);
                parent.Add(created);
                siblings.Add(created);
            }
            return Element = siblings[_index];
        }
    }

    private sealed class BoundField
    {
        public BoundField(XElement template, DataNode? target, string? reason)
        {
            Template = template;
            Target = target;
            Reason = reason;
        }

        public XElement Template { get; }
        public DataNode? Target { get; }
        public string? Reason { get; }

        /// <summary>Значение AcroForm в виде, в котором его хранит XFA.</summary>
        public string ToDataValue(XfaFieldValue value)
        {
            if (Template.Name.LocalName == "exclGroup")
            {
                // Группа переключателей хранит «включённое» значение выбранного.
                var options = Template.Elements().Where(e => e.Name.LocalName == "field").ToList();
                if (value.CheckedControl < 0) return "";
                var byExport = options.FirstOrDefault(o => OnValue(o, 0) == value.CheckedExport);
                var chosen = byExport ?? (value.CheckedControl < options.Count ? options[value.CheckedControl] : null);
                return chosen == null ? "" : OnValue(chosen, value.CheckedControl);
            }
            var ui = Template.Elements().FirstOrDefault(e => e.Name.LocalName == "ui")?.Elements().FirstOrDefault();
            if (ui?.Name.LocalName == "checkButton")
            {
                var items = Items(Template);
                return value.CheckedControl >= 0
                    ? (items.Count > 0 ? items[0] : "1")
                    : (items.Count > 1 ? items[1] : "0");
            }
            // Перевод строки в поле AcroForm — \r, в данных XFA — \n.
            return value.Value.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static string OnValue(XElement option, int position)
        {
            var items = Items(option);
            return items.Count > 0 ? items[0] : (position + 1).ToString(CultureInfo.InvariantCulture);
        }

        private static List<string> Items(XElement field) =>
            field.Elements().FirstOrDefault(e => e.Name.LocalName == "items")?.Elements()
                .Select(e => e.Value).ToList() ?? new List<string>();
    }
}
