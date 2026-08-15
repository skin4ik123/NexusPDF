namespace NexusPdf.Printing;

/// <summary>Одна сигнатура: сколько страниц и с какой начинается.</summary>
public sealed record Signature(int FirstPage, int Count);

/// <summary>
/// Порядок страниц для складывания буклета. Вынесен отдельно от геометрии,
/// потому что это чистая арифметика, которую можно и нужно проверить тестами:
/// ошибка здесь портит всю брошюру, а увидеть её можно только сложив бумагу.
/// </summary>
public static class BookletImposition
{
    /// <summary>
    /// Разбивает документ на сигнатуры. Размер сигнатуры округляется вверх до
    /// кратного четырём: лист буклета всегда несёт четыре страницы.
    /// 0 или отрицательное значение — весь документ одной сигнатурой.
    /// </summary>
    public static IReadOnlyList<Signature> SplitSignatures(int pageCount, int signatureSize)
    {
        if (pageCount <= 0) return Array.Empty<Signature>();

        if (signatureSize <= 0)
            return new[] { new Signature(0, RoundUpToFour(pageCount)) };

        var size = RoundUpToFour(signatureSize);
        var result = new List<Signature>();
        for (var first = 0; first < pageCount; first += size)
            result.Add(new Signature(first, size));
        return result;
    }

    public static int RoundUpToFour(int value) => value <= 0 ? 4 : (value + 3) / 4 * 4;

    /// <summary>
    /// Порядок половинок листов сигнатуры. Возвращает массив сторон: каждая
    /// сторона — два номера страниц (левая и правая половина), нумерация с нуля
    /// внутри сигнатуры. Стороны идут парами: лицевая, обратная, лицевая…
    ///
    /// Классическая раскладка на 8 страниц:
    ///   лист 1 лицо  8 1
    ///   лист 1 оборот 2 7
    ///   лист 2 лицо  6 3
    ///   лист 2 оборот 4 5
    /// Сложенные вдвое и вложенные листы дают 1..8 подряд.
    /// </summary>
    public static IReadOnlyList<int[]> SheetOrder(int signaturePages)
    {
        var total = RoundUpToFour(signaturePages);
        var sheets = total / 4;
        var sides = new List<int[]>(sheets * 2);

        for (var sheet = 0; sheet < sheets; sheet++)
        {
            // Внешняя пара страниц сигнатуры смыкается с внутренней:
            // для листа k это (total-1-2k, 2k) на лице и (2k+1, total-2-2k) на обороте.
            var frontLeft = total - 1 - 2 * sheet;
            var frontRight = 2 * sheet;
            var backLeft = 2 * sheet + 1;
            var backRight = total - 2 - 2 * sheet;

            sides.Add(new[] { frontLeft, frontRight });
            sides.Add(new[] { backLeft, backRight });
        }
        return sides;
    }

    /// <summary>
    /// Порядок печати для ручного дуплекса: сначала все лицевые стороны, затем
    /// все обратные. Второй проход разворачивается, потому что стопка выходит
    /// из принтера в обратном порядке — это и есть источник большинства
    /// испорченных пачек бумаги.
    /// </summary>
    public static (IReadOnlyList<int> FirstPass, IReadOnlyList<int> SecondPass) ManualDuplexOrder(
        int sheetCount, bool reverseSecondPass = true)
    {
        var first = new List<int>();
        var second = new List<int>();
        for (var i = 0; i < sheetCount; i++)
            (i % 2 == 0 ? first : second).Add(i);

        if (reverseSecondPass)
            second.Reverse();
        return (first, second);
    }
}
