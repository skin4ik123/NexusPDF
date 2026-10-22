using System.Xml.Linq;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Куда в данных XFA попадает значение поля: правила слияния шаблона с
/// данными, которыми пользуется Adobe Reader. Ошибка здесь не видна нигде,
/// кроме Adobe: значение уехало бы в чужое поле или пропало.
/// </summary>
public sealed class XfaDataBindingTests
{
    private const string T = "http://www.xfa.org/schema/xfa-template/3.3/";

    private static string Template(string body) =>
        $"<template xmlns=\"{T}\"><subform name=\"form1\">{body}</subform></template>";

    private static string Data(string record) =>
        "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><xfa:data>" +
        $"<form1>{record}</form1></xfa:data></xfa:datasets>";

    private static string Field(string name, string extra = "") =>
        $"<field name=\"{name}\"><ui><textEdit/></ui>{extra}</field>";

    private static XElement Record(string datasets) =>
        XDocument.Parse(datasets).Root!.Elements().Single(e => e.Name.LocalName == "data").Elements().First();

    private static XElement Apply(XfaDataBinding binding, params XfaFieldValue[] values)
    {
        var xml = binding.Apply(values, out var skipped);
        Assert.Empty(skipped);
        return Record(xml);
    }

    [Fact]
    public void Same_Names_In_Unnamed_Subforms_Number_Through_And_Bind_In_Order()
    {
        // Как в бланке CBP I-418: одно имя в двух безымянных подформах — сквозная
        // нумерация в именах AcroForm и по порядку в одной группе данных.
        var binding = XfaDataBinding.Create(Template(
                "<subform name=\"Page1\">" + Field("M") + "</subform>" +
                "<subform>" + Field("M") + Field("M") + "</subform>" +
                "<subform><area>" + Field("M") + "</area></subform>"),
            Data("<Page1><M>p</M></Page1><M>a</M><M>b</M><M>c</M>"));

        Assert.True(binding.CanWrite("form1[0].Page1[0].M[0]"));
        Assert.True(binding.CanWrite("form1[0].#subform[1].M[0]"));
        Assert.True(binding.CanWrite("form1[0].#subform[1].M[1]"));
        Assert.True(binding.CanWrite("form1[0].#subform[2].M[2]"));
        Assert.False(binding.CanWrite("form1[0].#subform[2].M[0]"));

        var record = Apply(binding, new XfaFieldValue("form1[0].#subform[2].M[2]", "Z"));
        Assert.Equal(new[] { "a", "b", "Z" }, record.Elements("M").Select(e => e.Value));
        Assert.Equal("p", record.Element("Page1")!.Element("M")!.Value);
    }

    [Fact]
    public void Global_Fields_Share_One_Node_At_The_Record_Level()
    {
        var binding = XfaDataBinding.Create(Template(
                "<subform name=\"Page1\">" + Field("Vessel", "<bind match=\"global\"/>") + "</subform>" +
                "<subform name=\"Page2\">" + Field("Vessel", "<bind match=\"global\"/>") + "</subform>"),
            Data("<Page1/><Page2/>"));

        var shared = Assert.Single(binding.SharedFields);
        Assert.Equal(new[] { "form1[0].Page1[0].Vessel[0]", "form1[0].Page2[0].Vessel[0]" }, shared.OrderBy(n => n));

        var record = Apply(binding, new XfaFieldValue("form1[0].Page2[0].Vessel[0]", "SHIP"));
        Assert.Equal("SHIP", Assert.Single(record.Elements("Vessel")).Value);
        Assert.Empty(record.Element("Page1")!.Elements());
    }

    [Fact]
    public void Missing_Nodes_Are_Created_In_Their_Places()
    {
        var binding = XfaDataBinding.Create(Template(
                "<subform name=\"Page1\">" + Field("A") + Field("B") + "</subform>" +
                Field("Code", "<bind match=\"dataRef\" ref=\"$record.Extra.Item[2]\"/>")),
            Data(""));

        var record = Apply(binding,
            new XfaFieldValue("form1[0].Page1[0].B[0]", "b"),
            new XfaFieldValue("form1[0].Code[0]", "c"));
        Assert.Equal("b", record.Element("Page1")!.Element("B")!.Value);
        // Третий Item — с пустыми перед ним: иначе он оказался бы первым.
        Assert.Equal(new[] { "", "", "c" }, record.Element("Extra")!.Elements("Item").Select(e => e.Value));
    }

    [Fact]
    public void Datasets_Packet_Is_Created_When_The_Form_Has_None()
    {
        var binding = XfaDataBinding.Create(Template(Field("A")), datasets: null);
        var record = Apply(binding, new XfaFieldValue("form1[0].A[0]", "x"));
        Assert.Equal(XName.Get("form1"), record.Name);
        Assert.Equal("x", record.Element("A")!.Value);
    }

    [Fact]
    public void Unbound_And_Master_Page_Fields_Cannot_Be_Written()
    {
        var binding = XfaDataBinding.Create(Template(
                "<pageSet><pageArea name=\"P\">" + Field("Footer") + "</pageArea></pageSet>" +
                Field("Note", "<bind match=\"none\"/>") +
                Field("Odd", "<bind match=\"dataRef\" ref=\"$record..Deep\"/>") +
                Field("Ok")),
            Data(""));

        Assert.False(binding.CanWrite("form1[0].#pageSet[0].P[0].Footer[0]"));
        Assert.False(binding.CanWrite("form1[0].Note[0]"));
        Assert.False(binding.CanWrite("form1[0].Odd[0]"));
        Assert.False(binding.CanWrite("form1[0].Unknown[0]"));
        Assert.True(binding.CanWrite("form1[0].Ok[0]"));

        binding.Apply(new[] { new XfaFieldValue("form1[0].Note[0]", "n") }, out var skipped);
        Assert.Equal(new[] { "form1[0].Note[0]" }, skipped);
    }

    [Fact]
    public void Check_Boxes_And_Radio_Groups_Store_Their_Item_Values()
    {
        var binding = XfaDataBinding.Create(Template(
                "<field name=\"Agree\"><ui><checkButton/></ui><items><text>Y</text><text>N</text></items></field>" +
                "<field name=\"Plain\"><ui><checkButton/></ui></field>" +
                "<exclGroup name=\"G\">" +
                "<field name=\"A\"><ui><checkButton/></ui><items><text>alpha</text></items></field>" +
                "<field name=\"B\"><ui><checkButton/></ui><items><text>beta</text></items></field>" +
                "</exclGroup>"),
            Data("<Agree>N</Agree><Plain>1</Plain><G>alpha</G>"));

        var record = Apply(binding,
            new XfaFieldValue("form1[0].Agree[0]", "Y", CheckedControl: 0, CheckedExport: "Y"),
            new XfaFieldValue("form1[0].Plain[0]", "Off"),
            // Имя состояния не совпало с XFA — выбор по номеру переключателя.
            new XfaFieldValue("form1[0].G[0]", "2", CheckedControl: 1, CheckedExport: "2"));
        Assert.Equal("Y", record.Element("Agree")!.Value);
        Assert.Equal("0", record.Element("Plain")!.Value);
        Assert.Equal("beta", record.Element("G")!.Value);

        record = Apply(binding, new XfaFieldValue("form1[0].G[0]", "Off"));
        Assert.Equal("", record.Element("G")!.Value);
    }

    [Fact]
    public void Text_Keeps_Escaping_Line_Breaks_And_Drops_Rich_Text_Markers()
    {
        var binding = XfaDataBinding.Create(Template(Field("Memo")),
            Data("<Memo xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\" xfa:contentType=\"text/html\">" +
                 "<body xmlns=\"http://www.w3.org/1999/xhtml\"><p>old</p></body></Memo>"));

        var record = Apply(binding, new XfaFieldValue("form1[0].Memo[0]", "a < b & c\rnext"));
        var memo = record.Element("Memo")!;
        Assert.Equal("a < b & c\nnext", memo.Value);
        Assert.Empty(memo.Elements());
        Assert.DoesNotContain(memo.Attributes(), a => a.Name.LocalName == "contentType");
    }

    [Fact]
    public void Untouched_Data_Is_Kept_As_Is()
    {
        var datasets = Data("<A>1</A><Other>keep</Other>");
        var binding = XfaDataBinding.Create(Template(Field("A") + Field("B")), datasets);
        var xml = binding.Apply(new[] { new XfaFieldValue("form1[0].B[0]", "2") }, out _);
        var record = Record(xml);
        Assert.Equal("1", record.Element("A")!.Value);
        Assert.Equal("keep", record.Element("Other")!.Value);
        Assert.Equal("2", record.Element("B")!.Value);
    }

    [Fact]
    public void Documents_With_Dtd_Are_Refused()
    {
        // Сущности DTD из недоверенного файла не раскрываются.
        const string hostile = "<?xml version=\"1.0\"?><!DOCTYPE t [<!ENTITY x \"boom\">]>" +
                               "<template><subform name=\"form1\"/></template>";
        Assert.Throws<FormatException>(() => XfaDataBinding.Create(hostile, null));
    }

    [Fact]
    public void Whole_Xdp_Stream_Is_Split_And_Reassembled()
    {
        var xdp = "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">" + Template(Field("A")) + Data("<A>old</A>") +
                  "<config xmlns=\"http://www.xfa.org/schema/xci/3.0/\"/></xdp:xdp>";
        var (template, datasets) = XfaDataBinding.SplitXdp(xdp);
        Assert.NotNull(template);
        var binding = XfaDataBinding.Create(template!, datasets);
        var updated = XfaDataBinding.ReplaceDatasetsInXdp(xdp, binding.Apply(
            new[] { new XfaFieldValue("form1[0].A[0]", "new") }, out _));

        var root = XDocument.Parse(updated).Root!;
        Assert.Equal(new[] { "template", "datasets", "config" }, root.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("new", root.Descendants().Single(e => e.Name.LocalName == "A" && e.Parent!.Name.LocalName == "form1").Value);
    }
}
