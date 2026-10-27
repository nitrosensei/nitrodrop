// Nitro.Awdl: C# port of owly-awdl (https://github.com/t4t5/owly, crates/owly-awdl, commit 9a676e1).
// Copyright (c) 2026 T4T5 (Owly, MIT License). Port: Copyright (c) 2026 Nitrobyte. See LICENSE-OWLY.txt.
// Source: crates/owly-awdl/src/ip.rs: the IPv6 packets AWDL's data path carries (SPEC §3.5, §4).

using System.Buffers.Binary;
using System.Net;

namespace Nitro.Awdl;

/// <summary>
/// An IPv6 packet and the link-layer addresses it travels between. On an L3 interface such as Wintun there is no
/// Ethernet header, so they are derived from the IPv6 addresses (<see cref="FromIp"/>, NET-2).
/// </summary>
public sealed record Ipv6Packet(MacAddr Dst, MacAddr Src, byte[] Data)
{
    private const int HeaderLen = 40;
    public const byte Tcp = 6;
    public const byte Udp = 17;
    public const byte IcmpV6 = 58;

    /// <summary>The packet in an Ethernet frame, if it carries IPv6 (NET-3).</summary>
    public static Ipv6Packet? FromEthernet(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 14 + HeaderLen || frame[12] != 0x86 || frame[13] != 0xdd) return null;
        return new Ipv6Packet(new MacAddr(frame[..6]), new MacAddr(frame[6..12]), frame[14..].ToArray());
    }

    public byte[] ToEthernet()
    {
        var o = new byte[14 + Data.Length];
        Dst.CopyTo(o);
        Src.CopyTo(o.AsSpan(6));
        o[12] = 0x86;
        o[13] = 0xdd;
        Data.CopyTo(o, 14);
        return o;
    }

    /// <summary>
    /// A packet whose link-layer addresses follow from its IPv6 ones: a modified-EUI-64 link-local unicast address
    /// gives the MAC it was made from (NET-2), a multicast one its 33:33 group. Null otherwise (e.g. DAD from ::, or a
    /// random/privacy link-local the OS picked, which no AWDL peer could answer).
    /// </summary>
    public static Ipv6Packet? FromIp(byte[] data)
    {
        if (!TryAddrs(data, out var src, out var dst)) return null;
        MacAddr d;
        if (dst[0] == 0xff) d = new MacAddr(0x33, 0x33, dst[12], dst[13], dst[14], dst[15]);
        else if (MacAddr.FromLinkLocal(dst) is { } m) d = m;
        else return null;
        return MacAddr.FromLinkLocal(src) is { } s ? new Ipv6Packet(d, s, data) : null;
    }

    /// <summary>The source and destination addresses.</summary>
    public (IPAddress Src, IPAddress Dst)? Addrs() =>
        TryAddrs(Data, out var s, out var d) ? (new IPAddress(s), new IPAddress(d)) : null;

    private static bool TryAddrs(ReadOnlySpan<byte> data, out ReadOnlySpan<byte> src, out ReadOnlySpan<byte> dst)
    {
        src = dst = default;
        if (data.Length < HeaderLen || data[0] >> 4 != 6) return false;
        src = data.Slice(8, 16);
        dst = data.Slice(24, 16);
        return true;
    }

    /// <summary>The upper-layer message if the next header is <paramref name="protocol"/> with no extension headers.</summary>
    public static ReadOnlySpan<byte> Upper(ReadOnlySpan<byte> data, byte protocol)
    {
        if (!TryAddrs(data, out _, out _) || data[6] != protocol) return default;
        int len = BinaryPrimitives.ReadUInt16BigEndian(data[4..6]);
        return HeaderLen + len <= data.Length ? data.Slice(HeaderLen, len) : default;
    }

    /// <summary>A TCP SYN to <paramref name="local"/> carrying nothing else: the start of a connection.</summary>
    public bool IsBareSynTo(MacAddr local)
    {
        if (Dst != local) return false;
        var tcp = Upper(Data, Tcp);
        if (tcp.Length < 20) return false;
        int flags = tcp[13] & 0x17; // FIN, SYN, RST, ACK
        int offset = (tcp[12] >> 4) * 4;
        return flags == 0x02 && offset >= 20 && tcp.Length == offset;
    }

    /// <summary>An ICMPv6 echo request from <paramref name="src"/> to <paramref name="dst"/>, link-local to link-local.</summary>
    public static Ipv6Packet EchoRequest(MacAddr src, MacAddr dst, ushort id, ushort seq, ReadOnlySpan<byte> payload)
    {
        var s = src.LinkLocal().GetAddressBytes();
        var d = dst.LinkLocal().GetAddressBytes();
        var icmp = new byte[8 + payload.Length];
        icmp[0] = 128;
        BinaryPrimitives.WriteUInt16BigEndian(icmp.AsSpan(4), id);
        BinaryPrimitives.WriteUInt16BigEndian(icmp.AsSpan(6), seq);
        payload.CopyTo(icmp.AsSpan(8));
        BinaryPrimitives.WriteUInt16BigEndian(icmp.AsSpan(2), Checksum(s, d, IcmpV6, icmp));
        return new Ipv6Packet(dst, src, Build(s, d, IcmpV6, 255, icmp));
    }

    /// <summary>An IPv6 header (no extension headers) around <paramref name="upper"/>.</summary>
    public static byte[] Build(ReadOnlySpan<byte> src16, ReadOnlySpan<byte> dst16, byte next, byte hopLimit, ReadOnlySpan<byte> upper)
    {
        var data = new byte[HeaderLen + upper.Length];
        data[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), (ushort)upper.Length);
        data[6] = next;
        data[7] = hopLimit;
        src16.CopyTo(data.AsSpan(8));
        dst16.CopyTo(data.AsSpan(24));
        upper.CopyTo(data.AsSpan(HeaderLen));
        return data;
    }

    /// <summary>Whether this is an ICMPv6 echo reply with identifier <paramref name="id"/>.</summary>
    public bool IsEchoReply(ushort id)
    {
        var m = Upper(Data, IcmpV6);
        return m.Length >= 8 && m[0] == 129 && BinaryPrimitives.ReadUInt16BigEndian(m[4..6]) == id;
    }

    /// <summary>The Internet checksum over the IPv6 pseudo-header and <paramref name="message"/>.</summary>
    public static ushort Checksum(ReadOnlySpan<byte> src16, ReadOnlySpan<byte> dst16, byte next, ReadOnlySpan<byte> message)
    {
        uint sum = 0;
        static uint Add(uint acc, ReadOnlySpan<byte> b)
        {
            for (int i = 0; i < b.Length; i += 2)
                acc += (uint)(b[i] << 8) | (i + 1 < b.Length ? b[i + 1] : 0u);
            return acc;
        }
        sum = Add(sum, src16);
        sum = Add(sum, dst16);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)message.Length);
        sum = Add(sum, len);
        sum = Add(sum, [0, 0, 0, next]);
        sum = Add(sum, message);
        while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)~sum;
    }

    public bool Equals(Ipv6Packet? other) =>
        other is not null && Dst == other.Dst && Src == other.Src && Data.AsSpan().SequenceEqual(other.Data);

    public override int GetHashCode() => HashCode.Combine(Dst, Src, Data.Length);
}
