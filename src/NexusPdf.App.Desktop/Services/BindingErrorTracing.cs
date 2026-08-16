using System.Diagnostics;
using Serilog;

namespace NexusPdf.App.Desktop.Services;

/// <summary>
/// Ошибки привязок WPF по умолчанию видны только в отладчике и исчезают в
/// поставляемой сборке. Здесь они направляются в журнал приложения: битая
/// привязка — это молча неработающий элемент интерфейса, и обнаруживать её
/// нужно на реальных прогонах, а не в отладчике.
/// </summary>
public static class BindingErrorTracing
{
    private sealed class SerilogTraceListener : TraceListener
    {
        private readonly System.Text.StringBuilder _pending = new();

        public override void Write(string? message)
        {
            if (message != null)
                _pending.Append(message);
        }

        public override void WriteLine(string? message)
        {
            _pending.Append(message);
            var text = _pending.ToString().Trim();
            _pending.Clear();
            if (text.Length == 0)
                return;
            // Уровень определяет сам WPF (Error/Warning), сюда попадает уже
            // отфильтрованный по SourceLevels поток.
            Log.Warning("Ошибка привязки WPF: {Message}", text);
        }
    }

    public static void Attach()
    {
        var source = PresentationTraceSources.DataBindingSource;
        // Warning уже включает в себя Error, поэтому уровень задаётся одним
        // значением — прежнее «Warning | Error» читалось как два условия,
        // хотя вторая половина ничего не добавляла.
        source.Switch.Level = SourceLevels.Warning;
        source.Listeners.Add(new SerilogTraceListener());

        // Без этого вызова WPF не создаёт источники трассировки в релизной сборке.
        PresentationTraceSources.Refresh();
    }
}
