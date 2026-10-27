using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace Nitro.Continuity.Platform;

/// <summary>
/// Procedure C (experimental, docs/continuity-plan.md §1.6-C / §6.1): host a minimal HID-over-GATT keyboard so the PC
/// shows up in the iPhone's Bluetooth list. When the user taps it the iPhone connects as central and LE-bonds; the same
/// link then serves ANCS. We wait for a new paired LE device and stop advertising. Never sends key reports.
/// UNVERIFIED: iOS may ignore a Windows-hosted HID service (Windows serves its own Device Information service).
/// </summary>
public sealed class HidBait : IAsyncDisposable
{
    private static readonly Guid HidService = BluetoothUuidHelper.FromShortId(0x1812);
    private GattServiceProvider? _provider;

    // Boot-protocol-like keyboard report map (report id 1: modifiers, reserved, 6 keys).
    private static readonly byte[] ReportMap =
    [
        0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0x85, 0x01,
        0x05, 0x07, 0x19, 0xE0, 0x29, 0xE7, 0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x08, 0x81, 0x02,
        0x95, 0x01, 0x75, 0x08, 0x81, 0x01,
        0x95, 0x06, 0x75, 0x08, 0x15, 0x00, 0x25, 0x65, 0x05, 0x07, 0x19, 0x00, 0x29, 0x65, 0x81, 0x00,
        0xC0,
    ];

    /// <summary>Advertises until a new LE device pairs or <paramref name="timeout"/> passes. Returns that device or null.</summary>
    public async Task<BluetoothLEDevice?> WaitForPhoneAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var created = await GattServiceProvider.CreateAsync(HidService).AsTask(ct);
        if (created.Error != BluetoothError.Success) return null;
        _provider = created.ServiceProvider;
        var svc = _provider.Service;

        await Add(svc, 0x2A4A, GattCharacteristicProperties.Read, [0x11, 0x01, 0x00, 0x02], ct);          // HID Information
        await Add(svc, 0x2A4B, GattCharacteristicProperties.Read, ReportMap, ct, encrypted: true);           // Report Map
        await Add(svc, 0x2A4C, GattCharacteristicProperties.WriteWithoutResponse, null, ct);                 // HID Control Point
        await Add(svc, 0x2A4E, GattCharacteristicProperties.Read | GattCharacteristicProperties.WriteWithoutResponse, [0x01], ct); // Protocol Mode = report
        var report = await Add(svc, 0x2A4D, GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            new byte[8], ct, encrypted: true);                                                               // Input report
        if (report is not null)
            await report.CreateDescriptorAsync(BluetoothUuidHelper.FromShortId(0x2908), new GattLocalDescriptorParameters
            {
                StaticValue = new byte[] { 0x01, 0x01 }.AsBuffer(),                                           // report id 1, input
                ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
            }).AsTask(ct);

        var known = (await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)).AsTask(ct))
            .Select(d => d.Id).ToHashSet();
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = DeviceInformation.CreateWatcher(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
        watcher.Added += (_, d) => { if (!known.Contains(d.Id)) tcs.TrySetResult(d.Id); };
        watcher.Start();
        _provider.StartAdvertising(new GattServiceProviderAdvertisingParameters { IsConnectable = true, IsDiscoverable = true });
        try
        {
            using var reg = ct.Register(() => tcs.TrySetResult(null));
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, CancellationToken.None));
            var id = done == tcs.Task ? tcs.Task.Result : null;
            return id is null ? null : await BluetoothLEDevice.FromIdAsync(id);
        }
        finally
        {
            _provider.StopAdvertising();
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
        }
    }

    private static async Task<GattLocalCharacteristic?> Add(GattLocalService svc, ushort uuid, GattCharacteristicProperties props,
        byte[]? value, CancellationToken ct, bool encrypted = false)
    {
        var p = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = props,
            ReadProtectionLevel = encrypted ? GattProtectionLevel.EncryptionRequired : GattProtectionLevel.Plain,
            WriteProtectionLevel = GattProtectionLevel.Plain,
        };
        if (value is not null) p.StaticValue = value.AsBuffer();
        var r = await svc.CreateCharacteristicAsync(BluetoothUuidHelper.FromShortId(uuid), p).AsTask(ct);
        return r.Error == BluetoothError.Success ? r.Characteristic : null;
    }

    public ValueTask DisposeAsync()
    {
        try { _provider?.StopAdvertising(); } catch (Exception) { }
        _provider = null;
        return ValueTask.CompletedTask;
    }
}
