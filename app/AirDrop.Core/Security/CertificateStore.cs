using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AirDrop.Core.Security;

/// <summary>Self-signed TLS identity, created on first run (OpenDrop: RSA-2048, CN=computer name).</summary>
public static class CertificateStore
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "airdrop-win");

    public static X509Certificate2 LoadOrCreate(string? directory = null, string? commonName = null)
    {
        directory ??= DefaultDirectory;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "tls-identity.pfx");
        if (File.Exists(path))
        {
            try
            {
                var existing = Load(File.ReadAllBytes(path));
                if (existing.NotAfter > DateTime.Now.AddDays(7)) return existing;
                existing.Dispose();
            }
            catch (CryptographicException) { /* corrupt: regenerate */ }
        }
        var pfx = CreatePfx(commonName ?? Environment.MachineName);
        File.WriteAllBytes(path, pfx);
        return Load(pfx);
    }

    /// <summary>Creates an in-memory certificate (for tests).</summary>
    public static X509Certificate2 CreateEphemeral(string commonName = "airdrop-test") => Load(CreatePfx(commonName));

    private static byte[] CreatePfx(string commonName)
    {
        using var rsa = RSA.Create(2048);
        var cn = new X500DistinguishedNameBuilder();
        cn.AddCommonName(commonName);
        var req = new CertificateRequest(cn.Build(), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false)); // server + client auth
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        return cert.Export(X509ContentType.Pfx);
    }

    // SChannel cannot use ephemeral keys, so load from PFX with a (temporary) user key container.
    private static X509Certificate2 Load(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
}
