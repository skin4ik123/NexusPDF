using System.Security.Cryptography;

namespace NexusPdf.Infrastructure;

public sealed record SignatureTemplate(string Name, string FilePath);

/// <summary>
/// Локальная библиотека шаблонов визуальных подписей. Файлы шифруются DPAPI
/// (ключ пользователя Windows): скопированный на другую машину или к другому
/// пользователю файл прочитать нельзя. Содержимое подписей не журналируется.
/// </summary>
public sealed class SignatureStore
{
    private const string Extension = ".sig";

    public string Directory { get; } = Path.Combine(AppPaths.Root, "Signatures");

    public IReadOnlyList<SignatureTemplate> List()
    {
        if (!System.IO.Directory.Exists(Directory))
            return Array.Empty<SignatureTemplate>();
        return System.IO.Directory.GetFiles(Directory, "*" + Extension)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => new SignatureTemplate(Path.GetFileNameWithoutExtension(p), p))
            .ToList();
    }

    /// <summary>Сохраняет PNG-байты подписи под именем. Возвращает шаблон.</summary>
    public SignatureTemplate Save(string name, byte[] pngBytes)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (safeName.Length == 0)
            safeName = "подпись";
        var path = Path.Combine(Directory, safeName + Extension);
        var counter = 1;
        while (File.Exists(path))
            path = Path.Combine(Directory, $"{safeName}-{++counter}{Extension}");

        var protectedBytes = ProtectedData.Protect(pngBytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, protectedBytes);
        return new SignatureTemplate(Path.GetFileNameWithoutExtension(path), path);
    }

    /// <summary>Читает и расшифровывает PNG-байты шаблона.</summary>
    public byte[] Load(SignatureTemplate template) =>
        ProtectedData.Unprotect(File.ReadAllBytes(template.FilePath), null, DataProtectionScope.CurrentUser);

    public void Delete(SignatureTemplate template)
    {
        try
        {
            File.Delete(template.FilePath);
        }
        catch (IOException)
        {
            // Файл занят — удаление повторится при следующей попытке пользователя.
        }
    }
}
