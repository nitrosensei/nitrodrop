using System.Buffers.Binary;
using System.Text;

namespace AirDrop.Core.Plist;

/// <summary>
/// Binary property list (bplist00) reader/writer.
/// Object model: Dictionary&lt;string, object&gt;, List&lt;object&gt;, string, long, double, bool, byte[], DateTime.
/// Integers of any CLR width are accepted on write; ints are always read back as long.
/// </summary>
public static class BinaryPlist
{
    private static readonly byte[] Magic = "bplist00"u8.ToArray();
    private static readonly DateTime AppleEpoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const int MaxObjects = 1_000_000;
    private const int MaxDepth = 64;

    public static bool IsBinaryPlist(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && data[..8].SequenceEqual(Magic);

    // ---------------- Reader ----------------

    public static object Read(ReadOnlySpan<byte> data) => new Reader(data.ToArray()).ReadRoot();

    public static Dictionary<string, object> ReadDictionary(ReadOnlySpan<byte> data) =>
        Read(data) as Dictionary<string, object>
        ?? throw new PlistException("Root object is not a dictionary");

    private sealed class Reader
    {
        private readonly byte[] _d;
        private int _offsetSize, _refSize;
        private long[] _offsets = [];
        private readonly HashSet<long> _inProgress = [];

        public Reader(byte[] d) => _d = d;

        public object ReadRoot()
        {
            if (_d.Length < 8 + 32 || !IsBinaryPlist(_d))
                throw new PlistException("Not a bplist00 document");
            var t = _d.AsSpan(_d.Length - 32);
            _offsetSize = t[6];
            _refSize = t[7];
            long numObjects = (long)BinaryPrimitives.ReadUInt64BigEndian(t[8..]);
            long topObject = (long)BinaryPrimitives.ReadUInt64BigEndian(t[16..]);
            long tableOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(t[24..]);
            if (_offsetSize is < 1 or > 8 || _refSize is < 1 or > 8)
                throw new PlistException("Invalid trailer sizes");
            if (numObjects < 1 || numObjects > MaxObjects || topObject >= numObjects)
                throw new PlistException("Invalid object count");
            if (tableOffset < 8 || tableOffset + numObjects * _offsetSize > _d.Length - 32)
                throw new PlistException("Invalid offset table");
            _offsets = new long[numObjects];
            for (int i = 0; i < numObjects; i++)
            {
                _offsets[i] = (long)ReadUInt(tableOffset + (long)i * _offsetSize, _offsetSize);
                if (_offsets[i] < 8 || _offsets[i] >= tableOffset)
                    throw new PlistException("Object offset out of range");
            }
            return ReadObject(topObject, 0);
        }

        private ulong ReadUInt(long pos, int size)
        {
            if (pos < 0 || pos + size > _d.Length) throw new PlistException("Read past end");
            ulong v = 0;
            for (int i = 0; i < size; i++) v = (v << 8) | _d[pos + i];
            return v;
        }

        private object ReadObject(long index, int depth)
        {
            if (index < 0 || index >= _offsets.Length) throw new PlistException("Bad object ref");
            if (depth > MaxDepth) throw new PlistException("Nesting too deep");
            long pos = _offsets[index];
            byte marker = _d[pos];
            int type = marker >> 4, info = marker & 0x0F;
            switch (type)
            {
                case 0x0:
                    return info switch
                    {
                        0x8 => false,
                        0x9 => true,
                        _ => throw new PlistException($"Unsupported simple marker 0x{marker:x2}"),
                    };
                case 0x1:
                {
                    int n = 1 << info;
                    if (n == 16) // 128-bit int: only lower 64 bits supported
                        return (long)ReadUInt(pos + 1 + 8, 8);
                    if (n > 8) throw new PlistException("Int too large");
                    ulong u = ReadUInt(pos + 1, n);
                    return (long)u; // 1,2,4 bytes are unsigned; 8 bytes is signed
                }
                case 0x2:
                {
                    int n = 1 << info;
                    if (n == 4) return (double)BinaryPrimitives.ReadSingleBigEndian(Slice(pos + 1, 4));
                    if (n == 8) return BinaryPrimitives.ReadDoubleBigEndian(Slice(pos + 1, 8));
                    throw new PlistException("Bad real size");
                }
                case 0x3:
                    if (marker != 0x33) throw new PlistException("Bad date marker");
                    return AppleEpoch.AddSeconds(BinaryPrimitives.ReadDoubleBigEndian(Slice(pos + 1, 8)));
                case 0x4:
                {
                    var (len, start) = ReadLength(pos, info);
                    return Slice(start, len).ToArray();
                }
                case 0x5:
                {
                    var (len, start) = ReadLength(pos, info);
                    return Encoding.ASCII.GetString(Slice(start, len));
                }
                case 0x6:
                {
                    var (len, start) = ReadLength(pos, info);
                    return Encoding.BigEndianUnicode.GetString(Slice(start, checked(len * 2)));
                }
                case 0x8: // UID: surface as long
                    return (long)ReadUInt(pos + 1, info + 1);
                case 0xA:
                {
                    var (len, start) = ReadLength(pos, info);
                    var list = new List<object>(Math.Min(len, 1024));
                    Enter(index);
                    for (int i = 0; i < len; i++)
                        list.Add(ReadObject((long)ReadUInt(start + (long)i * _refSize, _refSize), depth + 1));
                    _inProgress.Remove(index);
                    return list;
                }
                case 0xD:
                {
                    var (len, start) = ReadLength(pos, info);
                    var dict = new Dictionary<string, object>(Math.Min(len, 1024));
                    Enter(index);
                    for (int i = 0; i < len; i++)
                    {
                        long kref = (long)ReadUInt(start + (long)i * _refSize, _refSize);
                        long vref = (long)ReadUInt(start + (long)(len + i) * _refSize, _refSize);
                        if (ReadObject(kref, depth + 1) is not string key)
                            throw new PlistException("Dictionary key is not a string");
                        dict[key] = ReadObject(vref, depth + 1);
                    }
                    _inProgress.Remove(index);
                    return dict;
                }
                default:
                    throw new PlistException($"Unsupported object marker 0x{marker:x2}");
            }
        }

        private void Enter(long index)
        {
            if (!_inProgress.Add(index)) throw new PlistException("Cyclic reference");
        }

        private (int len, long start) ReadLength(long pos, int info)
        {
            if (info != 0xF) return (info, pos + 1);
            byte m = _d[pos + 1];
            if (m >> 4 != 0x1) throw new PlistException("Bad length marker");
            int n = 1 << (m & 0xF);
            ulong len = ReadUInt(pos + 2, n);
            if (len > int.MaxValue) throw new PlistException("Length too large");
            return ((int)len, pos + 2 + n);
        }

        private ReadOnlySpan<byte> Slice(long start, int len)
        {
            if (start < 0 || len < 0 || start + len > _d.Length) throw new PlistException("Read past end");
            return _d.AsSpan((int)start, len);
        }
    }

    // ---------------- Writer ----------------

    public static byte[] Write(object root)
    {
        var objects = new List<object>();
        var refs = new List<int[]>(); // child refs per container object (null for scalars)
        Flatten(root, objects, refs, 0);

        int numObjects = objects.Count;
        int refSize = numObjects <= 0xFF ? 1 : numObjects <= 0xFFFF ? 2 : 4;

        using var ms = new MemoryStream();
        ms.Write(Magic);
        var offsets = new long[numObjects];
        for (int i = 0; i < numObjects; i++)
        {
            offsets[i] = ms.Position;
            WriteObject(ms, objects[i], refs[i], refSize);
        }
        long tableOffset = ms.Position;
        int offsetSize = tableOffset <= 0xFF ? 1 : tableOffset <= 0xFFFF ? 2 : tableOffset <= 0xFFFFFFFF ? 4 : 8;
        foreach (var off in offsets) WriteUIntBE(ms, (ulong)off, offsetSize);

        Span<byte> trailer = stackalloc byte[32];
        trailer.Clear();
        trailer[6] = (byte)offsetSize;
        trailer[7] = (byte)refSize;
        BinaryPrimitives.WriteUInt64BigEndian(trailer[8..], (ulong)numObjects);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[16..], 0);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[24..], (ulong)tableOffset);
        ms.Write(trailer);
        return ms.ToArray();
    }

    private static int Flatten(object value, List<object> objects, List<int[]> refs, int depth)
    {
        if (depth > MaxDepth) throw new PlistException("Nesting too deep");
        int index = objects.Count;
        objects.Add(value);
        refs.Add(null!);
        switch (value)
        {
            case IDictionary<string, object> dict:
            {
                var r = new int[dict.Count * 2];
                int i = 0;
                var values = new List<object>(dict.Count);
                foreach (var kv in dict)
                {
                    r[i++] = Flatten(kv.Key, objects, refs, depth + 1);
                    values.Add(kv.Value);
                }
                foreach (var v in values) r[i++] = Flatten(v, objects, refs, depth + 1);
                refs[index] = r;
                break;
            }
            case string or byte[]:
                break;
            case System.Collections.IEnumerable list:
            {
                var r = new List<int>();
                foreach (var item in list) r.Add(Flatten(item ?? throw new PlistException("null in array"), objects, refs, depth + 1));
                refs[index] = r.ToArray();
                break;
            }
        }
        return index;
    }

    private static void WriteObject(Stream s, object value, int[]? childRefs, int refSize)
    {
        switch (value)
        {
            case bool b:
                s.WriteByte(b ? (byte)0x09 : (byte)0x08);
                break;
            case byte or sbyte or short or ushort or int or uint or long:
                WriteInt(s, Convert.ToInt64(value));
                break;
            case ulong ul:
                if (ul > long.MaxValue) throw new PlistException("ulong > long.MaxValue not supported");
                WriteInt(s, (long)ul);
                break;
            case float or double or decimal:
            {
                s.WriteByte(0x23);
                Span<byte> b = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(b, Convert.ToDouble(value));
                s.Write(b);
                break;
            }
            case DateTime dt:
            {
                s.WriteByte(0x33);
                Span<byte> b = stackalloc byte[8];
                var utc = dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt;
                BinaryPrimitives.WriteDoubleBigEndian(b, (utc - AppleEpoch).TotalSeconds);
                s.Write(b);
                break;
            }
            case DateTimeOffset dto:
                WriteObject(s, dto.UtcDateTime, null, refSize);
                break;
            case byte[] data:
                WriteMarker(s, 0x4, data.Length);
                s.Write(data);
                break;
            case string str:
                if (str.All(c => c < 0x80))
                {
                    WriteMarker(s, 0x5, str.Length);
                    s.Write(Encoding.ASCII.GetBytes(str));
                }
                else
                {
                    var bytes = Encoding.BigEndianUnicode.GetBytes(str);
                    WriteMarker(s, 0x6, bytes.Length / 2);
                    s.Write(bytes);
                }
                break;
            case IDictionary<string, object>:
                WriteMarker(s, 0xD, childRefs!.Length / 2);
                foreach (var r in childRefs) WriteUIntBE(s, (ulong)r, refSize);
                break;
            case System.Collections.IEnumerable:
                WriteMarker(s, 0xA, childRefs!.Length);
                foreach (var r in childRefs) WriteUIntBE(s, (ulong)r, refSize);
                break;
            default:
                throw new PlistException($"Unsupported plist type {value.GetType()}");
        }
    }

    private static void WriteInt(Stream s, long v)
    {
        if (v >= 0 && v <= 0xFF) { s.WriteByte(0x10); WriteUIntBE(s, (ulong)v, 1); }
        else if (v >= 0 && v <= 0xFFFF) { s.WriteByte(0x11); WriteUIntBE(s, (ulong)v, 2); }
        else if (v >= 0 && v <= 0xFFFFFFFFL) { s.WriteByte(0x12); WriteUIntBE(s, (ulong)v, 4); }
        else { s.WriteByte(0x13); WriteUIntBE(s, (ulong)v, 8); } // negatives always 8 bytes
    }

    private static void WriteMarker(Stream s, int type, int length)
    {
        if (length < 0xF) { s.WriteByte((byte)((type << 4) | length)); return; }
        s.WriteByte((byte)((type << 4) | 0xF));
        WriteInt(s, length);
    }

    private static void WriteUIntBE(Stream s, ulong v, int size)
    {
        for (int i = size - 1; i >= 0; i--) s.WriteByte((byte)(v >> (8 * i)));
    }
}

public sealed class PlistException(string message) : Exception(message);
