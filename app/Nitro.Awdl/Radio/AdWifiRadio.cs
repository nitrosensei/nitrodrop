// Nitro.Awdl: IAwdlRadio over our KMDF Intel AX203 driver (driver/, IOCTL ABI in driver/include/airdrop_ioctl.h).
// Copyright (c) 2026 Nitrobyte.

using System.Runtime.Versioning;
using Nitro.Awdl.Interop;

namespace Nitro.Awdl.Radio;

/// <summary>Options for <see cref="AdWifiRadio.OpenAsync"/>.</summary>
public sealed record AdWifiRadioOptions
{
    /// <summary>Owly's iwlwifi profile: channel 6 (drivers.toml).</summary>
    public Channel Channel { get; init; } = new(6);
    /// <summary>
    /// M2 monitor only (LISTENER MAC context, no ACKs). Default false = M3 IBSS cell on AWDL's BSSID with our MAC, so
    /// the firmware ACKs unicast addressed to us: "a monitor alone on the AX201 never sees an ACK" (owly setup.rs).
    /// </summary>
    public bool ListenerOnly { get; init; }
    /// <summary>IBSS: receive only our BSS. Default false: keep promiscuous reception, as Owly's monitor beside the cell.</summary>
    public bool NoPromisc { get; init; }
    /// <summary>IBSS beacon interval; 0 = driver default 10000 TU (Owly's cell interval: beacons almost never go).</summary>
    public ushort BeaconIntervalTu { get; init; }
    /// <summary>Explicit device interface path; null = first present GUID_DEVINTERFACE_AIRDROP_WIFI.</summary>
    public string? DevicePath { get; init; }
    /// <summary>READ_FRAME requests kept outstanding (the header asks for several).</summary>
    public int ReadsOutstanding { get; init; } = 8;
    /// <summary>
    /// Ask the driver for a TX_RESULT per frame (request pends until the firmware reports) so in-flight frames are
    /// counted exactly against the driver's limit of 64 and ACK/retry stats are kept. False = fire-and-forget.
    /// </summary>
    public bool TrackTxResults { get; init; } = true;
    /// <summary>
    /// The driver honours NOACK only for unicast QoS data; owly sends plain data frames. True = rewrite NOACK plain
    /// data into QoS data (TID 0) before injecting. UNVERIFIED that iOS accepts it (iPhones send QoS data themselves).
    /// </summary>
    public bool UpgradeNoAckDataToQos { get; init; } = true;
    public Action<string>? Log { get; init; }
}

/// <summary>Counters kept by <see cref="AdWifiRadio"/>.</summary>
public sealed record AdWifiRadioStats(long RxFrames, long RxDroppedByDriver, long RxDiscarded, long TxSubmitted, long TxSuccess,
    long TxFailed, long TxFlushed, long TxNoAckNotHonored, long TxErrors, long TxBusy);

/// <summary>
/// The radio over our driver's IOCTLs: GET_INFO for the MAC, START (IBSS on 00:25:00:ff:94:73, fixed channel),
/// overlapped READ_FRAME (several outstanding, delivered in issue order), INJECT_FRAME with optional TX_RESULT.
/// Requires the driver installed and an elevated process (device ACL: SYSTEM + Administrators).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AdWifiRadio : IAwdlRadio
{
    private readonly OverlappedDevice _dev;
    private readonly AdWifiRadioOptions _o;
    private readonly System.Threading.Channels.Channel<RxFrame> _rx =
        System.Threading.Channels.Channel.CreateBounded<RxFrame>(new System.Threading.Channels.BoundedChannelOptions(1024)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _readers = [];
    private readonly object _order = new();
    private long _issueSeq, _deliverSeq;
    private readonly SortedDictionary<long, RxFrame?> _reorder = new();
    private int _txInFlight;
    private long _rxFrames, _rxDroppedDriver, _rxDiscarded, _txSubmitted, _txSuccess, _txFailed, _txFlushed, _txNoAckNotHonored, _txErrors, _txBusy;
    private ulong _lastTsf, _lastTsfHostUs;
    private bool _haveTsf;
    private bool _loggedTxError;

    private AdWifiRadio(OverlappedDevice dev, AirdropInfo info, AdWifiRadioOptions o)
    {
        _dev = dev;
        _o = o;
        Info = info;
        Address = info.MacAddress;
        Channel = o.Channel;
    }

    /// <summary>GET_INFO at open.</summary>
    public AirdropInfo Info { get; private set; }

    public MacAddr Address { get; }
    public Channel Channel { get; private set; }

    /// <summary>Present device interface paths of our driver.</summary>
    public static IReadOnlyList<string> FindDevices() => DeviceNative.InterfacePaths(AirdropIoctl.DeviceInterface);

    /// <summary>Opens the device, reads GET_INFO, starts the radio and the RX readers.</summary>
    public static async Task<AdWifiRadio> OpenAsync(AdWifiRadioOptions? options = null, CancellationToken ct = default)
    {
        var o = options ?? new AdWifiRadioOptions();
        var path = o.DevicePath ?? FindDevices().FirstOrDefault()
                   ?? throw new FileNotFoundException("No airdrop-win Wi-Fi device (GUID_DEVINTERFACE_AIRDROP_WIFI): is the driver installed and bound?");
        var dev = new OverlappedDevice(DeviceNative.Open(path));
        try
        {
            var info = AirdropInfo.Parse(await dev.IoctlAsync(AirdropIoctl.GetInfo, null, AirdropIoctl.InfoSize, ct).ConfigureAwait(false));
            if (info.AbiVersion < 1 || info.AbiVersion > AirdropIoctl.AbiVersion)
                o.Log?.Invoke($"driver ABI {info.AbiVersion}, binding written for {AirdropIoctl.AbiVersion}: continuing with the common prefix");
            if (info.RfKill) throw new InvalidOperationException("Wi-Fi hardware radio switch is off (RF kill)");
            var radio = new AdWifiRadio(dev, info, o);
            await radio.StartRadioAsync(o.Channel, ct).ConfigureAwait(false);
            radio.StartReaders();
            return radio;
        }
        catch
        {
            dev.Dispose();
            throw;
        }
    }

    private async Task StartRadioAsync(Channel channel, CancellationToken ct)
    {
        uint flags = 0;
        if (_o.ListenerOnly) flags |= AirdropIoctl.StartListenerOnly;
        if (_o.NoPromisc) flags |= AirdropIoctl.StartNoPromisc;
        var p = new AirdropStartParams(flags, channel.Number, Awdl.Bssid, _o.BeaconIntervalTu);
        await _dev.IoctlAsync(AirdropIoctl.Start, p.ToBytes(), 0, ct).ConfigureAwait(false);
        Channel = channel;
        _o.Log?.Invoke($"radio on: {channel}, {(_o.ListenerOnly ? "listener" : "IBSS")} as {Address}");
    }

    /// <summary>Re-reads GET_INFO (driver counters).</summary>
    public async Task<AirdropInfo> RefreshInfoAsync(CancellationToken ct = default) =>
        Info = AirdropInfo.Parse(await _dev.IoctlAsync(AirdropIoctl.GetInfo, null, AirdropIoctl.InfoSize, ct).ConfigureAwait(false));

    /// <summary>STOP + START on the new channel: no in-place retune IOCTL in ABI v2.</summary>
    public async Task SetChannelAsync(Channel channel, CancellationToken ct = default)
    {
        await _dev.IoctlAsync(AirdropIoctl.Stop, null, 0, ct).ConfigureAwait(false);
        await StartRadioAsync(channel, ct).ConfigureAwait(false);
    }

    public ulong NowMicros() => HostClock.NowMicros();

    public bool TryGetTsf(out ulong tsf)
    {
        // No GET_TSF IOCTL in ABI v2: extrapolate from the last RX TSF stamp (same µs rate as the host clock).
        lock (_order)
        {
            tsf = _haveTsf ? _lastTsf + (NowMicros() - _lastTsfHostUs) : 0;
            return _haveTsf;
        }
    }

    public ValueTask<RxFrame> ReceiveAsync(CancellationToken ct) => _rx.Reader.ReadAsync(ct);

    private void StartReaders()
    {
        for (int i = 0; i < Math.Max(1, _o.ReadsOutstanding); i++) _readers.Add(Task.Run(ReaderLoop));
    }

    private async Task ReaderLoop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            long seq;
            Task<byte[]> io;
            lock (_order)
            {
                seq = _issueSeq++;
                io = _dev.IoctlAsync(AirdropIoctl.ReadFrame, null, AirdropIoctl.RxBufferSize, ct);
            }
            RxFrame? frame = null;
            try
            {
                frame = Convert(AirdropRxFrame.Parse(await io.ConfigureAwait(false)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (InvalidDataException e)
            {
                Interlocked.Increment(ref _rxDiscarded);
                _o.Log?.Invoke($"bad READ_FRAME reply: {e.Message}");
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                _o.Log?.Invoke($"READ_FRAME failed: {e.Message}");
                _rx.Writer.TryComplete(e);
                Deliver(seq, null);
                return;
            }
            Deliver(seq, frame);
        }
    }

    /// <summary>Completions arrive on pool threads in any order; frames go out in the order the requests were issued.</summary>
    private void Deliver(long seq, RxFrame? frame)
    {
        lock (_order)
        {
            _reorder[seq] = frame;
            while (_reorder.TryGetValue(_deliverSeq, out var f))
            {
                _reorder.Remove(_deliverSeq);
                _deliverSeq++;
                if (f != null) _rx.Writer.TryWrite(f);
            }
        }
    }

    private RxFrame? Convert(AirdropRxFrame r)
    {
        Interlocked.Increment(ref _rxFrames);
        if (r.DroppedSinceLast > 0) Interlocked.Add(ref _rxDroppedDriver, r.DroppedSinceLast);
        if (r.Truncated || r.BadFcs || r.Frame.Length == 0)
        {
            Interlocked.Increment(ref _rxDiscarded);
            return null;
        }
        ulong host = HostClock.IsInterruptTime ? HostClock.FromInterruptTime100ns(r.HostTimestamp) : NowMicros();
        if (r.TsfValid)
            lock (_order)
            {
                _lastTsf = r.Tsf;
                _lastTsfHostUs = host;
                _haveTsf = true;
            }
        return new RxFrame
        {
            Bytes = r.Frame,
            Rssi = r.RssiDbm,
            HostTimeUs = host,
            Tsf = r.TsfValid ? r.Tsf : null,
            Channel = r.Channel,
            RateNFlags = r.RateNFlags,
            DroppedBefore = r.DroppedSinceLast,
        };
    }

    public bool TrySend(TxFrame frame)
    {
        if (_cts.IsCancellationRequested) return true;
        if (Interlocked.Increment(ref _txInFlight) > AirdropIoctl.MaxTxInFlight)
        {
            Interlocked.Decrement(ref _txInFlight);
            return false;
        }
        var bytes = frame.NoAck && _o.UpgradeNoAckDataToQos ? AirdropTx.UpgradeDataToQos(frame.Bytes) : frame.Bytes;
        byte[] request;
        try { request = AirdropTx.Build(bytes, frame.Rate, frame.NoAck); }
        catch (ArgumentException e)
        {
            Interlocked.Decrement(ref _txInFlight);
            Interlocked.Increment(ref _txErrors);
            _o.Log?.Invoke($"frame not sent: {e.Message}");
            return true;
        }
        Interlocked.Increment(ref _txSubmitted);
        _ = InjectAsync(request);
        return true;
    }

    private async Task InjectAsync(byte[] request)
    {
        try
        {
            var reply = await _dev.IoctlAsync(AirdropIoctl.InjectFrame, request, _o.TrackTxResults ? AirdropIoctl.TxResultSize : 0, _cts.Token)
                .ConfigureAwait(false);
            if (reply.Length >= AirdropIoctl.TxResultSize)
            {
                var r = AirdropTxResult.Parse(reply);
                switch (r.Status)
                {
                    case AirdropIoctl.TxStSuccess or AirdropIoctl.TxStNoStatus: Interlocked.Increment(ref _txSuccess); break;
                    case AirdropIoctl.TxStFailed: Interlocked.Increment(ref _txFailed); break;
                    case AirdropIoctl.TxStFlushed: Interlocked.Increment(ref _txFlushed); break;
                }
                if ((r.Flags & AirdropIoctl.TxrNoAckNotHonored) != 0) Interlocked.Increment(ref _txNoAckNotHonored);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { Interlocked.Increment(ref _txBusy); }
        catch (Exception e)
        {
            Interlocked.Increment(ref _txErrors);
            if (!_loggedTxError)
            {
                _loggedTxError = true;
                _o.Log?.Invoke($"INJECT_FRAME failed: {e.Message}");
            }
        }
        finally
        {
            Interlocked.Decrement(ref _txInFlight);
        }
    }

    public AdWifiRadioStats Stats => new(Interlocked.Read(ref _rxFrames), Interlocked.Read(ref _rxDroppedDriver), Interlocked.Read(ref _rxDiscarded),
        Interlocked.Read(ref _txSubmitted), Interlocked.Read(ref _txSuccess), Interlocked.Read(ref _txFailed), Interlocked.Read(ref _txFlushed),
        Interlocked.Read(ref _txNoAckNotHonored), Interlocked.Read(ref _txErrors), Interlocked.Read(ref _txBusy));

    /// <summary>Stops the radio (STOP) and closes the device; closing the last handle also stops it.</summary>
    public async ValueTask DisposeAsync()
    {
        try { await _dev.IoctlAsync(AirdropIoctl.Stop, null, 0).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or TimeoutException or NotSupportedException or System.ComponentModel.Win32Exception) { }
        _cts.Cancel();
        try { await Task.WhenAll(_readers).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException) { }
        _rx.Writer.TryComplete();
        _dev.Dispose();
        _cts.Dispose();
    }
}
