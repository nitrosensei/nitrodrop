using System.Buffers.Binary;
using System.Text;

namespace Nitro.Continuity.Obex;

// IrDA OBEX 1.5 (as profiled by Bluetooth GOEP). Packet = Opcode/ResponseCode(1) | Length(2, BE, whole packet) |
// [Connect/SetPath extra fields] | Headers*. All integers big-endian. Header ID top two bits select the encoding:
// 00 = null-terminated UTF-16BE text with 2-byte length, 01 = byte sequence with 2-byte length, 10 = 1 byte, 11 = 4 bytes.

public enum ObexOpcode : byte
{
    Connect = 0x80,
    Disconnect = 0x81,
    Put = 0x02,
    PutFinal = 0x82,
    Get = 0x03,
    GetFinal = 0x83,
    SetPath = 0x85,
    Abort = 0xFF,
}

public enum ObexResponseCode : byte
{
    Continue = 0x90,
    Success = 0xA0,
    Created = 0xA1,
    Accepted = 0xA2,
    BadRequest = 0xC0,
    Unauthorized = 0xC1,
    Forbidden = 0xC3,
    NotFound = 0xC4,
    NotAcceptable = 0xC6,
    PreconditionFailed = 0xCC,
    InternalServerError = 0xD0,
    NotImplemented = 0xD1,
    ServiceUnavailable = 0xD3,
}

public enum ObexHeaderId : byte
{
    Count = 0xC0,
    Name = 0x01,
    Type = 0x42,
    Length = 0xC3,
    Time = 0x44,
    Description = 0x05,
    Target = 0x46,
    Http = 0x47,
    Body = 0x48,
    EndOfBody = 0x49,
    Who = 0x4A,
    ConnectionId = 0xCB,
    AppParameters = 0x4C,
    AuthChallenge = 0x4D,
    AuthResponse = 0x4E,
    /// <summary>GOEP 2.0 Single Response Mode (OBEX over L2CAP).</summary>
    SingleResponseMode = 0x97,
    SingleResponseModeParameters = 0x98,
}

public enum ObexHeaderEncoding { Unicode = 0, Bytes = 1, Byte1 = 2, Int4 = 3 }

public sealed class ObexHeader
{
    public ObexHeaderId Id { get; }
    public byte[]? Bytes { get; }
    public string? Text { get; }
    public uint Value { get; }

    public ObexHeaderEncoding Encoding => (ObexHeaderEncoding)((byte)Id >> 6);

    private ObexHeader(ObexHeaderId id, byte[]? bytes, string? text, uint value) { Id = id; Bytes = bytes; Text = text; Value = value; }

    public static ObexHeader Unicode(ObexHeaderId id, string text) { Check(id, ObexHeaderEncoding.Unicode); return new(id, null, text, 0); }
    public static ObexHeader ByteSeq(ObexHeaderId id, byte[] data) { Check(id, ObexHeaderEncoding.Bytes); return new(id, data, null, 0); }
    public static ObexHeader UInt8(ObexHeaderId id, byte v) { Check(id, ObexHeaderEncoding.Byte1); return new(id, null, null, v); }
    public static ObexHeader UInt32(ObexHeaderId id, uint v) { Check(id, ObexHeaderEncoding.Int4); return new(id, null, null, v); }

    /// <summary>Type header is a byte sequence holding null-terminated ASCII (e.g. "x-bt/MAP-msg-listing\0").</summary>
    public static ObexHeader Type(string mime) => ByteSeq(ObexHeaderId.Type, [.. System.Text.Encoding.ASCII.GetBytes(mime), 0]);
    public static ObexHeader Name(string name) => Unicode(ObexHeaderId.Name, name);
    public static ObexHeader Target(Guid uuid) => ByteSeq(ObexHeaderId.Target, UuidBytes(uuid));
    public static ObexHeader Who(Guid uuid) => ByteSeq(ObexHeaderId.Who, UuidBytes(uuid));
    public static ObexHeader ConnectionId(uint id) => UInt32(ObexHeaderId.ConnectionId, id);

    /// <summary>Big-endian (RFC 4122 network order) 16 bytes, as OBEX Target/Who carry them.</summary>
    public static byte[] UuidBytes(Guid g)
    {
        var b = new byte[16];
        g.TryWriteBytes(b, bigEndian: true, out _);
        return b;
    }

    public string TypeString => Bytes is null ? "" : System.Text.Encoding.ASCII.GetString(Bytes).TrimEnd('\0');

    private static void Check(ObexHeaderId id, ObexHeaderEncoding e)
    {
        if ((ObexHeaderEncoding)((byte)id >> 6) != e) throw new ArgumentException($"Header {id} (0x{(byte)id:X2}) is not {e}-encoded.");
    }

    public int EncodedLength => Encoding switch
    {
        ObexHeaderEncoding.Unicode => 3 + (Text!.Length == 0 ? 0 : (Text.Length + 1) * 2),
        ObexHeaderEncoding.Bytes => 3 + Bytes!.Length,
        ObexHeaderEncoding.Byte1 => 2,
        _ => 5,
    };

    internal void WriteTo(Stream s)
    {
        s.WriteByte((byte)Id);
        Span<byte> tmp = stackalloc byte[4];
        switch (Encoding)
        {
            case ObexHeaderEncoding.Unicode:
                // Empty Name is sent as just HI + length 3 (no terminator) — e.g. SetPath to root / "current folder".
                byte[] t = Text!.Length == 0 ? [] : System.Text.Encoding.BigEndianUnicode.GetBytes(Text + "\0");
                BinaryPrimitives.WriteUInt16BigEndian(tmp, (ushort)(3 + t.Length)); s.Write(tmp[..2]); s.Write(t);
                break;
            case ObexHeaderEncoding.Bytes:
                BinaryPrimitives.WriteUInt16BigEndian(tmp, (ushort)(3 + Bytes!.Length)); s.Write(tmp[..2]); s.Write(Bytes);
                break;
            case ObexHeaderEncoding.Byte1:
                s.WriteByte((byte)Value);
                break;
            default:
                BinaryPrimitives.WriteUInt32BigEndian(tmp, Value); s.Write(tmp);
                break;
        }
    }

    internal static ObexHeader Read(ReadOnlySpan<byte> d, ref int pos)
    {
        var id = (ObexHeaderId)d[pos];
        var enc = (ObexHeaderEncoding)(d[pos] >> 6);
        switch (enc)
        {
            case ObexHeaderEncoding.Unicode:
            case ObexHeaderEncoding.Bytes:
            {
                Need(d, pos, 3);
                int len = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(pos + 1, 2));
                if (len < 3) throw new FormatException($"OBEX header 0x{(byte)id:X2} length {len} < 3.");
                Need(d, pos, len);
                var body = d.Slice(pos + 3, len - 3);
                pos += len;
                if (enc == ObexHeaderEncoding.Bytes) return new(id, body.ToArray(), null, 0);
                var text = System.Text.Encoding.BigEndianUnicode.GetString(body);
                return new(id, null, text.TrimEnd('\0'), 0);
            }
            case ObexHeaderEncoding.Byte1:
                Need(d, pos, 2);
                pos += 2;
                return new(id, null, null, d[pos - 1]);
            default:
                Need(d, pos, 5);
                uint v = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(pos + 1, 4));
                pos += 5;
                return new(id, null, null, v);
        }
    }

    private static void Need(ReadOnlySpan<byte> d, int pos, int n)
    {
        if (d.Length < pos + n) throw new FormatException("Truncated OBEX header.");
    }

    public override string ToString() => Encoding switch
    {
        ObexHeaderEncoding.Unicode => $"{Id}=\"{Text}\"",
        ObexHeaderEncoding.Bytes => Id == ObexHeaderId.Type ? $"{Id}={TypeString}" : $"{Id}[{Bytes!.Length}]",
        _ => $"{Id}={Value}",
    };
}

/// <summary>An OBEX request or response packet.</summary>
public sealed class ObexPacket
{
    public const int MinimumMaxPacketLength = 255;

    /// <summary>Request opcode (incl. final bit) or response code (incl. final bit 0x80).</summary>
    public byte Code { get; set; }
    public List<ObexHeader> Headers { get; } = new();

    // Connect request/response: Version(1) Flags(1) MaxPacketLength(2)
    public bool IsConnect { get; init; }
    public byte ObexVersion { get; set; } = 0x10;
    public byte ConnectFlags { get; set; }
    public ushort MaxPacketLength { get; set; } = 0xFFFF;

    // SetPath request: Flags(1) Constants(1). Flags bit0 = backup ("..") , bit1 = don't create.
    public bool IsSetPath { get; init; }
    public byte SetPathFlags { get; set; }

    public ObexResponseCode Response => (ObexResponseCode)(Code | 0x80);
    public bool IsFinal => (Code & 0x80) != 0;

    public ObexHeader? Find(ObexHeaderId id) => Headers.FirstOrDefault(h => h.Id == id);

    public ObexPacket Add(ObexHeader h) { Headers.Add(h); return this; }

    public static ObexPacket Connect(Guid? target = null, ushort maxPacketLength = 0xFFFF)
    {
        var p = new ObexPacket { Code = (byte)ObexOpcode.Connect, IsConnect = true, MaxPacketLength = maxPacketLength };
        if (target is { } t) p.Add(ObexHeader.Target(t));
        return p;
    }

    /// <summary>SetPath: name=null+backup=false → root; name="" → root too (empty Name); backup=true → parent.</summary>
    public static ObexPacket SetPath(string? name, bool backup = false, bool dontCreate = true)
    {
        var p = new ObexPacket { Code = (byte)ObexOpcode.SetPath, IsSetPath = true, SetPathFlags = (byte)((backup ? 1 : 0) | (dontCreate ? 2 : 0)) };
        if (name is not null) p.Add(ObexHeader.Name(name));
        return p;
    }

    public byte[] Encode()
    {
        var ms = new MemoryStream();
        ms.WriteByte(Code);
        ms.WriteByte(0); ms.WriteByte(0);
        if (IsConnect)
        {
            ms.WriteByte(ObexVersion); ms.WriteByte(ConnectFlags);
            ms.WriteByte((byte)(MaxPacketLength >> 8)); ms.WriteByte((byte)MaxPacketLength);
        }
        else if (IsSetPath)
        {
            ms.WriteByte(SetPathFlags); ms.WriteByte(0);
        }
        foreach (var h in Headers) h.WriteTo(ms);
        var b = ms.ToArray();
        if (b.Length > 0xFFFF) throw new InvalidOperationException("OBEX packet exceeds 65535 bytes; split the body.");
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(1), (ushort)b.Length);
        return b;
    }

    /// <summary>
    /// Decodes one complete packet. <paramref name="isConnectResponse"/> must be set when decoding the reply to a Connect
    /// (response packets don't say what request they answer). Requests (Connect/SetPath opcodes) are detected automatically.
    /// </summary>
    public static ObexPacket Decode(ReadOnlySpan<byte> d, bool isConnectResponse = false)
    {
        if (d.Length < 3) throw new FormatException("OBEX packet shorter than 3 bytes.");
        int len = BinaryPrimitives.ReadUInt16BigEndian(d[1..3]);
        if (len < 3 || len > d.Length) throw new FormatException($"OBEX packet length {len} invalid for buffer of {d.Length}.");
        d = d[..len];
        byte code = d[0];
        bool connect = isConnectResponse || code == (byte)ObexOpcode.Connect;
        bool setPath = !isConnectResponse && code == (byte)ObexOpcode.SetPath;
        var p = new ObexPacket { Code = code, IsConnect = connect, IsSetPath = setPath };
        int pos = 3;
        if (connect)
        {
            if (len < 7) throw new FormatException("OBEX Connect packet too short.");
            p.ObexVersion = d[3]; p.ConnectFlags = d[4];
            p.MaxPacketLength = BinaryPrimitives.ReadUInt16BigEndian(d[5..7]);
            pos = 7;
        }
        else if (setPath)
        {
            if (len < 5) throw new FormatException("OBEX SetPath packet too short.");
            p.SetPathFlags = d[3];
            pos = 5;
        }
        while (pos < len) p.Headers.Add(ObexHeader.Read(d, ref pos));
        return p;
    }

    /// <summary>Returns the total length of the first packet in <paramref name="buffer"/> once its 3-byte prefix is present, else 0.</summary>
    public static int PeekLength(ReadOnlySpan<byte> buffer) =>
        buffer.Length < 3 ? 0 : BinaryPrimitives.ReadUInt16BigEndian(buffer[1..3]);

    /// <summary>Reads exactly one packet from a stream (RFCOMM socket stream). Returns raw bytes.</summary>
    public static async Task<byte[]> ReadRawAsync(Stream s, CancellationToken ct = default)
    {
        var head = new byte[3];
        await s.ReadExactlyAsync(head, ct).ConfigureAwait(false);
        int len = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(1));
        if (len < 3) throw new FormatException($"OBEX length {len} < 3.");
        var all = new byte[len];
        head.CopyTo(all, 0);
        await s.ReadExactlyAsync(all.AsMemory(3), ct).ConfigureAwait(false);
        return all;
    }

    public override string ToString() => $"0x{Code:X2} [{string.Join(", ", Headers)}]";
}
