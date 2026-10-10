using System.Windows;
using System.Windows.Media;

namespace NexusPdf.App.Desktop.Services.Ux;

/// <summary>
/// Резкость интерфейса на экранах с низкой плотностью.
///
/// WPF по умолчанию кладёт текст в режиме Ideal: геометрия букв точная, но на
/// экранах со 100–125 % масштаба (все ноутбуки 1366×768) штрихи попадают между
/// пикселями, и ВЕСЬ интерфейс выглядит размытым — ровно жалоба «на 1366×768
/// всё расплывается». Режим Display прибивает штрихи к пиксельной сетке, как в
/// обычных программах Windows; на плотных экранах (150 %+) он, наоборот,
/// огрубляет кернинг, поэтому выбор зависит от фактического DPI окна и
/// пересматривается при переезде окна на другой монитор.
///
/// Заодно включается UseLayoutRounding: без него контейнеры встают на дробные
/// координаты, и даже готовый растр страницы растягивается на полпикселя.
/// </summary>
public static class CrispRendering
{
    /// <summary>Подключает обработчик ко ВСЕМ окнам программы. Вызывать один раз при старте.</summary>
    public static void HookAllWindows()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window) Apply(window);
            }));
    }

    public static void Apply(Window window)
    {
        window.UseLayoutRounding = true;
        UpdateTextMode(window);
        window.DpiChanged -= OnDpiChanged;
        window.DpiChanged += OnDpiChanged;
    }

    private static void OnDpiChanged(object? sender, DpiChangedEventArgs e)
    {
        if (sender is Window window) UpdateTextMode(window);
    }

    private static void UpdateTextMode(Window window)
    {
        var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        TextOptions.SetTextFormattingMode(window,
            scale <= 1.25 ? TextFormattingMode.Display : TextFormattingMode.Ideal);
    }
}
