using System.Windows;
using System.Windows.Input;

namespace NexusPdf.App.Desktop.Services.Ux;

/// <summary>
/// Следит, чем пользователь работает прямо сейчас: мышью, пальцем или пером.
///
/// Нужно ровно для одного: на трансформере экран сенсорный ВСЕГДА, но работают
/// на нём обычно мышью. Укрупнять интерфейс из-за наличия сенсора — испортить
/// его большинству; укрупнять после первого касания — попасть в то, чем
/// пользуются в эту минуту.
/// </summary>
public static class TouchInputWatcher
{
    private static string? _densitySetting;
    private static bool _touchUsed;

    /// <summary>Есть ли у машины сенсорный ввод вообще.</summary>
    public static bool HasTouchScreen { get; } = DetectTouchScreen();

    /// <summary>Последний ввод был пальцем.</summary>
    public static bool TouchUsedRecently => _touchUsed;

    /// <summary>Подписывается на события ввода один раз за приложение.</summary>
    public static void Start(string? densitySetting)
    {
        _densitySetting = densitySetting;

        // PreviewTouchDown ловится на уровне класса окна: подписываться на
        // каждый элемент нельзя, а знать о касании нужно всему приложению.
        EventManager.RegisterClassHandler(typeof(Window),
            UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(OnTouch), true);
        EventManager.RegisterClassHandler(typeof(Window),
            UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(OnMouse), true);
    }

    /// <summary>Смена настройки плотности из меню.</summary>
    public static void SetSetting(string? densitySetting)
    {
        _densitySetting = densitySetting;
        DensityManager.Apply(_densitySetting, _touchUsed);
    }

    private static void OnTouch(object? sender, TouchEventArgs e)
    {
        if (_touchUsed) return;
        _touchUsed = true;
        // Только в автоматическом режиме: выбранную вручную плотность
        // касание не отменяет.
        if (!DensityManager.IsExplicit)
            DensityManager.Apply(_densitySetting, touchUsedRecently: true);
    }

    private static void OnMouse(object? sender, MouseButtonEventArgs e)
    {
        // Настоящая мышь, а не «мышиные» события, синтезированные из касания.
        if (!_touchUsed || e.StylusDevice != null) return;
        _touchUsed = false;
        if (!DensityManager.IsExplicit)
            DensityManager.Apply(_densitySetting, touchUsedRecently: false);
    }

    private const int SmDigitizer = 94;
    private const int NidReadyMask = 0x00000080;   // NID_READY
    private const int NidIntegratedTouch = 0x01;
    private const int NidExternalTouch = 0x02;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private static bool DetectTouchScreen()
    {
        try
        {
            var digitizer = GetSystemMetrics(SmDigitizer);
            return (digitizer & NidReadyMask) != 0 &&
                   (digitizer & (NidIntegratedTouch | NidExternalTouch)) != 0;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Не удалось определить наличие сенсорного экрана");
            return false;
        }
    }
}
