using System.Buffers.Binary;
using System.Text;

namespace Nitro.Continuity.Obex;

/// <summary>
/// OBEX Application Parameters header body: a sequence of Tag(1) | Length(1) | Value(Length) triplets (big-endian ints).
/// Tag meanings are profile-specific (see <see cref="Map.MapTag"/>).
/// </summary>
public sealed class ObexAppParameters
{
    private readonly List<(byte Tag, byte[] Value)> _items = new();

    public IReadOnlyList<(byte Tag, byte[] Value)> Items => _items;

    public ObexAppParameters Add(byte tag, byte[] value)
    {
        if (value.Length > 255) throw new ArgumentException("App parameter value longer than 255 bytes.");
        _items.Add((tag, value));
        return this;
    }

    public ObexAppParameters AddByte(byte tag, byte v) => Add(tag, [v]);
    public ObexAppParameters AddUInt16(byte tag, ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); return Add(tag, b); }
    public ObexAppParameters AddUInt32(byte tag, uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return Add(tag, b); }
    public ObexAppParameters AddString(byte tag, string v) => Add(tag, Encoding.UTF8.GetBytes(v));

    public byte[]? Get(byte tag) => _items.FirstOrDefault(i => i.Tag == tag).Value;

    public uint? GetUInt(byte tag) => Get(tag) switch
    {
        null => null,
        { Length: 1 } b => b[0],
        { Length: 2 } b => BinaryPrimitives.ReadUInt16BigEndian(b),
        { Length: 4 } b => BinaryPrimitives.ReadUInt32BigEndian(b),
        _ => null,
    };

    public string? GetString(byte tag) => Get(tag) is { } b ? Encoding.UTF8.GetString(b).TrimEnd('\0') : null;

    public byte[] Encode()
    {
        var ms = new MemoryStream();
        foreach (var (t, v) in _items) { ms.WriteByte(t); ms.WriteByte((byte)v.Length); ms.Write(v); }
        return ms.ToArray();
    }

    public ObexHeader ToHeader() => ObexHeader.ByteSeq(ObexHeaderId.AppParameters, Encode());

    public static ObexAppParameters Decode(ReadOnlySpan<byte> d)
    {
        var p = new ObexAppParameters();
        int pos = 0;
        while (pos < d.Length)
        {
            if (pos + 2 > d.Length) throw new FormatException("Truncated application parameter triplet.");
            byte tag = d[pos], len = d[pos + 1];
            if (pos + 2 + len > d.Length) throw new FormatException($"Application parameter 0x{tag:X2} overruns header.");
            p._items.Add((tag, d.Slice(pos + 2, len).ToArray()));
            pos += 2 + len;
        }
        return p;
    }
}
