using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AirDrop.App.Setup;

/// <summary>A Wi-Fi card our kernel driver supports. <see cref="HardwareId"/> must also be listed in airdropwifi.inf.</summary>
public sealed record SupportedCard(string HardwareId, string Name, string FirmwareFile);

/// <summary>Parsed <c>PCI\VEN_xxxx&amp;DEV_xxxx[&amp;SUBSYS_xxxxxxxx][&amp;REV_xx]</c>.</summary>
public sealed record PciId(ushort Vendor, ushort Device, uint? Subsystem, byte? Revision);

/// <summary>
/// Table of supported cards. To add one: driver INF + core/iwx_init.c (see the INF header), then a row here.
/// WPF-free (linked into AirDrop.Tests).
/// </summary>
public static class SupportedDevices
{
    public static IReadOnlyList<SupportedCard> All { get; } = new[]
    {
        new SupportedCard(@"PCI\VEN_8086&DEV_51F1&SUBSYS_02748086", "Intel Wi-Fi 6 AX203", "iwlwifi-so-a0-hr-b0-77.ucode"),
    };

    /// <summary>First supported card among a device's hardware IDs (exact, or the same ID plus &amp;REV_xx).</summary>
    public static SupportedCard? Match(IEnumerable<string> hardwareIds)
    {
        var ids = hardwareIds.Select(Parse).Where(p => p is not null).ToList();
        foreach (var card in All)
        {
            var want = Parse(card.HardwareId)!;
            if (ids.Any(p => p!.Vendor == want.Vendor && p.Device == want.Device && p.Subsystem == want.Subsystem))
                return card;
        }
        return null;
    }

    /// <summary>Matches a PCI enum key name (<c>VEN_8086&amp;DEV_51F1&amp;SUBSYS_02748086&amp;REV_01</c>).</summary>
    public static SupportedCard? MatchEnumKey(string keyName) => Match(new[] { @"PCI\" + keyName });

    /// <summary>Registry key name prefix under HKLM\SYSTEM\CurrentControlSet\Enum\PCI for a card.</summary>
    public static string EnumKeyPrefix(SupportedCard card) => card.HardwareId.Substring(4);

    public static PciId? Parse(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var s = id.Trim();
        if (s.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        int slash = s.IndexOf('\\');
        if (slash >= 0) s = s[..slash];   // instance path: PCI\...\3&11583659&0&A3
        ushort? ven = null, dev = null;
        uint? subsys = null;
        byte? rev = null;
        foreach (var part in s.Split('&'))
        {
            int u = part.IndexOf('_');
            if (u <= 0) continue;
            var key = part[..u].ToUpperInvariant();
            var val = part[(u + 1)..];
            switch (key)
            {
                case "VEN" when ushort.TryParse(val, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) && val.Length == 4: ven = v; break;
                case "DEV" when ushort.TryParse(val, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var d) && val.Length == 4: dev = d; break;
                case "SUBSYS" when uint.TryParse(val, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ss) && val.Length == 8: subsys = ss; break;
                case "REV" when byte.TryParse(val, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) && val.Length == 2: rev = r; break;
            }
        }
        return ven is null || dev is null ? null : new PciId(ven.Value, dev.Value, subsys, rev);
    }
}
