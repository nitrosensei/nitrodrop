// Nitro.Awdl: C# port of owly-awdl's driver (https://github.com/t4t5/owly, crates/owly-awdl/src/driver.rs,
// commit 9a676e1). Copyright (c) 2026 T4T5 (Owly, MIT License). Port: Copyright (c) 2026 Nitrobyte. See LICENSE-OWLY.txt.
//
// Owly runs the node on its own thread with a single-threaded tokio runtime. Here: a dedicated thread (optionally
// MMCSS "Pro Audio", priority Highest) that waits on the radio RX event, the packet sink's read event and a
// high-resolution waitable timer, so nothing else in the process delays its timers. It records how late they fire.

using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Nitro.Awdl.Interop;
using Nitro.Awdl.Ip;
using Nitro.Awdl.Radio;

namespace Nitro.Awdl.Host;

/// <summary>Knobs for <see cref="AwdlHost"/>.</summary>
public sealed record AwdlHostOptions
{
    /// <summary>Use the radio's RX timestamp (ISR time on our driver) as the frame's time instead of the time it was read.</summary>
    public bool UseRxTimestamps { get; init; } = true;
    /// <summary>High-resolution waitable timer for the node's deadlines (Windows 10 1803+).</summary>
    public bool HighResolutionTimer { get; init; } = true;
    /// <summary>Busy-wait the last microseconds before a deadline (0 = off).</summary>
    public ulong SpinUs { get; init; }
    /// <summary>Register the loop thread with MMCSS (task "Pro Audio") for scheduling priority. No admin needed.</summary>
    public bool Mmcss { get; init; } = true;
    public ThreadPriority Priority { get; init; } = ThreadPriority.Highest;
    public Action<string>? Log { get; init; }
}

/// <summary>Timer lateness and radio refusals (owly driver.rs Lateness/TxStats).</summary>
public sealed record HostStats(
    ulong TimerFires, ulong TimerLateAvgUs, ulong TimerLateMaxUs, ulong RadioFull, ulong GaveUp,
    ulong OutboundNotAwdl, ulong OutboundForeignSource, NodeStats Node);

/// <summary>
/// Runs an <see cref="AwdlNode"/> between an <see cref="IAwdlRadio"/> and an <see cref="IPacketSink"/> on a thread of
/// its own: radio frames in, sink packets in, timers; transmits, deliveries and peer up/down out.
/// </summary>
public sealed class AwdlHost : IAsyncDisposable
{
    /// <summary>
    /// Unicast the radio couldn't take is kept for its peer's next slot, as legacy kept it (patch owl-07: dropped, 8383
    /// frames in one 34 MB receive on an Intel card, among them the TCP ACKs the phone was waiting for), for at most this long.
    /// </summary>
    private const ulong UnicastHoldUs = 2_000_000;
    /// <summary>Multicast the radio couldn't take is tried again while its slot lasts, for at most this long.</summary>
    private const ulong RetryUs = 100_000;
    private const ulong RetryPauseUs = 500;
    private const int MaxOutboundPerWake = 64;

    private readonly AwdlConfig _cfg;
    private readonly IAwdlRadio _radio;
    private readonly IPacketSink _sink;
    private readonly AwdlHostOptions _o;
    private readonly ConcurrentQueue<RxFrame> _rx = new();
    private readonly AutoResetEvent _rxEvent = new(false);
    private readonly ManualResetEvent _stopEvent = new(false);
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private Task? _rxPump;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Loop-thread state.
    private AwdlNode? _node;
    private readonly LinkedList<(AwdlTime At, TxFrame Frame)> _unicastRetry = new();
    private readonly LinkedList<(AwdlTime At, TxFrame Frame)> _multicastRetry = new();
    private bool _full;
    private AwdlTime _timer;
    private ulong _lateCount, _lateTotal, _lateMax, _radioFull, _gaveUp, _notAwdl, _foreignSrc;
    private (MacAddr, byte)? _overlapLogged;

    private volatile IReadOnlyList<PeerInfo> _peers = [];
    private volatile HostStats? _stats;

    public AwdlHost(AwdlConfig cfg, IAwdlRadio radio, IPacketSink sink, AwdlHostOptions? options = null)
    {
        if (cfg.Addr != radio.Address)
            throw new ArgumentException($"AWDL-2: the AWDL address ({cfg.Addr}) must be the radio's MAC ({radio.Address})");
        _cfg = cfg;
        _radio = radio;
        _sink = sink;
        _o = options ?? new AwdlHostOptions();
    }

    /// <summary>Valid peers now (snapshot).</summary>
    public IReadOnlyList<PeerInfo> Peers => _peers;

    /// <summary>Diagnostics snapshot, refreshed every loop iteration.</summary>
    public HostStats? Stats => _stats;

    /// <summary>Why the host stopped by itself (radio or sink failed), once it has.</summary>
    public Exception? Failure { get; private set; }

    public event Action<PeerInfo>? PeerUp;
    public event Action<MacAddr>? PeerDown;
    public event Action<Exception>? Failed;

    /// <summary>Starts the RX pump and the loop thread.</summary>
    public Task StartAsync()
    {
        if (_thread != null) throw new InvalidOperationException("Already started");
        _rxPump = Task.Run(RxPumpAsync);
        _thread = new Thread(Loop) { IsBackground = true, Name = "nitro-awdl", Priority = _o.Priority };
        _thread.Start();
        return _started.Task;
    }

    private async Task RxPumpAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var f = await _radio.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                _rx.Enqueue(f);
                _rxEvent.Set();
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (System.Threading.Channels.ChannelClosedException) when (_cts.IsCancellationRequested) { }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    private void Fail(Exception e)
    {
        if (Failure != null) return;
        Failure = e;
        _o.Log?.Invoke($"AWDL stopped: {e.Message}");
        _stopEvent.Set();
        Failed?.Invoke(e);
    }

    private AwdlTime Now() => new(_radio.NowMicros());

    private void Loop()
    {
        nint mmcss = 0;
        if (_o.Mmcss && OperatingSystem.IsWindows()) mmcss = EnterMmcss();
        using var waiter = new PreciseWaiter(_o.HighResolutionTimer) { SpinUs = _o.SpinUs };
        try
        {
            _node = new AwdlNode(_cfg, Now());
            Handle(_node.HandleTimer(Now()));
            _started.TrySetResult();
            var handles = new WaitHandle[3];
            while (!_stopEvent.WaitOne(0))
            {
                ulong wake = _timer.Micros;
                if (RetryAt() is { } r && r < wake) wake = r;
                handles[0] = _stopEvent;
                handles[1] = _rxEvent;
                int n = 2;
                if (TakesOutbound) handles[n++] = _sink.ReadWaitHandle;
                waiter.WaitUntil(handles.AsSpan(0, n), wake, _radio.NowMicros);
                if (_stopEvent.WaitOne(0)) break;

                while (_rx.TryDequeue(out var f))
                {
                    var now = Now();
                    var at = _o.UseRxTimestamps && f.HostTimeUs != 0 ? AwdlTime.Min(new AwdlTime(f.HostTimeUs), now) : now;
                    Handle(_node.HandleFrame(at, f.Rssi, f.Bytes));
                }
                for (int i = 0; i < MaxOutboundPerWake && TakesOutbound && _sink.TryRead(out var pkt); i++)
                    Outbound(pkt);
                var t = Now();
                if (t >= _timer)
                {
                    ulong late = t.Since(_timer);
                    _lateCount++;
                    _lateTotal += late;
                    _lateMax = Math.Max(_lateMax, late);
                    Handle(_node.HandleTimer(t));
                }
                FlushRetries();
                Snapshot();
            }
        }
        catch (Exception e)
        {
            _started.TrySetException(e);
            Fail(e);
        }
        finally
        {
            if (mmcss != 0 && OperatingSystem.IsWindows()) TimingNative.AvRevertMmThreadCharacteristics(mmcss);
        }
    }

    [SupportedOSPlatform("windows")]
    private nint EnterMmcss()
    {
        try
        {
            uint index = 0;
            var h = TimingNative.AvSetMmThreadCharacteristics("Pro Audio", ref index);
            if (h == 0) _o.Log?.Invoke("MMCSS registration refused; running at normal class");
            return h;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0;
        }
    }

    private void Outbound(byte[] data)
    {
        // Wintun is L3: link-layer addresses follow from the IPv6 ones (NET-2). Packets the OS sends from anything but
        // our EUI-64 link-local (DAD from ::, a random link-local, a global address) can't be answered over AWDL.
        var p = Ipv6Packet.FromIp(data);
        if (p == null)
        {
            _notAwdl++;
            return;
        }
        if (p.Src != _cfg.Addr)
        {
            _foreignSrc++;
            return;
        }
        Handle(_node!.HandleOutbound(Now(), p));
    }

    private void Handle(List<NodeAction> actions)
    {
        foreach (var a in actions)
        {
            switch (a)
            {
                case NodeAction.Transmit t: Transmit(t.At, t.Frame); break;
                case NodeAction.ArmTimer at: _timer = at.At; break;
                case NodeAction.PeerUp up:
                    _o.Log?.Invoke($"add peer {up.Peer.Mac} ({up.Peer.Name})");
                    _sink.PeerUp(up.Peer);
                    PublishPeers();
                    PeerUp?.Invoke(up.Peer);
                    break;
                case NodeAction.PeerDown down:
                    _o.Log?.Invoke($"remove peer {down.Mac}");
                    _sink.PeerDown(down.Mac);
                    PublishPeers();
                    PeerDown?.Invoke(down.Mac);
                    break;
                case NodeAction.Deliver d: _sink.Deliver(d.Packet.Data); break;
            }
        }
        LogOverlap();
    }

    private void LogOverlap()
    {
        var now = _node!.NoOverlap;
        if (now == _overlapLogged) return;
        if (now is { } o) _o.Log?.Invoke($"NO OVERLAP with {o.Master} on ch {_cfg.Radio.Channel.Number} - peer wants ch {o.Channel}");
        else _o.Log?.Invoke("overlap with the master's sequence again");
        _overlapLogged = now;
    }

    private void Transmit(AwdlTime at, TxFrame frame)
    {
        if (frame.IsAction)
        {
            // Built for this moment (sync TLVs, timestamp): stale by the next try, and the next PSF/MIF is near.
            if (!_radio.TrySend(frame))
            {
                _full = true;
                _radioFull++;
                _gaveUp++;
            }
            return;
        }
        var queue = frame.To.IsMulticast ? _multicastRetry : _unicastRetry;
        if (queue.Count > 0)
        {
            queue.AddLast((at, frame)); // in order behind those already waiting
            return;
        }
        if (!_radio.TrySend(frame))
        {
            _full = true;
            _radioFull++;
            queue.AddLast((at, frame));
        }
    }

    /// <summary>Tries again what the radio had no room for, while it may still go, until the radio refuses again.</summary>
    private void FlushRetries()
    {
        var now = Now();
        var node = _node!;
        _full = false;
        while (_multicastRetry.First is { } m)
        {
            var (at, frame) = m.Value;
            _multicastRetry.RemoveFirst();
            if (now.Since(at) > RetryUs || !node.MaySend(frame.To, now))
            {
                _gaveUp++;
                continue;
            }
            if (!_radio.TrySend(frame))
            {
                _multicastRetry.AddFirst((at, frame));
                _full = true;
                _radioFull++;
                break;
            }
        }
        var kept = new LinkedList<(AwdlTime, TxFrame)>();
        while (_unicastRetry.First is { } u)
        {
            var (at, frame) = u.Value;
            _unicastRetry.RemoveFirst();
            var wait = node.SendWait(frame.To, now);
            if (wait == null || now.Since(at) > UnicastHoldUs) _gaveUp++;
            else if (wait == 0 && !_full)
            {
                if (!_radio.TrySend(frame))
                {
                    _full = true;
                    _radioFull++;
                    kept.AddLast((at, frame));
                }
            }
            else kept.AddLast((at, frame));
        }
        foreach (var k in kept) _unicastRetry.AddLast(k);
    }

    /// <summary>When to try the waiting frames again, if any are.</summary>
    private ulong? RetryAt()
    {
        var now = Now();
        if (_full || _multicastRetry.Count > 0) return now.Micros + RetryPauseUs;
        ulong? min = null;
        foreach (var (_, f) in _unicastRetry)
        {
            ulong w = _node!.SendWait(f.To, now) ?? 0;
            min = min is { } m ? Math.Min(m, w) : w;
        }
        return min is { } x ? now.Micros + Math.Max(x, 1) : null;
    }

    /// <summary>Not while as many as the node queues wait already: the host stack's queue holds them meanwhile.</summary>
    private bool TakesOutbound => _node!.Backlog + _unicastRetry.Count < AwdlNode.UnicastQueue;

    private void PublishPeers() => _peers = _node!.Peers.Where(p => p.Valid).Select(p => p.Info()).ToList();

    private void Snapshot() =>
        _stats = new HostStats(_lateCount, _lateCount == 0 ? 0 : _lateTotal / _lateCount, _lateMax, _radioFull, _gaveUp,
            _notAwdl, _foreignSrc, _node!.Stats.Clone());

    /// <summary>Stops the loop and the RX pump. The radio and the sink stay open (the caller owns them).</summary>
    public async ValueTask DisposeAsync()
    {
        _stopEvent.Set();
        _cts.Cancel();
        if (_thread != null && Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(5));
        if (_rxPump != null)
        {
            try { await _rxPump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        _cts.Dispose();
        _rxEvent.Dispose();
        _stopEvent.Dispose();
    }
}
