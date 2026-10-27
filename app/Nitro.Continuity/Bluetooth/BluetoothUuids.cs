namespace Nitro.Continuity.Bluetooth;

/// <summary>SDP service-class UUIDs relevant to phone continuity (Bluetooth Assigned Numbers, "Service Class").</summary>
public static class BluetoothUuids
{
    private static readonly Guid Base = new("00000000-0000-1000-8000-00805F9B34FB");

    public static Guid FromShort(uint shortUuid)
    {
        var b = Base.ToByteArray();
        BitConverter.GetBytes(shortUuid).CopyTo(b, 0); // Guid's first field is little-endian in ToByteArray
        return new Guid(b);
    }

    /// <summary>Returns the 16/32-bit alias if the UUID is on the Bluetooth base, else null.</summary>
    public static uint? ToShort(Guid g)
    {
        var b = g.ToByteArray();
        var bb = Base.ToByteArray();
        for (int i = 4; i < 16; i++) if (b[i] != bb[i]) return null;
        return BitConverter.ToUInt32(b, 0);
    }

    public const ushort SerialPort = 0x1101;
    public const ushort HeadsetAg = 0x1112;
    public const ushort Headset = 0x1108;
    public const ushort ObexObjectPush = 0x1105;
    public const ushort HandsFree = 0x111E;
    public const ushort HandsFreeAudioGateway = 0x111F;
    public const ushort PbapPce = 0x112E;
    public const ushort PbapPse = 0x112F;
    public const ushort MapMas = 0x1132;
    public const ushort MapMns = 0x1133;
    public const ushort AudioSource = 0x110A;
    public const ushort AudioSink = 0x110B;
    public const ushort AvrcpTarget = 0x110C;
    public const ushort Avrcp = 0x110E;
    public const ushort Pnp = 0x1200;

    public static string Describe(Guid g) => ToShort(g) switch
    {
        SerialPort => "SPP",
        HeadsetAg => "HSP Audio Gateway",
        Headset => "HSP Headset",
        ObexObjectPush => "OBEX Object Push",
        HandsFree => "HFP Hands-Free (HF)",
        HandsFreeAudioGateway => "HFP Audio Gateway (AG)",
        PbapPce => "PBAP PCE",
        PbapPse => "PBAP PSE (phonebook server)",
        MapMas => "MAP MAS (message server)",
        MapMns => "MAP MNS (notification server)",
        AudioSource => "A2DP Source",
        AudioSink => "A2DP Sink",
        AvrcpTarget => "AVRCP Target",
        Avrcp => "AVRCP",
        Pnp => "PnP Information",
        uint s => $"0x{s:X4}",
        null => g.ToString(),
    };
}
