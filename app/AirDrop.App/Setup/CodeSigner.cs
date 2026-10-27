using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace AirDrop.App.Setup;

/// <summary>
/// Per-install driver signing without the WDK.
/// <para>
/// End-user machines have neither Inf2Cat nor MakeCat, so the catalog is generated at our build time
/// (driver\package.ps1, unsigned) and shipped. A catalog only lists file hashes, so it doesn't depend on the
/// signer. On the user's machine we create a fresh self-signed code-signing certificate whose private key only
/// ever exists in memory, sign the .sys (embedded) and the .cat with <c>SignerSignEx</c> (mssign32.dll — the API
/// signtool and Set-AuthenticodeSignature use; it has SIPs for PE and catalog files), then drop the key.
/// Only the public certificate is installed (LocalMachine\Root + TrustedPublisher).
/// </para>
/// <para>
/// Embedding a signature in the .sys after the catalog was built is fine: catalogs store the Authenticode PE hash,
/// which skips the checksum and the certificate table (checked by <see cref="VerifyPackageAgainstCatalog"/>).
/// </para>
/// WPF-free (linked into AirDrop.Tests, which signs temp copies with an in-memory key — no store is touched).
/// </summary>
public static class CodeSigner
{
    public const string SubjectCommonName = "NitroDrop Driver Signing";
    private const string CodeSigningEku = "1.3.6.1.5.5.7.3.3";

    /// <summary>RSA-3072 / SHA-256, code signing EKU, private key ephemeral (never persisted to a key container).</summary>
    public static X509Certificate2 CreatePerInstallCertificate(string machineName)
    {
        using var rsa = RSA.Create(3072);
        var safeMachine = new string(machineName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        var dn = new X500DistinguishedNameBuilder();
        dn.AddOrganizationName("Nitrobyte NitroDrop (per-install; private key destroyed after signing)");
        dn.AddCommonName($"{SubjectCommonName} ({safeMachine})");
        var req = new CertificateRequest(dn.Build(), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(CodeSigningEku) }, false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        // Long validity: kernel code integrity ignores expiry, and the key no longer exists to re-sign anyway.
        return req.CreateSelfSigned(now.AddDays(-1), now.AddYears(25));
    }

    public static bool IsOurCertificate(X509Certificate2 c) =>
        c.GetNameInfo(X509NameType.SimpleName, false).StartsWith(SubjectCommonName, StringComparison.Ordinal);

    /// <summary>Authenticode-signs a PE (.sys) or catalog (.cat) in place with SHA-256. No timestamp (offline, test mode).</summary>
    public static void SignFile(string path, X509Certificate2 certWithKey)
    {
        if (!certWithKey.HasPrivateKey) throw new ArgumentException("sertifikanın özel anahtarı yok", nameof(certWithKey));
        var full = Path.GetFullPath(path);

        IntPtr fileName = Marshal.StringToHGlobalUni(full);
        IntPtr index = Marshal.AllocHGlobal(sizeof(uint));
        IntPtr fileInfo = IntPtr.Zero, storeInfo = IntPtr.Zero;
        try
        {
            Marshal.WriteInt32(index, 0);
            var fi = new SIGNER_FILE_INFO { cbSize = (uint)Marshal.SizeOf<SIGNER_FILE_INFO>(), pwszFileName = fileName, hFile = IntPtr.Zero };
            fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<SIGNER_FILE_INFO>());
            Marshal.StructureToPtr(fi, fileInfo, false);

            var subject = new SIGNER_SUBJECT_INFO
            {
                cbSize = (uint)Marshal.SizeOf<SIGNER_SUBJECT_INFO>(),
                pdwIndex = index,
                dwSubjectChoice = SIGNER_SUBJECT_FILE,
                pSignerFileInfo = fileInfo,
            };

            var si = new SIGNER_CERT_STORE_INFO
            {
                cbSize = (uint)Marshal.SizeOf<SIGNER_CERT_STORE_INFO>(),
                pSigningCert = certWithKey.Handle,
                dwCertPolicy = SIGNER_CERT_POLICY_CHAIN,
                hCertStore = IntPtr.Zero,
            };
            storeInfo = Marshal.AllocHGlobal(Marshal.SizeOf<SIGNER_CERT_STORE_INFO>());
            Marshal.StructureToPtr(si, storeInfo, false);

            var signerCert = new SIGNER_CERT
            {
                cbSize = (uint)Marshal.SizeOf<SIGNER_CERT>(),
                dwCertChoice = SIGNER_CERT_STORE,
                pCertStoreInfo = storeInfo,
                hwnd = IntPtr.Zero,
            };

            var sig = new SIGNER_SIGNATURE_INFO
            {
                cbSize = (uint)Marshal.SizeOf<SIGNER_SIGNATURE_INFO>(),
                algidHash = CALG_SHA_256,
                dwAttrChoice = SIGNER_NO_ATTR,
            };

            int hr = SignerSignEx(0, ref subject, ref signerCert, ref sig, IntPtr.Zero, null, IntPtr.Zero, IntPtr.Zero, out var ctx);
            if (ctx != IntPtr.Zero) SignerFreeSignerContext(ctx);
            if (hr != 0) throw new CryptographicException($"SignerSignEx başarısız ({Path.GetFileName(full)}): 0x{hr:X8}", Marshal.GetExceptionForHR(hr));
            GC.KeepAlive(certWithKey);
        }
        finally
        {
            if (fileInfo != IntPtr.Zero) Marshal.FreeHGlobal(fileInfo);
            if (storeInfo != IntPtr.Zero) Marshal.FreeHGlobal(storeInfo);
            Marshal.FreeHGlobal(index);
            Marshal.FreeHGlobal(fileName);
        }
    }

    // ---------------- inspection (used after signing and by tests) ----------------

    /// <summary>Signer certificate thumbprint of a PE's embedded Authenticode signature, or null if unsigned.</summary>
    public static string? GetPeSigner(string path)
    {
        var pkcs7 = ReadPeCertificateTable(File.ReadAllBytes(path));
        return pkcs7 is null ? null : SignerThumbprint(pkcs7);
    }

    /// <summary>Signer thumbprint of a catalog (a .cat is a PKCS#7 SignedData), or null if unsigned.</summary>
    public static string? GetCatalogSigner(string path) => SignerThumbprint(File.ReadAllBytes(path));

    /// <summary>
    /// PKCS#7 SignedData → certificate matching the first SignerInfo's issuerAndSerialNumber.
    /// (Structure only; trust and the signature itself are checked by Windows at install time.)
    /// </summary>
    private static string? SignerThumbprint(byte[] pkcs7)
    {
        try
        {
            var outer = new AsnReader(pkcs7, AsnEncodingRules.BER).ReadSequence();
            if (outer.ReadObjectIdentifier() != "1.2.840.113549.1.7.2") return null;
            var sd = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
            sd.ReadInteger();           // version
            sd.ReadSetOf();             // digestAlgorithms
            sd.ReadEncodedValue();      // encapContentInfo (SpcIndirectData / CTL)
            var certs = new List<X509Certificate2>();
            var tag0 = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
            var tag1 = new Asn1Tag(TagClass.ContextSpecific, 1, isConstructed: true);
            if (sd.HasData && sd.PeekTag().HasSameClassAndValue(tag0))
            {
                var set = sd.ReadSetOf(tag0);
                while (set.HasData)
                {
                    var enc = set.ReadEncodedValue();
                    if (enc.Span[0] == 0x30) certs.Add(X509CertificateLoader.LoadCertificate(enc.Span));
                }
            }
            if (sd.HasData && sd.PeekTag().HasSameClassAndValue(tag1)) sd.ReadEncodedValue();
            var infos = sd.ReadSetOf();
            if (!infos.HasData) return null;
            var si = infos.ReadSequence();
            si.ReadInteger();
            var ias = si.ReadSequence();   // issuerAndSerialNumber
            ias.ReadEncodedValue();        // issuer
            var serial = TrimZeros(ias.ReadIntegerBytes().Span);
            var match = certs.FirstOrDefault(c => TrimZeros(c.SerialNumberBytes.Span).SequenceEqual(serial));
            return match?.Thumbprint;
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or InvalidOperationException) { return null; }
    }

    private static byte[] TrimZeros(ReadOnlySpan<byte> b)
    {
        int i = 0;
        while (i < b.Length - 1 && b[i] == 0) i++;
        return b[i..].ToArray();
    }

    /// <summary>WIN_CERTIFICATE.bCertificate of the PE security directory (PKCS#7), or null.</summary>
    public static byte[]? ReadPeCertificateTable(byte[] pe)
    {
        if (pe.Length < 0x40 || pe[0] != 'M' || pe[1] != 'Z') return null;
        int peOff = BitConverter.ToInt32(pe, 0x3C);
        if (peOff < 0 || peOff + 24 > pe.Length || BitConverter.ToUInt32(pe, peOff) != 0x00004550) return null;
        int opt = peOff + 24;
        ushort magic = BitConverter.ToUInt16(pe, opt);
        int dirs = magic switch { 0x20B => opt + 112, 0x10B => opt + 96, _ => -1 };
        if (dirs < 0) return null;
        int sec = dirs + 4 * 8;   // IMAGE_DIRECTORY_ENTRY_SECURITY; file offset, not RVA
        if (sec + 8 > pe.Length) return null;
        int off = BitConverter.ToInt32(pe, sec), size = BitConverter.ToInt32(pe, sec + 4);
        if (off <= 0 || size < 8 || (long)off + size > pe.Length) return null;
        int len = BitConverter.ToInt32(pe, off);
        if (len < 8 || len > size) return null;
        return pe.AsSpan(off + 8, len - 8).ToArray();
    }

    /// <summary>Catalog member tag (hex hash, as Inf2Cat/MakeCat write it) for a file — Authenticode hash for PE files.</summary>
    public static string ComputeMemberTag(string path, string hashAlgorithm)
    {
        if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, hashAlgorithm, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CryptCATAdminAcquireContext2");
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            SafeFileHandle h = fs.SafeFileHandle;
            uint cb = 0;
            CryptCATAdminCalcHashFromFileHandle2(admin, h, ref cb, null, 0);
            if (cb == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "CryptCATAdminCalcHashFromFileHandle2");
            var hash = new byte[cb];
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, h, ref cb, hash, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CryptCATAdminCalcHashFromFileHandle2");
            return Convert.ToHexString(hash, 0, (int)cb);
        }
        finally { CryptCATAdminReleaseContext(admin, 0); }
    }

    /// <summary>Member reference tags of a catalog (upper-case hex hashes).</summary>
    public static IReadOnlyList<string> ReadCatalogMemberTags(string catPath)
    {
        var h = CryptCATOpen(Path.GetFullPath(catPath), CRYPTCAT_OPEN_EXISTING, IntPtr.Zero, 0, 0);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "CryptCATOpen");
        var tags = new List<string>();
        try
        {
            IntPtr m = IntPtr.Zero;
            while ((m = CryptCATEnumerateMember(h, m)) != IntPtr.Zero)
            {
                // CRYPTCATMEMBER { DWORD cbStruct; LPWSTR pwszReferenceTag; ... }
                var tag = Marshal.PtrToStringUni(Marshal.ReadIntPtr(m, IntPtr.Size));
                if (!string.IsNullOrEmpty(tag)) tags.Add(tag.ToUpperInvariant());
            }
        }
        finally { CryptCATClose(h); }
        return tags;
    }

    /// <summary>
    /// Every catalog member file of the package must be listed in the catalog (SHA-256 or SHA-1 tags).
    /// Returns the names that are not — empty means pnputil's own catalog check will pass on hashes.
    /// </summary>
    public static IReadOnlyList<string> VerifyPackageAgainstCatalog(string packageDir)
    {
        var tags = new HashSet<string>(ReadCatalogMemberTags(Path.Combine(packageDir, DriverPackage.CatName)), StringComparer.OrdinalIgnoreCase);
        var bad = new List<string>();
        foreach (var name in DriverPackage.CatalogMembers)
        {
            var path = Path.Combine(packageDir, name);
            if (!File.Exists(path)) { bad.Add(name); continue; }
            if (!tags.Contains(ComputeMemberTag(path, "SHA256")) && !tags.Contains(ComputeMemberTag(path, "SHA1")))
                bad.Add(name);
        }
        return bad;
    }

    // ---------------- interop ----------------

    private const uint SIGNER_SUBJECT_FILE = 1;
    private const uint SIGNER_CERT_STORE = 2;
    private const uint SIGNER_CERT_POLICY_CHAIN = 2;
    private const uint SIGNER_NO_ATTR = 0;
    private const uint CALG_SHA_256 = 0x0000800c;
    private const uint CRYPTCAT_OPEN_EXISTING = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIGNER_FILE_INFO { public uint cbSize; public IntPtr pwszFileName; public IntPtr hFile; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIGNER_SUBJECT_INFO { public uint cbSize; public IntPtr pdwIndex; public uint dwSubjectChoice; public IntPtr pSignerFileInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIGNER_CERT_STORE_INFO { public uint cbSize; public IntPtr pSigningCert; public uint dwCertPolicy; public IntPtr hCertStore; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIGNER_CERT { public uint cbSize; public uint dwCertChoice; public IntPtr pCertStoreInfo; public IntPtr hwnd; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIGNER_SIGNATURE_INFO
    {
        public uint cbSize; public uint algidHash; public uint dwAttrChoice; public IntPtr pAttrAuthcode;
        public IntPtr psAuthenticated; public IntPtr psUnauthenticated;
    }

    [DllImport("mssign32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SignerSignEx(uint dwFlags, ref SIGNER_SUBJECT_INFO pSubjectInfo, ref SIGNER_CERT pSignerCert,
        ref SIGNER_SIGNATURE_INFO pSignatureInfo, IntPtr pProviderInfo, string? pwszHttpTimeStamp, IntPtr psRequest,
        IntPtr pSipData, out IntPtr ppSignerContext);

    [DllImport("mssign32.dll", ExactSpelling = true)]
    private static extern int SignerFreeSignerContext(IntPtr pSignerContext);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr phCatAdmin, IntPtr pgSubsystem, string pwszHashAlgorithm,
        IntPtr pStrongHashPolicy, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr hCatAdmin, SafeFileHandle hFile, ref uint pcbHash,
        byte[]? pbHash, uint dwFlags);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr CryptCATOpen(string pwszFileName, uint fdwOpenFlags, IntPtr hProv, uint dwPublicVersion, uint dwEncodingType);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr CryptCATEnumerateMember(IntPtr hCatalog, IntPtr pPrevMember);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATClose(IntPtr hCatalog);
}
