// Nitro.Awdl's binding of driver/include/airdrop_ioctl.h (ABI v2), the BLE advert and the host clock.

using System.Buffers.Binary;
using Nitro.Awdl;
using Nitro.Awdl.Ble;
using Nitro.Awdl.Interop;

namespace AirDrop.Tests.Awdl;

public class AwdlInteropTests
{
    [Fact]
    public void IoctlCodes_MatchCtlCode()
    {
        Assert.Equal(0x8A576000u, AirdropIoctl.GetInfo);
        Assert.Equal(0x8A57A004u, AirdropIoctl.Start);
        Assert.Equal(0x8A57A008u, AirdropIoctl.Stop);
        Assert.Equal(0x8A57600Eu, AirdropIoctl.ReadFrame);
        Assert.Equal(0x8A57A010u, AirdropIoctl.InjectFrame);
        Assert.Equal(Guid.Parse("6c1b3e0a-9d42-4f5e-8a71-2f3c4b5d6e7f"), AirdropIoctl.DeviceInterface);
    }

    [Fact] // header static_asserts: START_PARAMS 24, offsets 8/9/16
    public void StartParams_Layout()
    {
        var b = new AirdropStartParams(AirdropIoctl.StartNoPromisc, 6, Nitro.Awdl.Awdl.Bssid, 100).ToBytes();
        Assert.Equal(24, b.Length);
        Assert.Equal(24u, BinaryPrimitives.ReadUInt32LittleEndian(b));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)));
        Assert.Equal(6, b[8]);
        Assert.Equal(new byte[] { 0x00, 0x25, 0x00, 0xff, 0x94, 0x73 }, b[9..15]);
        Assert.Equal(100, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(16)));
    }

    [Fact] // INFO 176 (v1 prefix 144)
    public void Info_ParsesV2AndV1()
    {
        var b = new byte[176];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 176);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), AirdropIoctl.StateRadioOn);
        new byte[] { 0x86, 0x08, 0x04, 0x8a, 0x2b, 0xf4 }.CopyTo(b, 16);
        "77.ab12cd"u8.CopyTo(b.AsSpan(24));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(76), 3);
        new byte[] { 1, 6, 11 }.CopyTo(b, 80);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(152), 42);
        b[168] = 6;
        b[169] = AirdropIoctl.RadioModeIbss;
        var i = AirdropInfo.Parse(b);
        Assert.Equal((2u, AirdropIoctl.StateRadioOn, "86:08:04:8a:2b:f4", "77.ab12cd"), (i.AbiVersion, i.State, i.MacAddress.ToString(), i.FirmwareVersion));
        Assert.Equal(new byte[] { 1, 6, 11 }, i.Channels);
        Assert.Equal((42u, (byte)6, AirdropIoctl.RadioModeIbss), (i.TxSuccess, i.RadioChannel, i.RadioMode));
        var v1 = b[..144];
        BinaryPrimitives.WriteUInt32LittleEndian(v1, 144);
        var o = AirdropInfo.Parse(v1);
        Assert.Equal((0u, (byte)0), (o.TxSuccess, o.RadioChannel));
        Assert.Throws<InvalidDataException>(() => AirdropInfo.Parse(b[..100]));
    }

    [Fact] // RX_HEADER 64: HostTimestamp@16, Tsf@24, Rssi@40, Channel@41, Dropped@44, Mcs@54
    public void RxHeader_RoundTripsAndUsesHeaderSize()
    {
        byte[] frame = [0xd0, 0, 0, 0, 1, 2, 3];
        var b = AirdropRxFrame.Build(frame, 123_456_789, tsf: 0x1122334455, rssi: -52, channel: 6);
        Assert.Equal(64 + frame.Length, b.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(44), 3);
        var r = AirdropRxFrame.Parse(b);
        Assert.Equal(frame, r.Frame);
        Assert.Equal((123_456_789ul, 0x1122334455ul, (sbyte)-52, (byte)6, 3u), (r.HostTimestamp, r.Tsf, r.RssiDbm, r.Channel, r.DroppedSinceLast));
        Assert.True(r.TsfValid);
        Assert.False(r.BadFcs);
        // A future header that grew: the frame still starts at HeaderSize.
        var grown = new byte[80 + frame.Length];
        b.AsSpan(0, 64).CopyTo(grown);
        BinaryPrimitives.WriteUInt32LittleEndian(grown, 80);
        frame.CopyTo(grown, 80);
        Assert.Equal(frame, AirdropRxFrame.Parse(grown).Frame);
        BinaryPrimitives.WriteUInt32LittleEndian(grown.AsSpan(4), 1000);
        Assert.Throws<InvalidDataException>(() => AirdropRxFrame.Parse(grown));
    }

    [Fact] // TX_HEADER 16 + frame; Rate in 500 kb/s units or MCS with RATE_HT
    public void TxHeader_EncodesRateAndFlags()
    {
        byte[] f = [8, 0, 0, 0];
        var legacy = AirdropTx.Build(f, TxRate.Basic, noAck: true);
        Assert.Equal((16u, 4u, 24u, AirdropIoctl.TxNoAck), // 12 Mb/s in 500 kb/s units
            (BinaryPrimitives.ReadUInt32LittleEndian(legacy), BinaryPrimitives.ReadUInt32LittleEndian(legacy.AsSpan(4)),
             BinaryPrimitives.ReadUInt32LittleEndian(legacy.AsSpan(8)), BinaryPrimitives.ReadUInt32LittleEndian(legacy.AsSpan(12))));
        Assert.Equal(f, legacy[16..]);
        var ht = AirdropTx.Build(f, TxRate.Ht(7, true), noAck: false);
        Assert.Equal((7u, AirdropIoctl.TxRateHt | AirdropIoctl.TxSgi),
            (BinaryPrimitives.ReadUInt32LittleEndian(ht.AsSpan(8)), BinaryPrimitives.ReadUInt32LittleEndian(ht.AsSpan(12))));
        Assert.Throws<ArgumentException>(() => AirdropTx.Build(new byte[2347], TxRate.Basic, false));
    }

    [Fact] // the driver honours NOACK only on unicast QoS data (driver/README.md "Owly eşlemesi")
    public void NoAckData_IsUpgradedToQosData()
    {
        var a = new MacAddr(2, 0, 0, 0, 0, 1);
        var b = new MacAddr(2, 0, 0, 0, 0, 2);
        var plain = AwdlFrame.DataFrame(a, b, 0x10, 3, [0x60, 0, 0, 0]);
        var q = AirdropTx.UpgradeDataToQos(plain);
        Assert.Equal(plain.Length + 2, q.Length);
        Assert.Equal(0x88, q[0]);
        Assert.Equal(plain[1..24], q[1..24]);
        Assert.Equal(new byte[] { 0, 0 }, q[24..26]);
        Assert.Equal(plain[24..], q[26..]);
        // Parses as the same MSDU (as iPhones' own QoS data does, see the capture test).
        var d = Assert.IsType<ReceivedFrame.Data>(AwdlFrame.Parse(q));
        Assert.Equal(new byte[] { 0x60, 0, 0, 0 }, Assert.Single(AwdlFrame.Msdus(d.Header, d.Amsdu, d.Body)).Payload.ToArray());
        // Multicast data and action frames are untouched.
        var mc = AwdlFrame.DataFrame(a, new MacAddr(0x33, 0x33, 0, 0, 0, 1), 1, 1, [0x60]);
        Assert.Same(mc, AirdropTx.UpgradeDataToQos(mc));
        var act = AwdlFrame.ActionFrame(a, 1, ActionSubtype.Psf, 0, o => { });
        Assert.Same(act, AirdropTx.UpgradeDataToQos(act));
    }

    [Fact]
    public void TxResult_Parses()
    {
        var b = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 24);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), AirdropIoctl.TxStFailed);
        b[12] = 15;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), 0x123);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), AirdropIoctl.TxrNoAckNotHonored);
        var r = AirdropTxResult.Parse(b);
        Assert.Equal((AirdropIoctl.TxStFailed, (byte)15, (ushort)0x123, AirdropIoctl.TxrNoAckNotHonored), (r.Status, r.Retries, r.Sequence, r.Flags));
    }

    [Fact] // owly-airdrop proto/ble.rs layout (v3) and tools/ble_test.py (v1)
    public void BleAdvert_Layouts()
    {
        var a = new AirDropBleAdvert([0xaa, 0xbb, 0xcc], [1, 2], [3, 4], [5, 6], [7, 8]);
        Assert.Equal(Convert.FromHexString("051240aabbcc00000000030102030405060708 00".Replace(" ", "")), a.ManufacturerData());
        Assert.Equal(Convert.FromHexString("05120000000000000000010102030405060708 00".Replace(" ", "")), a.ManufacturerDataV1());
        var anon = AirDropBleAdvert.Anonymous([1, 2, 3]).ManufacturerData();
        Assert.Equal(20, anon.Length);
        Assert.All(anon[10..], x => Assert.Equal(0, x == 3 ? 0 : x));
        Assert.Equal(0x004c, AirDropBleAdvert.AppleCompanyId);
        Assert.Equal(2, AirDropBleAdvert.HashPrefix("+905551112233").Length);
    }

    [Fact]
    public void HostClock_IsMonotonicMicroseconds()
    {
        ulong a = HostClock.NowMicros();
        Thread.Sleep(20);
        ulong b = HostClock.NowMicros();
        Assert.InRange(b - a, 15_000ul, 2_000_000ul);
        if (HostClock.IsInterruptTime) Assert.Equal(12_345ul, HostClock.FromInterruptTime100ns(123_450));
    }

    [Fact]
    public void PreciseWaiter_WakesForHandlesAndDeadlines()
    {
        using var w = new PreciseWaiter();
        using var e = new ManualResetEvent(false);
        ulong t0 = HostClock.NowMicros();
        Assert.Equal(-1, w.WaitUntil([e], t0 + 5_000, HostClock.NowMicros));
        Assert.True(HostClock.NowMicros() >= t0 + 5_000);
        e.Set();
        Assert.Equal(0, w.WaitUntil([e], HostClock.NowMicros() + 1_000_000, HostClock.NowMicros));
    }
}
