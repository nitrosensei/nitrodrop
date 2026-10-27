// Nitro.Awdl: in-process packet sink (no Wintun, no admin).
// Copyright (c) 2026 Nitrobyte.

using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Nitro.Awdl.Ip;

/// <summary>
/// The pure user-mode <see cref="IPacketSink"/>: the "host stack" is whoever calls <see cref="Send"/> and reads
/// <see cref="Delivered"/>. Used by tests and by anything that runs its own IP/TCP in-process.
/// </summary>
public sealed class UserModePacketSink : IPacketSink
{
    private readonly ConcurrentQueue<byte[]> _outbound = new();
    private readonly ManualResetEvent _ready = new(false);
    private readonly Channel<byte[]> _delivered = Channel.CreateUnbounded<byte[]>();
    private readonly object _gate = new();

    public WaitHandle ReadWaitHandle => _ready;

    /// <summary>Packets AWDL delivered to this host, in order.</summary>
    public ChannelReader<byte[]> Delivered => _delivered.Reader;

    /// <summary>Peers currently up.</summary>
    public ConcurrentDictionary<MacAddr, PeerInfo> Up { get; } = new();

    public event Action<PeerInfo>? PeerCameUp;
    public event Action<MacAddr>? PeerWentDown;
    /// <summary>Raised on the AWDL host thread for each delivered packet (in addition to <see cref="Delivered"/>).</summary>
    public event Action<byte[]>? PacketDelivered;

    /// <summary>The local stack sends an IPv6 packet onto the link.</summary>
    public void Send(byte[] ipv6Packet)
    {
        lock (_gate)
        {
            _outbound.Enqueue(ipv6Packet);
            _ready.Set();
        }
    }

    public bool TryRead(out byte[] packet)
    {
        lock (_gate)
        {
            if (_outbound.TryDequeue(out packet!)) return true;
            _ready.Reset();
            return false;
        }
    }

    public void Deliver(ReadOnlySpan<byte> packet)
    {
        var copy = packet.ToArray();
        _delivered.Writer.TryWrite(copy);
        PacketDelivered?.Invoke(copy);
    }

    public void PeerUp(PeerInfo peer)
    {
        Up[peer.Mac] = peer;
        PeerCameUp?.Invoke(peer);
    }

    public void PeerDown(MacAddr mac)
    {
        Up.TryRemove(mac, out _);
        PeerWentDown?.Invoke(mac);
    }

    public void Dispose()
    {
        _delivered.Writer.TryComplete();
        _ready.Dispose();
    }
}
