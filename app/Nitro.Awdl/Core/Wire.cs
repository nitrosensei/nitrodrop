// Nitro.Awdl: C# port of owly-awdl (https://github.com/t4t5/owly, crates/owly-awdl, commit 9a676e1).
// Copyright (c) 2026 T4T5 (Owly, MIT License). Port: Copyright (c) 2026 Nitrobyte. See LICENSE-OWLY.txt.
// Source: crates/owly-awdl/src/wire.rs: bounds-checked reading, and appending to a buffer.

using System.Buffers.Binary;

namespace Nitro.Awdl;

/// <summary>Reads fields at offsets into a byte span, failing rather than reading past its end.</summary>
internal readonly ref struct WireReader(ReadOnlySpan<byte> bytes)
{
    private readonly ReadOnlySpan<byte> _b = bytes;

    public int Length => _b.Length;

    public ReadOnlySpan<byte> Slice(int at, int len)
    {
        if (at < 0 || len < 0 || (long)at + len > _b.Length) throw AwdlFormatException.Short();
        return _b.Slice(at, len);
    }

    public ReadOnlySpan<byte> Rest(int at)
    {
        if (at < 0 || at > _b.Length) throw AwdlFormatException.Short();
        return _b[at..];
    }

    public byte U8(int at) => Slice(at, 1)[0];
    public ushort Le16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(Slice(at, 2));
    public ushort Be16(int at) => BinaryPrimitives.ReadUInt16BigEndian(Slice(at, 2));
    public uint Le32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(at, 4));
    public MacAddr Mac(int at) => new(Slice(at, 6));
}

/// <summary>Appends little-endian (or explicitly big-endian) fields.</summary>
internal static class WireWriter
{
    public static void U8(this List<byte> o, byte v) => o.Add(v);

    public static void Le16(this List<byte> o, ushort v)
    {
        o.Add((byte)v);
        o.Add((byte)(v >> 8));
    }

    public static void Be16(this List<byte> o, ushort v)
    {
        o.Add((byte)(v >> 8));
        o.Add((byte)v);
    }

    public static void Le32(this List<byte> o, uint v)
    {
        o.Add((byte)v);
        o.Add((byte)(v >> 8));
        o.Add((byte)(v >> 16));
        o.Add((byte)(v >> 24));
    }

    public static void Mac(this List<byte> o, MacAddr m)
    {
        for (int i = 0; i < 6; i++) o.Add(m[i]);
    }

    public static void Bytes(this List<byte> o, ReadOnlySpan<byte> b)
    {
        foreach (var x in b) o.Add(x);
    }

    public static void Zeros(this List<byte> o, int n)
    {
        for (int i = 0; i < n; i++) o.Add(0);
    }

    /// <summary>Overwrites a little-endian u16 at <paramref name="at"/>.</summary>
    public static void PatchLe16(this List<byte> o, int at, ushort v)
    {
        o[at] = (byte)v;
        o[at + 1] = (byte)(v >> 8);
    }
}
