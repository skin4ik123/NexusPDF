using System.Diagnostics;
using System.Text;
using NexusPdf.Pdf.Abstractions;

namespace NexusPdf.Pdf.Qpdf;

/// <summary>
/// Адаптер официального qpdf.exe (Apache-2.0): структурная проверка,
/// линеаризация, оптимизация без потерь, шифрование AES-256 и снятие защиты.
/// Бинарник ищется в tools\qpdf рядом с приложением и вверх по каталогам
/// (режим разработки). Версия и SHA-256 пинуются в tools/qpdf.lock.json.
/// Пока qpdf.exe не найден — движок честно недоступен, зависящие функции
/// в интерфейсе не показываются.
/// </summary>
public sealed class QpdfEngine : IPdfStructureEngine, IPdfSecurityEngine, IPdfValidationEngine
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(5);

    private readonly string? _qpdfPath;

    public QpdfEngine() : this(AppContext.BaseDirectory) { }

    public QpdfEngine(string baseDirectory) => _qpdfPath = ResolveQpdfPath(baseDirectory);

    public bool IsAvailable => _qpdfPath != null;

    public string? UnavailableReason => IsAvailable
        ? null
        : "qpdf.exe не найден. Запустите tools/fetch-qpdf.ps1 (версия и SHA-256 — в tools/qpdf.lock.json) или переустановите приложение.";

    /// <summary>Поиск tools\qpdf\qpdf.exe рядом с приложением и до шести уровней вверх (для запуска из bin/Debug).</summary>
    private static string? ResolveQpdfPath(string baseDirectory)
    {
        var dir = new DirectoryInfo(baseDirectory);
        for (var depth = 0; depth < 7 && dir != null; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "qpdf", "qpdf.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    public Task<PdfValidationResult> CheckAsync(string filePath, string? password, CancellationToken ct) =>
        ValidateAsync(filePath, password, ct);

    public async Task<PdfValidationResult> ValidateAsync(string filePath, string? password, CancellationToken ct)
    {
        var args = new List<string> { "--check", "--warning-exit-0" };
        if (!string.IsNullOrEmpty(password))
            args.Add("--password=" + password);
        args.Add(filePath);

        var (exitCode, output) = await RunAsync(args, ct).ConfigureAwait(false);
        var problems = output
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("WARNING", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("error", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exitCode != 0 && problems.Count == 0)
            problems.Add($"qpdf --check завершился с кодом {exitCode}.");

        return new PdfValidationResult(exitCode == 0, 0, problems);
    }

    public Task LinearizeAsync(string sourcePath, string targetPath, CancellationToken ct) =>
        RunExpectSuccessAsync(new[] { "--linearize", sourcePath, targetPath }, "Линеаризация", ct);

    public Task OptimizeAsync(string sourcePath, string targetPath, bool linearize, CancellationToken ct)
    {
        var args = new List<string>
        {
            "--object-streams=generate",
            "--compress-streams=y",
            "--recompress-flate",
            "--compression-level=9",
        };
        if (linearize)
            args.Add("--linearize");
        args.Add(sourcePath);
        args.Add(targetPath);
        return RunExpectSuccessAsync(args, "Оптимизация", ct);
    }

    public Task EncryptAsync(string sourcePath, string targetPath, string userPassword, string? ownerPassword, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(userPassword))
            throw new ArgumentException("Пароль открытия не может быть пустым.", nameof(userPassword));
        var owner = string.IsNullOrEmpty(ownerPassword) ? userPassword : ownerPassword;
        return RunExpectSuccessAsync(
            new[] { "--encrypt", userPassword, owner, "256", "--", sourcePath, targetPath },
            "Шифрование", ct);
    }

    public Task DecryptAsync(string sourcePath, string targetPath, string password, CancellationToken ct) =>
        RunExpectSuccessAsync(
            new[] { "--password=" + password, "--decrypt", sourcePath, targetPath },
            "Снятие защиты", ct);

    private async Task RunExpectSuccessAsync(IReadOnlyList<string> args, string operationName, CancellationToken ct)
    {
        // --warning-exit-0: qpdf возвращает 3 при УСПЕХЕ с предупреждениями
        // (обычное дело для реальных слегка некорректных PDF) — без флага
        // готовый корректный результат отбрасывался бы как ошибка.
        var fullArgs = new List<string>(args.Count + 1) { "--warning-exit-0" };
        fullArgs.AddRange(args);

        var (exitCode, output) = await RunAsync(fullArgs, ct).ConfigureAwait(false);
        if (exitCode != 0)
            throw new PdfEngineException($"{operationName}: qpdf завершился с кодом {exitCode}. {Truncate(output)}");
    }

    private async Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!IsAvailable)
            throw new PdfFeatureUnavailableException("qpdf", UnavailableReason!);

        // Аргументы (среди них бывают пароли) передаются через response-файл:
        // командная строка процесса видна любому приложению пользователя,
        // а временный файл защищён ACL профиля и удаляется сразу после запуска.
        var responseFile = Path.Combine(Path.GetTempPath(), "nexusqpdf-" + Guid.NewGuid().ToString("N") + ".args");
        await File.WriteAllLinesAsync(responseFile, args, new UTF8Encoding(false), ct).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = _qpdfPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("@" + responseFile);

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                throw new PdfEngineException("Не удалось запустить qpdf.exe.");
        }
        catch
        {
            try { File.Delete(responseFile); } catch { /* лучшая попытка */ }
            throw;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(OperationTimeout);

        var stdOut = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stdErr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже завершился */ }
            throw;
        }
        finally
        {
            try { File.Delete(responseFile); } catch { /* лучшая попытка */ }
        }

        var output = (await stdOut.ConfigureAwait(false)) + "\n" + (await stdErr.ConfigureAwait(false));
        return (process.ExitCode, output);
    }

    private static string Truncate(string text)
    {
        text = text.Trim();
        return text.Length <= 400 ? text : text[..400] + "…";
    }
}
