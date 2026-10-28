using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AirDrop.Core.Devices;

/// <summary>
/// Detects this PC's manufacturer / model / chassis without WMI: SMBIOS strings from
/// HKLM\HARDWARE\DESCRIPTION\System\BIOS (the same values Win32_ComputerSystem reports), the chassis from the raw SMBIOS
/// table (GetSystemFirmwareTable 'RSMB', type 3 = Win32_SystemEnclosure.ChassisTypes) with PowerDeterminePlatformRoleEx
/// as fallback. Read-only, no elevation, never throws.
/// </summary>
public static class LocalHostProbe
{
    public static HostDevice? Detect()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var strings = ReadSmbiosStrings();
            var (mf, model) = strings is null ? (null, null) : PcModelNames.Clean(strings);
            var chassis = ChassisFromTable(ReadRawSmbios());
            if (chassis == HostChassis.Unknown) chassis = HostChassis.FromPowerRole(PowerRole());
            return new HostDevice { Manufacturer = mf, Model = model, Chassis = chassis, Platform = "windows" }.Sanitized();
        }
        catch (Exception) { return new HostDevice { Platform = "windows" }; }
    }

    private static SmbiosStrings? ReadSmbiosStrings()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        if (key is null) return null;
        string? V(string n) => key.GetValue(n) as string;
        return new SmbiosStrings(V("SystemManufacturer"), V("SystemProductName"), V("SystemFamily"), V("SystemVersion"),
            V("BaseBoardManufacturer"), V("BaseBoardProduct"));
    }

    /// <summary>
    /// Parses a RawSMBIOSData blob (8-byte header + structure table, as GetSystemFirmwareTable('RSMB') returns it) and
    /// returns the first type-3 (System Enclosure) chassis type mapped to <see cref="HostChassis"/>.
    /// </summary>
    public static string ChassisFromTable(byte[]? raw)
    {
        if (raw is null || raw.Length < 8) return HostChassis.Unknown;
        int length = BitConverter.ToInt32(raw, 4);
        int end = Math.Min(raw.Length, 8 + Math.Max(0, length));
        int p = 8;
        while (p + 4 <= end)
        {
            byte type = raw[p], len = raw[p + 1];
            if (len < 4 || p + len > end) break;
            if (type == 3 && len > 5) return HostChassis.FromSmbios(raw[p + 5]);
            if (type == 127) break;                                   // end-of-table
            // skip the formatted area, then the string set (terminated by a double NUL)
            int q = p + len;
            while (q + 1 < end && !(raw[q] == 0 && raw[q + 1] == 0)) q++;
            p = q + 2;
        }
        return HostChassis.Unknown;
    }

    private const uint Rsmb = 0x52534D42; // 'RSMB'

    private static byte[]? ReadRawSmbios()
    {
        try
        {
            uint size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
            if (size == 0 || size > 1 << 20) return null;
            var buf = new byte[size];
            return GetSystemFirmwareTable(Rsmb, 0, buf, size) == size ? buf : null;
        }
        catch (Exception) { return null; }
    }

    private static int PowerRole()
    {
        try { return (int)PowerDeterminePlatformRoleEx(2 /* POWER_PLATFORM_ROLE_V2 */); }
        catch (Exception) { return 0; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint firmwareTableProviderSignature, uint firmwareTableId,
        [Out] byte[]? firmwareTableBuffer, uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerDeterminePlatformRoleEx(uint version);
}
