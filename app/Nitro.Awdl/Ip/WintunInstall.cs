// Nitro.Awdl: on-demand Wintun download and verification. Copyright (c) 2026 Nitrobyte. MIT.
//
// wintun.dll (WireGuard LLC) is distributed under its "Prebuilt Binaries License", which is not an OSI license, so it is
// NOT shipped inside NitroDrop's (signed) packages. After the user agrees in the app, the official zip is downloaded once
// from wintun.net, checked against the SHA-256 published there, the DLL for this architecture is checked against a pinned
// SHA-256 and its Authenticode signature must be valid and from "WireGuard LLC". The same checks run again right before
// the elevated radio host loads it (WintunNative's resolver).

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Nitro.Awdl.Ip;

/// <summary>Where wintun.dll lives on this PC and how it gets there (download + verify).</summary>
public static class WintunInstall
{
    public const string Version = "0.14.1";
    /// <summary>Official build (https://www.wintun.net, "Download Wintun 0.14.1").</summary>
    public static readonly Uri ZipUrl = new($"https://www.wintun.net/builds/wintun-{Version}.zip");
    /// <summary>SHA2-256 published on wintun.net for wintun-0.14.1.zip (same pin as tools/fetch-wintun.ps1).</summary>
    public const string ZipSha256 = "07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51";
    /// <summary>SHA-256 of bin/&lt;arch&gt;/wintun.dll inside that zip.</summary>
    public static IReadOnlyDictionary<string, string> DllSha256 { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["amd64"] = "e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce",
        ["arm64"] = "f7ba89005544be9d85231a9e0d5f23b2d15b3311667e2dad0debd344918a3f80",
    };
    /// <summary>Authenticode signer (certificate subject CN / O) the DLL must carry.</summary>
    public const string Signer = "WireGuard LLC";
    public const string DllName = "wintun.dll";
    /// <summary>Upper bound for the download (the 0.14.1 zip is ~0.4 MB).</summary>
    private const long MaxZipBytes = 8 * 1024 * 1024;

    /// <summary>%LOCALAPPDATA%\airdrop-win\wintun (the app's data folder, next to logs and settings).</summary>
    public static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "airdrop-win", "wintun");

    /// <summary>
    /// Folder the resolver looks in first. The radio host sets it from <c>--wintun-dir</c> (the unelevated app's folder,
    /// which differs when a different administrator account approves the UAC prompt).
    /// </summary>
    public static string Directory { get; set; } = DefaultDirectory;

    /// <summary>"amd64" / "arm64" / "x86": the folder names inside the official zip.</summary>
    public static string Arch => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "amd64",
    };

    /// <summary>&lt;root&gt;\&lt;arch&gt;\wintun.dll.</summary>
    public static string DllPath(string? root = null) => Path.Combine(root ?? Directory, Arch, DllName);

    /// <summary>True when a verified copy is in <paramref name="root"/> (default <see cref="Directory"/>).</summary>
    public static bool IsInstalled(string? root = null)
    {
        var path = DllPath(root);
        return File.Exists(path) && Verify(path) is null;
    }

    /// <summary>
    /// null when <paramref name="path"/> is the pinned build for this architecture with a valid "WireGuard LLC"
    /// signature, otherwise why not (English, for logs).
    /// </summary>
    public static string? Verify(string path, bool checkSignature = true)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Verify(fs, path, checkSignature);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return e.Message; }
    }

    /// <summary>
    /// Verifies an open file (the caller keeps <paramref name="file"/> open with FileShare.Read, so it cannot be
    /// replaced between this check and LoadLibrary).
    /// </summary>
    internal static string? Verify(FileStream file, string path, bool checkSignature = true)
    {
        if (!DllSha256.TryGetValue(Arch, out var expected)) return $"no pinned wintun.dll for {Arch}";
        file.Position = 0;
        var hash = Convert.ToHexStringLower(SHA256.HashData(file));
        file.Position = 0;
        if (hash != expected) return $"SHA-256 mismatch: {hash}";
        if (checkSignature && Authenticode.SignerOf(path) is var signer && signer != Signer)
            return signer is null ? "no valid Authenticode signature" : $"signed by '{signer}', expected '{Signer}'";
        return null;
    }

    /// <summary>
    /// Downloads <see cref="ZipUrl"/>, verifies it and puts this architecture's DLL (and the licence) into
    /// <paramref name="root"/>. Throws <see cref="InvalidDataException"/> when a check fails; nothing is left behind then.
    /// </summary>
    public static async Task InstallAsync(HttpClient http, string? root = null, CancellationToken ct = default)
    {
        using var resp = await http.GetAsync(ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > MaxZipBytes) throw new InvalidDataException("wintun zip is unexpectedly large");
        await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buf = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buf.Length + n > MaxZipBytes) throw new InvalidDataException("wintun zip is unexpectedly large");
            buf.Write(chunk, 0, n);
        }
        InstallFromZip(buf.ToArray(), root ?? Directory, ZipSha256, DllSha256, checkSignature: true);
    }

    /// <summary>The offline part of <see cref="InstallAsync"/> (tests pass their own pins).</summary>
    internal static void InstallFromZip(byte[] zip, string root, string zipSha256, IReadOnlyDictionary<string, string> dllSha256, bool checkSignature)
    {
        var zipHash = Convert.ToHexStringLower(SHA256.HashData(zip));
        if (zipHash != zipSha256) throw new InvalidDataException($"wintun zip SHA-256 mismatch: got {zipHash}, expected {zipSha256}");
        if (!dllSha256.TryGetValue(Arch, out var dllExpected)) throw new PlatformNotSupportedException($"no wintun.dll for {Arch}");

        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        byte[] dll = ReadEntry(archive, $"wintun/bin/{Arch}/{DllName}") ?? throw new InvalidDataException($"wintun/bin/{Arch}/{DllName} not in the zip");
        byte[]? license = ReadEntry(archive, "wintun/LICENSE.txt");
        var dllHash = Convert.ToHexStringLower(SHA256.HashData(dll));
        if (dllHash != dllExpected) throw new InvalidDataException($"wintun.dll SHA-256 mismatch: got {dllHash}");

        var dir = Path.Combine(root, Arch);
        System.IO.Directory.CreateDirectory(dir);
        var final = Path.Combine(dir, DllName);
        var tmp = final + ".download";
        File.WriteAllBytes(tmp, dll);
        try
        {
            if (checkSignature && Authenticode.SignerOf(tmp) is var signer && signer != Signer)
                throw new InvalidDataException(signer is null ? "wintun.dll has no valid Authenticode signature" : $"wintun.dll is signed by '{signer}', expected '{Signer}'");
            File.Move(tmp, final, overwrite: true);
        }
        finally { File.Delete(tmp); }
        if (license != null) File.WriteAllBytes(Path.Combine(root, "LICENSE.txt"), license);
    }

    private static byte[]? ReadEntry(ZipArchive archive, string name)
    {
        var e = archive.Entries.FirstOrDefault(x => string.Equals(x.FullName.Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase));
        if (e is null || e.Length > MaxZipBytes) return null;
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

/// <summary>WinVerifyTrust (Authenticode) + the signer certificate's subject name.</summary>
public static class Authenticode
{
    /// <summary>
    /// The signer's subject CN when <paramref name="path"/> carries a valid, trusted Authenticode signature; null
    /// otherwise (unsigned, tampered, untrusted chain, not Windows). Revocation is not checked online (no network here).
    /// </summary>
    public static string? SignerOf(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return null;
        if (!Native.Verify(path)) return null;
        try
        {
#pragma warning disable SYSLIB0057 // the signer certificate of a signed file; no non-obsolete equivalent in the BCL
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }
        catch (CryptographicException) { return null; }
    }

    private static class Native
    {
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        private const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1, WTD_STATEACTION_VERIFY = 1,
            WTD_STATEACTION_CLOSE = 2, WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfo
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public nint hFile;
            public nint pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TrustData
        {
            public uint cbStruct;
            public nint pPolicyCallbackData, pSIPClientData;
            public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
            public nint pFile;
            public uint dwStateAction;
            public nint hWVTStateData, pwszURLReference;
            public uint dwProvFlags, dwUIContext;
            public nint pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern int WinVerifyTrust(nint hwnd, in Guid action, ref TrustData data);

        public static bool Verify(string path)
        {
            var fi = new FileInfo { cbStruct = (uint)Marshal.SizeOf<FileInfo>(), pcwszFilePath = path };
            nint pFile = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
            try
            {
                Marshal.StructureToPtr(fi, pFile, false);
                var td = new TrustData
                {
                    cbStruct = (uint)Marshal.SizeOf<TrustData>(),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = pFile,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                    dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
                };
                int hr = WinVerifyTrust(-1, GenericVerifyV2, ref td);
                td.dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust(-1, GenericVerifyV2, ref td);
                return hr == 0;
            }
            finally
            {
                Marshal.DestroyStructure<FileInfo>(pFile);
                Marshal.FreeHGlobal(pFile);
            }
        }
    }
}
