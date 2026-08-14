using System.Security.Cryptography.X509Certificates;

namespace NexusPdf.Signing;

/// <summary>Источники сертификатов подписания: хранилище Windows и файлы PFX/P12.</summary>
public static class CertificateSource
{
    /// <summary>Личные сертификаты пользователя с закрытым ключом, действительные по времени.</summary>
    public static IReadOnlyList<X509Certificate2> FromPersonalStore()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var now = DateTime.Now;
        return store.Certificates
            .Where(c => c.HasPrivateKey && c.NotBefore <= now && c.NotAfter >= now)
            .OrderBy(c => c.GetNameInfo(X509NameType.SimpleName, false), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static X509Certificate2 FromPfx(string path, string password) =>
        X509CertificateLoader.LoadPkcs12FromFile(path, password,
            X509KeyStorageFlags.EphemeralKeySet);
}
