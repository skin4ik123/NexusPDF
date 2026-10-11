namespace NexusPdf.Infrastructure;

/// <summary>A generated file stays available until the last document using it closes.</summary>
public sealed class TemporaryFileLease : IDisposable
{
    private sealed class Entry
    {
        public int Users;
        public bool Delete = true;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Files = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly Entry _entry;
    private bool _disposed;

    private TemporaryFileLease(string path, Entry entry) { _path = path; _entry = entry; }

    public static TemporaryFileLease Own(string path)
    {
        path = Path.GetFullPath(path);
        lock (Gate)
        {
            if (!Files.TryGetValue(path, out var entry)) Files[path] = entry = new Entry();
            entry.Users++;
            return new TemporaryFileLease(path, entry);
        }
    }

    /// <summary>Ordinary input files are never registered or deleted.</summary>
    public static TemporaryFileLease? TryRetain(string path)
    {
        path = Path.GetFullPath(path);
        lock (Gate)
        {
            if (!Files.TryGetValue(path, out var entry)) return null;
            entry.Users++;
            return new TemporaryFileLease(path, entry);
        }
    }

    /// <summary>A file explicitly saved by the user is no longer disposable.</summary>
    public void Preserve()
    {
        lock (Gate) _entry.Delete = false;
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (--_entry.Users > 0) return;
            Files.Remove(_path);
            if (_entry.Delete) SafeFileReplace.TryDelete(_path);
        }
    }
}
