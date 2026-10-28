using System;
using System.Collections.Generic;
using System.Linq;
using AirDrop.App.Localization;
using static AirDrop.App.Localization.SetupStrings;

namespace AirDrop.App.Setup;

/// <summary>
/// How to turn Secure Boot off (and back on) in one vendor's firmware setup.
/// <see cref="BiosKeys"/> is only the fallback: "Restart to BIOS" (shutdown /r /fw) opens setup directly.
/// Firmware menu item names stay in English (that is what the firmware shows); the prose around them is localized.
/// </summary>
public sealed record VendorGuide(
    string Id,
    string DisplayName,
    string[] ManufacturerKeywords,
    string[] ModelKeywords,
    string BiosKeys,
    string MenuPath,
    string[] DisableSteps,
    string? Note = null)
{
    /// <summary>Rollback: the same menu, set back to Enabled.</summary>
    public string[] EnableSteps => new[] { Vendor_Enable1(MenuPath), Vendor_Enable2, Vendor_Enable3 };
}

/// <summary>Vendor-specific Secure Boot instructions as data, looked up by SMBIOS strings. WPF-free.</summary>
public static class VendorGuides
{
    private sealed record Table(string Lang, IReadOnlyList<VendorGuide> All, VendorGuide Generic);
    private static Table? _cache;

    /// <summary>Rows in the current UI language (rebuilt when the language changes).</summary>
    public static IReadOnlyList<VendorGuide> All => Current().All;
    public static VendorGuide Generic => Current().Generic;

    private static Table Current()
    {
        var c = _cache;
        if (c is not null && c.Lang == Loc.Language) return c;
        return _cache = new Table(Loc.Language, Build(), BuildGeneric());
    }

    private static VendorGuide[] Build()
    {
        string save = Vendor_Save;
        const string SecurityMenu = "Security → Secure Boot";
        return new VendorGuide[]
        {
            // Model-specific rows come before their vendor's generic row.
            new("lenovo-thinkpad", "Lenovo ThinkPad", new[] { "LENOVO" }, new[] { "THINKPAD", "THINKBOOK" },
                Vendor_ThinkPad_Keys, SecurityMenu,
                new[] { Vendor_ThinkPad_Step1, Vendor_ThinkPad_Step2, Vendor_ThinkPad_Step3, save },
                Vendor_ThinkPad_Note),
            new("lenovo", "Lenovo (IdeaPad, Legion, Yoga, LOQ)", new[] { "LENOVO" }, Array.Empty<string>(),
                Vendor_Lenovo_Keys, SecurityMenu,
                new[] { Vendor_Lenovo_Step1, Vendor_Lenovo_Step2, save }),
            new("hp", "HP (Pavilion, Victus, Omen, EliteBook, ProBook)", new[] { "HP", "HEWLETT" }, Array.Empty<string>(),
                Vendor_Hp_Keys, Vendor_Hp_Menu,
                new[] { Vendor_Hp_Step1, Vendor_Hp_Step2, save, Vendor_Hp_Step4 }),
            new("dell", "Dell / Alienware", new[] { "DELL", "ALIENWARE" }, Array.Empty<string>(),
                Vendor_Dell_Keys, Vendor_Dell_Menu,
                new[] { Vendor_Dell_Step1, Vendor_Dell_Step2, Vendor_Dell_Step3 }),
            new("asus", Vendor_Asus_Name, new[] { "ASUS" }, Array.Empty<string>(),
                Vendor_Asus_Keys, Vendor_Asus_Menu,
                new[] { Vendor_Asus_Step1, Vendor_Asus_Step2, Vendor_Asus_Step3, save }),
            new("acer", "Acer (Nitro, Predator, Aspire, Swift)", new[] { "ACER" }, Array.Empty<string>(),
                Vendor_Acer_Keys, "Boot → Secure Boot",
                new[] { Vendor_Acer_Step1, Vendor_Acer_Step2, save, Vendor_Acer_Step4 }),
            new("msi", Vendor_Msi_Name, new[] { "MICRO-STAR", "MSI" }, Array.Empty<string>(),
                Vendor_Msi_Keys, Vendor_Msi_Menu,
                new[] { Vendor_Msi_Step1, Vendor_Msi_Step2, save }),
            new("gigabyte", "Gigabyte / AORUS", new[] { "GIGABYTE", "AORUS" }, Array.Empty<string>(),
                Vendor_Gigabyte_Keys, "Boot → Secure Boot",
                new[] { Vendor_Gigabyte_Step1, Vendor_Gigabyte_Step2, save }),
            new("samsung", "Samsung Galaxy Book", new[] { "SAMSUNG" }, Array.Empty<string>(),
                Vendor_KeysF2, "Boot → Secure Boot Control",
                new[] { Vendor_Samsung_Step1, Vendor_Samsung_Step2, save }),
            new("huawei", "Huawei / Honor MateBook", new[] { "HUAWEI", "HONOR" }, Array.Empty<string>(),
                Vendor_KeysF2, "Secure Boot",
                new[] { Vendor_Huawei_Step1, Vendor_Huawei_Step2, save }),
            new("casper", "Casper (Excalibur, Nirvana)", new[] { "CASPER" }, Array.Empty<string>(),
                Vendor_Casper_Keys, SecurityMenu,
                new[] { Vendor_Casper_Step1, Vendor_Casper_Step2, save },
                Vendor_Casper_Note),
            new("monster", "Monster (Abra, Tulpar, Semruk)", new[] { "MONSTER" }, Array.Empty<string>(),
                Vendor_KeysF2, SecurityMenu,
                new[] { Vendor_Monster_Step1, Vendor_Monster_Step2, Vendor_SaveF10F4 }),
            new("clevo", Vendor_Clevo_Name, new[] { "CLEVO", "TONGFANG", "NOTEBOOK" }, Array.Empty<string>(),
                Vendor_KeysF2, SecurityMenu,
                new[] { Vendor_Monster_Step1, Vendor_SecureBootDisabled, Vendor_SaveF10F4 }),
            new("surface", "Microsoft Surface", new[] { "MICROSOFT" }, new[] { "SURFACE" },
                Vendor_Surface_Keys, SecurityMenu,
                new[] { Vendor_Surface_Step1, Vendor_Surface_Step2, Vendor_Surface_Step3 }),
            new("asrock", Vendor_Asrock_Name, new[] { "ASROCK" }, Array.Empty<string>(),
                Vendor_Asrock_Keys, SecurityMenu,
                new[] { Vendor_Asrock_Step1, Vendor_SecureBootDisabled, save }),
        };
    }

    private static VendorGuide BuildGeneric() => new("generic", Vendor_Generic_Name, Array.Empty<string>(), Array.Empty<string>(),
        Vendor_Generic_Keys, Vendor_Generic_Menu,
        new[] { Vendor_Generic_Step1, Vendor_Generic_Step2, Vendor_Save },
        Vendor_Generic_Note);

    /// <summary>Best guide for this machine: vendor + model keyword first, vendor only next, generic last.</summary>
    public static VendorGuide Find(string? manufacturer, string? product = null, string? family = null)
    {
        string man = Norm(manufacturer);
        string model = Norm(product) + " " + Norm(family);
        if (man.Length == 0) return Generic;

        var vendorRows = All.Where(g => g.ManufacturerKeywords.Any(k => MatchesVendor(man, k))).ToList();
        return vendorRows.FirstOrDefault(g => g.ModelKeywords.Length > 0 && g.ModelKeywords.Any(k => model.Contains(k, StringComparison.Ordinal)))
               ?? vendorRows.FirstOrDefault(g => g.ModelKeywords.Length == 0)
               ?? Generic;
    }

    public static VendorGuide Find(MachineInfo m) => Find(m.Manufacturer, m.Product, m.Family);

    private static string Norm(string? s) => (s ?? "").Trim().ToUpperInvariant();

    /// <summary>"HP" must not match "HPE…"-like substrings of other names; long keywords match anywhere.</summary>
    private static bool MatchesVendor(string manufacturer, string keyword)
    {
        if (keyword.Length > 3) return manufacturer.Contains(keyword, StringComparison.Ordinal);
        foreach (var word in manufacturer.Split(new[] { ' ', ',', '.', '-', '(', ')' }, StringSplitOptions.RemoveEmptyEntries))
            if (word == keyword) return true;
        return false;
    }
}
