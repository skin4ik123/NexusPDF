using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NexusPdf.App.Desktop;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Двусторонняя инверсия bool: одно свойство управляет парой вкладок.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

public sealed class NonZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i && i != 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class MinusOneConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i ? i - 1 : -1;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i ? i + 1 : 1;
}

/// <summary>
/// Перечисление ↔ индекс в ComboBox. Пункты списка идут в том же порядке, что
/// и значения enum, поэтому список и модель не расходятся при добавлении
/// нового режима — он просто встаёт на своё место.
/// </summary>
public sealed class EnumToIndexConverter : IValueConverter
{
    private Type? _enumType;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null) return -1;
        _enumType = value.GetType();
        var values = Enum.GetValues(_enumType);
        for (var i = 0; i < values.Length; i++)
            if (Equals(values.GetValue(i), value)) return i;
        return -1;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var type = targetType.IsEnum ? targetType : _enumType;
        if (type == null || value is not int index || index < 0) return Binding.DoNothing;
        var values = Enum.GetValues(type);
        return index < values.Length ? values.GetValue(index) : Binding.DoNothing;
    }
}

/// <summary>Радиокнопка для одного значения перечисления: параметр — имя значения.</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value != null && parameter is string name &&
        string.Equals(value.ToString(), name, StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Снятие отметки приходит от соседней кнопки группы — его игнорируем,
        // иначе значение сбрасывалось бы дважды за один щелчок.
        if (value is not true || parameter is not string name) return Binding.DoNothing;
        return targetType.IsEnum && Enum.TryParse(targetType, name, out var parsed)
            ? parsed
            : Binding.DoNothing;
    }
}

/// <summary>Подсказка «нечего показывать» видна ровно тогда, когда картинки нет.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value == null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
