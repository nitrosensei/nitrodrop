using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using Nitro.Continuity.Ancs;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Nitro.Continuity.Platform;

public enum GattStartResult
{
    Ok,
    /// <summary>Service not listed: "Sistem Bildirimlerini Paylaş" off, or no LE bond yet.</summary>
    ServiceMissing,
    /// <summary>Insufficient authentication/encryption: needs (LE) pairing.</summary>
    NeedsPairing,
    Unreachable,
    Failed,
}

/// <summary>
/// ANCS GATT consumer: Data Source then Notification Source subscriptions (spec order), Control Point writes queued and
/// sent with response, everything fed to <see cref="AncsNotificationCenter"/>. GATT callbacks arrive on the thread pool;
/// one lock keeps the state machine single-threaded.
/// </summary>
public sealed class AncsClient : IAsyncDisposable
{
    private readonly object _feed = new();
    private readonly Channel<(byte[] Data, TaskCompletionSource<bool>? Done)> _writes =
        Channel.CreateUnbounded<(byte[], TaskCompletionSource<bool>?)>(new UnboundedChannelOptions { SingleReader = true });
    private GattDeviceService? _service;
    private GattCharacteristic? _ns, _cp, _ds;
    private CancellationTokenSource? _cts;

    public AncsClient()
    {
        Center.WriteControlPoint += bytes => _writes.Writer.TryWrite((bytes, null));
    }

    public AncsNotificationCenter Center { get; } = new();
    public bool IsRunning => _cts is { IsCancellationRequested: false };

    /// <summary>The Data Source stream did not match our requests (another consumer, e.g. Phone Link) — state was reset.</summary>
    public event Action<Exception>? StreamError;

    public async Task<GattStartResult> StartAsync(BluetoothLEDevice le, CancellationToken ct = default)
    {
        await StopAsync();
        var sr = await le.GetGattServicesForUuidAsync(AncsUuids.Service, BluetoothCacheMode.Uncached).AsTask(ct);
        if (sr.Status == GattCommunicationStatus.AccessDenied) return GattStartResult.NeedsPairing;
        if (sr.Status == GattCommunicationStatus.Unreachable) return GattStartResult.Unreachable;
        if (sr.Status != GattCommunicationStatus.Success) return Insufficient(sr.ProtocolError) ? GattStartResult.NeedsPairing : GattStartResult.Failed;
        if (sr.Services.Count == 0) return GattStartResult.ServiceMissing;
        _service = sr.Services[0];

        _ns = await Char(AncsUuids.NotificationSource, ct);
        _cp = await Char(AncsUuids.ControlPoint, ct);
        _ds = await Char(AncsUuids.DataSource, ct);
        if (_ns is null || _cp is null || _ds is null) { await StopAsync(); return GattStartResult.NeedsPairing; }

        Center.Reset();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => WriterLoopAsync(_cts.Token), CancellationToken.None);

        _ds.ValueChanged += OnDataSource;
        _ns.ValueChanged += OnNotificationSource;
        // Spec: Data Source first, otherwise the first answers (PreExisting burst) can be lost.
        foreach (var c in new[] { _ds, _ns })
        {
            var w = await c.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct);
            if (w.Status != GattCommunicationStatus.Success)
            {
                await StopAsync();
                return Insufficient(w.ProtocolError) || w.Status == GattCommunicationStatus.AccessDenied
                    ? GattStartResult.NeedsPairing : GattStartResult.Failed;
            }
        }
        return GattStartResult.Ok;
    }

    private async Task<GattCharacteristic?> Char(Guid uuid, CancellationToken ct)
    {
        var r = await _service!.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached).AsTask(ct);
        return r.Status == GattCommunicationStatus.Success && r.Characteristics.Count > 0 ? r.Characteristics[0] : null;
    }

    private static bool Insufficient(byte? attError) => attError is 0x05 or 0x08 or 0x0C or 0x0F;  // auth / authz / key size / encryption

    private void OnNotificationSource(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = args.CharacteristicValue.ToArray();
        lock (_feed)
        {
            try { Center.OnNotificationSource(data); }
            catch (FormatException e) { StreamError?.Invoke(e); }
        }
    }

    private void OnDataSource(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = args.CharacteristicValue.ToArray();
        lock (_feed)
        {
            try { Center.OnDataSource(data); }
            catch (FormatException e)
            {
                Center.Reset();
                StreamError?.Invoke(e);
                _ = ResubscribeAsync();
            }
        }
    }

    /// <summary>Toggling the Notification Source CCCD makes iOS re-send every live notification (PreExisting).</summary>
    private async Task ResubscribeAsync()
    {
        if (_ns is null) return;
        try
        {
            await _ns.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None);
            await _ns.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
        }
        catch (Exception) { /* link dropped; IphoneLink restarts us */ }
    }

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (data, done) in _writes.Reader.ReadAllAsync(ct))
            {
                bool ok = false;
                try
                {
                    var r = await _cp!.WriteValueWithResultAsync(data.AsBuffer(), GattWriteOption.WriteWithResponse).AsTask(ct);
                    ok = r.Status == GattCommunicationStatus.Success;
                    // A failed GetNotificationAttributes leaves the assembler waiting for an answer that never comes: reset.
                    if (!ok && data.Length > 0 && data[0] != (byte)AncsCommandId.PerformNotificationAction)
                    {
                        lock (_feed) Center.Reset();
                        _ = ResubscribeAsync();
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { ok = false; }
                done?.TrySetResult(ok);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Positive (answer / notification's own action) or Negative (decline / clear). True when iOS accepted the write.</summary>
    public Task<bool> PerformActionAsync(uint uid, bool positive)
    {
        if (!IsRunning) return Task.FromResult(false);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _writes.Writer.TryWrite((AncsCommands.PerformNotificationAction(uid, positive ? AncsActionId.Positive : AncsActionId.Negative), tcs));
        return tcs.Task;
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _cts = null;
        if (_ds is not null) _ds.ValueChanged -= OnDataSource;
        if (_ns is not null) _ns.ValueChanged -= OnNotificationSource;
        _ns = _cp = _ds = null;
        _service?.Dispose();
        _service = null;
        lock (_feed) Center.Reset();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
