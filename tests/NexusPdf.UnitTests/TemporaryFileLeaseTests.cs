using NexusPdf.Infrastructure;

namespace NexusPdf.UnitTests;

public sealed class TemporaryFileLeaseTests
{
    [Fact]
    public void A_Transferred_Temporary_File_Survives_Until_The_Last_Tab_Closes()
    {
        var path = Path.GetTempFileName();
        var first = TemporaryFileLease.Own(path);
        var second = TemporaryFileLease.TryRetain(path);
        Assert.NotNull(second);
        first.Dispose();
        Assert.True(File.Exists(path));
        second.Dispose();
        Assert.False(File.Exists(path));
        second.Dispose();
    }

    [Fact]
    public void Ordinary_User_Files_Are_Never_Claimed()
    {
        var path = Path.GetTempFileName();
        try { Assert.Null(TemporaryFileLease.TryRetain(path)); Assert.True(File.Exists(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Explicitly_Saved_File_Is_Preserved_For_Every_Tab()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var first = TemporaryFileLease.Own(path))
            using (var second = TemporaryFileLease.TryRetain(path)) first.Preserve();
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }
}
