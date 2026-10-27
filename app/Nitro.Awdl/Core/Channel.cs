// Nitro.Awdl: C# port of owly-awdl (https://github.com/t4t5/owly, crates/owly-awdl, commit 9a676e1).
// Copyright (c) 2026 T4T5 (Owly, MIT License). Port: Copyright (c) 2026 Nitrobyte. See LICENSE-OWLY.txt.
// Source: crates/owly-awdl/src/channel.rs: channels and channel sequences (SPEC §3.4, AWDL-10..12).

namespace Nitro.Awdl;

/// <summary>An 802.11 channel number, on 2.4 GHz up to 14 and on 5 GHz from 32.</summary>
public readonly record struct Channel(byte Number)
{
    /// <summary>AWDL's social channels (AWDL-11).</summary>
    public static readonly Channel[] Social = [new(6), new(44), new(149)];

    /// <summary>The centre frequency of its 20 MHz channel.</summary>
    public int? FreqMhz => Number switch
    {
        >= 1 and <= 13 => 2407 + 5 * Number,
        14 => 2484,
        >= 32 and <= 196 => 5000 + 5 * Number,
        _ => null,
    };

    public static Channel? FromFreqMhz(int mhz) => mhz switch
    {
        2484 => new Channel(14),
        >= 2412 and <= 2472 when (mhz - 2407) % 5 == 0 => new Channel((byte)((mhz - 2407) / 5)),
        >= 5160 and <= 5980 when mhz % 5 == 0 => new Channel((byte)((mhz - 5000) / 5)),
        _ => null,
    };

    /// <summary>Its operating class in the opclass encoding: 81 on 2.4 GHz, 128 on 5 GHz.</summary>
    public byte OpClass => Number <= 14 ? (byte)0x51 : (byte)0x80;

    public override string ToString() => $"ch{Number}";
}

/// <summary>How a sequence writes each slot's channel (AWDL-10).</summary>
public enum SequenceEncoding : byte
{
    /// <summary>One byte: the channel number.</summary>
    Simple = 0,
    /// <summary>Two bytes: flags, then the channel number.</summary>
    Legacy = 1,
    /// <summary>Two bytes: the channel number, then its operating class.</summary>
    OpClass = 3,
}

/// <summary>
/// A 16-slot channel sequence. Each slot keeps the bytes it was written with, so a sequence copied from a peer goes
/// out byte for byte as the peer sent it; it is decoded with the encoding it came in.
/// </summary>
public sealed class ChannelSequence : IEquatable<ChannelSequence>
{
    /// <summary>Slots in a channel sequence (AWDL-10).</summary>
    public const int Slots = 16;

    /// <summary>AWs per slot less one: presence mode 4 (§3.2).</summary>
    private const byte StepCount = 3;

    public SequenceEncoding Encoding { get; }

    // Two raw bytes per slot; Simple uses only the first.
    private readonly byte[] _slots = new byte[Slots * 2];

    private ChannelSequence(SequenceEncoding encoding) => Encoding = encoding;

    /// <summary>No channel in any slot.</summary>
    public static ChannelSequence Empty(SequenceEncoding encoding) => new(encoding);

    /// <summary><paramref name="channel"/> in every slot, in the opclass encoding.</summary>
    public static ChannelSequence Fixed(Channel channel)
    {
        var s = new ChannelSequence(SequenceEncoding.OpClass);
        for (int i = 0; i < Slots; i++) s.SetSlot(i, channel);
        return s;
    }

    /// <summary>A sequence in the opclass encoding from 16 channel numbers (0 = none).</summary>
    public static ChannelSequence FromChannels(ReadOnlySpan<byte> channels)
    {
        if (channels.Length != Slots) throw new ArgumentException("16 slots", nameof(channels));
        var s = new ChannelSequence(SequenceEncoding.OpClass);
        for (int i = 0; i < Slots; i++)
            if (channels[i] != 0) s.SetSlot(i, new Channel(channels[i]));
        return s;
    }

    private void SetSlot(int i, Channel c)
    {
        _slots[i * 2] = c.Number;
        _slots[i * 2 + 1] = c.OpClass;
    }

    /// <summary>The channel number in slot <paramref name="i"/> (modulo 16), 0 for none.</summary>
    public byte ChannelAt(int i)
    {
        int s = (int)((uint)i % Slots) * 2;
        return Encoding == SequenceEncoding.Legacy ? _slots[s + 1] : _slots[s];
    }

    public byte[] Channels()
    {
        var c = new byte[Slots];
        for (int i = 0; i < Slots; i++) c[i] = ChannelAt(i);
        return c;
    }

    /// <summary>How many slots name <paramref name="channel"/>.</summary>
    public int Count(byte channel)
    {
        int n = 0;
        for (int i = 0; i < Slots; i++) if (ChannelAt(i) == channel) n++;
        return n;
    }

    /// <summary>
    /// AWDL-12: this sequence with every slot blanked that doesn't name <paramref name="keep"/>, the one channel a
    /// radio that can't hop is on. What's kept is copied from the peer's bytes. Returns how many were kept.
    /// </summary>
    public (ChannelSequence Sequence, int Kept) Intersect(byte keep)
    {
        var o = new ChannelSequence(Encoding);
        int kept = 0;
        for (int i = 0; i < Slots; i++)
        {
            if (keep != 0 && ChannelAt(i) == keep)
            {
                o._slots[i * 2] = _slots[i * 2];
                o._slots[i * 2 + 1] = _slots[i * 2 + 1];
                kept++;
            }
        }
        return (o, kept);
    }

    /// <summary>The channel most slots name, with <paramref name="prefer"/> winning exact ties; 0 when none.</summary>
    public byte Dominant(byte prefer)
    {
        Span<int> counts = stackalloc int[256];
        for (int i = 0; i < Slots; i++)
        {
            var c = ChannelAt(i);
            if (c != 0) counts[c]++;
        }
        int best = 0;
        for (int c = 1; c < 256; c++) if (counts[c] > counts[best]) best = c;
        if (counts[best] == 0) return 0;
        if (prefer != 0 && counts[prefer] == counts[best]) return prefer;
        return (byte)best;
    }

    /// <summary>
    /// Parses the sequence structure the Channel Sequence TLV (18) carries: count, encoding, duplicate count, step
    /// count, fill channel, then the slots. Only the shape legacy accepted: 16 slots, no duplicates, 4 AWs a slot,
    /// fill 0xffff.
    /// </summary>
    public static ChannelSequence Parse(ReadOnlySpan<byte> value)
    {
        var b = new WireReader(value);
        if (b.U8(0) + 1 != Slots) throw AwdlFormatException.Malformed("channel sequence length");
        var enc = b.U8(1) switch
        {
            0 => SequenceEncoding.Simple,
            1 => SequenceEncoding.Legacy,
            3 => SequenceEncoding.OpClass,
            _ => throw AwdlFormatException.Malformed("channel sequence encoding"),
        };
        if (b.U8(2) != 0) throw AwdlFormatException.Malformed("channel sequence duplicate count");
        if (b.U8(3) != StepCount) throw AwdlFormatException.Malformed("channel sequence step count");
        if (b.Le16(4) != 0xffff) throw AwdlFormatException.Malformed("channel sequence fill channel");
        int size = SlotSize(enc);
        var seq = new ChannelSequence(enc);
        for (int i = 0; i < Slots; i++)
            b.Slice(6 + i * size, size).CopyTo(seq._slots.AsSpan(i * 2));
        return seq;
    }

    private static int SlotSize(SequenceEncoding e) => e == SequenceEncoding.Simple ? 1 : 2;

    /// <summary>Bytes <see cref="Parse"/> reads.</summary>
    public int WireLength => 6 + Slots * SlotSize(Encoding);

    /// <summary>Writes the structure <see cref="Parse"/> reads.</summary>
    internal void Put(List<byte> o)
    {
        o.U8(Slots - 1);
        o.U8((byte)Encoding);
        o.U8(0);
        o.U8(StepCount);
        o.Le16(0xffff);
        int size = SlotSize(Encoding);
        for (int i = 0; i < Slots; i++)
            o.Bytes(_slots.AsSpan(i * 2, size));
    }

    public byte[] ToBytes()
    {
        var o = new List<byte>(WireLength);
        Put(o);
        return [.. o];
    }

    public bool Equals(ChannelSequence? other) =>
        other is not null && Encoding == other.Encoding && _slots.AsSpan().SequenceEqual(other._slots);

    public override bool Equals(object? obj) => Equals(obj as ChannelSequence);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Encoding);
        h.AddBytes(_slots);
        return h.ToHashCode();
    }

    public override string ToString() => $"{Encoding}[{string.Join(",", Channels())}]";
}
