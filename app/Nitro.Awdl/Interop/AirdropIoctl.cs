// Nitro.Awdl: C# binding of driver/include/airdrop_ioctl.h (ABI v2; v1 replies tolerated).
// Copyright (c) 2026 Nitrobyte. Layouts are (de)serialized field by field with explicit offsets, so they don't depend
// on CLR struct layout; AwdlIoctlTests pins every offset and size against the header's static_asserts.

using System.Buffers.Binary;
using System.Text;

namespace Nitro.Awdl.Interop;

/// <summary>Constants and codes from airdrop_ioctl.h.</summary>
public static class AirdropIoctl
{
    /// <summary>GUID_DEVINTERFACE_AIRDROP_WIFI {6c1b3e0a-9d42-4f5e-8a71-2f3c4b5d6e7f}.</summary>
    public static readonly Guid DeviceInterface = new(0x6c1b3e0a, 0x9d42, 0x4f5e, 0x8a, 0x71, 0x2f, 0x3c, 0x4b, 0x5d, 0x6e, 0x7f);

    public const uint AbiVersion = 2;
    public const uint FileDeviceAirdrop = 0x8a57;

    private const uint MethodBuffered = 0, MethodOutDirect = 2;
    private const uint FileReadAccess = 1, FileWriteAccess = 2;

    /// <summary>winioctl.h CTL_CODE.</summary>
    public static uint CtlCode(uint deviceType, uint function, uint method, uint access) =>
        (deviceType << 16) | (access << 14) | (function << 2) | method;

    public static readonly uint GetInfo = CtlCode(FileDeviceAirdrop, 0x800, MethodBuffered, FileReadAccess);
    public static readonly uint Start = CtlCode(FileDeviceAirdrop, 0x801, MethodBuffered, FileWriteAccess);
    public static readonly uint Stop = CtlCode(FileDeviceAirdrop, 0x802, MethodBuffered, FileWriteAccess);
    public static readonly uint ReadFrame = CtlCode(FileDeviceAirdrop, 0x803, MethodOutDirect, FileReadAccess);
    public static readonly uint InjectFrame = CtlCode(FileDeviceAirdrop, 0x804, MethodBuffered, FileWriteAccess);

    public const uint StateDown = 0, StateFwReady = 1, StateRadioOn = 2, StateError = 3;
    public const byte RadioModeOff = 0, RadioModeListener = 1, RadioModeIbss = 2;

    public const uint StartListenerOnly = 0x1, StartKeepBadFcs = 0x2, StartNoPromisc = 0x4;

    public const uint RxShortPreamble = 0x0001, RxTsfValid = 0x0002, RxTruncated = 0x0004, RxFcsStripped = 0x0008,
        RxBadFcs = 0x0010, RxAmpdu = 0x0020, RxSgi = 0x0040, RxLdpc = 0x0080, RxStbc = 0x0100, RxAmsdu = 0x0200;

    public const uint TxNoAck = 0x1, TxRateHt = 0x2, TxAssignSeq = 0x4, TxSgi = 0x8, TxHighPri = 0x10;
    public const int TxMaxFrame = 2346;

    public const uint TxStSuccess = 0, TxStFailed = 1, TxStFlushed = 2, TxStNoStatus = 3;
    public const uint TxrNoAckNotHonored = 0x1, TxrAckPolicySet = 0x2, TxrSeqAssigned = 0x4;

    public const int InfoSize = 176, InfoV1Size = 144;
    public const int StartParamsSize = 24, StartParamsV1Size = 16;
    public const int RxHeaderSize = 64, RxHeaderV1Size = 48;
    public const int TxHeaderSize = 16;
    public const int TxResultSize = 24;
    /// <summary>Recommended READ_FRAME buffer: header + largest RX buffer (4 KiB).</summary>
    public const int RxBufferSize = RxHeaderSize + 4096;
    /// <summary>The driver refuses with STATUS_DEVICE_BUSY past this many frames in flight.</summary>
    public const int MaxTxInFlight = 64;
}

/// <summary>AIRDROP_INFO.</summary>
public sealed record AirdropInfo
{
    public uint Size { get; init; }
    public uint AbiVersion { get; init; }
    public uint State { get; init; }
    public int InitStatus { get; init; }
    public MacAddr MacAddress { get; init; }
    public bool RfKill { get; init; }
    public bool Msix { get; init; }
    public string FirmwareVersion { get; init; } = "";
    public uint HwRev { get; init; }
    public uint HwRfId { get; init; }
    public uint LastFirmwareError { get; init; }
    public uint RxFrames { get; init; }
    public uint RxDropped { get; init; }
    public byte[] Channels { get; init; } = [];
    // v2
    public uint RxBadFcs { get; init; }
    public uint TxSubmitted { get; init; }
    public uint TxSuccess { get; init; }
    public uint TxFailed { get; init; }
    public uint TxFlushed { get; init; }
    public uint TxPending { get; init; }
    public byte RadioChannel { get; init; }
    public byte RadioMode { get; init; }

    /// <summary>Parses a GET_INFO reply (v1 prefix of 144 bytes or the full v2 176).</summary>
    public static AirdropInfo Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < AirdropIoctl.InfoV1Size) throw new InvalidDataException($"AIRDROP_INFO too short: {b.Length}");
        uint count = Math.Min(U32(b, 76), 64);
        var fw = b.Slice(24, 32);
        int nul = fw.IndexOf((byte)0);
        bool v2 = b.Length >= AirdropIoctl.InfoSize && U32(b, 0) >= AirdropIoctl.InfoSize;
        return new AirdropInfo
        {
            Size = U32(b, 0),
            AbiVersion = U32(b, 4),
            State = U32(b, 8),
            InitStatus = (int)U32(b, 12),
            MacAddress = new MacAddr(b.Slice(16, 6)),
            RfKill = b[22] != 0,
            Msix = b[23] != 0,
            FirmwareVersion = Encoding.ASCII.GetString(nul >= 0 ? fw[..nul] : fw),
            HwRev = U32(b, 56),
            HwRfId = U32(b, 60),
            LastFirmwareError = U32(b, 64),
            RxFrames = U32(b, 68),
            RxDropped = U32(b, 72),
            Channels = b.Slice(80, (int)count).ToArray(),
            RxBadFcs = v2 ? U32(b, 144) : 0,
            TxSubmitted = v2 ? U32(b, 148) : 0,
            TxSuccess = v2 ? U32(b, 152) : 0,
            TxFailed = v2 ? U32(b, 156) : 0,
            TxFlushed = v2 ? U32(b, 160) : 0,
            TxPending = v2 ? U32(b, 164) : 0,
            RadioChannel = v2 ? b[168] : (byte)0,
            RadioMode = v2 ? b[169] : (byte)0,
        };
    }

    private static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
}

/// <summary>AIRDROP_START_PARAMS (v2, 24 bytes).</summary>
public readonly record struct AirdropStartParams(uint Flags, byte Channel, MacAddr Bssid, ushort BeaconIntervalTu = 0)
{
    public byte[] ToBytes()
    {
        var b = new byte[AirdropIoctl.StartParamsSize];
        BinaryPrimitives.WriteUInt32LittleEndian(b, AirdropIoctl.StartParamsSize);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), Flags);
        b[8] = Channel;
        Bssid.CopyTo(b.AsSpan(9));
        b[15] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16), BeaconIntervalTu);
        return b;
    }
}

/// <summary>AIRDROP_RX_HEADER plus the frame that follows it.</summary>
public sealed record AirdropRxFrame
{
    public uint HeaderSize { get; init; }
    public uint OriginalLength { get; init; }
    public uint Flags { get; init; }
    /// <summary>KeQueryInterruptTimePrecise, 100 ns.</summary>
    public ulong HostTimestamp { get; init; }
    public ulong Tsf { get; init; }
    public uint DeviceTimestampUs { get; init; }
    public uint RateNFlags { get; init; }
    public sbyte RssiDbm { get; init; }
    public byte Channel { get; init; }
    public ushort PhyInfo { get; init; }
    public uint DroppedSinceLast { get; init; }
    // v2
    public uint AmpduRef { get; init; }
    public ushort LegacyRate { get; init; }
    public byte Mcs { get; init; }
    public byte Nss { get; init; }
    public byte Bandwidth { get; init; }
    public byte Encoding { get; init; }
    public byte MacContext { get; init; }
    public uint MpduStatus { get; init; }
    public byte[] Frame { get; init; } = [];

    public bool TsfValid => (Flags & AirdropIoctl.RxTsfValid) != 0;
    public bool Truncated => (Flags & AirdropIoctl.RxTruncated) != 0;
    public bool BadFcs => (Flags & AirdropIoctl.RxBadFcs) != 0;

    /// <summary>Parses a READ_FRAME reply: header (HeaderSize bytes, ≥ 48), then FrameLength bytes of 802.11 frame.</summary>
    public static AirdropRxFrame Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < AirdropIoctl.RxHeaderV1Size) throw new InvalidDataException($"AIRDROP_RX_HEADER too short: {b.Length}");
        uint hs = U32(b, 0), fl = U32(b, 4);
        if (hs < AirdropIoctl.RxHeaderV1Size || hs > b.Length) throw new InvalidDataException($"bad RX HeaderSize {hs}");
        if (fl > b.Length - hs) throw new InvalidDataException($"RX FrameLength {fl} past the buffer");
        bool v2 = hs >= AirdropIoctl.RxHeaderSize;
        return new AirdropRxFrame
        {
            HeaderSize = hs,
            OriginalLength = U32(b, 8),
            Flags = U32(b, 12),
            HostTimestamp = BinaryPrimitives.ReadUInt64LittleEndian(b[16..]),
            Tsf = BinaryPrimitives.ReadUInt64LittleEndian(b[24..]),
            DeviceTimestampUs = U32(b, 32),
            RateNFlags = U32(b, 36),
            RssiDbm = (sbyte)b[40],
            Channel = b[41],
            PhyInfo = BinaryPrimitives.ReadUInt16LittleEndian(b[42..]),
            DroppedSinceLast = U32(b, 44),
            AmpduRef = v2 ? U32(b, 48) : 0,
            LegacyRate = v2 ? BinaryPrimitives.ReadUInt16LittleEndian(b[52..]) : (ushort)0,
            Mcs = v2 ? b[54] : (byte)0xff,
            Nss = v2 ? b[55] : (byte)0,
            Bandwidth = v2 ? b[56] : (byte)0,
            Encoding = v2 ? b[57] : (byte)0,
            MacContext = v2 ? b[58] : (byte)0,
            MpduStatus = v2 ? U32(b, 60) : 0,
            Frame = b.Slice((int)hs, (int)fl).ToArray(),
        };
    }

    /// <summary>Builds a reply as the driver would (for tests and replay).</summary>
    public static byte[] Build(ReadOnlySpan<byte> frame, ulong hostTimestamp100ns, ulong? tsf = null, sbyte rssi = -40, byte channel = 6, uint flags = AirdropIoctl.RxFcsStripped)
    {
        var b = new byte[AirdropIoctl.RxHeaderSize + frame.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, AirdropIoctl.RxHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)frame.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)frame.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), flags | (tsf.HasValue ? AirdropIoctl.RxTsfValid : 0));
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), hostTimestamp100ns);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), tsf ?? 0);
        b[40] = (byte)rssi;
        b[41] = channel;
        b[54] = 0xff;
        frame.CopyTo(b.AsSpan(AirdropIoctl.RxHeaderSize));
        return b;
    }

    private static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
}

/// <summary>AIRDROP_TX_HEADER + frame, as INJECT_FRAME takes it.</summary>
public static class AirdropTx
{
    /// <summary>
    /// Encodes <paramref name="frame"/> for INJECT_FRAME. Legacy rates go in 500 kb/s units (as owly's TxRate counts
    /// them), HT as MCS with RATE_HT (+SGI).
    /// </summary>
    public static byte[] Build(TxFrame frame, uint extraFlags = 0) => Build(frame.Bytes, frame.Rate, frame.NoAck, extraFlags);

    public static byte[] Build(ReadOnlySpan<byte> mpdu, TxRate rate, bool noAck, uint extraFlags = 0)
    {
        if (mpdu.Length > AirdropIoctl.TxMaxFrame) throw new ArgumentException($"frame of {mpdu.Length} bytes > {AirdropIoctl.TxMaxFrame}");
        uint flags = extraFlags;
        if (noAck) flags |= AirdropIoctl.TxNoAck;
        if (rate.IsHt)
        {
            flags |= AirdropIoctl.TxRateHt;
            if (rate.ShortGi) flags |= AirdropIoctl.TxSgi;
        }
        var b = new byte[AirdropIoctl.TxHeaderSize + mpdu.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, AirdropIoctl.TxHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)mpdu.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), rate.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), flags);
        mpdu.CopyTo(b.AsSpan(AirdropIoctl.TxHeaderSize));
        return b;
    }

    /// <summary>
    /// The driver honours NOACK only for unicast QoS data (it rewrites the QoS Ack Policy); a plain data frame as owly
    /// sends it would go out with ACK + retries. This turns a plain unicast data frame into a QoS data frame (TID 0,
    /// QoS control 0x0000) carrying the same body, so NOACK takes effect. Other frames are returned unchanged.
    /// </summary>
    public static byte[] UpgradeDataToQos(byte[] mpdu)
    {
        if (mpdu.Length < AwdlFrame.HeaderLen) return mpdu;
        ushort fc = (ushort)(mpdu[0] | mpdu[1] << 8);
        if ((fc & Fc.TypeSubtype) != Fc.Data || (mpdu[4] & 1) != 0) return mpdu; // only plain unicast data
        var o = new byte[mpdu.Length + 2];
        mpdu.AsSpan(0, AwdlFrame.HeaderLen).CopyTo(o);
        ushort qfc = (ushort)((fc & ~Fc.TypeSubtype) | Fc.QosData);
        o[0] = (byte)qfc;
        o[1] = (byte)(qfc >> 8);
        // QoS control 0x0000: TID 0, normal ack policy (the driver rewrites it for NOACK), no A-MSDU.
        mpdu.AsSpan(AwdlFrame.HeaderLen).CopyTo(o.AsSpan(AwdlFrame.HeaderLen + 2));
        return o;
    }
}

/// <summary>AIRDROP_TX_RESULT.</summary>
public readonly record struct AirdropTxResult(uint Status, uint FwStatus, byte Retries, byte RtsRetries, ushort Sequence, uint InitialRate, uint Flags)
{
    public static AirdropTxResult Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < AirdropIoctl.TxResultSize) throw new InvalidDataException($"AIRDROP_TX_RESULT too short: {b.Length}");
        return new AirdropTxResult(
            BinaryPrimitives.ReadUInt32LittleEndian(b[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[8..]),
            b[12], b[13],
            BinaryPrimitives.ReadUInt16LittleEndian(b[14..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[20..]));
    }
}
