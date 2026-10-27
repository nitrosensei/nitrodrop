using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Nitro.Continuity.Ancs;

/// <summary>One 8-byte GATT notification from the Notification Source characteristic.</summary>
public readonly record struct AncsNotificationEvent(
    AncsEventId EventId,
    AncsEventFlags Flags,
    AncsCategoryId Category,
    byte CategoryCount,
    uint NotificationUid)
{
    public const int Size = 8;

    public static AncsNotificationEvent Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
            throw new FormatException($"ANCS Notification Source event must be {Size} bytes, got {data.Length}.");
        return new AncsNotificationEvent(
            (AncsEventId)data[0],
            (AncsEventFlags)data[1],
            (AncsCategoryId)data[2],
            data[3],
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]));
    }

    public byte[] ToBytes()
    {
        var b = new byte[Size];
        b[0] = (byte)EventId; b[1] = (byte)Flags; b[2] = (byte)Category; b[3] = CategoryCount;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), NotificationUid);
        return b;
    }
}

/// <summary>Builders for writes to the Control Point characteristic.</summary>
public static class AncsCommands
{
    /// <summary>CommandID(0) | NotificationUID(4, LE) | { AttributeID(1) [MaxLen(2, LE)] }*</summary>
    public static byte[] GetNotificationAttributes(uint notificationUid, IEnumerable<AncsAttributeRequest> attributes)
    {
        var ms = new MemoryStream();
        ms.WriteByte((byte)AncsCommandId.GetNotificationAttributes);
        Span<byte> u = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(u, notificationUid);
        ms.Write(u);
        foreach (var a in attributes)
        {
            ms.WriteByte((byte)a.Id);
            if (AncsAttributes.NeedsMaxLength(a.Id))
            {
                BinaryPrimitives.WriteUInt16LittleEndian(u, a.MaxLength == 0 ? (ushort)256 : a.MaxLength);
                ms.Write(u[..2]);
            }
        }
        return ms.ToArray();
    }

    /// <summary>CommandID(1) | AppIdentifier (UTF-8, NUL-terminated) | { AttributeID(1) }*</summary>
    public static byte[] GetAppAttributes(string appIdentifier, params AncsAppAttributeId[] attributes)
    {
        var id = Encoding.UTF8.GetBytes(appIdentifier);
        var b = new byte[1 + id.Length + 1 + attributes.Length];
        b[0] = (byte)AncsCommandId.GetAppAttributes;
        id.CopyTo(b, 1);
        b[1 + id.Length] = 0;
        for (int i = 0; i < attributes.Length; i++) b[2 + id.Length + i] = (byte)attributes[i];
        return b;
    }

    /// <summary>CommandID(2) | NotificationUID(4, LE) | ActionID(1). On an IncomingCall notification Positive = answer, Negative = decline.</summary>
    public static byte[] PerformNotificationAction(uint notificationUid, AncsActionId action)
    {
        var b = new byte[6];
        b[0] = (byte)AncsCommandId.PerformNotificationAction;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), notificationUid);
        b[5] = (byte)action;
        return b;
    }
}

public abstract record AncsResponse;

public sealed record AncsNotificationAttributesResponse(uint NotificationUid, IReadOnlyDictionary<AncsNotificationAttributeId, string> Attributes) : AncsResponse
{
    public string? this[AncsNotificationAttributeId id] => Attributes.TryGetValue(id, out var v) ? v : null;
}

public sealed record AncsAppAttributesResponse(string AppIdentifier, IReadOnlyDictionary<AncsAppAttributeId, string> Attributes) : AncsResponse;

/// <summary>
/// Reassembles Data Source notifications into responses. A response can be split over any number of GATT
/// notifications (MTU-sized); the stream carries no length prefix, so — as the spec says — the client must remember which
/// attributes it asked for to know when a response is complete. Requests must be registered in the order they are written
/// to the Control Point (ANCS answers them in order).
/// </summary>
public sealed class AncsDataSourceAssembler
{
    private abstract record Pending(int AttributeCount);
    private sealed record PendingNotif(uint Uid, int AttributeCount) : Pending(AttributeCount);
    private sealed record PendingApp(string AppId, int AttributeCount) : Pending(AttributeCount);

    private readonly Queue<Pending> _pending = new();
    private readonly List<byte> _buf = new();

    public int PendingCount => _pending.Count;

    /// <summary>Call right before writing a GetNotificationAttributes command (same attribute list).</summary>
    public void ExpectNotificationAttributes(uint uid, IReadOnlyCollection<AncsAttributeRequest> attributes) =>
        _pending.Enqueue(new PendingNotif(uid, attributes.Count));

    public void ExpectAppAttributes(string appId, int attributeCount) =>
        _pending.Enqueue(new PendingApp(appId, attributeCount));

    /// <summary>Drop partial state (e.g. after a disconnect or a Control Point write error).</summary>
    public void Reset() { _pending.Clear(); _buf.Clear(); }

    /// <summary>Feeds one GATT notification; returns every response completed by it.</summary>
    public IReadOnlyList<AncsResponse> Feed(ReadOnlySpan<byte> chunk)
    {
        _buf.AddRange(chunk.ToArray());
        var done = new List<AncsResponse>();
        while (_pending.Count > 0 && TryParse(_pending.Peek(), out var resp, out int consumed))
        {
            _pending.Dequeue();
            _buf.RemoveRange(0, consumed);
            done.Add(resp!);
        }
        return done;
    }

    private bool TryParse(Pending p, out AncsResponse? resp, out int consumed)
    {
        resp = null; consumed = 0;
        var s = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_buf);
        if (s.Length < 1) return false;
        int pos;
        switch (p)
        {
            case PendingNotif n:
            {
                if (s[0] != (byte)AncsCommandId.GetNotificationAttributes)
                    throw new FormatException($"Unexpected Data Source command id {s[0]} (expected GetNotificationAttributes).");
                if (s.Length < 5) return false;
                uint uid = BinaryPrimitives.ReadUInt32LittleEndian(s[1..5]);
                pos = 5;
                var attrs = new Dictionary<AncsNotificationAttributeId, string>();
                if (!ReadTuples(s, ref pos, n.AttributeCount, (id, v) => attrs[(AncsNotificationAttributeId)id] = v)) return false;
                resp = new AncsNotificationAttributesResponse(uid, attrs);
                break;
            }
            case PendingApp a:
            {
                if (s[0] != (byte)AncsCommandId.GetAppAttributes)
                    throw new FormatException($"Unexpected Data Source command id {s[0]} (expected GetAppAttributes).");
                int nul = s[1..].IndexOf((byte)0);
                if (nul < 0) return false;
                string appId = Encoding.UTF8.GetString(s.Slice(1, nul));
                pos = 1 + nul + 1;
                var attrs = new Dictionary<AncsAppAttributeId, string>();
                if (!ReadTuples(s, ref pos, a.AttributeCount, (id, v) => attrs[(AncsAppAttributeId)id] = v)) return false;
                resp = new AncsAppAttributesResponse(appId, attrs);
                break;
            }
            default: return false;
        }
        consumed = pos;
        return true;
    }

    private static bool ReadTuples(ReadOnlySpan<byte> s, ref int pos, int count, Action<byte, string> sink)
    {
        int p = pos;
        var parsed = new List<(byte, string)>(count);
        for (int i = 0; i < count; i++)
        {
            if (s.Length < p + 3) return false;
            byte id = s[p];
            int len = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(p + 1, 2));
            if (s.Length < p + 3 + len) return false;
            parsed.Add((id, Encoding.UTF8.GetString(s.Slice(p + 3, len))));
            p += 3 + len;
        }
        foreach (var (id, v) in parsed) sink(id, v);
        pos = p;
        return true;
    }

    /// <summary>ANCS Date attribute: "yyyyMMdd'T'HHmmSS" (phone local time, no zone).</summary>
    public static DateTime? ParseDate(string? value) =>
        value is not null && DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
}
