// Nitro.Awdl: C# port of owly-awdl (https://github.com/t4t5/owly, crates/owly-awdl, commit 9a676e1).
// Copyright (c) 2026 T4T5 (Owly, MIT License). Port: Copyright (c) 2026 Nitrobyte. See LICENSE-OWLY.txt.
// Source: crates/owly-awdl/src/node.rs: an AWDL node as a sans-IO state machine driven by explicit time.
//
// It runs what legacy's daemon ran on its event loop: a PSF every 110 TU and a MIF in the middle of every slot,
// the peer table swept and the election run every second, the channel sequence derived from the sync master's,
// and data sent only in slots a peer shares with us, clear of the slot edges. Beyond legacy it holds a peer's TCP
// SYN until the peer is valid, and sends each valid peer an ICMPv6 echo now and then.

namespace Nitro.Awdl;

/// <summary>What a node asks of its host, in order (owly <c>Action</c>).</summary>
public abstract record NodeAction
{
    private NodeAction() { }

    /// <summary>Send <paramref name="Frame"/> now; <paramref name="At"/> is when the node decided to.</summary>
    public sealed record Transmit(AwdlTime At, TxFrame Frame) : NodeAction;

    /// <summary>Call back with a timer input at this time, replacing any earlier timer. Every input's actions end with one.</summary>
    public sealed record ArmTimer(AwdlTime At) : NodeAction;

    /// <summary>A peer turned valid: give it a neighbour entry (NET-2) before the packets that follow.</summary>
    public sealed record PeerUp(PeerInfo Peer) : NodeAction;

    /// <summary>A valid peer went quiet and was dropped.</summary>
    public sealed record PeerDown(MacAddr Mac) : NodeAction;

    /// <summary>Hand this packet to the IP stack.</summary>
    public sealed record Deliver(Ipv6Packet Packet) : NodeAction;
}

/// <summary>How the radio's channel relates to the slots it advertises.</summary>
public enum Presence
{
    /// <summary>
    /// The radio stays on a channel it shares with an AP and advertises the master's slots on it and no others
    /// (AWDL-12). Ranks masters counter-first, as legacy did in shared mode.
    /// </summary>
    Share,
    /// <summary>
    /// The radio stays on its channel and has it to itself (Owly's Intel "exclusive" mode): it advertises it in every
    /// slot, sends multicast in any slot and unicast in every slot a peer is there. Ranks masters metric-first (AWDL-9).
    /// </summary>
    Fixed,
}

/// <summary>How a radio is present on AWDL: the one channel it stays on, and how.</summary>
public sealed record RadioConstraints(Channel Channel, Presence Presence)
{
    /// <summary>Owly's iwlwifi profile (drivers.toml: mode = "exclusive", channels = [6]).</summary>
    public static RadioConstraints IntelExclusive => new(new Channel(6), Presence.Fixed);

    public Ranking Ranking => Presence == Presence.Share ? Ranking.CounterFirst : Ranking.MetricFirst;
}

/// <summary>A node's settings (owly <c>Config</c>, legacy's defaults).</summary>
public sealed record AwdlConfig
{
    /// <summary>The AWDL address (AWDL-2): our interface MAC, never changed while visible.</summary>
    public required MacAddr Addr { get; init; }
    /// <summary>The host name for the ARPA TLV.</summary>
    public required string Name { get; init; }
    public required RadioConstraints Radio { get; init; }
    /// <summary>The data rate towards peers that receive it (see <see cref="TxRate.ForPeer"/>).</summary>
    public TxRate Rate { get; init; } = TxRate.Basic;
    /// <summary>
    /// Send unicast once without waiting for an ACK. Default on for <see cref="Presence.Fixed"/> (legacy patch
    /// owl-05): a phone visits our channel for a slot or two, and a frame the hardware retries past the visit
    /// (15 times over 75-140 ms on an AX201) holds up every frame behind it.
    /// </summary>
    public bool? NoAck { get; init; }
    /// <summary>Accept a peer's Block Ack session (AWDL-15).</summary>
    public bool AcceptAddBa { get; init; }
    /// <summary>Send unicast to one peer as A-MSDUs up to its limit.</summary>
    public bool Amsdu { get; init; }
    /// <summary>The largest frame the radio takes, MAC header included.</summary>
    public int MaxFrame { get; init; } = 2304;
    /// <summary>Peers heard more weakly than this (dBm) aren't taken on; known ones are kept down to 5 dB below. Null = any.</summary>
    public sbyte? RssiThreshold { get; init; } = -65;
    /// <summary>Send each valid peer an ICMPv6 echo every few seconds.</summary>
    public bool NeighbourEcho { get; init; } = true;
    /// <summary>Most peers tracked.</summary>
    public int MaxPeers { get; init; } = AwdlNode.MaxPeersDefault;

    public bool EffectiveNoAck => NoAck ?? Radio.Presence == Presence.Fixed;
}

/// <summary>Counters, for diagnostics.</summary>
public sealed class NodeStats
{
    public ulong RxAction, RxData, RxMalformed, TxAction, TxUnicast, TxMulticast, TxAmsdu, Delivered,
        DroppedNoPeer, DroppedQueueFull, DroppedExpired, SynHeld, SynReleased, NoOverlap, PeersEvicted;

    public NodeStats Clone() => (NodeStats)MemberwiseClone();

    public override string ToString() =>
        $"tx_action {TxAction} tx_unicast {TxUnicast} tx_multicast {TxMulticast} tx_amsdu {TxAmsdu} | rx_action {RxAction} " +
        $"rx_data {RxData} rx_malformed {RxMalformed} delivered {Delivered} | dropped no_peer {DroppedNoPeer} queue {DroppedQueueFull} " +
        $"expired {DroppedExpired} | syn held {SynHeld} released {SynReleased} | no_overlap {NoOverlap} evicted {PeersEvicted}";
}

/// <summary>
/// An AWDL node. It does no I/O and reads no clock: the host feeds it received frames, outbound IPv6 packets and timer
/// expiries, each with the time, and carries out the <see cref="NodeAction"/>s it returns. Not thread-safe: drive it
/// from one thread (see <see cref="Host.AwdlHost"/>).
/// </summary>
public sealed class AwdlNode
{
    /// <summary>Announced in the Version TLV: macOS, as legacy announced.</summary>
    private const byte Devclass = DeviceClass.MacOS;
    private static readonly HtCapabilities Ht = HtCapabilities.OneStream;
    /// <summary>PSFs every 110 TU (§3.2).</summary>
    public const ushort PsfIntervalTu = 110;
    /// <summary>Peers unheard for 5 s are dropped (legacy patch owl-07 raised it from 2).</summary>
    public const ulong PeerTimeoutUs = 5_000_000;
    /// <summary>How often the peer table is swept and the election run.</summary>
    public const ulong TickUs = 1_000_000;
    /// <summary>How often each valid peer gets an ICMPv6 echo.</summary>
    public const ulong EchoUs = 3_000_000;
    /// <summary>The echoes' identifier and payload (owly's, so captures compare).</summary>
    public const ushort EchoId = 0x4f77;
    public static ReadOnlySpan<byte> EchoPayload => "owly"u8;
    /// <summary>Packets waiting longer than this for their slot are dropped.</summary>
    public const ulong PacketExpiryUs = 2_000_000;
    /// <summary>A held SYN waits this long for its peer to turn valid (AWDL-18).</summary>
    public const ulong SynHoldUs = 2_000_000;
    /// <summary>Unicast packets waiting for their peer's slot at most; a host should stop reading while backlog is this long.</summary>
    public const int UnicastQueue = 128;
    private const int MulticastQueue = 16;
    private const int HeldSyns = 128;
    /// <summary>Most peers tracked by default; a new one beyond this replaces the one heard from longest ago (SEC-11).</summary>
    public const int MaxPeersDefault = 512;
    /// <summary>Frames sent for one input at most; the rest follow half a millisecond later.</summary>
    private const int Burst = 64;
    private const ulong BurstPauseUs = 500;
    private const int AmsduPackets = 8;
    private const int AmsduDefaultMax = 3839;

    private sealed record Queued(Ipv6Packet Packet, AwdlTime At);

    private readonly AwdlConfig _cfg;
    private AwdlClock _clock;
    private Election _election;
    private ChannelSequence _sequence;
    private readonly SortedDictionary<MacAddr, Peer> _peers = new();
    private LinkedList<Queued> _unicast = new();
    private readonly LinkedList<Queued> _multicast = new();
    private LinkedList<Queued> _held = new();
    private ushort _seq, _awdlSeq, _echoSeq;
    private AwdlTime _nextPsf, _nextMif, _nextTick, _nextEcho;
    private AwdlTime? _resume;
    private (MacAddr Master, byte Channel)? _noOverlap;
    private readonly NodeStats _stats = new();

    public AwdlNode(AwdlConfig cfg, AwdlTime now)
    {
        _cfg = cfg;
        _clock = new AwdlClock(now);
        _election = Election.OwnState(cfg.Addr);
        _sequence = ChannelSequence.Fixed(cfg.Radio.Channel);
        _nextPsf = now.Plus(PsfIntervalTu * Awdl.TU);
        _nextMif = now;
        _nextTick = now.Plus(TickUs);
        _nextEcho = now.Plus(EchoUs);
    }

    public AwdlConfig Config => _cfg;
    public IEnumerable<Peer> Peers => _peers.Values;
    public Peer? PeerOf(MacAddr mac) => _peers.GetValueOrDefault(mac);
    public Election Election => _election;
    /// <summary>The channel sequence it advertises.</summary>
    public ChannelSequence Sequence => _sequence;
    public AwdlClock Clock => _clock;
    public NodeStats Stats => _stats;
    /// <summary>Unicast packets waiting for their peer's slot (see <see cref="UnicastQueue"/>).</summary>
    public int Backlog => _unicast.Count;
    /// <summary>Shared radio and the master's sequence has no slot on our channel: the master and its main channel.</summary>
    public (MacAddr Master, byte Channel)? NoOverlap => _noOverlap;

    // ---------------- inputs ----------------

    /// <summary>A frame the radio received at <paramref name="rxTime"/>, from its MAC header on, without FCS.</summary>
    public List<NodeAction> HandleFrame(AwdlTime rxTime, sbyte? rssi, ReadOnlyMemory<byte> bytes)
    {
        var o = new List<NodeAction>();
        Receive(rxTime, rssi, bytes, o);
        return Finish(rxTime, o);
    }

    /// <summary>The timer the node asked for expired, or just the time now.</summary>
    public List<NodeAction> HandleTimer(AwdlTime now) => Finish(now, []);

    /// <summary>A packet the IP stack wants sent.</summary>
    public List<NodeAction> HandleOutbound(AwdlTime now, Ipv6Packet packet)
    {
        var o = new List<NodeAction>();
        Enqueue(now, packet, o);
        return Finish(now, o);
    }

    private List<NodeAction> Finish(AwdlTime now, List<NodeAction> o)
    {
        Service(now, o);
        o.Add(new NodeAction.ArmTimer(Deadline(now)));
        return o;
    }

    /// <summary>Whether a frame to <paramref name="to"/> may go now (for a host retrying one the radio refused).</summary>
    public bool MaySend(MacAddr to, AwdlTime now) => SendWait(to, now) == 0;

    /// <summary>How long until a frame to <paramref name="to"/> may go, 0 for now; null when <paramref name="to"/> is no longer a peer.</summary>
    public ulong? SendWait(MacAddr to, AwdlTime now)
    {
        if (to.IsMulticast) return MulticastWait(now);
        return _peers.TryGetValue(to, out var p) ? UnicastWait(p, now) : null;
    }

    private ushort NextSeq()
    {
        var s = _seq;
        _seq = (ushort)((s + 1) & 0x0fff);
        return s;
    }

    private bool IsFixed => _cfg.Radio.Presence == Presence.Fixed;

    private void Receive(AwdlTime now, sbyte? rssi, ReadOnlyMemory<byte> bytes, List<NodeAction> o)
    {
        ReceivedFrame parsed;
        try { parsed = AwdlFrame.Parse(bytes); }
        catch (AwdlFormatException)
        {
            _stats.RxMalformed++;
            return;
        }
        switch (parsed)
        {
            case ReceivedFrame.Action a when a.Header.Src != _cfg.Addr:
                RxAction(now, rssi, a.Header.Src, a.Subtype, a.Tlvs, o);
                break;
            case ReceivedFrame.AddBaRequest r when _cfg.AcceptAddBa && r.Header.Dst == _cfg.Addr && _peers.ContainsKey(r.Header.Src):
                var seq = NextSeq();
                var resp = AwdlFrame.AddBaResponse(_cfg.Addr, r.Header.Src, seq, r.Token, r.Params, r.Timeout);
                o.Add(new NodeAction.Transmit(now, new TxFrame(resp, r.Header.Src, TxRate.Basic, false)));
                break;
            case ReceivedFrame.Data d when d.Header.Src != _cfg.Addr:
                RxData(now, d.Header, d.Amsdu, d.Body, o);
                break;
        }
    }

    private void RxAction(AwdlTime now, sbyte? rssi, MacAddr src, byte subtype, ReadOnlyMemory<byte> tlvs, List<NodeAction> o)
    {
        bool known = _peers.ContainsKey(src);
        if (_cfg.RssiThreshold is { } threshold && rssi is { } r)
        {
            int floor = known ? threshold - 5 : threshold;
            if (r < floor) return;
        }
        Advert advert;
        try { advert = Advert.Parse(tlvs); } // parsed whole before anything changes
        catch (AwdlFormatException)
        {
            _stats.RxMalformed++;
            return;
        }
        if (!known && _peers.Count >= Math.Max(_cfg.MaxPeers, 1)) EvictStalest(o);
        _stats.RxAction++;
        bool ours = _election.Sync == src;
        if (!_peers.TryGetValue(src, out var peer))
        {
            peer = new Peer(src, now);
            _peers[src] = peer;
        }
        peer.LastSeen = now;
        if (rssi.HasValue) peer.Rssi = rssi;
        bool up = peer.Absorb(advert, subtype == ActionSubtype.Mif);
        if (advert.Sync is { } s)
        {
            peer.SyncOffsetUs = _clock.ErrorTu(now, s.NextTu, s.AwCounter) * (long)Awdl.TU;
            if (ours) _clock.Update(now, s.NextTu, s.AwCounter); // AWDL-7: follow the master's schedule
        }
        if (up)
        {
            o.Add(new NodeAction.PeerUp(peer.Info()));
            ReleaseSyns(src, o);
        }
    }

    private void RxData(AwdlTime now, FrameHeader header, bool amsdu, ReadOnlyMemory<byte> body, List<NodeAction> o)
    {
        List<Msdu> msdus;
        try { msdus = AwdlFrame.Msdus(header, amsdu, body); }
        catch (AwdlFormatException)
        {
            _stats.RxMalformed++;
            return;
        }
        _stats.RxData++;
        bool known = false, valid = false;
        if (_peers.TryGetValue(header.Src, out var p))
        {
            // Data proves a peer is still there: an iPhone deep in a transfer can go seconds between the action
            // frames we hear (legacy patch owl-07).
            p.LastSeen = now;
            known = true;
            valid = p.Valid;
        }
        foreach (var m in msdus)
        {
            // NET-3: IPv6 only. Never another node's unicast, which a monitor hears too.
            if (m.Ethertype != AwdlFrame.EthertypeIpv6 || !(m.Dst == _cfg.Addr || m.Dst.IsMulticast)) continue;
            var packet = new Ipv6Packet(m.Dst, m.Src, m.Payload.ToArray());
            if (valid)
            {
                if (packet.Dst == _cfg.Addr && packet.IsEchoReply(EchoId)) continue;
                _stats.Delivered++;
                o.Add(new NodeAction.Deliver(packet));
            }
            else if (packet.IsBareSynTo(_cfg.Addr))
            {
                // A phone can open its connection before the MIF that makes it valid (AWDL-18).
                HoldSyn(now, packet);
            }
            else if (known)
            {
                _stats.Delivered++; // legacy handed on data from any peer in its table
                o.Add(new NodeAction.Deliver(packet));
            }
        }
    }

    private void HoldSyn(AwdlTime now, Ipv6Packet packet)
    {
        foreach (var q in _held) if (q.Packet.Equals(packet)) return;
        if (_held.Count == HeldSyns) _held.RemoveFirst();
        _stats.SynHeld++;
        _held.AddLast(new Queued(packet, now));
    }

    private void ReleaseSyns(MacAddr src, List<NodeAction> o)
    {
        var held = _held;
        _held = new LinkedList<Queued>();
        foreach (var q in held)
        {
            if (q.Packet.Src == src)
            {
                _stats.SynReleased++;
                _stats.Delivered++;
                o.Add(new NodeAction.Deliver(q.Packet));
            }
            else _held.AddLast(q);
        }
    }

    private void Enqueue(AwdlTime now, Ipv6Packet packet, List<NodeAction> o)
    {
        if (packet.Dst == _cfg.Addr)
        {
            o.Add(new NodeAction.Deliver(packet));
            return;
        }
        LinkedList<Queued> queue;
        if (packet.Dst.IsMulticast)
        {
            if (_multicast.Count == MulticastQueue)
            {
                _multicast.RemoveFirst();
                _stats.DroppedQueueFull++;
            }
            queue = _multicast;
        }
        else
        {
            if (!_peers.ContainsKey(packet.Dst))
            {
                _stats.DroppedNoPeer++;
                return;
            }
            if (_unicast.Count == UnicastQueue)
            {
                _unicast.RemoveFirst();
                _stats.DroppedQueueFull++;
            }
            queue = _unicast;
        }
        queue.AddLast(new Queued(packet, now));
    }

    private void Service(AwdlTime now, List<NodeAction> o)
    {
        if (now >= _nextTick)
        {
            Tick(now, o);
            while (_nextTick <= now) _nextTick = _nextTick.Plus(TickUs);
        }
        if (now >= _nextPsf)
        {
            o.Add(new NodeAction.Transmit(now, BuildActionFrame(now, ActionSubtype.Psf)));
            ulong interval = PsfIntervalTu * Awdl.TU;
            while (_nextPsf <= now) _nextPsf = _nextPsf.Plus(interval);
        }
        if (now >= _nextMif)
        {
            // In the middle of the slot.
            o.Add(new NodeAction.Transmit(now, BuildActionFrame(now, ActionSubtype.Mif)));
            _nextMif = now.Plus(_clock.NextSlotUs(now) + AwdlSchedule.SlotUs / 2);
        }
        if (_cfg.NeighbourEcho && now >= _nextEcho)
        {
            _nextEcho = now.Plus(EchoUs);
            _echoSeq++;
            var macs = _peers.Values.Where(p => p.Valid).Select(p => p.Mac).ToList();
            foreach (var mac in macs)
                Enqueue(now, Ipv6Packet.EchoRequest(_cfg.Addr, mac, EchoId, _echoSeq, EchoPayload), o);
        }
        _resume = null;
        int sent = 0;
        SendMulticast(now, ref sent, o);
        SendUnicast(now, ref sent, o);
        if (sent >= Burst) _resume = now.Plus(BurstPauseUs);
    }

    /// <summary>Drops the peer heard from longest ago; the election runs without it at the next tick.</summary>
    private void EvictStalest(List<NodeAction> o)
    {
        Peer? stalest = null;
        foreach (var p in _peers.Values)
            if (stalest == null || p.LastSeen < stalest.LastSeen) stalest = p;
        if (stalest == null) return;
        _peers.Remove(stalest.Mac);
        _stats.PeersEvicted++;
        if (stalest.Valid) o.Add(new NodeAction.PeerDown(stalest.Mac));
    }

    private void Tick(AwdlTime now, List<NodeAction> o)
    {
        foreach (var p in _peers.Values.Where(p => now.Since(p.LastSeen) > PeerTimeoutUs).ToList())
        {
            _peers.Remove(p.Mac);
            if (p.Valid) o.Add(new NodeAction.PeerDown(p.Mac));
        }
        // Packets for a peer that's gone never go, and would hold up the host until they expired.
        int before = _unicast.Count;
        _unicast = new LinkedList<Queued>(_unicast.Where(q => _peers.ContainsKey(q.Packet.Dst)));
        _stats.DroppedNoPeer += (ulong)(before - _unicast.Count);
        before = _unicast.Count + _multicast.Count;
        _unicast = new LinkedList<Queued>(_unicast.Where(q => now.Since(q.At) < PacketExpiryUs));
        RemoveWhere(_multicast, q => now.Since(q.At) >= PacketExpiryUs);
        _stats.DroppedExpired += (ulong)(before - _unicast.Count - _multicast.Count);
        RemoveWhere(_held, q => now.Since(q.At) >= SynHoldUs);
        _election.Run(_peers.Values.Where(p => p.Valid).Select(p => p.Election).ToList(), _cfg.Radio.Ranking);
        AdoptSequence();
    }

    private static void RemoveWhere(LinkedList<Queued> list, Func<Queued, bool> pred)
    {
        for (var n = list.First; n != null;)
        {
            var next = n.Next;
            if (pred(n.Value)) list.Remove(n);
            n = next;
        }
    }

    /// <summary>The sequence to advertise: derived from the sync master's, as the radio's presence allows.</summary>
    private void AdoptSequence()
    {
        var home = _cfg.Radio.Channel;
        ChannelSequence? master = _election.IsMaster ? null : _peers.GetValueOrDefault(_election.Sync)?.Sequence;
        _noOverlap = null;
        if (_cfg.Radio.Presence == Presence.Fixed || master == null)
        {
            _sequence = ChannelSequence.Fixed(home);
            return;
        }
        var (ours, kept) = master.Intersect(home.Number);
        if (kept == 0)
        {
            // Rather than announce we're never available, which would have the phone stop addressing us, keep what we had.
            _stats.NoOverlap++;
            _noOverlap = (_election.Sync, master.Dominant(0));
            return;
        }
        _sequence = ours;
    }

    private TxFrame BuildActionFrame(AwdlTime now, byte subtype)
    {
        var seq = NextSeq();
        var own = new OwnAdvert
        {
            Addr = _cfg.Addr,
            Name = _cfg.Name,
            DeviceClass = Devclass,
            Channel = _cfg.Radio.Channel.Number,
            MasterChannel = _cfg.Radio.Channel,
            NextTu = _clock.NextSlotTu(now),
            AwCounter = _clock.CurrentAw(now),
            PsfIntervalTu = PsfIntervalTu,
            Sequence = _sequence,
            Election = _election,
            Ht = Ht,
        };
        bool mif = subtype == ActionSubtype.Mif;
        var bytes = AwdlFrame.ActionFrame(_cfg.Addr, seq, subtype, (uint)now.Micros, o => AwdlTlv.PutAll(o, own, mif));
        _stats.TxAction++;
        return new TxFrame(bytes, MacAddr.Broadcast, TxRate.Basic, false);
    }

    /// <summary>The channel we're on in slot <paramref name="slot"/> as far as sending goes.</summary>
    private byte OwnChannel(int slot) => IsFixed ? _cfg.Radio.Channel.Number : _sequence.ChannelAt(slot);

    private bool SameChannel(Peer peer, AwdlTime t)
    {
        int slot = _clock.Slot(t);
        byte own = OwnChannel(slot);
        byte theirs = peer.Sequence?.ChannelAt(slot) ?? 0;
        return own != 0 && own == theirs;
    }

    /// <summary>
    /// AWDL-16: how long until unicast to <paramref name="peer"/> may go: in a slot both are on the same channel, clear
    /// of the 3 TU guard at its edges unless the neighbouring slot is shared too.
    /// </summary>
    private ulong UnicastWait(Peer peer, AwdlTime now)
    {
        ulong next = _clock.NextSlotUs(now);
        if (!SameChannel(peer, now)) return next;
        const ulong g = AwdlSchedule.UnicastGuardUs;
        if (next < g) return SameChannel(peer, now.Plus(AwdlSchedule.SlotUs)) ? 0 : next + g;
        ulong into = AwdlSchedule.SlotUs - next;
        if (into < g) return SameChannel(peer, now.Minus(AwdlSchedule.SlotUs)) ? 0 : g - into;
        return 0;
    }

    /// <summary>
    /// AWDL-16: how long until multicast may go: slots 0 and 10 clear of a 16 TU guard; a fixed radio in any slot with
    /// the unicast guard, since the first AW may be all a peer spends on its channel (legacy patch owl-03).
    /// </summary>
    private ulong MulticastWait(AwdlTime now)
    {
        ulong guard = IsFixed ? AwdlSchedule.UnicastGuardUs : AwdlSchedule.MulticastGuardUs;
        ulong wait = _clock.EdgeWait(now, guard);
        if (wait > 0 || IsFixed || _clock.Slot(now) is 0 or 10) return wait;
        return _clock.NextSlotUs(now) + guard;
    }

    private ushort TakeAwdlSeq(int n)
    {
        var s = _awdlSeq;
        _awdlSeq = (ushort)(_awdlSeq + n);
        return s;
    }

    private void SendMulticast(AwdlTime now, ref int sent, List<NodeAction> o)
    {
        while (sent < Burst && _multicast.Count > 0 && MulticastWait(now) == 0)
        {
            var q = _multicast.First!.Value;
            _multicast.RemoveFirst();
            var bytes = AwdlFrame.DataFrame(_cfg.Addr, q.Packet.Dst, NextSeq(), TakeAwdlSeq(1), q.Packet.Data);
            _stats.TxMulticast++;
            sent++;
            o.Add(new NodeAction.Transmit(now, new TxFrame(bytes, q.Packet.Dst, _cfg.Rate.ForPeer(null), false)));
        }
    }

    private void SendUnicast(AwdlTime now, ref int sent, List<NodeAction> o)
    {
        if (_unicast.Count == 0) return;
        var waiting = new LinkedList<Queued>();
        var items = _unicast;
        _unicast = new LinkedList<Queued>();
        var blocked = new HashSet<MacAddr>();
        while (items.First is { } node)
        {
            var q = node.Value;
            items.RemoveFirst();
            var dst = q.Packet.Dst;
            bool open = sent < Burst && !blocked.Contains(dst)
                        && _peers.TryGetValue(dst, out var pp) && UnicastWait(pp, now) == 0;
            if (!open)
            {
                blocked.Add(dst);
                waiting.AddLast(q);
                continue;
            }
            var peer = _peers[dst];
            var rate = _cfg.Rate.ForPeer(peer.Ht);
            var batch = new List<Queued> { q };
            if (_cfg.Amsdu && peer.Ht.HasValue)
            {
                int limit = Math.Min(peer.Ht?.MaxAmsdu ?? AmsduDefaultMax, Math.Max(_cfg.MaxFrame - (AwdlFrame.HeaderLen + 2), 0));
                int len = AwdlFrame.AmsduSubframeLen(q.Packet.Data.Length);
                var rest = new LinkedList<Queued>();
                while (items.First is { } n2)
                {
                    var next = n2.Value;
                    items.RemoveFirst();
                    int add = AwdlFrame.AmsduSubframeLen(next.Packet.Data.Length) + 3;
                    if (next.Packet.Dst == dst && batch.Count < AmsduPackets && len + add <= limit)
                    {
                        len += add;
                        batch.Add(next);
                    }
                    else rest.AddLast(next);
                }
                items = rest;
            }
            var seq = NextSeq();
            var awdlSeq = TakeAwdlSeq(batch.Count);
            byte[] bytes;
            if (batch.Count == 1) bytes = AwdlFrame.DataFrame(_cfg.Addr, dst, seq, awdlSeq, batch[0].Packet.Data);
            else
            {
                _stats.TxAmsdu++;
                bytes = AwdlFrame.AmsduFrame(_cfg.Addr, dst, seq, awdlSeq, batch.Select(b => b.Packet.Data).ToList());
            }
            _stats.TxUnicast += (ulong)batch.Count;
            sent++;
            o.Add(new NodeAction.Transmit(now, new TxFrame(bytes, dst, rate, _cfg.EffectiveNoAck)));
        }
        _unicast = waiting;
    }

    /// <summary>When to be called back.</summary>
    private AwdlTime Deadline(AwdlTime now)
    {
        var t = AwdlTime.Min(_nextTick, AwdlTime.Min(_nextPsf, _nextMif));
        if (_cfg.NeighbourEcho) t = AwdlTime.Min(t, _nextEcho);
        if (_resume is { } r) t = AwdlTime.Min(t, r);
        if (_multicast.Count > 0) t = AwdlTime.Min(t, now.Plus(Math.Max(MulticastWait(now), 1)));
        var seen = new HashSet<MacAddr>();
        foreach (var q in _unicast)
        {
            if (!seen.Add(q.Packet.Dst)) continue;
            if (_peers.TryGetValue(q.Packet.Dst, out var p))
                t = AwdlTime.Min(t, now.Plus(Math.Max(UnicastWait(p, now), 1)));
        }
        return AwdlTime.Max(t, now);
    }
}
