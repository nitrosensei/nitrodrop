// Tests for Nitro.Awdl's wire formats. Most are ports of owly-awdl's unit tests (src/*.rs `mod tests`,
// https://github.com/t4t5/owly commit 9a676e1, MIT, Copyright (c) 2026 T4T5); the capture-vector tests are new.

using System.Net;
using Nitro.Awdl;
using AwdlC = Nitro.Awdl.Awdl;
using static AirDrop.Tests.Awdl.OwlyCaptureVectors;

namespace AirDrop.Tests.Awdl;

public class AwdlWireTests
{
    private static readonly MacAddr A = new(0x02, 0, 0, 0, 0, 0x01);
    private static readonly MacAddr B = new(0x86, 0x08, 0x04, 0x8a, 0x2b, 0xf4);
    private static MacAddr Mac(byte n) => new(2, 0, 0, 0, 0, n);

    // ---------------- lib.rs ----------------

    [Fact] // spec: NET-1
    public void LinkLocal_IsModifiedEui64_AndBack()
    {
        var ll = B.LinkLocal();
        Assert.Equal(IPAddress.Parse("fe80::8408:4ff:fe8a:2bf4"), ll);
        Assert.Equal(B, MacAddr.FromLinkLocal(ll));
        Assert.Null(MacAddr.FromLinkLocal(IPAddress.Parse("fe80::1")));
        Assert.Null(MacAddr.FromLinkLocal(IPAddress.Parse("2001:db8::8408:4ff:fe8a:2bf4")));
        // The U/L bit flips both ways: a locally administered 02:.. gives IID 00:..
        Assert.Equal(IPAddress.Parse("fe80::ff:fe00:1"), A.LinkLocal());
        Assert.Equal(7, B.LinkLocal(7).ScopeId);
    }

    [Fact]
    public void Macs_ParseUnpaddedOctets()
    {
        Assert.Equal("86:08:04:8a:2b:f4", MacAddr.Parse("86:8:4:8a:2b:f4").ToString());
        Assert.Equal("aa:bb:cc:dd:ee:ff", MacAddr.Parse("AA:BB:CC:DD:EE:FF").ToString());
        foreach (var bad in new[] { "", "1:2:3:4:5", "1:2:3:4:5:6:7", "123:0:0:0:0:0", "g:0:0:0:0:0", "1::3:4:5:6" })
            Assert.False(MacAddr.TryParse(bad, out _), bad);
        Assert.Equal("00:25:00:ff:94:73", AwdlC.Bssid.ToString()); // spec: AWDL-1
    }

    // ---------------- frame.rs ----------------

    [Fact] // spec: AWDL-13, T-AWDL-2
    public void DataFrame_MatchesOwlysGoldenBytes()
    {
        byte[] packet = [0x60, 0, 0, 0, 0, 0, 0x3b, 0x40];
        var frame = AwdlFrame.DataFrame(A, B, 0x123, 7, packet);
        byte[] golden =
        [
            0x08, 0x00, 0x00, 0x00,
            0x86, 0x08, 0x04, 0x8a, 0x2b, 0xf4,
            0x02, 0x00, 0x00, 0x00, 0x00, 0x01,
            0x00, 0x25, 0x00, 0xff, 0x94, 0x73,
            0x30, 0x12,
            0xaa, 0xaa, 0x03, 0x00, 0x17, 0xf2, 0x08, 0x00,
            0x03, 0x04, 0x07, 0x00, 0x00, 0x00, 0x86, 0xdd,
            0x60, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3b, 0x40,
        ];
        Assert.Equal(golden, frame);
        var d = Assert.IsType<ReceivedFrame.Data>(AwdlFrame.Parse(frame));
        Assert.Equal((A, B, (ushort)0x123, false), (d.Header.Src, d.Header.Dst, d.Header.Seq, d.Amsdu));
        var m = Assert.Single(AwdlFrame.Msdus(d.Header, d.Amsdu, d.Body));
        Assert.Equal((B, A, (ushort)0x86dd), (m.Dst, m.Src, m.Ethertype));
        Assert.Equal(packet, m.Payload.ToArray());
    }

    [Fact] // spec: AWDL-3
    public void ActionFrames_RoundTrip()
    {
        var frame = AwdlFrame.ActionFrame(A, 5, ActionSubtype.Mif, 0xdeadbeef, o => o.AddRange([21, 2, 0, 0x34, 1]));
        Assert.Equal(new byte[] { 127, 0x00, 0x17, 0xf2, 8, 0x10, 3, 0 }, frame[24..32]);
        var a = Assert.IsType<ReceivedFrame.Action>(AwdlFrame.Parse(frame));
        Assert.Equal((A, MacAddr.Broadcast, ActionSubtype.Mif), (a.Header.Src, a.Header.Dst, a.Subtype));
        Assert.Equal((0xdeadbeefu, 0xdeadbeefu), (a.PhyTx, a.TargetTx));
        Assert.Equal(new byte[] { 21, 2, 0, 0x34, 1 }, a.Tlvs.ToArray());
        var other = (byte[])frame.Clone();
        other[24 + 3] = 0xf3; // another vendor
        Assert.IsType<ReceivedFrame.Other>(AwdlFrame.Parse(other));
        other = (byte[])frame.Clone();
        other[24 + 6] = 1; // another AWDL subtype
        Assert.IsType<ReceivedFrame.Other>(AwdlFrame.Parse(other));
    }

    [Fact] // spec: AWDL-14
    public void Amsdus_UnpackWithABound()
    {
        byte[] p1 = [0x60, 1, 2], p2 = [0x60, 3, 4, 5, 6];
        var d = Assert.IsType<ReceivedFrame.Data>(AwdlFrame.Parse(AwdlFrame.AmsduFrame(B, A, 1, 10, [p1, p2])));
        Assert.True(d.Amsdu);
        var m = AwdlFrame.Msdus(d.Header, d.Amsdu, d.Body);
        Assert.Equal(2, m.Count);
        Assert.Equal((B, A), (m[0].Src, m[0].Dst));
        Assert.Equal(p1, m[0].Payload.ToArray());
        Assert.Equal(p2, m[1].Payload.ToArray());

        var many = Enumerable.Repeat(p1, AwdlFrame.MaxAmsduSubframes + 1).ToList();
        var tooMany = Assert.IsType<ReceivedFrame.Data>(AwdlFrame.Parse(AwdlFrame.AmsduFrame(B, A, 1, 10, many)));
        Assert.Throws<AwdlFormatException>(() => AwdlFrame.Msdus(tooMany.Header, true, tooMany.Body));
        var okBytes = AwdlFrame.AmsduFrame(B, A, 1, 10, many.Take(AwdlFrame.MaxAmsduSubframes).ToList());
        var ok = Assert.IsType<ReceivedFrame.Data>(AwdlFrame.Parse(okBytes));
        Assert.Equal(AwdlFrame.MaxAmsduSubframes, AwdlFrame.Msdus(ok.Header, true, ok.Body).Count);
        var shortF = Assert.IsType<ReceivedFrame.Data>(AwdlFrame.Parse(okBytes[..^1]));
        Assert.True(Assert.Throws<AwdlFormatException>(() => AwdlFrame.Msdus(shortF.Header, true, shortF.Body)).Truncated);
    }

    [Fact]
    public void ControlFrames_ArentAwdlOrMalformed()
    {
        byte[] ack = [0xd4, 0x00, 0x00, 0x00, 0x86, 0x08, 0x04, 0x8a, 0x2b, 0xf4];
        Assert.IsType<ReceivedFrame.Other>(AwdlFrame.Parse(ack));
        byte[] shortMgmt = [0xd0, 0x00, 0x00, 0x00, .. B.ToArray()];
        Assert.True(Assert.Throws<AwdlFormatException>(() => AwdlFrame.Parse(shortMgmt)).Truncated);
        var empty = new List<byte>();
        AwdlFrame.PutHeader(empty, Fc.Action, B, A, 0);
        Assert.IsType<ReceivedFrame.Other>(AwdlFrame.Parse(empty.ToArray()));
    }

    [Fact]
    public void Data_OffAwdlsBssidOrThroughAnAp_IsntAwdl()
    {
        foreach (var (at, v) in new[] { (1, (byte)0x01), (16, (byte)0x02), (1, (byte)0x40) })
        {
            var f = AwdlFrame.DataFrame(A, B, 0, 0, [0x60]);
            f[at] = v;
            Assert.IsType<ReceivedFrame.Other>(AwdlFrame.Parse(f));
        }
    }

    [Fact] // spec: AWDL-15
    public void AddBaRequests_AreAnsweredWithSuccess()
    {
        var req = new List<byte>();
        AwdlFrame.PutHeader(req, Fc.Action, A, B, 3);
        ushort p = (256 << 6) | (5 << 2) | 0x0003;
        req.AddRange([3, 0, 9, (byte)p, (byte)(p >> 8), 0, 0, 0, 0]);
        var r = Assert.IsType<ReceivedFrame.AddBaRequest>(AwdlFrame.Parse(req.ToArray()));
        Assert.Equal((B, (byte)9, (ushort)0), (r.Header.Src, r.Token, r.Timeout));
        var resp = AwdlFrame.AddBaResponse(A, B, 4, r.Token, r.Params, r.Timeout);
        Assert.Equal(new byte[] { 3, 1, 9, 0, 0 }, resp[24..29]);
        int q = resp[29] | resp[30] << 8;
        Assert.Equal((5, 0x2, 64), ((q >> 2) & 0xf, q & 0x3, q >> 6));
        Assert.Equal(B.ToArray(), resp[4..10]);
    }

    // ---------------- tlv.rs ----------------

    private static byte[] OwnFrame(bool mif)
    {
        var mac = Mac(1);
        var e = Election.OwnState(mac);
        e.Master = e.Sync = Mac(9);
        e.Height = 1;
        e.MasterMetric = 522;
        e.MasterCounter = 393;
        var own = new OwnAdvert
        {
            Addr = mac, Name = "owly", DeviceClass = DeviceClass.MacOS, Channel = 44, MasterChannel = new Channel(44),
            NextTu = 20, AwCounter = 401, PsfIntervalTu = 110, Sequence = ChannelSequence.Fixed(new Channel(44)),
            Election = e, Ht = HtCapabilities.OneStream,
        };
        var o = new List<byte>();
        AwdlTlv.PutAll(o, own, mif);
        return [.. o];
    }

    [Fact] // spec: AWDL-4
    public void OurTlvs_ParseBack()
    {
        var body = OwnFrame(true);
        Assert.Equal(new byte[] { 4, 5, 18, 24, 6, 7, 16, 12, 21 }, AwdlTlv.Split(body).Select(t => t.Kind).ToArray());
        var a = Advert.Parse(body);
        var s = a.Sync!.Value;
        Assert.Equal(((ushort)20, (ushort)401, (byte)44), (s.NextTu, s.AwCounter, s.MasterChannel));
        Assert.Equal(Mac(9), s.Master);
        var e = a.Election!.Value;
        Assert.True(a.ElectionV2);
        Assert.Equal(Mac(9), e.Sync);
        Assert.Equal((1u, 522u, 393u), (e.Height, e.MasterMetric, e.MasterCounter));
        Assert.Equal(ChannelSequence.Fixed(new Channel(44)), a.Sequence);
        Assert.Equal("owly", a.Name);
        Assert.Equal((AwdlTlv.Version, DeviceClass.MacOS), a.Version);
        Assert.Equal(HtCapabilities.OneStream, a.Ht);
        Assert.Equal("X0\0"u8.ToArray(), a.DataPath!.Value.Country);
        var psf = OwnFrame(false);
        Assert.Equal(new byte[] { 4, 5, 18, 24, 6, 7, 12, 21 }, AwdlTlv.Split(psf).Select(t => t.Kind).ToArray());
        Assert.Null(Advert.Parse(psf).Name);
    }

    [Fact]
    public void OwlsTlvLengths()
    {
        Assert.Equal(new[] { 73, 21, 41, 40, 9, 8, 8, 15, 2 }, AwdlTlv.Split(OwnFrame(true)).Select(t => t.Value.Length).ToArray());
    }

    [Fact] // spec: AWDL-5
    public void ZeroPadding_IsAccepted_AndAnythingElseRefused()
    {
        byte[] version = [TlvKind.Version, 2, 0, AwdlTlv.Version, DeviceClass.IOS];
        foreach (var pad in new[] { 0, 1, 2, 3, 8 })
            Assert.Equal((AwdlTlv.Version, DeviceClass.IOS), Advert.Parse(version.Concat(new byte[pad]).ToArray()).Version);
        Assert.Throws<AwdlFormatException>(() => Advert.Parse(version.Concat(new byte[] { 0, 1 }).ToArray()));
        Assert.Throws<AwdlFormatException>(() => Advert.Parse(version.Concat(new byte[] { TlvKind.Version, 2 }).ToArray()));
        Assert.Throws<AwdlFormatException>(() => Advert.Parse(version.Concat(new byte[] { TlvKind.Version, 9, 0, 1 }).ToArray()));
    }

    [Fact] // spec: AWDL-6
    public void UnknownTlvs_AreSkippedByLength()
    {
        byte[] body = [99, 4, 0, 1, 2, 3, 4, TlvKind.BloomFilter, 1, 0, 7, TlvKind.Version, 2, 0, AwdlTlv.Version, DeviceClass.IOS];
        Assert.Equal((AwdlTlv.Version, DeviceClass.IOS), Advert.Parse(body).Version);
    }

    [Fact]
    public void Version1Election_GivesWayToVersion2()
    {
        var v1 = AwdlTlv.Split(OwnFrame(false)).Where(t => t.Kind == TlvKind.ElectionParams)
            .SelectMany(t => new byte[] { t.Kind, (byte)t.Value.Length, (byte)(t.Value.Length >> 8) }.Concat(t.Value.ToArray())).ToArray();
        var a = Advert.Parse(v1);
        Assert.False(a.ElectionV2);
        var e = a.Election!.Value;
        Assert.Equal(((MacAddr?)null, 1u, 522u), (e.Sync, e.Height, e.MasterMetric));
    }

    [Fact]
    public void HtMasks_OfAnyLength()
    {
        byte[] v = [0, 0, 0xce, 0x11, 0x1b, 0xff, 0xff, 0, 0];
        Assert.Equal(0xffffu, HtCapabilities.Parse(v).RxMcs);
        Assert.Equal(0xffu, HtCapabilities.Parse(v[..6]).RxMcs);
        Assert.Equal(3839, HtCapabilities.OneStream.MaxAmsdu);
        Assert.Throws<AwdlFormatException>(() => HtCapabilities.Parse(v[..5]));
    }

    [Fact]
    public void Names_AreBounded()
    {
        var o = new List<byte>();
        AwdlTlv.PutArpa(o, new string('x', 100));
        Assert.Equal(AwdlTlv.NameMax, AwdlTlv.ParseArpa(AwdlTlv.Split(o.ToArray()).First().Value.Span).Length);
        Assert.Throws<AwdlFormatException>(() => AwdlTlv.ParseArpa([3, 10, (byte)'a']));
        o.Clear();
        AwdlTlv.PutArpa(o, new string('ğ', 40)); // 80 UTF-8 bytes: cut on a character boundary
        Assert.Equal(new string('ğ', 32), AwdlTlv.ParseArpa(AwdlTlv.Split(o.ToArray()).First().Value.Span));
    }

    // ---------------- channel.rs ----------------

    [Fact] // spec: AWDL-10
    public void Sequences_RoundTripInEachEncoding()
    {
        var s = ChannelSequence.FromChannels([149, 149, 149, 0, 0, 0, 0, 0, 6, 149, 149, 0, 0, 0, 0, 0]);
        var bytes = s.ToBytes();
        Assert.Equal(s.WireLength, bytes.Length);
        Assert.Equal(s, ChannelSequence.Parse(bytes));
        var legacy = new List<byte> { 15, 1, 0, 3, 0xff, 0xff };
        for (int i = 0; i < 16; i++) legacy.AddRange([0x01, (byte)(i < 8 ? 44 : 6)]);
        var l = ChannelSequence.Parse(legacy.ToArray());
        Assert.Equal((44, 6), (l.ChannelAt(0), l.ChannelAt(15)));
        byte[] simple = [15, 0, 0, 3, 0xff, 0xff, .. Enumerable.Repeat((byte)6, 16)];
        var sp = ChannelSequence.Parse(simple);
        Assert.All(sp.Channels(), c => Assert.Equal(6, c));
        Assert.Equal(simple, sp.ToBytes());
    }

    [Fact]
    public void OnlyOwlsShape_Parses()
    {
        var good = ChannelSequence.Fixed(new Channel(6)).ToBytes();
        foreach (var (at, bad) in new[] { (0, 7), (1, 2), (2, 1), (3, 0), (4, 0) })
        {
            var b = (byte[])good.Clone();
            b[at] = (byte)bad;
            Assert.Throws<AwdlFormatException>(() => ChannelSequence.Parse(b));
        }
        Assert.True(Assert.Throws<AwdlFormatException>(() => ChannelSequence.Parse(good[..^1])).Truncated);
    }

    [Fact] // spec: AWDL-12
    public void Intersecting_KeepsOnlyOurChannel_AndDominantPicksTheBest()
    {
        var phone = ChannelSequence.FromChannels([36, 36, 149, 0, 0, 0, 0, 36, 6, 36, 149, 36, 0, 0, 0, 36]);
        var (ours, kept) = phone.Intersect(149);
        Assert.Equal(2, kept);
        Assert.Equal(new byte[] { 0, 0, 149, 0, 0, 0, 0, 0, 0, 0, 149, 0, 0, 0, 0, 0 }, ours.Channels());
        Assert.Equal(0, phone.Intersect(44).Kept);
        Assert.Equal(36, phone.Dominant(6));
        Assert.Equal(149, ChannelSequence.FromChannels([6, 149, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]).Dominant(149));
        Assert.Equal(0, ChannelSequence.Empty(SequenceEncoding.OpClass).Dominant(6));
        Assert.Equal(2437, new Channel(6).FreqMhz);
        Assert.Equal(5745, new Channel(149).FreqMhz);
        Assert.Equal(new Channel(149), Channel.FromFreqMhz(5745));
        Assert.Null(Channel.FromFreqMhz(2436));
    }

    // ---------------- sync.rs ----------------

    [Fact]
    public void ANewClock_CountsSlotsFromItsStart()
    {
        var t = new AwdlTime(1_000_000);
        var c = new AwdlClock(t);
        Assert.Equal(AwdlSchedule.SlotUs, c.NextSlotUs(t));
        Assert.Equal(64, c.NextSlotTu(t));
        Assert.Equal(0, c.Slot(t));
        Assert.Equal(1, c.CurrentAw(t.Plus(16 * AwdlC.TU)));
        Assert.Equal(1, c.Slot(t.Plus(AwdlSchedule.SlotUs)));
        Assert.Equal(1, c.Slot(t.Plus(17 * AwdlSchedule.SlotUs)));
        Assert.Equal(1ul, c.NextSlotUs(t.Plus(AwdlSchedule.SlotUs - 1)));
    }

    [Fact] // spec: AWDL-7
    public void FollowingAMaster_TakesItsPhaseAndCount()
    {
        var c = new AwdlClock(new AwdlTime(0));
        var now = new AwdlTime(10_000_000);
        Assert.NotEqual(0, c.ErrorTu(now, 20, 401));
        c.Update(now, 20, 401);
        Assert.Equal(0, c.ErrorTu(now, 20, 401));
        Assert.Equal(20, c.NextSlotTu(now));
        Assert.Equal(100, c.CurrentEaw(now));
        Assert.Equal(100 % 16, c.Slot(now));
        Assert.Equal(101 % 16, c.Slot(now.Plus(20 * AwdlC.TU)));
        Assert.Equal(3, c.ErrorTu(now, 17, 401));
        Assert.Equal(-64, c.ErrorTu(now, 20, 397));
        var w = new AwdlClock(new AwdlTime(0));
        w.Update(new AwdlTime(0), 64, 65532);
        Assert.Equal(16383, w.CurrentEaw(new AwdlTime(0)));
        Assert.Equal(64, w.ErrorTu(new AwdlTime(0), 64, 0));
        Assert.Equal(0, w.CurrentEaw(new AwdlTime(AwdlSchedule.SlotUs)));
    }

    [Fact] // spec: AWDL-16
    public void Guards_KeepClearOfSlotEdges()
    {
        var c = new AwdlClock(new AwdlTime(0));
        const ulong g = AwdlSchedule.UnicastGuardUs, s = AwdlSchedule.SlotUs;
        Assert.Equal(g, c.EdgeWait(new AwdlTime(0), g));
        Assert.Equal(0ul, c.EdgeWait(new AwdlTime(g), g));
        Assert.Equal(0ul, c.EdgeWait(new AwdlTime(s / 2), g));
        Assert.Equal(0ul, c.EdgeWait(new AwdlTime(s - g), g));
        Assert.Equal(1000 + g, c.EdgeWait(new AwdlTime(s - 1000), g));
        Assert.True(c.InEndGuard(new AwdlTime(s - 1000), g));
        Assert.True(c.InStartGuard(new AwdlTime(s + 1000), g));
        Assert.Equal(AwdlSchedule.MulticastGuardUs - 1000, c.EdgeWait(new AwdlTime(1000), AwdlSchedule.MulticastGuardUs));
    }

    // ---------------- election.rs ----------------

    private static Election PeerElection(byte n, uint metric, uint counter)
    {
        var e = Election.OwnState(Mac(n));
        e.SelfMetric = e.MasterMetric = metric;
        e.SelfCounter = e.MasterCounter = counter;
        return e;
    }

    [Fact] // spec: AWDL-7, AWDL-8, AWDL-9
    public void Election_TiesMetricsCountersPathsAndCycles()
    {
        var low = Election.OwnState(Mac(1));
        Assert.True(low.IsMaster);
        Assert.Equal((60u, 0u), (low.SelfMetric, low.SelfCounter));
        var high = Election.OwnState(Mac(2));
        Assert.True(low.Run([high], Ranking.CounterFirst));
        Assert.Equal((Mac(2), Mac(2), 1u), (low.Master, low.Sync, low.Height));
        Assert.False(high.Run([low], Ranking.CounterFirst));
        Assert.True(high.IsMaster);

        var old = PeerElection(3, 510, 3214);
        var neu = PeerElection(4, 522, 393);
        var us = Election.OwnState(Mac(1));
        us.Run([old, neu], Ranking.MetricFirst);
        Assert.Equal(Mac(4), us.Master);
        us.Run([old, neu], Ranking.CounterFirst);
        Assert.Equal(Mac(3), us.Master);

        var master = PeerElection(9, 600, 10);
        var relay = Election.OwnState(Mac(5));
        relay.Run([master], Ranking.MetricFirst);
        us.Run([relay, master], Ranking.MetricFirst);
        Assert.Equal((Mac(9), 1u), (us.Sync, us.Height));
        us.Run([relay], Ranking.MetricFirst);
        Assert.Equal((Mac(9), Mac(5), 2u), (us.Master, us.Sync, us.Height));
        var follower = PeerElection(7, 900, 0);
        follower.Sync = Mac(1);
        us.Run([follower], Ranking.MetricFirst);
        Assert.True(us.IsMaster);
        var far = PeerElection(8, 900, 0);
        far.Height = Election.MaxHeight;
        us.Run([far], Ranking.MetricFirst);
        Assert.True(us.IsMaster);
        far.Height = uint.MaxValue;
        us.Run([far], Ranking.MetricFirst);
        Assert.True(us.IsMaster);
    }

    // ---------------- rate.rs ----------------

    [Fact]
    public void Rates_ParseAndFallBackForPeersWithoutHt()
    {
        Assert.Equal(TxRate.Basic, TxRate.Parse("12"));
        Assert.Equal(TxRate.Legacy(108), TxRate.Parse("54"));
        Assert.Equal(TxRate.Ht(15, true), TxRate.Parse("mcs15-sgi"));
        foreach (var bad in new[] { "", "11", "mcs", "mcs16", "mcs7-lgi", "mcs+7", "-12" }) Assert.Null(TxRate.Parse(bad));
        var ht = TxRate.Ht(7, true);
        Assert.Equal(TxRate.Basic, ht.ForPeer(null));
        Assert.Equal(TxRate.Ht(7), ht.ForPeer(HtCapabilities.OneStream));
        Assert.Equal(TxRate.Basic, TxRate.Ht(15).ForPeer(HtCapabilities.OneStream));
        Assert.Equal(ht, ht.ForPeer(HtCapabilities.OneStream with { Capabilities = 0x0020 }));
    }

    // ---------------- ip.rs ----------------

    private static Ipv6Packet Tcp(byte flags, byte[] payload)
    {
        var seg = new byte[20 + payload.Length];
        seg[12] = 5 << 4;
        seg[13] = flags;
        payload.CopyTo(seg, 20);
        return new Ipv6Packet(A, B, Ipv6Packet.Build(B.LinkLocal().GetAddressBytes(), A.LinkLocal().GetAddressBytes(), Ipv6Packet.Tcp, 64, seg));
    }

    [Fact]
    public void BareSyns_AreToldApart()
    {
        Assert.True(Tcp(0x02, []).IsBareSynTo(A));
        Assert.False(Tcp(0x02, []).IsBareSynTo(B));
        Assert.False(Tcp(0x12, []).IsBareSynTo(A));
        Assert.False(Tcp(0x02, "x"u8.ToArray()).IsBareSynTo(A));
        Assert.False(Tcp(0x10, []).IsBareSynTo(A));
    }

    [Fact]
    public void EchoRequests_AreValidIcmpv6()
    {
        var p = Ipv6Packet.EchoRequest(A, B, 0x4f77, 1, "owly"u8);
        var (s, d) = p.Addrs()!.Value;
        Assert.Equal((A.LinkLocal(), B.LinkLocal()), (s, d));
        Assert.Equal(0, Ipv6Packet.Checksum(s.GetAddressBytes(), d.GetAddressBytes(), Ipv6Packet.IcmpV6, p.Data.AsSpan(40)));
        Assert.Equal(255, p.Data[7]);
        var reply = (byte[])p.Data.Clone();
        d.GetAddressBytes().CopyTo(reply, 8);
        s.GetAddressBytes().CopyTo(reply, 24);
        reply[40] = 129;
        Assert.True(new Ipv6Packet(A, B, reply).IsEchoReply(0x4f77));
        Assert.False(new Ipv6Packet(A, B, reply).IsEchoReply(1));
        Assert.False(p.IsEchoReply(0x4f77));
    }

    [Fact] // spec: NET-2
    public void EthernetAndIpAddressing()
    {
        var p = Tcp(0x02, []);
        Assert.Equal(p, Ipv6Packet.FromEthernet(p.ToEthernet()));
        var v4 = p.ToEthernet();
        v4[12] = 0x08; v4[13] = 0x00;
        Assert.Null(Ipv6Packet.FromEthernet(v4));
        Assert.Equal(p, Ipv6Packet.FromIp(p.Data));
        var mdns = (byte[])p.Data.Clone();
        IPAddress.Parse("ff02::fb").GetAddressBytes().CopyTo(mdns, 24);
        Assert.Equal(new MacAddr(0x33, 0x33, 0, 0, 0, 0xfb), Ipv6Packet.FromIp(mdns)!.Dst);
        // Windows' own random link-local (no ff:fe) and DAD from :: can't map to an AWDL address.
        var random = (byte[])p.Data.Clone();
        IPAddress.Parse("fe80::1c2d:3e4f:5a6b:7c8d").GetAddressBytes().CopyTo(random, 8);
        Assert.Null(Ipv6Packet.FromIp(random));
        var dad = (byte[])p.Data.Clone();
        IPAddress.IPv6Any.GetAddressBytes().CopyTo(dad, 8);
        Assert.Null(Ipv6Packet.FromIp(dad));
    }

    // ---------------- real frames from Owly's captures ----------------

    [Fact] // spec: T-AWDL-1, AWDL-6
    public void IphoneMif_FromCapture_ParsesWhole()
    {
        var a = Assert.IsType<ReceivedFrame.Action>(AwdlFrame.Parse(Bytes(IphoneMif)));
        Assert.Equal((ActionSubtype.Mif, Mac(2), (ushort)2127), (a.Subtype, a.Header.Src, a.Header.Seq));
        Assert.Equal(0x611b0f5au, a.PhyTx);
        var kinds = AwdlTlv.Split(a.Tlvs).Select(t => t.Kind).ToArray();
        Assert.Equal(new byte[] { 4, 5, 18, 24, 12, 7, 17, 16, 21, 6, 2, 2, 2, 2, 2, 2 }, kinds); // iOS order, unknown 17 + service responses skipped
        var ad = Advert.Parse(a.Tlvs);
        var s = ad.Sync!.Value;
        Assert.Equal(((byte)44, (ushort)38, (byte)44, (ushort)16, (ushort)110, Mac(2), (byte)4, (ushort)2533),
            (s.NextAwChannel, s.NextTu, s.MasterChannel, s.AwPeriod, s.AfPeriod, s.Master, s.PresenceMode, s.AwCounter));
        var e = ad.Election!.Value;
        Assert.True(ad.ElectionV2);
        Assert.Equal((Mac(2), (MacAddr?)Mac(2), 577u, 0u, 525u, 525u, 577u),
            (e.Master, e.Sync, e.MasterCounter, e.Height, e.MasterMetric, e.SelfMetric, e.SelfCounter));
        // The phone spends slots 0-2 and 9-10 on 44 and slot 8 on 6 (the one Owly's Intel mode meets it in).
        Assert.Equal(new byte[] { 44, 44, 44, 0, 0, 0, 0, 0, 6, 44, 44, 0, 0, 0, 0, 0 }, ad.Sequence!.Channels());
        Assert.Equal(SequenceEncoding.OpClass, ad.Sequence.Encoding);
        Assert.Equal(((byte)0xa0, DeviceClass.IOS), ad.Version);
        Assert.Equal(new string('x', 36), ad.Name);
        Assert.Equal(0xffffu, ad.Ht!.Value.RxMcs); // two spatial streams (MCS 0-15)
        // And a node takes it for a valid peer.
        var node = new AwdlNode(new AwdlConfig { Addr = Mac(3), Name = "us", Radio = RadioConstraints.IntelExclusive }, new AwdlTime(1_000_000));
        var acts = node.HandleFrame(new AwdlTime(1_000_100), -40, Bytes(IphoneMif));
        var up = Assert.Single(acts.OfType<NodeAction.PeerUp>());
        Assert.Equal((Mac(2), DeviceClass.IOS), (up.Peer.Mac, up.Peer.DeviceClass));
        Assert.Equal(0ul, node.Stats.RxMalformed);
    }

    [Fact] // spec: AWDL-17
    public void IphonePsf_FromCapture_ParsesButDoesNotMakeThePeerValid()
    {
        var a = Assert.IsType<ReceivedFrame.Action>(AwdlFrame.Parse(Bytes(IphonePsf)));
        Assert.Equal(ActionSubtype.Psf, a.Subtype);
        var ad = Advert.Parse(a.Tlvs);
        Assert.Equal((ushort)2515, ad.Sync!.Value.AwCounter);
        Assert.Null(ad.Name);
        var node = new AwdlNode(new AwdlConfig { Addr = Mac(3), Name = "us", Radio = RadioConstraints.IntelExclusive }, new AwdlTime(1_000_000));
        Assert.Empty(node.HandleFrame(new AwdlTime(1_000_100), -40, Bytes(IphonePsf)).OfType<NodeAction.PeerUp>());
        Assert.False(node.PeerOf(Mac(2))!.Valid);
    }

    [Fact] // spec: AWDL-13, AWDL-18
    public void IphoneQosSyn_FromCapture_IsAQosDataFrameWithABareSyn()
    {
        var d = Assert.IsType<ReceivedFrame.Data>(AwdlFrame.Parse(Bytes(IphoneQosSyn)));
        Assert.False(d.Amsdu);
        Assert.Equal((Mac(1), Mac(2)), (d.Header.Src, d.Header.Dst));
        var m = Assert.Single(AwdlFrame.Msdus(d.Header, false, d.Body));
        Assert.Equal(AwdlFrame.EthertypeIpv6, m.Ethertype);
        var p = Ipv6Packet.FromIp(m.Payload.ToArray())!;
        Assert.Equal((Mac(2), Mac(1)), (p.Dst, p.Src)); // EUI-64 link-locals map back to the frame's MACs (NET-2)
        Assert.True(p.IsBareSynTo(Mac(2)));
        Assert.Equal(0x2243, (p.Data[42] << 8) | p.Data[43]); // the receiver's AirDrop HTTPS port in this capture
    }

    [Fact] // byte-exact against Owly's own transmitted MIF
    public void OurEncoder_ReproducesOwlysCapturedMif()
    {
        var captured = Bytes(OwlyMif);
        var a = Assert.IsType<ReceivedFrame.Action>(AwdlFrame.Parse(captured));
        var ad = Advert.Parse(a.Tlvs);
        var s = ad.Sync!.Value;
        var pe = ad.Election!.Value;
        var e = Election.OwnState(a.Header.Src);
        e.Master = pe.Master;
        e.Sync = pe.Sync!.Value;
        e.Height = pe.Height;
        e.MasterMetric = pe.MasterMetric;
        e.MasterCounter = pe.MasterCounter;
        e.SelfMetric = pe.SelfMetric;
        e.SelfCounter = pe.SelfCounter;
        var own = new OwnAdvert
        {
            Addr = a.Header.Src, Name = ad.Name!, DeviceClass = ad.Version!.Value.DeviceClass, Channel = s.NextAwChannel,
            MasterChannel = new Channel(s.MasterChannel), NextTu = s.NextTu, AwCounter = s.AwCounter, PsfIntervalTu = s.AfPeriod,
            Sequence = ad.Sequence!, Election = e, Ht = ad.Ht,
        };
        var ours = AwdlFrame.ActionFrame(a.Header.Src, a.Header.Seq, ActionSubtype.Mif, a.PhyTx, o => AwdlTlv.PutAll(o, own, mif: true));
        // The capture tool anonymised the Data Path State country ("X0\0" -> "XXX"); put ours in its place.
        var expected = (byte[])captured.Clone();
        int at = IndexOf(expected, [0x0c, 0x0f, 0x00, 0x24, 0x8f]) + 5;
        "X0\0"u8.CopyTo(expected.AsSpan(at));
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(ours));
    }

    private static int IndexOf(byte[] hay, byte[] needle) => hay.AsSpan().IndexOf(needle);
}
