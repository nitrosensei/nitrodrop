// Nitro.Awdl: WinRT BLE publisher for the AirDrop wake-up advert (net10.0-windows only).
// Copyright (c) 2026 Nitrobyte. Same API tools/ble_test.py used (proved on this machine 2026-10-28).

#if NITRO_WINRT
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth.Advertisement;

namespace Nitro.Awdl.Ble;

/// <summary>
/// Publishes the AirDrop BLE advert (Apple 0x004C, type 0x05) while a sender browses, so iPhones in Everyone mode
/// wake their AWDL interface and AirDrop server (SPEC §2: without it they stay unreachable). Windows picks the
/// advertising interval and address; BLE-5 asks for a connectable advert at ~100 ms with a rotating tag, which
/// BluetoothLEAdvertisementPublisher doesn't expose (UNVERIFIED how much that matters; ble_test.py worked as is).
/// </summary>
public sealed class BleAirDropAdvertiser : IAirDropBleAdvertiser
{
    private BluetoothLEAdvertisementPublisher? _publisher;

    /// <summary>Version 3 (Owly) by default; false = the version-1 bytes ble_test.py sent.</summary>
    public bool UseVersion3 { get; init; } = true;

    public BluetoothLEAdvertisementPublisherStatus Status => _publisher?.Status ?? BluetoothLEAdvertisementPublisherStatus.Created;

    public event Action<BluetoothLEAdvertisementPublisherStatus, string>? StatusChanged;

    /// <summary>Starts advertising <paramref name="advert"/> (anonymous with a fresh random tag if null).</summary>
    public void Start(AirDropBleAdvert? advert = null)
    {
        Stop();
        var a = advert ?? AirDropBleAdvert.Anonymous();
        var data = UseVersion3 ? a.ManufacturerData() : a.ManufacturerDataV1();
        var pub = new BluetoothLEAdvertisementPublisher();
        pub.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData(AirDropBleAdvert.AppleCompanyId, data.AsBuffer()));
        pub.StatusChanged += (_, e) => StatusChanged?.Invoke(e.Status, e.Error.ToString());
        pub.Start();
        _publisher = pub;
    }

    /// <summary>Restarts with a new random tag (BLE-5 rotates it with the address).</summary>
    public void Rotate() => Start(AirDropBleAdvert.Anonymous());

    public void Stop()
    {
        var p = _publisher;
        _publisher = null;
        if (p != null && p.Status is BluetoothLEAdvertisementPublisherStatus.Started or BluetoothLEAdvertisementPublisherStatus.Waiting)
            p.Stop();
    }

    public void Dispose() => Stop();
}
#endif
