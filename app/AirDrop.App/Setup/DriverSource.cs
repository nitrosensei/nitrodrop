using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace AirDrop.App.Setup;

/// <summary>
/// Where the wizard takes airdropwifi.inf/.sys/.cat from. Release packages do not contain the driver (it ships as
/// NitroDrop-driver-&lt;ver&gt;.zip, outside the signed app); developer builds have it next to the exe (driver\).
/// The user picks the zip, the extracted airdropwifi.inf or its folder and the three files are copied into
/// <see cref="SetupPaths.ImportedDriverDir"/>. Nothing here is trusted: the wizard still checks every file against the
/// catalog before installing (CodeSigner.VerifyPackageAgainstCatalog). WPF-free (linked into AirDrop.Tests).
/// </summary>
public static class DriverSource
{
    /// <summary>Upper bound per file (airdropwifi.sys is ~0.3 MB).</summary>
    private const long MaxFileBytes = 32 * 1024 * 1024;

    /// <summary>Folders searched in order: next to the app (developer builds), then the imported copy.</summary>
    public static IEnumerable<string> Candidates()
    {
        yield return SetupPaths.BundledDriverDir;
        yield return SetupPaths.ImportedDriverDir;
    }

    /// <summary>First folder holding all of <see cref="DriverPackage.BundledFiles"/>, or null.</summary>
    public static string? Find(IEnumerable<string>? candidates = null) =>
        (candidates ?? Candidates()).FirstOrDefault(IsComplete);

    public static bool IsComplete(string dir) =>
        DriverPackage.BundledFiles.All(f => File.Exists(Path.Combine(dir, f)));

    /// <summary>
    /// Copies the driver files from <paramref name="source"/> (a .zip, a file inside the extracted folder such as
    /// airdropwifi.inf, or the folder itself) into <paramref name="target"/> (default <see cref="SetupPaths.ImportedDriverDir"/>).
    /// Returns the files that were not found (empty = complete). Only the known file names are taken from a zip, by
    /// name, wherever they sit in it; paths inside the archive are never used for writing.
    /// </summary>
    public static IReadOnlyList<string> Import(string source, string? target = null)
    {
        target ??= SetupPaths.ImportedDriverDir;
        var found = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(source);
            foreach (var e in zip.Entries)
            {
                var name = Path.GetFileName(e.FullName.Replace('\\', '/'));
                if (!DriverPackage.BundledFiles.Contains(name, StringComparer.OrdinalIgnoreCase) || found.ContainsKey(name)) continue;
                if (e.Length > MaxFileBytes) throw new InvalidDataException($"{name}: {e.Length} bytes");
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                found[name] = ms.ToArray();
            }
        }
        else
        {
            var dir = Directory.Exists(source) ? source : Path.GetDirectoryName(source);
            if (dir is null || !Directory.Exists(dir)) throw new DirectoryNotFoundException(source);
            foreach (var f in DriverPackage.BundledFiles)
            {
                var p = Path.Combine(dir, f);
                if (!File.Exists(p)) continue;
                if (new FileInfo(p).Length > MaxFileBytes) throw new InvalidDataException($"{f}: too large");
                found[f] = File.ReadAllBytes(p);
            }
        }

        var missing = DriverPackage.BundledFiles.Where(f => !found.ContainsKey(f)).ToList();
        if (missing.Count > 0) return missing;
        Directory.CreateDirectory(target);
        foreach (var (name, bytes) in found) File.WriteAllBytes(Path.Combine(target, name), bytes);
        SetupLog.Write($"driver package imported from {source} into {target}");
        return missing;
    }
}
