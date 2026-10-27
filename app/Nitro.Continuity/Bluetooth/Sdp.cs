using System.Buffers.Binary;
using System.Text;

namespace Nitro.Continuity.Bluetooth;

/// <summary>
/// SDP data elements (Bluetooth Core Vol 3 Part B §3) for RfcommServiceProvider.SdpRawAttributes. Windows writes
/// ServiceClassIdList (0x0001) and ProtocolDescriptorList (0x0004) itself; we add the rest.
/// </summary>
public static class Sdp
{
    public const ushort AttrServiceName = 0x0100;                 // primary language base 0x0100 + 0
    public const ushort AttrBluetoothProfileDescriptorList = 0x0009;
    public const ushort AttrMapSupportedFeatures = 0x0317;

    public static byte[] UInt16(ushort v) { var b = new byte[3]; b[0] = 0x09; BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(1), v); return b; }
    public static byte[] UInt32(uint v) { var b = new byte[5]; b[0] = 0x0A; BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(1), v); return b; }
    public static byte[] Uuid16(ushort v) { var b = new byte[3]; b[0] = 0x19; BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(1), v); return b; }

    public static byte[] Text(string s)
    {
        var t = Encoding.UTF8.GetBytes(s);
        if (t.Length > 255) throw new ArgumentException("SDP text longer than 255 bytes.");
        return [0x25, (byte)t.Length, .. t];
    }

    public static byte[] Sequence(params byte[][] items)
    {
        var body = items.SelectMany(i => i).ToArray();
        if (body.Length > 255) throw new ArgumentException("SDP sequence longer than 255 bytes.");
        return [0x35, (byte)body.Length, .. body];
    }

    /// <summary>
    /// Extra SDP attributes for our MAP MNS record (MAP 1.4 §7.1.2): ServiceName, profile descriptor (0x1134 v1.4) and
    /// MapSupportedFeatures. Features 0x7F = notification registration, notification, browsing, uploading, delete,
    /// instance information, extended event report 1.1 (UNVERIFIED which bits iOS looks at; 1.1 events carry sender/subject).
    /// </summary>
    public static IReadOnlyDictionary<uint, byte[]> MapMnsAttributes(string serviceName = "MAP MNS-NitroDrop", uint features = 0x0000007F) =>
        new Dictionary<uint, byte[]>
        {
            [AttrServiceName] = Text(serviceName),
            [AttrBluetoothProfileDescriptorList] = Sequence(Sequence(Uuid16(0x1134), UInt16(0x0104))),
            [AttrMapSupportedFeatures] = UInt32(features),
        };
}
