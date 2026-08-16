using System.Diagnostics;

namespace NexusPdf.App.Desktop.Services;

/// <summary>
/// Внешние адреса проекта.
///
/// Программа НЕ хранит у себя реквизиты пожертвований и ведёт на раздел сайта:
/// способы оплаты меняются (появится PayPal, сменится кошелёк), а установленная
/// копия обновляется редко. Зашитый в неё кошелёк однажды станет чужим.
/// </summary>
public static class ProjectLinks
{
    public const string Site = "https://nexus.internetdeco.com/";
    public const string Support = "https://nexus.internetdeco.com/#support";
    public const string Changelog = "https://nexus.internetdeco.com/changelog";

    /// <summary>
    /// Исходный код. AGPL-3.0 обязывает дать получателю программы тот код, из
    /// которого она собрана, поэтому ссылка обязана быть в самой программе,
    /// а не только на сайте.
    /// </summary>
    public const string Source = "https://github.com/skin4ik123/NexusPDF";

    /// <summary>
    /// Открывает адрес в браузере по умолчанию.
    ///
    /// Подтверждение здесь не спрашивается, в отличие от ссылок ИЗ документа:
    /// эти адреса записаны в самой программе, а действие начал сам человек,
    /// нажав пункт меню.
    /// </summary>
    public static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось открыть адрес {Url}", url);
        }
    }
}
