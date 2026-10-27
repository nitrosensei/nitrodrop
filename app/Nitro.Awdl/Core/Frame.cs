// Nitro.Awdl: C# port of owly-awdl (https://github.com/t4t5/owly, crates/owly-awdl, commit 9a676e1).
// Copyright (c) 2026 T4T5 (Owly, MIT License). Port: Copyright (c) 2026 Nitrobyte. See LICENSE-OWLY.txt.
// Source: crates/owly-awdl/src/frame.rs: 802.11 framing, AWDL action frames (AWDL-3), data frames and A-MSDUs
// (AWDL-13, AWDL-14), Block Ack setup (AWDL-15). Frames start at the 802.11 MAC header and carry no FCS.

namespace Nitro.Awdl;

/// <summary>A frame for the radio to send (owly <c>Frame</c>).</summary>
public sealed record TxFrame(byte[] Bytes, MacAddr To, TxRate Rate, bool NoAck)
{
    /// <summary>A management action frame (PSF, MIF or Block Ack answer) rather than data.</summary>
    public bool IsAction => Bytes.Length >= 2 && ((Bytes[0] | Bytes[1] << 8) & Fc.TypeSubtype) == Fc.Action;
}

/// <summary>Frame control values, type and subtype.</summary>
public static class Fc
{
    public const ushort Action = 0x00d0;
    public const ushort Data = 0x0008;
    public const ushort QosData = 0x0088;
    public const ushort TypeSubtype = 0x00fc;
    public const ushort Type = 0x000c;
    public const ushort TypeManagement = 0x0000;
    public const ushort TypeData = 0x0008;
    public const ushort ToDs = 0x0100;
    public const ushort FromDs = 0x0200;
    public const ushort MoreFragments = 0x0400;
    public const ushort Retry = 0x0800;
    public const ushort Protected = 0x4000;
}

/// <summary>Action frame subtypes (AWDL-3).</summary>
public static class ActionSubtype
{
    /// <summary>Periodic Synchronization Frame.</summary>
    public const byte Psf = 0;
    /// <summary>Master Indication Frame.</summary>
    public const byte Mif = 3;
}

/// <summary>A three-address 802.11 MAC header.</summary>
public readonly record struct FrameHeader(ushort Fc, MacAddr Dst, MacAddr Src, MacAddr Bssid, ushort Seq);

/// <summary>What a received frame is, as far as AWDL cares.</summary>
public abstract record ReceivedFrame
{
    private ReceivedFrame() { }

    /// <summary>An AWDL PSF or MIF.</summary>
    public sealed record Action(FrameHeader Header, byte Subtype, uint PhyTx, uint TargetTx, ReadOnlyMemory<byte> Tlvs) : ReceivedFrame;

    /// <summary>A request for a Block Ack session (AWDL-15).</summary>
    public sealed record AddBaRequest(FrameHeader Header, byte Token, ushort Params, ushort Timeout) : ReceivedFrame;

    /// <summary>An AWDL data frame: one MSDU, or an A-MSDU.</summary>
    public sealed record Data(FrameHeader Header, bool Amsdu, ReadOnlyMemory<byte> Body) : ReceivedFrame;

    /// <summary>Anything else on the air.</summary>
    public sealed record Other : ReceivedFrame
    {
        public static readonly Other Instance = new();
    }
}

/// <summary>One packet out of a data frame, with the addresses it travels between.</summary>
public readonly record struct Msdu(MacAddr Dst, MacAddr Src, ushort Ethertype, ReadOnlyMemory<byte> Payload);

/// <summary>Parsing and building of AWDL 802.11 frames.</summary>
public static class AwdlFrame
{
    /// <summary>The 802.11 MAC header's length, without QoS control.</summary>
    public const int HeaderLen = 24;
    /// <summary>The AWDL action header's length (AWDL-3).</summary>
    public const int ActionHeaderLen = 16;
    /// <summary>LLC/SNAP and the AWDL data header (AWDL-13).</summary>
    public const int DataHeaderLen = 16;
    /// <summary>AWDL-14: the most A-MSDU subframes unpacked from one frame.</summary>
    public const int MaxAmsduSubframes = 16;

    private const byte CategoryVendor = 127;
    private const byte AwdlType = 8;
    /// <summary>AWDL 1.0, major in the high nibble: what the action header carries.</summary>
    private const byte AwdlVersionCompat = 0x10;
    private const byte CategoryBlockAck = 3;
    private const byte AddBaRequestCode = 0;
    private const byte AddBaResponseCode = 1;
    private static ReadOnlySpan<byte> LlcSnap => [0xaa, 0xaa, 0x03, 0x00, 0x17, 0xf2, 0x08, 0x00];
    private const ushort DataHead = 0x0403;
    public const ushort EthertypeIpv6 = 0x86dd;

    /// <summary>A frame's three-address MAC header, if it has one.</summary>
    public static FrameHeader? ParseHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderLen) return null;
        var b = new WireReader(bytes);
        return new FrameHeader(b.Le16(0), b.Mac(4), b.Mac(10), b.Mac(16), (ushort)(b.Le16(22) >> 4));
    }

    /// <summary>Reads a frame's MAC header and classifies it. Throws <see cref="AwdlFormatException"/> on truncation.</summary>
    public static ReceivedFrame Parse(ReadOnlyMemory<byte> bytes)
    {
        var b = new WireReader(bytes.Span);
        // Control and extension frames have no three-address header: not AWDL, not malformed.
        ushort t = (ushort)(b.Le16(0) & Fc.Type);
        if (t != Fc.TypeManagement && t != Fc.TypeData) return ReceivedFrame.Other.Instance;
        var header = ParseHeader(bytes.Span) ?? throw AwdlFormatException.Short();
        ushort fc = header.Fc;
        int frag = b.Le16(22) & 0xf;
        if ((fc & (Fc.Protected | Fc.MoreFragments)) != 0 || frag != 0) return ReceivedFrame.Other.Instance;
        var body = bytes[HeaderLen..];
        switch (fc & Fc.TypeSubtype)
        {
            case Fc.Action:
                return ParseAction(header, body);
            case Fc.Data:
            case Fc.QosData:
                // AWDL-1: between peers on AWDL's BSSID, neither to nor from a distribution system.
                if ((fc & (Fc.ToDs | Fc.FromDs)) != 0 || header.Bssid != Awdl.Bssid) return ReceivedFrame.Other.Instance;
                if ((fc & Fc.TypeSubtype) == Fc.QosData)
                {
                    ushort qos = new WireReader(body.Span).Le16(0);
                    return new ReceivedFrame.Data(header, (qos & 0x0080) != 0, body[2..]);
                }
                return new ReceivedFrame.Data(header, false, body);
            default:
                return ReceivedFrame.Other.Instance;
        }
    }

    private static ReceivedFrame ParseAction(FrameHeader header, ReadOnlyMemory<byte> bodyMem)
    {
        var body = bodyMem.Span;
        if (body.IsEmpty) return ReceivedFrame.Other.Instance;
        var b = new WireReader(body);
        if (b.U8(0) == CategoryBlockAck && b.U8(1) == AddBaRequestCode)
            return new ReceivedFrame.AddBaRequest(header, b.U8(2), b.Le16(3), b.Le16(5));
        if (body.Length < ActionHeaderLen || body[0] != CategoryVendor || !body.Slice(1, 3).SequenceEqual(Awdl.Oui)
            || body[4] != AwdlType || body[5] != AwdlVersionCompat)
            return ReceivedFrame.Other.Instance;
        byte subtype = body[6];
        if (subtype != ActionSubtype.Psf && subtype != ActionSubtype.Mif) return ReceivedFrame.Other.Instance;
        return new ReceivedFrame.Action(header, subtype, b.Le32(8), b.Le32(12), bodyMem[ActionHeaderLen..]);
    }

    /// <summary>The packets in a data frame's body (AWDL-13); an A-MSDU holds at most 16 (AWDL-14).</summary>
    public static List<Msdu> Msdus(FrameHeader header, bool amsdu, ReadOnlyMemory<byte> body)
    {
        if (!amsdu) return [ParseMsdu(header.Dst, header.Src, body)];
        var o = new List<Msdu>();
        var rest = body;
        while (!rest.IsEmpty)
        {
            if (o.Count == MaxAmsduSubframes) throw AwdlFormatException.Malformed("too many A-MSDU subframes");
            var b = new WireReader(rest.Span);
            var dst = b.Mac(0);
            var src = b.Mac(6);
            int len = b.Be16(12);
            b.Slice(14, len);
            o.Add(ParseMsdu(dst, src, rest.Slice(14, len)));
            rest = rest[(14 + len)..];
            if (!rest.IsEmpty)
            {
                int pad = (4 - (14 + len) % 4) % 4; // each subframe but the last is padded to 4 bytes
                if (pad > rest.Length) throw AwdlFormatException.Short();
                rest = rest[pad..];
            }
        }
        return o;
    }

    private static Msdu ParseMsdu(MacAddr dst, MacAddr src, ReadOnlyMemory<byte> body)
    {
        var b = new WireReader(body.Span);
        var llc = b.Slice(0, 8);
        // As legacy checked it: SNAP, and AWDL's protocol ID. The OUI isn't.
        if (!llc[..3].SequenceEqual(LlcSnap[..3]) || !llc[6..].SequenceEqual(LlcSnap[6..]))
            throw AwdlFormatException.Malformed("not AWDL's LLC/SNAP header");
        ushort ethertype = b.Be16(14);
        b.Rest(DataHeaderLen);
        return new Msdu(dst, src, ethertype, body[DataHeaderLen..]);
    }

    /// <summary>Writes a MAC header on AWDL's BSSID (AWDL-1).</summary>
    public static void PutHeader(List<byte> o, ushort fc, MacAddr dst, MacAddr src, ushort seq)
    {
        o.Le16(fc);
        o.Le16(0); // duration
        o.Mac(dst);
        o.Mac(src);
        o.Mac(Awdl.Bssid);
        o.Le16((ushort)((seq & 0x0fff) << 4));
    }

    /// <summary>An AWDL action frame (AWDL-3): header stamped with <paramref name="time"/> (sender's µs clock), then TLVs.</summary>
    public static byte[] ActionFrame(MacAddr src, ushort seq, byte subtype, uint time, Action<List<byte>> tlvs)
    {
        var o = new List<byte>(512);
        PutHeader(o, Fc.Action, MacAddr.Broadcast, src, seq);
        o.U8(CategoryVendor);
        o.Bytes(Awdl.Oui);
        o.U8(AwdlType);
        o.U8(AwdlVersionCompat);
        o.U8(subtype);
        o.U8(0);
        o.Le32(time); // PHY TX time
        o.Le32(time); // target TX time
        tlvs(o);
        return [.. o];
    }

    private static void PutMsdu(List<byte> o, ushort awdlSeq, ReadOnlySpan<byte> payload)
    {
        o.Bytes(LlcSnap);
        o.Le16(DataHead);
        o.Le16(awdlSeq);
        o.Le16(0);
        o.Be16(EthertypeIpv6);
        o.Bytes(payload);
    }

    /// <summary>AWDL-13: a data frame carrying one IPv6 packet.</summary>
    public static byte[] DataFrame(MacAddr src, MacAddr dst, ushort seq, ushort awdlSeq, ReadOnlySpan<byte> packet)
    {
        var o = new List<byte>(HeaderLen + DataHeaderLen + packet.Length);
        PutHeader(o, Fc.Data, dst, src, seq);
        PutMsdu(o, awdlSeq, packet);
        return [.. o];
    }

    /// <summary>The bytes one packet adds to an A-MSDU, less up to 3 bytes of padding before it.</summary>
    public static int AmsduSubframeLen(int packetLen) => 14 + DataHeaderLen + packetLen;

    /// <summary>An A-MSDU of IPv6 packets to one peer in a QoS data frame (TID 0); consecutive AWDL seqs from <paramref name="awdlSeq"/>.</summary>
    public static byte[] AmsduFrame(MacAddr src, MacAddr dst, ushort seq, ushort awdlSeq, IReadOnlyList<byte[]> packets)
    {
        var o = new List<byte>(2048);
        PutHeader(o, Fc.QosData, dst, src, seq);
        o.Le16(0x0080); // QoS control: A-MSDU present
        for (int i = 0; i < packets.Count; i++)
        {
            if (i > 0)
                while ((o.Count - HeaderLen - 2) % 4 != 0) o.U8(0);
            o.Mac(dst);
            o.Mac(src);
            o.Be16((ushort)(DataHeaderLen + packets[i].Length));
            PutMsdu(o, (ushort)(awdlSeq + i), packets[i]);
        }
        return [.. o];
    }

    /// <summary>
    /// AWDL-15: an ADDBA response accepting a request, as legacy patch owl-10 answers it: immediate Block Ack, the
    /// requested TID, no A-MSDU inside A-MPDUs, a window of at most 64.
    /// </summary>
    public static byte[] AddBaResponse(MacAddr src, MacAddr dst, ushort seq, byte token, ushort requested, ushort timeout)
    {
        int window = (requested >> 6) & 0x3ff;
        if (window is 0 or > 64) window = 64;
        ushort p = (ushort)((requested & 0x003c) | 0x0002 | (window << 6));
        var o = new List<byte>(HeaderLen + 9);
        PutHeader(o, Fc.Action, dst, src, seq);
        o.U8(CategoryBlockAck);
        o.U8(AddBaResponseCode);
        o.U8(token);
        o.Le16(0); // success
        o.Le16(p);
        o.Le16(timeout);
        return [.. o];
    }
}
