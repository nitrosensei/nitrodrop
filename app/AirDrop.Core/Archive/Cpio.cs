using System.Text;

namespace AirDrop.Core.Archive;

public enum CpioEntryType { File, Directory, Symlink, Other }

public sealed class CpioEntry
{
    public required string Name { get; init; }
    public required int Mode { get; init; }
    public required long Size { get; init; }
    public DateTimeOffset ModifiedTime { get; init; }
    /// <summary>Entry data; valid only until the next call to <see cref="CpioReader.NextAsync"/>.</summary>
    public Stream Data { get; internal set; } = Stream.Null;

    public CpioEntryType Type => (Mode & CpioMode.TypeMask) switch
    {
        CpioMode.Regular => CpioEntryType.File,
        CpioMode.Directory => CpioEntryType.Directory,
        CpioMode.Symlink => CpioEntryType.Symlink,
        _ => CpioEntryType.Other,
    };
}

public static class CpioMode
{
    public const int TypeMask = 0xF000;     // 0170000
    public const int Regular = 0x8000;      // 0100000
    public const int Directory = 0x4000;    // 0040000
    public const int Symlink = 0xA000;      // 0120000
    public const int DefaultFile = Regular | 0x1A4;   // 0644
    public const int DefaultDir = Directory | 0x1ED;  // 0755
}

/// <summary>
/// Streaming writer for POSIX.1 portable "odc" cpio (magic 070707), the format libarchive
/// emits for "cpio" and the one OpenDrop sends to Apple devices.
/// </summary>
public sealed class CpioWriter : IAsyncDisposable
{
    public const string TrailerName = "TRAILER!!!";
    private const long MaxOdcSize = 077777777777L; // 11 octal digits
    private readonly Stream _out;
    private readonly bool _leaveOpen;
    private int _ino = 1;
    private bool _finished;

    public CpioWriter(Stream output, bool leaveOpen = false)
    {
        _out = output;
        _leaveOpen = leaveOpen;
    }

    public async Task AddDirectoryAsync(string name, DateTimeOffset? mtime = null, CancellationToken ct = default)
    {
        await WriteHeaderAsync(name, CpioMode.DefaultDir, 0, mtime ?? DateTimeOffset.UtcNow, 2, ct);
    }

    /// <summary>Adds a regular file; exactly <paramref name="size"/> bytes are copied from <paramref name="content"/>.</summary>
    public async Task AddFileAsync(string name, Stream content, long size, DateTimeOffset? mtime = null,
        Action<long>? onBytes = null, CancellationToken ct = default)
    {
        if (size < 0 || size > MaxOdcSize) throw new ArgumentOutOfRangeException(nameof(size), "odc cpio supports files up to 8 GiB");
        await WriteHeaderAsync(name, CpioMode.DefaultFile, size, mtime ?? DateTimeOffset.UtcNow, 1, ct);
        var buffer = new byte[81920];
        long remaining = size;
        while (remaining > 0)
        {
            int n = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (n == 0) throw new EndOfStreamException("File shorter than declared size");
            await _out.WriteAsync(buffer.AsMemory(0, n), ct);
            remaining -= n;
            onBytes?.Invoke(n);
        }
    }

    public async Task FinishAsync(CancellationToken ct = default)
    {
        if (_finished) return;
        _finished = true;
        _ino = 0;
        await WriteHeaderAsync(TrailerName, 0, 0, DateTimeOffset.UnixEpoch, 1, ct);
        await _out.FlushAsync(ct);
    }

    private async Task WriteHeaderAsync(string name, int mode, long size, DateTimeOffset mtime, int nlink, CancellationToken ct)
    {
        if (_finished && name != TrailerName) throw new InvalidOperationException("Archive already finished");
        var nameBytes = Encoding.UTF8.GetBytes(name);
        long mt = Math.Clamp(mtime.ToUnixTimeSeconds(), 0, MaxOdcSize);
        var sb = new StringBuilder(76);
        sb.Append("070707");
        sb.Append(Oct(0, 6));                 // dev
        sb.Append(Oct(_ino++ & 0x3FFFF, 6));  // ino (18-bit field)
        sb.Append(Oct(mode, 6));
        sb.Append(Oct(0, 6));                 // uid
        sb.Append(Oct(0, 6));                 // gid
        sb.Append(Oct(nlink, 6));
        sb.Append(Oct(0, 6));                 // rdev
        sb.Append(Oct(mt, 11));
        sb.Append(Oct(nameBytes.Length + 1, 6));
        sb.Append(Oct(size, 11));
        var header = Encoding.ASCII.GetBytes(sb.ToString());
        await _out.WriteAsync(header, ct);
        await _out.WriteAsync(nameBytes, ct);
        _out.WriteByte(0);
    }

    private static string Oct(long value, int width)
    {
        var s = Convert.ToString(value, 8);
        if (s.Length > width) throw new ArgumentOutOfRangeException(nameof(value), "Value too large for odc field");
        return s.PadLeft(width, '0');
    }

    public async ValueTask DisposeAsync()
    {
        if (!_leaveOpen) await _out.DisposeAsync();
    }
}

/// <summary>Streaming cpio reader; supports odc (070707) and newc (070701/070702).</summary>
public sealed class CpioReader
{
    private const int MaxNameLength = 4096;
    private readonly Stream _in;
    private BoundedStream? _current;
    private int _currentPad;
    private bool _done;

    public CpioReader(Stream input) => _in = input;

    /// <summary>Returns the next entry, or null at the trailer / end of stream.</summary>
    public async Task<CpioEntry?> NextAsync(CancellationToken ct = default)
    {
        if (_done) return null;
        if (_current != null)
        {
            await _current.DrainAsync(ct);
            await SkipAsync(_currentPad, ct);
            _current = null;
        }

        var magic = new byte[6];
        int got = await ReadAtLeastAsync(magic, ct);
        if (got == 0) { _done = true; return null; }
        if (got < 6) throw new InvalidDataException("Truncated cpio header");
        string m = Encoding.ASCII.GetString(magic);

        string name;
        int mode;
        long size, mtime;
        bool newc;
        if (m == "070707")
        {
            newc = false;
            var h = await ReadAsciiAsync(70, ct);
            mode = (int)ParseOct(h, 12, 6);
            mtime = ParseOct(h, 42, 11);
            int nameSize = (int)ParseOct(h, 53, 6);
            size = ParseOct(h, 59, 11);
            name = await ReadNameAsync(nameSize, ct);
        }
        else if (m is "070701" or "070702")
        {
            newc = true;
            var h = await ReadAsciiAsync(104, ct);
            mode = (int)ParseHex(h, 8);
            mtime = ParseHex(h, 40);
            size = ParseHex(h, 48);
            int nameSize = (int)ParseHex(h, 88);
            name = await ReadNameAsync(nameSize, ct);
            await SkipAsync(Pad4(110 + nameSize), ct);
        }
        else throw new InvalidDataException($"Unsupported cpio magic '{m}'");

        if (name == CpioWriter.TrailerName) { _done = true; return null; }

        _current = new BoundedStream(_in, size);
        _currentPad = newc ? Pad4(size) : 0;
        return new CpioEntry
        {
            Name = name,
            Mode = mode,
            Size = size,
            ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(mtime, 0, 253402300799)),
            Data = _current,
        };
    }

    private static int Pad4(long n) => (int)((4 - (n % 4)) % 4);

    private async Task<string> ReadNameAsync(int nameSize, CancellationToken ct)
    {
        if (nameSize < 1 || nameSize > MaxNameLength) throw new InvalidDataException("Bad cpio name size");
        var buf = new byte[nameSize];
        if (await ReadAtLeastAsync(buf, ct) != nameSize) throw new InvalidDataException("Truncated cpio name");
        int len = Array.IndexOf(buf, (byte)0);
        if (len < 0) len = nameSize;
        if (len != nameSize - 1) throw new InvalidDataException("Embedded NUL in cpio name");
        return new UTF8Encoding(false, true).GetString(buf, 0, len);
    }

    private async Task<string> ReadAsciiAsync(int n, CancellationToken ct)
    {
        var buf = new byte[n];
        if (await ReadAtLeastAsync(buf, ct) != n) throw new InvalidDataException("Truncated cpio header");
        return Encoding.ASCII.GetString(buf);
    }

    private async Task<int> ReadAtLeastAsync(byte[] buf, CancellationToken ct) =>
        await _in.ReadAtLeastAsync(buf, buf.Length, throwOnEndOfStream: false, ct);

    private async Task SkipAsync(int n, CancellationToken ct)
    {
        if (n <= 0) return;
        var buf = new byte[n];
        if (await ReadAtLeastAsync(buf, ct) != n) throw new InvalidDataException("Truncated cpio padding");
    }

    private static long ParseOct(string s, int start, int len)
    {
        long v = 0;
        for (int i = start; i < start + len; i++)
        {
            char c = s[i];
            if (c < '0' || c > '7') throw new InvalidDataException("Bad octal digit in cpio header");
            v = v * 8 + (c - '0');
        }
        return v;
    }

    private static long ParseHex(string s, int start) =>
        long.TryParse(s.AsSpan(start, 8), System.Globalization.NumberStyles.HexNumber, null, out var v)
            ? v : throw new InvalidDataException("Bad hex field in cpio header");
}

/// <summary>Read-only view of the next N bytes of an inner stream.</summary>
internal sealed class BoundedStream(Stream inner, long totalLength) : Stream
{
    private readonly long _length = totalLength;
    private long _remaining = totalLength;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_remaining <= 0) return 0;
        int n = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
        if (n == 0) throw new EndOfStreamException("Truncated cpio entry data");
        _remaining -= n;
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_remaining <= 0) return 0;
        int n = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], ct);
        if (n == 0) throw new EndOfStreamException("Truncated cpio entry data");
        _remaining -= n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public async Task DrainAsync(CancellationToken ct)
    {
        var buf = new byte[81920];
        while (await ReadAsync(buf, ct) > 0) { }
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
