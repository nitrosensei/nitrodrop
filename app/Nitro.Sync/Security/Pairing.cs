using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Nitro.Sync.Security;

/// <summary>Shown to the user on both PCs while pairing.</summary>
public sealed record PairingPrompt(string PeerDeviceId, string PeerName, string Code, bool IsInitiator);

/// <summary>
/// Pairing crypto (numeric comparison, like Bluetooth SSP):
///   transcript = ids, names-free: idA | idB | certA | certB | ecdhA | ecdhB | nonceA | nonceB   (A = initiator)
///   code       = SHA-256("nitrosync-sas" | transcript) → first 4 bytes mod 10^6, 6 digits
///   key        = HKDF-SHA256(ECDH(P-256), salt = nonceA|nonceB, info = "nitrosync-v1" | SHA-256(transcript))
/// A man in the middle must substitute a certificate fingerprint or ECDH key, which changes the code the
/// users compare. Afterwards each side pins the other's certificate (TOFU) and signs requests with the key.
/// </summary>
public static class PairingCrypto
{
    public static string CertFingerprint(X509Certificate cert) =>
        Convert.ToHexStringLower(SHA256.HashData(cert.GetRawCertData()));

    public static byte[] Transcript(string idA, string idB, string certA, string certB,
        byte[] ecdhA, byte[] ecdhB, byte[] nonceA, byte[] nonceB)
    {
        using var ms = new MemoryStream();
        void Put(ReadOnlySpan<byte> b)
        {
            Span<byte> len = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, b.Length);
            ms.Write(len);
            ms.Write(b);
        }
        Put("nitrosync-pair-v1"u8);
        Put(Encoding.UTF8.GetBytes(idA.ToLowerInvariant()));
        Put(Encoding.UTF8.GetBytes(idB.ToLowerInvariant()));
        Put(Encoding.ASCII.GetBytes(certA.ToLowerInvariant()));
        Put(Encoding.ASCII.GetBytes(certB.ToLowerInvariant()));
        Put(ecdhA); Put(ecdhB); Put(nonceA); Put(nonceB);
        return ms.ToArray();
    }

    public static string Code(byte[] transcript)
    {
        var h = SHA256.HashData((byte[])[.. "nitrosync-sas"u8, .. transcript]);
        uint v = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(h);
        return (v % 1_000_000).ToString("D6");
    }

    public static byte[] DeriveKey(ECDiffieHellman mine, byte[] peerPublicSpki, byte[] nonceA, byte[] nonceB, byte[] transcript)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerPublicSpki, out _);
        if (peer.KeySize != 256) throw new CryptographicException("Unexpected curve");
        var shared = mine.DeriveRawSecretAgreement(peer.PublicKey);
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, [.. nonceA, .. nonceB],
                [.. "nitrosync-v1"u8, .. SHA256.HashData(transcript)]);
        }
        finally { CryptographicOperations.ZeroMemory(shared); }
    }

    public static ECDiffieHellman NewEcdh() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>Key confirmation MAC: proves each side derived the same key (and saw the same transcript).</summary>
    public static byte[] Confirm(byte[] key, string role, byte[] transcript) =>
        HMACSHA256.HashData(key, (byte[])[.. Encoding.ASCII.GetBytes(role), .. transcript]);
}
