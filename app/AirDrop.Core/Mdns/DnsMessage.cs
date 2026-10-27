using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace AirDrop.Core.Mdns;

public enum DnsType : ushort { A = 1, PTR = 12, TXT = 16, AAAA = 28, SRV = 33, NSEC = 47, ANY = 255 }

public sealed record DnsQuestion(string Name, DnsType Type, bool UnicastResponse = false);

public sealed class DnsRecord
{
    public required string Name { get; init; }
    public required DnsType Type { get; init; }
    public bool CacheFlush { get; init; }
    public uint Ttl { get; init; }

    // Type-specific payloads.
    public IPAddress? Address { get; init; }          // A / AAAA
    public string? Target { get; init; }              // PTR / SRV target
    public ushort Priority { get; init; }             // SRV
    public ushort Weight { get; init; }               // SRV
    public ushort Port { get; init; }                 // SRV
    public IReadOnlyList<string>? Text { get; init; } // TXT strings ("key=value")
    public byte[]? Raw { get; init; }                 // unknown types

    public DnsRecord WithTtl(uint ttl) => new()
    {
        Name = Name, Type = Type, CacheFlush = CacheFlush, Ttl = ttl, Address = Address, Target = Target,
        Priority = Priority, Weight = Weight, Port = Port, Text = Text, Raw = Raw,
    };

    public override string ToString() => $"{Name} {Type} ttl={Ttl} {Address}{Target}{(Type == DnsType.SRV ? ":" + Port : "")}{(Text != null ? string.Join(",", Text) : "")}";
}

/// <summary>Minimal DNS wire-format message (RFC 1035 / RFC 6762 mDNS flavour).</summary>
public sealed class DnsMessage
{
    public ushort Id { get; set; }
    public bool IsResponse { get; set; }
    public bool Authoritative { get; set; }
    public List<DnsQuestion> Questions { get; } = [];
    public List<DnsRecord> Answers { get; } = [];
    public List<DnsRecord> Authorities { get; } = [];
    public List<DnsRecord> Additionals { get; } = [];

    public IEnumerable<DnsRecord> AllRecords => Answers.Concat(Authorities).Concat(Additionals);

    // ---------- encode ----------

    public byte[] Encode()
    {
        var w = new Writer();
        w.U16(Id);
        ushort flags = 0;
        if (IsResponse) flags |= 0x8000;
        if (Authoritative) flags |= 0x0400;
        w.U16(flags);
        w.U16((ushort)Questions.Count);
        w.U16((ushort)Answers.Count);
        w.U16((ushort)Authorities.Count);
        w.U16((ushort)Additionals.Count);
        foreach (var q in Questions)
        {
            w.Name(q.Name);
            w.U16((ushort)q.Type);
            w.U16((ushort)(1 | (q.UnicastResponse ? 0x8000 : 0)));
        }
        foreach (var r in AllRecords) WriteRecord(w, r);
        return w.ToArray();
    }

    private static void WriteRecord(Writer w, DnsRecord r)
    {
        w.Name(r.Name);
        w.U16((ushort)r.Type);
        w.U16((ushort)(1 | (r.CacheFlush ? 0x8000 : 0)));
        w.U32(r.Ttl);
        int lenPos = w.Reserve16();
        int start = w.Length;
        switch (r.Type)
        {
            case DnsType.A:
            case DnsType.AAAA:
                w.Bytes(r.Address!.GetAddressBytes());
                break;
            case DnsType.PTR:
                w.Name(r.Target!);
                break;
            case DnsType.SRV:
                w.U16(r.Priority); w.U16(r.Weight); w.U16(r.Port);
                w.Name(r.Target!);
                break;
            case DnsType.TXT:
                var strings = r.Text is { Count: > 0 } ? r.Text : [""];
                foreach (var s in strings)
                {
                    var b = Encoding.UTF8.GetBytes(s);
                    if (b.Length > 255) throw new ArgumentException("TXT string too long");
                    w.U8((byte)b.Length);
                    w.Bytes(b);
                }
                break;
            default:
                w.Bytes(r.Raw ?? []);
                break;
        }
        w.Patch16(lenPos, (ushort)(w.Length - start));
    }

    private sealed class Writer
    {
        private readonly MemoryStream _ms = new();
        private readonly Dictionary<string, int> _names = new(StringComparer.OrdinalIgnoreCase);
        public int Length => (int)_ms.Length;
        public void U8(byte b) => _ms.WriteByte(b);
        public void U16(ushort v) { _ms.WriteByte((byte)(v >> 8)); _ms.WriteByte((byte)v); }
        public void U32(uint v) { U16((ushort)(v >> 16)); U16((ushort)v); }
        public void Bytes(byte[] b) => _ms.Write(b);
        public int Reserve16() { int p = Length; U16(0); return p; }
        public void Patch16(int pos, ushort v) { var buf = _ms.GetBuffer(); buf[pos] = (byte)(v >> 8); buf[pos + 1] = (byte)v; }
        public byte[] ToArray() => _ms.ToArray();

        /// <summary>Writes a name with suffix compression.</summary>
        public void Name(string name)
        {
            var labels = name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < labels.Length; i++)
            {
                var suffix = string.Join('.', labels, i, labels.Length - i);
                if (_names.TryGetValue(suffix, out int ptr))
                {
                    U16((ushort)(0xC000 | ptr));
                    return;
                }
                if (Length < 0x3FFF) _names[suffix] = Length;
                var b = Encoding.UTF8.GetBytes(labels[i]);
                if (b.Length > 63) throw new ArgumentException("DNS label too long");
                U8((byte)b.Length);
                Bytes(b);
            }
            U8(0);
        }
    }

    // ---------- decode ----------

    public static DnsMessage Decode(ReadOnlySpan<byte> data)
    {
        var d = data.ToArray();
        if (d.Length < 12) throw new FormatException("DNS message too short");
        var m = new DnsMessage
        {
            Id = BinaryPrimitives.ReadUInt16BigEndian(d),
        };
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(2));
        m.IsResponse = (flags & 0x8000) != 0;
        m.Authoritative = (flags & 0x0400) != 0;
        int qd = U16(d, 4), an = U16(d, 6), ns = U16(d, 8), ar = U16(d, 10);
        int pos = 12;
        for (int i = 0; i < qd; i++)
        {
            var name = ReadName(d, ref pos);
            var type = (DnsType)U16(d, pos);
            var cls = U16(d, pos + 2);
            pos += 4;
            m.Questions.Add(new DnsQuestion(name, type, (cls & 0x8000) != 0));
        }
        for (int i = 0; i < an; i++) m.Answers.Add(ReadRecord(d, ref pos));
        for (int i = 0; i < ns; i++) m.Authorities.Add(ReadRecord(d, ref pos));
        for (int i = 0; i < ar; i++) m.Additionals.Add(ReadRecord(d, ref pos));
        return m;
    }

    private static int U16(byte[] d, int pos)
    {
        if (pos + 2 > d.Length) throw new FormatException("DNS message truncated");
        return (d[pos] << 8) | d[pos + 1];
    }

    private static DnsRecord ReadRecord(byte[] d, ref int pos)
    {
        var name = ReadName(d, ref pos);
        var type = (DnsType)U16(d, pos);
        int cls = U16(d, pos + 2);
        if (pos + 10 > d.Length) throw new FormatException("DNS record truncated");
        uint ttl = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(pos + 4));
        int rdlen = U16(d, pos + 8);
        pos += 10;
        int start = pos;
        if (start + rdlen > d.Length) throw new FormatException("DNS rdata truncated");
        pos += rdlen;
        bool flush = (cls & 0x8000) != 0;
        int p = start;
        switch (type)
        {
            case DnsType.A when rdlen == 4:
            case DnsType.AAAA when rdlen == 16:
                return new DnsRecord { Name = name, Type = type, CacheFlush = flush, Ttl = ttl, Address = new IPAddress(d.AsSpan(start, rdlen)) };
            case DnsType.PTR:
                return new DnsRecord { Name = name, Type = type, CacheFlush = flush, Ttl = ttl, Target = ReadName(d, ref p) };
            case DnsType.SRV when rdlen >= 7:
                p += 6;
                return new DnsRecord
                {
                    Name = name, Type = type, CacheFlush = flush, Ttl = ttl,
                    Priority = (ushort)U16(d, start), Weight = (ushort)U16(d, start + 2), Port = (ushort)U16(d, start + 4),
                    Target = ReadName(d, ref p),
                };
            case DnsType.TXT:
            {
                var list = new List<string>();
                while (p < start + rdlen)
                {
                    int len = d[p++];
                    if (p + len > start + rdlen) throw new FormatException("TXT truncated");
                    if (len > 0) list.Add(Encoding.UTF8.GetString(d, p, len));
                    p += len;
                }
                return new DnsRecord { Name = name, Type = type, CacheFlush = flush, Ttl = ttl, Text = list };
            }
            default:
                return new DnsRecord { Name = name, Type = type, CacheFlush = flush, Ttl = ttl, Raw = d.AsSpan(start, rdlen).ToArray() };
        }
    }

    private static string ReadName(byte[] d, ref int pos)
    {
        var labels = new List<string>();
        int p = pos, jumps = 0;
        bool jumped = false;
        while (true)
        {
            if (p >= d.Length) throw new FormatException("DNS name truncated");
            int len = d[p];
            if (len == 0) { p++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (p + 1 >= d.Length || ++jumps > 32) throw new FormatException("Bad DNS compression pointer");
                int target = ((len & 0x3F) << 8) | d[p + 1];
                if (!jumped) pos = p + 2;
                jumped = true;
                p = target;
                continue;
            }
            if ((len & 0xC0) != 0) throw new FormatException("Bad DNS label");
            if (p + 1 + len > d.Length) throw new FormatException("DNS label truncated");
            labels.Add(Encoding.UTF8.GetString(d, p + 1, len));
            p += 1 + len;
        }
        if (!jumped) pos = p;
        return string.Join('.', labels) + ".";
    }

    public static bool NameEquals(string a, string b) =>
        string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
}
