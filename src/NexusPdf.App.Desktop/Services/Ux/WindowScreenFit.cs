using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NexusPdf.Ux;

namespace NexusPdf.App.Desktop.Services.Ux;

/// <summary>
/// Каждое окно программы вписывается в рабочую область СВОЕГО монитора.
///
/// Размеры окон в разметке подбирались на больших экранах. На ноутбуке
/// 1366×768 рабочая область при масштабе 125 % — 1093×582 единицы, и окно
/// печати высотой 820 открывалось с кнопками под панелью задач, а его
/// минимум в 600 не давал уменьшить его мышью. Здесь окно получает размер не
/// больше области, минимумы — не больше области, а положение — внутри экрана.
/// Арифметика — в <see cref="ScreenFit"/>, здесь только чтение монитора.
/// </summary>
public static class WindowScreenFit
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
        // Развёрнутое окно и так в границах экрана, а Left/Top у него
        // относятся к прежнему положению.
        if (window.WindowState != WindowState.Normal) return;
        if (WorkAreaOf(window) is not { } work) return;
        if (double.IsNaN(window.Left) || double.IsNaN(window.Top)) return;
        if (window.ActualWidth <= 0 || window.ActualHeight <= 0) return;

        var current = new ScreenBox(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
        var fit = ScreenFit.Fit(current, work, window.MinWidth, window.MinHeight);

        if (fit.MinWidth < window.MinWidth) window.MinWidth = fit.MinWidth;
        if (fit.MinHeight < window.MinHeight) window.MinHeight = fit.MinHeight;

        var growsByContent = window.SizeToContent != SizeToContent.Manual;
        if (growsByContent)
        {
            // Окно, растущее по содержимому, не ужимается — ему ставится потолок.
            window.MaxWidth = Math.Min(window.MaxWidth, Math.Max(ScreenFit.FloorWidth, work.Width - ScreenFit.Gap));
            window.MaxHeight = Math.Min(window.MaxHeight, Math.Max(ScreenFit.FloorHeight, work.Height - ScreenFit.Gap));
        }
        else
        {
            if (fit.Box.Width < current.Width - 0.5) window.Width = fit.Box.Width;
            if (fit.Box.Height < current.Height - 0.5) window.Height = fit.Box.Height;
        }

        if (Math.Abs(fit.Box.Left - current.Left) > 0.5) window.Left = fit.Box.Left;
        if (Math.Abs(fit.Box.Top - current.Top) > 0.5) window.Top = fit.Box.Top;
    }

    /// <summary>
    /// Рабочая область монитора, на котором стоит окно, в единицах WPF этого
    /// окна. SystemParameters.WorkArea знает только главный монитор, а окно
    /// может быть на втором — с другой плотностью и другой панелью задач.
    /// </summary>
    private static ScreenBox? WorkAreaOf(Window window)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source)
            return null;

        var monitor = MonitorFromWindow(source.Handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info))
        {
            var fallback = SystemParameters.WorkArea;
            return new ScreenBox(fallback.Left, fallback.Top, fallback.Width, fallback.Height);
        }

        var toDiu = source.CompositionTarget.TransformFromDevice;
        var topLeft = toDiu.Transform(new Point(info.Work.Left, info.Work.Top));
        var bottomRight = toDiu.Transform(new Point(info.Work.Right, info.Work.Bottom));
        return new ScreenBox(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
}
