using System.Windows.Media.Imaging;

namespace NexusPdf.App.Desktop.Services;

/// <summary>
/// LRU-кэш готовых растров страниц с лимитом по байтам. Ключ включает источник,
/// страницу, поворот и ширину в пикселях, поэтому после Undo/Redo и повторного
/// открытия совпадающие растры берутся из кэша мгновенно.
/// </summary>
public sealed class RenderCache
{
    private sealed record Entry(string Key, BitmapSource Bitmap, long Bytes);

    private readonly object _gate = new();
    private readonly LinkedList<Entry> _lru = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new();
    private long _budgetBytes;
    private long _usedBytes;

    public RenderCache(int budgetMegabytes) => SetBudget(budgetMegabytes);

    public void SetBudget(int megabytes)
    {
        lock (_gate)
        {
            _budgetBytes = Math.Max(32, megabytes) * 1024L * 1024L;
            EvictOverBudget();
        }
    }

    public static string MakeKey(Guid sourceId, int sourcePageIndex, int rotation, int pixelWidth) =>
        $"{sourceId:N}:{sourcePageIndex}:{rotation}:{pixelWidth}";

    public BitmapSource? TryGet(string key)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var node))
                return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Bitmap;
        }
    }

    public void Store(string key, BitmapSource bitmap)
    {
        var bytes = (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _map.Remove(key);
                _usedBytes -= existing.Value.Bytes;
            }
            var node = _lru.AddFirst(new Entry(key, bitmap, bytes));
            _map[key] = node;
            _usedBytes += bytes;
            EvictOverBudget();
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            if (_map.Remove(key, out var node))
            {
                _usedBytes -= node.Value.Bytes;
                _lru.Remove(node);
            }
        }
    }

    public void RemoveSource(Guid sourceId)
    {
        var prefix = sourceId.ToString("N") + ":";
        lock (_gate)
        {
            foreach (var key in _map.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                var node = _map[key];
                _usedBytes -= node.Value.Bytes;
                _lru.Remove(node);
                _map.Remove(key);
            }
        }
    }

    private void EvictOverBudget()
    {
        while (_usedBytes > _budgetBytes && _lru.Last is { } last)
        {
            _usedBytes -= last.Value.Bytes;
            _map.Remove(last.Value.Key);
            _lru.RemoveLast();
        }
    }
}
