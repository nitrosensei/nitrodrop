using System;
using System.Collections.Generic;
using System.Linq;

namespace AirDrop.App.Setup;

/// <summary>
/// How to turn Secure Boot off (and back on) in one vendor's firmware setup.
/// <see cref="BiosKeys"/> is only the fallback: "BIOS'a yeniden başlat" (shutdown /r /fw) opens setup directly.
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
    public string[] EnableSteps => new[]
    {
        $"BIOS ayarlarında {MenuPath} bölümüne gidin.",
        "Secure Boot'u yeniden \"Enabled\" (Açık) yapın. Daha önce \"Restore Factory Keys\" / \"Install default keys\" istenirse onaylayın.",
        "F10 ile kaydedip çıkın (Save & Exit). Windows normal açılır, \"Test Modu\" yazısı kaybolur.",
    };
}

/// <summary>Vendor-specific Secure Boot instructions as data, looked up by SMBIOS strings. WPF-free.</summary>
public static class VendorGuides
{
    private const string Save = "F10'a basıp \"Save & Exit\" / \"Kaydet ve Çık\"ı onaylayın (Yes). Bilgisayar Windows'a döner.";

    public static IReadOnlyList<VendorGuide> All { get; } = new VendorGuide[]
    {
        // Model-specific rows come before their vendor's generic row.
        new("lenovo-thinkpad", "Lenovo ThinkPad", new[] { "LENOVO" }, new[] { "THINKPAD", "THINKBOOK" },
            "Açılışta F1 (ThinkBook: F2); ya da Enter → F1",
            "Security → Secure Boot",
            new[]
            {
                "Ok tuşlarıyla \"Security\" sekmesine geçin.",
                "\"Secure Boot\" satırına girin (Enter).",
                "\"Secure Boot\" değerini \"Off\" / \"Disabled\" yapın.",
                Save,
            },
            "Supervisor şifresi soruyorsa önce o girilmelidir."),
        new("lenovo", "Lenovo (IdeaPad, Legion, Yoga, LOQ)", new[] { "LENOVO" }, Array.Empty<string>(),
            "Açılışta F2 ya da Fn+F2; olmazsa kapalıyken yan taraftaki Novo deliğine iğneyle basıp \"BIOS Setup\"",
            "Security → Secure Boot",
            new[]
            {
                "Üstteki \"Security\" sekmesine geçin (Legion'da gerekirse önce \"More Settings\").",
                "\"Secure Boot\" satırını \"Disabled\" yapın.",
                Save,
            }),
        new("hp", "HP (Pavilion, Victus, Omen, EliteBook, ProBook)", new[] { "HP", "HEWLETT" }, Array.Empty<string>(),
            "Açılışta art arda Esc, menüde F10 (BIOS Setup)",
            "System Configuration (ya da Advanced) → Boot Options / Secure Boot Configuration",
            new[]
            {
                "\"System Configuration\" sekmesinde \"Boot Options\"a girin (iş modellerinde: \"Advanced\" → \"Secure Boot Configuration\").",
                "\"Secure Boot\" kutusunun işaretini kaldırın / \"Disable\" seçin. \"Legacy Support\" kapalı kalsın.",
                Save,
                "Açılışta HP 4 haneli bir doğrulama kodu gösterip yazmanızı ister: ekrandaki kodu yazıp Enter'a basın. Bu normaldir.",
            }),
        new("dell", "Dell / Alienware", new[] { "DELL", "ALIENWARE" }, Array.Empty<string>(),
            "Dell logosunda F2",
            "Boot Configuration → Secure Boot (eski BIOS: Secure Boot → Secure Boot Enable)",
            new[]
            {
                "Soldaki menüden \"Boot Configuration\"ı açın (eski görünümde \"Secure Boot\" → \"Secure Boot Enable\").",
                "\"Enable Secure Boot\" anahtarını kapatın; uyarıya \"Yes\" deyin.",
                "\"Apply Changes\" (ya da \"Apply\"), sonra \"Exit\". Bilgisayar yeniden başlar.",
            }),
        new("asus", "ASUS (ROG, TUF, Zenbook, Vivobook, anakartlar)", new[] { "ASUS" }, Array.Empty<string>(),
            "Açılışta F2 (masaüstü anakart: Del)",
            "Advanced Mode (F7) → Boot (ya da Security) → Secure Boot",
            new[]
            {
                "EZ Mode açılırsa F7 ile \"Advanced Mode\"a geçin.",
                "Dizüstü: \"Security\" → \"Secure Boot\" → \"Secure Boot Control\" = Disabled.",
                "Masaüstü anakart: \"Boot\" → \"Secure Boot\" → \"OS Type\" = \"Other OS\".",
                Save,
            }),
        new("acer", "Acer (Nitro, Predator, Aspire, Swift)", new[] { "ACER" }, Array.Empty<string>(),
            "Acer logosunda F2",
            "Boot → Secure Boot",
            new[]
            {
                "Secure Boot satırı gri ise önce \"Security\" → \"Set Supervisor Password\" ile bir BIOS şifresi belirleyin ve bir yere not edin (Acer bu şifre olmadan Secure Boot'u değiştirtmez).",
                "\"Boot\" sekmesine geçip \"Secure Boot\" değerini \"Disabled\" yapın.",
                Save,
                "İsterseniz sonra aynı menüden şifreyi boş bırakarak kaldırabilirsiniz.",
            }),
        new("msi", "MSI (dizüstü ve anakartlar)", new[] { "MICRO-STAR", "MSI" }, Array.Empty<string>(),
            "Açılışta Del (bazı dizüstülerde F2)",
            "Settings → Advanced → Windows OS Configuration → Secure Boot (dizüstü: Security → Secure Boot)",
            new[]
            {
                "Dizüstü: \"Security\" → \"Secure Boot\" → \"Secure Boot\" = Disabled.",
                "Anakart (Click BIOS): F7 ile Advanced → \"Settings\" → \"Advanced\" → \"Windows OS Configuration\" → \"Secure Boot\" = Disabled.",
                Save,
            }),
        new("gigabyte", "Gigabyte / AORUS", new[] { "GIGABYTE", "AORUS" }, Array.Empty<string>(),
            "Açılışta Del (dizüstü: F2)",
            "Boot → Secure Boot",
            new[]
            {
                "Easy Mode açılırsa F2 ile Advanced Mode'a geçin.",
                "\"Boot\" sekmesinde \"Secure Boot\"a girip \"Secure Boot Enable\" = Disabled yapın.",
                Save,
            }),
        new("samsung", "Samsung Galaxy Book", new[] { "SAMSUNG" }, Array.Empty<string>(),
            "Açılışta F2",
            "Boot → Secure Boot Control",
            new[]
            {
                "\"Boot\" (ya da \"Advanced\") sekmesine geçin.",
                "\"Secure Boot Control\" = Off. \"OS Mode Selection\" sorulursa \"UEFI OS\" seçili kalsın.",
                Save,
            }),
        new("huawei", "Huawei / Honor MateBook", new[] { "HUAWEI", "HONOR" }, Array.Empty<string>(),
            "Açılışta F2",
            "Secure Boot",
            new[]
            {
                "BIOS ana menüsünde \"Secure Boot\" (bazı modellerde \"Security\" altında) satırını bulun.",
                "\"Disabled\" yapın; şifre soruyorsa BIOS şifrenizi girin.",
                Save,
            }),
        new("casper", "Casper (Excalibur, Nirvana)", new[] { "CASPER" }, Array.Empty<string>(),
            "Açılışta F2 (bazı modellerde Del)",
            "Security → Secure Boot",
            new[]
            {
                "\"Security\" sekmesine geçin. Secure Boot gri ise önce \"Set Supervisor Password\" ile şifre belirleyin.",
                "\"Secure Boot\" (ya da \"Secure Boot Control\") = Disabled.",
                Save,
            },
            "Excalibur modellerinde menü \"Boot\" sekmesinde de olabilir."),
        new("monster", "Monster (Abra, Tulpar, Semruk)", new[] { "MONSTER" }, Array.Empty<string>(),
            "Açılışta F2",
            "Security → Secure Boot",
            new[]
            {
                "\"Security\" sekmesine geçin.",
                "\"Secure Boot\" = Disabled (bazı modellerde \"Secure Boot Control\").",
                "F10 (bazı modellerde F4) ile kaydedip çıkın.",
            }),
        new("clevo", "Clevo / Tongfang tabanlı dizüstü", new[] { "CLEVO", "TONGFANG", "NOTEBOOK" }, Array.Empty<string>(),
            "Açılışta F2",
            "Security → Secure Boot",
            new[]
            {
                "\"Security\" sekmesine geçin.",
                "\"Secure Boot\" = Disabled.",
                "F10 (bazı modellerde F4) ile kaydedip çıkın.",
            }),
        new("surface", "Microsoft Surface", new[] { "MICROSOFT" }, new[] { "SURFACE" },
            "Kapalıyken ses açma tuşunu basılı tutup güç tuşuna basın",
            "Security → Secure Boot",
            new[]
            {
                "Soldaki \"Security\"ye dokunun.",
                "\"Secure Boot\" altında \"Change configuration\" → \"None\" seçin.",
                "\"Exit\" → \"Restart now\".",
            }),
        new("asrock", "ASRock anakart", new[] { "ASROCK" }, Array.Empty<string>(),
            "Açılışta F2 ya da Del",
            "Security → Secure Boot",
            new[]
            {
                "F6 ile Advanced Mode'a geçin, \"Security\" sekmesini açın.",
                "\"Secure Boot\" = Disabled.",
                Save,
            }),
    };

    public static VendorGuide Generic { get; } = new("generic", "Diğer üreticiler", Array.Empty<string>(), Array.Empty<string>(),
        "Açılışta F2, Del, F10 ya da Esc (üretici logosunun altında yazar)",
        "Security ya da Boot → Secure Boot",
        new[]
        {
            "\"Security\", \"Boot\" ya da \"Authentication\" sekmelerinde \"Secure Boot\" satırını arayın.",
            "Değerini \"Disabled\" / \"Off\" yapın. Gri ise önce \"Supervisor/Administrator Password\" belirleyin.",
            Save,
        },
        "Emin olamazsanız üreticinin destek sitesinde \"<model> disable secure boot\" diye aratın.");

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
