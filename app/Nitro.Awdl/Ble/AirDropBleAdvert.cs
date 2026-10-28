// Nitro.Awdl: the AirDrop BLE advertisement (SPEC §2, BLE-1..5).
// C# port of owly-airdrop src/proto/ble.rs (https://github.com/t4t5/owly, commit 9a676e1).
// Copyright (c) 2026 T4T5 (Owly, MIT License). Port: Copyright (c) 2026 Nitrobyte. See LICENSE-OWLY.txt.

using System.Security.Cryptography;
using System.Text;

namespace Nitro.Awdl.Ble;

/// <summary>
/// The advert a sender transmits while browsing so that receivers wake AWDL: Apple manufacturer data (company 0x004C)
/// holding an AirDrop TLV, type 0x05, 18-byte payload. Version 3: 0x40, a 3-byte tag, 4 zero bytes, 0x03, four 2-byte
/// contact-hash prefixes, 0x00. Zero hashes wake receivers in Everyone mode (BLE-3).
/// </summary>
public readonly record struct AirDropBleAdvert(byte[] Tag, byte[] AppleId, byte[] Phone, byte[] Email, byte[] Email2)
{
    /// <summary>Apple's Bluetooth company ID, the key of the manufacturer data.</summary>
    public const ushort AppleCompanyId = 0x004c;
    /// <summary>The AirDrop TLV type in Apple's manufacturer data.</summary>
    public const byte AirDropType = 0x05;
    /// <summary>The advertising interval current Apple devices answer to (BLE-5).</summary>
    public const int IntervalMs = 100;

    /// <summary>BLE-3/BLE-5: no Apple identity, zero hashes, random tag unless given.</summary>
    public static AirDropBleAdvert Anonymous(byte[]? tag = null) =>
        new(tag ?? RandomNumberGenerator.GetBytes(3), new byte[2], new byte[2], new byte[2], new byte[2]);

    /// <summary>First 2 bytes of SHA-256 of an identifier (normalisation is open, BLE-4: pass it already normalised).</summary>
    public static byte[] HashPrefix(string identifier) => SHA256.HashData(Encoding.UTF8.GetBytes(identifier))[..2];

    /// <summary>The manufacturer data without the company ID: the AirDrop TLV and its 18-byte payload (20 bytes).</summary>
    public byte[] ManufacturerData()
    {
        var o = new List<byte>(20) { AirDropType, 0x12, 0x40 };
        o.AddRange(Fixed(Tag, 3));
        o.AddRange(new byte[4]);
        o.Add(0x03);
        foreach (var h in new[] { AppleId, Phone, Email, Email2 }) o.AddRange(Fixed(h, 2));
        o.Add(0x00);
        return [.. o];
    }

    /// <summary>
    /// The version-1 layout tools/ble_test.py proved on this machine (OpenDrop): offsets 0-7 zero, 0x01 at 8, hashes, 0.
    /// </summary>
    public byte[] ManufacturerDataV1()
    {
        var o = new List<byte>(20) { AirDropType, 0x12 };
        o.AddRange(new byte[8]);
        o.Add(0x01);
        foreach (var h in new[] { AppleId, Phone, Email, Email2 }) o.AddRange(Fixed(h, 2));
        o.Add(0x00);
        return [.. o];
    }

    private static byte[] Fixed(byte[]? b, int n)
    {
        var o = new byte[n];
        if (b != null) b.AsSpan(0, Math.Min(n, b.Length)).CopyTo(o);
        return o;
    }
}
