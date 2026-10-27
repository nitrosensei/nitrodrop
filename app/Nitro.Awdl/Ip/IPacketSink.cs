// Nitro.Awdl: the host side of the AWDL link (the role of owly-awdl's driver::Netif).
// Copyright (c) 2026 Nitrobyte. Shape follows owly-awdl src/driver.rs (MIT, Copyright (c) 2026 T4T5).

namespace Nitro.Awdl.Ip;

/// <summary>
/// The host's end of the AWDL link: where raw IPv6 packets (from the IPv6 header on, no link-layer header) come from
/// and go to. <see cref="WintunPacketSink"/> is the awdl0-like Windows interface; <see cref="UserModePacketSink"/> is
/// the pure in-process alternative (tests, or a user-mode IP stack).
/// </summary>
public interface IPacketSink : IDisposable
{
    /// <summary>
    /// Signalled while packets may be waiting in <see cref="TryRead"/> (Wintun's read-wait event, or an equivalent).
    /// The host waits on it together with its timer.
    /// </summary>
    WaitHandle ReadWaitHandle { get; }

    /// <summary>The next IPv6 packet the host stack sends on the link, if one is waiting.</summary>
    bool TryRead(out byte[] packet);

    /// <summary>Hands the host stack a packet from a peer. One it can't take now is dropped.</summary>
    void Deliver(ReadOnlySpan<byte> packet);

    /// <summary>A peer turned valid and may be reached from now on (NET-2).</summary>
    void PeerUp(PeerInfo peer);

    /// <summary>A peer went away.</summary>
    void PeerDown(MacAddr mac);
}
