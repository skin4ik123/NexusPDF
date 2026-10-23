using NexusPdf.Infrastructure;

namespace NexusPdf.UnitTests;

public sealed class InfrastructureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NexusPdfTests", Guid.NewGuid().ToString("N"));

    public InfrastructureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Settings_Roundtrip_Preserves_Values()
    {
        var store = new JsonSettingsStore(Path.Combine(_dir, "settings.json"));
        var settings = new AppSettings { Language = "en", Theme = "dark" };
        settings.TouchRecent(@"C:\docs\а.pdf");
        settings.TouchRecent(@"C:\docs\б.pdf");
        store.Save(settings);

        var loaded = store.Load();
        Assert.Equal("en", loaded.Language);
        Assert.Equal("dark", loaded.Theme);
        Assert.Equal(new[] { @"C:\docs\б.pdf", @"C:\docs\а.pdf" }, loaded.RecentFiles);
    }

    [Fact]
    public void Text_Settings_Have_Defaults_Roundtrip_And_Survive_Nulls()
    {
        var defaults = new AppSettings().DefaultTextStyle;
        Assert.Equal(("Segoe UI", 11.0, false, false, 0xFF000000u, "Left", 1.15),
            (defaults.Family, defaults.SizePt, defaults.Bold, defaults.Italic, defaults.ColorArgb, defaults.Align, defaults.LineSpacing));

        var path = Path.Combine(_dir, "settings.json");
        var store = new JsonSettingsStore(path);
        var settings = new AppSettings();
        settings.DefaultTextStyle.Family = "Arial";
        settings.DefaultTextStyle.SizePt = 14;
        for (var i = 0; i < 10; i++)
            settings.TouchRecentFont($"Font {i}");
        settings.TouchRecentFont("font 3");
        store.Save(settings);

        var loaded = store.Load();
        Assert.Equal(("Arial", 14.0), (loaded.DefaultTextStyle.Family, loaded.DefaultTextStyle.SizePt));
        Assert.Equal(AppSettings.MaxRecentFonts, loaded.RecentFonts.Count);
        Assert.Equal("font 3", loaded.RecentFonts[0]);
        Assert.Single(loaded.RecentFonts, f => f.Equals("Font 3", StringComparison.OrdinalIgnoreCase));

        // Файл старой версии или испорченный вручную: пустые значения — умолчания.
        File.WriteAllText(path, "{ \"RecentFonts\": null, \"DefaultTextStyle\": null }");
        var repaired = store.Load();
        Assert.Empty(repaired.RecentFonts);
        Assert.Equal("Segoe UI", repaired.DefaultTextStyle.Family);
    }

    [Fact]
    public void Settings_Corrupted_File_Falls_Back_To_Defaults()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ мусор ");
        var loaded = new JsonSettingsStore(path).Load();
        // Умолчание языка — пусто, то есть «как в системе»: программа
        // переведена на три языка, и открываться по-английски на русской
        // Windows ей незачем. Выбранный вручную язык хранится в этом же файле.
        Assert.Equal("", loaded.Language);
    }

    [Fact]
    public void TouchRecent_Moves_Duplicate_To_Front_And_Limits_Count()
    {
        var settings = new AppSettings();
        for (var i = 0; i < 20; i++)
            settings.TouchRecent($@"C:\docs\{i}.pdf");
        settings.TouchRecent(@"C:\docs\7.pdf");

        Assert.Equal(AppSettings.MaxRecentFiles, settings.RecentFiles.Count);
        Assert.Equal(@"C:\docs\7.pdf", settings.RecentFiles[0]);
        Assert.Single(settings.RecentFiles, p => p == @"C:\docs\7.pdf");
    }

    [Fact]
    public async Task SafeReplace_Success_Replaces_Target()
    {
        var target = Path.Combine(_dir, "doc.pdf");
        await File.WriteAllTextAsync(target, "OLD");

        await SafeFileReplace.WriteAndReplaceAsync(
            target,
            tmp => File.WriteAllTextAsync(tmp, "NEW"),
            _ => Task.CompletedTask,
            keepBackup: false,
            CancellationToken.None);

        Assert.Equal("NEW", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(_dir, "*.nexustmp-*"));
    }

    [Fact]
    public async Task SafeReplace_Validation_Failure_Keeps_Original_Intact()
    {
        var target = Path.Combine(_dir, "doc.pdf");
        await File.WriteAllTextAsync(target, "OLD");

        // Неудачная проверка называется проверкой (а не безликой ошибкой
        // записи), исходная причина — внутри.
        var error = await Assert.ThrowsAsync<SaveFailedException>(() =>
            SafeFileReplace.WriteAndReplaceAsync(
                target,
                tmp => File.WriteAllTextAsync(tmp, "BROKEN"),
                _ => throw new InvalidDataException("проверка не пройдена"),
                keepBackup: false,
                CancellationToken.None));
        Assert.Equal(SaveFailure.Validation, error.Failure);
        Assert.IsType<InvalidDataException>(error.InnerException);

        Assert.Equal("OLD", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(_dir, "*.nexustmp-*"));
    }

    [Fact]
    public async Task SafeReplace_Works_While_The_Target_Is_Held_Open_With_Delete_Sharing()
    {
        // Открытый документ держит свой файл (с разрешением на удаление) и
        // больше не закрывает его перед подменой: при неудаче вкладка
        // оставалась без файла. Переименование такого файла обязано работать.
        var target = Path.Combine(_dir, "held.pdf");
        await File.WriteAllTextAsync(target, "OLD");
        using var held = new FileStream(target, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var outcome = await SafeFileReplace.WriteAndReplaceRetainingAsync(
            target, tmp => File.WriteAllTextAsync(tmp, "NEW"), _ => Task.CompletedTask,
            backupPath: null, beforeReplace: null, CancellationToken.None);

        Assert.Equal("NEW", await File.ReadAllTextAsync(target));
        Assert.NotNull(outcome.RetiredPath);
        // Прежнее содержимое по-прежнему читается через открытый дескриптор.
        using var reader = new StreamReader(held);
        Assert.Equal("OLD", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task SafeReplace_Refuses_A_Read_Only_Target_Before_Writing()
    {
        var target = Path.Combine(_dir, "ro.pdf");
        await File.WriteAllTextAsync(target, "OLD");
        File.SetAttributes(target, FileAttributes.ReadOnly);
        try
        {
            var error = await Assert.ThrowsAsync<SaveFailedException>(() =>
                SafeFileReplace.WriteAndReplaceAsync(
                    target, tmp => File.WriteAllTextAsync(tmp, "NEW"), _ => Task.CompletedTask,
                    keepBackup: false, CancellationToken.None));
            Assert.Equal(SaveFailure.ReadOnly, error.Failure);
            Assert.Equal("OLD", await File.ReadAllTextAsync(target));
        }
        finally
        {
            File.SetAttributes(target, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task SafeReplace_Creates_Backup_When_Requested()
    {
        var target = Path.Combine(_dir, "doc.pdf");
        await File.WriteAllTextAsync(target, "OLD");

        await SafeFileReplace.WriteAndReplaceAsync(
            target,
            tmp => File.WriteAllTextAsync(tmp, "NEW"),
            _ => Task.CompletedTask,
            keepBackup: true,
            CancellationToken.None);

        Assert.Equal("NEW", await File.ReadAllTextAsync(target));
        Assert.Equal("OLD", await File.ReadAllTextAsync(target + ".bak"));
    }
}
