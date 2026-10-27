using System.Buffers.Binary;
using System.Security.Principal;

namespace Nitro.Sync.Ipc;

/// <summary>
/// Chrome/Edge native messaging framing: each message is a 32-bit length in native byte order (little-endian
/// on Windows) followed by that many bytes of UTF-8 JSON. Browser → host messages can be up to 64 MiB,
/// host → browser at most 1 MiB. The same framing is used on the NitroDrop.Sync named pipe.
/// This file is shared (linked) with Nitro.Sync.Host.
/// </summary>
public static class NativeMessaging
{
    public const int MaxToBrowser = 1024 * 1024;
    /// <summary>Our own cap for browser → host (tab lists are small; 4 MiB is generous).</summary>
    public const int MaxFromBrowser = 4 * 1024 * 1024;

    public const string HostName = "com.nitrobyte.nitrodrop";

    /// <summary>Reads one frame. Returns null on clean EOF before a header. Throws on oversize/truncation.</summary>
    public static async Task<byte[]?> ReadAsync(Stream s, int maxLength, CancellationToken ct = default)
    {
        var header = new byte[4];
        int got = await ReadFullAsync(s, header, ct);
        if (got == 0) return null;
        if (got < 4) throw new EndOfStreamException("truncated length prefix");
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (len > (uint)maxLength) throw new InvalidDataException($"message too large ({len} bytes)");
        var body = new byte[len];
        if (await ReadFullAsync(s, body, ct) != len) throw new EndOfStreamException("truncated message");
        return body;
    }

    public static async Task WriteAsync(Stream s, ReadOnlyMemory<byte> json, int maxLength, CancellationToken ct = default)
    {
        if (json.Length > maxLength) throw new InvalidDataException($"message too large ({json.Length} bytes)");
        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)json.Length);
        json.CopyTo(frame.AsMemory(4));
        await s.WriteAsync(frame, ct);
        await s.FlushAsync(ct);
    }

    private static async Task<int> ReadFullAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(total), ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>"NitroDrop.Sync.&lt;user SID&gt;" — per user, separate from the single-instance pipe.</summary>
    public static string PipeName()
    {
        string user = OperatingSystem.IsWindows()
            ? WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName
            : Environment.UserName;
        return "NitroDrop.Sync." + user;
    }
}
