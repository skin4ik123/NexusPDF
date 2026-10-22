using System.Text;
using System.Xml.Linq;
using NexusPdf.Application;
using NexusPdf.Pdf.Abstractions;
using NexusPdf.Pdf.MuPdf;
using NexusPdf.Pdf.Pdfium;

namespace NexusPdf.PdfEngineTests;

/// <summary>
/// Гибридные формы (AcroForm + XFA, например бланк CBP I-418). Жалоба: такой
/// заполненный бланк открывался пустым и на экране, и при печати, хотя
/// Chrome, Firefox и Adobe Reader показывают значения. Окружение форм
/// создавалось только для чистого AcroForm, а значения полей рисует лишь оно.
///
/// Заполнение: поля правятся как AcroForm, а при сохранении изменённые
/// значения дописываются и в пакет XFA datasets — именно его показывает Adobe
/// Reader. Без этого он показал бы прежние или пустые значения.
/// </summary>
public sealed class XfaFormTests : IAsyncLifetime
{
    private const string Value = "HYBRID 42";

    // Поле [100 600 400 640] простой фикстуры на странице 612x792; текст прижат
    // к левому краю, в отображаемых координатах (y сверху) его строка около y = 172.
    private const double FieldX = 250, FieldY = 172, InkX = 130;

    // Центры полей BuildRichHybrid в отображаемых координатах (y сверху).
    private static readonly (double X, double Y) Name = (250, 77), Vessel = (250, 127), Agree = (115, 177),
        ChoiceB = (215, 227), Note = (250, 277), Row1 = (250, 377), Code = (250, 477);

    private PdfiumRenderEngine _engine = null!;

    public Task InitializeAsync()
    {
        // Так же, как в программе: данные XFA гибридных форм пишет MuPDF.
        _engine = new PdfiumRenderEngine(new MuPdfXfaPacketWriter());
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _engine.DisposeAsync();

    // ----- Отображение -----

    [Fact]
    public async Task Hybrid_Form_Shows_Values_On_Screen()
    {
        var path = PdfFixture.WriteXfaFormToTemp("hybrid.pdf", PdfFixture.XfaKind.Hybrid, Value);
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);
        Assert.Equal(3, await doc.GetFormTypeAsync(CancellationToken.None));

        // Ни режима форм, ни клика: обычный рендер открытого документа.
        Assert.True(InkInField(await Render(doc), InkX, FieldY),
            "значение гибридной формы не видно на экране");
    }

    [Fact]
    public async Task Hybrid_Form_Prints_Values()
    {
        var path = PdfFixture.WriteXfaFormToTemp("hybrid-print.pdf", PdfFixture.XfaKind.Hybrid, Value);
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);

        var withValues = await doc.RenderPageForPrintAsync(0, 612, 792, 0,
            new PrintContentOptions(IncludeAnnotations: true, OnlyPrintableAnnotations: true, IncludeFormFields: true),
            CancellationToken.None);
        Assert.True(InkInField(withValues, InkX, FieldY), "значение гибридной формы не попало в печать");

        // Контроль: чернила в поле — именно значение формы, а не содержимое страницы.
        var withoutFields = await doc.RenderPageForPrintAsync(0, 612, 792, 0,
            new PrintContentOptions(IncludeAnnotations: true, OnlyPrintableAnnotations: true, IncludeFormFields: false),
            CancellationToken.None);
        Assert.False(InkInField(withoutFields, InkX, FieldY), "поле напечаталось, хотя формы исключены");
    }

    [Fact]
    public async Task Hybrid_Form_Survives_Save_As_Hybrid()
    {
        var path = PdfFixture.WriteXfaFormToTemp("hybrid-save.pdf", PdfFixture.XfaKind.Hybrid, Value);
        var target = Path.Combine(Path.GetDirectoryName(path)!, "hybrid-saved.pdf");
        await using (var doc = await _engine.OpenAsync(path, null, CancellationToken.None))
        {
            await Render(doc);
            await doc.SaveCurrentAsync(target, CancellationToken.None);
        }

        // XFA-часть не выброшена: для Adobe Reader форма та же.
        await using var reopened = await _engine.OpenAsync(target, null, CancellationToken.None);
        Assert.Equal(3, await reopened.GetFormTypeAsync(CancellationToken.None));
        Assert.True(InkInField(await Render(reopened), InkX, FieldY), "значение не видно в сохранённой копии");
    }

    [Fact]
    public async Task Plain_AcroForm_Still_Shows_Values_And_Is_Editable()
    {
        var path = PdfFixture.WriteXfaFormToTemp("acro.pdf", PdfFixture.XfaKind.None, Value);
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);
        Assert.Equal(1, await doc.GetFormTypeAsync(CancellationToken.None));
        Assert.True(InkInField(await Render(doc), InkX, FieldY), "значение AcroForm не видно на экране");
        Assert.True(await doc.InitFormsAsync(CancellationToken.None));
        Assert.Equal(PdfFormFieldKind.Text,
            await doc.FormClickAsync(0, 0, FieldX, FieldY, CancellationToken.None));
    }

    [Fact]
    public async Task Dynamic_Xfa_Reports_Type_And_Offers_No_Fields()
    {
        var path = PdfFixture.WriteXfaFormToTemp("dynamic.pdf", PdfFixture.XfaKind.Dynamic, Value);
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);

        // По типу 2 программа говорит «XFA-форма, отображение не поддерживается».
        Assert.Equal(2, await doc.GetFormTypeAsync(CancellationToken.None));
        Assert.False(await doc.InitFormsAsync(CancellationToken.None));
        Assert.Empty(await doc.GetFormFieldAreasAsync(0, 0, CancellationToken.None));
        var image = await Render(doc); // страница рисуется без ошибок — тем, что в ней есть
        Assert.Equal(612, image.PixelWidth);
    }

    // ----- Заполнение -----

    [Fact]
    public async Task Filling_A_Hybrid_Form_Writes_The_Values_Into_Xfa_Data()
    {
        var path = PdfFixture.WriteRichHybridToTemp("fill.pdf");
        var target = Path.Combine(Path.GetDirectoryName(path)!, "filled.pdf");
        await using (var doc = await _engine.OpenAsync(path, null, CancellationToken.None))
        {
            Assert.Equal(3, await doc.GetFormTypeAsync(CancellationToken.None));
            Assert.True(await doc.InitFormsAsync(CancellationToken.None));

            await Type(doc, Name, "Ivan Petrov");
            await Type(doc, Vessel, "TEST SHIP");
            await Click(doc, Agree);
            await Click(doc, ChoiceB);
            await Type(doc, Row1, "second");
            await Type(doc, Code, "C-7");
            await doc.FormKillFocusAsync(CancellationToken.None);
            Assert.True(doc.HasUnsavedFormChanges);
            await doc.SaveCurrentAsync(target, CancellationToken.None);
        }

        var data = ReadData(target, null);
        Assert.Equal("Ivan Petrov", Node(data, "Page1", "Name"));
        Assert.Equal("1", Node(data, "Page1", "Agree"));
        Assert.Equal("beta", Node(data, "Page1", "Choice"));
        // Повторяющееся имя: первое значение не тронуто, второе — новым узлом.
        Assert.Equal(new[] { "first", "second" }, data.Elements("Row").Select(e => e.Value));
        // global — один узел на верхнем уровне записи.
        Assert.Equal("TEST SHIP", Assert.Single(data.Elements("Vessel")).Value);
        // dataRef $record.Extra.Code — путь создан.
        Assert.Equal("C-7", Node(data, "Extra", "Code"));
        // Поле без привязки в данные не попадает.
        Assert.Empty(data.Descendants("Note"));

        // AcroForm тоже несёт значения, а второе поле с тем же global — то же название.
        await using var reopened = await _engine.OpenAsync(target, null, CancellationToken.None);
        Assert.Equal(3, await reopened.GetFormTypeAsync(CancellationToken.None));
        var values = await FieldValues(reopened);
        Assert.Equal("Ivan Petrov", values["form1[0].Page1[0].Name[0]"]);
        Assert.Equal("TEST SHIP", values["form1[0].Page1[0].Vessel[0]"]);
        Assert.Equal("TEST SHIP", values["form1[0].#subform[1].Vessel[0]"]);
        Assert.Equal("first", values["form1[0].#subform[1].Row[0]"]);
        Assert.Equal("second", values["form1[0].#subform[1].Row[1]"]);
        Assert.Equal("C-7", values["form1[0].#subform[1].Code[0]"]);
    }

    [Fact]
    public async Task Values_From_Another_Page_Reach_Xfa_Data_And_Shared_Fields()
    {
        // Случай с бланка CBP: флажок отмечен на первой странице, а при
        // сохранении активна уже вторая — отметка всё равно должна дойти.
        // Название судна с первой страницы появляется и на второй.
        var path = PdfFixture.WriteTwoPageHybridToTemp("two-pages.pdf");
        var target = Path.Combine(Path.GetDirectoryName(path)!, "two-pages-filled.pdf");
        await using (var doc = await _engine.OpenAsync(path, null, CancellationToken.None))
        {
            Assert.True(await doc.InitFormsAsync(CancellationToken.None));
            await Click(doc, (115, 177));                              // Agree, страница 1
            await Type(doc, (250, 127), "SHIP");                       // Vessel, страница 1
            await doc.FormClickAsync(1, 0, 250, 77, CancellationToken.None); // Name, страница 2
            foreach (var c in "N")
                await doc.FormCharAsync(c, CancellationToken.None);
            await doc.FormKillFocusAsync(CancellationToken.None);
            await doc.SaveCurrentAsync(target, CancellationToken.None);
        }

        var data = ReadData(target, null);
        Assert.Equal("1", Node(data, "Page1", "Agree"));
        Assert.Equal("N", Node(data, "Page2", "Name"));
        Assert.Equal("SHIP", Assert.Single(data.Elements("Vessel")).Value);

        await using var reopened = await _engine.OpenAsync(target, null, CancellationToken.None);
        var second = (await reopened.GetAnnotationsAsync(1, CancellationToken.None))
            .Where(a => a.Subtype == 20).ToDictionary(a => a.Author, a => a.Value);
        Assert.Equal("SHIP", second["form1[0].Page2[0].Vessel[0]"]);
    }

    [Fact]
    public async Task Without_An_Xfa_Writer_A_Hybrid_Form_Is_Only_Shown()
    {
        // Движок без записи XFA-данных не даёт заполнять гибрид: изменения
        // не дошли бы до Adobe Reader. Обычный AcroForm заполняется как раньше.
        await using var bare = new PdfiumRenderEngine();
        var hybrid = PdfFixture.WriteRichHybridToTemp("bare.pdf");
        await using (var doc = await bare.OpenAsync(hybrid, null, CancellationToken.None))
        {
            Assert.False(await doc.InitFormsAsync(CancellationToken.None));
            Assert.Equal(PdfFormFieldKind.Unknown,
                await doc.FormClickAsync(0, 0, Name.X, Name.Y, CancellationToken.None));
        }
        var acro = PdfFixture.WriteXfaFormToTemp("bare-acro.pdf", PdfFixture.XfaKind.None, Value);
        await using (var doc = await bare.OpenAsync(acro, null, CancellationToken.None))
            Assert.True(await doc.InitFormsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_Field_Without_A_Place_In_Xfa_Data_Is_Read_Only()
    {
        var path = PdfFixture.WriteRichHybridToTemp("locked.pdf");
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);
        Assert.True(await doc.InitFormsAsync(CancellationToken.None));

        var areas = await doc.GetFormFieldAreasAsync(0, 0, CancellationToken.None);
        Assert.True(Assert.Single(areas, a => a.Contains(Note.X, Note.Y)).IsReadOnly,
            "поле с привязкой none должно быть только для чтения");
        Assert.False(Assert.Single(areas, a => a.Contains(Name.X, Name.Y)).IsReadOnly);

        Assert.Equal(PdfFormFieldKind.Unknown,
            await doc.FormClickAsync(0, 0, Note.X, Note.Y, CancellationToken.None));
        foreach (var c in "zzz")
            await doc.FormCharAsync(c, CancellationToken.None);
        await doc.FormKillFocusAsync(CancellationToken.None);
        Assert.DoesNotContain("zzz", (await FieldValues(doc)).Values);
        Assert.False(doc.HasUnsavedFormChanges);
    }

    [Fact]
    public async Task Tab_Moves_On_But_Typing_Into_A_Read_Only_Xfa_Field_Is_Ignored()
    {
        var path = PdfFixture.WriteRichHybridToTemp("tab.pdf");
        await using var doc = await _engine.OpenAsync(path, null, CancellationToken.None);
        Assert.True(await doc.InitFormsAsync(CancellationToken.None));
        const int tab = 0x09;

        // Tab из имени ведёт в следующее поле, и ввод туда работает.
        await Type(doc, Name, "N");
        await doc.FormKeyDownAsync(tab, shift: false, CancellationToken.None);
        await doc.FormCharAsync('V', CancellationToken.None);
        // Из второго переключателя Tab ведёт в поле с привязкой none: ввод в нём не принимается.
        await Click(doc, ChoiceB);
        await doc.FormKeyDownAsync(tab, shift: false, CancellationToken.None);
        foreach (var c in "zzz")
            await doc.FormCharAsync(c, CancellationToken.None);
        await doc.FormKillFocusAsync(CancellationToken.None);

        var values = await FieldValues(doc);
        Assert.Equal("N", values["form1[0].Page1[0].Name[0]"]);
        Assert.Equal("V", values["form1[0].Page1[0].Vessel[0]"]);
        Assert.Equal("", values["form1[0].Page1[0].Note[0]"]);
        Assert.DoesNotContain(values.Values, v => v.Contains('z'));
    }

    [Fact]
    public async Task Saving_Without_Changes_Leaves_Xfa_Data_As_It_Was()
    {
        var path = PdfFixture.WriteRichHybridToTemp("untouched.pdf");
        var target = Path.Combine(Path.GetDirectoryName(path)!, "untouched-saved.pdf");
        var before = MuPdfXfaPacketWriter.ReadPacket(path, null, "datasets");
        await using (var doc = await _engine.OpenAsync(path, null, CancellationToken.None))
        {
            Assert.True(await doc.InitFormsAsync(CancellationToken.None));
            await Click(doc, Name); // фокус без правки
            await doc.FormKillFocusAsync(CancellationToken.None);
            await doc.SaveCurrentAsync(target, CancellationToken.None);
        }
        Assert.Equal(before, MuPdfXfaPacketWriter.ReadPacket(target, null, "datasets"));
    }

    [Fact]
    public async Task Incremental_Save_Keeps_The_Original_Bytes_And_Adds_Xfa_Data()
    {
        var path = PdfFixture.WriteRichHybridToTemp("signed-like.pdf");
        var target = Path.Combine(Path.GetDirectoryName(path)!, "appended.pdf");
        await using (var doc = await _engine.OpenAsync(path, null, CancellationToken.None))
        {
            Assert.True(await doc.InitFormsAsync(CancellationToken.None));
            await Type(doc, Name, "Appended");
            await doc.FormKillFocusAsync(CancellationToken.None);
            await doc.SaveCurrentAsync(target, incremental: true, CancellationToken.None);
        }

        // Подписанные байты остаются как были: всё новое — только дописано.
        var original = await File.ReadAllBytesAsync(path);
        var saved = await File.ReadAllBytesAsync(target);
        Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original), "исходные байты изменены");
        Assert.Equal("Appended", Node(ReadData(target, null), "Page1", "Name"));
    }

    [Fact]
    public async Task Save_Service_Carries_Hybrid_Values_Into_Xfa_Data()
    {
        var path = PdfFixture.WriteRichHybridToTemp("service.pdf");
        var target = Path.Combine(Path.GetDirectoryName(path)!, "service-saved.pdf");
        var document = await OpenedDocument.OpenAsync(_engine, path, null, CancellationToken.None);
        await using (document)
        {
            var handle = document.PrimaryHandle;
            Assert.True(await handle.InitFormsAsync(CancellationToken.None));
            await Type(handle, Name, "Through service");
            await handle.FormKillFocusAsync(CancellationToken.None);
            await new SaveService(_engine).SaveAsAsync(document, target, keepBackup: false, CancellationToken.None);
        }
        Assert.Equal("Through service", Node(ReadData(target, null), "Page1", "Name"));
    }

    [Fact]
    public async Task Saving_With_The_Cursor_In_A_Shared_Field_Completes_The_Save()
    {
        // Ctrl+S, пока курсор ещё в названии судна: значение фиксируется и
        // переносится на вторую страницу до сохранения, а не «во время» него —
        // иначе документ остался бы помеченным несохранённым.
        var path = PdfFixture.WriteTwoPageHybridToTemp("cursor.pdf");
        var target = Path.Combine(Path.GetDirectoryName(path)!, "cursor-saved.pdf");
        var document = await OpenedDocument.OpenAsync(_engine, path, null, CancellationToken.None);
        await using (document)
        {
            var handle = document.PrimaryHandle;
            Assert.True(await handle.InitFormsAsync(CancellationToken.None));
            await Type(handle, (250, 127), "SHIP");
            var result = await new SaveService(_engine).SaveAsAsync(document, target, keepBackup: false, CancellationToken.None);
            Assert.True(result.Reopened, "сохранение сочло фиксацию поля правкой во время записи");
        }
        Assert.Equal("SHIP", Assert.Single(ReadData(target, null).Elements("Vessel")).Value);
        await using var reopened = await _engine.OpenAsync(target, null, CancellationToken.None);
        var second = (await reopened.GetAnnotationsAsync(1, CancellationToken.None))
            .Where(a => a.Subtype == 20).ToDictionary(a => a.Author, a => a.Value);
        Assert.Equal("SHIP", second["form1[0].Page2[0].Vessel[0]"]);
    }

    [Fact]
    public async Task Encrypted_Hybrid_Stays_Encrypted_And_Gets_Xfa_Data()
    {
        var plain = PdfFixture.WriteRichHybridToTemp("plain.pdf");
        var encrypted = Path.Combine(Path.GetDirectoryName(plain)!, "encrypted.pdf");
        using (var mu = new MuPDF.NET.Document(plain))
            mu.Save(encrypted, encryption: 5 /* mupdf PDF_ENCRYPT_AES_256 */, userPW: "pw", ownerPW: "owner-pw");
        var target = Path.Combine(Path.GetDirectoryName(plain)!, "encrypted-filled.pdf");

        await using (var doc = await _engine.OpenAsync(encrypted, "pw", CancellationToken.None))
        {
            Assert.True(await doc.InitFormsAsync(CancellationToken.None));
            await Type(doc, Name, "Secret");
            await doc.FormKillFocusAsync(CancellationToken.None);
            await doc.SaveCurrentAsync(target, CancellationToken.None);
        }

        using (var check = new MuPDF.NET.Document(target))
            Assert.True(check.NeedsPass, "сохранённая копия потеряла пароль");
        Assert.Equal("Secret", Node(ReadData(target, "pw"), "Page1", "Name"));
    }

    // ----- Вспомогательное -----

    private static async Task Click(IPdfDocumentHandle doc, (double X, double Y) at) =>
        await doc.FormClickAsync(0, 0, at.X, at.Y, CancellationToken.None);

    private static async Task Type(IPdfDocumentHandle doc, (double X, double Y) at, string text)
    {
        await Click(doc, at);
        foreach (var c in text)
            await doc.FormCharAsync(c, CancellationToken.None);
    }

    private static async Task<Dictionary<string, string>> FieldValues(IPdfDocumentHandle doc) =>
        (await doc.GetAnnotationsAsync(0, CancellationToken.None))
            .Where(a => a.Subtype == 20 && a.Author.Length > 0)
            .GroupBy(a => a.Author)
            .ToDictionary(g => g.Key, g => g.First().Value);

    /// <summary>Запись данных XFA (первый элемент под xfa:data) сохранённого файла.</summary>
    private static XElement ReadData(string path, string? password)
    {
        var bytes = MuPdfXfaPacketWriter.ReadPacket(path, password, "datasets");
        Assert.NotNull(bytes);
        var datasets = XDocument.Parse(Encoding.UTF8.GetString(bytes!));
        var data = datasets.Root!.Elements().Single(e => e.Name.LocalName == "data");
        return data.Elements().First();
    }

    private static string? Node(XElement record, string group, string name) =>
        record.Element(group)?.Element(name)?.Value;

    private static Task<RenderedPageImage> Render(IPdfDocumentHandle doc) =>
        doc.RenderPageAsync(0, 612, 792, 0, CancellationToken.None);

    /// <summary>Есть ли в окрестности точки (±40×±14 px при масштабе 1 px/pt) не-белые пиксели.</summary>
    private static bool InkInField(RenderedPageImage image, double x, double y)
    {
        var dark = 0;
        for (var dy = -14; dy <= 14; dy++)
        {
            var row = (int)y + dy;
            if (row < 0 || row >= image.PixelHeight) continue;
            for (var dx = -40; dx <= 40; dx++)
            {
                var col = (int)x + dx;
                if (col < 0 || col >= image.PixelWidth) continue;
                var i = row * image.Stride + col * 4;
                if (image.Bgra[i] < 200 || image.Bgra[i + 1] < 200 || image.Bgra[i + 2] < 200)
                    dark++;
            }
        }
        return dark > 10;
    }
}
