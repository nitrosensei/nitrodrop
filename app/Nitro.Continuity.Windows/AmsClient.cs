using System.Runtime.InteropServices.WindowsRuntime;
using Nitro.Continuity.Ancs;
using Nitro.Continuity.Runtime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Nitro.Continuity.Platform;

/// <summary>Apple Media Service client: now playing (Entity Update) and play/pause/next (Remote Command).</summary>
public sealed class AmsClient : IAsyncDisposable
{
    private readonly AmsNowPlayingTracker _tracker = new();
    private GattDeviceService? _service;
    private GattCharacteristic? _remote, _entity;

    public NowPlaying Current => _tracker.Current;
    public event Action<NowPlaying>? Changed;
    public bool IsRunning => _remote is not null;

    public async Task<GattStartResult> StartAsync(BluetoothLEDevice le, CancellationToken ct = default)
    {
        await StopAsync();
        var sr = await le.GetGattServicesForUuidAsync(AmsUuids.Service, BluetoothCacheMode.Uncached).AsTask(ct);
        if (sr.Status == GattCommunicationStatus.AccessDenied) return GattStartResult.NeedsPairing;
        if (sr.Status != GattCommunicationStatus.Success) return GattStartResult.Failed;
        if (sr.Services.Count == 0) return GattStartResult.ServiceMissing;
        _service = sr.Services[0];
        _remote = await Char(AmsUuids.RemoteCommand, ct);
        _entity = await Char(AmsUuids.EntityUpdate, ct);
        if (_remote is null || _entity is null) { await StopAsync(); return GattStartResult.NeedsPairing; }

        _entity.ValueChanged += OnEntityUpdate;
        foreach (var c in new[] { _remote, _entity })
        {
            var w = await c.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct);
            if (w.Status != GattCommunicationStatus.Success) { await StopAsync(); return GattStartResult.NeedsPairing; }
        }
        foreach (var sub in AmsNowPlayingTracker.Subscriptions)
            await _entity.WriteValueWithResultAsync(sub.AsBuffer(), GattWriteOption.WriteWithResponse).AsTask(ct);
        return GattStartResult.Ok;
    }

    private async Task<GattCharacteristic?> Char(Guid uuid, CancellationToken ct)
    {
        var r = await _service!.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached).AsTask(ct);
        return r.Status == GattCommunicationStatus.Success && r.Characteristics.Count > 0 ? r.Characteristics[0] : null;
    }

    private void OnEntityUpdate(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var np = _tracker.Apply(AmsEntityUpdate.Parse(args.CharacteristicValue.ToArray()));
            Changed?.Invoke(np);
        }
        catch (FormatException) { }
    }

    public async Task<bool> CommandAsync(AmsRemoteCommandId cmd)
    {
        if (_remote is null) return false;
        try
        {
            var r = await _remote.WriteValueWithResultAsync(new[] { (byte)cmd }.AsBuffer(), GattWriteOption.WriteWithResponse);
            return r.Status == GattCommunicationStatus.Success;
        }
        catch (Exception) { return false; }
    }

    public Task StopAsync()
    {
        if (_entity is not null) _entity.ValueChanged -= OnEntityUpdate;
        _remote = _entity = null;
        _service?.Dispose();
        _service = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
