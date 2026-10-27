using Nitro.Continuity.Runtime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Nitro.Continuity.Platform;

public enum LinkPhase { Idle, NoBluetooth, Connecting, NoLeLink, Ready, Lost }

/// <summary>
/// Owns the chosen iPhone: the classic <see cref="BluetoothDevice"/> (RFCOMM: MAP/PBAP) and the LE device (GATT:
/// ANCS/AMS). Keeps the LE link up with <c>GattSession.MaintainConnection</c> and re-opens it with backoff when it drops.
/// <see cref="LeReady"/> fires every time GATT is usable again (subscribers re-subscribe; iOS re-sends PreExisting).
/// </summary>
public sealed class IphoneLink : IAsyncDisposable
{
    private readonly ReconnectBackoff _backoff = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _loop;
    private GattSession? _session;

    public PhoneDevice? Phone { get; private set; }
    public BluetoothDevice? Classic { get; private set; }
    public BluetoothLEDevice? Le { get; private set; }
    public LinkPhase Phase { get; private set; } = LinkPhase.Idle;
    public string? LastError { get; private set; }

    public event Action<LinkPhase>? PhaseChanged;
    public event Action<BluetoothLEDevice>? LeReady;
    public event Action? LeLost;

    /// <summary>Starts (or restarts) the connect loop for <paramref name="phone"/>.</summary>
    public async Task UseAsync(PhoneDevice phone, CancellationToken ct = default)
    {
        await StopAsync();
        Phone = phone;
        _loop = new CancellationTokenSource();
        _ = Task.Run(() => ConnectLoopAsync(_loop.Token), CancellationToken.None);
    }

    /// <summary>Adopts an LE device obtained by procedure B/C (already bonded) and attaches the keep-alive.</summary>
    public async Task AdoptLeAsync(BluetoothLEDevice le)
    {
        await _gate.WaitAsync();
        try { await AttachLeAsync(le); }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        _loop?.Cancel();
        _loop = null;
        await _gate.WaitAsync();
        try { DetachLe(); Classic?.Dispose(); Classic = null; SetPhase(LinkPhase.Idle); }
        finally { _gate.Release(); }
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool ready = false;
            await _gate.WaitAsync(ct);
            try
            {
                if (Le is { ConnectionStatus: BluetoothConnectionStatus.Connected }) ready = true;
                else ready = await TryConnectOnceAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { LastError = e.Message; }
            finally { _gate.Release(); }

            if (ready) { _backoff.Reset(); return; }   // ConnectionStatusChanged restarts the loop on loss
            try { await Task.Delay(_backoff.Next(), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<bool> TryConnectOnceAsync(CancellationToken ct)
    {
        var (present, on) = await IphonePairing.RadioStateAsync();
        if (!present || !on) { SetPhase(LinkPhase.NoBluetooth); return false; }
        if (Phone is null) return false;
        SetPhase(Phase == LinkPhase.Lost ? LinkPhase.Lost : LinkPhase.Connecting);

        Classic ??= Phone.Id.StartsWith("Bluetooth#", StringComparison.OrdinalIgnoreCase)
            ? await BluetoothDevice.FromIdAsync(Phone.Id).AsTask(ct)
            : Phone.Address != 0 ? await BluetoothDevice.FromBluetoothAddressAsync(Phone.Address).AsTask(ct) : null;

        if (Le is null)
        {
            var le = await IphonePairing.FindLeDeviceAsync(Phone, ct);
            if (le is null) { SetPhase(LinkPhase.NoLeLink); return false; }
            await AttachLeAsync(le);
        }
        // Touching GATT makes Windows open the link (MaintainConnection keeps it).
        var r = await Le!.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(ct);
        if (r.Status != GattCommunicationStatus.Success) { LastError = $"GATT {r.Status}"; return false; }
        foreach (var s in r.Services) s.Dispose();
        SetPhase(LinkPhase.Ready);
        LeReady?.Invoke(Le);
        return true;
    }

    private async Task AttachLeAsync(BluetoothLEDevice le)
    {
        DetachLe();
        Le = le;
        le.ConnectionStatusChanged += OnLeConnectionChanged;
        try
        {
            _session = await GattSession.FromDeviceIdAsync(le.BluetoothDeviceId);
            if (_session.CanMaintainConnection) _session.MaintainConnection = true;
        }
        catch (Exception e) { LastError = $"GattSession: {e.Message}"; }
    }

    private void DetachLe()
    {
        if (Le is not null) Le.ConnectionStatusChanged -= OnLeConnectionChanged;
        _session?.Dispose();
        _session = null;
        Le?.Dispose();
        Le = null;
    }

    private void OnLeConnectionChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected)
        {
            if (Phase != LinkPhase.Ready) { SetPhase(LinkPhase.Ready); LeReady?.Invoke(sender); }
            return;
        }
        SetPhase(LinkPhase.Lost);
        LeLost?.Invoke();
        // MaintainConnection usually brings it back by itself; the loop is the safety net (sleep/wake, out of range).
        if (_loop is { IsCancellationRequested: false } loop) _ = Task.Run(() => ConnectLoopAsync(loop.Token));
    }

    private void SetPhase(LinkPhase p)
    {
        if (Phase == p) return;
        Phase = p;
        PhaseChanged?.Invoke(p);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }
}
