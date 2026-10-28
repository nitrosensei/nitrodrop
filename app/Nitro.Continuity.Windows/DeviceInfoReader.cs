using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace Nitro.Continuity.Platform;

/// <summary>
/// Reads the GATT Device Information Service (0x180A) Model Number String (0x2A24): iPhones answer with their hardware
/// identifier ("iPhone15,2"), which AirDrop.Core.Devices.AppleModels turns into "iPhone 14 Pro" + art. Read-only.
/// UNVERIFIED: that every iOS version exposes DIS to a bonded Windows central (seen in nRF Connect captures of iPhones).
/// </summary>
public static class DeviceInfoReader
{
    public static async Task<string?> ReadModelNumberAsync(BluetoothLEDevice le, CancellationToken ct = default)
    {
        var services = await le.GetGattServicesForUuidAsync(GattServiceUuids.DeviceInformation, BluetoothCacheMode.Cached).AsTask(ct);
        if (services.Status != GattCommunicationStatus.Success) return null;
        foreach (var service in services.Services)
        {
            using (service)
            {
                var chars = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.ModelNumberString, BluetoothCacheMode.Cached).AsTask(ct);
                if (chars.Status != GattCommunicationStatus.Success) continue;
                foreach (var c in chars.Characteristics)
                {
                    var r = await c.ReadValueAsync(BluetoothCacheMode.Cached).AsTask(ct);
                    if (r.Status != GattCommunicationStatus.Success || r.Value is null) continue;
                    var bytes = new byte[r.Value.Length];
                    DataReader.FromBuffer(r.Value).ReadBytes(bytes);
                    if (Clean(Encoding.UTF8.GetString(bytes)) is { } model) return model;
                }
            }
        }
        return null;
    }

    /// <summary>Printable, trimmed, at most 40 chars; null when empty.</summary>
    public static string? Clean(string? raw)
    {
        if (raw is null) return null;
        var s = new string(raw.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return s.Length == 0 ? null : s.Length > 40 ? s[..40] : s;
    }
}
