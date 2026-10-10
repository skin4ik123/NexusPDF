namespace NexusPdf.Printing;

/// <summary>
/// Тексты слоя печати на языке интерфейса.
///
/// Слой печати не знает о словарях программы, поэтому раньше писал сообщения
/// по-русски прямо в коде — и в английском и украинском интерфейсе проверка
/// перед печатью, ошибки диапазона и указания ручного дуплекса оставались
/// русскими. Теперь он отдаёт только ключи и значения, а перевод подставляет
/// интерфейс при запуске (<see cref="Resolver"/>).
///
/// Без интерфейса (в тестах) ключ возвращается как есть вместе со значениями:
/// так тест проверяет, ЧТО сообщено, не завися от формулировки.
/// </summary>
public static class PrintText
{
    public static Func<string, object[], string> Resolver { get; set; } = Fallback;

    public static string Get(string key, params object[] args) => Resolver(key, args);

    private static string Fallback(string key, object[] args) =>
        args.Length == 0 ? key : key + ": " + string.Join(", ", args);
}
