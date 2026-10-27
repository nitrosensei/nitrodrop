// Nitro.Awdl: C# port of owly-awdl (https://github.com/t4t5/owly, crates/owly-awdl, commit 9a676e1).
// Copyright (c) 2026 T4T5 (Owly, MIT License). Port: Copyright (c) 2026 Nitrobyte. See LICENSE-OWLY.txt.
// Source: crates/owly-awdl/src/peer.rs: the peer table's entries.

namespace Nitro.Awdl;

/// <summary>An AWDL peer, as heard.</summary>
public sealed class Peer
{
    public MacAddr Mac { get; }
    /// <summary>From its ARPA TLV.</summary>
    public string Name { get; internal set; } = "";
    public byte Version { get; internal set; }
    public byte DeviceClass { get; internal set; }
    /// <summary>Of the last action frame heard from it, in dBm.</summary>
    public sbyte? Rssi { get; internal set; }
    /// <summary>When it was last heard, by action or data frame.</summary>
    public AwdlTime LastSeen { get; internal set; }
    public Election Election;
    public ChannelSequence? Sequence { get; internal set; }
    /// <summary>How far its AW schedule is ahead of ours, in µs.</summary>
    public long? SyncOffsetUs { get; internal set; }
    public HtCapabilities? Ht { get; internal set; }
    /// <summary>Its station address on its infrastructure network.</summary>
    public MacAddr? Infra { get; internal set; }
    /// <summary>Whether it has sent a MIF.</summary>
    public bool Mif { get; internal set; }
    /// <summary>Whether it sends Election Parameters v2, which then wins over v1.</summary>
    public bool V2 { get; internal set; }
    /// <summary>Sent a MIF and announced version + device class: only then a peer to talk to (AWDL-17).</summary>
    public bool Valid { get; internal set; }

    public Peer(MacAddr mac, AwdlTime now)
    {
        Mac = mac;
        LastSeen = now;
        Election = Election.OwnState(mac);
    }

    public PeerInfo Info() => new(Mac, Name, DeviceClass, Rssi);

    /// <summary>Takes in one action frame's TLVs (except sync, read against our clock). Returns whether it just turned valid.</summary>
    internal bool Absorb(Advert a, bool mif)
    {
        if (a.Name is { } name) Name = name;
        if (a.Version is { } v)
        {
            Version = v.Version;
            DeviceClass = v.DeviceClass;
        }
        if (a.Sequence is { } s) Sequence = s;
        if (a.Ht is { } ht) Ht = ht;
        if (a.DataPath?.Infra is { } infra) Infra = infra;
        if (a.Election is { } e && (a.ElectionV2 || !V2))
        {
            Election.Master = e.Master;
            Election.Height = e.Height;
            Election.MasterMetric = e.MasterMetric;
            Election.SelfMetric = e.SelfMetric;
            if (a.ElectionV2)
            {
                Election.Sync = e.Sync ?? Mac;
                Election.MasterCounter = e.MasterCounter;
                Election.SelfCounter = e.SelfCounter;
                V2 = true;
            }
        }
        Mif |= mif;
        bool was = Valid;
        Valid = Mif && Version != 0 && DeviceClass != 0;
        return Valid && !was;
    }
}

/// <summary>What a host is told about a peer that has turned valid.</summary>
public sealed record PeerInfo(MacAddr Mac, string Name, byte DeviceClass, sbyte? Rssi);
