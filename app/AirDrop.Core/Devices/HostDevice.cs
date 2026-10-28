using System.Text.Json.Serialization;

namespace AirDrop.Core.Devices;

/// <summary>Enclosure kinds we distinguish (string constants so the wire format stays readable and extensible).</summary>
public static class HostChassis
{
    public const string Laptop = "laptop";
    public const string Desktop = "desktop";
    public const string AllInOne = "allInOne";
    public const string Tablet = "tablet";
    public const string Phone = "phone";
    public const string Unknown = "unknown";

    public static string Normalize(string? c) => c?.Trim().ToLowerInvariant() switch
    {
        "laptop" or "notebook" or "portable" => Laptop,
        "desktop" or "tower" or "server" => Desktop,
        "allinone" or "all-in-one" => AllInOne,
        "tablet" or "slate" or "detachable" => Tablet,
        "phone" => Phone,
        _ => Unknown,
    };

    /// <summary>SMBIOS type 3 "Chassis Type" (DSP0134 §7.4.1, low 7 bits) → our kind.</summary>
    public static string FromSmbios(int type) => (type & 0x7F) switch
    {
        3 or 4 or 5 or 6 or 7 or 15 or 16 or 17 or 23 or 24 or 25 or 28 or 29 or 34 or 35 or 36 => Desktop,
        8 or 9 or 10 or 12 or 14 or 18 or 21 or 31 => Laptop,   // 31 = convertible
        13 => AllInOne,
        11 or 30 or 32 => Tablet,                                // 11 = hand held, 30 = tablet, 32 = detachable
        _ => Unknown,                                            // 1 other, 2 unknown, …
    };

    /// <summary>POWER_PLATFORM_ROLE (PowerDeterminePlatformRoleEx) → our kind; fallback when SMBIOS says "unknown".</summary>
    public static string FromPowerRole(int role) => role switch
    {
        2 => Laptop,                     // PlatformRoleMobile
        8 => Tablet,                     // PlatformRoleSlate
        1 or 3 or 4 or 5 or 6 or 7 => Desktop,
        _ => Unknown,
    };
}

/// <summary>
/// The hardware a NitroDrop peer runs on, as it advertises it to other NitroDrop peers (AirDrop /Discover + /Ask under
/// <see cref="AirDropConstants.NitroDropDevice"/>, and Nitro.Sync DeviceState.Hardware). Values are already cleaned
/// (<see cref="PcModelNames"/>): "Acer" + "Nitro V 15" + laptop.
/// </summary>
public sealed record HostDevice
{
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    /// <summary><see cref="HostChassis"/> constant.</summary>
    public string Chassis { get; init; } = HostChassis.Unknown;
    /// <summary>"windows" for NitroDrop on Windows; room for "android"/"linux" peers later.</summary>
    public string? Platform { get; init; }

    /// <summary>"Acer Nitro V 15", "Lenovo ThinkPad X1 Carbon Gen 11"; null when nothing is known.</summary>
    [JsonIgnore]
    public string? DisplayName
    {
        get
        {
            var mf = Manufacturer?.Trim();
            var model = Model?.Trim();
            if (string.IsNullOrEmpty(model)) return string.IsNullOrEmpty(mf) ? null : mf;
            if (string.IsNullOrEmpty(mf) || model.StartsWith(mf, StringComparison.OrdinalIgnoreCase)) return model;
            return mf + " " + model;
        }
    }

    public const int MaxField = 80;

    /// <summary>Clamps lengths and strips control characters (input from the network).</summary>
    public HostDevice Sanitized() => this with
    {
        Manufacturer = Clean(Manufacturer),
        Model = Clean(Model),
        Chassis = HostChassis.Normalize(Chassis),
        Platform = Clean(Platform)?.ToLowerInvariant(),
    };

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (s.Length == 0) return null;
        return s.Length <= MaxField ? s : s[..MaxField];
    }

    // ---- AirDrop plist form: {"Manufacturer": "...", "Model": "...", "Chassis": "laptop", "Platform": "windows"} ----

    public Dictionary<string, object> ToPlist()
    {
        var d = new Dictionary<string, object> { ["Chassis"] = Chassis };
        if (Manufacturer is { } m) d["Manufacturer"] = m;
        if (Model is { } mo) d["Model"] = mo;
        if (Platform is { } p) d["Platform"] = p;
        return d;
    }

    /// <summary>Parses our marker value; anything malformed → null (never throws).</summary>
    public static HostDevice? FromPlist(object? value)
    {
        if (value is not Dictionary<string, object> d) return null;
        var h = new HostDevice
        {
            Manufacturer = d.GetValueOrDefault("Manufacturer") as string,
            Model = d.GetValueOrDefault("Model") as string,
            Chassis = d.GetValueOrDefault("Chassis") as string ?? HostChassis.Unknown,
            Platform = d.GetValueOrDefault("Platform") as string,
        }.Sanitized();
        return h.Manufacturer is null && h.Model is null && h.Chassis == HostChassis.Unknown ? null : h;
    }

    private static readonly Lazy<HostDevice?> LocalLazy = new(LocalHostProbe.Detect);

    /// <summary>This PC (detected once; null when detection is impossible, e.g. not Windows).</summary>
    public static HostDevice? Local => LocalLazy.Value;
}
