using System.Collections.Generic;
using AirDrop.App.Localization;
using AirDrop.Core.Devices;

namespace AirDrop.App.Services;

// No WPF types in this file: it is linked into AirDrop.Tests (and used by PhoneViewModel, which is linked too).

/// <summary>User-facing device kind names (Apple product names are never translated; the rest from Main.resx).</summary>
public static class DeviceStrings
{
    public static string iPhone => "iPhone";
    public static string iPad => "iPad";
    public static string Mac => "Mac";
    public static string WindowsPc => MainStrings.Device_WindowsPc;
    public static string WindowsLaptop => MainStrings.Device_WindowsLaptop;
    public static string WindowsDesktop => MainStrings.Device_WindowsDesktop;
    public static string WindowsTablet => MainStrings.Device_WindowsTablet;
    public static string AndroidPhone => MainStrings.Device_AndroidPhone;
    public static string Generic => MainStrings.Device_Generic;
}

/// <summary>Keys of the vector art in Themes/DeviceArt.xaml (resource key = "DeviceArt." + key).</summary>
public static class DeviceArtKeys
{
    public const string IphoneIsland = "iPhoneIsland";
    public const string IphoneNotch = "iPhoneNotch";
    public const string IphoneHome = "iPhoneHome";
    public const string IpadFull = "iPadFull";
    public const string IpadHome = "iPadHome";
    public const string MacBook = "MacBook";
    public const string IMac = "iMac";
    public const string MacMini = "MacMini";
    public const string MacStudio = "MacStudio";
    public const string MacPro = "MacPro";
    public const string Watch = "Watch";
    public const string VisionPro = "VisionPro";
    public const string WindowsLaptop = "WinLaptop";
    public const string WindowsDesktop = "WinDesktop";
    public const string Tablet = "Tablet";
    public const string AndroidPhone = "AndroidPhone";
    public const string Generic = "Generic";

    public static readonly IReadOnlyList<string> All =
    [
        IphoneIsland, IphoneNotch, IphoneHome, IpadFull, IpadHome, MacBook, IMac, MacMini, MacStudio, MacPro, Watch, VisionPro,
        WindowsLaptop, WindowsDesktop, Tablet, AndroidPhone, Generic,
    ];
}

/// <summary>
/// Friendly names and device art for every place a device appears (radar, receive sheet, Devices, Phone, history).
/// Sources, in order: a NitroDrop peer's own <see cref="HostDevice"/> (real PC model), the Apple model identifier
/// (<see cref="AppleModels"/>), then the coarse <see cref="DeviceKind"/>. Our Windows peers claim a Mac identifier on the
/// wire for iOS, so a Windows peer never borrows the Mac's name or art.
/// </summary>
public static class DeviceModels
{
    /// <summary>Full friendly name: "iPhone 15 Pro", "MacBook Pro (14-inch, M1 Pro, 2021)", "Acer Nitro V 15".</summary>
    public static string Describe(DeviceKind kind, string? model, HostDevice? host = null)
    {
        if (host?.DisplayName is { } pc) return pc;
        if (kind != DeviceKind.Windows && AppleModels.Resolve(model) is { } m) return m.Name;
        return KindName(kind, host);
    }

    /// <summary>One-line name for subtitles: "MacBook Pro 14″ (M1 Pro)", "iPad Pro 11″ (M4)", "Acer Nitro V 15".</summary>
    public static string ShortDescribe(DeviceKind kind, string? model, HostDevice? host = null)
    {
        if (host?.DisplayName is { } pc) return pc;
        if (kind != DeviceKind.Windows && AppleModels.Resolve(model) is { } m) return m.ShortName;
        return KindName(kind, host);
    }

    /// <summary>Name for a phone/iPhone known only by its Bluetooth model number ("iPhone15,2"); null if unknown.</summary>
    public static string? DescribeIdentifier(string? identifier) =>
        AppleModels.Resolve(identifier) is { } m ? m.ShortName : null;

    public static string KindName(DeviceKind kind, HostDevice? host = null) => kind switch
    {
        DeviceKind.iPhone => DeviceStrings.iPhone,
        DeviceKind.iPad => DeviceStrings.iPad,
        DeviceKind.Mac => DeviceStrings.Mac,
        DeviceKind.Windows => HostChassis.Normalize(host?.Chassis) switch
        {
            HostChassis.Laptop => DeviceStrings.WindowsLaptop,
            HostChassis.Desktop or HostChassis.AllInOne => DeviceStrings.WindowsDesktop,
            HostChassis.Tablet => DeviceStrings.WindowsTablet,
            _ => DeviceStrings.WindowsPc,
        },
        _ when host?.Platform == "android" => DeviceStrings.AndroidPhone,
        _ => DeviceStrings.Generic,
    };

    /// <summary>Art key (<see cref="DeviceArtKeys"/>) for a device.</summary>
    public static string ArtKey(DeviceKind kind, string? model, HostDevice? host = null)
    {
        if (host is not null && (kind == DeviceKind.Windows || host.Platform is not null)) return ArtForHost(host, kind);
        if (kind == DeviceKind.Windows) return DeviceArtKeys.WindowsLaptop;   // older NitroDrop peer without the marker
        if (AppleModels.Resolve(model) is { } m) return ArtForApple(m);
        return kind switch
        {
            DeviceKind.iPhone => DeviceArtKeys.IphoneIsland,
            DeviceKind.iPad => DeviceArtKeys.IpadFull,
            DeviceKind.Mac => DeviceArtKeys.MacBook,
            _ => DeviceArtKeys.Generic,
        };
    }

    /// <summary>Art for an Apple model identifier alone (e.g. the Bluetooth iPhone); <paramref name="fallback"/> if unknown.</summary>
    public static string ArtKeyForIdentifier(string? identifier, string fallback = DeviceArtKeys.IphoneIsland) =>
        AppleModels.Resolve(identifier) is { } m ? ArtForApple(m) : fallback;

    public static string ArtForApple(AppleModel m) => m.Form switch
    {
        AppleForm.DynamicIsland => DeviceArtKeys.IphoneIsland,
        AppleForm.Notch => DeviceArtKeys.IphoneNotch,
        AppleForm.HomeButton => m.Family == AppleFamily.iPad ? DeviceArtKeys.IpadHome : DeviceArtKeys.IphoneHome,
        AppleForm.NoHomeButton => DeviceArtKeys.IpadFull,
        AppleForm.Laptop => DeviceArtKeys.MacBook,
        AppleForm.AllInOne => DeviceArtKeys.IMac,
        AppleForm.MacMini => DeviceArtKeys.MacMini,
        AppleForm.MacStudio => DeviceArtKeys.MacStudio,
        AppleForm.Tower => DeviceArtKeys.MacPro,
        AppleForm.Watch => DeviceArtKeys.Watch,
        AppleForm.Headset => DeviceArtKeys.VisionPro,
        _ => m.Family switch
        {
            AppleFamily.iPhone => DeviceArtKeys.IphoneIsland,
            AppleFamily.iPad => DeviceArtKeys.IpadFull,
            AppleFamily.Mac => DeviceArtKeys.MacBook,
            _ => DeviceArtKeys.Generic,
        },
    };

    public static string ArtForHost(HostDevice host, DeviceKind kind = DeviceKind.Windows)
    {
        if (host.Manufacturer == "Apple" && kind == DeviceKind.Windows)   // Boot Camp Mac running NitroDrop: still a Windows peer
            return HostChassis.Normalize(host.Chassis) == HostChassis.Laptop ? DeviceArtKeys.MacBook : DeviceArtKeys.IMac;
        return HostChassis.Normalize(host.Chassis) switch
        {
            HostChassis.Laptop => DeviceArtKeys.WindowsLaptop,
            HostChassis.Desktop or HostChassis.AllInOne => DeviceArtKeys.WindowsDesktop,
            HostChassis.Tablet => DeviceArtKeys.Tablet,
            HostChassis.Phone => host.Platform == "android" ? DeviceArtKeys.AndroidPhone : DeviceArtKeys.Generic,
            _ => host.Platform == "android" ? DeviceArtKeys.AndroidPhone : DeviceArtKeys.WindowsLaptop,
        };
    }
}
