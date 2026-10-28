// Multi-node AWDL in virtual time: AwdlNode instances on a deterministic simulated air (no threads, no clocks).
// Scenarios follow owly-awdl's node.rs tests and its tests/ simulation (https://github.com/t4t5/owly, commit 9a676e1).

using Nitro.Awdl;
using AwdlC = Nitro.Awdl.Awdl;

namespace AirDrop.Tests.Awdl;

/// <summary>Event-driven air: transmits reach every other node on the same channel after an airtime, minus losses.</summary>
internal sealed class VirtualAir
{
    public sealed class Station
    {
        public required AwdlNode Node;
        public required byte Channel;
        public bool Silent;
        public AwdlTime? Timer;
        public readonly List<PeerInfo> Up = [];
        public readonly List<MacAddr> Down = [];
        public readonly List<Ipv6Packet> Delivered = [];
        public readonly List<TxFrame> Sent = [];
        public MacAddr Mac => Node.Config.Addr;
    }

    private readonly PriorityQueue<(Station To, byte[] Frame, int Kind), (ulong, long)> _events = new();
    private readonly Random _rnd;
    private long _order;
    public readonly List<Station> Stations = [];
    public AwdlTime Now { get; private set; }
    public int LossPercent { get; set; }
    public ulong AirtimeUs { get; set; } = 150;

    public VirtualAir(int seed = 7, ulong startUs = 1_000_000)
    {
        _rnd = new Random(seed);
        Now = new AwdlTime(startUs);
    }

    public Station Add(byte macByte, RadioConstraints? radio = null, string? name = null, ulong offsetUs = 0)
    {
        var mac = new MacAddr(0x02, 0, 0, 0, 0, macByte);
        var r = radio ?? RadioConstraints.IntelExclusive;
        var s = new Station
        {
            Node = new AwdlNode(new AwdlConfig { Addr = mac, Name = name ?? $"node{macByte}", Radio = r, RssiThreshold = null }, Now.Plus(offsetUs)),
            Channel = r.Channel.Number,
        };
        Stations.Add(s);
        Apply(s, s.Node.HandleTimer(Now.Plus(offsetUs)));
        return s;
    }

    public void Outbound(Station s, Ipv6Packet p) => Apply(s, s.Node.HandleOutbound(Now, p));

    private void Apply(Station s, List<NodeAction> actions)
    {
        foreach (var a in actions)
            switch (a)
            {
                case NodeAction.Transmit t:
                    if (s.Silent) break;
                    s.Sent.Add(t.Frame);
                    var at = AwdlTime.Max(t.At, Now).Plus(AirtimeUs);
                    foreach (var o in Stations)
                        if (o != s && o.Channel == s.Channel && !o.Silent && _rnd.Next(100) >= LossPercent)
                            _events.Enqueue((o, t.Frame.Bytes, 0), (at.Micros, _order++));
                    break;
                case NodeAction.ArmTimer arm:
                    s.Timer = arm.At;
                    _events.Enqueue((s, [], 1), (Math.Max(arm.At.Micros, Now.Micros), _order++));
                    break;
                case NodeAction.PeerUp up: s.Up.Add(up.Peer); break;
                case NodeAction.PeerDown down: s.Down.Add(down.Mac); break;
                case NodeAction.Deliver d: s.Delivered.Add(d.Packet); break;
            }
    }

    /// <summary>Runs events up to <paramref name="untilUs"/> more microseconds.</summary>
    public void Run(ulong forUs)
    {
        var end = Now.Plus(forUs);
        while (_events.TryPeek(out var e, out var key) && key.Item1 <= end.Micros)
        {
            _events.Dequeue();
            Now = new AwdlTime(Math.Max(key.Item1, Now.Micros));
            var s = e.To;
            if (e.Kind == 0)
                Apply(s, s.Node.HandleFrame(Now, -45, e.Frame));
            else if (s.Timer is { } t && t <= Now)
            {
                s.Timer = null;
                Apply(s, s.Node.HandleTimer(Now));
            }
        }
        Now = end;
    }
}

public class AwdlSimulationTests
{
    private static Ipv6Packet Udp(VirtualAir.Station from, VirtualAir.Station to, byte[] payload)
    {
        var udp = new byte[8 + payload.Length];
        udp[0] = 0x22; udp[1] = 0x42; udp[2] = 0x22; udp[3] = 0x42;
        udp[4] = (byte)(udp.Length >> 8); udp[5] = (byte)udp.Length;
        payload.CopyTo(udp, 8);
        return Ipv6Packet.FromIp(Ipv6Packet.Build(from.Mac.LinkLocal().GetAddressBytes(), to.Mac.LinkLocal().GetAddressBytes(), 17, 64, udp))!;
    }

    [Fact] // spec: AWDL-7, AWDL-17, T-AWDL-3
    public void TwoFixedNodes_FindEachOther_AgreeOnAMaster_AndSync()
    {
        var air = new VirtualAir();
        var a = air.Add(1);
        var b = air.Add(2, offsetUs: 23_456);
        air.Run(3_000_000);
        Assert.Equal(b.Mac, Assert.Single(a.Up).Mac);
        Assert.Equal(a.Mac, Assert.Single(b.Up).Mac);
        Assert.Equal(a.Node.Election.Master, b.Node.Election.Master);
        var master = a.Node.Election.IsMaster ? a : b;
        var slave = master == a ? b : a;
        Assert.Equal(1u, slave.Node.Election.Height);
        // The follower's slot clock lines up with the master's (within a TU).
        var now = air.Now;
        Assert.InRange((long)slave.Node.Clock.NextSlotUs(now) - (long)master.Node.Clock.NextSlotUs(now), -(long)AwdlC.TU, (long)AwdlC.TU);
        Assert.Equal(master.Node.Clock.CurrentEaw(now), slave.Node.Clock.CurrentEaw(now));
        Assert.Equal(0ul, a.Node.Stats.RxMalformed + b.Node.Stats.RxMalformed);
    }

    [Fact] // spec: AWDL-8, T-AWDL-3
    public void ThreeNodes_UnderTwentyPercentLoss_Converge()
    {
        var air = new VirtualAir(seed: 3) { LossPercent = 20 };
        var n = new[] { air.Add(1), air.Add(2, offsetUs: 40_000), air.Add(3, offsetUs: 77_000) };
        air.Run(10_000_000);
        foreach (var s in n)
            Assert.Equal(2, s.Node.Peers.Count(p => p.Valid));
        Assert.Single(n.Select(s => s.Node.Election.Master).Distinct());
        Assert.Single(n, s => s.Node.Election.IsMaster);
    }

    [Fact]
    public void NodesOnDifferentChannels_NeverMeet()
    {
        var air = new VirtualAir();
        var a = air.Add(1);
        var b = air.Add(2, new RadioConstraints(new Channel(44), Presence.Fixed));
        air.Run(3_000_000);
        Assert.Empty(a.Up);
        Assert.Empty(b.Up);
    }

    [Fact] // spec: AWDL-11
    public void ASilentPeer_IsDroppedAfterTheTimeout()
    {
        var air = new VirtualAir();
        var a = air.Add(1);
        var b = air.Add(2);
        air.Run(2_000_000);
        Assert.Single(a.Up);
        b.Silent = true;
        air.Run(AwdlNode.PeerTimeoutUs - 1_200_000);
        Assert.Empty(a.Down);
        air.Run(2_500_000);
        Assert.Equal(b.Mac, Assert.Single(a.Down));
        Assert.Null(a.Node.PeerOf(b.Mac));
        Assert.True(a.Node.Election.IsMaster);
    }

    [Fact] // spec: AWDL-13, NET-2
    public void Packets_GoAcrossInOrder()
    {
        var air = new VirtualAir();
        var a = air.Add(1);
        var b = air.Add(2);
        air.Run(2_000_000);
        for (int i = 0; i < 50; i++) air.Outbound(a, Udp(a, b, [(byte)i, 1, 2, 3]));
        air.Run(500_000);
        var got = b.Delivered.Where(p => p.Data[6] == 17).ToList();
        Assert.Equal(50, got.Count);
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(a.Mac, got[i].Src);
            Assert.Equal((byte)i, got[i].Data[48]);
        }
        Assert.Equal(50ul, a.Node.Stats.TxUnicast);
        Assert.True(a.Node.Config.EffectiveNoAck);
        Assert.All(a.Sent.Where(f => !f.IsAction && !f.To.IsMulticast), f => Assert.True(f.NoAck));
    }

    [Fact]
    public void PacketsToNobody_AreDropped()
    {
        var air = new VirtualAir();
        var a = air.Add(1);
        var b = air.Add(2);
        air.Run(1_500_000);
        var ghost = new VirtualAir.Station { Node = new AwdlNode(new AwdlConfig { Addr = new MacAddr(2, 0, 0, 0, 0, 9), Name = "ghost", Radio = RadioConstraints.IntelExclusive }, air.Now), Channel = 6 };
        air.Outbound(a, Udp(a, ghost, [1]));
        air.Run(100_000);
        Assert.Equal(1ul, a.Node.Stats.DroppedNoPeer);
        Assert.Equal(0ul, a.Node.Stats.TxUnicast);
    }

    [Fact] // owly: neighbour echo every 3 s keeps an idle iPhone's peer entry fresh
    public void IdlePeers_AreEchoed()
    {
        var air = new VirtualAir();
        var a = air.Add(1);
        var b = air.Add(2);
        air.Run(8_000_000);
        var echoes = a.Sent.Where(f => !f.IsAction && f.To == b.Mac).Select(f => AwdlFrame.Parse(f.Bytes)).OfType<ReceivedFrame.Data>()
            .SelectMany(d => AwdlFrame.Msdus(d.Header, d.Amsdu, d.Body))
            .Select(m => m.Payload.ToArray()).Where(p => p.Length > 40 && p[6] == 58 && p[40] == 128).ToList();
        Assert.InRange(echoes.Count, 1, 4);
        // b's node doesn't hand our echoes to its host as if they were traffic... it's the host stack's job to answer.
        Assert.All(echoes, e => Assert.Equal(AwdlNode.EchoPayload.ToArray(), e[48..]));
    }

    [Fact] // spec: AWDL-18
    public void ASynBeforeThePeerIsValid_IsHeldAndThenDelivered()
    {
        var air = new VirtualAir();
        var a = air.Add(1);
        // b hears a frame from a phone that has only sent a PSF so far: a SYN from it is held.
        var phone = new MacAddr(0x02, 0, 0, 0, 0, 7);
        var b = air.Add(2);
        var psf = AwdlFrame.ActionFrame(phone, 1, ActionSubtype.Psf, 0, o => { });
        b.Node.HandleFrame(air.Now, -40, psf);
        var seg = new byte[20];
        seg[12] = 5 << 4;
        seg[13] = 0x02;
        var syn = Ipv6Packet.FromIp(Ipv6Packet.Build(phone.LinkLocal().GetAddressBytes(), b.Mac.LinkLocal().GetAddressBytes(), 6, 64, seg))!;
        var data = AwdlFrame.DataFrame(phone, b.Mac, 1, 1, syn.Data);
        Assert.Empty(b.Node.HandleFrame(air.Now, -40, data).OfType<NodeAction.Deliver>());
        Assert.Equal(1ul, b.Node.Stats.SynHeld);
        // Its MIF arrives: the peer goes valid and the SYN is handed on.
        var own = new OwnAdvert
        {
            Addr = phone, Name = "iPhone", DeviceClass = DeviceClass.IOS, Channel = 6, MasterChannel = new Channel(6), NextTu = 30,
            AwCounter = 5, PsfIntervalTu = 110, Sequence = ChannelSequence.Fixed(new Channel(6)), Election = Election.OwnState(phone),
            Ht = HtCapabilities.OneStream,
        };
        var mif = AwdlFrame.ActionFrame(phone, 2, ActionSubtype.Mif, 0, o => AwdlTlv.PutAll(o, own, mif: true));
        var acts = b.Node.HandleFrame(air.Now.Plus(10_000), -40, mif);
        Assert.Single(acts.OfType<NodeAction.PeerUp>());
        Assert.Equal(syn, Assert.Single(acts.OfType<NodeAction.Deliver>()).Packet);
        Assert.Equal(1ul, b.Node.Stats.SynReleased);
        _ = a;
    }

    [Fact] // spec: AWDL-19 (owly MAX_PEERS)
    public void ThePeerTable_IsBounded()
    {
        var node = new AwdlNode(new AwdlConfig { Addr = new MacAddr(2, 0, 0, 0, 0, 1), Name = "us", Radio = RadioConstraints.IntelExclusive, MaxPeers = 8, RssiThreshold = null },
            new AwdlTime(1));
        for (int i = 0; i < 20; i++)
            node.HandleFrame(new AwdlTime(10 + (ulong)i), -40, AwdlFrame.ActionFrame(new MacAddr(2, 1, 0, 0, 0, (byte)i), 1, ActionSubtype.Psf, 0, o => { }));
        Assert.Equal(8, node.Peers.Count());
        Assert.Equal(12ul, node.Stats.PeersEvicted);
    }

    [Fact] // spec: AWDL-20 (owly rssi threshold)
    public void FaintStrangers_AreIgnored()
    {
        var node = new AwdlNode(new AwdlConfig { Addr = new MacAddr(2, 0, 0, 0, 0, 1), Name = "us", Radio = RadioConstraints.IntelExclusive },
            new AwdlTime(1));
        node.HandleFrame(new AwdlTime(10), -80, AwdlFrame.ActionFrame(new MacAddr(2, 1, 0, 0, 0, 1), 1, ActionSubtype.Psf, 0, o => { }));
        Assert.Empty(node.Peers);
        node.HandleFrame(new AwdlTime(20), -50, AwdlFrame.ActionFrame(new MacAddr(2, 1, 0, 0, 0, 1), 2, ActionSubtype.Psf, 0, o => { }));
        Assert.Single(node.Peers);
    }
}
