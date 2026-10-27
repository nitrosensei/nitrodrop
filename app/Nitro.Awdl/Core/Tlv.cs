// Nitro.Awdl: C# port of owly-awdl (https://github.com/t4t5/owly, crates/owly-awdl, commit 9a676e1).
// Copyright (c) 2026 T4T5 (Owly, MIT License). Port: Copyright (c) 2026 Nitrobyte. See LICENSE-OWLY.txt.
// Source: crates/owly-awdl/src/tlv.rs: AWDL TLVs (SPEC §3.2, AWDL-4..6). Offsets are into a TLV's value,
// after its 3-byte header.

using System.Text;

namespace Nitro.Awdl;

/// <summary>AWDL-6: TLV types.</summary>
public static class TlvKind
{
    public const byte ServiceResponse = 2;
    public const byte SyncParams = 4;
    public const byte ElectionParams = 5;
    public const byte ServiceParams = 6;
    public const byte HtCapabilities = 7;
    public const byte DataPathState = 12;
    public const byte Arpa = 16;
    public const byte Ieee80211Container = 17;
    public const byte ChannelSequence = 18;
    public const byte SyncTree = 20;
    public const byte Version = 21;
    public const byte BloomFilter = 22;
    public const byte ElectionParamsV2 = 24;
}

/// <summary>Device classes in the Version TLV (AWDL-6).</summary>
public static class DeviceClass
{
    public const byte MacOS = 1;
    public const byte IOS = 2;
    public const byte TvOS = 8;
}

/// <summary>One TLV: type and value (a slice of the frame body).</summary>
public readonly record struct Tlv(byte Kind, ReadOnlyMemory<byte> Value);

/// <summary>The Synchronization Parameters TLV (4), as far as a node reads it (offsets 0-30).</summary>
public readonly record struct SyncParams(
    byte NextAwChannel, ushort NextTu, byte MasterChannel, ushort AwPeriod, ushort AfPeriod,
    MacAddr Master, byte PresenceMode, ushort AwCounter)
{
    public static SyncParams Parse(ReadOnlySpan<byte> value)
    {
        var b = new WireReader(value);
        return new SyncParams(b.U8(0), b.Le16(1), b.U8(3), b.Le16(5), b.Le16(7), b.Mac(21), b.U8(27), b.Le16(29));
    }
}

/// <summary>What the Election Parameters TLVs (5 and 24) say about the sender.</summary>
public readonly record struct ElectionParams(
    MacAddr Master, MacAddr? Sync, uint Height, uint MasterMetric, uint SelfMetric, uint MasterCounter, uint SelfCounter)
{
    /// <summary>Version 1 (5): flags, ID, distance to the top master, its address and metric, the sender's metric.</summary>
    public static ElectionParams ParseV1(ReadOnlySpan<byte> value)
    {
        var b = new WireReader(value);
        return new ElectionParams(b.Mac(5), null, b.U8(3), b.Le32(11), b.Le32(15), 0, 0);
    }

    /// <summary>Version 2 (24): master and sync addresses, master counter, distance, both metrics, own counter.</summary>
    public static ElectionParams ParseV2(ReadOnlySpan<byte> value)
    {
        var b = new WireReader(value);
        return new ElectionParams(b.Mac(0), b.Mac(6), b.Le32(16), b.Le32(20), b.Le32(24), b.Le32(12), b.Le32(36));
    }
}

/// <summary>The HT Capabilities TLV (7): 2 reserved bytes, HT capability word, A-MPDU params, RX MCS mask, 2 reserved.</summary>
public readonly record struct HtCapabilities(ushort Capabilities, byte AmpduParams, uint RxMcs)
{
    /// <summary>One spatial stream, all its MCS: what a node here announces.</summary>
    public static readonly HtCapabilities OneStream = new(0x11ce, 0x1b, 0xff);

    /// <summary>The mask may be longer than one byte; bits past MCS 31 are dropped.</summary>
    public static HtCapabilities Parse(ReadOnlySpan<byte> value)
    {
        var b = new WireReader(value);
        int end = value.Length >= 8 ? value.Length - 2 : value.Length;
        var mask = b.Slice(5, Math.Max(end - 5, 1));
        uint rx = 0;
        for (int i = 0; i < Math.Min(4, mask.Length); i++) rx |= (uint)mask[i] << (8 * i);
        return new HtCapabilities(b.Le16(2), b.U8(4), rx);
    }

    /// <summary>The largest A-MSDU the sender receives: 7935 with the max A-MSDU bit, else 3839.</summary>
    public int MaxAmsdu => (Capabilities & 0x0800) != 0 ? 7935 : 3839;

    /// <summary>Short guard interval at 20 MHz.</summary>
    public bool Sgi20 => (Capabilities & 0x0020) != 0;
}

/// <summary>What a node reads from the Data Path State TLV (12).</summary>
public readonly record struct DataPathState(byte[]? Country, MacAddr? Infra)
{
    private const ushort InfraInfo = 0x0001, InfraAddress = 0x0002, AwdlAddress = 0x0004,
        CountryCode = 0x0100, SocialChannelMap = 0x0200;

    /// <summary>Flags, then each optional field the flags announce, as far as legacy read them.</summary>
    public static DataPathState Parse(ReadOnlySpan<byte> value)
    {
        var b = new WireReader(value);
        ushort flags = b.Le16(0);
        int at = 2;
        byte[]? country = null;
        MacAddr? infra = null;
        if ((flags & CountryCode) != 0) { country = b.Slice(at, 3).ToArray(); at += 3; }
        if ((flags & SocialChannelMap) != 0) { b.Slice(at, 2); at += 2; }
        if ((flags & InfraInfo) != 0) { b.Slice(at, 8); at += 8; }
        if ((flags & InfraAddress) != 0) { infra = b.Mac(at); at += 6; }
        if ((flags & AwdlAddress) != 0) b.Slice(at, 6);
        return new DataPathState(country, infra);
    }
}

/// <summary>
/// What one action frame's TLVs say, decoded whole before anything acts on them, so a malformed TLV late in the frame
/// can't half-update a peer.
/// </summary>
public sealed record Advert
{
    public SyncParams? Sync { get; init; }
    /// <summary>Version 2 when the frame has it, else version 1.</summary>
    public ElectionParams? Election { get; init; }
    public bool ElectionV2 { get; init; }
    public ChannelSequence? Sequence { get; init; }
    public string? Name { get; init; }
    /// <summary>(version, device class).</summary>
    public (byte Version, byte DeviceClass)? Version { get; init; }
    public HtCapabilities? Ht { get; init; }
    public DataPathState? DataPath { get; init; }

    /// <summary>Decodes an action frame's TLVs. Unknown TLVs are skipped by length (AWDL-6).</summary>
    public static Advert Parse(ReadOnlyMemory<byte> body)
    {
        SyncParams? sync = null;
        ElectionParams? election = null;
        bool v2 = false;
        ChannelSequence? seq = null;
        string? name = null;
        (byte, byte)? version = null;
        HtCapabilities? ht = null;
        DataPathState? dp = null;
        foreach (var t in AwdlTlv.Split(body))
        {
            var v = t.Value.Span;
            switch (t.Kind)
            {
                case TlvKind.SyncParams: sync = SyncParams.Parse(v); break;
                case TlvKind.ElectionParams:
                    var e1 = ElectionParams.ParseV1(v);
                    if (!v2) election = e1;
                    break;
                case TlvKind.ElectionParamsV2:
                    election = ElectionParams.ParseV2(v);
                    v2 = true;
                    break;
                case TlvKind.ChannelSequence: seq = ChannelSequence.Parse(v); break;
                case TlvKind.Arpa: name = AwdlTlv.ParseArpa(v); break;
                case TlvKind.Version: version = AwdlTlv.ParseVersion(v); break;
                case TlvKind.HtCapabilities: ht = HtCapabilities.Parse(v); break;
                case TlvKind.DataPathState: dp = DataPathState.Parse(v); break;
                // Sync tree layout unknown (owly docs/open-questions.md item 11); the rest carry nothing we act on.
            }
        }
        return new Advert { Sync = sync, Election = election, ElectionV2 = v2, Sequence = seq, Name = name, Version = version, Ht = ht, DataPath = dp };
    }
}

/// <summary>What a node announces about itself, for writing its TLVs (owly <c>Own</c>).</summary>
public sealed class OwnAdvert
{
    public required MacAddr Addr { get; init; }
    public required string Name { get; init; }
    public byte DeviceClass { get; init; } = Nitro.Awdl.DeviceClass.MacOS;
    /// <summary>The channel the radio is on now, 0 for none.</summary>
    public required byte Channel { get; init; }
    /// <summary>The channel the node calls its own: the one it serves in every slot it can.</summary>
    public required Channel MasterChannel { get; init; }
    public required ushort NextTu { get; init; }
    public required ushort AwCounter { get; init; }
    public ushort PsfIntervalTu { get; init; } = 110;
    public required ChannelSequence Sequence { get; init; }
    public required Election Election { get; init; }
    public HtCapabilities? Ht { get; init; } = HtCapabilities.OneStream;
}

/// <summary>TLV splitting, parsing helpers and writers.</summary>
public static class AwdlTlv
{
    /// <summary>The AWDL version a node announces: 3.4, as legacy did.</summary>
    public const byte Version = 0x34;

    /// <summary>The longest host name kept from, or written to, an ARPA TLV.</summary>
    public const int NameMax = 64;

    /// <summary>
    /// The TLVs in an action frame's body, in order (AWDL-4). A TLV whose length runs past the end is an error, and so
    /// is a tail of fewer than 3 bytes unless it's all zeros (AWDL-5: iPhones pad some MIFs). Zeros of 3 bytes or more
    /// read as empty TLVs of type 0, deprecated and ignored.
    /// </summary>
    public static IEnumerable<Tlv> Split(ReadOnlyMemory<byte> body)
    {
        var rest = body;
        while (true)
        {
            if (rest.Length < 3)
            {
                foreach (var b in rest.Span) if (b != 0) throw AwdlFormatException.Short();
                yield break;
            }
            var s = rest.Span;
            int len = s[1] | s[2] << 8;
            if (3 + len > rest.Length) throw AwdlFormatException.Short();
            yield return new Tlv(s[0], rest.Slice(3, len));
            rest = rest[(3 + len)..];
        }
    }

    /// <summary>The host name in an ARPA TLV (16): flags, then a length-prefixed label, cut at <see cref="NameMax"/>.</summary>
    public static string ParseArpa(ReadOnlySpan<byte> value)
    {
        var b = new WireReader(value);
        int len = b.U8(1);
        var name = b.Slice(2, len);
        return Encoding.UTF8.GetString(name[..Math.Min(len, NameMax)]);
    }

    /// <summary>The Version TLV (21): version, device class.</summary>
    public static (byte Version, byte DeviceClass) ParseVersion(ReadOnlySpan<byte> value)
    {
        var b = new WireReader(value);
        return (b.U8(0), b.U8(1));
    }

    private static void Put(List<byte> o, byte kind, Action<List<byte>> value)
    {
        o.U8(kind);
        int lenAt = o.Count;
        o.Le16(0);
        value(o);
        o.PatchLe16(lenAt, (ushort)(o.Count - lenAt - 2));
    }

    /// <summary>Sync Parameters (§3.2): fixed fields, AW count and phase, then the channel sequence and 2 bytes padding.</summary>
    public static void PutSyncParams(List<byte> o, OwnAdvert own) => Put(o, TlvKind.SyncParams, v =>
    {
        const byte ext = AwdlSchedule.PresenceMode - 1;
        v.U8(own.Channel);
        v.Le16(own.NextTu);
        v.U8(own.MasterChannel.Number);
        v.U8(0); // guard time
        v.Le16(AwdlSchedule.AwPeriodTu);
        v.Le16(own.PsfIntervalTu);
        v.Le16(0x1800); // flags: reserved
        v.Le16(AwdlSchedule.AwPeriodTu); // extended AW length
        v.Le16(AwdlSchedule.AwPeriodTu); // common AW length
        int elapsed = AwdlSchedule.AwPeriodTu * AwdlSchedule.PresenceMode - Math.Min((int)own.NextTu, 64);
        v.Le16((ushort)Math.Max(AwdlSchedule.AwPeriodTu - elapsed, 0)); // remaining AW length
        v.U8(ext); v.U8(ext); v.U8(ext); v.U8(ext); // min ext, max ext multicast, unicast, action frames
        v.Mac(own.Election.Master);
        v.U8(AwdlSchedule.PresenceMode);
        v.U8(0);
        v.Le16(own.AwCounter); // next AW sequence number
        v.Le16(own.AwCounter); // AP alignment
        own.Sequence.Put(v);
        v.Zeros(2);
    });

    /// <summary>Election Parameters, version 1.</summary>
    public static void PutElectionV1(List<byte> o, OwnAdvert own) => Put(o, TlvKind.ElectionParams, v =>
    {
        var e = own.Election;
        v.U8(0); // flags
        v.Le16(0); // ID
        v.U8((byte)Math.Min(e.Height, 255));
        v.U8(0);
        v.Mac(e.Master);
        v.Le32(e.MasterMetric);
        v.Le32(e.SelfMetric);
        v.Zeros(2);
    });

    /// <summary>The Channel Sequence TLV, with its 3 bytes of padding.</summary>
    public static void PutChannelSequence(List<byte> o, OwnAdvert own) => Put(o, TlvKind.ChannelSequence, v =>
    {
        own.Sequence.Put(v);
        v.Zeros(3);
    });

    /// <summary>Election Parameters, version 2.</summary>
    public static void PutElectionV2(List<byte> o, OwnAdvert own) => Put(o, TlvKind.ElectionParamsV2, v =>
    {
        var e = own.Election;
        v.Mac(e.Master);
        v.Mac(e.Sync);
        v.Le32(e.MasterCounter);
        v.Le32(e.Height);
        v.Le32(e.MasterMetric);
        v.Le32(e.SelfMetric);
        v.Le32(0);
        v.Le32(0);
        v.Le32(e.SelfCounter);
    });

    /// <summary>Service Parameters, all zero: a changed SUI makes peers flush their mDNS caches.</summary>
    public static void PutServiceParams(List<byte> o) => Put(o, TlvKind.ServiceParams, v => v.Zeros(9));

    public static void PutHtCapabilities(List<byte> o, HtCapabilities ht) => Put(o, TlvKind.HtCapabilities, v =>
    {
        v.Le16(0);
        v.Le16(ht.Capabilities);
        v.U8(ht.AmpduParams);
        v.U8((byte)ht.RxMcs);
        v.Le16(0);
    });

    /// <summary>Data Path State with legacy's flags (0x8f24), country "X0", the master channel's social map bit, our address.</summary>
    public static void PutDataPathState(List<byte> o, OwnAdvert own) => Put(o, TlvKind.DataPathState, v =>
    {
        v.Le16(0x8f24);
        v.Bytes("X0\0"u8);
        v.Le16(own.MasterChannel.Number switch { 6 => 0x0001, 44 => 0x0002, _ => 0x0004 });
        v.Mac(own.Addr);
        v.Le16(0);
    });

    /// <summary>The ARPA TLV: our host name, then <c>.local</c> as a DNS pointer.</summary>
    public static void PutArpa(List<byte> o, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        int end = Math.Min(bytes.Length, NameMax);
        while (end > 0 && end < bytes.Length && (bytes[end] & 0xc0) == 0x80) end--; // char boundary
        Put(o, TlvKind.Arpa, v =>
        {
            v.U8(3); // the one flag value IO80211Family accepts
            v.U8((byte)end);
            v.Bytes(bytes.AsSpan(0, end));
            v.Be16(0xc00c);
        });
    }

    public static void PutVersion(List<byte> o, byte deviceClass) => Put(o, TlvKind.Version, v =>
    {
        v.U8(Version);
        v.U8(deviceClass);
    });

    /// <summary>A PSF's or MIF's TLVs, in legacy's order (4, 5, 18, 24, 6, 7, [16], 12, 21). Only MIFs carry the host name.</summary>
    public static void PutAll(List<byte> o, OwnAdvert own, bool mif)
    {
        PutSyncParams(o, own);
        PutElectionV1(o, own);
        PutChannelSequence(o, own);
        PutElectionV2(o, own);
        PutServiceParams(o);
        if (own.Ht is { } ht) PutHtCapabilities(o, ht);
        if (mif) PutArpa(o, own.Name);
        PutDataPathState(o, own);
        PutVersion(o, own.DeviceClass);
    }
}
