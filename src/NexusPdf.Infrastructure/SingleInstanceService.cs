using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace NexusPdf.Infrastructure;

/// <summary>
/// Одиночный экземпляр приложения: первый процесс держит мьютекс и слушает
/// именованный канал, последующие передают ему список файлов и завершаются.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private static readonly string BaseName =
        "NexusPdf_" + Convert.ToHexString(Encoding.UTF8.GetBytes(Environment.UserName))[..Math.Min(24, Environment.UserName.Length * 2)];

    private readonly Mutex _mutex;
    private CancellationTokenSource? _cts;

    public bool IsPrimary { get; }

    public SingleInstanceService()
    {
        _mutex = new Mutex(initiallyOwned: true, "Local\\" + BaseName + ".mutex", out var createdNew);
        IsPrimary = createdNew;
    }

    /// <summary>Запускает приём файлов от вторичных экземпляров (только в первичном).</summary>
    public void StartServer(Action<IReadOnlyList<string>> onFilesReceived)
    {
        if (!IsPrimary) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            var failureDelayMs = 100;
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        BaseName + ".pipe", PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    // Чтение — в фоне, чтобы сразу поднять следующий сервер:
                    // залповое открытие нескольких файлов из Проводника не должно
                    // получать отказ подключения и плодить лишние экземпляры.
                    var connected = server;
                    server = null;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var owned = connected;
                            using var reader = new StreamReader(owned, Encoding.UTF8);
                            var payload = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                            var files = JsonSerializer.Deserialize<List<string>>(payload);
                            // Пустой список — это запуск без файлов: окно всё равно
                            // нужно активировать, поэтому колбэк вызывается всегда.
                            if (files != null)
                                onFilesReceived(files);
                        }
                        catch
                        {
                            // Ошибка одного клиента не должна останавливать приём.
                        }
                    }, ct);

                    failureDelayMs = 100; // сервер поднялся — сбрасываем backoff
                }
                catch (OperationCanceledException)
                {
                    server?.Dispose();
                    break;
                }
                catch
                {
                    server?.Dispose();
                    // Персистентная ошибка создания канала (имя занято чужим
                    // процессом, ACL) не должна выедать ядро горячим циклом.
                    try
                    {
                        await Task.Delay(failureDelayMs, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    failureDelayMs = Math.Min(failureDelayMs * 2, 5000);
                }
            }
        }, ct);
    }

    /// <summary>Передаёт файлы первичному экземпляру. Возвращает false, если он не отвечает.</summary>
    public static bool TrySendToPrimary(IReadOnlyList<string> files)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", BaseName + ".pipe", PipeDirection.Out);
            client.Connect(4000);
            AllowPrimaryToComeForward(client);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.Write(JsonSerializer.Serialize(files));
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Выйти на передний план Windows разрешает только процессу, который
    /// запустил пользователь (этому — его запустил Проводник), а свёрнутое
    /// первичное окно работает в фоне: без разрешения оно не выходило вперёд, а
    /// лишь мигало на панели задач. Разрешение передаётся первичному процессу
    /// до отправки файлов.
    /// </summary>
    private static void AllowPrimaryToComeForward(NamedPipeClientStream client)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            if (GetNamedPipeServerProcessId(client.SafePipeHandle, out var processId))
                AllowSetForegroundWindow(processId);
        }
        catch (Exception)
        {
            // Без разрешения окно лишь мигнёт на панели задач — файл всё равно откроется.
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    public void Dispose()
    {
        _cts?.Cancel();
        if (IsPrimary)
            _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
