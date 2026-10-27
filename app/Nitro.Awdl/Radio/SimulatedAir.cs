// Nitro.Awdl: an in-memory "air" connecting several simulated radios, for tests and demos.
// Copyright (c) 2026 Nitrobyte. Modelled on owly-awdl tests/sim/mod.rs (MIT, Copyright (c) 2026 T4T5): frames are
// heard by every other radio on the same channel, after a little airtime, with an optional share lost.

using System.Diagnostics;
using System.Threading.Channels;

namespace Nitro.Awdl.Radio;

/// <summary>
/// A real-time shared medium. Every <see cref="SimulatedRadio"/> joined to it hears frames sent by the others on the
/// same channel. The clock is a <see cref="Stopwatch"/> shared by all radios, so one host's timestamps make sense to
/// another. For deterministic virtual-time simulation drive <see cref="AwdlNode"/>s directly (see the tests).
/// </summary>
public sealed class SimulatedAir
{
    private readonly object _gate = new();
    private readonly List<SimulatedRadio> _radios = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _rng;

    /// <param name="lossPermille">Out of 1000 frames, how many each receiver misses.</param>
    /// <param name="seed">Seed for the loss generator.</param>
    public SimulatedAir(int lossPermille = 0, int seed = 1)
    {
        LossPermille = lossPermille;
        _rng = new Random(seed);
    }

    public int LossPermille { get; set; }

    /// <summary>Simulated airtime added to each frame's receive stamp.</summary>
    public ulong AirtimeUs { get; set; } = 200;

    /// <summary>Microseconds since the air was created (+1, so 0 never appears).</summary>
    public ulong NowMicros() => (ulong)(_clock.ElapsedTicks * 1_000_000.0 / Stopwatch.Frequency) + 1;

    /// <summary>Frames sent through the air, all radios.</summary>
    public long FramesSent;

    /// <summary>Adds a radio with this MAC on <paramref name="channel"/>.</summary>
    public SimulatedRadio Join(MacAddr mac, Channel channel)
    {
        var r = new SimulatedRadio(this, mac, channel);
        lock (_gate) _radios.Add(r);
        return r;
    }

    internal void Leave(SimulatedRadio r)
    {
        lock (_gate) _radios.Remove(r);
    }

    internal void Transmit(SimulatedRadio from, TxFrame frame)
    {
        Interlocked.Increment(ref FramesSent);
        ulong at = NowMicros() + AirtimeUs;
        SimulatedRadio[] targets;
        lock (_gate) targets = [.. _radios];
        foreach (var r in targets)
        {
            if (ReferenceEquals(r, from) || r.Channel != from.Channel) continue;
            bool lost;
            lock (_rng) lost = LossPermille > 0 && _rng.Next(1000) < LossPermille;
            if (lost) continue;
            r.Hear(new RxFrame { Bytes = frame.Bytes, Rssi = -40, HostTimeUs = at, Channel = from.Channel.Number });
        }
    }
}

/// <summary>A radio on a <see cref="SimulatedAir"/>.</summary>
public sealed class SimulatedRadio : IAwdlRadio
{
    private readonly SimulatedAir _air;
    private readonly Channel<RxFrame> _rx = System.Threading.Channels.Channel.CreateUnbounded<RxFrame>(
        new UnboundedChannelOptions { SingleReader = true });
    private volatile bool _disposed;

    internal SimulatedRadio(SimulatedAir air, MacAddr mac, Channel channel)
    {
        _air = air;
        Address = mac;
        Channel = channel;
    }

    public MacAddr Address { get; }
    public Channel Channel { get; private set; }

    /// <summary>When set, <see cref="TrySend"/> reports a full queue (to test the host's retry path).</summary>
    public Func<TxFrame, bool>? RefuseWhen { get; set; }

    /// <summary>Every frame this radio sent, for assertions.</summary>
    public long Sent;

    public Task SetChannelAsync(Channel channel, CancellationToken ct = default)
    {
        Channel = channel;
        return Task.CompletedTask;
    }

    public ulong NowMicros() => _air.NowMicros();

    public bool TryGetTsf(out ulong tsf)
    {
        tsf = _air.NowMicros(); // one shared timebase stands in for a TSF
        return true;
    }

    public ValueTask<RxFrame> ReceiveAsync(CancellationToken ct) => _rx.Reader.ReadAsync(ct);

    public bool TrySend(TxFrame frame)
    {
        if (_disposed) return true;
        if (RefuseWhen?.Invoke(frame) == true) return false;
        Interlocked.Increment(ref Sent);
        _air.Transmit(this, frame);
        return true;
    }

    internal void Hear(RxFrame f)
    {
        if (!_disposed) _rx.Writer.TryWrite(f);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _air.Leave(this);
        _rx.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
