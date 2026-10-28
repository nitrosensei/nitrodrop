// Nitro.Awdl: C# port of owly-awdl (https://github.com/t4t5/owly, crates/owly-awdl, commit 9a676e1).
// Copyright (c) 2026 T4T5 (Owly, MIT License). Port: Copyright (c) 2026 Nitrobyte. See LICENSE-OWLY.txt.
// Source: crates/owly-awdl/src/lib.rs (Time, TU, MacAddr, BSSID, OUI, Error).

using System.Globalization;
using System.Net;

namespace Nitro.Awdl;

/// <summary>A monotonic count of microseconds from an epoch the caller picks (owly <c>Time</c>).</summary>
public readonly record struct AwdlTime(ulong Micros) : IComparable<AwdlTime>
{
    /// <summary><paramref name="us"/> microseconds later (saturating).</summary>
    public AwdlTime Plus(ulong us) => new(Micros > ulong.MaxValue - us ? ulong.MaxValue : Micros + us);

    /// <summary><paramref name="us"/> microseconds earlier, or the epoch.</summary>
    public AwdlTime Minus(ulong us) => new(Micros > us ? Micros - us : 0);

    /// <summary>Microseconds since <paramref name="earlier"/>, or 0 if it is later.</summary>
    public ulong Since(AwdlTime earlier) => Micros > earlier.Micros ? Micros - earlier.Micros : 0;

    public int CompareTo(AwdlTime other) => Micros.CompareTo(other.Micros);
    public static bool operator <(AwdlTime a, AwdlTime b) => a.Micros < b.Micros;
    public static bool operator >(AwdlTime a, AwdlTime b) => a.Micros > b.Micros;
    public static bool operator <=(AwdlTime a, AwdlTime b) => a.Micros <= b.Micros;
    public static bool operator >=(AwdlTime a, AwdlTime b) => a.Micros >= b.Micros;
    public static AwdlTime Min(AwdlTime a, AwdlTime b) => a <= b ? a : b;
    public static AwdlTime Max(AwdlTime a, AwdlTime b) => a >= b ? a : b;
    public override string ToString() => $"{Micros} µs";
}

/// <summary>Protocol-wide constants (owly lib.rs).</summary>
public static class Awdl
{
    /// <summary>One time unit, 1024 µs: what 802.11 and AWDL count in.</summary>
    public const ulong TU = 1024;

    /// <summary>AWDL-1: the BSSID every AWDL frame carries.</summary>
    public static readonly MacAddr Bssid = new(0x00, 0x25, 0x00, 0xff, 0x94, 0x73);

    /// <summary>Apple's OUI, in action frames and in data frames' SNAP header.</summary>
    public static ReadOnlySpan<byte> Oui => [0x00, 0x17, 0xf2];
}

/// <summary>An 802.11 MAC address (owly <c>MacAddr</c>).</summary>
public readonly struct MacAddr : IEquatable<MacAddr>, IComparable<MacAddr>
{
    private readonly ulong _v; // big-endian packed into the low 48 bits, so ordering matches byte order

    public MacAddr(byte b0, byte b1, byte b2, byte b3, byte b4, byte b5) =>
        _v = (ulong)b0 << 40 | (ulong)b1 << 32 | (ulong)b2 << 24 | (ulong)b3 << 16 | (ulong)b4 << 8 | b5;

    public MacAddr(ReadOnlySpan<byte> six)
    {
        if (six.Length < 6) throw new ArgumentException("A MAC address is 6 bytes", nameof(six));
        _v = (ulong)six[0] << 40 | (ulong)six[1] << 32 | (ulong)six[2] << 24 | (ulong)six[3] << 16 | (ulong)six[4] << 8 | six[5];
    }

    public static readonly MacAddr Broadcast = new(0xff, 0xff, 0xff, 0xff, 0xff, 0xff);

    public byte this[int i] => (byte)(_v >> (8 * (5 - i)));

    public byte[] ToArray() => [this[0], this[1], this[2], this[3], this[4], this[5]];

    public void CopyTo(Span<byte> dest)
    {
        for (int i = 0; i < 6; i++) dest[i] = this[i];
    }

    /// <summary>Broadcast or multicast: the group bit is set.</summary>
    public bool IsMulticast => (this[0] & 1) == 1;

    /// <summary>A locally administered address beside this one (owly <c>MacAddr::local</c>).</summary>
    public MacAddr Local() => new(0x02, this[1], this[2], this[3], this[4], this[5]);

    /// <summary>NET-1: the modified EUI-64 link-local address for this MAC (fe80::/64, U/L bit flipped, ff:fe inserted).</summary>
    public IPAddress LinkLocal(long scopeId = 0)
    {
        Span<byte> o = stackalloc byte[16];
        o[0] = 0xfe; o[1] = 0x80;
        o[8] = (byte)(this[0] ^ 0x02); o[9] = this[1]; o[10] = this[2];
        o[11] = 0xff; o[12] = 0xfe;
        o[13] = this[3]; o[14] = this[4]; o[15] = this[5];
        return new IPAddress(o, scopeId);
    }

    /// <summary>The MAC a modified EUI-64 link-local address was made from, if it was made from one.</summary>
    public static MacAddr? FromLinkLocal(IPAddress addr) =>
        addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? FromLinkLocal(addr.GetAddressBytes()) : null;

    public static MacAddr? FromLinkLocal(ReadOnlySpan<byte> o)
    {
        if (o.Length != 16) return null;
        if (o[0] != 0xfe || o[1] != 0x80 || o[2] != 0 || o[3] != 0 || o[4] != 0 || o[5] != 0 || o[6] != 0 || o[7] != 0
            || o[11] != 0xff || o[12] != 0xfe)
            return null;
        return new MacAddr((byte)(o[8] ^ 0x02), o[9], o[10], o[13], o[14], o[15]);
    }

    /// <summary>Colon-separated hex octets, either case; also <c>ether_ntoa</c>'s unpadded ones.</summary>
    public static MacAddr Parse(string s) =>
        TryParse(s, out var m) ? m : throw new FormatException($"not a MAC address: {s}");

    public static bool TryParse(string? s, out MacAddr mac)
    {
        mac = default;
        if (s == null) return false;
        var parts = s.Split(':');
        if (parts.Length != 6) return false;
        Span<byte> b = stackalloc byte[6];
        for (int i = 0; i < 6; i++)
        {
            var p = parts[i];
            if (p.Length is 0 or > 2 || !byte.TryParse(p, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out b[i]))
                return false;
        }
        mac = new MacAddr(b);
        return true;
    }

    public bool Equals(MacAddr other) => _v == other._v;
    public override bool Equals(object? obj) => obj is MacAddr m && Equals(m);
    public override int GetHashCode() => _v.GetHashCode();
    public int CompareTo(MacAddr other) => _v.CompareTo(other._v);
    public static bool operator ==(MacAddr a, MacAddr b) => a._v == b._v;
    public static bool operator !=(MacAddr a, MacAddr b) => a._v != b._v;
    public static bool operator <(MacAddr a, MacAddr b) => a._v < b._v;
    public static bool operator >(MacAddr a, MacAddr b) => a._v > b._v;
    public static bool operator <=(MacAddr a, MacAddr b) => a._v <= b._v;
    public static bool operator >=(MacAddr a, MacAddr b) => a._v >= b._v;

    public override string ToString() =>
        string.Create(17, this, static (span, m) =>
        {
            for (int i = 0; i < 6; i++)
            {
                if (i > 0) span[i * 3 - 1] = ':';
                m[i].TryFormat(span[(i * 3)..], out _, "x2", CultureInfo.InvariantCulture);
            }
        });
}

/// <summary>Why bytes off the air were refused (owly <c>Error</c>).</summary>
public sealed class AwdlFormatException : FormatException
{
    /// <summary>True: shorter than its headers or lengths say. False: a field holds a value we don't accept.</summary>
    public bool Truncated { get; }

    private AwdlFormatException(bool truncated, string message) : base(message) => Truncated = truncated;

    public static AwdlFormatException Short() => new(true, "truncated");
    public static AwdlFormatException Malformed(string what) => new(false, "malformed: " + what);
}
