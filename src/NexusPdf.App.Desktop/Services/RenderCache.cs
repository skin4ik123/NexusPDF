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

    /// <summary>
    /// Прежний потолок в 256 МБ. На современном экране страница в масштабе по
    /// ширине занимает 12–15 МБ, то есть в кэш помещалось СЕМНАДЦАТЬ страниц:
    /// на документе в триста листов он вымывался постоянно, и возврат к уже
    /// прочитанной странице означал полный повторный рендер.
    /// </summary>
    public const int LegacyDefaultMegabytes = 256;

    public RenderCache(int budgetMegabytes) => SetBudget(Resolve(budgetMegabytes));

    /// <summary>
    /// Бюджет по памяти машины: восьмая часть, но не меньше 384 МБ и не больше
    /// 1,5 ГБ. Так на слабом ноутбуке кэш остаётся скромным, а на рабочей
    /// машине держит сотню страниц и перестаёт мешать чтению.
    ///
    /// Значение, оставшееся от прежнего умолчания, заменяется вычисленным:
    /// это не «настройка пользователя», а старый потолок, который и был
    /// причиной тормозов.
    /// </summary>
    public static int Resolve(int settingMegabytes)
    {
        if (settingMegabytes != LegacyDefaultMegabytes && settingMegabytes > 0)
            return settingMegabytes;

        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (available <= 0) return LegacyDefaultMegabytes;

        var eighth = available / 8 / (1024 * 1024);
        return (int)Math.Clamp(eighth, 384, 1536);
    }

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
