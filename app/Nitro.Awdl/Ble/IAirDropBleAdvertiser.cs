// Nitro.Awdl: seam for the AirDrop BLE wake-up advert (real: BleAirDropAdvertiser on net10.0-windows; tests: a fake).
// Copyright (c) 2026 Nitrobyte.

namespace Nitro.Awdl.Ble;

public interface IAirDropBleAdvertiser : IDisposable
{
    /// <summary>Starts (or restarts) advertising; anonymous with a fresh random tag when <paramref name="advert"/> is null.</summary>
    void Start(AirDropBleAdvert? advert = null);
    void Stop();
}
