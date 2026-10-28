using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AirDrop.App.Localization;

namespace AirDrop.App.Setup;

public sealed record FirmwareFile(string RepoPath, string Name, long? Size, string Sha256);

/// <summary>Same pins as tools/fetch-firmware.ps1 (keep both in sync).</summary>
public static class FirmwareSpec
{
    /// <summary>Pinned linux-firmware tag.</summary>
    public const string Tag = "20261019";

    /// <summary>git.kernel.org serves a bot challenge to browser-like UAs, so a plain tool UA is sent.</summary>
    public const string UserAgent = "airdrop-win-fetch-firmware/1.0";

    public static readonly FirmwareFile Firmware = new(
        "intel/iwlwifi/iwlwifi-so-a0-hr-b0-77.ucode", "iwlwifi-so-a0-hr-b0-77.ucode", 1500532,
        "bed4958236366c3c451b3ec994a56b2ad83c08d70e0a1eae42fb00f2eaabbcc8");

    public static readonly FirmwareFile License = new(
        "LICENSES/LICENCE.iwlwifi_firmware", "LICENCE.iwlwifi_firmware", null,
        "16d5040c7cf851fc693b7542e20870935b99802533ea1bfd231c377a2305e5c5");

    public static IReadOnlyList<FirmwareFile> Files { get; } = new[] { Firmware, License };

    public static IEnumerable<string> Urls(FirmwareFile f)
    {
        yield return $"https://git.kernel.org/pub/scm/linux/kernel/git/firmware/linux-firmware.git/plain/{f.RepoPath}?h={Tag}";
        yield return $"https://gitlab.com/kernel-firmware/linux-firmware/-/raw/{Tag}/{f.RepoPath}";
    }
}

/// <summary>Downloads the Intel firmware + licence and checks the pinned SHA-256 (C# port of tools/fetch-firmware.ps1).</summary>
public sealed class FirmwareFetcher(HttpClient http)
{
    public static string Sha256Of(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    public static bool Verify(string path, FirmwareFile f) =>
        File.Exists(path)
        && (f.Size is null || new FileInfo(path).Length == f.Size)
        && Sha256Of(path) == f.Sha256;

    /// <summary>
    /// Ensures <paramref name="f"/> is in <paramref name="destDir"/> with the right hash. A verified copy in one of
    /// <paramref name="alsoLookIn"/> (e.g. the repo's driver\firmware) is reused instead of downloading.
    /// </summary>
    public async Task<string> EnsureAsync(FirmwareFile f, string destDir, IEnumerable<string>? alsoLookIn = null,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, f.Name);
        if (Verify(dest, f))
        {
            SetupLog.Write($"firmware verified in cache: {f.Name}");
            return dest;
        }
        foreach (var dir in alsoLookIn ?? Enumerable.Empty<string>())
        {
            var candidate = Path.Combine(dir, f.Name);
            if (Verify(candidate, f))
            {
                File.Copy(candidate, dest, overwrite: true);
                SetupLog.Write($"firmware taken from local copy: {candidate}");
                return dest;
            }
        }

        var tmp = dest + ".part";
        foreach (var url in FirmwareSpec.Urls(f))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(SetupStrings.Setup_Status_Downloading(f.Name));
            SetupLog.Write($"downloading: {url}");
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd(FirmwareSpec.UserAgent);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using (var src = await resp.Content.ReadAsStreamAsync(ct))
                await using (var fs = File.Create(tmp))
                    await src.CopyToAsync(fs, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                SetupLog.Write($"failed: {url} ({ex.Message})");
                continue;
            }
            if (Verify(tmp, f))
            {
                File.Move(tmp, dest, overwrite: true);
                SetupLog.Write($"downloaded and verified: {f.Name} sha256={f.Sha256}");
                return dest;
            }
            SetupLog.Write($"SHA-256 mismatch: {url} (got {Sha256Of(tmp)})");
        }
        try { File.Delete(tmp); } catch (Exception) { }
        throw new InvalidOperationException(SetupStrings.Setup_Error_DownloadFailed(f.Name));
    }
}
