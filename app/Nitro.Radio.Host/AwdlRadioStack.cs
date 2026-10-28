using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using Nitro.Awdl;
using Nitro.Awdl.Host;
using Nitro.Awdl.Ip;
using Nitro.Awdl.Ipc;
using Nitro.Awdl.Radio;

namespace Nitro.Radio.Host;

/// <summary>
/// The real stack: our driver's radio (IBSS on channel 6, Intel exclusive), the "NitroDrop AWDL" Wintun adapter
/// (opened, or created when missing, then configured: fe80::EUI-64, MTU 1450, no DAD) and the AWDL host between them
/// (docs/awdl-port.md, "Entegrasyon").
/// </summary>
internal sealed class AwdlRadioStack : IRadioStack
{
    private static readonly TimeSpan InterfaceUpTimeout = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private AdWifiRadio? _radio;
    private WintunAdapter? _adapter;
    private WintunPacketSink? _sink;
    private AwdlHost? _host;

    public async Task StartAsync(RadioMessage start, Action<RadioStatus> report, Action<string> log, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_host != null) { report(Ready()); return; }

            var radio = _radio = await OpenRadioAsync(start, log, ct);
            log($"radio: {radio.Address}, ch {radio.Channel.Number}, firmware state {radio.Info.State}");

            var adapter = _adapter = OpenAdapter(log);
            try { adapter.ApplyConfiguration(radio.Address, log); }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception)
            {
                throw new RadioStackException(RadioFailureKind.Network, e.Message, e);
            }
            try { _sink = new WintunPacketSink(adapter); }
            catch (Win32Exception e) { throw new RadioStackException(RadioFailureKind.Network, e.Message, e); }

            var host = _host = new AwdlHost(new AwdlConfig
            {
                Addr = radio.Address,
                Name = string.IsNullOrWhiteSpace(start.Name) ? Environment.MachineName : start.Name!,
                Radio = RadioConstraints.IntelExclusive,
            }, radio, _sink, new AwdlHostOptions { Log = log });
            host.PeerUp += _ => report(Ready());
            host.PeerDown += _ => report(Ready());
            host.Failed += e => report(RadioStatus.Failed(RadioFailureKind.Radio, e.Message));
            try { await host.StartAsync().WaitAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new RadioStackException(RadioFailureKind.Radio, e.Message, e);
            }

            await WaitInterfaceUpAsync(adapter.InterfaceIndex, radio.Address.LinkLocal(), ct);
            log($"ready: {adapter.Name} (#{adapter.InterfaceIndex}) {radio.Address.LinkLocal()}");
            report(Ready());
        }
        finally { _gate.Release(); }
    }

    private static async Task<AdWifiRadio> OpenRadioAsync(RadioMessage start, Action<string> log, CancellationToken ct)
    {
        try
        {
            return await AdWifiRadio.OpenAsync(new AdWifiRadioOptions
            {
                Channel = RadioConstraints.IntelExclusive.Channel,
                ListenerOnly = start.ListenerOnly == true,
                Log = log,
            }, ct);
        }
        catch (FileNotFoundException e) { throw new RadioStackException(RadioFailureKind.DriverMissing, e.Message, e); }
        catch (UnauthorizedAccessException e) { throw new RadioStackException(RadioFailureKind.AccessDenied, e.Message, e); }
        catch (IOException e) when (e.Message.Contains("already open", StringComparison.Ordinal))
        {
            throw new RadioStackException(RadioFailureKind.AccessDenied, e.Message, e);
        }
        catch (InvalidOperationException e) when (e.Message.Contains("RF kill", StringComparison.Ordinal))
        {
            throw new RadioStackException(RadioFailureKind.RfKill, e.Message, e);
        }
        catch (Exception e) when (e is not OperationCanceledException and not RadioStackException)
        {
            throw new RadioStackException(RadioFailureKind.Radio, e.Message, e);
        }
    }

    /// <summary>Opens "NitroDrop AWDL", or creates it (installs the Wintun driver on first use).</summary>
    private static WintunAdapter OpenAdapter(Action<string> log)
    {
        try
        {
            try { return WintunAdapter.Open(); }
            catch (Win32Exception)
            {
                log($"creating Wintun adapter '{WintunAdapter.DefaultName}' (Wintun {WintunAdapter.PinnedVersion})");
                return WintunAdapter.Create();
            }
        }
        catch (DllNotFoundException e) { throw new RadioStackException(RadioFailureKind.WintunMissing, e.Message, e); }
        catch (Win32Exception e) { throw new RadioStackException(RadioFailureKind.Network, e.Message, e); }
    }

    /// <summary>Waits until Windows reports the adapter up with our link-local on it.</summary>
    private static async Task WaitInterfaceUpAsync(int index, IPAddress linkLocal, CancellationToken ct)
    {
        var until = DateTime.UtcNow + InterfaceUpTimeout;
        while (true)
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Supports(NetworkInterfaceComponent.IPv6) && n.GetIPProperties().GetIPv6Properties()?.Index == index);
            if (nic is { OperationalStatus: OperationalStatus.Up }
                && nic.GetIPProperties().UnicastAddresses.Any(u => new IPAddress(u.Address.GetAddressBytes()).Equals(linkLocal)))
                return;
            if (DateTime.UtcNow > until)
                throw new RadioStackException(RadioFailureKind.Network,
                    $"interface #{index} did not come up with {linkLocal} (state {nic?.OperationalStatus.ToString() ?? "missing"})");
            await Task.Delay(250, ct);
        }
    }

    private RadioStatus Ready()
    {
        var radio = _radio;
        var adapter = _adapter;
        var host = _host;
        if (radio == null || adapter == null || host == null) return RadioStatus.Stopped();
        if (host.Failure is { } f) return RadioStatus.Failed(RadioFailureKind.Radio, f.Message);
        return new RadioStatus
        {
            State = RadioState.Ready,
            Channel = radio.Channel.Number,
            Peers = host.Peers.Select(p => new RadioPeer(p.Mac.ToString(), p.Name, p.Rssi)).ToList(),
            Interface = adapter.Name,
            InterfaceIndex = adapter.InterfaceIndex,
            Mac = radio.Address.ToString(),
            Address = radio.Address.LinkLocal().ToString(),
        };
    }

    /// <summary>AWDL off, sink and adapter closed (a Wintun adapter we created disappears), radio STOP + close.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_host is { } host) { _host = null; await host.DisposeAsync(); }
            if (_sink is { } sink) { _sink = null; sink.Dispose(); }
            if (_adapter is { } adapter) { _adapter = null; adapter.Dispose(); }
            if (_radio is { } radio) { _radio = null; await radio.DisposeAsync(); }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }
}
