// Nitro.Awdl: radio abstraction (the role of owly-awdl's driver::Air, extended with the metadata our driver gives).
// Copyright (c) 2026 Nitrobyte. Shape follows owly-awdl src/driver.rs (MIT, Copyright (c) 2026 T4T5).

namespace Nitro.Awdl.Radio;

/// <summary>A frame heard on the air, from its 802.11 MAC header on, without FCS.</summary>
public sealed record RxFrame
{
    public required byte[] Bytes { get; init; }
    /// <summary>dBm, when the radio reports it.</summary>
    public sbyte? Rssi { get; init; }
    /// <summary>
    /// When the frame arrived, in the radio's host clock (<see cref="IAwdlRadio.NowMicros"/>). Our driver stamps it in
    /// the ISR path with KeQueryInterruptTimePrecise, so it carries no user-mode scheduling latency.
    /// </summary>
    public required ulong HostTimeUs { get; init; }
    /// <summary>TSF at the start of the frame, if the radio reported one.</summary>
    public ulong? Tsf { get; init; }
    /// <summary>Channel the frame was received on (0 = unknown).</summary>
    public byte Channel { get; init; }
    /// <summary>Raw firmware rate_n_flags (driver specific), 0 if unknown.</summary>
    public uint RateNFlags { get; init; }
    /// <summary>Frames the radio lost before this one.</summary>
    public uint DroppedBefore { get; init; }
}

/// <summary>
/// Where AWDL frames go out and come in: a radio that injects and captures raw 802.11 frames on one channel.
/// Implementations: <see cref="AdWifiRadio"/> (our KMDF driver over IOCTLs) and <see cref="SimulatedRadio"/>.
/// </summary>
public interface IAwdlRadio : IAsyncDisposable
{
    /// <summary>The radio's MAC, which becomes the AWDL address (AWDL-2).</summary>
    MacAddr Address { get; }

    /// <summary>The channel the radio is on.</summary>
    Channel Channel { get; }

    /// <summary>Retunes the radio. On our driver this restarts the radio (STOP + START) and takes milliseconds.</summary>
    Task SetChannelAsync(Channel channel, CancellationToken ct = default);

    /// <summary>The host clock that <see cref="RxFrame.HostTimeUs"/> is in, µs, monotonic.</summary>
    ulong NowMicros();

    /// <summary>The radio's TSF now, if it can tell (exact or extrapolated from the last RX stamp).</summary>
    bool TryGetTsf(out ulong tsf);

    /// <summary>The next frame heard. Cancelling loses no frame.</summary>
    ValueTask<RxFrame> ReceiveAsync(CancellationToken ct);

    /// <summary>
    /// Hands a frame to the radio without waiting, at its rate and with or without an ACK as it says. Returns false
    /// when the radio's queue is full (try again later); a frame refused for any other reason is the radio's to log,
    /// and counts as sent.
    /// </summary>
    bool TrySend(TxFrame frame);
}
