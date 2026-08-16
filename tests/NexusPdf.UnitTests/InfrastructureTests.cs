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
    public void Settings_Corrupted_File_Falls_Back_To_Defaults()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ мусор ");
        var loaded = new JsonSettingsStore(path).Load();
        // Умолчание языка — английский: программа рассчитана не только на
        // русскоговорящих, а выбранный язык хранится в этом же файле.
        Assert.Equal("en", loaded.Language);
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

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeFileReplace.WriteAndReplaceAsync(
                target,
                tmp => File.WriteAllTextAsync(tmp, "BROKEN"),
                _ => throw new InvalidDataException("проверка не пройдена"),
                keepBackup: false,
                CancellationToken.None));

        Assert.Equal("OLD", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(_dir, "*.nexustmp-*"));
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
