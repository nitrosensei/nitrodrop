using System.Collections.Generic;

namespace AirDrop.App.Services;

/// <summary>Apple model identifiers → marketing names (subset; unknown ids fall back to the family).</summary>
internal static class DeviceModels
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["iPhone13,1"] = "iPhone 12 mini", ["iPhone13,2"] = "iPhone 12",
        ["iPhone13,3"] = "iPhone 12 Pro", ["iPhone13,4"] = "iPhone 12 Pro Max",
        ["iPhone14,4"] = "iPhone 13 mini", ["iPhone14,5"] = "iPhone 13",
        ["iPhone14,2"] = "iPhone 13 Pro", ["iPhone14,3"] = "iPhone 13 Pro Max",
        ["iPhone14,6"] = "iPhone SE (3. nesil)",
        ["iPhone14,7"] = "iPhone 14", ["iPhone14,8"] = "iPhone 14 Plus",
        ["iPhone15,2"] = "iPhone 14 Pro", ["iPhone15,3"] = "iPhone 14 Pro Max",
        ["iPhone15,4"] = "iPhone 15", ["iPhone15,5"] = "iPhone 15 Plus",
        ["iPhone16,1"] = "iPhone 15 Pro", ["iPhone16,2"] = "iPhone 15 Pro Max",
        ["iPhone17,3"] = "iPhone 16", ["iPhone17,4"] = "iPhone 16 Plus",
        ["iPhone17,1"] = "iPhone 16 Pro", ["iPhone17,2"] = "iPhone 16 Pro Max",
        ["iPhone17,5"] = "iPhone 16e",
        ["iPad13,18"] = "iPad (10. nesil)", ["iPad13,19"] = "iPad (10. nesil)",
        ["MacBookAir10,1"] = "MacBook Air (M1)",
        ["Mac14,2"] = "MacBook Air (M2)", ["Mac14,15"] = "MacBook Air 15\" (M2)",
        ["Mac15,12"] = "MacBook Air 13\" (M3)", ["Mac15,13"] = "MacBook Air 15\" (M3)",
        ["MacBookPro17,1"] = "MacBook Pro 13\" (M1)", ["Mac14,7"] = "MacBook Pro 13\" (M2)",
    };

    public static string Describe(DeviceKind kind, string? model)
    {
        if (model is not null && Names.TryGetValue(model, out var n)) return n;
        return kind switch
        {
            DeviceKind.iPhone => "iPhone",
            DeviceKind.iPad => "iPad",
            DeviceKind.Mac => "Mac",
            DeviceKind.Windows => "Windows PC",
            _ => "Cihaz",
        };
    }
}
