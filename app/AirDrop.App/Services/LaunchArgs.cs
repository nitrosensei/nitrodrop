using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AirDrop.App.Services;

/// <summary>Files to send from one command line (or one forwarded command line).</summary>
/// <param name="Requested">The command line had <c>--send</c> or <c>--send-list</c> (even if no path survived).</param>
public sealed record SendRequest(IReadOnlyList<string> Paths, bool Requested)
{
    public static readonly SendRequest None = new(Array.Empty<string>(), false);
}

/// <summary>
/// "Send these files" part of the NitroDrop command line. WPF-free (linked into AirDrop.Tests).
/// <list type="bullet">
/// <item><c>--send &lt;path&gt; [&lt;path&gt;…]</c>: classic HKCU verb (<c>--send "%1"</c>, once per file with
///   MultiSelectModel=Player), the "Gönder" shortcut (all files at once) and the MSIX IExplorerCommand.</item>
/// <item><c>--send-list &lt;file&gt;</c>: the IExplorerCommand writes the paths to a <see cref="SendListFile"/> when the
///   command line would get too long (CreateProcess limit 32767 chars).</item>
/// </list>
/// Every token after <c>--send</c> up to the next <c>--flag</c> is a path; paths that do not exist are dropped,
/// duplicates (case-insensitive) are removed, order is kept.
/// </summary>
public static class LaunchArgs
{
    public const string Send = "--send";
    public const string SendList = "--send-list";

    public static SendRequest ParseSend(IReadOnlyList<string> args, Func<string, bool>? exists = null,
                                        Func<string, IReadOnlyList<string>>? readList = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        readList ??= SendListFile.ReadAndDelete;
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool requested = false, inSend = false;

        void Add(string raw)
        {
            var p = raw.Trim().Trim('"');
            if (p.Length == 0 || !exists(p) || !seen.Add(p)) return;
            paths.Add(p);
        }

        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (string.Equals(a, Send, StringComparison.OrdinalIgnoreCase)) { requested = inSend = true; continue; }
            if (string.Equals(a, SendList, StringComparison.OrdinalIgnoreCase))
            {
                requested = true;
                inSend = false;
                if (i + 1 < args.Count)
                {
                    IReadOnlyList<string> listed;
                    try { listed = readList(args[++i]); }
                    catch (Exception) { listed = Array.Empty<string>(); }
                    foreach (var p in listed) Add(p);
                }
                continue;
            }
            if (a.StartsWith("--", StringComparison.Ordinal)) { inSend = false; continue; }
            if (inSend) Add(a);
        }
        return requested ? new SendRequest(paths, true) : SendRequest.None;
    }

    /// <summary>
    /// One argument quoted for <c>CommandLineToArgvW</c> / the CRT: wrapped in quotes when needed, backslashes before a
    /// quote (and before the closing quote, e.g. <c>C:\</c>) doubled, embedded quotes escaped.
    /// </summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;
        var sb = new StringBuilder(arg.Length + 2).Append('"');
        int slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') sb.Append('\\', slashes * 2 + 1).Append('"');
            else sb.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return sb.Append('\\', slashes * 2).Append('"').ToString();
    }
}

/// <summary>
/// Path list handed over with <c>--send-list</c>: UTF-8 (BOM optional), one absolute path per line (CRLF or LF),
/// written to %TEMP% as <c>NitroDrop-send-*.txt</c> by app/NitroDrop.ShellExt (same format; keep both in sync).
/// The receiving instance deletes it after reading, but only when it looks like ours (prefix + %TEMP%), so a
/// <c>--send-list</c> pointing at some other file never deletes it.
/// </summary>
public static class SendListFile
{
    public const string Prefix = "NitroDrop-send-";
    public const string Extension = ".txt";
    private const long MaxBytes = 32L * 1024 * 1024;

    /// <summary>Writes a new list file (in <paramref name="directory"/>, default %TEMP%) and returns its path.</summary>
    public static string Write(IEnumerable<string> paths, string? directory = null)
    {
        var dir = directory ?? Path.GetTempPath();
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{Prefix}{Environment.ProcessId}-{Guid.NewGuid():N}{Extension}");
        var text = string.Concat(paths.Select(p => p + "\r\n"));
        using (var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
        {
            var bytes = new UTF8Encoding(false).GetBytes(text);
            fs.Write(bytes, 0, bytes.Length);
        }
        return file;
    }

    public static IReadOnlyList<string> Read(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length > MaxBytes) return Array.Empty<string>();
        // UTF-8 with BOM detection; invalid bytes become U+FFFD and then fail the exists check.
        var text = File.ReadAllText(file, Encoding.UTF8);
        return text.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).ToArray();
    }

    public static IReadOnlyList<string> ReadAndDelete(string file)
    {
        try
        {
            var list = Read(file);
            if (IsOurTempFile(file)) File.Delete(file);
            return list;
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    /// <summary>True for <c>%TEMP%\NitroDrop-send-*.txt</c> (or the given temp directory).</summary>
    public static bool IsOurTempFile(string file, string? tempDirectory = null)
    {
        try
        {
            var full = Path.GetFullPath(file);
            var name = Path.GetFileName(full);
            if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return false;
            var dir = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(full) ?? "");
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(tempDirectory ?? Path.GetTempPath()));
            return string.Equals(dir, temp, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }
}
