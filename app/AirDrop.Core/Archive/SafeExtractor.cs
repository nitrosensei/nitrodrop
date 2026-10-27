namespace AirDrop.Core.Archive;

public sealed class UnsafeArchiveException(string message) : Exception(message);

/// <summary>Extracts cpio entries into a folder, refusing anything that could escape it.</summary>
public sealed class SafeExtractor
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly string _root;
    private readonly Dictionary<string, string> _topLevelMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _extracted = [];

    /// <summary>Upper bound on total extracted bytes (null = unlimited).</summary>
    public long? MaxTotalBytes { get; init; }
    public IReadOnlyList<string> ExtractedPaths => _extracted;
    public long TotalBytes { get; private set; }

    public SafeExtractor(string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
    }

    /// <summary>Extracts a whole (uncompressed) cpio stream. Returns top-level paths written.</summary>
    public async Task<IReadOnlyList<string>> ExtractAsync(Stream cpio, Action<long>? onBytes = null, CancellationToken ct = default)
    {
        var reader = new CpioReader(cpio);
        while (await reader.NextAsync(ct) is { } entry)
            await ExtractEntryAsync(entry, onBytes, ct);
        return _extracted;
    }

    public async Task ExtractEntryAsync(CpioEntry entry, Action<long>? onBytes = null, CancellationToken ct = default)
    {
        var parts = SplitSafe(entry.Name);
        if (parts.Count == 0) return; // "." / "./" root entry

        switch (entry.Type)
        {
            case CpioEntryType.Directory:
            {
                var dir = MapPath(parts);
                EnsureNoReparsePoints(dir);
                Directory.CreateDirectory(dir);
                Track(parts, dir);
                break;
            }
            case CpioEntryType.File:
            {
                var file = MapPath(parts);
                var parent = Path.GetDirectoryName(file)!;
                EnsureNoReparsePoints(parent);
                Directory.CreateDirectory(parent);
                if (MaxTotalBytes is { } max && TotalBytes + entry.Size > max)
                    throw new UnsafeArchiveException("Upload exceeds size limit");
                await using (var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buf = new byte[81920];
                    int n;
                    while ((n = await entry.Data.ReadAsync(buf, ct)) > 0)
                    {
                        await fs.WriteAsync(buf.AsMemory(0, n), ct);
                        TotalBytes += n;
                        onBytes?.Invoke(n);
                    }
                }
                try { File.SetLastWriteTimeUtc(file, entry.ModifiedTime.UtcDateTime); } catch (IOException) { }
                Track(parts, file);
                break;
            }
            case CpioEntryType.Symlink:
                throw new UnsafeArchiveException($"Symbolic links are not allowed: {entry.Name}");
            default:
                throw new UnsafeArchiveException($"Unsupported entry type (mode {Convert.ToString(entry.Mode, 8)}): {entry.Name}");
        }
    }

    private void Track(List<string> parts, string fullPath)
    {
        if (parts.Count == 1) _extracted.Add(fullPath);
    }

    /// <summary>
    /// Validates an archive path and returns its sanitized components.
    /// Rejects absolute paths, drive letters, backslashes, NUL and ".." segments.
    /// </summary>
    public static List<string> SplitSafe(string name)
    {
        if (string.IsNullOrEmpty(name)) throw new UnsafeArchiveException("Empty entry name");
        if (name.Contains('\0')) throw new UnsafeArchiveException("NUL in entry name");
        if (name.Contains('\\')) throw new UnsafeArchiveException($"Backslash in entry name: {name}");
        if (name.StartsWith('/')) throw new UnsafeArchiveException($"Absolute path: {name}");
        if (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':')
            throw new UnsafeArchiveException($"Drive-qualified path: {name}");

        var result = new List<string>();
        foreach (var raw in name.Split('/'))
        {
            if (raw.Length == 0 || raw == ".") continue;
            if (raw == "..") throw new UnsafeArchiveException($"Path traversal: {name}");
            result.Add(SanitizeComponent(raw));
        }
        return result;
    }

    private static string SanitizeComponent(string part)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = part.Select(c => c < 0x20 || invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).TrimEnd('.', ' '); // Windows silently strips these
        if (s.Length == 0 || s == "..") s = "_";
        var stem = s.Split('.')[0];
        if (ReservedNames.Contains(stem)) s = "_" + s;
        return s;
    }

    private string MapPath(List<string> parts)
    {
        // Rename colliding top-level items once; nested entries follow the mapping.
        if (!_topLevelMap.TryGetValue(parts[0], out var top))
        {
            top = UniqueName(parts[0]);
            _topLevelMap[parts[0]] = top;
        }
        var full = Path.GetFullPath(Path.Combine([_root, top, .. parts.Skip(1)]));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnsafeArchiveException("Resolved path escapes target directory");
        return full;
    }

    private string UniqueName(string name)
    {
        if (!Exists(Path.Combine(_root, name))) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 1; i < 10000; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!Exists(Path.Combine(_root, candidate))) return candidate;
        }
        throw new IOException("Could not find a free file name");
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    /// <summary>Refuses to write through junctions/symlinks that already exist under the root.</summary>
    private void EnsureNoReparsePoints(string dir)
    {
        var rel = Path.GetRelativePath(_root, dir);
        if (rel == ".") return;
        var current = _root;
        foreach (var seg in rel.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, seg);
            if (!Directory.Exists(current) && !File.Exists(current)) return;
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new UnsafeArchiveException($"Refusing to write through reparse point: {current}");
        }
    }
}
