namespace NexusPdf.Export;

/// <summary>
/// Сколько работы можно вести разом на ЭТОЙ машине.
///
/// Замер показал, где на самом деле уходит время экспорта: вызовы PDFium —
/// 12–25 %, а сжатие картинок нашим же кодом — 75–78 %. Значит ускорять надо
/// своё, обычными потоками .NET, не трогая чужую нативную библиотеку: она не
/// потокобезопасна, и попытка распараллелить её ломалась бы на чужих машинах
/// редко и непредсказуемо.
///
/// Число потоков считается от машины, а не забито числом: на одноядерной
/// работа идёт ровно как раньше, на двенадцати — в несколько ручьёв. Память
/// тоже учитывается: страница-скан в памяти занимает десятки мегабайт, и
/// двенадцать таких разом уронили бы слабый ноутбук.
/// </summary>
public static class ParallelWork
{
    /// <summary>Больше этого потоков не берём даже на мощной машине: дальше упираемся в память и диск.</summary>
    private const int MaxWorkers = 8;

    /// <summary>Сколько памяти разрешено занять «в полёте» — 256 МБ.</summary>
    private const long InFlightBudgetBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Аварийный выключатель. Если на какой-то машине многопоточность поведёт
    /// себя странно, работу можно вернуть в один поток переменной окружения
    /// NEXUSPDF_SINGLE_THREAD=1, не дожидаясь новой версии.
    /// </summary>
    public static bool Disabled { get; } =
        Environment.GetEnvironmentVariable("NEXUSPDF_SINGLE_THREAD") is "1" or "true";

    /// <summary>Сколько задач вести разом, если каждая держит примерно столько байтов.</summary>
    public static int Workers(long bytesPerItem = 0, int items = int.MaxValue)
    {
        if (Disabled) return 1;
        var cores = Math.Max(1, Environment.ProcessorCount - 1);
        var limit = Math.Min(cores, MaxWorkers);
        if (bytesPerItem > 0)
            limit = Math.Min(limit, (int)Math.Max(1, InFlightBudgetBytes / bytesPerItem));
        return Math.Max(1, Math.Min(limit, items));
    }

    /// <summary>
    /// Обработать список, сохранив ПОРЯДОК результатов: страницы и картинки в
    /// документе обязаны остаться на своих местах, как бы ни легли потоки.
    /// </summary>
    public static TResult?[] Map<TSource, TResult>(
        IReadOnlyList<TSource> source, Func<TSource, TResult?> work, int workers)
    {
        var results = new TResult?[source.Count];
        if (source.Count == 0) return results;

        if (workers <= 1 || source.Count == 1)
        {
            for (var i = 0; i < source.Count; i++) results[i] = work(source[i]);
            return results;
        }

        Parallel.For(0, source.Count,
            new ParallelOptions { MaxDegreeOfParallelism = workers },
            i => results[i] = work(source[i]));
        return results;
    }
}
