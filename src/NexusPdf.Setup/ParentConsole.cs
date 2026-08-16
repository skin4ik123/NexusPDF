using System.IO;
using System.Runtime.InteropServices;

namespace NexusPdf.Setup;

/// <summary>
/// Установщик собран как оконная программа, поэтому своей консоли у него нет, и
/// всё написанное в Console.Error пропадало. В тихом режиме (/S) это значило
/// вот что: администратор запускал установку из скрипта, получал голый код
/// возврата 87, 740, 1638 или 1603 — и ни одной строки о том, что не так.
///
/// Здесь процесс подключается к консоли того, кто его запустил, и поток ошибок
/// открывается заново уже на неё. Если запуска из консоли не было (двойной щелчок,
/// служба развёртывания), AttachConsole вернёт false и всё останется как прежде.
/// </summary>
internal static class ParentConsole
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    public static void AttachToParent()
    {
        try
        {
            if (!AttachConsole(AttachParentProcess))
                return;

            // Поток ошибок открывается заново намеренно: к этому моменту .NET мог
            // уже связать Console.Error с пустым устройством, и запись снова ушла
            // бы в никуда.
            var writer = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetError(writer);
        }
        catch (IOException)
        {
            // Консоли нет или она недоступна — сообщения останутся невидимыми,
            // но код возврата установщик отдаст в любом случае.
        }
    }
}
